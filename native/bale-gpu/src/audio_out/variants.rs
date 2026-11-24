//! Debug `Initialize` variants (docs/audio-passthrough.md, "Exclusive output", "Initialize variants"). On the
//! console the TV endpoint accepted the 7.1 PCM format in `IsFormatSupported(EXCLUSIVE)`, but the output's
//! `Initialize` refused it with `AUDCLNT_E_UNSUPPORTED_FORMAT`; the first variants run refused every row
//! (`AUDCLNT_E_UNSUPPORTED_FORMAT` or `AUDCLNT_E_DEVICE_IN_USE`) and `GetBufferSizeLimits` answered
//! `AUDCLNT_E_OFFLOAD_MODE_ONLY`. `bale_audio_out_init_variants` narrows that down: for each row of [`variants`] it
//! activates a fresh `IAudioClient`, applies the row's client properties (offload rows set `bIsOffload` and fit
//! their buffer into `GetBufferSizeLimits`), asks `IsFormatSupported` and calls `Initialize` in the row's share mode
//! with its flags, timing and format, then releases the client and pauses before the next row. It never calls
//! `Start` and never asks for a render client, so nothing plays; an initialized client holds the endpoint only until
//! it is released, milliseconds later.
//!
//! Rows, offload first: offload in exclusive and shared mode (Movie and Media), then the init flags and timing, the
//! client properties, the format's structure (valid bits, layout, sample type, the shared mix format and the
//! shared-mode closest match) and the bitstream path. Texts are platform independent and unit tested here; only the
//! COM calls are Windows-only.

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
/// `AUDCLNT_E_DEVICE_IN_USE`: the row tries once more on a new client after [`IN_USE_RETRY_MS`].
const AUDCLNT_E_DEVICE_IN_USE: i32 = 0x8889_000Au32 as i32;
/// The pause after every row that reached `Initialize`, so a just-released client cannot make the next row's
/// endpoint look in use.
pub const ROW_PAUSE_MS: u64 = 150;
/// How long a row waits before trying again after `AUDCLNT_E_DEVICE_IN_USE`.
pub const IN_USE_RETRY_MS: u64 = 500;

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
        match self {
            Self::Zero => "0".to_owned(),
            Self::DefaultPeriod => format!("{} (default period)", ms(default_period)),
            Self::MinimumPeriod => format!("{} (minimum period)", ms(minimum_period)),
            Self::Ms(_) => ms(self.hns(default_period, minimum_period)),
        }
    }
}

fn ms(hns: i64) -> String {
    format!("{:.3} ms", hns as f64 / 10_000.0)
}

/// How a row calls `Initialize`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Init {
    /// `AUDCLNT_SHAREMODE_SHARED` (the question too) instead of exclusive.
    pub shared: bool,
    /// `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` (event-driven) or polling.
    pub event: bool,
    /// `AUDCLNT_STREAMFLAGS_NOPERSIST` too.
    pub nopersist: bool,
    pub buffer: Timing,
    pub periodicity: Timing,
    /// `IsFormatSupported` first; false calls `Initialize` alone.
    pub query: bool,
    /// Fit the buffer into `GetBufferSizeLimits` (asked after the properties) before `Initialize`.
    pub fit: bool,
}

impl Init {
    /// The output's own call: event-driven, buffer duration = periodicity = the default period.
    pub const EVENT: Self = Self {
        shared: false,
        event: true,
        nopersist: false,
        buffer: Timing::DefaultPeriod,
        periodicity: Timing::DefaultPeriod,
        query: true,
        fit: false,
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
            "{}{}{}, buffer {}, period {}",
            if self.shared { "shared " } else { "" },
            if self.event { "event" } else { "polling" },
            if self.nopersist { "+nopersist" } else { "" },
            self.buffer.describe(default_period, minimum_period),
            self.periodicity.describe(default_period, minimum_period)
        );
        if self.fit {
            text.push_str(", within GetBufferSizeLimits");
        }
        if !self.query {
            text.push_str(", no IsFormatSupported first");
        }
        text
    }
}

/// The buffer duration and periodicity a row passes to `Initialize`: its timing, with the buffer fitted into
/// `GetBufferSizeLimits` when the row asks for that and the limits make sense. An event-driven exclusive stream
/// takes periodicity = buffer, so its periodicity follows the fitted buffer.
pub fn timings(
    init: &Init,
    default_period: i64,
    minimum_period: i64,
    limits: Option<(i64, i64)>,
) -> (i64, i64) {
    let mut buffer = init.buffer.hns(default_period, minimum_period);
    let mut period = init.periodicity.hns(default_period, minimum_period);
    if init.fit
        && let Some((minimum, maximum)) = limits
        && minimum > 0
        && minimum <= maximum
    {
        buffer = buffer.clamp(minimum, maximum);
        if init.event && !init.shared && period != 0 {
            period = buffer;
        }
    }
    (buffer, period)
}

/// The client properties a row sets before asking.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Properties {
    /// Plain `IAudioClient`: `IAudioClient2` is never asked for, the category stays the default.
    None,
    /// `IAudioClient2::SetClientProperties` with this category, `bIsOffload` from `offload`, and RAW or no stream
    /// options.
    Category {
        category: i32,
        raw: bool,
        offload: bool,
    },
}

impl Properties {
    /// The output's own: `AudioCategory_Movie`.
    pub const MOVIE: Self = Self::Category {
        category: AUDIO_CATEGORY_MOVIE,
        raw: false,
        offload: false,
    };

    pub fn describe(&self) -> String {
        match *self {
            Self::None => "no client properties".to_owned(),
            Self::Category {
                category,
                raw,
                offload,
            } => {
                let mut name = category_name(category);
                if raw {
                    name.push_str(" RAW");
                }
                if offload {
                    name.push_str(" offload");
                }
                name
            }
        }
    }
}

/// The audiosessiontypes.h name of a category the rows use.
pub fn category_name(category: i32) -> String {
    match category {
        AUDIO_CATEGORY_OTHER => "Other".to_owned(),
        AUDIO_CATEGORY_MOVIE => "Movie".to_owned(),
        AUDIO_CATEGORY_MEDIA => "Media".to_owned(),
        other => format!("category {other}"),
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

fn ac3() -> WaveFormat {
    WaveFormat::link(2, 48_000, SPEAKER_STEREO, SUBTYPE_IEC61937_DOLBY_DIGITAL)
}

fn eac3() -> WaveFormat {
    WaveFormat::link(
        2,
        192_000,
        SPEAKER_STEREO,
        SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS,
    )
}

/// Every row, in report order. First offload (Movie, then Media): exclusive event-driven and polling within
/// `GetBufferSizeLimits` for 7.1, 5.1 and 2.0 24-in-32, 2.0 16-bit, AC-3 and E-AC-3, then shared mode for the mix
/// format and 7.1 24-in-32. Then the first run's rows: init flags and timing, client properties (both with the
/// output's format), and the format variations, each event-driven and polling.
pub fn variants() -> Vec<Variant> {
    let today = Source::Fixed(todays_format());
    let (event, poll, movie) = (Init::EVENT, Init::POLL, Properties::MOVIE);
    let fixed = Source::Fixed;
    let mut list = Vec::new();
    let mut push = |name: String, init, properties, source| {
        list.push(Variant {
            name,
            init,
            properties,
            source,
        });
    };

    let offload_formats = [
        ("7.1 24 in 32", today),
        ("5.1 24 in 32", fixed(pcm(6, 32, 24, SPEAKER_5POINT1))),
        ("2.0 24 in 32", fixed(pcm(2, 32, 24, SPEAKER_STEREO))),
        ("2.0 16", fixed(pcm(2, 16, 16, SPEAKER_STEREO))),
        ("AC-3", fixed(ac3())),
        ("E-AC-3", fixed(eac3())),
    ];
    let shared_formats = [("mix format", Source::Mix), ("7.1 24 in 32", today)];
    let fitted = |init: Init| Init { fit: true, ..init };
    let shared_event = Init {
        shared: true,
        buffer: Timing::Ms(20),
        periodicity: Timing::Zero,
        ..fitted(event)
    };
    let shared_poll = Init {
        shared: true,
        ..fitted(poll)
    };
    for category in [AUDIO_CATEGORY_MOVIE, AUDIO_CATEGORY_MEDIA] {
        let offload = Properties::Category {
            category,
            raw: false,
            offload: true,
        };
        let label = category_name(category);
        for (name, source) in offload_formats {
            for (mode, init) in [("event", fitted(event)), ("polling", fitted(poll))] {
                push(
                    format!("offload {label} {name} {mode}"),
                    init,
                    offload,
                    source,
                );
            }
        }
        for (name, source) in shared_formats {
            for (mode, init) in [("event", shared_event), ("polling", shared_poll)] {
                push(
                    format!("offload {label} shared {name} {mode}"),
                    init,
                    offload,
                    source,
                );
            }
        }
    }

    let category = |category, raw| Properties::Category {
        category,
        raw,
        offload: false,
    };
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
    for (name, init, properties, source) in rows {
        push(name.to_owned(), init, properties, source);
    }

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
        ("AC-3", fixed(ac3())),
        ("E-AC-3", fixed(eac3())),
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
    for (name, source) in formats {
        for (mode, init) in [("event", event), ("polling", poll)] {
            push(format!("{name} {mode}"), init, movie, source);
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
    /// `GetBufferSizeLimits` (minimum, maximum in 100 ns) after the properties, when the row fits its buffer.
    pub limits: Option<Result<(i64, i64), i32>>,
    /// The buffer duration and periodicity passed, when fitting changed them.
    pub fitted: Option<(i64, i64)>,
    /// `IsFormatSupported`, when the row asks it.
    pub supported: Option<i32>,
    pub initialize: Option<i32>,
    /// After `S_OK`: `GetBufferSize` and `GetStreamLatency`.
    pub buffer_frames: Option<u32>,
    pub latency_hns: Option<i64>,
    /// After `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`: the aligned buffer, and `Initialize` of a new client with it.
    pub aligned: Option<(u32, i32)>,
    /// After `AUDCLNT_E_DEVICE_IN_USE`: `Initialize` of a new client [`IN_USE_RETRY_MS`] later.
    pub retry: Option<i32>,
}

impl Outcome {
    /// The row's last `Initialize` answer: the retry's, the re-aligned one's, or the first.
    pub fn final_answer(&self) -> Option<i32> {
        self.retry
            .or(self.aligned.map(|(_, hr)| hr))
            .or(self.initialize)
    }

    /// Whether some `Initialize` of the row succeeded.
    pub fn initialized(&self) -> bool {
        self.initialize == Some(S_OK)
            || self.aligned.is_some_and(|(_, hr)| hr == S_OK)
            || self.retry == Some(S_OK)
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

/// Whether a row's `Initialize` answer calls for one more try after [`IN_USE_RETRY_MS`].
pub fn in_use(hr: i32) -> bool {
    hr == AUDCLNT_E_DEVICE_IN_USE
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
    match outcome.limits {
        Some(Ok((minimum, maximum))) => {
            parts.push(format!("limits {} to {}", ms(minimum), ms(maximum)));
        }
        Some(Err(hr)) => parts.push(format!("limits {}", hresult_text(hr))),
        None => {}
    }
    if let Some((buffer, period)) = outcome.fitted {
        parts.push(format!(
            "passed buffer {}, period {}",
            ms(buffer),
            ms(period)
        ));
    }
    if let Some(hr) = outcome.supported {
        parts.push(format!("IsFormatSupported {}", verdict(hr)));
    }
    if let Some(hr) = outcome.initialize {
        let mut init = format!("Initialize {}", hresult_text(hr));
        if let Some((frames, hr)) = outcome.aligned {
            let _ = write!(
                init,
                ", aligned buffer {frames} frames -> re-aligned Initialize {}",
                hresult_text(hr)
            );
        }
        if let Some(hr) = outcome.retry {
            let _ = write!(
                init,
                ", again {IN_USE_RETRY_MS} ms later on a new client -> {}",
                hresult_text(hr)
            );
        }
        if let Some(frames) = outcome.buffer_frames {
            let _ = write!(init, ", buffer {frames} frames");
        }
        if let Some(latency) = outcome.latency_hns {
            let _ = write!(init, ", latency {}", ms(latency));
        }
        parts.push(init);
    }
    text.push_str(&parts.join("; "));
    text
}

/// The report's last line (also logged by the app): rows that initialized, how many were refused and their last
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
        if outcome.initialized() {
            continue;
        }
        let hr = outcome.final_answer().unwrap_or(S_OK);
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
    use std::time::Duration;

    use windows::Media::Devices::{AudioDeviceRole, MediaDevice};
    use windows::Win32::Foundation::HMODULE;
    use windows::Win32::Media::Audio::{
        AUDCLNT_SHAREMODE, AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMOPTIONS, AUDCLNT_STREAMOPTIONS_NONE, AUDIO_STREAM_CATEGORY,
        AudioClientProperties, IAudioClient, IAudioClient2, IAudioClient3, WAVEFORMATEX,
    };
    use windows::Win32::System::Com::CoTaskMemFree;
    use windows::core::{BOOL, Interface, PCWSTR};

    use super::{
        AUDIO_CATEGORY_MEDIA, AUDIO_CATEGORY_MOVIE, IN_USE_RETRY_MS, Init, Outcome, Properties,
        ROW_PAUSE_MS, Source, Variant, aligned_buffer_hns, category_name, in_use, line,
        needs_realign, summary, timings, todays_format, variants,
    };
    use crate::audio_probe::wasapi::{
        ActivateAudioInterfaceAsync, Failed, Mta, activate_client, activation_function,
    };
    use crate::audio_probe::{E_NOT_SET, E_TIMEOUT, S_OK, WaveFormat, hresult_text, verdict};

    /// Debug `Initialize` variants (DEBUG_SERVER `GET /audio-out-test?variants=1`; docs/audio-passthrough.md,
    /// "Initialize variants"): per row a fresh `IAudioClient` on the default render endpoint, the row's client
    /// properties, `IsFormatSupported` and `Initialize` in the row's share mode, then the client is released and the
    /// next row waits 150 ms. Never calls `Start` or asks for a render client: nothing plays. Writes the plain-text
    /// report as UTF-8 into `report` and returns the bytes written (cut at a character boundary). Blocks the calling
    /// thread, which must not be the UI thread, for one activation and pause per row (about 12 s for all rows; a 2 s
    /// activation timeout stops the run).
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
            "Audio Initialize variants: default render endpoint, a fresh IAudioClient per row (properties, \
             IsFormatSupported, Initialize), released without Start (nothing plays), 150 ms between rows\n",
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
        offload_capable(&context, report);
        let closest = shared_closest(&context, &today, report);
        buffer_limits(&context, &today, report);
        drop(context);

        let _ = writeln!(
            report,
            "Rows: name: init; client properties; format -> answers (properties, limits, IsFormatSupported, Initialize):"
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
            if outcome.initialize.is_some() {
                // Released above; give the endpoint time before the next row asks.
                std::thread::sleep(Duration::from_millis(ROW_PAUSE_MS));
            }
        }
        report.push_str(&summary(&results));
        Ok(())
    }

    /// `IAudioClient2::IsOffloadCapable` for the categories the offload rows use.
    fn offload_capable(client: &IAudioClient, report: &mut String) {
        let Ok(client2) = client.cast::<IAudioClient2>() else {
            let _ = writeln!(report, "Context: IsOffloadCapable: no IAudioClient2");
            return;
        };
        let answers: Vec<String> = [AUDIO_CATEGORY_MOVIE, AUDIO_CATEGORY_MEDIA]
            .into_iter()
            .map(|category| {
                // SAFETY: a live client; a plain category value.
                let answer = unsafe { client2.IsOffloadCapable(AUDIO_STREAM_CATEGORY(category)) };
                let text = answer.map_or_else(
                    |error| hresult_text(error.code().0),
                    |capable| if capable.as_bool() { "yes" } else { "no" }.to_owned(),
                );
                format!("{} {text}", category_name(category))
            })
            .collect();
        let _ = writeln!(report, "Context: IsOffloadCapable {}", answers.join(", "));
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

    /// `IsFormatSupported(SHARED, format)` and the closest match it offers (freed), if any.
    fn shared_question(client: &IAudioClient, format: &WaveFormat) -> (i32, Option<WaveFormat>) {
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
        (hr.0, unsafe { read_and_free(closest) })
    }

    /// The shared-mode answer for the output's format and the closest match offered, reported.
    fn shared_closest(
        client: &IAudioClient,
        format: &WaveFormat,
        report: &mut String,
    ) -> Option<WaveFormat> {
        let (hr, closest) = shared_question(client, format);
        let _ = writeln!(
            report,
            "Context: IsFormatSupported(SHARED, the output's format) -> {}{}",
            verdict(hr),
            closest
                .as_ref()
                .map_or_else(String::new, |f| format!(", closest match {}", f.describe()))
        );
        closest
    }

    /// `IAudioClient2::GetBufferSizeLimits` for `format`, in 100 ns.
    fn limits(client: &IAudioClient, format: &WaveFormat, event: bool) -> Result<(i64, i64), i32> {
        let client2 = client
            .cast::<IAudioClient2>()
            .map_err(|error| error.code().0)?;
        let (mut minimum, mut maximum) = (0i64, 0i64);
        // SAFETY: a live client; `format` covers its declared bytes; both out-slots are valid locals.
        unsafe {
            client2.GetBufferSizeLimits(
                std::ptr::from_ref(format).cast(),
                event,
                &mut minimum,
                &mut maximum,
            )
        }
        .map_err(|error| error.code().0)?;
        Ok((minimum, maximum))
    }

    /// The context client's limits for the output's format without offload, event-driven and polling.
    fn buffer_limits(client: &IAudioClient, format: &WaveFormat, report: &mut String) {
        for event in [true, false] {
            let _ = writeln!(
                report,
                "Context: GetBufferSizeLimits(the output's format, {}, no offload) -> {}",
                if event { "event-driven" } else { "polling" },
                limits(client, format, event).map_or_else(hresult_text, |(minimum, maximum)| {
                    format!(
                        "S_OK, {:.3} to {:.3} ms",
                        minimum as f64 / 10_000.0,
                        maximum as f64 / 10_000.0
                    )
                })
            );
        }
    }

    /// Sets a row's client properties; `None` when the row sets none.
    fn apply(client: &IAudioClient, properties: Properties) -> Option<i32> {
        let Properties::Category {
            category,
            raw,
            offload,
        } = properties
        else {
            return None;
        };
        let client2 = match client.cast::<IAudioClient2>() {
            Ok(client2) => client2,
            Err(error) => return Some(error.code().0),
        };
        let properties = AudioClientProperties {
            cbSize: size_of::<AudioClientProperties>() as u32,
            bIsOffload: BOOL::from(offload),
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

    fn share_mode(init: &Init) -> AUDCLNT_SHAREMODE {
        if init.shared {
            AUDCLNT_SHAREMODE_SHARED
        } else {
            AUDCLNT_SHAREMODE_EXCLUSIVE
        }
    }

    /// `Initialize` in the row's share mode with its flags and the given timing.
    fn initialize(
        client: &IAudioClient,
        init: &Init,
        buffer: i64,
        period: i64,
        format: &WaveFormat,
    ) -> i32 {
        // SAFETY: a live client; `format` covers its declared bytes.
        unsafe {
            client.Initialize(
                share_mode(init),
                init.flags(),
                buffer,
                period,
                std::ptr::from_ref(format).cast(),
                None,
            )
        }
        .map_or_else(|error| error.code().0, |()| S_OK)
    }

    /// One `Initialize` on a fresh client with the row's properties, released before returning (never started).
    fn initialize_fresh(
        activate: ActivateAudioInterfaceAsync,
        endpoint: PCWSTR,
        variant: &Variant,
        buffer: i64,
        period: i64,
        format: &WaveFormat,
    ) -> i32 {
        match activate_client(activate, endpoint) {
            Ok((client, _)) => {
                let _ = apply(&client, variant.properties);
                initialize(&client, &variant.init, buffer, period, format)
            }
            Err(Failed(_, hr)) => hr,
        }
    }

    /// One row on a fresh client, released before returning: properties, limits (offload rows), the question,
    /// `Initialize`, and on success the buffer and latency. Re-aligns after `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` and
    /// tries once more after `AUDCLNT_E_DEVICE_IN_USE`. Never started.
    fn attempt(
        activate: ActivateAudioInterfaceAsync,
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
        let init = variant.init;
        outcome.properties = apply(&client, variant.properties);
        if init.fit {
            outcome.limits = Some(limits(&client, format, init.event));
        }
        let fitted = timings(
            &init,
            default_period,
            minimum_period,
            outcome.limits.and_then(Result::ok),
        );
        let plain = timings(&init, default_period, minimum_period, None);
        if fitted != plain {
            outcome.fitted = Some(fitted);
        }
        let (buffer, period) = fitted;
        if init.query {
            outcome.supported = Some(if init.shared {
                shared_question(&client, format).0
            } else {
                // SAFETY: a live client; `format` covers its declared bytes; exclusive mode takes no closest match.
                unsafe {
                    client.IsFormatSupported(
                        AUDCLNT_SHAREMODE_EXCLUSIVE,
                        std::ptr::from_ref(format).cast(),
                        None,
                    )
                }
                .0
            });
        }
        let hr = initialize(&client, &init, buffer, period, format);
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
                let period = if init.event && !init.shared {
                    aligned
                } else {
                    period
                };
                let second = initialize_fresh(activate, endpoint, variant, aligned, period, format);
                outcome.aligned = Some((frames, second));
                return outcome;
            }
        } else if in_use(hr) {
            // In use: released first, then the same call again on a new client a little later.
            drop(client);
            std::thread::sleep(std::time::Duration::from_millis(IN_USE_RETRY_MS));
            outcome.retry = Some(initialize_fresh(
                activate, endpoint, variant, buffer, period, format,
            ));
            return outcome;
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

    /// The rows as the report prints them before the answers: offload first (exclusive within the limits for
    /// six formats, shared for two, Movie then Media), then the first run's rows unchanged.
    #[test]
    fn variants_are_the_documented_list() {
        let expected = include_str!("variants_golden.txt");
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
        assert_eq!(names.len(), 68);
        // Offload rows come first and are the only ones that fit their buffer.
        let all = variants();
        assert!(
            all[..32]
                .iter()
                .all(|v| v.init.fit && v.name.starts_with("offload "))
        );
        assert!(all[32..].iter().all(|v| !v.init.fit));
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
        assert_eq!(category_name(3), "category 3");
        // 512 frames at 48 kHz, as the re-align formula gives it.
        assert_eq!(aligned_buffer_hns(&todays_format(), 512), 106_667);
        assert!(needs_realign(0x8889_0019u32 as i32));
        assert!(!needs_realign(0x8889_0008u32 as i32));
        assert!(in_use(0x8889_000Au32 as i32));
        assert!(!in_use(0x8889_0008u32 as i32));
    }

    #[test]
    fn offload_timing_fits_into_the_limits() {
        let fitted = Init {
            fit: true,
            ..Init::EVENT
        };
        // Event-driven exclusive: the buffer clamps into the limits and the periodicity follows it.
        assert_eq!(
            timings(&fitted, DEFAULT, MINIMUM, Some((200_000, 2_000_000))),
            (200_000, 200_000)
        );
        assert_eq!(
            timings(&fitted, DEFAULT, MINIMUM, Some((10_000, 50_000))),
            (50_000, 50_000)
        );
        assert_eq!(
            timings(&fitted, DEFAULT, MINIMUM, Some((10_000, 2_000_000))),
            (DEFAULT, DEFAULT)
        );
        // Unusable limits leave the timing alone; so does a row that does not fit.
        assert_eq!(
            timings(&fitted, DEFAULT, MINIMUM, Some((0, 0))),
            (DEFAULT, DEFAULT)
        );
        assert_eq!(
            timings(&fitted, DEFAULT, MINIMUM, Some((500_000, 100_000))),
            (DEFAULT, DEFAULT)
        );
        assert_eq!(
            timings(&Init::EVENT, DEFAULT, MINIMUM, Some((200_000, 400_000))),
            (DEFAULT, DEFAULT)
        );
        // Polling and shared rows keep periodicity 0.
        let poll = Init {
            fit: true,
            ..Init::POLL
        };
        assert_eq!(
            timings(&poll, DEFAULT, MINIMUM, Some((10_000, 1_000_000))),
            (1_000_000, 0)
        );
        let shared = Init {
            shared: true,
            buffer: Timing::Ms(20),
            periodicity: Timing::Zero,
            fit: true,
            ..Init::EVENT
        };
        assert_eq!(
            timings(&shared, DEFAULT, MINIMUM, Some((300_000, 1_000_000))),
            (300_000, 0)
        );
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
        let retried = Outcome {
            supported: Some(S_OK),
            initialize: Some(0x8889_000Au32 as i32),
            retry: Some(0x8889_000Au32 as i32),
            ..Outcome::default()
        };
        assert!(line(a, Some(&today), &retried, DEFAULT, MINIMUM).ends_with(
            "-> IsFormatSupported S_OK (accepted); Initialize AUDCLNT_E_DEVICE_IN_USE (0x8889000A), again 500 ms later \
             on a new client -> AUDCLNT_E_DEVICE_IN_USE (0x8889000A)"
        ));
        assert!(!retried.initialized());
        let offload = all
            .iter()
            .find(|v| v.name == "offload Movie 7.1 24 in 32 event")
            .unwrap();
        let fitted = Outcome {
            properties: Some(S_OK),
            limits: Some(Ok((200_000, 2_000_000))),
            fitted: Some((200_000, 200_000)),
            supported: Some(S_OK),
            initialize: Some(S_OK),
            buffer_frames: Some(960),
            ..Outcome::default()
        };
        assert!(line(offload, Some(&today), &fitted, DEFAULT, MINIMUM).ends_with(
            "; Movie offload; ext 8 ch 48000 Hz 32-bit (24 valid) mask 0x63F PCM -> limits 20.000 ms to 200.000 ms; \
             passed buffer 20.000 ms, period 20.000 ms; IsFormatSupported S_OK (accepted); Initialize S_OK, buffer 960 frames"
        ));
        let no_limits = Outcome {
            properties: Some(0x8889_0022u32 as i32),
            limits: Some(Err(0x8889_0024u32 as i32)),
            supported: Some(S_OK),
            initialize: Some(0x8889_0008u32 as i32),
            ..Outcome::default()
        };
        assert!(line(offload, Some(&today), &no_limits, DEFAULT, MINIMUM).ends_with(
            "-> properties AUDCLNT_E_ENDPOINT_OFFLOAD_NOT_CAPABLE (0x88890022) (not applied); limits \
             AUDCLNT_E_OFFLOAD_MODE_ONLY (0x88890024); IsFormatSupported S_OK (accepted); Initialize \
             AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008)"
        ));
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
    fn the_summary_names_what_initialized_and_counts_the_last_answers() {
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
        let in_use_then_ok = Outcome {
            initialize: Some(0x8889_000Au32 as i32),
            retry: Some(S_OK),
            ..Outcome::default()
        };
        let still_in_use = Outcome {
            initialize: Some(0x8889_000Au32 as i32),
            retry: Some(0x8889_000Au32 as i32),
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
            ("2.0 16 event", in_use_then_ok),
            ("2.0 16 polling", still_in_use),
            ("mix format event", skipped),
            ("d polling default", unactivated),
        ];
        assert_eq!(
            summary(&results),
            "Summary: initialized: c polling 200 ms, 2.0 16 event; 3 of 5 rows refused; Initialize: \
             AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008) x2, AUDCLNT_E_DEVICE_IN_USE (0x8889000A) x1; 1 skipped; \
             1 not activated\n"
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
