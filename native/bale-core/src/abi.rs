//! The C ABI: `bale_core_abi_version`, `bale_core_call` and `bale_core_free`.

use std::panic::{AssertUnwindSafe, catch_unwind};

use serde::Serialize;
use serde::de::DeserializeOwned;

/// Bumped whenever an export's signature or meaning changes; C# refuses a mismatched library.
/// Adding a function name to [`dispatch`] is not an ABI change.
pub const ABI_VERSION: u32 = 1;

/// The result buffer holds the function's JSON result.
pub const CALL_OK: i32 = 0;
/// The result buffer holds a JSON string describing the failure (unknown name, bad JSON, panic).
pub const CALL_FAILED: i32 = 1;
/// A pointer argument was null or the name was not UTF-8; nothing was written.
pub const CALL_INVALID_ARGUMENT: i32 = -1;

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_abi_version() -> u32 {
    ABI_VERSION
}

/// Runs the function `name` on the JSON argument `input` and hands back a buffer the caller must
/// release with [`bale_core_free`] (on [`CALL_OK`] and [`CALL_FAILED`]).
///
/// # Safety
///
/// `name` and `input` must point to `name_len` and `input_len` readable bytes (either may be
/// dangling when its length is 0), and `output` and `output_len` must be valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_call(
    name: *const u8,
    name_len: usize,
    input: *const u8,
    input_len: usize,
    output: *mut *mut u8,
    output_len: *mut usize,
) -> i32 {
    if output.is_null() || output_len.is_null() {
        return CALL_INVALID_ARGUMENT;
    }
    // SAFETY: the caller guarantees both ranges are readable; a zero length never reads.
    let (name, input) = unsafe { (bytes(name, name_len), bytes(input, input_len)) };
    let (Some(name), Some(input)) = (name, input) else {
        return CALL_INVALID_ARGUMENT;
    };
    let Ok(name) = std::str::from_utf8(name) else {
        return CALL_INVALID_ARGUMENT;
    };
    let (status, body) = match call(name, input) {
        Ok(body) => (CALL_OK, body),
        Err(message) => (CALL_FAILED, error_body(&message)),
    };
    let len = body.len();
    let body = Box::into_raw(body.into_boxed_slice());
    // SAFETY: checked non-null above; the caller guarantees they are writable.
    unsafe {
        *output_len = len;
        *output = body.cast();
    }
    status
}

/// Releases a buffer returned by [`bale_core_call`]. A null pointer is ignored.
///
/// # Safety
///
/// `buffer` and `len` must be exactly what one `bale_core_call` returned, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_free(buffer: *mut u8, len: usize) {
    if buffer.is_null() {
        return;
    }
    // SAFETY: the caller hands back a boxed slice this library leaked, exactly once.
    drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(buffer, len)) });
}

/// Runs one call, turning a panic into an error so it never unwinds into C#.
pub fn call(name: &str, input: &[u8]) -> Result<Vec<u8>, String> {
    catch_unwind(AssertUnwindSafe(|| dispatch(name, input)))
        .unwrap_or_else(|_| Err(format!("{name} panicked")))
}

fn dispatch(name: &str, input: &[u8]) -> Result<Vec<u8>, String> {
    use crate::models::{
        Checked, DeviceCapabilities, DisplayMode, FileVersion, HdrCapabilities, PlaybackPlan,
    };
    use crate::{display, dolby_vision7, hardware, route, software_decode, tone_map};
    use serde::Deserialize;

    #[derive(Deserialize)]
    struct VideoSupported {
        avx2: bool,
        device_form: Option<String>,
    }
    #[derive(Deserialize)]
    struct AudioTrack {
        selected_ordinal: Option<i64>,
        stream_indices: Vec<i32>,
        expected_audio_count: Option<i32>,
    }
    #[derive(Deserialize)]
    struct CanManage {
        version: FileVersion,
        device: DeviceCapabilities,
        decodes: bool,
        dolby_vision: bool,
    }
    #[derive(Deserialize)]
    struct ToneMap {
        plan: PlaybackPlan,
        sdr_output: bool,
        versions: Option<Vec<FileVersion>>,
    }
    #[derive(Deserialize)]
    struct BaseLayer {
        stream_indices: Vec<i32>,
        expected_video_count: Option<i32>,
    }
    #[derive(Deserialize)]
    struct FromModes {
        read_succeeded: bool,
        modes: Vec<DisplayMode>,
    }
    #[derive(Deserialize)]
    struct Plan {
        plan: PlaybackPlan,
    }
    #[derive(Deserialize)]
    struct Sizes {
        sizes: Vec<hardware::AcceptedSize>,
    }
    #[derive(Deserialize)]
    struct Hdr10Mode {
        current: DisplayMode,
        modes: Vec<DisplayMode>,
    }

    match name {
        "software_decode.normalize_codec" => with_json(name, input, |codec: Option<String>| {
            software_decode::normalize_codec(codec.as_deref())
        }),
        "software_decode.lists" => with_json(name, input, |(): ()| software_decode::lists()),
        "software_decode.video_supported" => with_json(name, input, |a: VideoSupported| {
            software_decode::software_video_supported(a.avx2, a.device_form.as_deref())
        }),
        "software_decode.filter_device" => with_json(name, input, software_decode::filter_device),
        "software_decode.video_decoders_allowed" => {
            with_json(name, input, software_decode::video_decoders_allowed)
        }
        "software_decode.without_failed_fallbacks" => {
            with_json(name, input, software_decode::without_failed_fallbacks)
        }
        "route.decide" => with_json(name, input, route::decide),
        "route.is_matroska" => with_json(name, input, |c: Option<String>| {
            route::is_matroska(c.as_deref())
        }),
        "route.audio_track_index" => with_json(name, input, |a: AudioTrack| {
            Checked::from(route::audio_track_index(
                a.selected_ordinal,
                &a.stream_indices,
                a.expected_audio_count,
            ))
        }),
        "tone_map.has_hdr_output" => with_json(name, input, |h: HdrCapabilities| {
            tone_map::has_hdr_output(&h)
        }),
        "tone_map.wrapper_supported" => {
            with_json(name, input, |v: String| tone_map::wrapper_supported(&v))
        }
        "tone_map.can_manage" => with_json(name, input, |a: CanManage| {
            tone_map::can_manage(&a.version, &a.device, a.decodes, a.dolby_vision)
        }),
        "tone_map.requires_tone_map" => with_json(name, input, |a: ToneMap| {
            tone_map::requires_tone_map(&a.plan, a.sdr_output, a.versions.as_deref())
        }),
        "dolby_vision7.requires_base_layer" => with_json(name, input, |a: Plan| {
            dolby_vision7::requires_base_layer(&a.plan)
        }),
        "dolby_vision7.validate_plan" => with_json(name, input, |a: Plan| {
            Checked::from(dolby_vision7::validate_plan(&a.plan))
        }),
        "dolby_vision7.base_layer_track_index" => with_json(name, input, |a: BaseLayer| {
            Checked::from(dolby_vision7::base_layer_track_index(
                &a.stream_indices,
                a.expected_video_count,
            ))
        }),
        "display.from_modes" => with_json(name, input, |a: FromModes| {
            display::from_modes(a.read_succeeded, &a.modes)
        }),
        "display.pick_hdr10_mode" => with_json(name, input, |a: Hdr10Mode| {
            display::pick_hdr10_mode(&a.current, &a.modes)
        }),
        "display.requires_hdr10" => {
            with_json(name, input, |a: Plan| display::requires_hdr10(&a.plan))
        }
        "display.pick_mode" => with_json(name, input, |a: display::PickModeArgs| {
            display::pick_mode(&a)
        }),
        "hardware.profile_name" => with_json(name, input, |p: String| hardware::profile_name(&p)),
        "hardware.from_accepted_sizes" => with_json(name, input, |a: Sizes| {
            hardware::from_accepted_sizes(&a.sizes)
        }),
        _ => Err(format!("unknown function {name}")),
    }
}

fn with_json<I: DeserializeOwned, O: Serialize>(
    name: &str,
    input: &[u8],
    function: impl FnOnce(I) -> O,
) -> Result<Vec<u8>, String> {
    // serde_json's messages quote unexpected input, so report only where the JSON went wrong.
    let argument = serde_json::from_slice(input).map_err(|error| {
        format!(
            "{name}: argument is not valid JSON for this function (line {}, column {})",
            error.line(),
            error.column()
        )
    })?;
    serde_json::to_vec(&function(argument))
        .map_err(|_| format!("{name}: result could not be serialised"))
}

fn error_body(message: &str) -> Vec<u8> {
    serde_json::to_vec(message).unwrap_or_else(|_| b"\"error\"".to_vec())
}

/// # Safety
///
/// `pointer` must point to `len` readable bytes unless `len` is 0.
unsafe fn bytes<'a>(pointer: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        Some(&[])
    } else if pointer.is_null() {
        None
    } else {
        // SAFETY: non-null, and the caller guarantees `len` readable bytes.
        Some(unsafe { std::slice::from_raw_parts(pointer, len) })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn round_trip(name: &str, input: &str) -> (i32, String) {
        let mut output = std::ptr::null_mut();
        let mut output_len = 0;
        // SAFETY: valid slices and out-pointers; the buffer is released below.
        let status = unsafe {
            bale_core_call(
                name.as_ptr(),
                name.len(),
                input.as_ptr(),
                input.len(),
                &mut output,
                &mut output_len,
            )
        };
        // SAFETY: the buffer and length come straight from bale_core_call.
        let text = unsafe {
            String::from_utf8(std::slice::from_raw_parts(output, output_len).to_vec()).unwrap()
        };
        // SAFETY: released once, exactly as returned.
        unsafe { bale_core_free(output, output_len) };
        (status, text)
    }

    #[test]
    fn calls_return_json() {
        assert_eq!(
            round_trip("software_decode.normalize_codec", "\" H.265 \""),
            (CALL_OK, "\"hevc\"".into())
        );
        assert_eq!(
            round_trip("software_decode.normalize_codec", "null"),
            (CALL_OK, "null".into())
        );
    }

    #[test]
    fn failures_return_a_message_without_the_argument() {
        let (status, text) = round_trip("software_decode.normalize_codec", "{\"secret\": 1}");
        assert_eq!(status, CALL_FAILED);
        assert!(!text.contains("secret"), "{text}");
        assert_eq!(round_trip("nope", "null").0, CALL_FAILED);
    }

    #[test]
    fn rejects_null_and_non_utf8_arguments() {
        let mut output = std::ptr::null_mut();
        let mut output_len = 0;
        // SAFETY: deliberately invalid arguments the function must reject without reading.
        unsafe {
            assert_eq!(
                bale_core_call(
                    std::ptr::null(),
                    3,
                    b"null".as_ptr(),
                    4,
                    &mut output,
                    &mut output_len
                ),
                CALL_INVALID_ARGUMENT
            );
            assert_eq!(
                bale_core_call(
                    b"\xff".as_ptr(),
                    1,
                    b"null".as_ptr(),
                    4,
                    &mut output,
                    &mut output_len
                ),
                CALL_INVALID_ARGUMENT
            );
            assert_eq!(
                bale_core_call(
                    b"x".as_ptr(),
                    1,
                    b"null".as_ptr(),
                    4,
                    std::ptr::null_mut(),
                    &mut output_len
                ),
                CALL_INVALID_ARGUMENT
            );
            bale_core_free(std::ptr::null_mut(), 0);
        }
        assert!(output.is_null());
    }
}
