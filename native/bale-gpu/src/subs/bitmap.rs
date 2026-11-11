//! Image subtitles (PGS, DVD, DVB): the bitmap ABI (`bale_sub_bitmap_*` in `lib.rs`), the queue of
//! decoded events with its timing rules, where a subtitle canvas lands in the frame, and the
//! palette conversion. FFmpegInteropX decodes the events on its demux thread and pushes them with
//! a token; an overlay bound to that token (`bale_sub_overlay_bind_bitmaps`) draws them.
//!
//! Everything here is platform-independent; the drawing is in `overlay`.

use std::collections::{BTreeMap, VecDeque};
use std::sync::{Arc, Mutex, PoisonError, Weak};

use super::{E_INVALIDARG, E_POINTER, Layout, MAX_FRAME_SIZE};

/// Returned by `bale_sub_bitmap_abi_version`; bumped whenever a `bale_sub_bitmap_*` export or one
/// of its structs changes.
pub const BITMAP_ABI_VERSION: u32 = 1;

/// `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)`: no overlay is bound to the token.
pub const E_NOT_FOUND: i32 = 0x8007_0490u32 as i32;

/// Limits of one pushed event.
pub const MAX_RECTS: i32 = 64;
pub const MAX_RECT_PIXELS: i64 = 4096 * 4096;
pub const MAX_COLORS: i32 = 256;
/// Events kept per overlay; the oldest goes when one more arrives.
pub const MAX_EVENTS: usize = 256;

/// One PAL8 rectangle of an event, as FFmpeg's `AVSubtitleRect` has it.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct BaleSubBitmapRect {
    /// Position and size in canvas pixels.
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
    /// Bytes from one row of indices to the next.
    pub linesize: i32,
    /// Palette entries (at most 256).
    pub nb_colors: i32,
    /// `h` rows of `w` palette indices, `linesize` apart.
    pub indices: *const u8,
    /// `nb_colors` entries in `AV_PIX_FMT_PAL8` layout: native-endian `0xAARRGGBB`, straight alpha.
    pub palette: *const u32,
}

/// One decoded subtitle event.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct BaleSubBitmapEvent {
    /// Media timeline, the clock of `bale_sub_overlay_set_clock`.
    pub start_ms: i64,
    /// Negative or not after `start_ms`: shown until the next event.
    pub end_ms: i64,
    /// Subtitle canvas (`AVCodecContext` width/height); not positive when unknown.
    pub canvas_w: i32,
    pub canvas_h: i32,
    /// Decoded video frame size; not positive when unknown.
    pub video_w: i32,
    pub video_h: i32,
    pub rects: *const BaleSubBitmapRect,
    /// 0 is a clear: it only ends what is showing at `start_ms`.
    pub count: i32,
    /// Must be 0.
    pub reserved: i32,
}

#[cfg(target_pointer_width = "64")]
const _: () = assert!(size_of::<BaleSubBitmapRect>() == 40);
#[cfg(target_pointer_width = "64")]
const _: () = assert!(size_of::<BaleSubBitmapEvent>() == 48);

/// A copied rectangle. The indices stay palettised until the overlay uploads them, so a queued
/// event costs a quarter of its RGBA size and the pushing (demux) thread only copies.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
    /// PGS composition forced flag (selection is track-based, so both kinds draw).
    pub forced: bool,
    /// `w * h` palette indices, rows packed. Shared so overlapping windows carry a held
    /// composition without copying its pixels on the UI thread.
    indices: Arc<Vec<u8>>,
    /// Premultiplied RGBA per palette entry.
    palette: Arc<Vec<[u8; 4]>>,
}

impl Rect {
    pub fn new(x: i32, y: i32, w: u32, h: u32, indices: Vec<u8>, palette: &[u32]) -> Self {
        debug_assert_eq!(indices.len(), w as usize * h as usize);
        Rect {
            x,
            y,
            w,
            h,
            indices: Arc::new(indices),
            forced: false,
            palette: Arc::new(
                palette
                    .iter()
                    .map(|&color| premultiplied_rgba(color))
                    .collect(),
            ),
        }
    }

    /// The picture as premultiplied RGBA8 rows. An index past the palette is transparent.
    pub fn rgba(&self) -> Vec<u8> {
        let mut rgba = Vec::with_capacity(self.indices.len() * 4);
        for &index in self.indices.iter() {
            let color = self.palette.get(usize::from(index)).unwrap_or(&[0; 4]);
            rgba.extend_from_slice(color);
        }
        rgba
    }
}

/// A PAL8 palette entry (`0xAARRGGBB`, straight alpha) as premultiplied RGBA bytes.
pub fn premultiplied_rgba(color: u32) -> [u8; 4] {
    let [a, r, g, b] = color.to_be_bytes();
    let scale = |channel: u8| (u32::from(channel) * u32::from(a) / 255) as u8;
    [scale(r), scale(g), scale(b), a]
}

/// A copied event.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Event {
    pub start_ms: i64,
    /// `None` when open-ended (until the next event).
    pub end_ms: Option<i64>,
    /// Canvas and video sizes, each `None` when unknown.
    pub canvas: Option<(u32, u32)>,
    pub video: Option<(u32, u32)>,
    /// Empty for a clear.
    pub rects: Vec<Rect>,
}

impl Event {
    /// Copies and validates what `event` points to: `E_POINTER` for a missing pointer,
    /// `E_INVALIDARG` for sizes or counts outside the ABI's limits. Empty rectangles are left out.
    ///
    /// # Safety
    /// `event` must be null or point to a readable event whose `rects` (when `count > 0`) point to
    /// `count` rectangles, each with `(h - 1) * linesize + w` readable index bytes and `nb_colors`
    /// readable palette entries.
    pub unsafe fn copy_from(event: *const BaleSubBitmapEvent) -> Result<Event, i32> {
        if event.is_null() {
            return Err(E_POINTER);
        }
        // SAFETY: the caller guarantees a readable event; nothing is assumed about alignment.
        let raw = unsafe { event.read_unaligned() };
        if raw.reserved != 0 || !(0..=MAX_RECTS).contains(&raw.count) {
            return Err(E_INVALIDARG);
        }
        if raw.count > 0 && raw.rects.is_null() {
            return Err(E_POINTER);
        }
        let mut rects = Vec::with_capacity(raw.count as usize);
        for at in 0..raw.count as usize {
            // SAFETY: `at < count`, and the caller guarantees `count` readable rectangles.
            let rect = unsafe { raw.rects.add(at).read_unaligned() };
            // SAFETY: forwarded contract for this rectangle's buffers.
            if let Some(rect) = unsafe { copy_rect(&rect) }? {
                rects.push(rect);
            }
        }
        let size = |w: i32, h: i32| (w > 0 && h > 0).then_some((w as u32, h as u32));
        Ok(Event {
            start_ms: raw.start_ms,
            end_ms: (raw.end_ms > raw.start_ms).then_some(raw.end_ms),
            canvas: size(raw.canvas_w, raw.canvas_h),
            video: size(raw.video_w, raw.video_h),
            rects,
        })
    }

    /// Where the event ends on its own or because `next` (the event queued after it) starts.
    fn effective_end(&self, next: Option<&Event>) -> Option<i64> {
        match (self.end_ms, next.map(|next| next.start_ms)) {
            (Some(end), Some(next)) => Some(end.min(next)),
            (end, next) => end.or(next),
        }
    }

    /// The canvas, video frame and frame layout as a [`Placement`].
    pub fn placement(&self, layout: &Layout) -> Placement {
        let bounds = self.rects.iter().fold((0i64, 0i64), |(w, h), rect| {
            (
                w.max(i64::from(rect.x) + i64::from(rect.w)),
                h.max(i64::from(rect.y) + i64::from(rect.h)),
            )
        });
        let bounds = (bounds.0.max(1) as f32, bounds.1.max(1) as f32);
        let as_f32 = |(w, h): (u32, u32)| (w as f32, h as f32);
        place(
            self.canvas.map(as_f32),
            self.video.map(as_f32),
            bounds,
            [
                layout.video_x,
                layout.video_y,
                layout.video_w,
                layout.video_h,
            ],
        )
    }
}

/// Validates and copies one rectangle; `None` for an empty one.
///
/// # Safety
/// As [`Event::copy_from`], for this rectangle's `indices` and `palette`.
unsafe fn copy_rect(rect: &BaleSubBitmapRect) -> Result<Option<Rect>, i32> {
    let side = 0..=MAX_FRAME_SIZE as i32;
    if !side.contains(&rect.w)
        || !side.contains(&rect.h)
        || i64::from(rect.w) * i64::from(rect.h) > MAX_RECT_PIXELS
        || !(0..=MAX_COLORS).contains(&rect.nb_colors)
        || rect.linesize < rect.w
    {
        return Err(E_INVALIDARG);
    }
    if rect.w == 0 || rect.h == 0 {
        return Ok(None);
    }
    if rect.indices.is_null() || (rect.nb_colors > 0 && rect.palette.is_null()) {
        return Err(E_POINTER);
    }
    let (w, h) = (rect.w as usize, rect.h as usize);
    let mut indices = Vec::with_capacity(w * h);
    for row in 0..h {
        // SAFETY: the caller guarantees `w` readable bytes in each of the `h` rows, which start
        // `linesize` bytes apart.
        let source = unsafe {
            std::slice::from_raw_parts(rect.indices.add(row * rect.linesize as usize), w)
        };
        indices.extend_from_slice(source);
    }
    let palette: Vec<u32> = (0..rect.nb_colors as usize)
        // SAFETY: the caller guarantees `nb_colors` readable entries; alignment is not assumed.
        .map(|at| unsafe { rect.palette.add(at).read_unaligned() })
        .collect();
    Ok(Some(Rect::new(
        rect.x,
        rect.y,
        rect.w as u32,
        rect.h as u32,
        indices,
        &palette,
    )))
}

/// The events of one overlay, ascending by start, one per start time.
#[derive(Debug, Default)]
pub struct Queue {
    events: VecDeque<Arc<Event>>,
    /// Sidecar windows retain history for backward seeks; the PGS decoder bounds their memory.
    retained: bool,
}

impl Queue {
    /// Adds `event` in start order; one with the same start is replaced. Past [`MAX_EVENTS`] the
    /// oldest event is dropped.
    pub fn push(&mut self, event: Event) {
        let at = self
            .events
            .partition_point(|queued| queued.start_ms < event.start_ms);
        let event = Arc::new(event);
        match self.events.get_mut(at) {
            Some(queued) if queued.start_ms == event.start_ms => *queued = event,
            _ => self.events.insert(at, event),
        }
        if self.events.len() > MAX_EVENTS {
            self.events.pop_front();
        }
    }

    pub fn replace(&mut self, events: Vec<Event>) {
        self.events = events.into_iter().map(Arc::new).collect();
        self.retained = true;
    }

    /// A positioned extract can start inside a long-held composition. If it has no state yet at
    /// the current clock, carry the old visible picture through the overlap until a known clear
    /// or the first new event. Only one picture is copied, so retained memory stays bounded.
    pub fn replace_window(&mut self, mut events: Vec<Event>, now: i64, end: i64) {
        if self.retained
            && events.first().is_none_or(|e| e.start_ms > now)
            && let Some(at) = self.current(now)
            && self.visible(now).is_some()
        {
            let mut carry = (*self.events[at]).clone();
            // The old window cap is synthetic; a following display set is the authored end.
            carry.end_ms = Some(
                self.events
                    .get(at + 1)
                    .map_or(end, |next| end.min(next.start_ms)),
            );
            events.insert(0, carry);
        }
        self.replace(events);
    }

    pub fn flush(&mut self) {
        self.retained = false;
        self.events.clear();
    }

    pub fn len(&self) -> usize {
        self.events.len()
    }

    pub fn is_empty(&self) -> bool {
        self.events.is_empty()
    }

    /// Index of the event that started last at or before `t_ms`.
    fn current(&self, t_ms: i64) -> Option<usize> {
        self.events
            .partition_point(|event| event.start_ms <= t_ms)
            .checked_sub(1)
    }

    fn effective_end(&self, at: usize) -> Option<i64> {
        self.events[at].effective_end(self.events.get(at + 1).map(|next| &**next))
    }

    /// The event to show at `t_ms`: the last one started, unless it has ended or is a clear.
    /// Events never overlap (each ends where the next starts), so there is at most one.
    pub fn visible(&self, t_ms: i64) -> Option<&Arc<Event>> {
        let at = self.current(t_ms)?;
        let shown = self.effective_end(at).is_none_or(|end| t_ms < end);
        let event = &self.events[at];
        (shown && !event.rects.is_empty()).then_some(event)
    }

    /// Drops the events that ended at or before `t_ms`. An open-ended last event (a clear too)
    /// stays: a late push with an earlier start still has to end at it.
    pub fn prune(&mut self, t_ms: i64) {
        if self.retained {
            return;
        }
        while !self.events.is_empty() && self.effective_end(0).is_some_and(|end| end <= t_ms) {
            self.events.pop_front();
        }
    }

    /// The first start or end after `t_ms` at which the shown picture can change; `None` when
    /// nothing queued changes it any more.
    pub fn next_boundary(&self, t_ms: i64) -> Option<i64> {
        let current = self.current(t_ms);
        // Earlier events ended at or before the current one's start, so only its end is ahead.
        let end = current
            .and_then(|at| self.effective_end(at))
            .filter(|&end| end > t_ms);
        let start = self
            .events
            .get(current.map_or(0, |at| at + 1))
            .map(|event| event.start_ms);
        match (end, start) {
            (Some(end), Some(start)) => Some(end.min(start)),
            (end, start) => end.or(start),
        }
    }
}

/// How canvas pixels map to frame pixels: `frame = offset + canvas * scale` per axis.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Placement {
    pub scale_x: f32,
    pub scale_y: f32,
    pub offset_x: f32,
    pub offset_y: f32,
}

impl Placement {
    /// Frame rectangle `[x, y, width, height]` of a canvas rectangle. The edges are rounded to
    /// whole pixels, so an unscaled bitmap is drawn texel for pixel and neighbours stay joined.
    pub fn rect(&self, x: i32, y: i32, w: u32, h: u32) -> [f32; 4] {
        let edge =
            |offset: f32, scale: f32, at: f64| (f64::from(offset) + at * f64::from(scale)).round();
        let x0 = edge(self.offset_x, self.scale_x, f64::from(x));
        let x1 = edge(self.offset_x, self.scale_x, f64::from(x) + f64::from(w));
        let y0 = edge(self.offset_y, self.scale_y, f64::from(y));
        let y1 = edge(self.offset_y, self.scale_y, f64::from(y) + f64::from(h));
        [x0 as f32, y0 as f32, (x1 - x0) as f32, (y1 - y0) as f32]
    }
}

/// Maps a subtitle canvas into the displayed video rectangle `video_rect` (`[x, y, width,
/// height]` in frame pixels). An unknown canvas is taken to be the video frame, an unknown video
/// frame the canvas, and with both unknown the rectangles' bounding box `bounds` stands in.
///
/// Each axis on its own: where canvas and video agree the canvas fills the rectangle. Where they
/// differ but the other axis agrees, the video is a cropped encode of the canvas (1920x1080 PGS
/// over 1920x1040 video): the canvas keeps the video's pixel scale and is centred, so its
/// overhang falls into the bars. When neither axis agrees the canvas is stretched to fill.
pub fn place(
    canvas: Option<(f32, f32)>,
    video: Option<(f32, f32)>,
    bounds: (f32, f32),
    video_rect: [f32; 4],
) -> Placement {
    let canvas = canvas.or(video).unwrap_or(bounds);
    let video = video.unwrap_or(canvas);
    let [rect_x, rect_y, rect_w, rect_h] = video_rect;
    let same = (canvas.0 == video.0, canvas.1 == video.1);
    let axis = |canvas: f32, video: f32, origin: f32, len: f32, cropped: bool| {
        if cropped && video > 0.0 {
            let scale = len / video;
            (scale, origin + (len - canvas * scale) / 2.0)
        } else if canvas > 0.0 {
            (len / canvas, origin)
        } else {
            (1.0, origin)
        }
    };
    let (scale_x, offset_x) = axis(canvas.0, video.0, rect_x, rect_w, !same.0 && same.1);
    let (scale_y, offset_y) = axis(canvas.1, video.1, rect_y, rect_h, !same.1 && same.0);
    Placement {
        scale_x,
        scale_y,
        offset_x,
        offset_y,
    }
}

/// What a token is bound to: the shared state of an overlay.
pub trait Sink: Send + Sync {
    /// Queues `event` if the sink is (still) bound to `token`; an HRESULT.
    fn push(&self, token: u64, event: Event) -> i32;
    /// Atomically installs a decoded sidecar window if the token still owns the sink.
    fn replace(&self, token: u64, events: Vec<Event>, now: i64, end: i64) -> i32;
    /// Drops everything queued for `token`; an HRESULT.
    fn flush(&self, token: u64) -> i32;
    /// Another sink took `token` over.
    fn unbound(&self, token: u64);
}

/// Process-wide: which sink each token feeds. Weak, so a destroyed overlay is simply gone.
static REGISTRY: Mutex<BTreeMap<u64, Weak<dyn Sink>>> = Mutex::new(BTreeMap::new());

fn registry() -> std::sync::MutexGuard<'static, BTreeMap<u64, Weak<dyn Sink>>> {
    REGISTRY.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Binds `token` (not 0) to `sink`, replacing and notifying a sink that held it.
pub fn bind(token: u64, sink: &Arc<dyn Sink>) {
    let old = registry().insert(token, Arc::downgrade(sink));
    // Outside the registry lock: the sink takes its own.
    if let Some(old) = old.and_then(|old| old.upgrade())
        && !Arc::ptr_eq(&old, sink)
    {
        old.unbound(token);
    }
}

/// Removes every token bound to `sink`, and entries whose sink is gone.
pub fn unbind(sink: &Arc<dyn Sink>) {
    let sink = Arc::downgrade(sink);
    registry().retain(|_, bound| bound.strong_count() > 0 && !Weak::ptr_eq(bound, &sink));
}

fn lookup(token: u64) -> Option<Arc<dyn Sink>> {
    registry().get(&token)?.upgrade()
}

/// `bale_sub_bitmap_push`: copies `event` and hands it to the sink bound to `token`.
///
/// # Safety
/// As [`Event::copy_from`].
pub unsafe fn push(token: u64, event: *const BaleSubBitmapEvent) -> i32 {
    // Looked up first: an event nobody shows is not worth copying.
    let Some(sink) = lookup(token) else {
        return E_NOT_FOUND;
    };
    // SAFETY: forwarded contract.
    match unsafe { Event::copy_from(event) } {
        Ok(event) => sink.push(token, event),
        Err(hr) => hr,
    }
}

/// Commit a decoded sidecar; a retired mount cannot replace its successor's track.
pub fn replace(token: u64, events: Vec<Event>, now: i64, end: i64) -> i32 {
    match lookup(token) {
        Some(sink) => sink.replace(token, events, now, end),
        None => E_NOT_FOUND,
    }
}

/// `bale_sub_bitmap_flush`.
pub fn flush(token: u64) -> i32 {
    match lookup(token) {
        Some(sink) => sink.flush(token),
        None => E_NOT_FOUND,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::subs::S_OK;

    fn event(start_ms: i64, end_ms: i64, rects: usize) -> Event {
        Event {
            start_ms,
            end_ms: (end_ms > start_ms).then_some(end_ms),
            canvas: None,
            video: None,
            rects: (0..rects)
                .map(|at| Rect::new(at as i32, 0, 1, 1, vec![0], &[0xFFFF_FFFF]))
                .collect(),
        }
    }

    fn queue(events: &[(i64, i64, usize)]) -> Queue {
        let mut queue = Queue::default();
        for &(start, end, rects) in events {
            queue.push(event(start, end, rects));
        }
        queue
    }

    fn visible_start(queue: &Queue, t_ms: i64) -> Option<i64> {
        queue.visible(t_ms).map(|event| event.start_ms)
    }

    #[test]
    fn abi_structs_have_the_contract_layout() {
        use std::mem::offset_of;
        assert_eq!(size_of::<BaleSubBitmapRect>(), 40);
        assert_eq!(offset_of!(BaleSubBitmapRect, linesize), 16);
        assert_eq!(offset_of!(BaleSubBitmapRect, indices), 24);
        assert_eq!(offset_of!(BaleSubBitmapRect, palette), 32);
        assert_eq!(size_of::<BaleSubBitmapEvent>(), 48);
        assert_eq!(offset_of!(BaleSubBitmapEvent, canvas_w), 16);
        assert_eq!(offset_of!(BaleSubBitmapEvent, rects), 32);
        assert_eq!(offset_of!(BaleSubBitmapEvent, count), 40);
        assert_eq!(offset_of!(BaleSubBitmapEvent, reserved), 44);
    }

    #[test]
    fn palette_is_premultiplied_rgba() {
        assert_eq!(premultiplied_rgba(0xFF11_2233), [0x11, 0x22, 0x33, 0xFF]);
        assert_eq!(premultiplied_rgba(0x00FF_FFFF), [0, 0, 0, 0]);
        // 0x80 = 128: 255*128/255 = 128, 100*128/255 = 50, 1*128/255 = 0.
        assert_eq!(premultiplied_rgba(0x80FF_6401), [128, 50, 0, 128]);
    }

    #[test]
    fn indices_past_the_palette_are_transparent() {
        let rect = Rect::new(0, 0, 3, 1, vec![1, 0, 2], &[0xFF00_00FF, 0x80FF_0000]);
        assert_eq!(rect.rgba(), [128, 0, 0, 128, 0, 0, 255, 255, 0, 0, 0, 0]);
    }

    #[test]
    fn an_event_shows_from_its_start_to_its_end() {
        let queue = queue(&[(1000, 3000, 1)]);
        assert_eq!(visible_start(&queue, 999), None);
        assert_eq!(visible_start(&queue, 1000), Some(1000));
        assert_eq!(visible_start(&queue, 2999), Some(1000));
        assert_eq!(visible_start(&queue, 3000), None);
    }

    #[test]
    fn the_next_event_ends_the_one_before() {
        // The first is open-ended, the second would run past the third.
        let queue = queue(&[(1000, -1, 1), (2000, 9000, 1), (2500, 2600, 1)]);
        assert_eq!(visible_start(&queue, 1999), Some(1000));
        assert_eq!(visible_start(&queue, 2000), Some(2000));
        assert_eq!(visible_start(&queue, 2500), Some(2500));
        // The second does not come back after the third ends.
        assert_eq!(visible_start(&queue, 2600), None);
        assert_eq!(visible_start(&queue, 8000), None);
    }

    #[test]
    fn a_clear_only_ends_the_previous_event() {
        let queue = queue(&[(1000, 0, 2), (4000, 0, 0)]);
        assert_eq!(visible_start(&queue, 3999), Some(1000));
        assert_eq!(visible_start(&queue, 4000), None);
        assert_eq!(visible_start(&queue, 99_000), None);
        // An end at or before the start is open-ended too.
        assert_eq!(event(1000, 1000, 1).end_ms, None);
    }

    #[test]
    fn pushes_sort_and_replace_by_start() {
        let mut queue = queue(&[(3000, 4000, 1), (1000, 2000, 1), (2000, 2500, 1)]);
        let starts = |queue: &Queue| -> Vec<i64> {
            queue.events.iter().map(|event| event.start_ms).collect()
        };
        assert_eq!(starts(&queue), [1000, 2000, 3000]);
        queue.push(event(2000, 2800, 3));
        assert_eq!(starts(&queue), [1000, 2000, 3000]);
        assert_eq!(queue.visible(2700).map(|event| event.rects.len()), Some(3));
    }

    #[test]
    fn prune_drops_what_has_ended() {
        let mut queue = queue(&[
            (1000, 2000, 1),
            (3000, -1, 1),
            (5000, -1, 0),
            (7000, 8000, 1),
        ]);
        queue.prune(1999);
        assert_eq!(queue.len(), 4);
        queue.prune(2000);
        assert_eq!(queue.len(), 3);
        // The open-ended event ends at the clear, the clear at the event after it.
        queue.prune(6999);
        assert_eq!(queue.len(), 2);
        queue.prune(7500);
        assert_eq!(queue.len(), 1);
        assert_eq!(visible_start(&queue, 7500), Some(7000));
        queue.prune(8000);
        assert!(queue.is_empty());
        queue.prune(9000);
        // A last open-ended event is never pruned.
        queue.push(event(100, -1, 1));
        queue.prune(i64::MAX);
        assert_eq!(queue.len(), 1);
    }

    #[test]
    fn the_queue_is_capped_and_drops_the_oldest() {
        let mut queue = Queue::default();
        for at in 0..MAX_EVENTS as i64 + 10 {
            queue.push(event(at * 100, -1, 1));
        }
        assert_eq!(queue.len(), MAX_EVENTS);
        assert_eq!(queue.events[0].start_ms, 1000);
        queue.flush();
        assert!(queue.is_empty());
        assert_eq!(queue.next_boundary(0), None);
    }

    #[test]
    fn next_boundary_is_the_next_start_or_effective_end() {
        let queue = queue(&[(1000, 2000, 1), (3000, 9000, 1), (4000, -1, 1)]);
        assert_eq!(queue.next_boundary(0), Some(1000));
        assert_eq!(queue.next_boundary(1000), Some(2000));
        assert_eq!(queue.next_boundary(2000), Some(3000));
        // The second ends when the third starts, not at its own end.
        assert_eq!(queue.next_boundary(3000), Some(4000));
        assert_eq!(queue.next_boundary(4000), None);
        assert_eq!(Queue::default().next_boundary(0), None);
        // A gap before the next start: the end comes first.
        let queue = queue_with_gap();
        assert_eq!(queue.next_boundary(1500), Some(2000));
        assert_eq!(queue.next_boundary(2500), Some(5000));
    }

    fn queue_with_gap() -> Queue {
        queue(&[(1000, 2000, 1), (5000, 6000, 1)])
    }

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-4
    }

    #[test]
    fn a_matching_canvas_fills_the_video_rectangle() {
        // DVD 720x480 shown 4:3 in a 1920x1080 frame.
        let placement = place(
            Some((720.0, 480.0)),
            Some((720.0, 480.0)),
            (1.0, 1.0),
            [240.0, 0.0, 1440.0, 1080.0],
        );
        assert!(close(placement.scale_x, 2.0) && close(placement.scale_y, 2.25));
        assert_eq!((placement.offset_x, placement.offset_y), (240.0, 0.0));
        assert_eq!(
            placement.rect(100, 400, 520, 40),
            [440.0, 900.0, 1040.0, 90.0]
        );
    }

    #[test]
    fn a_cropped_encode_keeps_the_pixel_scale_and_centres_the_canvas() {
        // PGS 1920x1080 over a 1920x1040 encode shown at y = 20.
        let placement = place(
            Some((1920.0, 1080.0)),
            Some((1920.0, 1040.0)),
            (1.0, 1.0),
            [0.0, 20.0, 1920.0, 1040.0],
        );
        assert_eq!((placement.scale_x, placement.scale_y), (1.0, 1.0));
        assert_eq!((placement.offset_x, placement.offset_y), (0.0, 0.0));
        assert_eq!(
            placement.rect(600, 540, 720, 80),
            [600.0, 540.0, 720.0, 80.0]
        );
        // The same, scaled to a 4K frame; and cropped on the other axis.
        let placement = place(
            Some((1920.0, 1080.0)),
            Some((1920.0, 800.0)),
            (1.0, 1.0),
            [0.0, 280.0, 3840.0, 1600.0],
        );
        assert_eq!((placement.scale_x, placement.scale_y), (2.0, 2.0));
        assert_eq!(placement.offset_y, 0.0);
        let placement = place(
            Some((1920.0, 1080.0)),
            Some((1440.0, 1080.0)),
            (1.0, 1.0),
            [240.0, 0.0, 1440.0, 1080.0],
        );
        assert_eq!((placement.scale_x, placement.scale_y), (1.0, 1.0));
        assert_eq!(placement.offset_x, 0.0);
    }

    #[test]
    fn a_canvas_of_another_size_is_stretched() {
        let placement = place(
            Some((1920.0, 1080.0)),
            Some((1280.0, 720.0)),
            (1.0, 1.0),
            [0.0, 0.0, 3840.0, 2160.0],
        );
        assert_eq!((placement.scale_x, placement.scale_y), (2.0, 2.0));
        assert_eq!((placement.offset_x, placement.offset_y), (0.0, 0.0));
    }

    #[test]
    fn unknown_sizes_fall_back_to_each_other_then_to_the_bounds() {
        let rect = [0.0, 140.0, 1920.0, 800.0];
        let known = place(
            Some((1920.0, 800.0)),
            Some((1920.0, 800.0)),
            (1.0, 1.0),
            rect,
        );
        assert_eq!(place(None, Some((1920.0, 800.0)), (1.0, 1.0), rect), known);
        assert_eq!(place(Some((1920.0, 800.0)), None, (1.0, 1.0), rect), known);
        assert_eq!(place(None, None, (1920.0, 800.0), rect), known);
        assert_eq!(known.offset_y, 140.0);

        // An event without sizes uses its rectangles' bounding box from the origin.
        let mut event = event(0, 0, 0);
        event.rects = vec![
            Rect::new(100, 50, 200, 100, vec![0; 20_000], &[]),
            Rect::new(0, 300, 960, 240, vec![0; 230_400], &[]),
        ];
        let placement = event.placement(&Layout::full(1920, 1080));
        assert_eq!((placement.scale_x, placement.scale_y), (2.0, 2.0));
        // A degenerate layout must not divide by zero.
        let empty = place(None, None, (0.0, 0.0), [0.0; 4]);
        assert!(empty.scale_x.is_finite() && empty.scale_y.is_finite());
    }

    fn raw_rect(
        w: i32,
        h: i32,
        linesize: i32,
        indices: &[u8],
        palette: &[u32],
    ) -> BaleSubBitmapRect {
        BaleSubBitmapRect {
            x: 10,
            y: 20,
            w,
            h,
            linesize,
            nb_colors: palette.len() as i32,
            indices: indices.as_ptr(),
            palette: palette.as_ptr(),
        }
    }

    fn raw_event(rects: &[BaleSubBitmapRect]) -> BaleSubBitmapEvent {
        BaleSubBitmapEvent {
            start_ms: 1000,
            end_ms: 3000,
            canvas_w: 1920,
            canvas_h: 1080,
            video_w: 0,
            video_h: -1,
            rects: rects.as_ptr(),
            count: rects.len() as i32,
            reserved: 0,
        }
    }

    #[test]
    fn copying_packs_rows_and_keeps_the_fields() {
        // Two rows of two indices, four bytes apart; the last row is only `w` bytes long.
        let indices = [0u8, 1, 9, 9, 1, 0];
        let palette = [0xFF00_00FFu32, 0xFFFF_0000];
        let rects = [
            raw_rect(2, 2, 4, &indices, &palette),
            raw_rect(0, 5, 0, &[], &[]),
        ];
        // SAFETY: the pointers cover the declared sizes.
        let event = unsafe { Event::copy_from(&raw_event(&rects)) }.expect("valid event");
        assert_eq!((event.start_ms, event.end_ms), (1000, Some(3000)));
        assert_eq!((event.canvas, event.video), (Some((1920, 1080)), None));
        assert_eq!(event.rects.len(), 1, "the empty rectangle is left out");
        let rect = &event.rects[0];
        assert_eq!((rect.x, rect.y, rect.w, rect.h), (10, 20, 2, 2));
        assert_eq!(**rect.indices, [0, 1, 1, 0]);
        assert_eq!(&rect.rgba()[..8], [0, 0, 255, 255, 255, 0, 0, 255]);

        let clear = BaleSubBitmapEvent {
            rects: std::ptr::null(),
            count: 0,
            end_ms: -1,
            ..raw_event(&[])
        };
        // SAFETY: a clear has no rectangles to read.
        let clear = unsafe { Event::copy_from(&clear) }.expect("valid clear");
        assert!(clear.rects.is_empty() && clear.end_ms.is_none());
    }

    #[test]
    fn copying_rejects_what_the_abi_forbids() {
        let indices = [0u8; 16];
        let palette = [0u32; 4];
        let copy = |event: &BaleSubBitmapEvent| {
            // SAFETY: every rejected case fails before its buffers are read.
            unsafe { Event::copy_from(event) }.err()
        };
        // SAFETY: null is checked.
        assert_eq!(
            unsafe { Event::copy_from(std::ptr::null()) }.err(),
            Some(E_POINTER)
        );
        let good = [raw_rect(4, 4, 4, &indices, &palette)];
        assert_eq!(copy(&raw_event(&good)), None);
        for (change, expected) in [
            (
                BaleSubBitmapEvent {
                    reserved: 1,
                    ..raw_event(&good)
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapEvent {
                    count: -1,
                    ..raw_event(&good)
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapEvent {
                    count: MAX_RECTS + 1,
                    ..raw_event(&good)
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapEvent {
                    rects: std::ptr::null(),
                    ..raw_event(&good)
                },
                E_POINTER,
            ),
        ] {
            assert_eq!(copy(&change), Some(expected));
        }
        let rect = good[0];
        for (change, expected) in [
            (
                BaleSubBitmapRect {
                    linesize: 3,
                    ..rect
                },
                E_INVALIDARG,
            ),
            (BaleSubBitmapRect { w: -1, ..rect }, E_INVALIDARG),
            (BaleSubBitmapRect { h: -4, ..rect }, E_INVALIDARG),
            (
                BaleSubBitmapRect {
                    w: 4097,
                    h: 4096,
                    linesize: 4097,
                    ..rect
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapRect {
                    w: 16385,
                    h: 1,
                    linesize: 16385,
                    ..rect
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapRect {
                    nb_colors: 257,
                    ..rect
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapRect {
                    nb_colors: -1,
                    ..rect
                },
                E_INVALIDARG,
            ),
            (
                BaleSubBitmapRect {
                    indices: std::ptr::null(),
                    ..rect
                },
                E_POINTER,
            ),
            (
                BaleSubBitmapRect {
                    palette: std::ptr::null(),
                    ..rect
                },
                E_POINTER,
            ),
        ] {
            assert_eq!(copy(&raw_event(&[change])), Some(expected), "{change:?}");
        }
        // No palette at all is allowed: every index is transparent.
        let bare = BaleSubBitmapRect {
            nb_colors: 0,
            palette: std::ptr::null(),
            ..rect
        };
        assert_eq!(copy(&raw_event(&[bare])), None);
    }

    #[derive(Default)]
    struct Fake {
        token: Mutex<u64>,
        queue: Mutex<Queue>,
    }

    impl Sink for Fake {
        fn push(&self, token: u64, event: Event) -> i32 {
            if *self.token.lock().unwrap() != token {
                return E_NOT_FOUND;
            }
            self.queue.lock().unwrap().push(event);
            S_OK
        }

        fn replace(&self, token: u64, events: Vec<Event>, now: i64, end: i64) -> i32 {
            if *self.token.lock().unwrap() != token {
                return E_NOT_FOUND;
            }
            self.queue.lock().unwrap().replace_window(events, now, end);
            S_OK
        }

        fn flush(&self, token: u64) -> i32 {
            if *self.token.lock().unwrap() != token {
                return E_NOT_FOUND;
            }
            self.queue.lock().unwrap().flush();
            S_OK
        }

        fn unbound(&self, token: u64) {
            let mut bound = self.token.lock().unwrap();
            if *bound == token {
                *bound = 0;
            }
        }
    }

    fn fake(token: u64) -> (Arc<Fake>, Arc<dyn Sink>) {
        let fake = Arc::new(Fake::default());
        *fake.token.lock().unwrap() = token;
        let sink: Arc<dyn Sink> = fake.clone();
        bind(token, &sink);
        (fake, sink)
    }

    #[test]
    fn pushes_reach_only_the_bound_sink() {
        // Tokens are process-wide; these are used by this test alone.
        const TOKEN: u64 = 0xB17_0001;
        let indices = [0u8; 4];
        let palette = [0xFFFF_FFFFu32];
        let rects = [raw_rect(2, 2, 2, &indices, &palette)];
        let event = raw_event(&rects);
        // SAFETY (all pushes below): the event's pointers cover the declared sizes.
        assert_eq!(unsafe { push(TOKEN, &event) }, E_NOT_FOUND);
        assert_eq!(flush(TOKEN), E_NOT_FOUND);

        let (first, first_sink) = fake(TOKEN);
        assert_eq!(unsafe { push(TOKEN, &event) }, S_OK);
        assert_eq!(unsafe { push(TOKEN, std::ptr::null()) }, E_POINTER);
        assert_eq!(unsafe { push(TOKEN + 1, &event) }, E_NOT_FOUND);
        assert_eq!(first.queue.lock().unwrap().len(), 1);
        assert_eq!(flush(TOKEN), S_OK);
        assert!(first.queue.lock().unwrap().is_empty());

        // Rebinding the token to another sink replaces the first and tells it.
        let (second, second_sink) = fake(TOKEN);
        assert_eq!(*first.token.lock().unwrap(), 0);
        assert_eq!(unsafe { push(TOKEN, &event) }, S_OK);
        assert!(first.queue.lock().unwrap().is_empty());
        assert_eq!(second.queue.lock().unwrap().len(), 1);

        // Unbinding the old sink leaves the new binding alone; unbinding the new one ends it.
        unbind(&first_sink);
        assert_eq!(unsafe { push(TOKEN, &event) }, S_OK);
        unbind(&second_sink);
        assert_eq!(unsafe { push(TOKEN, &event) }, E_NOT_FOUND);
    }

    #[test]
    fn a_dropped_sink_is_unbound() {
        const TOKEN: u64 = 0xB17_0002;
        let (fake, sink) = fake(TOKEN);
        assert_eq!(flush(TOKEN), S_OK);
        drop((fake, sink));
        assert_eq!(flush(TOKEN), E_NOT_FOUND);
    }
}
