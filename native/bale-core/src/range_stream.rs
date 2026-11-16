//! Ported from `HttpRangeStream` (src/Bale.Core/Playback/HttpRangeStream.cs): a read-only, seekable
//! HTTP body for the player. Windows' MKV source alternates read positions and one request per jump
//! starved playback, so reads go through bounded, aligned 1 MiB blocks: a 32-block LRU cache, up to
//! `prefetch` blocks fetched ahead, three fetches at a time. A server without range support gets a
//! single sequential body that is reopened for backward or far-forward seeks.
//!
//! Everything here runs on native threads, so .NET's garbage collector can never pause it
//! (docs/rust-port.md, slice 7). Nothing logs; errors are fixed categories. The URL and headers are
//! credentials and stay in memory.

use std::collections::{HashMap, VecDeque};
use std::io::Read;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError, RwLock};
use std::time::Instant;

pub const BLOCK_SIZE: u64 = 1024 * 1024;
pub const DEFAULT_PREFETCH_BLOCKS: u32 = 2;
pub const MAXIMUM_PREFETCH_BLOCKS: u32 = 16;
const CACHE_CAPACITY: usize = 32;
const FETCH_SLOTS: usize = 3;
const FORWARD_SKIP_LIMIT: u64 = 256 * 1024;
const FREE_BLOCKS: usize = 4;

/// Why a call failed. The C# wrapper maps these to fixed exception messages.
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StreamError {
    Disposed = 1,
    /// A non-success status (the C# `RequireSuccess`).
    Status = 2,
    /// A range response that doesn't match the request or the stream's length.
    InvalidRange = 3,
    /// The body ended before its declared length.
    ShortBody = 4,
    /// Connection, TLS, timeout or read failure.
    Network = 5,
    /// The open response gave no usable total length.
    NoLength = 6,
}

/// Diagnostics, the C# `HttpRangeStream` counters. Times are microseconds.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct RangeStats {
    pub reads: u64,
    pub bytes_read: u64,
    pub smallest_read: u64,
    pub block_fetches: u64,
    pub cache_hits: u64,
    pub prefetch_hits: u64,
    pub demand_waits: u64,
    pub demand_wait_us: u64,
    pub max_demand_wait_us: u64,
    pub downloaded_bytes: u64,
    pub download_us: u64,
    pub block_allocations: u64,
    pub block_reuses: u64,
}

#[derive(Default)]
struct Counters {
    reads: AtomicU64,
    bytes_read: AtomicU64,
    smallest_read: AtomicU64,
    block_fetches: AtomicU64,
    cache_hits: AtomicU64,
    prefetch_hits: AtomicU64,
    demand_waits: AtomicU64,
    demand_wait_us: AtomicU64,
    max_demand_wait_us: AtomicU64,
    downloaded_bytes: AtomicU64,
    download_us: AtomicU64,
    block_allocations: AtomicU64,
    block_reuses: AtomicU64,
}

type Block = Arc<Vec<u8>>;

struct CacheEntry {
    data: Block,
    /// Fetched ahead and not yet read: its first use counts as a prefetch hit.
    prefetched: bool,
    last_use: u64,
}

/// One fetch, shared by every reader of its block and by the worker running it.
struct Fetch {
    prefetch: bool,
    result: Mutex<Option<Result<Block, StreamError>>>,
    done: Condvar,
}

impl Fetch {
    fn new(prefetch: bool) -> Arc<Self> {
        Arc::new(Fetch {
            prefetch,
            result: Mutex::new(None),
            done: Condvar::new(),
        })
    }

    fn finish(&self, result: Result<Block, StreamError>) {
        *lock(&self.result) = Some(result);
        self.done.notify_all();
    }

    fn is_finished(&self) -> bool {
        lock(&self.result).is_some()
    }

    fn wait(&self) -> Result<Block, StreamError> {
        let mut result = lock(&self.result);
        loop {
            if let Some(result) = result.as_ref() {
                return result.clone();
            }
            result = self
                .done
                .wait(result)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }
}

#[derive(Default)]
struct State {
    cache: HashMap<u64, CacheEntry>,
    use_clock: u64,
    in_flight: HashMap<u64, Arc<Fetch>>,
    /// Fetches waiting for one of the three workers, oldest first.
    queue: VecDeque<(u64, Arc<Fetch>)>,
    /// Full-size block buffers reclaimed from evicted blocks no reader still holds.
    free: Vec<Vec<u8>>,
}

/// A server without range support: one sequential body and how far into it we have read.
struct Sequential {
    body: Option<Box<dyn Read + Send>>,
    position: u64,
}

struct Shared {
    url: String,
    headers: RwLock<Vec<(String, String)>>,
    length: u64,
    prefetch_blocks: u64,
    disposed: AtomicBool,
    state: Mutex<State>,
    work: Condvar,
    counters: Counters,
}

pub struct RangeStream {
    shared: Arc<Shared>,
    supports_ranges: bool,
    /// Reads are serialized, as the C# stream's read lock does.
    sequential: Mutex<Sequential>,
    read_lock: Mutex<()>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn micros(since: Instant) -> u64 {
    since.elapsed().as_micros().min(u128::from(u64::MAX)) as u64
}

/// `Content-Range: bytes <from>-<to>/<total>` for a 206 starting at `start`: (to, total).
pub fn parse_content_range(value: Option<&str>, start: u64) -> Option<(u64, u64)> {
    let (unit, rest) = value?.trim().split_once(' ')?;
    if !unit.eq_ignore_ascii_case("bytes") {
        return None;
    }
    let (range, total) = rest.trim().split_once('/')?;
    let (from, to) = range.split_once('-')?;
    let (from, to, total) = (
        from.trim().parse::<u64>().ok()?,
        to.trim().parse::<u64>().ok()?,
        total.trim().parse::<u64>().ok()?,
    );
    (from == start && to >= start && total > to).then_some((to, total))
}

impl Shared {
    fn request(&self, range: &str) -> Result<ureq::http::Response<ureq::Body>, StreamError> {
        let mut request = crate::http::agent().get(&self.url).header("Range", range);
        for (name, value) in self
            .headers
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
        {
            request = request.header(name, value);
        }
        request.call().map_err(|_| StreamError::Network)
    }

    /// A body read, counted as downloaded bytes and download time like the C# `ReadNetworkAsync`.
    fn read_network(&self, body: &mut dyn Read, buffer: &mut [u8]) -> Result<usize, StreamError> {
        let started = Instant::now();
        let result = body.read(buffer);
        self.counters
            .download_us
            .fetch_add(micros(started), Ordering::Relaxed);
        let count = result.map_err(|_| StreamError::Network)?;
        self.counters
            .downloaded_bytes
            .fetch_add(count as u64, Ordering::Relaxed);
        Ok(count)
    }

    fn last_block(&self) -> u64 {
        self.length.saturating_sub(1) / BLOCK_SIZE
    }

    /// Looks a block up for a read, counting a cache hit and a first-use prefetch hit.
    fn find_cached(&self, state: &mut State, index: u64) -> Option<Block> {
        state.use_clock += 1;
        let clock = state.use_clock;
        let entry = state.cache.get_mut(&index)?;
        entry.last_use = clock;
        self.counters.cache_hits.fetch_add(1, Ordering::Relaxed);
        if entry.prefetched {
            self.counters.prefetch_hits.fetch_add(1, Ordering::Relaxed);
            entry.prefetched = false;
        }
        Some(entry.data.clone())
    }

    fn start_fetch(&self, state: &mut State, index: u64, prefetch: bool) -> Arc<Fetch> {
        let fetch = Fetch::new(prefetch);
        state.in_flight.insert(index, fetch.clone());
        state.queue.push_back((index, fetch.clone()));
        self.work.notify_one();
        fetch
    }

    fn prefetch_following(&self, index: u64) {
        let mut state = lock(&self.state);
        if self.disposed.load(Ordering::Acquire) {
            return;
        }
        let last = (index + self.prefetch_blocks).min(self.last_block());
        for next in index + 1..=last {
            if !state.cache.contains_key(&next) && !state.in_flight.contains_key(&next) {
                self.start_fetch(&mut state, next, true);
            }
        }
    }

    /// A ready block, or the fetch to wait for (joined or started as a demand fetch).
    fn get_block(&self, index: u64) -> Result<Result<Block, Arc<Fetch>>, StreamError> {
        let mut state = lock(&self.state);
        if self.disposed.load(Ordering::Acquire) {
            return Err(StreamError::Disposed);
        }
        if let Some(block) = self.find_cached(&mut state, index) {
            return Ok(Ok(block));
        }
        let fetch = match state.in_flight.get(&index) {
            Some(existing) => existing.clone(),
            None => self.start_fetch(&mut state, index, false),
        };
        Ok(Err(fetch))
    }

    /// Waits for a fetch. A read joining failed speculation retries with its own demand fetch.
    fn wait_for(&self, index: u64, fetch: Arc<Fetch>) -> Result<Block, StreamError> {
        match fetch.wait() {
            Ok(block) => {
                let mut state = lock(&self.state);
                if self.disposed.load(Ordering::Acquire) {
                    return Err(StreamError::Disposed);
                }
                state.use_clock += 1;
                let clock = state.use_clock;
                if let Some(entry) = state
                    .cache
                    .get_mut(&index)
                    .filter(|entry| Arc::ptr_eq(&entry.data, &block))
                {
                    entry.last_use = clock;
                    // Waiting on speculation isn't a hit on an already-ready prefetched block.
                    entry.prefetched = false;
                }
                Ok(block)
            }
            Err(_) if fetch.prefetch && !self.disposed.load(Ordering::Acquire) => {
                match self.get_block(index)? {
                    Ok(block) => Ok(block),
                    Err(retry) => self.wait_for(index, retry),
                }
            }
            Err(error) => Err(error),
        }
    }

    /// A worker's fetch of one block. Demand fetches retry once; a second failure belongs to
    /// playback recovery. Speculation gets one attempt.
    fn fetch_block(&self, index: u64, prefetch: bool) -> Result<Block, StreamError> {
        let start = index * BLOCK_SIZE;
        let size = BLOCK_SIZE.min(self.length - start);
        let mut attempt = 0;
        loop {
            let result = self.fetch_once(start, size);
            match result {
                Err(StreamError::Disposed) => return result,
                Err(_) if !prefetch && attempt == 0 => attempt += 1,
                _ => return result,
            }
        }
    }

    fn fetch_once(&self, start: u64, size: u64) -> Result<Block, StreamError> {
        if self.disposed.load(Ordering::Acquire) {
            return Err(StreamError::Disposed);
        }
        self.counters.block_fetches.fetch_add(1, Ordering::Relaxed);
        let response = self.request(&format!("bytes={}-{}", start, start + size - 1))?;
        if !response.status().is_success() {
            return Err(StreamError::Status);
        }
        let content_range = response
            .headers()
            .get("content-range")
            .and_then(|value| value.to_str().ok());
        match (
            response.status().as_u16(),
            parse_content_range(content_range, start),
        ) {
            (206, Some((to, total))) if total == self.length && to == start + size - 1 => {}
            _ => return Err(StreamError::InvalidRange),
        }
        let mut data = if size == BLOCK_SIZE {
            match lock(&self.state).free.pop() {
                Some(buffer) => {
                    self.counters.block_reuses.fetch_add(1, Ordering::Relaxed);
                    buffer
                }
                None => {
                    self.counters
                        .block_allocations
                        .fetch_add(1, Ordering::Relaxed);
                    vec![0; BLOCK_SIZE as usize]
                }
            }
        } else {
            vec![0; size as usize]
        };
        let mut body = response
            .into_body()
            .into_with_config()
            .limit(u64::MAX)
            .reader();
        let mut downloaded = 0;
        let outcome = loop {
            if downloaded == data.len() {
                break Ok(());
            }
            if self.disposed.load(Ordering::Acquire) {
                break Err(StreamError::Disposed);
            }
            match self.read_network(&mut body, &mut data[downloaded..]) {
                Ok(0) => break Err(StreamError::ShortBody),
                Ok(count) => downloaded += count,
                Err(error) => break Err(error),
            }
        };
        // Read to the end of the response. ureq returns a connection to its pool only once a body
        // reports its end; stopping at the last expected byte closed it, so every block paid a new
        // TCP + TLS handshake and slow start (console: ~10x slower blocks, many more stream waits).
        let outcome = outcome.and_then(|()| {
            let mut end = [0u8; 1];
            match body.read(&mut end) {
                Ok(0) => Ok(()),
                Ok(_) => Err(StreamError::InvalidRange),
                Err(_) => Err(StreamError::Network),
            }
        });
        match outcome {
            Ok(()) => Ok(Arc::new(data)),
            Err(error) => {
                self.recycle(&mut lock(&self.state), data);
                Err(error)
            }
        }
    }

    fn recycle(&self, state: &mut State, data: Vec<u8>) {
        if data.len() as u64 == BLOCK_SIZE && state.free.len() < FREE_BLOCKS {
            state.free.push(data);
        }
    }

    /// Caches a fetched block, evicting the least recently used beyond capacity. An evicted block
    /// still held by a reader stays alive until that reader drops it.
    fn add_block(&self, state: &mut State, index: u64, block: &Block, prefetch: bool) {
        state.use_clock += 1;
        let clock = state.use_clock;
        state.cache.insert(
            index,
            CacheEntry {
                data: block.clone(),
                prefetched: prefetch,
                last_use: clock,
            },
        );
        if state.cache.len() > CACHE_CAPACITY {
            let oldest = state
                .cache
                .iter()
                .min_by_key(|(_, entry)| entry.last_use)
                .map(|(index, _)| *index);
            if let Some(evicted) = oldest.and_then(|oldest| state.cache.remove(&oldest))
                && let Ok(data) = Arc::try_unwrap(evicted.data)
            {
                self.recycle(state, data);
            }
        }
    }

    /// One of the three fetch workers: takes queued fetches in order until the stream is disposed.
    fn work(self: Arc<Self>) {
        loop {
            let (index, fetch) = {
                let mut state = lock(&self.state);
                loop {
                    if self.disposed.load(Ordering::Acquire) {
                        return;
                    }
                    if let Some(job) = state.queue.pop_front() {
                        break job;
                    }
                    state = self
                        .work
                        .wait(state)
                        .unwrap_or_else(PoisonError::into_inner);
                }
            };
            let result = self.fetch_block(index, fetch.prefetch);
            {
                let mut state = lock(&self.state);
                state.in_flight.remove(&index);
                let result = match result {
                    Ok(block) if !self.disposed.load(Ordering::Acquire) => {
                        self.add_block(&mut state, index, &block, fetch.prefetch);
                        Ok(block)
                    }
                    Ok(_) => Err(StreamError::Disposed),
                    Err(error) => Err(error),
                };
                fetch.finish(result);
            }
        }
    }
}

impl RangeStream {
    /// Opens with an open-ended range request. A 206 gives the total length and is closed; a 200
    /// with a length means the server ignores ranges, and its body becomes the sequential reader.
    pub fn open(
        url: String,
        headers: Vec<(String, String)>,
        prefetch_blocks: u32,
    ) -> Result<Self, StreamError> {
        let prefetch_blocks =
            prefetch_blocks.clamp(DEFAULT_PREFETCH_BLOCKS, MAXIMUM_PREFETCH_BLOCKS);
        let probe = Shared {
            url,
            headers: RwLock::new(headers),
            length: 0,
            prefetch_blocks: u64::from(prefetch_blocks),
            disposed: AtomicBool::new(false),
            state: Mutex::new(State::default()),
            work: Condvar::new(),
            counters: Counters::default(),
        };
        probe
            .counters
            .smallest_read
            .store(u64::MAX, Ordering::Relaxed);
        let response = probe.request("bytes=0-")?;
        if !response.status().is_success() {
            return Err(StreamError::Status);
        }
        let status = response.status().as_u16();
        let content_range = response
            .headers()
            .get("content-range")
            .and_then(|value| value.to_str().ok());
        let content_length = response
            .headers()
            .get("content-length")
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.trim().parse::<u64>().ok());
        let (length, supports_ranges) = match (
            status,
            parse_content_range(content_range, 0),
            content_length,
        ) {
            (206, Some((_, total)), _) => (total, true),
            (200, _, Some(length)) => (length, false),
            _ => return Err(StreamError::NoLength),
        };
        let shared = Arc::new(Shared { length, ..probe });
        let sequential = if supports_ranges {
            // The open-ended probe supplies the total, not a body to keep alive between seeks.
            drop(response);
            Sequential {
                body: None,
                position: 0,
            }
        } else {
            let body: Box<dyn Read + Send> = Box::new(
                response
                    .into_body()
                    .into_with_config()
                    .limit(u64::MAX)
                    .reader(),
            );
            Sequential {
                body: Some(body),
                position: 0,
            }
        };
        if supports_ranges {
            for _ in 0..FETCH_SLOTS {
                let worker = shared.clone();
                std::thread::Builder::new()
                    .name("bale-range-fetch".into())
                    .spawn(move || worker.work())
                    .map_err(|_| StreamError::Network)?;
            }
        }
        Ok(RangeStream {
            shared,
            supports_ranges,
            sequential: Mutex::new(sequential),
            read_lock: Mutex::new(()),
        })
    }

    pub fn length(&self) -> u64 {
        self.shared.length
    }

    pub fn supports_ranges(&self) -> bool {
        self.supports_ranges
    }

    /// Replaces the request headers, e.g. after a token refresh; the next requests use them.
    pub fn set_headers(&self, headers: Vec<(String, String)>) {
        *self
            .shared
            .headers
            .write()
            .unwrap_or_else(PoisonError::into_inner) = headers;
    }

    /// Reads at `position`. Returns 0 at or past the end. A read need not fill `buffer`.
    pub fn read(&self, position: u64, buffer: &mut [u8]) -> Result<usize, StreamError> {
        let _serialized = lock(&self.read_lock);
        if self.shared.disposed.load(Ordering::Acquire) {
            return Err(StreamError::Disposed);
        }
        if buffer.is_empty() || position >= self.shared.length {
            return Ok(0);
        }
        let counters = &self.shared.counters;
        counters.reads.fetch_add(1, Ordering::Relaxed);
        counters
            .smallest_read
            .fetch_min(buffer.len() as u64, Ordering::Relaxed);
        let count = if self.supports_ranges {
            self.read_blocks(position, buffer)?
        } else {
            self.read_sequential(position, buffer)?
        };
        counters
            .bytes_read
            .fetch_add(count as u64, Ordering::Relaxed);
        Ok(count)
    }

    fn read_blocks(&self, position: u64, buffer: &mut [u8]) -> Result<usize, StreamError> {
        let shared = &self.shared;
        let mut copied = 0;
        while copied < buffer.len() && position + (copied as u64) < shared.length {
            let at = position + copied as u64;
            let index = at / BLOCK_SIZE;
            let block = if copied == 0 {
                let pending = shared.get_block(index)?;
                let waiting = matches!(&pending, Err(fetch) if !fetch.is_finished());
                let started = waiting.then(Instant::now);
                if waiting {
                    shared.counters.demand_waits.fetch_add(1, Ordering::Relaxed);
                }
                shared.prefetch_following(index);
                let block = match pending {
                    Ok(block) => Ok(block),
                    Err(fetch) => shared.wait_for(index, fetch),
                };
                if let Some(started) = started {
                    // One read waits once even if failed speculation falls back to demand retries.
                    let elapsed = micros(started);
                    shared
                        .counters
                        .demand_wait_us
                        .fetch_add(elapsed, Ordering::Relaxed);
                    shared
                        .counters
                        .max_demand_wait_us
                        .fetch_max(elapsed, Ordering::Relaxed);
                }
                block?
            } else {
                let cached = {
                    let mut state = lock(&shared.state);
                    if shared.disposed.load(Ordering::Acquire) {
                        return Err(StreamError::Disposed);
                    }
                    shared.find_cached(&mut state, index)
                };
                // A read need not fill the buffer: don't wait on another block after copying bytes.
                let Some(block) = cached else { break };
                shared.prefetch_following(index);
                block
            };
            let offset = (at % BLOCK_SIZE) as usize;
            let count = (buffer.len() - copied).min(block.len() - offset);
            buffer[copied..copied + count].copy_from_slice(&block[offset..offset + count]);
            copied += count;
        }
        Ok(copied)
    }

    fn read_sequential(&self, position: u64, buffer: &mut [u8]) -> Result<usize, StreamError> {
        let mut sequential = lock(&self.sequential);
        let forward = position.checked_sub(sequential.position);
        if sequential.body.is_none() || forward.is_none_or(|forward| forward > FORWARD_SKIP_LIMIT) {
            self.reopen(&mut sequential)?;
        }
        let mut attempt = 0;
        loop {
            match self.read_sequential_once(&mut sequential, position, buffer) {
                Err(StreamError::Disposed) => return Err(StreamError::Disposed),
                Err(_) if attempt == 0 => {
                    attempt += 1;
                    self.reopen(&mut sequential)?;
                }
                result => return result,
            }
        }
    }

    fn read_sequential_once(
        &self,
        sequential: &mut Sequential,
        position: u64,
        buffer: &mut [u8],
    ) -> Result<usize, StreamError> {
        let shared = &self.shared;
        if shared.disposed.load(Ordering::Acquire) {
            return Err(StreamError::Disposed);
        }
        let mut skip = [0u8; 32 * 1024];
        while sequential.position < position {
            let body = sequential.body.as_mut().ok_or(StreamError::Disposed)?;
            let wanted = (skip.len() as u64).min(position - sequential.position) as usize;
            let count = shared.read_network(body.as_mut(), &mut skip[..wanted])?;
            if count == 0 {
                return Err(StreamError::ShortBody);
            }
            sequential.position += count as u64;
        }
        let wanted = (buffer.len() as u64).min(shared.length - position) as usize;
        let body = sequential.body.as_mut().ok_or(StreamError::Disposed)?;
        let count = shared.read_network(body.as_mut(), &mut buffer[..wanted])?;
        if count == 0 {
            return Err(StreamError::ShortBody);
        }
        sequential.position += count as u64;
        Ok(count)
    }

    /// A 200 at open means ranges are unsupported: reopen at zero and let the skip reader advance,
    /// rather than mistaking a full body for a ranged one.
    fn reopen(&self, sequential: &mut Sequential) -> Result<(), StreamError> {
        let shared = &self.shared;
        sequential.body = None;
        if shared.disposed.load(Ordering::Acquire) {
            return Err(StreamError::Disposed);
        }
        let response = shared.request("bytes=0-")?;
        let content_range = response
            .headers()
            .get("content-range")
            .and_then(|value| value.to_str().ok());
        let content_length = response
            .headers()
            .get("content-length")
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.trim().parse::<u64>().ok());
        let valid_range = response.status().as_u16() == 206
            && parse_content_range(content_range, 0) == Some((shared.length - 1, shared.length));
        let valid_full_body =
            response.status().as_u16() == 200 && content_length == Some(shared.length);
        if !valid_range && !valid_full_body {
            return Err(StreamError::InvalidRange);
        }
        sequential.body = Some(Box::new(
            response
                .into_body()
                .into_with_config()
                .limit(u64::MAX)
                .reader(),
        ));
        sequential.position = 0;
        Ok(())
    }

    pub fn stats(&self) -> RangeStats {
        let c = &self.shared.counters;
        let get = |value: &AtomicU64| value.load(Ordering::Relaxed);
        RangeStats {
            reads: get(&c.reads),
            bytes_read: get(&c.bytes_read),
            smallest_read: get(&c.smallest_read),
            block_fetches: get(&c.block_fetches),
            cache_hits: get(&c.cache_hits),
            prefetch_hits: get(&c.prefetch_hits),
            demand_waits: get(&c.demand_waits),
            demand_wait_us: get(&c.demand_wait_us),
            max_demand_wait_us: get(&c.max_demand_wait_us),
            downloaded_bytes: get(&c.downloaded_bytes),
            download_us: get(&c.download_us),
            block_allocations: get(&c.block_allocations),
            block_reuses: get(&c.block_reuses),
        }
    }
}

impl RangeStream {
    /// Stops the workers and wakes every waiting read with `Disposed`. A worker mid-request finishes
    /// that request (bounded by the agent's timeout) and exits; nothing waits for it here. Safe to
    /// call while other threads are inside `read`, and more than once.
    pub fn close(&self) {
        self.shared.disposed.store(true, Ordering::Release);
        let mut state = lock(&self.shared.state);
        state.cache.clear();
        state.free.clear();
        for (_, fetch) in state.queue.drain(..) {
            fetch.finish(Err(StreamError::Disposed));
        }
        for (_, fetch) in state.in_flight.drain() {
            fetch.finish(Err(StreamError::Disposed));
        }
        self.shared.work.notify_all();
    }
}

impl Drop for RangeStream {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::parse_content_range as parse;

    #[test]
    fn content_ranges() {
        assert_eq!(parse(Some("bytes 0-1023/5000"), 0), Some((1023, 5000)));
        assert_eq!(parse(Some("Bytes 10-19/20"), 10), Some((19, 20)));
        assert_eq!(parse(Some("bytes 10-19/19"), 10), None); // the total must exceed the end
        assert_eq!(parse(Some("BYTES 10-19/21"), 10), Some((19, 21)));
        assert_eq!(parse(Some("bytes 5-4/10"), 5), None);
        assert_eq!(parse(Some("bytes 0-9/*"), 0), None);
        assert_eq!(parse(Some("items 0-9/10"), 0), None);
        assert_eq!(parse(Some("bytes 1-9/10"), 0), None);
        assert_eq!(parse(None, 0), None);
    }
}
