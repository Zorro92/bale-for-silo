//! Ported from `ProgressiveHttpStream` (src/Bale.Core/Playback/ProgressiveHttpStream.cs): one
//! forward-only `server_remux_progressive` response. It never sends a Range; only a 200 from the
//! plan's origin is accepted (409 is a refused range). A pump thread reads 64 KiB chunks up to
//! 16 MiB ahead of the demuxer to absorb network jitter.
//!
//! Reads never fail. FFmpeg's non-seekable input can't take an error, so a transport failure or
//! close ends the data (0 bytes), and `faulted` tells the player the end was not the title's
//! (protocol §4: an incomplete progressive response is a transport failure). Nothing logs; the URL
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
    downloaded_bytes: AtomicU64,
    download_us: AtomicU64,
    buffered_bytes: AtomicU64,
    position: AtomicU64,
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
        loop {
            if body.is_none() {
                if self.closed.load(Ordering::Acquire) {
                    self.faulted.store(true, Ordering::Release);
                    break;
                }
                match source.next_body() {
                    None => {
                        self.completed.store(true, Ordering::Release);
                        break;
                    }
                    Some(Err(_)) => {
                        self.faulted.store(true, Ordering::Release);
                        break;
                    }
                    Some(Ok(next)) => body = Some(next),
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
            self.download_us.fetch_add(
                started.elapsed().as_micros().min(u128::from(u64::MAX)) as u64,
                Ordering::Relaxed,
            );
            let length = match result {
                Ok(0) => {
                    // This body is done; the source decides whether another follows.
                    lock(&self.queue).free.push(data);
                    body = None;
                    continue;
                }
                Ok(length) => length,
                // Chunked HTTP/1.1 without its final chunk, a reset, a stall: never logged.
                Err(_) => {
                    self.faulted.store(true, Ordering::Release);
                    break;
                }
            };
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
        ProgressiveStats {
            downloaded_bytes: s.downloaded_bytes.load(Ordering::Relaxed),
            download_us: s.download_us.load(Ordering::Relaxed),
            buffered_bytes: s.buffered_bytes.load(Ordering::Relaxed),
            position: s.position.load(Ordering::Relaxed),
            faulted: flag(&s.faulted),
            completed: flag(&s.completed),
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
