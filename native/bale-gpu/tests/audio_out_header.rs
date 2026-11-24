//! Compile the public audio output header as C and C++, checking it against Rust's actual layout and constants
//! (the FFmpegInteropX fork includes it). Linux CI has cc/c++.
#![cfg(not(windows))]
use bale_gpu::audio_out::*;
use std::mem::{align_of, offset_of, size_of};
use std::process::Command;

#[test]
fn c_and_cpp_header_match_rust() {
    let mut source = String::from("#include <stddef.h>\n#include \"bale_audio_out.h\"\n");
    macro_rules! record {
        ($ty:ty, $($field:ident),+ $(,)?) => {{
            source.push_str(&format!("CHECK(sizeof({}) == {});\nCHECK(ALIGN({}) == {});\n",
                stringify!($ty), size_of::<$ty>(), stringify!($ty), align_of::<$ty>()));
            $(source.push_str(&format!("CHECK(offsetof({}, {}) == {});\n",
                stringify!($ty), stringify!($field), offset_of!($ty, $field)));)+
        }};
    }
    record!(
        BaleAudioFormat,
        kind,
        channels,
        sample_rate,
        container_bits,
        valid_bits,
        channel_mask,
        iec_subtype,
        encoded_rate,
        encoded_channels,
        reserved
    );
    record!(
        BaleAudioOutOpenInfo,
        step,
        hr,
        activate_hr,
        properties_hr,
        format_hr,
        initialize_hr,
        realign_hr,
        activate_ms,
        default_period_100ns,
        minimum_period_100ns,
        period_100ns,
        buffer_frames,
        ring_frames,
        mmcss,
        realigned,
        path,
        offload_hr
    );
    record!(
        BaleAudioOutStats,
        status,
        error,
        buffer_frames,
        ring_frames,
        queued_frames,
        queued_ms,
        underruns,
        flushes,
        frames_written,
        frames_rendered,
        silence_frames,
        events,
        device_position_100ns,
        device_qpc_100ns,
        device_pts_100ns,
        sync_error_100ns,
        syncs,
        syncs_unmeasured,
        drop_corrections,
        pad_corrections,
        dropped_frames,
        padded_frames,
        path,
        reserved
    );
    // Function pointer assignments check every export's C declaration.
    source.push_str(
        "static uint32_t (*abi_version)(void) = &bale_audio_out_abi_version;\n\
         static HRESULT (*create)(void*, const BaleAudioFormat*, uint32_t, uint32_t*, BaleAudioOutOpenInfo*) = &bale_audio_out_create;\n\
         static uint32_t (*write_frames)(uint32_t, const void*, uint32_t, int64_t) = &bale_audio_out_write;\n\
         static HRESULT (*start)(uint32_t) = &bale_audio_out_start;\n\
         static HRESULT (*pause_output)(uint32_t) = &bale_audio_out_pause;\n\
         static HRESULT (*flush)(uint32_t) = &bale_audio_out_flush;\n\
         static HRESULT (*sync_clock)(uint32_t, int64_t, int64_t) = &bale_audio_out_sync;\n\
         static HRESULT (*stats)(uint32_t, BaleAudioOutStats*) = &bale_audio_out_stats;\n\
         static HRESULT (*format)(uint32_t, BaleAudioFormat*) = &bale_audio_out_format;\n\
         static HRESULT (*destroy)(uint32_t) = &bale_audio_out_destroy;\n",
    );
    for (name, value) in [
        ("BALE_AUDIO_OUT_ABI_VERSION", u64::from(ABI_VERSION)),
        ("BALE_AUDIO_KIND_PCM", u64::from(KIND_PCM)),
        ("BALE_AUDIO_KIND_IEC61937", u64::from(KIND_IEC61937)),
        ("BALE_AUDIO_IEC_NONE", u64::from(IEC_NONE)),
        ("BALE_AUDIO_IEC_AC3", u64::from(IEC_AC3)),
        ("BALE_AUDIO_IEC_EAC3", u64::from(IEC_EAC3)),
        ("BALE_AUDIO_IEC_DTS", u64::from(IEC_DTS)),
        ("BALE_AUDIO_IEC_DTS_HD", u64::from(IEC_DTS_HD)),
        ("BALE_AUDIO_IEC_MLP", u64::from(IEC_MLP)),
        ("BALE_AUDIO_OUT_STEP_DONE", u64::from(STEP_DONE)),
        ("BALE_AUDIO_OUT_STEP_ARGUMENTS", u64::from(STEP_ARGUMENTS)),
        ("BALE_AUDIO_OUT_STEP_MODULE", u64::from(STEP_MODULE)),
        ("BALE_AUDIO_OUT_STEP_THREAD", u64::from(STEP_THREAD)),
        ("BALE_AUDIO_OUT_STEP_DEVICE_ID", u64::from(STEP_DEVICE_ID)),
        ("BALE_AUDIO_OUT_STEP_ACTIVATE", u64::from(STEP_ACTIVATE)),
        ("BALE_AUDIO_OUT_STEP_FORMAT", u64::from(STEP_FORMAT)),
        ("BALE_AUDIO_OUT_STEP_INITIALIZE", u64::from(STEP_INITIALIZE)),
        ("BALE_AUDIO_OUT_STEP_SERVICES", u64::from(STEP_SERVICES)),
        ("BALE_AUDIO_OUT_STEP_TABLE", u64::from(STEP_TABLE)),
        ("BALE_AUDIO_OUT_PATH_EXCLUSIVE", u64::from(PATH_EXCLUSIVE)),
        (
            "BALE_AUDIO_OUT_PATH_EXCLUSIVE_OFFLOAD",
            u64::from(PATH_EXCLUSIVE_OFFLOAD),
        ),
        (
            "BALE_AUDIO_OUT_PATH_SHARED_OFFLOAD",
            u64::from(PATH_SHARED_OFFLOAD),
        ),
        ("BALE_AUDIO_OUT_MMCSS_NONE", u64::from(MMCSS_NONE)),
        ("BALE_AUDIO_OUT_MMCSS_PRO_AUDIO", u64::from(MMCSS_PRO_AUDIO)),
        ("BALE_AUDIO_OUT_MMCSS_FAILED", u64::from(MMCSS_FAILED)),
        ("BALE_AUDIO_OUT_STATUS_STOPPED", u64::from(STATUS_STOPPED)),
        ("BALE_AUDIO_OUT_STATUS_RUNNING", u64::from(STATUS_RUNNING)),
        ("BALE_AUDIO_OUT_STATUS_PAUSED", u64::from(STATUS_PAUSED)),
        (
            "BALE_AUDIO_OUT_STATUS_DEVICE_LOST",
            u64::from(STATUS_DEVICE_LOST),
        ),
        ("BALE_AUDIO_OUT_STATUS_FAILED", u64::from(STATUS_FAILED)),
        ("BALE_AUDIO_OUT_WRITE_CLOSED", u64::from(WRITE_CLOSED)),
    ] {
        source.push_str(&format!("CHECK({name} == {value}u);\n"));
    }
    // i64::MIN, written so C never parses a too-large literal.
    source.push_str("CHECK(BALE_AUDIO_PTS_NONE == INT64_MIN);\n");
    assert_eq!(PTS_NONE, i64::MIN);
    let dir = std::env::temp_dir().join(format!("bale-audio-out-header-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    for (compiler, extension, standard, prefix) in [
        (
            "cc",
            "c",
            "-std=c11",
            "#define CHECK(x) _Static_assert(x, #x)\n#define ALIGN(x) _Alignof(x)\n",
        ),
        (
            "c++",
            "cpp",
            "-std=c++11",
            "#define CHECK(x) static_assert(x, #x)\n#define ALIGN(x) alignof(x)\n",
        ),
    ] {
        let path = dir.join(format!("layout.{extension}"));
        std::fs::write(&path, format!("{prefix}{source}")).unwrap();
        let result = Command::new(compiler)
            .args([standard, "-Werror", "-fsyntax-only", "-I"])
            .arg(concat!(env!("CARGO_MANIFEST_DIR"), "/include"))
            .arg(&path)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "header layout: {}",
            String::from_utf8_lossy(&result.stderr)
        );
    }
    std::fs::remove_dir_all(dir).unwrap();
}
