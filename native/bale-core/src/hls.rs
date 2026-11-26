//! Silo's HLS for FFmpeg's forward-only input (docs/rust-port.md, "HLS through FFmpeg"). Silo serves
//! one rendition, never an adaptive ladder: a multivariant playlist only attaches subtitle groups to
//! its single variant. Media playlists are complete VOD lists, or FFmpeg's own append-only EVENT
//! playlist while a copy remux is still being produced. Segments are MPEG-TS, or fragmented MP4 after
//! an EXT-X-MAP init segment; both concatenate into one stream FFmpeg can demux.
//!
//! The playlist, init and every segment go through the shared `AuthClient` (401 refresh, busy
//! retries), and every child URI must stay on the server's media routes, as the C#
//! `PlaybackMediaUrl.IsAllowedChild` requires of Windows' HLS downloads. Anything this reader can't
//! play safely (encryption, byte ranges, discontinuities, separate audio renditions) is reported as
//! unsupported so the caller can keep Windows' own HLS source. Nothing logs; URLs carry tokens.
//!
//! Through a server outage the player keeps playing what the forward-only stream already buffered,
//! while a failed request (or a segment cut mid-body) retries with the web player's backoff until the
//! server answers or the budget is spent (outage_budget.rs, silo-server `40c093c`); only then does the
//! stream end as a fault, once its buffer has run out.

use std::collections::HashMap;
use std::io::Read;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use crate::auth_client::AuthClient;
use crate::media_fault;
use crate::outage_budget::{OutageBudget, Policy as OutagePolicy};
use crate::progressive_stream::BodySource;
use crate::range_stream::StreamError;
use crate::segment_media::Scanned;

/// Silo warns above 3.5 MiB and Roku refuses 4 MiB; anything bigger is not a Silo playlist.
const MAX_PLAYLIST_BYTES: u64 = 8 * 1024 * 1024;
/// A segment that gets no bytes this long is a stall (as the forward-only remux).
const READ_STALL: Duration = Duration::from_secs(30);
/// How long a growing playlist may take to list the next segment before the stream gives up.
const GROWTH_WAIT: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, PartialEq)]
pub struct Segment {
    pub uri: String,
    pub duration: f64,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct Playlist {
    /// A multivariant playlist's (single) video variant; the other fields are then empty.
    pub variant: Option<String>,
    pub init: Option<String>,
    pub segments: Vec<Segment>,
    pub media_sequence: u64,
    pub target_duration: f64,
    /// EXT-X-ENDLIST: no segment will be added.
    pub ended: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlaylistError {
    NotHls,
    Malformed,
    /// A feature this reader doesn't play (the caller keeps Windows' HLS source).
    Unsupported,
}

/// `NAME="value",OTHER=...` attribute lists.
fn attributes(list: &str) -> Option<HashMap<String, String>> {
    let mut out = HashMap::new();
    let mut rest = list.trim();
    while !rest.is_empty() {
        let (name, after) = rest.split_once('=')?;
        let name = name.trim().to_owned();
        let (value, after) = if let Some(quoted) = after.strip_prefix('"') {
            let end = quoted.find('"')?;
            (quoted[..end].to_owned(), &quoted[end + 1..])
        } else {
            match after.find(',') {
                Some(end) => (after[..end].trim().to_owned(), &after[end..]),
                None => (after.trim().to_owned(), ""),
            }
        };
        out.insert(name, value);
        rest = after.trim_start();
        match rest.strip_prefix(',') {
            Some(next) => rest = next.trim_start(),
            None if rest.is_empty() => {}
            None => return None,
        }
    }
    Some(out)
}

/// `{$name}` references, from EXT-X-DEFINE NAME/VALUE. An undefined name is malformed.
fn substitute(text: &str, vars: &HashMap<String, String>) -> Result<String, PlaylistError> {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(start) = rest.find("{$") {
        out.push_str(&rest[..start]);
        let after = &rest[start + 2..];
        let end = after.find('}').ok_or(PlaylistError::Malformed)?;
        out.push_str(vars.get(&after[..end]).ok_or(PlaylistError::Malformed)?);
        rest = &after[end + 1..];
    }
    out.push_str(rest);
    Ok(out)
}

/// Parses a playlist. URIs come back with variables substituted, still unresolved.
pub fn parse(text: &str) -> Result<Playlist, PlaylistError> {
    let mut lines = text
        .lines()
        .map(|line| line.trim())
        .filter(|line| !line.is_empty());
    if lines.next() != Some("#EXTM3U") {
        return Err(PlaylistError::NotHls);
    }
    let mut playlist = Playlist::default();
    let mut vars = HashMap::new();
    let mut pending_duration: Option<f64> = None;
    let mut expect_variant = false;
    for line in lines {
        if let Some(tag) = line.strip_prefix('#') {
            let (name, value) = tag.split_once(':').unwrap_or((tag, ""));
            match name {
                "EXT-X-DEFINE" => {
                    let attrs = attributes(value).ok_or(PlaylistError::Malformed)?;
                    match (attrs.get("NAME"), attrs.get("VALUE")) {
                        (Some(name), Some(value)) if attrs.len() == 2 => {
                            vars.insert(name.clone(), value.clone());
                        }
                        // QUERYPARAM/IMPORT: Silo doesn't use them.
                        _ => return Err(PlaylistError::Unsupported),
                    }
                }
                "EXT-X-STREAM-INF" => expect_variant = true,
                "EXT-X-MEDIA" => {
                    let attrs = attributes(value).ok_or(PlaylistError::Malformed)?;
                    // Subtitle groups are Bale's own overlay's job; a separate audio or video
                    // rendition would need a second stream.
                    if attrs.get("TYPE").map(String::as_str) != Some("SUBTITLES")
                        && attrs.contains_key("URI")
                    {
                        return Err(PlaylistError::Unsupported);
                    }
                }
                "EXTINF" => {
                    let duration = value
                        .split(',')
                        .next()
                        .and_then(|d| d.trim().parse::<f64>().ok())
                        .filter(|d| d.is_finite() && *d >= 0.0)
                        .ok_or(PlaylistError::Malformed)?;
                    pending_duration = Some(duration);
                }
                "EXT-X-MAP" => {
                    let attrs = attributes(value).ok_or(PlaylistError::Malformed)?;
                    if attrs.contains_key("BYTERANGE") || playlist.init.is_some() {
                        return Err(PlaylistError::Unsupported);
                    }
                    let uri = attrs.get("URI").ok_or(PlaylistError::Malformed)?;
                    playlist.init = Some(substitute(uri, &vars)?);
                }
                "EXT-X-MEDIA-SEQUENCE" => {
                    playlist.media_sequence =
                        value.trim().parse().map_err(|_| PlaylistError::Malformed)?;
                }
                "EXT-X-TARGETDURATION" => {
                    playlist.target_duration =
                        value.trim().parse().map_err(|_| PlaylistError::Malformed)?;
                }
                "EXT-X-ENDLIST" => playlist.ended = true,
                "EXT-X-KEY" | "EXT-X-SESSION-KEY" => {
                    let attrs = attributes(value).ok_or(PlaylistError::Malformed)?;
                    if attrs.get("METHOD").map(String::as_str) != Some("NONE") {
                        return Err(PlaylistError::Unsupported);
                    }
                }
                "EXT-X-BYTERANGE" | "EXT-X-DISCONTINUITY" | "EXT-X-GAP" | "EXT-X-PART" => {
                    return Err(PlaylistError::Unsupported);
                }
                _ => {}
            }
            continue;
        }
        let uri = substitute(line, &vars)?;
        if expect_variant {
            expect_variant = false;
            if playlist.variant.is_some() {
                // An adaptive ladder: never served by Silo; keep Windows' source for it.
                return Err(PlaylistError::Unsupported);
            }
            playlist.variant = Some(uri);
        } else {
            let duration = pending_duration.take().ok_or(PlaylistError::Malformed)?;
            playlist.segments.push(Segment { uri, duration });
        }
    }
    if expect_variant || pending_duration.is_some() {
        return Err(PlaylistError::Malformed);
    }
    if playlist.variant.is_some() && (!playlist.segments.is_empty() || playlist.init.is_some()) {
        return Err(PlaylistError::Malformed);
    }
    Ok(playlist)
}

/// `scheme://authority` and path of an absolute http(s) URL without userinfo or fragment.
fn split_url(url: &str) -> Option<(String, String, String, u16, &str)> {
    let (scheme, rest) = url.split_once("://")?;
    let scheme = scheme.to_ascii_lowercase();
    let default_port = match scheme.as_str() {
        "http" => 80,
        "https" => 443,
        _ => return None,
    };
    let authority_end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let authority = &rest[..authority_end];
    if authority.is_empty() || authority.contains('@') || url.contains('#') {
        return None;
    }
    let (host, port) = match authority.rsplit_once(':') {
        Some((host, port)) if !host.ends_with(']') || authority.starts_with('[') => {
            (host, port.parse::<u16>().ok()?)
        }
        _ => (authority, default_port),
    };
    let path_and_query = &rest[authority_end..];
    Some((
        scheme,
        authority.to_owned(),
        host.to_ascii_lowercase(),
        port,
        if path_and_query.is_empty() {
            "/"
        } else {
            path_and_query
        },
    ))
}

fn percent_decoded_is_dot(segment: &str) -> bool {
    let decoded = segment.replace("%2e", ".").replace("%2E", ".");
    decoded == "." || decoded == ".."
}

/// `PlaybackMediaUrl.IsSafePath`: no encoded or literal slash tricks, no dot segments.
fn is_safe_path(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    !lower.contains("%2f")
        && !lower.contains("%5c")
        && !path.contains('\\')
        && !path.split('/').any(percent_decoded_is_dot)
}

/// Resolves a playlist reference against the playlist's URL and checks it the way
/// `PlaybackMediaUrl.IsAllowedChild` checks Windows' HLS downloads: same scheme, host and port as
/// the server, a path on the server's media routes, no traversal. Stricter than .NET's `Uri`:
/// references with spaces or control characters, and network-path (`//host`) references, are
/// refused rather than escaped.
pub fn resolve_child(base: &str, reference: &str, server: &str) -> Option<String> {
    let reference = reference.trim();
    if reference.is_empty()
        || reference.starts_with("//")
        || reference.contains('#')
        || reference
            .chars()
            .any(|c| c.is_whitespace() || c.is_control())
    {
        return None;
    }
    let (server_scheme, _, server_host, server_port, server_path) = split_url(server)?;
    if server_path.contains('?') {
        return None;
    }
    let absolute = if reference.contains("://") {
        reference.to_owned()
    } else {
        let (scheme, authority, _, _, base_path) = split_url(base)?;
        let base_path = base_path.split('?').next().unwrap_or("/");
        if let Some(rooted) = reference.strip_prefix('/') {
            format!("{scheme}://{authority}/{rooted}")
        } else if reference.starts_with('?') {
            format!("{scheme}://{authority}{base_path}{reference}")
        } else {
            let directory = &base_path[..base_path.rfind('/').map_or(0, |i| i + 1)];
            format!("{scheme}://{authority}{directory}{reference}")
        }
    };
    let (scheme, _, host, port, path_and_query) = split_url(&absolute)?;
    let path = path_and_query.split('?').next().unwrap_or("/");
    let prefix = server_path.trim_end_matches('/');
    let media = path.starts_with(&format!("{prefix}/api/v2/stream/"))
        || path.starts_with(&format!("{prefix}/api/v2/playback/transcode/"));
    (scheme == server_scheme
        && host == server_host
        && port == server_port
        && is_safe_path(path)
        && media)
        .then_some(absolute)
}

/// A failed request, and whether the server can recover from it (protocol §6.2: a network failure, a
/// 5xx, 408 or 429, which the client has already retried as `Busy`, waits for the next attempt).
struct Failure {
    error: StreamError,
    transient: bool,
}

impl From<StreamError> for Failure {
    fn from(error: StreamError) -> Self {
        Failure {
            error,
            transient: matches!(error, StreamError::Network | StreamError::Busy),
        }
    }
}

fn status_failure(status: u16) -> Failure {
    Failure {
        error: StreamError::Status,
        transient: matches!(status, 408 | 429 | 500..=599),
    }
}

fn read_text(response: ureq::http::Response<ureq::Body>) -> Result<String, Failure> {
    if !response.status().is_success() {
        return Err(status_failure(response.status().as_u16()));
    }
    let mut text = String::new();
    response
        .into_body()
        .into_with_config()
        .limit(MAX_PLAYLIST_BYTES)
        .reader()
        .read_to_string(&mut text)
        .map_err(|_| Failure::from(StreamError::Network))?;
    Ok(text)
}

/// Why opening the HLS stream failed: a transport error, or a playlist this reader won't play.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OpenError {
    Stream(StreamError),
    Playlist(PlaylistError),
}

impl From<StreamError> for OpenError {
    fn from(error: StreamError) -> Self {
        OpenError::Stream(error)
    }
}

/// The init segment and media segments, in order, from the segment containing the start time.
pub struct HlsSource {
    client: Arc<AuthClient>,
    server: String,
    /// The media playlist's URL (after following a variant).
    playlist_url: String,
    playlist: Playlist,
    /// Media sequence number of the next segment to play.
    next_sequence: u64,
    init_pending: Option<String>,
    /// A media segment of this mount held media (segment_media.rs). Silo answers segments past a finished
    /// transcode's real end with none (silo-server `4bee8f8`): a mount whose segments to the playlist's end
    /// held none started past the media's end.
    media: Arc<AtomicBool>,
    /// The PAT and PMT of this mount's MPEG-TS, once a segment showed them (for the debug empty tail).
    ts_tables: Arc<Mutex<Option<Vec<u8>>>>,
    /// Debug (`media-fault empty-tail`): this many of a complete playlist's last segments are answered here
    /// with no media, as Silo answers segments past a transcode's real end.
    empty_tail: usize,
    /// While playing, a request the server can recover from retries through an outage (outage_budget.rs).
    outage: OutageBudget,
    epoch: Instant,
    /// The body being played, to resume it after a cut: its URL, declared length, and whether it is a
    /// media segment (scanned for media) rather than the init segment.
    current: Option<(String, Option<u64>, bool)>,
}

impl HlsSource {
    fn fetch(&self, url: &str) -> Result<Playlist, OpenError> {
        self.fetch_classified(url).map_err(|(error, _)| error)
    }

    /// A playlist GET; a failure says whether the server can recover from it.
    fn fetch_classified(&self, url: &str) -> Result<Playlist, (OpenError, bool)> {
        let stream = |failure: Failure| (OpenError::Stream(failure.error), failure.transient);
        let response = self
            .client
            .get(url, None, |request| request)
            .map_err(|error| stream(error.into()))?;
        let text = read_text(response).map_err(stream)?;
        parse(&text).map_err(|error| (OpenError::Playlist(error), false))
    }

    fn now_ms(&self) -> u64 {
        self.epoch.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
    }

    fn child(&self, reference: &str) -> Result<String, OpenError> {
        resolve_child(&self.playlist_url, reference, &self.server)
            .ok_or(OpenError::Playlist(PlaylistError::Unsupported))
    }

    /// Fetches the playlist (following a single variant) and picks the segment containing
    /// `start_seconds` on the playlist's timeline (the sum of EXTINF durations from its first
    /// segment). Returns the source and that segment's start time.
    pub fn open(
        client: Arc<AuthClient>,
        server: &str,
        url: &str,
        start_seconds: f64,
    ) -> Result<(Self, f64), OpenError> {
        Self::open_with_policy(client, server, url, start_seconds, OutagePolicy::WEB)
    }

    /// [`HlsSource::open`] with another outage budget (tests scale it down).
    pub fn open_with_policy(
        client: Arc<AuthClient>,
        server: &str,
        url: &str,
        start_seconds: f64,
        outage: OutagePolicy,
    ) -> Result<(Self, f64), OpenError> {
        let mut source = HlsSource {
            client,
            server: server.to_owned(),
            playlist_url: url.to_owned(),
            playlist: Playlist::default(),
            next_sequence: 0,
            init_pending: None,
            media: Arc::new(AtomicBool::new(false)),
            ts_tables: Arc::new(Mutex::new(None)),
            empty_tail: media_fault::take_empty_tail(),
            outage: OutageBudget::new(outage),
            epoch: Instant::now(),
            current: None,
        };
        let mut playlist = source.fetch(url)?;
        if let Some(variant) = playlist.variant.clone() {
            source.playlist_url = source.child(&variant)?;
            playlist = source.fetch(&source.playlist_url.clone())?;
            if playlist.variant.is_some() {
                return Err(OpenError::Playlist(PlaylistError::Unsupported));
            }
        }
        source.playlist = playlist;
        let start = if start_seconds.is_finite() {
            start_seconds.max(0.0)
        } else {
            0.0
        };
        // A growing playlist may not list the start yet: wait for it, as Windows' source does.
        let deadline = Instant::now() + GROWTH_WAIT;
        let (index, segment_start) = loop {
            let mut at = 0.0;
            let found = source.playlist.segments.iter().position(|segment| {
                let hit = start < at + segment.duration;
                if !hit {
                    at += segment.duration;
                }
                hit
            });
            match found {
                Some(index) => break (index, at),
                None if source.playlist.ended || Instant::now() >= deadline => {
                    let last = source.playlist.segments.len().saturating_sub(1);
                    let at = source.playlist.segments[..last]
                        .iter()
                        .map(|s| s.duration)
                        .sum();
                    if source.playlist.segments.is_empty() {
                        return Err(OpenError::Playlist(PlaylistError::Malformed));
                    }
                    break (last, at);
                }
                None => {
                    source.wait_for_growth().map_err(|(error, _)| error)?;
                }
            }
        };
        source.next_sequence = source.playlist.media_sequence + index as u64;
        if let Some(init) = source.playlist.init.clone() {
            source.init_pending = Some(source.child(&init)?);
        }
        Ok((source, segment_start))
    }

    /// Re-reads a growing playlist after half a target duration. A failure says whether the server can
    /// recover from it.
    fn wait_for_growth(&mut self) -> Result<(), (OpenError, bool)> {
        let pause = Duration::from_secs_f64((self.playlist.target_duration / 2.0).clamp(0.5, 5.0));
        if !self.client.sleep_unless_closed(pause) {
            return Err((OpenError::Stream(StreamError::Disposed), false));
        }
        let refreshed = self.fetch_classified(&self.playlist_url.clone())?;
        if refreshed.variant.is_some() || refreshed.init != self.playlist.init {
            return Err((OpenError::Playlist(PlaylistError::Unsupported), false));
        }
        self.playlist = refreshed;
        Ok(())
    }

    /// One GET of the init or a segment: the body (scanned for media when `segment`) and its declared
    /// length, with its first `skip` bytes read and dropped when it resumes one that was cut.
    fn get_once(
        &self,
        url: &str,
        segment: bool,
        skip: u64,
        expected: Option<u64>,
    ) -> Result<(Box<dyn Read + Send>, Option<u64>), Failure> {
        let response = self.client.get(url, None, |request| {
            // A segment can be large (4K) and the server may still be producing it.
            request
                .config()
                .timeout_global(None)
                .timeout_per_read(Some(READ_STALL))
                .build()
        })?;
        if !response.status().is_success() {
            return Err(status_failure(response.status().as_u16()));
        }
        let length = response
            .headers()
            .get("content-length")
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.trim().parse::<u64>().ok());
        // A resumed body must be the one that was cut: other bytes can't be spliced onto what played.
        if skip > 0 && expected.is_some() && length.is_some() && length != expected {
            return Err(StreamError::InvalidRange.into());
        }
        let reader = response
            .into_body()
            .into_with_config()
            .limit(u64::MAX)
            .reader();
        let mut body: Box<dyn Read + Send> = if segment {
            Box::new(Scanned::new(
                reader,
                self.media.clone(),
                self.ts_tables.clone(),
            ))
        } else {
            Box::new(reader)
        };
        if skip > 0 {
            let skipped = std::io::copy(&mut (&mut body).take(skip), &mut std::io::sink())
                .map_err(|_| Failure::from(StreamError::Network))?;
            if skipped < skip {
                return Err(StreamError::ShortBody.into());
            }
        }
        Ok((body, length))
    }

    /// The init or a segment, retried through an outage while the budget lasts (outage_budget.rs).
    fn get(
        &mut self,
        url: &str,
        segment: bool,
        skip: u64,
        expected: Option<u64>,
    ) -> Result<Box<dyn Read + Send>, StreamError> {
        loop {
            match self.get_once(url, segment, skip, expected) {
                Ok((body, length)) => {
                    self.outage.succeeded(self.now_ms());
                    self.current = Some((url.to_owned(), length, segment));
                    return Ok(body);
                }
                Err(failure) if !failure.transient => return Err(failure.error),
                Err(failure) => self.wait_out(failure.error)?,
            }
        }
    }

    /// The outage budget's wait after a failure the server can recover from; the failure itself once
    /// the budget is spent.
    fn wait_out(&mut self, error: StreamError) -> Result<(), StreamError> {
        let Some(delay) = self.outage.failed(self.now_ms()) else {
            media_fault::count(&media_fault::HLS_GIVEN_UP);
            return Err(error);
        };
        media_fault::count(&media_fault::HLS_RETRIES);
        if self
            .client
            .sleep_unless_closed(Duration::from_millis(delay))
        {
            Ok(())
        } else {
            Err(StreamError::Disposed)
        }
    }

    fn next(&mut self) -> Result<Option<Box<dyn Read + Send>>, StreamError> {
        if let Some(init) = self.init_pending.take() {
            return self.get(&init, false, 0, None).map(Some);
        }
        let mut deadline = Instant::now() + GROWTH_WAIT;
        loop {
            let sequence = self.playlist.media_sequence;
            if self.next_sequence < sequence {
                // A sliding playlist evicted the segment we need: the stream can't continue.
                return Err(StreamError::InvalidRange);
            }
            let index = (self.next_sequence - sequence) as usize;
            if let Some(segment) = self.playlist.segments.get(index) {
                let url = self
                    .child(&segment.uri)
                    .map_err(|_| StreamError::InvalidRange)?;
                self.next_sequence += 1;
                if self.playlist.ended && index + self.empty_tail >= self.playlist.segments.len() {
                    self.current = None;
                    return Ok(Some(self.debug_empty_segment()));
                }
                return self.get(&url, true, 0, None).map(Some);
            }
            if self.playlist.ended {
                return Ok(None);
            }
            if Instant::now() >= deadline {
                return Err(StreamError::Network);
            }
            match self.wait_for_growth() {
                Ok(()) => self.outage.succeeded(self.now_ms()),
                Err((OpenError::Stream(error), true)) => {
                    self.wait_out(error)?;
                    // An outage isn't the server being slow to list the next segment: a full wait once it
                    // answers again.
                    deadline = deadline.max(Instant::now() + GROWTH_WAIT);
                }
                Err((OpenError::Stream(error), false)) => return Err(error),
                Err((OpenError::Playlist(_), _)) => return Err(StreamError::InvalidRange),
            }
        }
    }

    /// The body being played was cut after `delivered` bytes (a reset, a stall, an outage): request it
    /// again once the outage budget allows and skip what the stream already has.
    fn resume_current(&mut self, delivered: u64) -> Result<Box<dyn Read + Send>, StreamError> {
        let (url, length, segment) = self.current.clone().ok_or(StreamError::Network)?;
        self.wait_out(StreamError::Network)?;
        media_fault::count(&media_fault::HLS_RESUMES);
        self.get(&url, segment, delivered, length)
    }
}

impl HlsSource {
    /// Debug empty tail: Silo's past-end answer, the PAT and PMT of this mount's MPEG-TS, or no bytes
    /// (its answer for fMP4 when it can't read the init segment, and here for a mount that saw no tables).
    fn debug_empty_segment(&self) -> Box<dyn Read + Send> {
        media_fault::count(&media_fault::HLS_EMPTY_SERVED);
        media_fault::count(&media_fault::HLS_EMPTY_SEGMENTS);
        let tables = self
            .ts_tables
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone();
        Box::new(std::io::Cursor::new(tables.unwrap_or_default()))
    }
}

impl BodySource for HlsSource {
    fn next_body(&mut self) -> Option<Result<Box<dyn Read + Send>, StreamError>> {
        self.next().transpose()
    }

    fn delivered_media(&self) -> Option<bool> {
        Some(self.media.load(Ordering::Acquire))
    }

    fn resume(&mut self, delivered: u64) -> Option<Result<Box<dyn Read + Send>, StreamError>> {
        Some(self.resume_current(delivered))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::progressive_stream::{ProgressiveStats, ProgressiveStream};
    use crate::segment_media::tests::{mp4_empty, mp4_media, ts_media, ts_tables};
    use crate::test_http::{Reply, TestServer};

    const SERVER: &str = "https://silo.example:8443/base";
    const ROUTE: &str = "/api/v2/playback/transcode/s";

    /// A complete VOD playlist of 4 s segments (fMP4 with an init when `init` is given) and a server for it.
    fn serve(segments: Vec<Vec<u8>>, init: Option<Vec<u8>>) -> TestServer {
        serve_faulty(segments, init, |_, _| None)
    }

    /// [`serve`], where `fault(segment, request)` may answer the `request`-th (0-based) request for a
    /// segment instead.
    fn serve_faulty(
        segments: Vec<Vec<u8>>,
        init: Option<Vec<u8>>,
        fault: impl Fn(usize, usize) -> Option<Reply> + Send + Sync + 'static,
    ) -> TestServer {
        let requests: Vec<_> = (0..segments.len())
            .map(|_| std::sync::atomic::AtomicUsize::new(0))
            .collect();
        let extension = if init.is_some() { "m4s" } else { "ts" };
        let mut playlist = String::from(
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n",
        );
        if init.is_some() {
            playlist.push_str("#EXT-X-MAP:URI=\"init.mp4?st=t\"\n");
        }
        for index in 0..segments.len() {
            playlist.push_str(&format!(
                "#EXTINF:4.000000,\nseg_{index}.{extension}?st=t\n"
            ));
        }
        playlist.push_str("#EXT-X-ENDLIST\n");
        TestServer::start(move |request| {
            let name = request.path().rsplit('/').next().unwrap_or_default();
            if name == "index.m3u8" {
                return Reply::ok(playlist.clone().into_bytes());
            }
            if name == "init.mp4" {
                return init.clone().map_or_else(|| Reply::status(404), Reply::ok);
            }
            let Some(index) = name
                .strip_prefix("seg_")
                .and_then(|rest| rest.split('.').next())
                .and_then(|index| index.parse::<usize>().ok())
                .filter(|index| *index < segments.len())
            else {
                return Reply::status(404);
            };
            let request = requests[index].fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            fault(index, request).unwrap_or_else(|| Reply::ok(segments[index].clone()))
        })
    }

    /// Opens the server's playlist at `start` and reads the stream to its end.
    fn play(server: &TestServer, start: f64) -> (Vec<u8>, ProgressiveStats) {
        play_with(server, start, OutagePolicy::WEB)
    }

    /// Outage waits of 20 ms doubling to 40 ms, at most `attempts`.
    fn quick(attempts: u32) -> OutagePolicy {
        OutagePolicy {
            base_delay_ms: 20,
            max_delay_ms: 40,
            max_attempts: attempts,
            stable_ms: 30_000,
        }
    }

    fn play_with(
        server: &TestServer,
        start: f64,
        outage: OutagePolicy,
    ) -> (Vec<u8>, ProgressiveStats) {
        let client = Arc::new(AuthClient::new(Vec::new()));
        let url = format!("{}{ROUTE}/index.m3u8?st=t", server.base);
        let (source, _) =
            HlsSource::open_with_policy(client.clone(), &server.base, &url, start, outage)
                .expect("open");
        let stream = ProgressiveStream::start(Box::new(source), Some(client)).expect("start");
        let mut out = Vec::new();
        let mut buffer = vec![0; 65_536];
        loop {
            let count = stream.read(&mut buffer);
            if count == 0 {
                break;
            }
            out.extend_from_slice(&buffer[..count]);
        }
        (out, stream.stats())
    }

    #[test]
    fn a_ts_playlist_ending_in_empty_segments_plays_to_a_clean_end() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![
            ts_media(40),
            ts_media(40),
            ts_media(40),
            ts_tables(),
            ts_tables(),
        ];
        let server = serve(segments.clone(), None);
        let (bytes, stats) = play(&server, 0.0);
        assert_eq!(bytes, segments.concat());
        assert_eq!((stats.completed, stats.faulted, stats.no_media), (1, 0, 0));
    }

    #[test]
    fn an_fmp4_playlist_ending_in_empty_fragments_or_no_bytes_plays_to_a_clean_end() {
        let _faults = media_fault::tests::exclusive();
        let init = b"\0\0\0\x10ftypiso6\0\0\0\0".to_vec();
        let segments = vec![mp4_media(5_000), mp4_media(5_000), mp4_empty(), Vec::new()];
        let server = serve(segments.clone(), Some(init.clone()));
        let (bytes, stats) = play(&server, 0.0);
        assert_eq!(bytes, [init, segments.concat()].concat());
        assert_eq!((stats.completed, stats.faulted, stats.no_media), (1, 0, 0));
    }

    #[test]
    fn a_mount_that_starts_in_the_empty_tail_reports_no_media() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![
            ts_media(40),
            ts_media(40),
            ts_media(40),
            ts_tables(),
            ts_tables(),
        ];
        let server = serve(segments.clone(), None);
        // A seek to 13 s opens the fourth segment: past the media's real end.
        let (bytes, stats) = play(&server, 13.0);
        assert_eq!(bytes, segments[3..].concat());
        assert_eq!((stats.completed, stats.faulted, stats.no_media), (1, 0, 1));
        let init = b"\0\0\0\x10ftypiso6\0\0\0\0".to_vec();
        let server = serve(vec![mp4_media(500), mp4_empty(), Vec::new()], Some(init));
        assert_eq!(play(&server, 5.0).1.no_media, 1);
        // The last real segment is no tail.
        assert_eq!(play(&server, 1.0).1.no_media, 0);
    }

    fn requests_for(server: &TestServer, segment: &str) -> usize {
        server.count(|r| r.path().ends_with(segment))
    }

    #[test]
    fn segments_ride_out_an_outage_while_the_stream_plays_what_it_has() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(40), ts_media(40), ts_media(40), ts_media(40)];
        // The third segment's first three requests meet a server that is away.
        let server = serve_faulty(segments.clone(), None, |segment, request| {
            (segment == 2 && request < 3).then_some(Reply::Drop)
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(12));
        assert_eq!(bytes, segments.concat());
        assert_eq!((stats.completed, stats.faulted), (1, 0));
        assert_eq!(requests_for(&server, "seg_2.ts"), 4);
    }

    #[test]
    fn a_segment_cut_mid_body_is_requested_again_and_spliced_exactly() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(400), ts_media(400), ts_media(400)];
        let cut = segments[1].clone();
        let resumes = media_fault::HLS_RESUMES.load(std::sync::atomic::Ordering::Relaxed);
        let server = serve_faulty(segments.clone(), None, move |segment, request| {
            (segment == 1 && request < 2).then(|| Reply::Cut {
                status: 200,
                headers: Vec::new(),
                body: cut.clone(),
                sent: 30_000 * (request + 1),
            })
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(12));
        // Nothing repeated, nothing lost: what was queued before each cut is skipped in the next answer.
        assert_eq!(bytes, segments.concat());
        assert_eq!((stats.completed, stats.faulted), (1, 0));
        assert_eq!(requests_for(&server, "seg_1.ts"), 3);
        assert!(media_fault::HLS_RESUMES.load(std::sync::atomic::Ordering::Relaxed) >= resumes + 2);
    }

    #[test]
    fn server_errors_wait_for_the_next_attempt_but_a_missing_segment_ends_the_stream() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(40), ts_media(40), ts_media(40)];
        let server = serve_faulty(segments.clone(), None, |segment, request| {
            match (segment, request) {
                (0, 0) => Some(Reply::status(500)),
                (0, 1) => Some(Reply::status(502)),
                (1, 0) => Some(Reply::status(408)),
                _ => None,
            }
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(12));
        assert_eq!(bytes, segments.concat());
        assert_eq!((stats.completed, stats.faulted), (1, 0));
        // A 404 is the server's answer, not an outage: the stream ends at once, after what it had.
        let server = serve_faulty(segments.clone(), None, |segment, _| {
            (segment == 1).then(|| Reply::status(404))
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(12));
        assert_eq!(bytes, segments[0]);
        assert_eq!((stats.completed, stats.faulted), (0, 1));
        assert_eq!(requests_for(&server, "seg_1.ts"), 1);
    }

    #[test]
    fn a_spent_budget_ends_the_stream_as_a_fault_after_what_it_buffered() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(40), ts_media(40), ts_media(40)];
        let server = serve_faulty(segments.clone(), None, |segment, _| {
            (segment == 2).then_some(Reply::Drop)
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(3));
        assert_eq!(bytes, segments[..2].concat());
        assert_eq!((stats.completed, stats.faulted), (0, 1));
        // The first request and one attempt after each of the budget's waits.
        assert_eq!(requests_for(&server, "seg_2.ts"), 4);
    }

    #[test]
    fn a_growing_playlist_refresh_rides_out_an_outage() {
        let _faults = media_fault::tests::exclusive();
        let segments = [ts_media(40), ts_media(40), ts_media(40)];
        let listing = |count: usize, ended: bool| {
            let mut text = String::from(
                "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n",
            );
            for index in 0..count {
                text.push_str(&format!("#EXTINF:1.000000,\nseg_{index}.ts\n"));
            }
            if ended {
                text.push_str("#EXT-X-ENDLIST\n");
            }
            text.into_bytes()
        };
        let reads = std::sync::atomic::AtomicUsize::new(0);
        let served = segments.clone();
        let server = TestServer::start(move |request| {
            let name = request.path().rsplit('/').next().unwrap_or_default();
            if name == "index.m3u8" {
                // The open, then two refreshes that meet a server that is away, then the rest of the list.
                return match reads.fetch_add(1, std::sync::atomic::Ordering::SeqCst) {
                    0 => Reply::ok(listing(2, false)),
                    1 | 2 => Reply::Drop,
                    _ => Reply::ok(listing(3, true)),
                };
            }
            let index: usize = name[4..5].parse().unwrap_or(9);
            served
                .get(index)
                .map_or_else(|| Reply::status(404), |body| Reply::ok(body.clone()))
        });
        let (bytes, stats) = play_with(&server, 0.0, quick(12));
        assert_eq!(bytes, segments.concat());
        assert_eq!((stats.completed, stats.faulted), (1, 0));
    }

    #[cfg(feature = "debug-probes")]
    #[test]
    fn the_debug_outage_cuts_and_refuses_until_it_ends() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(40), ts_media(40)];
        let server = serve(segments.clone(), None);
        let client = Arc::new(AuthClient::new(Vec::new()));
        let url = format!("{}{ROUTE}/index.m3u8?st=t", server.base);
        let (source, _) =
            HlsSource::open_with_policy(client.clone(), &server.base, &url, 0.0, quick(60))
                .expect("open");
        media_fault::set(media_fault::SetArgs {
            outage_seconds: Some(1),
            ..Default::default()
        });
        let started = Instant::now();
        let stream = ProgressiveStream::start(Box::new(source), Some(client)).expect("start");
        let mut bytes = Vec::new();
        let mut buffer = vec![0; 65_536];
        loop {
            let count = stream.read(&mut buffer);
            if count == 0 {
                break;
            }
            bytes.extend_from_slice(&buffer[..count]);
        }
        media_fault::set(media_fault::SetArgs {
            outage_seconds: Some(0),
            ..Default::default()
        });
        assert_eq!(bytes, segments.concat());
        assert_eq!((stream.stats().completed, stream.stats().faulted), (1, 0));
        assert!(started.elapsed() >= Duration::from_millis(900));
    }

    #[cfg(feature = "debug-probes")]
    #[test]
    fn the_debug_empty_tail_answers_the_last_segments_itself() {
        let _faults = media_fault::tests::exclusive();
        let segments = vec![ts_media(40), ts_media(40), ts_media(40), ts_media(40)];
        let server = serve(segments.clone(), None);
        let arm = |segments| {
            media_fault::set(media_fault::SetArgs {
                empty_tail: Some(segments),
                ..Default::default()
            })
        };
        assert_eq!(arm(2).empty_tail_armed, 2);
        let (bytes, stats) = play(&server, 0.0);
        // The last two answered with the session's PAT and PMT, never requested from the server.
        assert_eq!(
            bytes,
            [
                segments[0].clone(),
                segments[1].clone(),
                ts_tables(),
                ts_tables()
            ]
            .concat()
        );
        assert_eq!((stats.completed, stats.no_media), (1, 0));
        assert_eq!(
            server.count(|r| r.path().ends_with("seg_2.ts") || r.path().ends_with("seg_3.ts")),
            0
        );
        // Armed for one mount only; a mount inside an armed tail sees no media at all.
        assert_eq!(media_fault::state().empty_tail_armed, 0);
        arm(2);
        let (bytes, stats) = play(&server, 13.0);
        assert!(bytes.is_empty());
        assert_eq!((stats.completed, stats.no_media), (1, 1));
        assert_eq!(play(&server, 13.0).0, segments[3]);
    }

    #[test]
    fn parses_silos_vod_and_variables() {
        let text = "#EXTM3U\n#EXT-X-VERSION:8\n#EXT-X-DEFINE:NAME=\"silo_query\",VALUE=\"st=abc&seek=1\"\n\
            #EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n\
            #EXT-X-MAP:URI=\"seg/init.mp4?{$silo_query}\"\n#EXTINF:6.000000,\nseg/seg_00000.m4s?{$silo_query}\n\
            #EXTINF:4.5,\nseg/seg_00001.m4s?{$silo_query}\n#EXT-X-ENDLIST\n";
        let playlist = parse(text).unwrap();
        assert_eq!(playlist.init.as_deref(), Some("seg/init.mp4?st=abc&seek=1"));
        assert_eq!(playlist.segments.len(), 2);
        assert_eq!(playlist.segments[1].uri, "seg/seg_00001.m4s?st=abc&seek=1");
        assert_eq!(playlist.segments[1].duration, 4.5);
        assert!(playlist.ended);
        assert_eq!(playlist.target_duration, 6.0);
    }

    #[test]
    fn follows_a_single_variant_and_ignores_subtitle_groups() {
        let text = "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"x\",URI=\"master.m3u8?r=s\"\n\
            #EXT-X-STREAM-INF:BANDWIDTH=8000000,VIDEO-RANGE=PQ,SUBTITLES=\"subs\"\nmaster.m3u8?r=v\n";
        let playlist = parse(text).unwrap();
        assert_eq!(playlist.variant.as_deref(), Some("master.m3u8?r=v"));
        assert!(playlist.segments.is_empty());
    }

    #[test]
    fn refuses_what_it_cannot_play() {
        let unsupported = [
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:6,\na.ts\n",
            "#EXTM3U\n#EXTINF:6,\n#EXT-X-BYTERANGE:100@0\na.ts\n",
            "#EXTM3U\n#EXTINF:6,\na.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:6,\nb.ts\n",
            "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"x\",URI=\"a.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8\n",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2\nhigh.m3u8\n",
            "#EXTM3U\n#EXT-X-DEFINE:QUERYPARAM=\"st\"\n#EXTINF:6,\na.ts\n",
        ];
        for text in unsupported {
            assert_eq!(parse(text), Err(PlaylistError::Unsupported), "{text}");
        }
        assert_eq!(parse("not a playlist"), Err(PlaylistError::NotHls));
        assert_eq!(
            parse("#EXTM3U\n#EXTINF:6,\na.ts?{$missing}\n"),
            Err(PlaylistError::Malformed)
        );
        assert_eq!(parse("#EXTM3U\nb.ts\n"), Err(PlaylistError::Malformed));
        assert!(
            parse("#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:6,\na.ts\n")
                .unwrap()
                .segments
                .len()
                == 1
        );
    }

    #[test]
    fn children_stay_on_the_media_routes() {
        let base = "https://silo.example:8443/base/api/v2/stream/abc/master.m3u8?st=x";
        assert_eq!(
            resolve_child(base, "seg_00001.ts?st=x", SERVER).as_deref(),
            Some("https://silo.example:8443/base/api/v2/stream/abc/seg_00001.ts?st=x")
        );
        assert_eq!(
            resolve_child(base, "/base/api/v2/playback/transcode/s/seg.ts", SERVER).as_deref(),
            Some("https://silo.example:8443/base/api/v2/playback/transcode/s/seg.ts")
        );
        assert_eq!(
            resolve_child(base, "?r=v", SERVER).as_deref(),
            Some("https://silo.example:8443/base/api/v2/stream/abc/master.m3u8?r=v")
        );
        for bad in [
            "../../users/me",
            "%2e%2e/x.ts",
            "a%2Fb.ts",
            "a\\b.ts",
            "//evil.example/base/api/v2/stream/a.ts",
            "https://evil.example:8443/base/api/v2/stream/a.ts",
            "http://silo.example:8443/base/api/v2/stream/a.ts",
            "https://silo.example/base/api/v2/stream/a.ts",
            "https://user@silo.example:8443/base/api/v2/stream/a.ts",
            "/base/api/v2/users/a",
            "/api/v2/stream/a.ts",
            "a.ts#frag",
            "a b.ts",
        ] {
            assert_eq!(resolve_child(base, bad, SERVER), None, "{bad}");
        }
        assert_eq!(
            resolve_child("http://h/api/v2/stream/s/m.m3u8", "seg.ts", "http://h:80").as_deref(),
            Some("http://h/api/v2/stream/s/seg.ts")
        );
    }
}
