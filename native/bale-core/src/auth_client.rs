//! The authenticated GET shared by the Rust media streams (range_stream.rs, the HLS mode of
//! progressive_stream.rs): the owner's headers with a generation number, the C# `SiloRequestHandler`
//! rule for 401 (ask the owner for fresh headers once, retry once), and Kiota's retries for
//! 429/503/504 (`http::call_with_retry`). Nothing logs; headers are credentials.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Condvar, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use crate::range_stream::StreamError;

/// How long a request rejected with 401 waits for the owner's refreshed headers.
pub const REFRESH_WAIT: Duration = Duration::from_secs(15);

/// Asks the stream's owner for fresh headers after a 401, passing the generation of the rejected
/// headers. It must return quickly and deliver them later through `set_headers`.
pub type Refresher = Box<dyn Fn(u64) + Send + Sync>;

/// The request headers and their generation (how many times `set_headers` replaced them).
struct Auth {
    headers: Vec<(String, String)>,
    generation: u64,
    /// The generation a refresh was already requested for, so concurrent 401s ask once.
    requested: Option<u64>,
}

pub struct AuthClient {
    auth: Mutex<Auth>,
    changed: Condvar,
    /// Cleared by `close`, under this lock, so no call into the owner runs after `close` returns.
    refresher: Mutex<Option<Refresher>>,
    closed: AtomicBool,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

type Request = ureq::RequestBuilder<ureq::typestate::WithoutBody>;

impl AuthClient {
    pub fn new(headers: Vec<(String, String)>) -> Self {
        AuthClient {
            auth: Mutex::new(Auth {
                headers,
                generation: 0,
                requested: None,
            }),
            changed: Condvar::new(),
            refresher: Mutex::new(None),
            closed: AtomicBool::new(false),
        }
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }

    /// GET `url` (with `range`, when given) with the current headers; `configure` adjusts the
    /// request (timeouts). 401 refreshes once; busy answers retry; a closed client returns
    /// `Disposed`.
    pub fn get(
        &self,
        url: &str,
        range: Option<&str>,
        configure: impl Fn(Request) -> Request,
    ) -> Result<ureq::http::Response<ureq::Body>, StreamError> {
        let (response, generation) = self.send(url, range, &configure)?;
        if response.status().as_u16() != 401 {
            return Ok(response);
        }
        drop(response);
        if !self.await_refresh(generation) {
            return Err(if self.is_closed() {
                StreamError::Disposed
            } else {
                StreamError::Unauthorized
            });
        }
        let (response, _) = self.send(url, range, &configure)?;
        if response.status().as_u16() == 401 {
            return Err(StreamError::Unauthorized);
        }
        Ok(response)
    }

    /// One request with the current headers, retried on 429/503/504 as the C# handler does.
    fn send(
        &self,
        url: &str,
        range: Option<&str>,
        configure: &impl Fn(Request) -> Request,
    ) -> Result<(ureq::http::Response<ureq::Body>, u64), StreamError> {
        if self.is_closed() {
            return Err(StreamError::Disposed);
        }
        let (headers, generation) = {
            let auth = lock(&self.auth);
            (auth.headers.clone(), auth.generation)
        };
        let retried = crate::http::call_with_retry(
            |attempt| {
                let mut request = configure(crate::http::agent().get(url));
                if let Some(range) = range {
                    request = request.header("Range", range);
                }
                for (name, value) in &headers {
                    request = request.header(name, value);
                }
                if let Some(attempt) = attempt {
                    request = request.header("Retry-Attempt", attempt.to_string());
                }
                request.call().map_err(|_| StreamError::Network)
            },
            |delay| self.sleep_unless_closed(delay),
        )?;
        match retried {
            crate::http::Retried::Response(response) => Ok((response, generation)),
            crate::http::Retried::Exhausted => Err(StreamError::Busy),
            crate::http::Retried::Stopped => Err(StreamError::Disposed),
        }
    }

    /// A wait that `close` cuts short. False once closed.
    pub fn sleep_unless_closed(&self, delay: Duration) -> bool {
        let deadline = Instant::now() + delay;
        let mut auth = lock(&self.auth);
        while !self.is_closed() {
            let now = Instant::now();
            if now >= deadline {
                return true;
            }
            auth = self
                .changed
                .wait_timeout(auth, deadline - now)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
        false
    }

    /// Waits for headers newer than `generation`, asking the owner once per generation. False on
    /// timeout, close, or when nobody can refresh.
    fn await_refresh(&self, generation: u64) -> bool {
        let ask = {
            let mut auth = lock(&self.auth);
            if auth.generation != generation {
                return true;
            }
            let ask = auth.requested != Some(generation);
            auth.requested = Some(generation);
            ask
        };
        {
            let refresher = lock(&self.refresher);
            match refresher.as_ref() {
                Some(refresh) if ask => refresh(generation),
                Some(_) => {}
                None => {
                    lock(&self.auth).requested = None;
                    return false;
                }
            }
        }
        let deadline = Instant::now() + REFRESH_WAIT;
        let mut auth = lock(&self.auth);
        while auth.generation == generation && !self.is_closed() {
            let now = Instant::now();
            if now >= deadline {
                // Let a later 401 ask again rather than wait out a refresh that never came.
                if auth.requested == Some(generation) {
                    auth.requested = None;
                }
                return false;
            }
            auth = self
                .changed
                .wait_timeout(auth, deadline - now)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
        auth.generation != generation
    }

    /// Replaces the headers: later requests use them, and requests waiting after a 401 retry.
    pub fn set_headers(&self, headers: Vec<(String, String)>) {
        let mut auth = lock(&self.auth);
        auth.headers = headers;
        auth.generation += 1;
        auth.requested = None;
        self.changed.notify_all();
    }

    /// Installs the owner's refresh callback. False once closed (nothing is kept).
    pub fn set_refresher(&self, refresher: Refresher) -> bool {
        let mut slot = lock(&self.refresher);
        if self.is_closed() {
            return false;
        }
        *slot = Some(refresher);
        true
    }

    /// Ends waits and refreshes. Waits for a refresh callback in progress; none starts afterwards.
    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
        *lock(&self.refresher) = None;
        let _auth = lock(&self.auth);
        self.changed.notify_all();
    }
}
