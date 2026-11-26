//! In-band closed captions (caption ABI, `bale_cc_*` in `lib.rs`): CEA-608 carried as ATSC A/53
//! `cc_data` in the video stream, decoded here and drawn by the subtitle overlay through libass.
//!
//! The FFmpegInteropX fork (bale.84+) hands every compressed packet of the playing H.264, HEVC or
//! MPEG-2 video stream to `bale_cc_push` with the token the app passed as `bale_cc_sink`, on its
//! sample (or GPU decoder feeder) thread, in decode order, and calls `bale_cc_flush` on every
//! seek, stream switch and close. A [`Captions`] decoder registered under that token
//! ([`Captions::register`]) then:
//! - finds the `cc_data` triplets (`extract`);
//! - puts them in presentation order by PTS (`reorder`);
//! - runs the selected channel's CEA-608 decoder (`cea608`, CC1 first, CC2-CC4 selectable);
//! - turns its displayed memory into timed cues (`cue`) and keeps the last ones;
//! - when an overlay is attached ([`Captions::attach`]), writes each cue as ASS events in the
//!   subtitle appearance (`ass`) and hands them to the overlay's live libass track as they come.
//!
//! The push must return quickly and never block: everything is a short lock and a little parsing,
//! and the overlay side only queues the events for its render thread. Every packet moves time on,
//! so an open caption's events are extended while it stays, and ended by the change after it.
//!
//! Platform independent except the overlay (Windows). Nothing here panics on any input, and nothing
//! logs (caption text is never logged). CEA-708 (DTVCC) is seen ([`SEEN_DTVCC`]) but not decoded
//! yet; `bale_cc_select` takes a service number for it.

pub mod ass;
pub mod cea608;
pub mod cue;
pub mod extract;
pub mod reorder;

use std::collections::{BTreeMap, VecDeque};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError, Weak};

use cea608::{Channel, Decoder, odd_parity};
use cue::{Cue, CueKind, displayed_rows};
use extract::{Codec, Extractor, Triplet};
use reorder::{Frame, Reorder};

use super::bitmap::E_NOT_FOUND;
use super::convert::ConvertOptions;
use super::{E_INVALIDARG, E_NOTIMPL, S_OK};

/// Returned by `bale_cc_abi_version`. The fork's tap (`bale_cc_push`, `bale_cc_flush`) requires 1;
/// Bale's own `bale_cc_*` exports are only ever added under it, never changed.
pub const CC_ABI_VERSION: u32 = 1;

/// `bale_cc_seen` bits: the channel carried caption commands (Resume Caption Loading, Roll-Up,
/// Resume Direct Captioning or End of Caption), not just padding or Text.
pub const SEEN_CC1: u32 = 1 << 0;
pub const SEEN_CC2: u32 = 1 << 1;
pub const SEEN_CC3: u32 = 1 << 2;
pub const SEEN_CC4: u32 = 1 << 3;
/// The stream carries A/53 `cc_data`, padding included.
pub const SEEN_CC_DATA: u32 = 1 << 8;
/// The stream carries valid DTVCC (CEA-708) data; not decoded yet.
pub const SEEN_DTVCC: u32 = 1 << 9;

/// How long a shown caption's events run at first; extended while it stays, so that a stream that
/// stops cannot leave one up.
const PROVISIONAL_MS: i64 = 30_000;
const EXTEND_BEFORE_MS: i64 = 10_000;
/// ANSI/CTA-608-E Annex C.9 (Media3 `MIN_DATA_CHANNEL_TIMEOUT_MS`): a roll-up or paint-on caption
/// with no new data for this long is erased.
const DATA_TIMEOUT_MS: i64 = 16_000;
/// Ended cues kept and replayed when an overlay attaches: the tap runs ahead of the clock.
const RETAIN_MS: i64 = 30_000;
const MAX_HISTORY: usize = 1024;

/// A change to a live caption track, in the order made.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum LiveCommand {
    /// A new event with ReadOrder `id`; times in ms of the tap's timeline, `text` the ASS event
    /// text (overrides included).
    Add {
        id: i32,
        start_ms: i64,
        end_ms: i64,
        text: String,
    },
    /// Event `id` now ends at `end_ms`: its caption ended, or is still shown and is extended.
    SetEnd { id: i32, end_ms: i64 },
    /// Every event goes: a seek, a flush, another channel or another stream.
    Clear,
}

/// Where an attached decoder's events go: the subtitle overlay's live caption track.
pub trait LiveSink: Send + Sync {
    /// Takes `commands` (leaving it empty) for live track `generation`. `E_NOT_FOUND` once that
    /// track is gone (another load, a clear, another attach); the decoder then detaches.
    fn live(&self, generation: u64, commands: &mut Vec<LiveCommand>) -> i32;
    /// Drops live track `generation` if it is still the overlay's track.
    fn end_live(&self, generation: u64);
}

/// A decoder's counters, as read by `bale_cc_stats`; field order is the C ABI.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleCcStats {
    /// Packets pushed since the token was registered.
    pub packets: u64,
    /// Of those, packets that carried `cc_data`.
    pub caption_packets: u64,
    /// CEA-608 byte pairs the selected channel acted on.
    pub pairs: u64,
    /// Captions shown (every change of what is displayed).
    pub cues: u64,
    /// Packets that came after a later PTS had been decoded (reorder depth too small).
    pub late: u32,
    /// PTS values the reorder buffer holds back.
    pub depth: u32,
    /// `bale_cc_flush` calls.
    pub flushes: u32,
    /// What `bale_cc_seen` returns.
    pub seen: u32,
}

#[cfg(target_pointer_width = "64")]
const _: () = assert!(size_of::<BaleCcStats>() == 48);

/// What `bale_cc_seen` reports, from both fields in decode order (no reordering needed).
#[derive(Clone, Copy, Debug, Default)]
struct Seen {
    bits: u32,
}

impl Seen {
    fn observe(&mut self, triplet: Triplet) {
        self.bits |= SEEN_CC_DATA;
        if !triplet.valid() {
            return;
        }
        let field = triplet.cc_type();
        if field >= 2 {
            self.bits |= SEEN_DTVCC;
            return;
        }
        let [a, b] = triplet.data();
        if !odd_parity(a) || !odd_parity(b) {
            return;
        }
        let (b1, b2) = (a & 0x7F, b & 0x7F);
        if !(0x10..=0x1F).contains(&b1) {
            return;
        }
        let data_channel = (b1 >> 3) & 1;
        let caption_command =
            (b1 & 0xF6) == 0x14 && matches!(b2, 0x20 | 0x25 | 0x26 | 0x27 | 0x29 | 0x2F);
        if caption_command {
            self.bits |= SEEN_CC1 << (field * 2 + data_channel);
        }
    }
}

/// An attached overlay.
struct Live {
    sink: Weak<dyn LiveSink>,
    generation: u64,
    options: ConvertOptions,
    flags: u32,
}

/// The caption on screen now, with the live events that draw it.
struct Open {
    cue: Cue,
    ids: Vec<i32>,
    /// Where those events end until extended or ended.
    until_ms: i64,
}

struct Inner {
    token: u64,
    channel: Channel,
    extractor: Extractor,
    reorder: Reorder,
    decoder: Decoder,
    seen: Seen,
    /// A packet with `cc_data` came since the stream started: from then on every packet goes
    /// through the reorder buffer.
    captioned: bool,
    /// The time of the last frame decoded, ms.
    last_ms: Option<i64>,
    /// When the channel last had caption data, ms (Annex C.9).
    last_data_ms: Option<i64>,
    open: Option<Open>,
    /// Ended cues, oldest first.
    history: VecDeque<Cue>,
    next_id: u32,
    live: Option<Live>,
    commands: Vec<LiveCommand>,
    stats: BaleCcStats,
}

/// One caption decoder: the handle behind the `bale_cc_*` exports.
pub struct Captions {
    inner: Mutex<Inner>,
}

/// 100 ns to ms, rounded.
fn to_ms(pts: i64) -> i64 {
    pts.saturating_add(5_000).div_euclid(10_000)
}

impl Captions {
    /// A decoder on CC1, registered under no token and attached to no overlay.
    pub fn new() -> Arc<Captions> {
        Arc::new(Captions {
            inner: Mutex::new(Inner {
                token: 0,
                channel: Channel::CC1,
                extractor: Extractor::default(),
                reorder: Reorder::default(),
                decoder: Decoder::new(Channel::CC1),
                seen: Seen::default(),
                captioned: false,
                last_ms: None,
                last_data_ms: None,
                open: None,
                history: VecDeque::new(),
                next_id: 0,
                live: None,
                commands: Vec::new(),
                stats: BaleCcStats::default(),
            }),
        })
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Takes the packets the tap pushes with `token` (1..; 0 takes none) as a new stream: what
    /// was decoded, seen and counted is dropped. A token registered to another decoder moves here.
    pub fn register(self: &Arc<Self>, token: u64) -> i32 {
        unregister(self);
        {
            let mut inner = self.lock();
            inner.restart();
            inner.stats = BaleCcStats::default();
            inner.seen = Seen::default();
            inner.token = token;
            inner.post();
        }
        if token != 0 {
            registry().insert(token, Arc::downgrade(self));
        }
        S_OK
    }

    /// Decodes `channel` (1..=4 = CC1..CC4) from now on; `service` is the CEA-708 service number,
    /// 0 for none (708 is not decoded yet: any other number is `E_NOTIMPL`). A change starts the
    /// channel's decoder afresh and clears what an attached overlay shows.
    pub fn select(&self, channel: u32, service: u32) -> i32 {
        let Some(channel) = Channel::new(channel) else {
            return E_INVALIDARG;
        };
        if service != 0 {
            return if service <= 63 {
                E_NOTIMPL
            } else {
                E_INVALIDARG
            };
        }
        let mut inner = self.lock();
        if inner.channel != channel {
            inner.channel = channel;
            inner.decoder = Decoder::new(channel);
            inner.forget();
            inner.post();
        }
        S_OK
    }

    /// `SEEN_*` bits of the stream so far.
    pub fn seen(&self) -> u32 {
        self.lock().seen.bits
    }

    pub fn stats(&self) -> BaleCcStats {
        let inner = self.lock();
        BaleCcStats {
            late: u32::try_from(inner.reorder.late()).unwrap_or(u32::MAX),
            depth: inner.reorder.depth() as u32,
            seen: inner.seen.bits,
            ..inner.stats
        }
    }

    /// One compressed packet (see `bale_cc_push`). `E_INVALIDARG` for an unknown codec or NAL
    /// length size.
    pub fn push(&self, data: &[u8], pts: Option<i64>, codec: u32, nal_length_size: u32) -> i32 {
        let Some(codec) = Codec::from_abi(codec) else {
            return E_INVALIDARG;
        };
        let size = match (codec, nal_length_size) {
            (Codec::Mpeg2, 0) => 0,
            (Codec::Mpeg2, _) => return E_INVALIDARG,
            (_, 0..=4) => nal_length_size as u8,
            _ => return E_INVALIDARG,
        };
        let mut inner = self.lock();
        inner.push(data, pts, codec, size);
        inner.post();
        S_OK
    }

    /// Seek, flush or stream switch: what is held and shown is dropped.
    pub fn flush(&self) -> i32 {
        let mut inner = self.lock();
        inner.stats.flushes = inner.stats.flushes.saturating_add(1);
        inner.restart();
        inner.post();
        S_OK
    }

    /// Sends the captions to live track `generation` of `sink` from now on, drawn with `options`
    /// and `flags` (`ass::FLAG_PLAIN`), starting with the kept ones and the one shown now. Replaces
    /// an earlier attachment (whose overlay is left as it is).
    pub fn attach(
        &self,
        sink: Weak<dyn LiveSink>,
        generation: u64,
        options: ConvertOptions,
        flags: u32,
    ) -> i32 {
        if flags & !ass::FLAG_PLAIN != 0 {
            return E_INVALIDARG;
        }
        let mut inner = self.lock();
        inner.live = Some(Live {
            sink,
            generation,
            options,
            flags,
        });
        inner.replay();
        inner.post();
        S_OK
    }

    /// Stops sending captions and drops the overlay's live track if it is still this one's.
    pub fn detach(&self) {
        let live = {
            let mut inner = self.lock();
            if let Some(open) = &mut inner.open {
                open.ids.clear();
            }
            inner.live.take()
        };
        // Outside the lock: the overlay takes its own.
        if let Some(live) = live
            && let Some(sink) = live.sink.upgrade()
        {
            sink.end_live(live.generation);
        }
    }

    /// The cues kept (ended ones within the last half minute, then the one shown, open-ended):
    /// tests and diagnostics.
    pub fn cues(&self) -> Vec<Cue> {
        let inner = self.lock();
        let mut cues: Vec<Cue> = inner.history.iter().cloned().collect();
        cues.extend(inner.open.as_ref().map(|open| open.cue.clone()));
        cues
    }

    /// Decodes everything still held back for reordering (end of a stream in tests and tools; the
    /// tap flushes instead).
    pub fn drain(&self) {
        let mut inner = self.lock();
        let mut frames = Vec::new();
        inner.reorder.drain(&mut |frame| frames.push(frame));
        for frame in frames {
            inner.decode(frame);
        }
        inner.post();
    }
}

impl Inner {
    /// A new run: nothing held, nothing shown, the decoder fresh (registration, flush).
    fn restart(&mut self) {
        self.reorder.flush();
        self.decoder = Decoder::new(self.channel);
        self.captioned = false;
        self.forget();
    }

    /// Drops the cues, and the attached overlay's events.
    fn forget(&mut self) {
        self.last_ms = None;
        self.last_data_ms = None;
        self.open = None;
        self.history.clear();
        if self.live.is_some() {
            self.commands.clear();
            self.commands.push(LiveCommand::Clear);
        }
    }

    fn push(&mut self, data: &[u8], pts: Option<i64>, codec: Codec, nal_length_size: u8) {
        self.stats.packets += 1;
        // Allocates only for a packet with captions.
        let mut triplets = Vec::new();
        self.extractor
            .extract(data, codec, nal_length_size, &mut triplets);
        if !triplets.is_empty() {
            self.stats.caption_packets += 1;
            self.captioned = true;
            for &triplet in &triplets {
                self.seen.observe(triplet);
            }
        }
        if !self.captioned {
            return;
        }
        let mut frames = Vec::new();
        self.reorder
            .push(pts, triplets, &mut |frame| frames.push(frame));
        for frame in frames {
            self.decode(frame);
        }
    }

    /// Runs the channel's decoder over one frame and follows what it displays.
    fn decode(&mut self, frame: Frame) {
        let mut t = to_ms(frame.pts);
        // A late frame cannot move what was shown back in time.
        if let Some(last) = self.last_ms {
            t = t.max(last);
        }
        let mut data = false;
        for &triplet in &frame.triplets {
            if self.decoder.feed(triplet) {
                data = true;
                self.stats.pairs += 1;
            }
        }
        let mut changed_at = t;
        if data {
            self.last_data_ms = Some(t);
        } else if let Some(last) = self.last_data_ms
            && t - last >= DATA_TIMEOUT_MS
            && self.decoder.expire()
        {
            changed_at = (last + DATA_TIMEOUT_MS).max(self.last_ms.unwrap_or(i64::MIN));
            self.last_data_ms = None;
        }
        if self.decoder.take_touched() {
            let rows = displayed_rows(self.decoder.displayed());
            let kind = CueKind::of(self.decoder.mode());
            self.show(changed_at, kind, rows);
        }
        self.last_ms = Some(t);
        self.extend(t);
    }

    /// What is displayed from `t` on.
    fn show(&mut self, t: i64, kind: CueKind, rows: Vec<cue::CueRow>) {
        if self.open.as_ref().is_some_and(|open| open.cue.rows == rows) {
            return;
        }
        self.end_open(t);
        if rows.is_empty() {
            return;
        }
        let cue = Cue {
            start_ms: t,
            end_ms: None,
            kind,
            rows,
        };
        self.stats.cues += 1;
        let until_ms = t.saturating_add(PROVISIONAL_MS);
        let ids = self.add_events(&cue, until_ms);
        self.open = Some(Open { cue, ids, until_ms });
    }

    fn end_open(&mut self, t: i64) {
        let Some(open) = self.open.take() else {
            return;
        };
        if self.live.is_some() {
            for &id in &open.ids {
                self.commands.push(LiveCommand::SetEnd { id, end_ms: t });
            }
        }
        let mut cue = open.cue;
        cue.end_ms = Some(t);
        self.history.push_back(cue);
        while self.history.len() > MAX_HISTORY
            || self
                .history
                .front()
                .is_some_and(|cue| cue.end_ms.is_some_and(|end| end < t - RETAIN_MS))
        {
            self.history.pop_front();
        }
    }

    /// Keeps the shown caption's events running while it stays.
    fn extend(&mut self, t: i64) {
        let Some(open) = &mut self.open else {
            return;
        };
        if t.saturating_add(EXTEND_BEFORE_MS) < open.until_ms {
            return;
        }
        open.until_ms = t.saturating_add(PROVISIONAL_MS);
        if self.live.is_some() {
            for &id in &open.ids {
                self.commands.push(LiveCommand::SetEnd {
                    id,
                    end_ms: open.until_ms,
                });
            }
        }
    }

    /// Queues `cue`'s events for the attached overlay, ending at `end_ms`; their ids.
    fn add_events(&mut self, cue: &Cue, end_ms: i64) -> Vec<i32> {
        let Some(live) = &self.live else {
            return Vec::new();
        };
        let texts = ass::events(cue, &live.options, live.flags);
        let mut ids = Vec::with_capacity(texts.len());
        for text in texts {
            // ReadOrder is a C int; ids only have to be unique among the track's events.
            let id = (self.next_id & 0x7FFF_FFFF) as i32;
            self.next_id = self.next_id.wrapping_add(1);
            ids.push(id);
            self.commands.push(LiveCommand::Add {
                id,
                start_ms: cue.start_ms,
                end_ms,
                text,
            });
        }
        ids
    }

    /// A new attachment starts from the kept cues and the shown one.
    fn replay(&mut self) {
        self.commands.clear();
        self.commands.push(LiveCommand::Clear);
        let history: Vec<Cue> = self.history.iter().cloned().collect();
        for cue in &history {
            if let Some(end) = cue.end_ms {
                self.add_events(cue, end);
            }
        }
        if let Some(open) = self.open.take() {
            let ids = self.add_events(&open.cue, open.until_ms);
            self.open = Some(Open { ids, ..open });
        }
    }

    /// Hands the queued commands to the attached overlay; detaches when it has moved on.
    fn post(&mut self) {
        if self.commands.is_empty() {
            return;
        }
        let Some(live) = &self.live else {
            self.commands.clear();
            return;
        };
        let delivered = live
            .sink
            .upgrade()
            .map(|sink| sink.live(live.generation, &mut self.commands));
        self.commands.clear();
        if delivered != Some(S_OK) {
            self.live = None;
            if let Some(open) = &mut self.open {
                open.ids.clear();
            }
        }
    }
}

/// Process-wide: which decoder each token feeds. Weak, so a destroyed decoder is simply gone.
static REGISTRY: Mutex<BTreeMap<u64, Weak<Captions>>> = Mutex::new(BTreeMap::new());

fn registry() -> MutexGuard<'static, BTreeMap<u64, Weak<Captions>>> {
    REGISTRY.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Removes every token registered to `captions`, and entries whose decoder is gone.
pub fn unregister(captions: &Arc<Captions>) {
    let captions = Arc::downgrade(captions);
    registry().retain(|_, bound| bound.strong_count() > 0 && !Weak::ptr_eq(bound, &captions));
}

fn lookup(token: u64) -> Option<Arc<Captions>> {
    if token == 0 {
        return None;
    }
    registry().get(&token)?.upgrade()
}

/// `bale_cc_push`: `E_NOT_FOUND` when no decoder is registered under `token`.
pub fn push(token: u64, data: &[u8], pts_100ns: i64, codec: u32, nal_length_size: u32) -> i32 {
    match lookup(token) {
        Some(captions) => {
            let pts = (pts_100ns != i64::MIN).then_some(pts_100ns);
            captions.push(data, pts, codec, nal_length_size)
        }
        None => E_NOT_FOUND,
    }
}

/// `bale_cc_flush`.
pub fn flush(token: u64) -> i32 {
    match lookup(token) {
        Some(captions) => captions.flush(),
        None => E_NOT_FOUND,
    }
}

/// The header a live caption track starts from, in `options`.
pub fn header(options: &ConvertOptions) -> String {
    ass::header(options)
}

#[cfg(test)]
#[path = "tests.rs"]
mod tests;
