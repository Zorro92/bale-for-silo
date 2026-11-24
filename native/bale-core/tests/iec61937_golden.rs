//! Bale's IEC 61937 packers against FFmpeg's `spdif` muxer, byte for byte. Each fixture in
//! tests/fixtures/iec61937 holds an elementary stream, the access-unit sizes FFmpeg's demuxer split it
//! into (ffprobe) and the muxer's output for it; its README says how they were made.
//!
//! FFmpeg drops a partly filled E-AC-3 burst or MAT frame at the end of the stream, so the comparison
//! covers what `push` returns; `flush` is checked separately.

use std::path::PathBuf;

use bale_core::iec61937::{
    Ac3Packer, Burst, Carrier, DtsHdLink, DtsHdPacker, DtsPacker, Eac3Packer, Packer, Subtype,
    TrueHdPacker,
};

fn fixture(name: &str) -> Vec<u8> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures/iec61937")
        .join(name);
    std::fs::read(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()))
}

/// The stream cut into the access units FFmpeg's demuxer delivered.
fn units(stream: &str) -> Vec<Vec<u8>> {
    let data = fixture(stream);
    let stem = stream
        .rsplit_once('.')
        .expect("stream name has an extension")
        .0;
    let sizes = String::from_utf8(fixture(&format!("{stem}.sizes"))).expect("sizes are text");
    let mut rest = data.as_slice();
    let units = sizes
        .lines()
        .map(|line| {
            let (unit, tail) = rest.split_at(line.parse().expect("a size per line"));
            rest = tail;
            unit.to_vec()
        })
        .collect();
    assert!(rest.is_empty(), "{stream}: sizes don't cover the stream");
    units
}

/// Packs the stream's units, asserts the bursts equal FFmpeg's output and that each is a whole
/// repetition period of `carrier` lasting `frames` carrier frames, and returns them.
fn check(
    stream: &str,
    expected: &str,
    packer: &mut dyn Packer,
    carrier: Carrier,
    frames: u32,
) -> Vec<Burst> {
    let mut bursts = Vec::new();
    for (index, unit) in units(stream).iter().enumerate() {
        bursts.extend(
            packer
                .push(unit)
                .unwrap_or_else(|error| panic!("{stream} unit {index}: {error}")),
        );
    }
    let actual: Vec<u8> = bursts
        .iter()
        .flat_map(|burst| burst.data.iter().copied())
        .collect();
    let expected_bytes = fixture(expected);
    if actual != expected_bytes {
        let at = actual
            .iter()
            .zip(&expected_bytes)
            .position(|(a, b)| a != b)
            .unwrap_or(actual.len().min(expected_bytes.len()));
        panic!(
            "{expected}: packed {} bytes, FFmpeg wrote {}; first difference at byte {at}",
            actual.len(),
            expected_bytes.len()
        );
    }
    assert!(!bursts.is_empty(), "{expected}: no bursts");
    for burst in &bursts {
        assert_eq!(burst.carrier, carrier, "{expected}");
        assert_eq!(burst.frames, frames, "{expected}");
        assert_eq!(
            burst.data.len(),
            frames as usize * carrier.block_align(),
            "{expected}"
        );
    }
    assert_eq!(packer.carrier(), Some(carrier), "{expected}");
    bursts
}

fn carrier(subtype: Subtype, channels: u16, sample_rate: u32) -> Carrier {
    Carrier {
        subtype,
        channels,
        sample_rate,
        bits_per_sample: 16,
    }
}

#[test]
fn ac3_matches_ffmpeg() {
    // 5.1 at 48 kHz with bsmod 3 (hearing impaired) in Pc.
    let ac3 = carrier(Subtype::DolbyDigital, 2, 48_000);
    let bursts = check(
        "ac3-48k-5.1.ac3",
        "ac3-48k-5.1.spdif",
        &mut Ac3Packer::new(),
        ac3,
        1536,
    );
    assert_eq!(&bursts[0].data[4..6], &[0x01, 0x03]);
    assert_eq!(bursts[0].seconds(), 0.032);
    // 44.1 kHz frames alternate between two sizes.
    let ac3 = carrier(Subtype::DolbyDigital, 2, 44_100);
    check(
        "ac3-44k-stereo.ac3",
        "ac3-44k-stereo.spdif",
        &mut Ac3Packer::new(),
        ac3,
        1536,
    );
}

#[test]
fn eac3_matches_ffmpeg() {
    let eac3 = carrier(Subtype::DolbyDigitalPlus, 2, 192_000);
    check(
        "eac3-48k-5.1.eac3",
        "eac3-48k-5.1.spdif",
        &mut Eac3Packer::new(),
        eac3,
        6144,
    );
    // Six 1-block units per burst, each an independent 5.1 frame and a dependent 7.1 frame.
    let bursts = check(
        "eac3-1block-7.1-synthetic.eac3",
        "eac3-1block-7.1-synthetic.spdif",
        &mut Eac3Packer::new(),
        eac3,
        6144,
    );
    assert_eq!(bursts.len(), 3);
}

#[test]
fn eac3_flush_sends_the_last_blocks() {
    // Seven 3-block units: three bursts of two, then one unit left that FFmpeg drops at the end.
    let eac3 = carrier(Subtype::DolbyDigitalPlus, 2, 192_000);
    let mut packer = Eac3Packer::new();
    let bursts = check(
        "eac3-3block-synthetic.eac3",
        "eac3-3block-synthetic.spdif",
        &mut packer,
        eac3,
        6144,
    );
    assert_eq!(bursts.len(), 3);
    let last = units("eac3-3block-synthetic.eac3").pop().unwrap();
    let flushed = packer.flush();
    assert_eq!(flushed.len(), 1);
    let data = &flushed[0].data;
    assert_eq!(data.len(), 24576);
    assert_eq!(
        &data[..8],
        &[
            0x72,
            0xF8,
            0x1F,
            0x4E,
            0x15,
            0x00,
            last.len() as u8,
            (last.len() >> 8) as u8
        ]
    );
    assert_eq!(&data[8..10], &[last[1], last[0]]);
    assert!(packer.flush().is_empty());
}

#[test]
fn dts_core_matches_ffmpeg() {
    let dts48 = carrier(Subtype::Dts, 2, 48_000);
    check(
        "dts-48k-5.1.dts",
        "dts-48k-5.1.spdif",
        &mut DtsPacker::new(),
        dts48,
        512,
    );
    // 2048-byte frames fill their 512-sample period: sent without a preamble.
    let dts44 = carrier(Subtype::Dts, 2, 44_100);
    let bursts = check(
        "dts-44k-stereo.dts",
        "dts-44k-stereo.spdif",
        &mut DtsPacker::new(),
        dts44,
        512,
    );
    assert_eq!(&bursts[0].data[..4], &[0xFE, 0x7F, 0x01, 0x80]);
    // Types II and III; one 1024-sample frame has an odd length.
    check(
        "dts-1024-synthetic.dts",
        "dts-1024-synthetic.spdif",
        &mut DtsPacker::new(),
        dts48,
        1024,
    );
    check(
        "dts-2048-synthetic.dts",
        "dts-2048-synthetic.spdif",
        &mut DtsPacker::new(),
        dts48,
        2048,
    );
}

#[test]
fn dts_packings_match_ffmpeg() {
    let dts = carrier(Subtype::Dts, 2, 48_000);
    for stream in ["dts-le16", "dts-be14", "dts-le14"] {
        check(
            &format!("{stream}.dts"),
            &format!("{stream}.spdif"),
            &mut DtsPacker::new(),
            dts,
            512,
        );
    }
}

#[test]
fn dts_core_drops_the_extension_substream() {
    let dts = carrier(Subtype::Dts, 2, 48_000);
    check(
        "dtshd-synthetic.dts",
        "dtshd-synthetic.core.spdif",
        &mut DtsPacker::new(),
        dts,
        512,
    );
}

#[test]
fn dts_hd_matches_ffmpeg() {
    // -dtshd_rate 768000: every frame fits HBR.
    let hbr = carrier(Subtype::DtsHd, 8, 192_000);
    let mut packer = DtsHdPacker::new(DtsHdLink::HighBitrate);
    let bursts = check(
        "dtshd-synthetic.dts",
        "dtshd-synthetic.hbr.spdif",
        &mut packer,
        hbr,
        2048,
    );
    let units = units("dtshd-synthetic.dts");
    for (burst, unit) in bursts.iter().zip(&units) {
        // Pc: type IV, repetition subtype 4 (8192 IEC 60958 frames); Pd % 16 == 8.
        assert_eq!(&burst.data[4..6], &[0x11, 0x04]);
        let pd = u16::from_le_bytes([burst.data[6], burst.data[7]]) as usize;
        assert_eq!(pd % 16, 8);
        assert!(pd >= 12 + unit.len());
    }
    // -dtshd_rate 192000: the sixth frame doesn't fit, so it and the rest go core-only.
    let stereo = carrier(Subtype::DtsHd, 2, 192_000);
    let mut packer = DtsHdPacker::new(DtsHdLink::Stereo);
    let bursts = check(
        "dtshd-synthetic.dts",
        "dtshd-synthetic.stereo.spdif",
        &mut packer,
        stereo,
        2048,
    );
    let core = 1884;
    for (index, (burst, unit)) in bursts.iter().zip(&units).enumerate() {
        // The payload's 11th and 12th bytes (its length) are the burst's tenth 16-bit word.
        let size = u16::from_le_bytes([burst.data[18], burst.data[19]]) as usize;
        assert_eq!(
            size,
            if index < 5 { unit.len() } else { core },
            "frame {index}"
        );
    }
}

#[test]
fn truehd_matches_ffmpeg() {
    let mat192 = carrier(Subtype::DolbyMlp, 8, 192_000);
    check(
        "truehd-48k-5.1.thd",
        "truehd-48k-5.1.spdif",
        &mut TrueHdPacker::new(),
        mat192,
        3840,
    );
    // 80 samples per access unit at 96 kHz: the same 1/1200 s.
    check(
        "truehd-96k-stereo.thd",
        "truehd-96k-stereo.spdif",
        &mut TrueHdPacker::new(),
        mat192,
        3840,
    );
    // MLP's major sync (0xBB) keeps its rate one byte later.
    check(
        "mlp-48k-stereo.mlp",
        "mlp-48k-stereo.spdif",
        &mut TrueHdPacker::new(),
        mat192,
        3840,
    );
}

#[test]
fn truehd_timing_jumps_match_ffmpeg() {
    // A 10-unit gap (padded), a 40-unit gap and a step back (both discontinuities: no padding).
    let mat192 = carrier(Subtype::DolbyMlp, 8, 192_000);
    check(
        "truehd-timing-synthetic.thd",
        "truehd-timing-synthetic.spdif",
        &mut TrueHdPacker::new(),
        mat192,
        3840,
    );
}

#[test]
fn truehd_flush_completes_the_mat_frame() {
    let mat192 = carrier(Subtype::DolbyMlp, 8, 192_000);
    let mut packer = TrueHdPacker::new();
    let bursts = check(
        "truehd-48k-5.1.thd",
        "truehd-48k-5.1.spdif",
        &mut packer,
        mat192,
        3840,
    );
    let flushed = packer.flush();
    assert_eq!(flushed.len(), 1);
    let (last, full) = (&flushed[0].data, &bursts[0].data);
    // Same preamble and codes as a full frame; the stuffing after the end code stays zero.
    assert_eq!(&last[..28], &full[..28]);
    assert_eq!(&last[30716..30728], &full[30716..30728]);
    assert_eq!(&last[61416..61440], &full[61416..61440]);
    assert!(packer.flush().is_empty());
}
