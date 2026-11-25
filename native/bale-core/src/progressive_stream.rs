//! Ported from `ProgressiveHttpStream` (src/Bale.Core/Playback/ProgressiveHttpStream.cs): one
//! forward-only `server_remux_progressive` response. It never sends a Range; only a 200 from the
//! plan's origin is accepted (409 is a refused range). A pump thread reads 64 KiB chunks up to
//! 16 MiB ahead of the demuxer to absorb network jitter.
//!
//! Reads never fail. FFmpeg's non-seekable input can't take an error, so a transport failure or
//! close ends the data (0 bytes), and `faulted` tells the player the end was not the title's
//! (protocol §4: an incomplete progressive response is a transport failure). HLS first requests a
//! cut segment again through an outage (hls.rs), while readers go on taking what is queued. Nothing logs; the URL
//! and headers are credentials and stay in memory.

use std::collections::VecDeque;
use std::io::Read;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use crate::range_stream::StreamError;

pub const CHUNK_SIZE: usize = 64 * 1024;
/// 16 MiB: about two seconds of a 4K HEVC remux (the C# `BufferChunks`).
pub const BUFFER_CHUNKS: usize = 256;
/// A body read that gets no bytes this long is a stall; the C# stream waits for the server's reap.
pub const READ_STALL: Duration = Duration::from_secs(30);
const FREE_CHUNKS: usize = 8;
/// A body that received for less than this gives no transfer reading ([`ProgressiveStats::transfer_us`]).
pub const MIN_TRANSFER_US: u64 = 1_000_000;

/// Diagnostics, the C# `ProgressiveHttpStream` properties. Times are microseconds; flags are 0 or 1.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct ProgressiveStats {
    pub downloaded_bytes: u64,
    pub download_us: u64,
    pub buffered_bytes: u64,
    pub position: u64,
    pub faulted: u64,
    pub completed: u64,
    /// HLS: completed, and no segment from the mount's start held media (segment_media.rs): the mount
    /// started past the media's end.
    pub no_media: u64,
    /// The bodies' transfer while bytes flowed, HLS's slow-link evidence ([`Transfer`]): bytes and time
    /// inside the reads of each body that received for at least [`MIN_TRANSFER_US`], its first read
    /// left out; the body being read counts once it has.
    pub transfer_bytes: u64,
    pub transfer_us: u64,
}

/// How fast the bodies arrived while they were arriving (silo-server buffering-quality design,
/// "Slow-link evidence"; silo-apple `3709c5fb` times each server-HLS segment from its first byte to its
/// last, segments under 1 s left out, Android's `PlaybackThroughputMeter` rules). A Silo transcode
/// answers a segment only once its encoder has written all of it, so time spent waiting for a body is
/// the server's work, not the link's: the first read of each body, which waited for its first bytes,
/// counts neither its bytes nor its time, and neither does time before or between bodies.
///
/// Unlike AVPlayer's relay, this pump stops reading mid-body while its 16 MiB buffer is full, so a
/// first-to-last-byte span would count FFmpeg's pace as the link's and read a fast link slow. Only time
/// inside the reads counts: a read right after such a pause takes what waited in the socket at once,
/// which reads fast, never slow. A failed read (a cut, a 30 s stall) adds nothing; its outage is the
/// player's transport trouble, not a slow link. Bodies arrive one at a time, so nothing overlaps.
#[derive(Debug, Default, Clone, Copy)]
struct Transfer {
    /// The body being read returned its first bytes: its later reads are timed.
    started: bool,
    body_bytes: u64,
    body_us: u64,
    /// Bodies that ended after receiving for MIN_TRANSFER_US.
    done_bytes: u64,
    done_us: u64,
}

impl Transfer {
    /// A read of the current body returned `bytes` after `us` inside the read.
    fn read(&mut self, bytes: u64, us: u64) {
        if !self.started {
            self.started = true;
            return;
        }
        self.body_bytes += bytes;
        self.body_us += us;
    }

    /// The current body ended, completely or cut: its books close, and the next body (or the rest of a
    /// cut one, a new request) starts with an untimed first read.
    fn end_body(&mut self) {
        if self.body_us >= MIN_TRANSFER_US {
            self.done_bytes += self.body_bytes;
            self.done_us += self.body_us;
        }
        *self = Transfer {
            done_bytes: self.done_bytes,
            done_us: self.done_us,
            ..Transfer::default()
        };
    }

    /// (bytes, microseconds): the bodies counted so far, the one being read included once it counts.
    fn totals(&self) -> (u64, u64) {
        if self.body_us >= MIN_TRANSFER_US {
            (
                self.done_bytes + self.body_bytes,
                self.done_us + self.body_us,
            )
        } else {
            (self.done_bytes, self.done_us)
        }
    }
}

struct Chunk {
    data: Vec<u8>,
    length: usize,
}

#[derive(Default)]
struct Queue {
    chunks: VecDeque<Chunk>,
    /// Bytes of the front chunk already handed to the reader.
    offset: usize,
    free: Vec<Vec<u8>>,
    /// The pump has stopped: no more chunks will arrive.
    ended: bool,
}

#[derive(Default)]
struct Shared {
    queue: Mutex<Queue>,
    data: Condvar,
    space: Condvar,
    closed: AtomicBool,
    faulted: AtomicBool,
    completed: AtomicBool,
    no_media: AtomicBool,
    downloaded_bytes: AtomicU64,
    download_us: AtomicU64,
    buffered_bytes: AtomicU64,
    position: AtomicU64,
    /// The pump's books; stats reads both totals at once.
    transfer: Mutex<Transfer>,
}

pub struct ProgressiveStream {
    shared: Arc<Shared>,
    client: Option<Arc<crate::auth_client::AuthClient>>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The bodies a stream plays in order: one forward-only response, or an HLS init segment and its
/// media segments (hls.rs). `None` is the clean end; an error ends the data as a fault.
pub trait BodySource: Send {
    fn next_body(&mut self) -> Option<Result<Box<dyn Read + Send>, StreamError>>;

    /// HLS: whether any media segment so far held media (segment_media.rs); None for a single response.
    fn delivered_media(&self) -> Option<bool> {
        None
    }

    /// The current body failed after `delivered` bytes: the rest of it, re-requested (HLS rides out an
    /// outage this way, hls.rs), or None when it can't be resumed (a single response) and the stream ends
    /// as a fault.
    fn resume(&mut self, _delivered: u64) -> Option<Result<Box<dyn Read + Send>, StreamError>> {
        None
    }
}

/// A single response body.
struct OneBody(Option<Box<dyn Read + Send>>);

impl BodySource for OneBody {
    fn next_body(&mut self) -> Option<Result<Box<dyn Read + Send>, StreamError>> {
        self.0.take().map(Ok)
    }
}

impl Shared {
    fn pump(&self, mut source: Box<dyn BodySource>) {
        let mut body: Option<Box<dyn Read + Send>> = None;
        // Bytes of the current body already queued, for a resume after a cut.
        let mut delivered = 0u64;
        loop {
            if body.is_none() {
                if self.closed.load(Ordering::Acquire) {
                    self.faulted.store(true, Ordering::Release);
                    break;
                }
                match source.next_body() {
                    None => {
                        // Every segment to the playlist's end held no media (silo-server 4bee8f8).
                        if source.delivered_media() == Some(false) {
                            self.no_media.store(true, Ordering::Release);
                            crate::media_fault::count(&crate::media_fault::HLS_NO_MEDIA);
                        }
                        self.completed.store(true, Ordering::Release);
                        break;
                    }
                    Some(Err(_)) => {
                        self.faulted.store(true, Ordering::Release);
                        break;
                    }
                    Some(Ok(next)) => {
                        body = Some(next);
                        delivered = 0;
                    }
                }
            }
            let mut data = lock(&self.queue)
                .free
                .pop()
                .unwrap_or_else(|| vec![0; CHUNK_SIZE]);
            if self.closed.load(Ordering::Acquire) {
                self.faulted.store(true, Ordering::Release);
                break;
            }
            let started = Instant::now();
            let result = if crate::media_fault::outage() {
                // bale-debug.sh media-fault outage: the response is cut where it stands.
                Err(std::io::ErrorKind::ConnectionReset.into())
            } else {
                body.as_mut().expect("a body is open").read(&mut data)
            };
            let read_us = started.elapsed().as_micros().min(u128::from(u64::MAX)) as u64;
            self.download_us.fetch_add(read_us, Ordering::Relaxed);
            let length = match result {
                Ok(0) => {
                    // This body is done; the source decides whether another follows.
                    lock(&self.queue).free.push(data);
                    lock(&self.transfer).end_body();
                    body = None;
                    continue;
                }
                Ok(length) => length,
                // Chunked HTTP/1.1 without its final chunk, a reset, a stall: never logged. HLS requests the
                // body again and goes on after what it already queued, through an outage if need be; meanwhile
                // the reader plays what is queued.
                Err(_) => {
                    lock(&self.queue).free.push(data);
                    // The rest is a new request, whose wait for its first bytes is the server's again.
                    lock(&self.transfer).end_body();
                    match source.resume(delivered) {
                        Some(Ok(rest)) if !self.closed.load(Ordering::Acquire) => {
                            body = Some(rest);
                            continue;
                        }
                        _ => {
                            self.faulted.store(true, Ordering::Release);
                            break;
                        }
                    }
                }
            };
            lock(&self.transfer).read(length as u64, read_us);
            delivered += length as u64;
            self.downloaded_bytes
                .fetch_add(length as u64, Ordering::Relaxed);
            let mut queue = lock(&self.queue);
            while queue.chunks.len() >= BUFFER_CHUNKS && !self.closed.load(Ordering::Acquire) {
                queue = self
                    .space
                    .wait(queue)
                    .unwrap_or_else(PoisonError::into_inner);
            }
            if self.closed.load(Ordering::Acquire) {
                self.faulted.store(true, Ordering::Release);
                break;
            }
            self.buffered_bytes
                .fetch_add(length as u64, Ordering::Relaxed);
            queue.chunks.push_back(Chunk { data, length });
            self.data.notify_one();
        }
        lock(&self.queue).ended = true;
        self.data.notify_all();
    }
}

impl ProgressiveStream {
    /// Opens the response without a Range. 409 is `RangeRefused`, 401 `Unauthorized`, any other
    /// failure `Status`, and a success other than 200 `InvalidRange` (it would claim a byte range
    /// this delivery does not have).
    pub fn open(url: &str, headers: &[(String, String)]) -> Result<Self, StreamError> {
        // One response for the whole title: no overall deadline, only a stall limit per read. Busy
        // answers retry as in the C# handler; nothing can close the stream before it exists.
        let retried = crate::http::call_with_retry(
            |attempt| {
                // bale-debug.sh media-fault outage: the server is away.
                if crate::media_fault::outage() {
                    return Err(StreamError::Network);
                }
                let mut request = crate::http::agent()
                    .get(url)
                    .config()
                    .timeout_global(None)
                    .timeout_per_read(Some(READ_STALL))
                    .build();
                for (name, value) in headers {
                    request = request.header(name, value);
                }
                if let Some(attempt) = attempt {
                    request = request.header("Retry-Attempt", attempt.to_string());
                }
                request.call().map_err(|_| StreamError::Network)
            },
            |delay| {
                std::thread::sleep(delay);
                true
            },
        )?;
        let response = match retried {
            crate::http::Retried::Response(response) => response,
            crate::http::Retried::Exhausted | crate::http::Retried::Stopped => {
                return Err(StreamError::Busy);
            }
        };
        match response.status().as_u16() {
            409 => return Err(StreamError::RangeRefused),
            401 => return Err(StreamError::Unauthorized),
            200 => {}
            status if (200..300).contains(&status) => return Err(StreamError::InvalidRange),
            _ => return Err(StreamError::Status),
        }
        let body = response
            .into_body()
            .into_with_config()
            .limit(u64::MAX)
            .reader();
        Self::start(Box::new(OneBody(Some(Box::new(body)))), None)
    }

    /// Starts the pump over `source`. `client` is the HLS stream's authenticated client: its headers
    /// can be replaced and `close` ends its waits.
    pub fn start(
        source: Box<dyn BodySource>,
        client: Option<Arc<crate::auth_client::AuthClient>>,
    ) -> Result<Self, StreamError> {
        let shared = Arc::new(Shared::default());
        let pump = shared.clone();
        std::thread::Builder::new()
            .name("bale-progressive".into())
            .spawn(move || pump.pump(source))
            .map_err(|_| StreamError::Network)?;
        Ok(ProgressiveStream { shared, client })
    }

    /// The HLS stream's client, for header refreshes; None for a single forward-only response.
    pub fn client(&self) -> Option<&crate::auth_client::AuthClient> {
        self.client.as_deref()
    }

    /// Up to `buffer.len()` bytes from the front chunk, waiting for the pump; 0 at the end of the
    /// data, after a fault, or once closed.
    pub fn read(&self, buffer: &mut [u8]) -> usize {
        let shared = &self.shared;
        if buffer.is_empty() || shared.closed.load(Ordering::Acquire) {
            return 0;
        }
        let mut queue = lock(&shared.queue);
        loop {
            if shared.closed.load(Ordering::Acquire) {
                return 0;
            }
            let state = &mut *queue;
            if let Some(front) = state.chunks.front() {
                let offset = state.offset;
                let count = buffer.len().min(front.length - offset);
                buffer[..count].copy_from_slice(&front.data[offset..offset + count]);
                state.offset += count;
                if state.offset == front.length {
                    let chunk = state.chunks.pop_front().expect("front exists");
                    state.offset = 0;
                    if state.free.len() < FREE_CHUNKS {
                        state.free.push(chunk.data);
                    }
                    shared.space.notify_one();
                }
                shared
                    .buffered_bytes
                    .fetch_sub(count as u64, Ordering::Relaxed);
                shared.position.fetch_add(count as u64, Ordering::Relaxed);
                return count;
            }
            if state.ended {
                return 0;
            }
            queue = shared
                .data
                .wait(queue)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }

    pub fn stats(&self) -> ProgressiveStats {
        let s = &self.shared;
        let flag = |value: &AtomicBool| u64::from(value.load(Ordering::Acquire));
        let (transfer_bytes, transfer_us) = lock(&s.transfer).totals();
        ProgressiveStats {
            downloaded_bytes: s.downloaded_bytes.load(Ordering::Relaxed),
            download_us: s.download_us.load(Ordering::Relaxed),
            buffered_bytes: s.buffered_bytes.load(Ordering::Relaxed),
            position: s.position.load(Ordering::Relaxed),
            faulted: flag(&s.faulted),
            completed: flag(&s.completed),
            no_media: flag(&s.no_media),
            transfer_bytes,
            transfer_us,
        }
    }

    /// Ends the data for readers, frees the buffer and stops the pump; an unfinished response is
    /// then `faulted`. A pump inside a body read leaves after it returns (at most the stall limit).
    pub fn close(&self) {
        let shared = &self.shared;
        shared.closed.store(true, Ordering::Release);
        let mut queue = lock(&shared.queue);
        queue.chunks.clear();
        queue.free.clear();
        queue.offset = 0;
        shared.buffered_bytes.store(0, Ordering::Relaxed);
        if !shared.completed.load(Ordering::Acquire) {
            shared.faulted.store(true, Ordering::Release);
        }
        shared.data.notify_all();
        shared.space.notify_all();
        drop(queue);
        if let Some(client) = &self.client {
            client.close();
        }
    }
}

impl Drop for ProgressiveStream {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;

    #[test]
    fn stats_keep_the_layout_the_csharp_struct_reads() {
        // RustProgressiveStream.NativeProgressiveStats: nine u64 fields, in this order.
        assert_eq!(std::mem::size_of::<ProgressiveStats>(), 9 * 8);
    }

    #[test]
    fn transfer_leaves_out_first_reads_and_bodies_under_a_second() {
        let mut t = Transfer::default();
        // A segment the server held while it encoded: its first read waited 3 s, the rest came in 0.5 s.
        t.read(1_000, 3_000_000);
        t.read(64_000, 250_000);
        t.read(64_000, 250_000);
        t.end_body();
        assert_eq!(t.totals(), (0, 0));
        // A segment on a slow link counts, while it is still arriving too.
        t.read(1_000, 10);
        for _ in 0..4 {
            t.read(10_000, 300_000);
        }
        assert_eq!(t.totals(), (40_000, 1_200_000));
        t.end_body();
        assert_eq!(t.totals(), (40_000, 1_200_000));
        // The next body starts with an untimed first read, and under a second it adds nothing.
        t.read(5_000, 2_000_000);
        t.read(5_000, 900_000);
        assert_eq!(t.totals(), (40_000, 1_200_000));
    }

    #[test]
    fn a_cut_body_and_its_resumed_rest_are_two_requests() {
        let mut t = Transfer::default();
        t.read(1, 1);
        t.read(10_000, 600_000);
        t.end_body();
        // The re-request's first read waited for the server again.
        t.read(1, 2_000_000);
        t.read(10_000, 600_000);
        t.end_body();
        assert_eq!(t.totals(), (0, 0));
    }

    /// A body whose first read returns after `first` and each of its next `reads` after `delay`, `chunk`
    /// bytes each.
    struct Paced {
        first: Option<Duration>,
        reads: usize,
        chunk: usize,
        delay: Duration,
    }

    impl Read for Paced {
        fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
            let wait = match self.first.take() {
                Some(first) => first,
                None if self.reads == 0 => return Ok(0),
                None => {
                    self.reads -= 1;
                    self.delay
                }
            };
            std::thread::sleep(wait);
            let count = self.chunk.min(buffer.len());
            buffer[..count].fill(7);
            Ok(count)
        }
    }

    struct Bodies(VecDeque<Paced>);

    impl BodySource for Bodies {
        fn next_body(&mut self) -> Option<Result<Box<dyn Read + Send>, StreamError>> {
            self.0
                .pop_front()
                .map(|body| Ok(Box::new(body) as Box<dyn Read + Send>))
        }
    }

    fn drain(stream: &ProgressiveStream) -> u64 {
        let mut buffer = vec![0; CHUNK_SIZE];
        let mut total = 0;
        loop {
            match stream.read(&mut buffer) {
                0 => return total,
                count => total += count as u64,
            }
        }
    }

    #[test]
    fn the_pump_times_a_slow_body_but_not_the_wait_for_its_first_bytes() {
        // The pump reads through the debug outage switch, which other tests arm.
        let _faults = crate::media_fault::tests::exclusive();
        let chunk = 16 * 1024;
        let stream = ProgressiveStream::start(
            Box::new(Bodies(VecDeque::from([
                // The server worked 1.1 s before the first bytes, then sent fast.
                Paced {
                    first: Some(Duration::from_millis(1_100)),
                    reads: 5,
                    chunk,
                    delay: Duration::ZERO,
                },
                // A slow link: 1.1 s of reads after the first.
                Paced {
                    first: Some(Duration::ZERO),
                    reads: 5,
                    chunk,
                    delay: Duration::from_millis(220),
                },
            ]))),
            None,
        )
        .unwrap();
        assert_eq!(drain(&stream), 12 * chunk as u64);
        let stats = stream.stats();
        assert_eq!((stats.completed, stats.faulted), (1, 0));
        assert_eq!(stats.transfer_bytes, 5 * chunk as u64);
        assert!(
            (1_100_000..2_300_000).contains(&stats.transfer_us),
            "{}",
            stats.transfer_us
        );
    }

    #[test]
    fn a_pause_on_a_full_buffer_is_not_receiving_time() {
        let _faults = crate::media_fault::tests::exclusive();
        // More than the 16 MiB buffer, sent fast; nothing reads for 1.2 s, so the pump waits mid-body.
        let stream = ProgressiveStream::start(
            Box::new(Bodies(VecDeque::from([Paced {
                first: Some(Duration::ZERO),
                reads: BUFFER_CHUNKS + 40,
                chunk: CHUNK_SIZE,
                delay: Duration::ZERO,
            }]))),
            None,
        )
        .unwrap();
        std::thread::sleep(Duration::from_millis(1_200));
        assert_eq!(
            drain(&stream),
            (BUFFER_CHUNKS as u64 + 41) * CHUNK_SIZE as u64
        );
        assert_eq!(stream.stats().completed, 1);
        // Had the pause counted, the body would have spent over a second receiving: FFmpeg's pace, not the link's.
        assert_eq!(
            (stream.stats().transfer_bytes, stream.stats().transfer_us),
            (0, 0)
        );
    }
}
