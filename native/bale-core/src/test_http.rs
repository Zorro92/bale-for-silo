//! A loopback HTTP/1.1 server for the media stream tests (range_stream.rs, hls.rs): keep-alive, byte
//! ranges and scripted faults, so the streams run their real ureq requests against it.

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

/// One request: its target (path and query) and Range header.
#[derive(Debug, Clone)]
pub struct Request {
    pub target: String,
    pub range: Option<String>,
}

#[allow(dead_code)] // The HLS tests read the path.
impl Request {
    /// The target without its query.
    pub fn path(&self) -> &str {
        self.target.split('?').next().unwrap_or_default()
    }
}

/// How the server answers one request.
pub enum Reply {
    Full {
        status: u16,
        headers: Vec<(String, String)>,
        body: Vec<u8>,
    },
    /// Headers that promise the whole body, then only `sent` bytes, and the connection closes.
    Cut {
        status: u16,
        headers: Vec<(String, String)>,
        body: Vec<u8>,
        sent: usize,
    },
    /// The connection closes without an answer.
    Drop,
}

impl Reply {
    pub fn ok(body: Vec<u8>) -> Reply {
        Reply::Full {
            status: 200,
            headers: Vec::new(),
            body,
        }
    }

    pub fn status(status: u16) -> Reply {
        Reply::Full {
            status,
            headers: Vec::new(),
            body: Vec::new(),
        }
    }

    /// `payload` as a server with byte ranges answers `range` (`bytes=a-b` or `bytes=a-`).
    pub fn ranged(payload: &[u8], range: Option<&str>) -> Reply {
        let Some((from, to)) = range
            .and_then(|r| r.strip_prefix("bytes="))
            .and_then(|r| r.split_once('-'))
        else {
            return Reply::ok(payload.to_vec());
        };
        let total = payload.len();
        let start: usize = from.parse().unwrap_or(0);
        let end = to
            .parse::<usize>()
            .map_or(total - 1, |end| end.min(total - 1));
        Reply::Full {
            status: 206,
            headers: vec![(
                "Content-Range".into(),
                format!("bytes {start}-{end}/{total}"),
            )],
            body: payload[start..=end].to_vec(),
        }
    }
}

type Handler = dyn Fn(&Request) -> Reply + Send + Sync;

pub struct TestServer {
    pub base: String,
    stop: Arc<AtomicBool>,
    requests: Arc<Mutex<Vec<Request>>>,
}

impl TestServer {
    pub fn start(handler: impl Fn(&Request) -> Reply + Send + Sync + 'static) -> TestServer {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind a loopback port");
        let base = format!("http://{}", listener.local_addr().expect("local address"));
        let stop = Arc::new(AtomicBool::new(false));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let handler: Arc<Handler> = Arc::new(handler);
        let (accept_stop, accept_requests) = (stop.clone(), requests.clone());
        std::thread::spawn(move || {
            for connection in listener.incoming() {
                if accept_stop.load(Ordering::Acquire) {
                    break;
                }
                let Ok(connection) = connection else { continue };
                let (handler, requests) = (handler.clone(), accept_requests.clone());
                std::thread::spawn(move || serve(connection, &*handler, &requests));
            }
        });
        TestServer {
            base,
            stop,
            requests,
        }
    }

    /// Every request so far, in arrival order.
    pub fn requests(&self) -> Vec<Request> {
        self.requests
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    pub fn count(&self, matches: impl Fn(&Request) -> bool) -> usize {
        self.requests().iter().filter(|r| matches(r)).count()
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        // Wakes the accept loop so it sees the flag.
        let _ = TcpStream::connect(self.base.trim_start_matches("http://"));
    }
}

fn serve(connection: TcpStream, handler: &Handler, requests: &Mutex<Vec<Request>>) {
    let _ = connection.set_read_timeout(Some(Duration::from_secs(30)));
    let Ok(mut writer) = connection.try_clone() else {
        return;
    };
    let mut reader = BufReader::new(connection);
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).unwrap_or(0) == 0 {
            return;
        }
        let target = line.split(' ').nth(1).unwrap_or("/").to_owned();
        let mut range = None;
        loop {
            let mut header = String::new();
            if reader.read_line(&mut header).unwrap_or(0) == 0 {
                return;
            }
            let header = header.trim_end();
            if header.is_empty() {
                break;
            }
            if let Some((name, value)) = header.split_once(':')
                && name.trim().eq_ignore_ascii_case("range")
            {
                range = Some(value.trim().to_owned());
            }
        }
        let request = Request { target, range };
        requests
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .push(request.clone());
        let keep = match handler(&request) {
            Reply::Full {
                status,
                headers,
                body,
            } => write(&mut writer, status, &headers, &body, body.len()),
            Reply::Cut {
                status,
                headers,
                body,
                sent,
            } => {
                write(&mut writer, status, &headers, &body, sent);
                false
            }
            Reply::Drop => false,
        };
        if !keep {
            return;
        }
    }
}

/// Writes the head for all of `body` and its first `sent` bytes; false when the connection must close.
fn write(
    writer: &mut TcpStream,
    status: u16,
    headers: &[(String, String)],
    body: &[u8],
    sent: usize,
) -> bool {
    let mut head = format!(
        "HTTP/1.1 {status} Test\r\nContent-Length: {}\r\n",
        body.len()
    );
    for (name, value) in headers {
        head.push_str(&format!("{name}: {value}\r\n"));
    }
    head.push_str("\r\n");
    writer.write_all(head.as_bytes()).is_ok()
        && writer.write_all(&body[..sent.min(body.len())]).is_ok()
        && writer.flush().is_ok()
        && sent >= body.len()
}
