//! Debug `Initialize` variants (docs/audio-passthrough.md, "Exclusive output", "Initialize variants"). On the
//! console the TV endpoint accepted the 7.1 PCM format in `IsFormatSupported(EXCLUSIVE)`, but the output's
//! `Initialize` refused it with `AUDCLNT_E_UNSUPPORTED_FORMAT`. `bale_audio_out_init_variants` narrows that down:
//! for each row of [`variants`] it activates a fresh `IAudioClient`, applies the row's client properties, asks
//! `IsFormatSupported(EXCLUSIVE)` and calls `Initialize` with the row's flags, timing and format, then releases the
//! client. It never calls `Start` and never asks for a render client, so nothing plays; an initialized exclusive
//! client holds the endpoint only until it is released, milliseconds later.
//!
//! The rows cover the plausible causes: the init flags and timing, the client properties, the format's structure
//! (valid bits, layout, sample type, the shared mix format and the shared-mode closest match) and the bitstream
//! path. Texts are platform independent and unit tested here; only the COM calls are Windows-only.

use std::fmt::Write as _;

use super::aligned_period_hns;
use crate::audio_probe::{
    S_OK, SPEAKER_5POINT1, SPEAKER_7POINT1_SURROUND, SPEAKER_STEREO,
    SUBTYPE_IEC61937_DOLBY_DIGITAL, SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS, SUBTYPE_IEEE_FLOAT,
    SUBTYPE_PCM, WAVE_FORMAT_PCM, WaveFormat, hresult_text, verdict,
};

/// audiosessiontypes.h stream flags.
pub const AUDCLNT_STREAMFLAGS_EVENTCALLBACK: u32 = 0x0004_0000;
pub const AUDCLNT_STREAMFLAGS_NOPERSIST: u32 = 0x0008_0000;
/// audiosessiontypes.h `AUDIO_STREAM_CATEGORY` values the rows use.
pub const AUDIO_CATEGORY_OTHER: i32 = 0;
pub const AUDIO_CATEGORY_MOVIE: i32 = 10;
pub const AUDIO_CATEGORY_MEDIA: i32 = 11;
/// audioclient.h `AUDCLNT_STREAMOPTIONS_RAW`.
pub const AUDCLNT_STREAMOPTIONS_RAW: i32 = 1;
/// `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`: the row re-aligns as Microsoft documents and reports both answers.
const AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED: i32 = 0x8889_0019u32 as i32;

/// A buffer duration or periodicity for `Initialize`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Timing {
    Zero,
    /// `GetDevicePeriod`'s default period.
    DefaultPeriod,
    /// `GetDevicePeriod`'s minimum period.
    MinimumPeriod,
    Ms(u32),
}

impl Timing {
    /// In 100 ns, with the device's default and minimum periods.
    pub fn hns(self, default_period: i64, minimum_period: i64) -> i64 {
        match self {
            Self::Zero => 0,
            Self::DefaultPeriod => default_period,
            Self::MinimumPeriod => minimum_period,
            Self::Ms(ms) => i64::from(ms) * 10_000,
        }
    }

    fn describe(self, default_period: i64, minimum_period: i64) -> String {
        let ms = |hns: i64| format!("{:.3} ms", hns as f64 / 10_000.0);
        match self {
            Self::Zero => "0".to_owned(),
            Self::DefaultPeriod => format!("{} (default period)", ms(default_period)),
            Self::MinimumPeriod => format!("{} (minimum period)", ms(minimum_period)),
            Self::Ms(_) => ms(self.hns(default_period, minimum_period)),
        }
    }
}

/// How a row calls `Initialize`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Init {
    /// `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` (event-driven) or polling.
    pub event: bool,
    /// `AUDCLNT_STREAMFLAGS_NOPERSIST` too.
    pub nopersist: bool,
    pub buffer: Timing,
    pub periodicity: Timing,
    /// `IsFormatSupported(EXCLUSIVE)` first; false calls `Initialize` alone.
    pub query: bool,
}

impl Init {
    /// The output's own call: event-driven, buffer duration = periodicity = the default period.
    pub const EVENT: Self = Self {
        event: true,
        nopersist: false,
        buffer: Timing::DefaultPeriod,
        periodicity: Timing::DefaultPeriod,
        query: true,
    };
    /// Polling with a 200 ms buffer and periodicity 0 (the device's period).
    pub const POLL: Self = Self {
        event: false,
        buffer: Timing::Ms(200),
        periodicity: Timing::Zero,
        ..Self::EVENT
    };

    pub fn flags(&self) -> u32 {
        let event = if self.event {
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK
        } else {
            0
        };
        let nopersist = if self.nopersist {
            AUDCLNT_STREAMFLAGS_NOPERSIST
        } else {
            0
        };
        event | nopersist
    }

    pub fn describe(&self, default_period: i64, minimum_period: i64) -> String {
        let mut text = format!(
            "{}{}, buffer {}, period {}",
            if self.event { "event" } else { "polling" },
            if self.nopersist { "+nopersist" } else { "" },
            self.buffer.describe(default_period, minimum_period),
            self.periodicity.describe(default_period, minimum_period)
        );
        if !self.query {
            text.push_str(", no IsFormatSupported first");
        }
        text
    }
}

/// The client properties a row sets before asking.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Properties {
    /// Plain `IAudioClient`: `IAudioClient2` is never asked for, the category stays the default.
    None,
    /// `IAudioClient2::SetClientProperties` with this category, offload off, and RAW or no stream options.
    Category { category: i32, raw: bool },
}

impl Properties {
    /// The output's own: `AudioCategory_Movie`.
    pub const MOVIE: Self = Self::Category {
        category: AUDIO_CATEGORY_MOVIE,
        raw: false,
    };

    pub fn describe(&self) -> String {
        match *self {
            Self::None => "no client properties".to_owned(),
            Self::Category { category, raw } => {
                let name = match category {
                    AUDIO_CATEGORY_OTHER => "Other".to_owned(),
                    AUDIO_CATEGORY_MOVIE => "Movie".to_owned(),
                    AUDIO_CATEGORY_MEDIA => "Media".to_owned(),
                    other => format!("category {other}"),
                };
                if raw { format!("{name} RAW") } else { name }
            }
        }
    }
}

/// Where a row's format comes from.
#[derive(Clone, Copy)]
pub enum Source {
    Fixed(WaveFormat),
    /// `GetMixFormat`'s block as it comes (the shared engine's format).
    Mix,
    /// The closest match `IsFormatSupported(SHARED)` offers for the output's format.
    SharedClosest,
}

/// One `Initialize` attempt.
#[derive(Clone)]
pub struct Variant {
    /// Short and unique, no commas.
    pub name: String,
    pub init: Init,
    pub properties: Properties,
    pub source: Source,
}

fn pcm(channels: u16, bits: u16, valid: u16, mask: u32) -> WaveFormat {
    WaveFormat::extensible(channels, 48_000, bits, valid, mask, SUBTYPE_PCM)
}

/// The output's format: the probe's accepted "PCM 7.1 24-bit in 32" row.
pub fn todays_format() -> WaveFormat {
    pcm(8, 32, 24, SPEAKER_7POINT1_SURROUND)
}

/// Every row, in report order: init flags and timing, then client properties (both with the output's format),
/// then the format variations, each event-driven and polling.
pub fn variants() -> Vec<Variant> {
    let today = Source::Fixed(todays_format());
    let (event, poll, movie) = (Init::EVENT, Init::POLL, Properties::MOVIE);
    let category = |category, raw| Properties::Category { category, raw };
    let fixed = Source::Fixed;
    let formats = [
        (
            "7.1 32 valid",
            fixed(pcm(8, 32, 32, SPEAKER_7POINT1_SURROUND)),
        ),
        ("5.1 24 in 32", fixed(pcm(6, 32, 24, SPEAKER_5POINT1))),
        ("2.0 16", fixed(pcm(2, 16, 16, SPEAKER_STEREO))),
        ("2.0 24 in 32", fixed(pcm(2, 32, 24, SPEAKER_STEREO))),
        ("7.1 16", fixed(pcm(8, 16, 16, SPEAKER_7POINT1_SURROUND))),
        (
            "2.0 16 WAVEFORMATEX",
            fixed(WaveFormat::tagged(WAVE_FORMAT_PCM, 2, 48_000)),
        ),
        (
            "7.1 float",
            fixed(WaveFormat::extensible(
                8,
                48_000,
                32,
                32,
                SPEAKER_7POINT1_SURROUND,
                SUBTYPE_IEEE_FLOAT,
            )),
        ),
        ("mix format", Source::Mix),
        ("shared closest", Source::SharedClosest),
        (
            "AC-3",
            fixed(WaveFormat::link(
                2,
                48_000,
                SPEAKER_STEREO,
                SUBTYPE_IEC61937_DOLBY_DIGITAL,
            )),
        ),
        (
            "E-AC-3",
            fixed(WaveFormat::link(
                2,
                192_000,
                SPEAKER_STEREO,
                SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS,
            )),
        ),
        (
            "E-AC-3 iec 48k 5.1",
            fixed(WaveFormat::iec61937(
                2,
                192_000,
                SPEAKER_5POINT1,
                SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS,
                48_000,
                6,
            )),
        ),
    ];
    #[rustfmt::skip]
    let rows = [
        ("a event default", event, movie, today),
        ("b event minimum", Init { buffer: Timing::MinimumPeriod, periodicity: Timing::MinimumPeriod, ..event }, movie, today),
        ("c polling 200 ms", poll, movie, today),
        ("d polling default", Init { event: false, ..event }, movie, today),
        ("e event nopersist", Init { nopersist: true, ..event }, movie, today),
        ("f event 0/0", Init { buffer: Timing::Zero, periodicity: Timing::Zero, ..event }, movie, today),
        ("g Initialize alone", Init { query: false, ..event }, movie, today),
        ("p1 no properties", event, Properties::None, today),
        ("p2 Other", event, category(AUDIO_CATEGORY_OTHER, false), today),
        ("p3 Media", event, category(AUDIO_CATEGORY_MEDIA, false), today),
        ("p4 Movie RAW", event, category(AUDIO_CATEGORY_MOVIE, true), today),
        ("p5 polling no properties", poll, Properties::None, today),
    ];
    let mut list: Vec<Variant> = rows
        .into_iter()
        .map(|(name, init, properties, source)| Variant {
            name: name.to_owned(),
            init,
            properties,
            source,
        })
        .collect();
    for (name, source) in formats {
        for (mode, init) in [("event", event), ("polling", poll)] {
            list.push(Variant {
                name: format!("{name} {mode}"),
                init,
                properties: movie,
                source,
            });
        }
    }
    list
}

/// What one row's calls answered.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Outcome {
    /// The row has no format (no mix format, no closest match): nothing was asked.
    pub skipped: Option<&'static str>,
    /// Activation failed with this; nothing else ran.
    pub activation: Option<i32>,
    /// `SetClientProperties` (or the `IAudioClient2` query) answered this, when the row sets properties.
    pub properties: Option<i32>,
    /// `IsFormatSupported(EXCLUSIVE)`, when the row asks it.
    pub supported: Option<i32>,
    pub initialize: Option<i32>,
    /// After `S_OK`: `GetBufferSize` and `GetStreamLatency`.
    pub buffer_frames: Option<u32>,
    pub latency_hns: Option<i64>,
    /// After `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`: the aligned buffer, and `Initialize` of a new client with it.
    pub aligned: Option<(u32, i32)>,
}

impl Outcome {
    /// Whether some `Initialize` of the row succeeded.
    pub fn initialized(&self) -> bool {
        self.initialize == Some(S_OK) || self.aligned.is_some_and(|(_, hr)| hr == S_OK)
    }
}

/// The re-aligned buffer duration for `frames` at the format's rate (Microsoft's formula).
pub fn aligned_buffer_hns(format: &WaveFormat, frames: u32) -> i64 {
    aligned_period_hns(frames, { format.samples_per_sec }.max(1))
}

/// Whether a row's `Initialize` answer calls for the re-align recipe.
pub fn needs_realign(hr: i32) -> bool {
    hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
}

/// One report row: `name: init; properties; format -> answers`.
pub fn line(
    variant: &Variant,
    format: Option<&WaveFormat>,
    outcome: &Outcome,
    default_period: i64,
    minimum_period: i64,
) -> String {
    let format_text = format.map_or_else(
        || match variant.source {
            Source::Mix => "the mix format".to_owned(),
            Source::SharedClosest => "the shared closest match".to_owned(),
            Source::Fixed(f) => f.describe(),
        },
        WaveFormat::describe,
    );
    let mut text = format!(
        "{}: {}; {}; {} -> ",
        variant.name,
        variant.init.describe(default_period, minimum_period),
        variant.properties.describe(),
        format_text
    );
    if let Some(reason) = outcome.skipped {
        let _ = write!(text, "skipped ({reason})");
        return text;
    }
    if let Some(hr) = outcome.activation {
        let _ = write!(text, "activation {} (nothing asked)", hresult_text(hr));
        return text;
    }
    let mut parts = Vec::new();
    if let Some(hr) = outcome.properties
        && hr != S_OK
    {
        parts.push(format!("properties {} (not applied)", hresult_text(hr)));
    }
    if let Some(hr) = outcome.supported {
        parts.push(format!("IsFormatSupported {}", verdict(hr)));
    }
    if let Some(hr) = outcome.initialize {
        let mut init = format!("Initialize {}", hresult_text(hr));
        if let Some(frames) = outcome.buffer_frames {
            let _ = write!(init, ", buffer {frames} frames");
        }
        if let Some(latency) = outcome.latency_hns {
            let _ = write!(init, ", latency {:.3} ms", latency as f64 / 10_000.0);
        }
        if let Some((frames, hr)) = outcome.aligned {
            let _ = write!(
                init,
                ", aligned buffer {frames} frames -> re-aligned Initialize {}",
                hresult_text(hr)
            );
        }
        parts.push(init);
    }
    text.push_str(&parts.join("; "));
    text
}

/// The report's last line (also logged by the app): rows that initialized, how many were refused and their
/// `Initialize` answers, and the rows that were skipped or could not be activated.
pub fn summary<S: AsRef<str>>(results: &[(S, Outcome)]) -> String {
    let initialized: Vec<&str> = results
        .iter()
        .filter(|(_, outcome)| outcome.initialized())
        .map(|(name, _)| name.as_ref())
        .collect();
    let tried: Vec<&Outcome> = results
        .iter()
        .map(|(_, outcome)| outcome)
        .filter(|outcome| outcome.initialize.is_some())
        .collect();
    let mut answers: Vec<(i32, usize)> = Vec::new();
    for outcome in &tried {
        let hr = match outcome.aligned {
            Some((_, hr)) => hr,
            None => outcome.initialize.unwrap_or(S_OK),
        };
        if hr == S_OK {
            continue;
        }
        match answers.iter_mut().find(|(code, _)| *code == hr) {
            Some((_, count)) => *count += 1,
            None => answers.push((hr, 1)),
        }
    }
    let failed = tried.len() - initialized.len();
    let mut text = format!(
        "Summary: initialized: {}; {failed} of {} rows refused",
        if initialized.is_empty() {
            "none".to_owned()
        } else {
            initialized.join(", ")
        },
        tried.len()
    );
    for (index, (hr, count)) in answers.iter().enumerate() {
        let separator = if index == 0 { "; Initialize: " } else { ", " };
        let _ = write!(text, "{separator}{} x{count}", hresult_text(*hr));
    }
    let count = |test: fn(&Outcome) -> bool| results.iter().filter(|(_, o)| test(o)).count();
    let skipped = count(|o| o.skipped.is_some());
    if skipped > 0 {
        let _ = write!(text, "; {skipped} skipped");
    }
    let unactivated = count(|o| o.activation.is_some());
    if unactivated > 0 {
        let _ = write!(text, "; {unactivated} not activated");
    }
    text.push('\n');
    text
}

#[cfg(windows)]
pub use wasapi::bale_audio_out_init_variants;

#[cfg(windows)]
mod wasapi {
    use std::fmt::Write as _;
    use std::panic::{AssertUnwindSafe, catch_unwind};

    use windows::Media::Devices::{AudioDeviceRole, MediaDevice};
    use windows::Win32::Foundation::HMODULE;
    use windows::Win32::Media::Audio::{
        AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMOPTIONS,
        AUDCLNT_STREAMOPTIONS_NONE, AUDIO_STREAM_CATEGORY, AudioClientProperties, IAudioClient,
        IAudioClient2, IAudioClient3, WAVEFORMATEX,
    };
    use windows::Win32::System::Com::CoTaskMemFree;
    use windows::core::{BOOL, Interface, PCWSTR};

    use super::{
        Outcome, Properties, Source, Variant, aligned_buffer_hns, line, needs_realign, summary,
        todays_format, variants,
    };
    use crate::audio_probe::wasapi::{Failed, Mta, activate_client, activation_function};
    use crate::audio_probe::{E_NOT_SET, E_TIMEOUT, S_OK, WaveFormat, hresult_text, verdict};

    /// Debug `Initialize` variants (DEBUG_SERVER `GET /audio-out-test?variants=1`; docs/audio-passthrough.md,
    /// "Initialize variants"): per row a fresh `IAudioClient` on the default render endpoint, the row's client
    /// properties, `IsFormatSupported(EXCLUSIVE)` and `Initialize`, then the client is released. Never calls
    /// `Start` or asks for a render client: nothing plays. Writes the plain-text report as UTF-8 into `report`
    /// and returns the bytes written (cut at a character boundary). Blocks the calling thread, which must not be
    /// the UI thread, for one activation per row (milliseconds; a 2 s activation timeout stops the run).
    ///
    /// # Safety
    /// `mmdevapi` must be null or a module the caller keeps loaded for the call; `report` must point to
    /// `report_capacity` writable bytes.
    #[unsafe(no_mangle)]
    pub unsafe extern "C" fn bale_audio_out_init_variants(
        mmdevapi: HMODULE,
        report: *mut u8,
        report_capacity: u32,
    ) -> u32 {
        if report.is_null() {
            return 0;
        }
        let text = catch_unwind(AssertUnwindSafe(|| run(mmdevapi))).unwrap_or_else(|_| {
            "Audio Initialize variants: panicked\nSummary: not run (panic)\n".to_owned()
        });
        // SAFETY: the caller guarantees `report_capacity` writable bytes.
        unsafe { crate::write_report(&text, report, report_capacity) }
    }

    fn not_run(report: &mut String, step: &str, hr: i32) {
        let _ = writeln!(report, "Error: {} at {step}", hresult_text(hr));
        let _ = writeln!(report, "Summary: not run ({} at {step})", hresult_text(hr));
    }

    fn run(mmdevapi: HMODULE) -> String {
        let mut report = String::from(
            "Audio Initialize variants: default render endpoint, exclusive mode, a fresh IAudioClient per row \
             (properties, IsFormatSupported, Initialize), released without Start: nothing plays\n",
        );
        let mta = match Mta::join() {
            Ok(mta) => mta,
            Err(Failed(step, hr)) => {
                not_run(&mut report, step, hr);
                return report;
            }
        };
        // Every COM object lives inside `rows`, so all are released before the apartment is left.
        if let Err(Failed(step, hr)) = rows(mmdevapi, &mut report) {
            not_run(&mut report, step, hr);
        }
        drop(mta);
        report
    }

    fn rows(mmdevapi: HMODULE, report: &mut String) -> Result<(), Failed> {
        let activate = activation_function(mmdevapi)?;
        let id = MediaDevice::GetDefaultAudioRenderId(AudioDeviceRole::Default)
            .map_err(|error| Failed("MediaDevice.GetDefaultAudioRenderId", error.code().0))?;
        if id.is_empty() {
            return Err(Failed(
                "MediaDevice.GetDefaultAudioRenderId (no default render endpoint)",
                E_NOT_SET,
            ));
        }
        let endpoint = PCWSTR(id.as_ptr());
        let today = todays_format();

        // Context from one client that is only asked, never initialized.
        let (context, elapsed) = activate_client(activate, endpoint)?;
        let _ = writeln!(
            report,
            "Activation: IAudioClient in {} ms",
            elapsed.as_millis()
        );
        let mix = mix_format(&context);
        let _ = writeln!(
            report,
            "Context: mix format {}",
            mix.as_ref()
                .map_or_else(|hr| hresult_text(*hr), WaveFormat::describe)
        );
        let (mut default_period, mut minimum_period) = (0i64, 0i64);
        // SAFETY: a live client; both out-slots are valid locals.
        let periods = unsafe {
            context.GetDevicePeriod(
                Some(std::ptr::from_mut(&mut default_period)),
                Some(std::ptr::from_mut(&mut minimum_period)),
            )
        };
        let _ = writeln!(
            report,
            "Context: GetDevicePeriod {}: default {:.3} ms, minimum {:.3} ms; IAudioClient3 {}",
            periods.map_or_else(|error| hresult_text(error.code().0), |()| "S_OK".to_owned()),
            default_period as f64 / 10_000.0,
            minimum_period as f64 / 10_000.0,
            if context.cast::<IAudioClient3>().is_ok() {
                "available"
            } else {
                "not available"
            }
        );
        let closest = shared_closest(&context, &today, report);
        buffer_limits(&context, &today, report);
        drop(context);

        let _ = writeln!(
            report,
            "Rows: name: init; client properties; format -> answers (IsFormatSupported(EXCLUSIVE); Initialize):"
        );
        let mut results = Vec::new();
        for variant in variants() {
            let format = match variant.source {
                Source::Fixed(format) => Some(format),
                Source::Mix => mix.ok(),
                Source::SharedClosest => closest,
            };
            let outcome = match format {
                None => Outcome {
                    skipped: Some(match variant.source {
                        Source::Mix => "no mix format",
                        _ => "no closest match offered",
                    }),
                    ..Outcome::default()
                },
                Some(format) => attempt(
                    activate,
                    endpoint,
                    &variant,
                    &format,
                    default_period,
                    minimum_period,
                ),
            };
            let _ = writeln!(
                report,
                "  {}",
                line(
                    &variant,
                    format.as_ref(),
                    &outcome,
                    default_period,
                    minimum_period
                )
            );
            results.push((variant.name.clone(), outcome));
            if outcome.activation == Some(E_TIMEOUT) {
                let _ = writeln!(
                    report,
                    "Stopped: an activation timed out; the rest were not tried"
                );
                break;
            }
        }
        report.push_str(&summary(&results));
        Ok(())
    }

    /// `GetMixFormat`, read and freed.
    fn mix_format(client: &IAudioClient) -> Result<WaveFormat, i32> {
        // SAFETY: a live client; the returned block is read and freed with CoTaskMemFree below.
        let mix = unsafe { client.GetMixFormat() }.map_err(|error| error.code().0)?;
        // SAFETY: GetMixFormat returns a WAVEFORMATEX and its cbSize extra bytes (or null).
        let format = unsafe { read_and_free(mix) };
        format.ok_or(E_NOT_SET)
    }

    /// Reads a CoTaskMem format block of `18 + cbSize` bytes and frees it.
    ///
    /// # Safety
    /// `format` must be null or a CoTaskMemAlloc'd WAVEFORMATEX followed by its `cbSize` extra bytes.
    unsafe fn read_and_free(format: *mut WAVEFORMATEX) -> Option<WaveFormat> {
        if format.is_null() {
            return None;
        }
        // SAFETY: the caller guarantees a readable WAVEFORMATEX (packed, alignment 1).
        let header = unsafe { format.read_unaligned() };
        let length = crate::audio_probe::WAVEFORMATEX_SIZE + usize::from(header.cbSize);
        // SAFETY: the caller guarantees `length` readable bytes.
        let read =
            WaveFormat::read(unsafe { std::slice::from_raw_parts(format.cast::<u8>(), length) });
        // SAFETY: allocated by the audio engine with CoTaskMemAlloc; not used afterwards.
        unsafe { CoTaskMemFree(Some(format.cast_const().cast())) };
        read
    }

    /// `IsFormatSupported(SHARED, format)` and the closest match it offers, if any.
    fn shared_closest(
        client: &IAudioClient,
        format: &WaveFormat,
        report: &mut String,
    ) -> Option<WaveFormat> {
        let mut closest: *mut WAVEFORMATEX = std::ptr::null_mut();
        // SAFETY: a live client; `format` covers its declared bytes; the out-slot is a valid local.
        let hr = unsafe {
            client.IsFormatSupported(
                AUDCLNT_SHAREMODE_SHARED,
                std::ptr::from_ref(format).cast(),
                Some(std::ptr::from_mut(&mut closest)),
            )
        };
        // SAFETY: a closest match is a CoTaskMem format block, or null.
        let closest = unsafe { read_and_free(closest) };
        let _ = writeln!(
            report,
            "Context: IsFormatSupported(SHARED, the output's format) -> {}{}",
            verdict(hr.0),
            closest
                .as_ref()
                .map_or_else(String::new, |f| format!(", closest match {}", f.describe()))
        );
        closest
    }

    /// `IAudioClient2::GetBufferSizeLimits` for the output's format, event-driven and polling.
    fn buffer_limits(client: &IAudioClient, format: &WaveFormat, report: &mut String) {
        let Ok(client2) = client.cast::<IAudioClient2>() else {
            let _ = writeln!(report, "Context: GetBufferSizeLimits: no IAudioClient2");
            return;
        };
        for event in [true, false] {
            let (mut minimum, mut maximum) = (0i64, 0i64);
            // SAFETY: a live client; `format` covers its declared bytes; both out-slots are valid locals.
            let answer = unsafe {
                client2.GetBufferSizeLimits(
                    std::ptr::from_ref(format).cast(),
                    event,
                    &mut minimum,
                    &mut maximum,
                )
            };
            let _ = writeln!(
                report,
                "Context: GetBufferSizeLimits(the output's format, {}) -> {}",
                if event { "event-driven" } else { "polling" },
                answer.map_or_else(
                    |error| hresult_text(error.code().0),
                    |()| format!(
                        "S_OK, {:.3} to {:.3} ms",
                        minimum as f64 / 10_000.0,
                        maximum as f64 / 10_000.0
                    )
                )
            );
        }
    }

    /// Sets a row's client properties; `None` when the row sets none.
    fn apply(client: &IAudioClient, properties: Properties) -> Option<i32> {
        let Properties::Category { category, raw } = properties else {
            return None;
        };
        let client2 = match client.cast::<IAudioClient2>() {
            Ok(client2) => client2,
            Err(error) => return Some(error.code().0),
        };
        let properties = AudioClientProperties {
            cbSize: size_of::<AudioClientProperties>() as u32,
            bIsOffload: BOOL(0),
            eCategory: AUDIO_STREAM_CATEGORY(category),
            Options: if raw {
                AUDCLNT_STREAMOPTIONS(super::AUDCLNT_STREAMOPTIONS_RAW)
            } else {
                AUDCLNT_STREAMOPTIONS_NONE
            },
        };
        // SAFETY: a live client; `properties` is a valid local for the call.
        Some(
            unsafe { client2.SetClientProperties(&properties) }
                .map_or_else(|e| e.code().0, |()| S_OK),
        )
    }

    /// `Initialize` in exclusive mode with the row's flags and timing.
    fn initialize(
        client: &IAudioClient,
        flags: u32,
        buffer: i64,
        period: i64,
        format: &WaveFormat,
    ) -> i32 {
        // SAFETY: a live client; `format` covers its declared bytes.
        unsafe {
            client.Initialize(
                AUDCLNT_SHAREMODE_EXCLUSIVE,
                flags,
                buffer,
                period,
                std::ptr::from_ref(format).cast(),
                None,
            )
        }
        .map_or_else(|error| error.code().0, |()| S_OK)
    }

    /// One row on a fresh client, released before returning: properties, the question, `Initialize`, and on
    /// success the buffer and latency. Never started.
    fn attempt(
        activate: crate::audio_probe::wasapi::ActivateAudioInterfaceAsync,
        endpoint: PCWSTR,
        variant: &Variant,
        format: &WaveFormat,
        default_period: i64,
        minimum_period: i64,
    ) -> Outcome {
        let mut outcome = Outcome::default();
        let client = match activate_client(activate, endpoint) {
            Ok((client, _)) => client,
            Err(Failed(_, hr)) => {
                outcome.activation = Some(hr);
                return outcome;
            }
        };
        outcome.properties = apply(&client, variant.properties);
        if variant.init.query {
            // SAFETY: a live client; `format` covers its declared bytes; exclusive mode takes no closest match.
            let hr = unsafe {
                client.IsFormatSupported(
                    AUDCLNT_SHAREMODE_EXCLUSIVE,
                    std::ptr::from_ref(format).cast(),
                    None,
                )
            };
            outcome.supported = Some(hr.0);
        }
        let flags = variant.init.flags();
        let buffer = variant.init.buffer.hns(default_period, minimum_period);
        let period = variant.init.periodicity.hns(default_period, minimum_period);
        let hr = initialize(&client, flags, buffer, period, format);
        outcome.initialize = Some(hr);
        if hr == S_OK {
            // SAFETY: a live, initialized client.
            outcome.buffer_frames = unsafe { client.GetBufferSize() }.ok();
            // SAFETY: as above.
            outcome.latency_hns = unsafe { client.GetStreamLatency() }.ok();
        } else if needs_realign(hr) {
            // Microsoft's recipe: the aligned size from this client, then a new client with that duration.
            // SAFETY: a live client; after this failure GetBufferSize returns the next aligned size.
            if let Ok(frames) = unsafe { client.GetBufferSize() } {
                drop(client);
                let aligned = aligned_buffer_hns(format, frames);
                let period = if variant.init.event { aligned } else { period };
                let second = match activate_client(activate, endpoint) {
                    Ok((client, _)) => {
                        let _ = apply(&client, variant.properties);
                        initialize(&client, flags, aligned, period, format)
                    }
                    Err(Failed(_, hr)) => hr,
                };
                outcome.aligned = Some((frames, second));
                return outcome;
            }
        }
        // Released here, never started.
        drop(client);
        outcome
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        /// No module: reported, nothing asked (no device needed).
        #[test]
        fn a_missing_module_is_reported() {
            let mut buffer = vec![0u8; 4096];
            // SAFETY: a valid buffer of the given size.
            let written = unsafe {
                bale_audio_out_init_variants(
                    HMODULE::default(),
                    buffer.as_mut_ptr(),
                    buffer.len() as u32,
                )
            };
            let text = std::str::from_utf8(&buffer[..written as usize]).unwrap();
            assert!(text.contains(
                "Summary: not run (E_INVALIDARG (0x80070057) at mmdevapi.dll (not loaded by the caller))"
            ));
            // SAFETY: a null report is refused before anything runs.
            assert_eq!(
                unsafe {
                    bale_audio_out_init_variants(HMODULE::default(), std::ptr::null_mut(), 0)
                },
                0
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const DEFAULT: i64 = 100_000;
    const MINIMUM: i64 = 30_000;

    /// The rows the coordinator asked for (init flags and timing, client properties, format structure,
    /// bitstreams), as the report prints them before the answers.
    #[test]
    fn variants_are_the_documented_list() {
        let expected = "\
a event default: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
b event minimum: event, buffer 3.000 ms (minimum period), period 3.000 ms (minimum period); Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
c polling 200 ms: polling, buffer 200.000 ms, period 0; Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
d polling default: polling, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
e event nopersist: event+nopersist, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
f event 0/0: event, buffer 0, period 0; Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
g Initialize alone: event, buffer 10.000 ms (default period), period 10.000 ms (default period), no IsFormatSupported first; Movie; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
p1 no properties: event, buffer 10.000 ms (default period), period 10.000 ms (default period); no client properties; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
p2 Other: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Other; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
p3 Media: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Media; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
p4 Movie RAW: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie RAW; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
p5 polling no properties: polling, buffer 200.000 ms, period 0; no client properties; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM
7.1 32 valid event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 32-bit mask 0x63F PCM
7.1 32 valid polling: polling, buffer 200.000 ms, period 0; Movie; ext 8 ch 48000 Hz 32-bit mask 0x63F PCM
5.1 24 in 32 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 6 ch 48000 Hz 32-bit (24 valid) mask 0x3F PCM
5.1 24 in 32 polling: polling, buffer 200.000 ms, period 0; Movie; ext 6 ch 48000 Hz 32-bit (24 valid) mask 0x3F PCM
2.0 16 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 2 ch 48000 Hz 16-bit mask 0x3 PCM
2.0 16 polling: polling, buffer 200.000 ms, period 0; Movie; ext 2 ch 48000 Hz 16-bit mask 0x3 PCM
2.0 24 in 32 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 2 ch 48000 Hz 32-bit (24 valid) mask 0x3 PCM
2.0 24 in 32 polling: polling, buffer 200.000 ms, period 0; Movie; ext 2 ch 48000 Hz 32-bit (24 valid) mask 0x3 PCM
7.1 16 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 16-bit mask 0x63F PCM
7.1 16 polling: polling, buffer 200.000 ms, period 0; Movie; ext 8 ch 48000 Hz 16-bit mask 0x63F PCM
2.0 16 WAVEFORMATEX event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; WAVE_FORMAT_PCM 2 ch 48000 Hz 16-bit
2.0 16 WAVEFORMATEX polling: polling, buffer 200.000 ms, period 0; Movie; WAVE_FORMAT_PCM 2 ch 48000 Hz 16-bit
7.1 float event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 8 ch 48000 Hz 32-bit mask 0x63F IEEE_FLOAT
7.1 float polling: polling, buffer 200.000 ms, period 0; Movie; ext 8 ch 48000 Hz 32-bit mask 0x63F IEEE_FLOAT
mix format event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; the mix format
mix format polling: polling, buffer 200.000 ms, period 0; Movie; the mix format
shared closest event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; the shared closest match
shared closest polling: polling, buffer 200.000 ms, period 0; Movie; the shared closest match
AC-3 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL
AC-3 polling: polling, buffer 200.000 ms, period 0; Movie; ext 2 ch 48000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL
E-AC-3 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; ext 2 ch 192000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL_PLUS
E-AC-3 polling: polling, buffer 200.000 ms, period 0; Movie; ext 2 ch 192000 Hz 16-bit mask 0x3 IEC61937_DOLBY_DIGITAL_PLUS
E-AC-3 iec 48k 5.1 event: event, buffer 10.000 ms (default period), period 10.000 ms (default period); Movie; iec 2 ch 192000 Hz 16-bit mask 0x3F IEC61937_DOLBY_DIGITAL_PLUS, decoded 48000 Hz 6 ch
E-AC-3 iec 48k 5.1 polling: polling, buffer 200.000 ms, period 0; Movie; iec 2 ch 192000 Hz 16-bit mask 0x3F IEC61937_DOLBY_DIGITAL_PLUS, decoded 48000 Hz 6 ch
";
        let actual: String = variants()
            .iter()
            .map(|v| {
                let text = line(v, None, &Outcome::default(), DEFAULT, MINIMUM);
                format!("{}\n", text.trim_end_matches(" -> "))
            })
            .collect();
        assert_eq!(actual, expected);
        let mut names = std::collections::HashSet::new();
        for variant in variants() {
            assert!(names.insert(variant.name.clone()), "{} twice", variant.name);
            assert!(!variant.name.contains(','), "{}", variant.name);
        }
        assert_eq!(names.len(), 36);
    }

    #[test]
    fn flags_and_timing_resolve() {
        assert_eq!(Init::EVENT.flags(), 0x4_0000);
        assert_eq!(Init::POLL.flags(), 0);
        assert_eq!(
            Init {
                nopersist: true,
                ..Init::EVENT
            }
            .flags(),
            0xC_0000
        );
        assert_eq!(Timing::DefaultPeriod.hns(DEFAULT, MINIMUM), DEFAULT);
        assert_eq!(Timing::MinimumPeriod.hns(DEFAULT, MINIMUM), MINIMUM);
        assert_eq!(Timing::Ms(200).hns(DEFAULT, MINIMUM), 2_000_000);
        assert_eq!(Timing::Zero.hns(DEFAULT, MINIMUM), 0);
        assert_eq!(
            Properties::Category {
                category: 3,
                raw: false
            }
            .describe(),
            "category 3"
        );
        // 512 frames at 48 kHz, as the re-align formula gives it.
        assert_eq!(aligned_buffer_hns(&todays_format(), 512), 106_667);
        assert!(needs_realign(0x8889_0019u32 as i32));
        assert!(!needs_realign(0x8889_0008u32 as i32));
    }

    #[test]
    fn rows_print_every_answer_they_got() {
        let all = variants();
        let a = all.iter().find(|v| v.name == "a event default").unwrap();
        let today = todays_format();
        let refused = Outcome {
            properties: Some(S_OK),
            supported: Some(S_OK),
            initialize: Some(0x8889_0008u32 as i32),
            ..Outcome::default()
        };
        assert!(
            line(a, Some(&today), &refused, DEFAULT, MINIMUM).ends_with(
                "ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM -> IsFormatSupported S_OK (accepted); \
                 Initialize AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008)"
            )
        );
        let opened = Outcome {
            properties: Some(0x8889_0027u32 as i32),
            supported: Some(S_OK),
            initialize: Some(S_OK),
            buffer_frames: Some(480),
            latency_hns: Some(20_000),
            ..Outcome::default()
        };
        assert!(line(a, Some(&today), &opened, DEFAULT, MINIMUM).ends_with(
            "-> properties AUDCLNT_E_RAW_MODE_UNSUPPORTED (0x88890027) (not applied); IsFormatSupported S_OK (accepted); \
             Initialize S_OK, buffer 480 frames, latency 2.000 ms"
        ));
        let aligned = Outcome {
            initialize: Some(0x8889_0019u32 as i32),
            aligned: Some((512, S_OK)),
            ..Outcome::default()
        };
        assert!(line(a, Some(&today), &aligned, DEFAULT, MINIMUM).ends_with(
            "-> Initialize AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED (0x88890019), aligned buffer 512 frames -> re-aligned \
             Initialize S_OK"
        ));
        assert!(aligned.initialized());
        let mix = all.iter().find(|v| v.name == "mix format event").unwrap();
        let skipped = Outcome {
            skipped: Some("no mix format"),
            ..Outcome::default()
        };
        assert!(
            line(mix, None, &skipped, DEFAULT, MINIMUM)
                .ends_with("; Movie; the mix format -> skipped (no mix format)")
        );
        let lost = Outcome {
            activation: Some(0x8007_05B4u32 as i32),
            ..Outcome::default()
        };
        assert!(line(a, Some(&today), &lost, DEFAULT, MINIMUM).ends_with(
            "-> activation HRESULT_FROM_WIN32(ERROR_TIMEOUT) (0x800705B4) (nothing asked)"
        ));
    }

    #[test]
    fn the_summary_names_what_initialized_and_counts_the_answers() {
        let unsupported = Outcome {
            supported: Some(S_OK),
            initialize: Some(0x8889_0008u32 as i32),
            ..Outcome::default()
        };
        let ok = Outcome {
            initialize: Some(S_OK),
            buffer_frames: Some(9_600),
            ..Outcome::default()
        };
        let realigned = Outcome {
            initialize: Some(0x8889_0019u32 as i32),
            aligned: Some((512, 0x8889_0008u32 as i32)),
            ..Outcome::default()
        };
        let skipped = Outcome {
            skipped: Some("no mix format"),
            ..Outcome::default()
        };
        let unactivated = Outcome {
            activation: Some(0x8007_05B4u32 as i32),
            ..Outcome::default()
        };
        let results = [
            ("a event default", unsupported),
            ("c polling 200 ms", ok),
            ("b event minimum", realigned),
            ("mix format event", skipped),
            ("d polling default", unactivated),
        ];
        assert_eq!(
            summary(&results),
            "Summary: initialized: c polling 200 ms; 2 of 3 rows refused; Initialize: \
             AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008) x2; 1 skipped; 1 not activated\n"
        );
        assert_eq!(
            summary(&[("a event default", unsupported)]),
            "Summary: initialized: none; 1 of 1 rows refused; Initialize: AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008) x1\n"
        );
    }

    /// The variants must never play: no Start, and no render client to write with.
    #[test]
    fn the_variants_never_start_or_render() {
        let source = include_str!("variants.rs");
        for call in [
            concat!(".Sta", "rt("),
            concat!("IAudioRender", "Client"),
            concat!("GetBu", "ffer("),
        ] {
            assert!(!source.contains(call), "variants.rs uses {call}");
        }
    }
}
