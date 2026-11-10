//! libass subtitle rendering, linked statically from `scripts/build-libass-native.ps1` (libass with
//! FreeType, FriBidi and HarfBuzz, built for the UWP app partition so libass uses its WinRT
//! DirectWrite font provider). For now only the debug probes use it: they check font discovery on the
//! console and time rendering. Output never contains paths, URLs or system error text.

use std::ffi::{CString, c_char, c_int, c_void};
use std::fmt::Write as _;
use std::marker::PhantomData;
use std::ptr;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::png;

#[repr(C)]
struct Library {
    _opaque: [u8; 0],
}

#[repr(C)]
struct Renderer {
    _opaque: [u8; 0],
}

#[repr(C)]
struct RawTrack {
    _opaque: [u8; 0],
}

/// `ASS_Image` (ass.h). Bitmaps are 8-bit coverage; `color` is RGBA where A is transparency.
#[repr(C)]
struct Image {
    w: c_int,
    h: c_int,
    stride: c_int,
    bitmap: *const u8,
    color: u32,
    dst_x: c_int,
    dst_y: c_int,
    next: *const Image,
    _kind: c_int,
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
/// `!Send`/`!Sync`); everything happens on the calling thread.
struct Session {
    library: *mut Library,
    renderer: *mut Renderer,
    log: Box<Mutex<Vec<Message>>>,
    width: i32,
    height: i32,
}

impl Session {
    /// `embedded` fonts stand in for MKV attachments / Silo's font bundle; libass copies the bytes.
    /// Returns the session and the time `ass_set_fonts` took (font provider start-up).
    fn new(
        width: i32,
        height: i32,
        embedded: &[(&str, &[u8])],
    ) -> Result<(Self, Duration), &'static str> {
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
            width,
            height,
        };
        let data = ptr::from_ref(&*session.log).cast_mut().cast();
        // SAFETY: the boxed log stays at this address until after `ass_library_done` (see Drop).
        unsafe { ass_set_message_cb(library, Some(on_message), data) };
        for (name, bytes) in embedded {
            let (Ok(name), Ok(size)) = (CString::new(*name), c_int::try_from(bytes.len())) else {
                continue;
            };
            // SAFETY: valid C string and `size` readable bytes; libass copies both.
            unsafe { ass_add_font(library, name.as_ptr(), bytes.as_ptr().cast(), size) };
        }
        // SAFETY: valid library.
        session.renderer = unsafe { ass_renderer_init(library) };
        if session.renderer.is_null() {
            return Err("ass_renderer_init failed");
        }
        let start = Instant::now();
        // SAFETY: valid renderer; null default font and config are allowed.
        unsafe {
            ass_set_frame_size(session.renderer, width, height);
            ass_set_fonts(
                session.renderer,
                ptr::null(),
                c"Arial".as_ptr(),
                FONTPROVIDER_AUTODETECT,
                ptr::null(),
                1,
            );
        }
        let elapsed = start.elapsed();
        Ok((session, elapsed))
    }

    fn drain_log(&self) -> Vec<Message> {
        self.log
            .lock()
            .map(|mut log| std::mem::take(&mut *log))
            .unwrap_or_default()
    }

    fn track(&self, script: &str) -> Option<Track<'_>> {
        let mut bytes = script.as_bytes().to_vec();
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

    /// Renders `track` at `now_ms`, optionally compositing the images onto `canvas`. The image list
    /// is only valid until the next render, so it is consumed here.
    fn render(
        &self,
        track: &Track<'_>,
        now_ms: i64,
        mut canvas: Option<&mut Canvas>,
    ) -> RenderStats {
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
            if let Some(canvas) = canvas.as_deref_mut() {
                canvas.blend(current);
            }
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

struct Track<'a> {
    raw: *mut RawTrack,
    _session: PhantomData<&'a Session>,
}

impl Drop for Track<'_> {
    fn drop(&mut self) {
        // SAFETY: owned track, freed before its library (it borrows the session).
        unsafe { ass_free_track(self.raw) };
    }
}

#[derive(Default)]
struct RenderStats {
    elapsed: Duration,
    changed: c_int,
    images: u32,
    bounds: Option<(i32, i32, i32, i32)>,
}

struct Canvas {
    width: i32,
    height: i32,
    rgb: Vec<u8>,
}

impl Canvas {
    fn new(width: i32, height: i32) -> Self {
        // Neutral dark grey so both white text and black outlines/shadows are visible.
        let rgb = [0x3A; 3].repeat(width as usize * height as usize);
        Canvas { width, height, rgb }
    }

    fn blend(&mut self, image: &Image) {
        let [r, g, b, transparency] = image.color.to_be_bytes();
        let opacity = 255 - u32::from(transparency);
        for y in 0..image.h {
            let dy = image.dst_y + y;
            if dy < 0 || dy >= self.height {
                continue;
            }
            // SAFETY: libass guarantees `stride * (h - 1) + w` bytes, so every row has `w` bytes.
            let row = unsafe {
                std::slice::from_raw_parts(
                    image.bitmap.add((y * image.stride) as usize),
                    image.w as usize,
                )
            };
            for (x, &coverage) in row.iter().enumerate() {
                let dx = image.dst_x + x as i32;
                let alpha = u32::from(coverage) * opacity / 255;
                if alpha == 0 || dx < 0 || dx >= self.width {
                    continue;
                }
                let at = (dy as usize * self.width as usize + dx as usize) * 3;
                for (pixel, source) in self.rgb[at..at + 3].iter_mut().zip([r, g, b]) {
                    *pixel = ((u32::from(*pixel) * (255 - alpha) + u32::from(source) * alpha) / 255)
                        as u8;
                }
            }
        }
    }
}

const WIDTH: i32 = 1920;
const HEIGHT: i32 = 1080;
const PROBE_SCRIPT: &str = include_str!("ass_probe.ass");
const EMBEDDED_NAME: &str = "InterTight-Black.ttf";

/// (label, `\fn` family, text): each case gets its own second in a generated script.
const FONT_CASES: [(&str, &str, &str); 14] = [
    ("Arial", "Arial", "Arial: The quick brown fox"),
    ("Segoe UI", "Segoe UI", "Segoe UI: The quick brown fox"),
    (
        "Times New Roman",
        "Times New Roman",
        "Times New Roman: The quick brown fox",
    ),
    (
        "Courier New",
        "Courier New",
        "Courier New: The quick brown fox",
    ),
    (
        "Missing family",
        "Bale Missing Font",
        "Unknown family falls back",
    ),
    ("Japanese", "Arial", "日本語の字幕テスト、句読点。"),
    ("Korean", "Arial", "한국어 자막 테스트"),
    ("Chinese", "Arial", "简体中文 / 繁體中文"),
    ("Arabic", "Arial", "العربية من اليمين إلى اليسار 123"),
    ("Hebrew", "Arial", "עברית מימין לשמאל"),
    ("Greek/Cyrillic", "Arial", "Ελληνικά / Кириллица"),
    ("Emoji", "Segoe UI Emoji", "😀🎬🍿"),
    ("Vertical @", "@Arial", "縦書き"),
    (
        "Embedded font",
        "Inter Tight Black",
        "Inter Tight Black (attached)",
    ),
];

fn font_case_script() -> String {
    let mut script = String::from(
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nScaledBorderAndShadow: yes\n\n\
         [V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, \
         BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, \
         Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n\
         Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,2,2,40,40,60,1\n\n\
         [Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n",
    );
    for (index, (_, family, text)) in FONT_CASES.iter().enumerate() {
        let _ = writeln!(
            script,
            "Dialogue: 0,0:00:{index:02}.00,0:00:{:02}.00,Default,,0,0,0,,{{\\fn{family}}}{text}",
            index + 1
        );
    }
    script
}

fn micros(duration: Duration) -> u128 {
    duration.as_micros()
}

fn append_log(report: &mut String, session: &Session, indent: &str, limit: usize) {
    let messages = session.drain_log();
    for message in messages.iter().take(limit) {
        let marker = if message.level <= 2 { "!" } else { "" };
        let text: String = message.text.trim_end().chars().take(200).collect();
        let _ = writeln!(report, "{indent}{marker}{text}");
    }
    if messages.len() > limit {
        let _ = writeln!(
            report,
            "{indent}… {} more log lines",
            messages.len() - limit
        );
    }
}

fn describe(stats: &RenderStats) -> String {
    let bounds = stats.bounds.map_or_else(
        || "nothing drawn".to_owned(),
        |(a, b, c, d)| format!("bounds {a},{b}–{c},{d}"),
    );
    format!(
        "{:>7} µs, {:>3} images, {bounds}",
        micros(stats.elapsed),
        stats.images
    )
}

/// Text report: libass version, font provider start-up, font lookups per case (with libass's
/// `fontselect` lines), cold/warm render times for the probe script and an animated stretch at 60 fps.
pub fn probe(font: &[u8]) -> String {
    let mut report = String::new();
    // SAFETY: plain query.
    let version = unsafe { ass_library_version() } as u32;
    let _ = writeln!(
        report,
        "libass {:x}.{:x}.{:x} (0x{version:08X}), static, WinRT DirectWrite provider",
        version >> 28,
        (version >> 20) & 0xFF,
        (version >> 12) & 0xFF
    );
    let embedded: Vec<(&str, &[u8])> = if font.is_empty() {
        Vec::new()
    } else {
        vec![(EMBEDDED_NAME, font)]
    };
    let total = Instant::now();
    let (session, fonts_init) = match Session::new(WIDTH, HEIGHT, &embedded) {
        Ok(created) => created,
        Err(error) => {
            let _ = writeln!(report, "  {error}");
            return report;
        }
    };
    let _ = writeln!(
        report,
        "  ass_set_fonts (provider start-up): {:.1} ms; embedded fonts: {}",
        fonts_init.as_secs_f64() * 1000.0,
        if font.is_empty() {
            "none".to_owned()
        } else {
            format!("{EMBEDDED_NAME}, {} bytes", font.len())
        }
    );
    append_log(&mut report, &session, "    ", 12);

    report.push_str("Font cases (first render includes lookup and fallback):\n");
    match session.track(&font_case_script()) {
        Some(track) => {
            append_log(&mut report, &session, "    ", 4);
            for (index, (label, _, _)) in FONT_CASES.iter().enumerate() {
                let stats = session.render(&track, index as i64 * 1000 + 500, None);
                let _ = writeln!(report, "  {label:<16} {}", describe(&stats));
                append_log(&mut report, &session, "      ", 8);
            }
        }
        None => report.push_str("  ass_read_memory failed\n"),
    }

    report.push_str(
        "Probe script 1920x1080 (cold = first render of each group, warm = 10 ms later):\n",
    );
    let Some(track) = session.track(PROBE_SCRIPT) else {
        report.push_str("  ass_read_memory failed\n");
        return report;
    };
    append_log(&mut report, &session, "    ", 4);
    for ms in [1500i64, 2500, 3500, 4000, 5000, 6500, 7500] {
        let cold = session.render(&track, ms, None);
        let warm = session.render(&track, ms + 10, None);
        let _ = writeln!(
            report,
            "  t={:.1}s cold {} | warm {:>6} µs (changed {})",
            ms as f64 / 1000.0,
            describe(&cold),
            micros(warm.elapsed),
            warm.changed
        );
        append_log(&mut report, &session, "      ", 6);
    }

    // Karaoke, \move, \t and \fad all animate between 4 and 6 s: what playback would ask per frame.
    let frames = 120;
    let (mut sum, mut worst, mut changed) = (Duration::ZERO, Duration::ZERO, 0);
    for frame in 0..frames {
        let stats = session.render(&track, 4000 + frame * 1000 / 60, None);
        sum += stats.elapsed;
        worst = worst.max(stats.elapsed);
        changed += i32::from(stats.changed != 0);
    }
    let _ = writeln!(
        report,
        "Animated 4.0–6.0 s at 60 fps: {frames} frames, avg {} µs, max {} µs, {changed} changed",
        micros(sum) / frames as u128,
        micros(worst)
    );
    append_log(&mut report, &session, "    ", 6);
    drop(track);
    drop(session);
    let _ = writeln!(
        report,
        "Total {:.1} ms",
        total.elapsed().as_secs_f64() * 1000.0
    );
    report
}

/// The probe script rendered at `time_ms` over a dark grey 1920x1080 frame, as a PNG.
pub fn probe_png(font: &[u8], time_ms: u32) -> Option<Vec<u8>> {
    let embedded: Vec<(&str, &[u8])> = if font.is_empty() {
        Vec::new()
    } else {
        vec![(EMBEDDED_NAME, font)]
    };
    let (session, _) = Session::new(WIDTH, HEIGHT, &embedded).ok()?;
    let track = session.track(PROBE_SCRIPT)?;
    let mut canvas = Canvas::new(session.width, session.height);
    session.render(&track, i64::from(time_ms), Some(&mut canvas));
    Some(png::encode_rgb(
        canvas.width as u32,
        canvas.height as u32,
        &canvas.rgb,
    ))
}

#[cfg(test)]
mod tests {
    /// Runs the real probe against the CI runner's DirectWrite fonts (desktop Windows, but the same
    /// app-partition libass build the Xbox gets), so a broken link or font provider fails CI.
    #[test]
    fn probe_renders_with_system_fonts() {
        let report = super::probe(&[]);
        println!("{report}");
        assert!(
            !report.contains("ass_read_memory failed") && !report.contains("_init failed"),
            "libass probe failed:\n{report}"
        );
        let arial = report
            .lines()
            .find(|line| line.trim_start().starts_with("Arial "))
            .expect("Arial case line");
        assert!(
            !arial.contains("nothing drawn"),
            "Arial drew nothing: {arial}"
        );
        let png = super::probe_png(&[], 1500).expect("probe frame renders");
        assert!(png.len() > 1920 * 1080 * 3, "PNG too small: {}", png.len());
    }
}
