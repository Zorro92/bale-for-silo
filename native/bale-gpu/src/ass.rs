//! libass subtitle rendering, linked statically from `scripts/build-libass-native.ps1` (libass with
//! FreeType, FriBidi and HarfBuzz, built for the UWP app partition so libass uses its WinRT
//! DirectWrite font provider). [`Session`] is shared by the subtitle overlay (`subs::overlay`) and
//! the debug probes, which check font discovery on the console and time rendering. Output never
//! contains paths, URLs or system error text.

use std::cell::Cell;
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::marker::PhantomData;
use std::ptr;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::subs::schedule::{Event, Schedule, motion};

#[repr(C)]
struct Library {
    _opaque: [u8; 0],
}

#[repr(C)]
struct Renderer {
    _opaque: [u8; 0],
}

/// The public head of `ASS_Track` (ass_types.h, libass 0.17.5); the rest is never read here.
#[repr(C)]
struct RawTrack {
    n_styles: c_int,
    _max_styles: c_int,
    n_events: c_int,
    _max_events: c_int,
    _styles: *mut c_void,
    events: *mut RawEvent,
    _style_format: *mut c_char,
    event_format: *mut c_char,
}

/// `ASS_Event` (ass_types.h, libass 0.17.5).
#[repr(C)]
struct RawEvent {
    /// `long long`, ms.
    start: i64,
    duration: i64,
    read_order: c_int,
    _layer: c_int,
    _style: c_int,
    _name: *mut c_char,
    _margin_l: c_int,
    _margin_r: c_int,
    _margin_v: c_int,
    effect: *mut c_char,
    text: *mut c_char,
    _render_priv: *mut c_void,
}

// The x64 layout the statically linked libass was built with.
#[cfg(target_pointer_width = "64")]
const _: () = {
    assert!(std::mem::offset_of!(RawTrack, events) == 24);
    assert!(std::mem::offset_of!(RawTrack, event_format) == 40);
    assert!(std::mem::offset_of!(RawEvent, read_order) == 16);
    assert!(std::mem::offset_of!(RawEvent, effect) == 56);
    assert!(std::mem::offset_of!(RawEvent, text) == 64);
    assert!(size_of::<RawEvent>() == 80);
};

/// `ASS_Image` (ass.h). Bitmaps are 8-bit coverage; `color` is RGBA where A is transparency.
#[repr(C)]
pub(crate) struct Image {
    pub(crate) w: c_int,
    pub(crate) h: c_int,
    pub(crate) stride: c_int,
    pub(crate) bitmap: *const u8,
    pub(crate) color: u32,
    pub(crate) dst_x: c_int,
    pub(crate) dst_y: c_int,
    next: *const Image,
    _kind: c_int,
}

impl Image {
    /// Row `y` (`0..h`) of the coverage bitmap, `w` bytes.
    pub(crate) fn row(&self, y: c_int) -> &[u8] {
        debug_assert!(self.w > 0 && (0..self.h).contains(&y));
        // SAFETY: libass guarantees `stride * (h - 1) + w` bytes, so every row has `w` bytes.
        unsafe {
            std::slice::from_raw_parts(self.bitmap.add((y * self.stride) as usize), self.w as usize)
        }
    }
}

type MessageCallback =
    unsafe extern "C" fn(level: c_int, format: *const c_char, args: *mut c_void, data: *mut c_void);

const FONTPROVIDER_AUTODETECT: c_int = 1;
/// libass `MSGL_INFO`: includes the `fontselect:` lines; `MSGL_V` and below are too chatty.
const MSGL_INFO: c_int = 4;
/// UCRT `_CRT_INTERNAL_PRINTF_STANDARD_SNPRINTF_BEHAVIOR`: truncate, always terminate.
const STANDARD_SNPRINTF: u64 = 1 << 1;
const MAX_LOG: usize = 512;

unsafe extern "C" {
    #[cfg(feature = "debug-probes")]
    fn ass_library_version() -> c_int;
    fn ass_library_init() -> *mut Library;
    fn ass_library_done(library: *mut Library);
    fn ass_set_message_cb(
        library: *mut Library,
        callback: Option<MessageCallback>,
        data: *mut c_void,
    );
    fn ass_add_font(library: *mut Library, name: *const c_char, data: *const c_char, size: c_int);
    fn ass_renderer_init(library: *mut Library) -> *mut Renderer;
    fn ass_renderer_done(renderer: *mut Renderer);
    fn ass_set_frame_size(renderer: *mut Renderer, width: c_int, height: c_int);
    fn ass_set_storage_size(renderer: *mut Renderer, width: c_int, height: c_int);
    fn ass_set_margins(
        renderer: *mut Renderer,
        top: c_int,
        bottom: c_int,
        left: c_int,
        right: c_int,
    );
    fn ass_set_use_margins(renderer: *mut Renderer, use_margins: c_int);
    fn ass_set_fonts(
        renderer: *mut Renderer,
        default_font: *const c_char,
        default_family: *const c_char,
        provider: c_int,
        config: *const c_char,
        update: c_int,
    );
    fn ass_read_memory(
        library: *mut Library,
        buffer: *mut c_char,
        size: usize,
        codepage: *const c_char,
    ) -> *mut RawTrack;
    fn ass_free_track(track: *mut RawTrack);
    fn ass_new_track(library: *mut Library) -> *mut RawTrack;
    fn ass_process_codec_private(track: *mut RawTrack, data: *const c_char, size: c_int);
    fn ass_process_chunk(
        track: *mut RawTrack,
        data: *const c_char,
        size: c_int,
        timecode: i64,
        duration: i64,
    );
    fn ass_set_check_readorder(track: *mut RawTrack, check_readorder: c_int);
    fn ass_prune_events(track: *mut RawTrack, deadline: i64);
    fn ass_flush_events(track: *mut RawTrack);
    fn ass_render_frame(
        renderer: *mut Renderer,
        track: *mut RawTrack,
        now_ms: i64,
        detect_change: *mut c_int,
    ) -> *const Image;
    /// The UCRT formatter behind the header-inline `vsnprintf`; on x64 a `va_list` is a pointer.
    fn __stdio_common_vsprintf(
        options: u64,
        buffer: *mut c_char,
        count: usize,
        format: *const c_char,
        locale: *mut c_void,
        args: *mut c_void,
    ) -> c_int;
}

// Collected in every build (at most MAX_LOG per session); only the debug probes read them back.
#[cfg_attr(not(feature = "debug-probes"), allow(dead_code))]
struct Message {
    level: c_int,
    text: String,
}

unsafe extern "C" fn on_message(
    level: c_int,
    format: *const c_char,
    args: *mut c_void,
    data: *mut c_void,
) {
    if level > MSGL_INFO || format.is_null() || data.is_null() {
        return;
    }
    let mut buffer = [0u8; 512];
    // SAFETY: libass passes a printf format with its matching va_list; `count` bounds the output.
    let written = unsafe {
        __stdio_common_vsprintf(
            STANDARD_SNPRINTF,
            buffer.as_mut_ptr().cast(),
            buffer.len(),
            format,
            ptr::null_mut(),
            args,
        )
    };
    let Ok(written) = usize::try_from(written) else {
        return;
    };
    let text = String::from_utf8_lossy(&buffer[..written.min(buffer.len() - 1)]).into_owned();
    // SAFETY: `data` is the owning session's boxed log, which outlives its library.
    let log = unsafe { &*data.cast::<Mutex<Vec<Message>>>() };
    if let Ok(mut log) = log.lock()
        && log.len() < MAX_LOG
    {
        log.push(Message { level, text });
    }
}

/// A libass library and renderer with captured log messages. Not thread-safe (raw pointers keep it
/// `!Send`/`!Sync`); everything happens on the thread that created it.
pub(crate) struct Session {
    library: *mut Library,
    renderer: *mut Renderer,
    log: Box<Mutex<Vec<Message>>>,
    /// False until `ass_set_fonts` has run with every added font.
    fonts_ready: Cell<bool>,
}

impl Session {
    /// `embedded` fonts are MKV attachments / Silo's font bundle; libass copies the bytes. The font
    /// provider is not started here: [`Session::ensure_fonts`] does that (it can take a while).
    pub(crate) fn new(
        width: i32,
        height: i32,
        embedded: &[(&str, &[u8])],
    ) -> Result<Self, &'static str> {
        let log = Box::new(Mutex::new(Vec::new()));
        // SAFETY: plain constructor.
        let library = unsafe { ass_library_init() };
        if library.is_null() {
            return Err("ass_library_init failed");
        }
        let mut session = Session {
            library,
            renderer: ptr::null_mut(),
            log,
            fonts_ready: Cell::new(false),
        };
        let data = ptr::from_ref(&*session.log).cast_mut().cast();
        // SAFETY: the boxed log stays at this address until after `ass_library_done` (see Drop).
        unsafe { ass_set_message_cb(library, Some(on_message), data) };
        for (name, bytes) in embedded {
            session.add_font(name, bytes);
        }
        // SAFETY: valid library.
        session.renderer = unsafe { ass_renderer_init(library) };
        if session.renderer.is_null() {
            return Err("ass_renderer_init failed");
        }
        // SAFETY: valid renderer.
        unsafe { ass_set_frame_size(session.renderer, width, height) };
        Ok(session)
    }

    /// Adds an embedded font (libass copies the bytes). It is picked up by the next
    /// [`Session::ensure_fonts`]. A name libass cannot take (inner NUL) is replaced by an empty one.
    pub(crate) fn add_font(&self, name: &str, bytes: &[u8]) {
        let Ok(size) = c_int::try_from(bytes.len()) else {
            return;
        };
        if size == 0 {
            return;
        }
        let name = CString::new(name).unwrap_or_default();
        // SAFETY: valid C string and `size` readable bytes; libass copies both.
        unsafe { ass_add_font(self.library, name.as_ptr(), bytes.as_ptr().cast(), size) };
        self.fonts_ready.set(false);
    }

    /// Starts (or restarts, after [`Session::add_font`]) the font provider; a no-op when it is
    /// current. Returns the time `ass_set_fonts` took.
    pub(crate) fn ensure_fonts(&self) -> Duration {
        if self.fonts_ready.replace(true) {
            return Duration::ZERO;
        }
        let start = Instant::now();
        // SAFETY: valid renderer; null default font and config are allowed.
        unsafe {
            ass_set_fonts(
                self.renderer,
                ptr::null(),
                c"Arial".as_ptr(),
                FONTPROVIDER_AUTODETECT,
                ptr::null(),
                1,
            );
        }
        start.elapsed()
    }

    /// Frame size in pixels, the video rectangle's distances to the frame edges
    /// (`[top, bottom, left, right]`) and the video size. Margins are not used for dialogue
    /// (`ass_set_use_margins(0)`), so positioned text stays inside the video area, as in mpv.
    pub(crate) fn set_layout(
        &self,
        width: i32,
        height: i32,
        margins: [i32; 4],
        storage: (i32, i32),
    ) {
        let [top, bottom, left, right] = margins;
        // SAFETY: valid renderer; plain setters.
        unsafe {
            ass_set_frame_size(self.renderer, width, height);
            ass_set_margins(self.renderer, top, bottom, left, right);
            ass_set_use_margins(self.renderer, 0);
            ass_set_storage_size(self.renderer, storage.0, storage.1);
        }
    }

    #[cfg(feature = "debug-probes")]
    fn drain_log(&self) -> Vec<Message> {
        self.log
            .lock()
            .map(|mut log| std::mem::take(&mut *log))
            .unwrap_or_default()
    }

    /// Parses UTF-8 ASS/SSA script text; `None` when libass does not recognise it.
    pub(crate) fn track(&self, script: &[u8]) -> Option<Track<'_>> {
        let mut bytes = script.to_vec();
        // SAFETY: valid library and a writable buffer of `len` bytes; libass parses a copy.
        let raw = unsafe {
            ass_read_memory(
                self.library,
                bytes.as_mut_ptr().cast(),
                bytes.len(),
                ptr::null(),
            )
        };
        (!raw.is_null()).then_some(Track {
            raw,
            _session: PhantomData,
        })
    }

    /// An empty track with `header`'s script info and styles, for events added as they come (live
    /// captions, `subs::cc`). `None` when libass finds no style in it (every track has libass's own
    /// default style first).
    pub(crate) fn live_track(&self, header: &[u8]) -> Option<Track<'_>> {
        let size = c_int::try_from(header.len()).ok()?;
        // SAFETY: valid library.
        let raw = unsafe { ass_new_track(self.library) };
        if raw.is_null() {
            return None;
        }
        // Freed by its Drop on every path from here.
        let track = Track {
            raw,
            _session: PhantomData,
        };
        // SAFETY: a live track and `size` readable bytes; libass parses a copy.
        unsafe {
            ass_process_codec_private(raw, header.as_ptr().cast(), size);
            // Bale's event ids are unique: no duplicate check, so no read order bitmap either.
            ass_set_check_readorder(raw, 0);
        }
        // SAFETY: a live `ASS_Track`, whose public head `RawTrack` mirrors.
        let parsed = unsafe { (*raw).n_styles > 1 && !(*raw).event_format.is_null() };
        parsed.then_some(track)
    }

    /// Renders `track` at `now_ms` and passes every non-empty image to `each`, in drawing order;
    /// with `always` false only when libass reports a change from the previous render. The image
    /// list is only valid until the next render, so it is consumed here.
    pub(crate) fn render(
        &self,
        track: &Track<'_>,
        now_ms: i64,
        always: bool,
        mut each: impl FnMut(&Image),
    ) -> RenderStats {
        debug_assert!(self.fonts_ready.get(), "ensure_fonts before rendering");
        let mut changed: c_int = 0;
        let start = Instant::now();
        // SAFETY: renderer and track belong to this session.
        let mut image = unsafe { ass_render_frame(self.renderer, track.raw, now_ms, &mut changed) };
        let elapsed = start.elapsed();
        let mut stats = RenderStats {
            elapsed,
            changed,
            ..RenderStats::default()
        };
        if changed == 0 && !always {
            return stats;
        }
        while !image.is_null() {
            // SAFETY: libass returns a valid list until the next render call.
            let current = unsafe { &*image };
            image = current.next;
            if current.w <= 0 || current.h <= 0 {
                continue;
            }
            stats.images += 1;
            let (x0, y0) = (current.dst_x, current.dst_y);
            let (x1, y1) = (x0 + current.w, y0 + current.h);
            stats.bounds = Some(match stats.bounds {
                Some((a, b, c, d)) => (a.min(x0), b.min(y0), c.max(x1), d.max(y1)),
                None => (x0, y0, x1, y1),
            });
            each(current);
        }
        stats
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        // SAFETY: owned pointers, released once; tracks borrow the session so they are gone already.
        unsafe {
            if !self.renderer.is_null() {
                ass_renderer_done(self.renderer);
            }
            ass_library_done(self.library);
        }
    }
}

pub(crate) struct Track<'a> {
    raw: *mut RawTrack,
    _session: PhantomData<&'a Session>,
}

impl Track<'_> {
    /// Adds one event to a live track: ReadOrder `id` on `layer` (libass draws higher layers over
    /// lower ones), `text` the event's text field (overrides included, no line breaks).
    pub(crate) fn add_event(
        &mut self,
        id: i32,
        layer: i32,
        start_ms: i64,
        end_ms: i64,
        text: &str,
    ) {
        // A Matroska chunk: ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text.
        let chunk = format!("{id},{layer},Default,,0,0,0,,{text}");
        let Ok(size) = c_int::try_from(chunk.len()) else {
            return;
        };
        let duration = end_ms.saturating_sub(start_ms).max(0);
        // SAFETY: a live track and `size` readable bytes; libass copies the event.
        unsafe { ass_process_chunk(self.raw, chunk.as_ptr().cast(), size, start_ms, duration) };
    }

    /// Moves the end of the live event with ReadOrder `id` (the newest if several); no event, no
    /// change. libass reads every event's start and duration again each render.
    pub(crate) fn set_event_end(&mut self, id: i32, end_ms: i64) {
        // SAFETY: a live `ASS_Track`, whose public head `RawTrack` mirrors.
        let (events, count) = unsafe { ((*self.raw).events, (*self.raw).n_events) };
        let count = usize::try_from(count).unwrap_or(0);
        if events.is_null() || count == 0 {
            return;
        }
        // SAFETY: libass keeps `n_events` initialised events at `events`, and only this thread
        // (the track's owner) touches them.
        let events = unsafe { std::slice::from_raw_parts_mut(events, count) };
        if let Some(event) = events.iter_mut().rev().find(|event| event.read_order == id) {
            event.duration = end_ms.saturating_sub(event.start).max(0);
        }
    }

    /// Drops every event.
    pub(crate) fn flush_events(&mut self) {
        // SAFETY: a live track.
        unsafe { ass_flush_events(self.raw) };
    }

    /// Drops the events that ended before `deadline_ms`.
    pub(crate) fn prune_events(&mut self, deadline_ms: i64) {
        // SAFETY: a live track.
        unsafe { ass_prune_events(self.raw, deadline_ms) };
    }

    /// Number of events (dialogue lines) in the track.
    pub(crate) fn events(&self) -> u32 {
        // SAFETY: a live `ASS_Track`, whose public head `RawTrack` mirrors.
        u32::try_from(unsafe { (*self.raw).n_events }).unwrap_or(0)
    }

    /// When the track's picture changes: every event's span and whether it is animated.
    pub(crate) fn schedule(&self) -> Schedule {
        // SAFETY: a live `ASS_Track`, whose public head `RawTrack` mirrors.
        let (events, count) = unsafe { ((*self.raw).events, (*self.raw).n_events) };
        let count = usize::try_from(count).unwrap_or(0);
        if events.is_null() || count == 0 {
            return Schedule::default();
        }
        // SAFETY: libass keeps `n_events` initialised events at `events`; nothing changes the
        // track while it is borrowed here.
        let events = unsafe { std::slice::from_raw_parts(events, count) };
        let bytes = |string: *mut c_char| {
            if string.is_null() {
                &[][..]
            } else {
                // SAFETY: event strings are NUL-terminated and live as long as the track.
                unsafe { CStr::from_ptr(string) }.to_bytes()
            }
        };
        Schedule::new(events.iter().map(|event| Event {
            start_ms: event.start,
            duration_ms: event.duration,
            motion: motion(bytes(event.text), bytes(event.effect)),
        }))
    }
}

impl Drop for Track<'_> {
    fn drop(&mut self) {
        // SAFETY: owned track, freed before its library (it borrows the session).
        unsafe { ass_free_track(self.raw) };
    }
}

#[derive(Default)]
pub(crate) struct RenderStats {
    /// Time spent in `ass_render_frame` (read by the debug probes only).
    #[cfg_attr(not(feature = "debug-probes"), allow(dead_code))]
    pub(crate) elapsed: Duration,
    /// libass's `detect_change`: 0 when the images equal the previous render's.
    pub(crate) changed: c_int,
    /// Non-empty images passed to the callback.
    pub(crate) images: u32,
    bounds: Option<(i32, i32, i32, i32)>,
}

/// The debug probes ([`probe`], [`probe_png`]): debug-server builds only.
#[cfg(feature = "debug-probes")]
mod probe;
#[cfg(feature = "debug-probes")]
pub use probe::{probe, probe_png};
