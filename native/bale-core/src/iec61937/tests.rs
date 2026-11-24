//! Packer tests that need no fixtures: layouts, edge cases and errors. Byte-for-byte agreement with
//! FFmpeg's muxer is tests/iec61937_golden.rs.

use super::*;

/// Undoes the carrier's byte swap: the burst's words as the big-endian bytes they carry.
fn words(burst: &Burst) -> Vec<u8> {
    burst
        .data
        .as_chunks::<2>()
        .0
        .iter()
        .flat_map(|&[low, high]| [high, low])
        .collect()
}

fn preamble(burst: &Burst) -> [u16; 4] {
    let w = |i: usize| u16::from_le_bytes([burst.data[2 * i], burst.data[2 * i + 1]]);
    [w(0), w(1), w(2), w(3)]
}

// --- Shared ---------------------------------------------------------------------------------------

#[test]
fn subtypes_name_the_ksmedia_guids() {
    assert_eq!(
        Subtype::DolbyDigital.name(),
        "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL"
    );
    assert_eq!(
        Subtype::DolbyDigitalPlus.name(),
        "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS"
    );
    assert_eq!(Subtype::Dts.name(), "KSDATAFORMAT_SUBTYPE_IEC61937_DTS");
    assert_eq!(
        Subtype::DtsHd.name(),
        "KSDATAFORMAT_SUBTYPE_IEC61937_DTS_HD"
    );
    assert_eq!(
        Subtype::DolbyMlp.name(),
        "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_MLP"
    );
    // WAVE_FORMAT_DOLBY_AC3_SPDIF and WAVE_FORMAT_DTS in the WAVEFORMATEX-tag pattern; the newer ones
    // in the 0CEA pattern.
    assert_eq!(
        Subtype::DolbyDigital.guid(),
        0x00000092_0000_0010_8000_00aa00389b71
    );
    assert_eq!(Subtype::Dts.guid(), 0x00000008_0000_0010_8000_00aa00389b71);
    assert_eq!(Subtype::DolbyDigitalPlus.guid() >> 96, 0x0a);
    assert_eq!(Subtype::DtsHd.guid() >> 96, 0x0b);
    assert_eq!(Subtype::DolbyMlp.guid() >> 96, 0x0c);
}

#[test]
fn formats_give_their_carriers() {
    let c = |format: Format, rate| {
        format
            .carrier(rate)
            .map(|c| (c.subtype, c.channels, c.sample_rate))
    };
    assert_eq!(
        c(Format::Ac3, 48_000),
        Some((Subtype::DolbyDigital, 2, 48_000))
    );
    assert_eq!(
        c(Format::Ac3, 44_100),
        Some((Subtype::DolbyDigital, 2, 44_100))
    );
    assert_eq!(
        c(Format::Eac3, 48_000),
        Some((Subtype::DolbyDigitalPlus, 2, 192_000))
    );
    assert_eq!(
        c(Format::Eac3, 32_000),
        Some((Subtype::DolbyDigitalPlus, 2, 128_000))
    );
    assert_eq!(c(Format::Dts, 48_000), Some((Subtype::Dts, 2, 48_000)));
    assert_eq!(c(Format::DtsHd, 48_000), Some((Subtype::DtsHd, 8, 192_000)));
    assert_eq!(c(Format::DtsHd, 44_100), Some((Subtype::DtsHd, 8, 176_400)));
    assert_eq!(
        c(Format::TrueHd, 96_000),
        Some((Subtype::DolbyMlp, 8, 192_000))
    );
    assert_eq!(
        c(Format::TrueHd, 44_100),
        Some((Subtype::DolbyMlp, 8, 176_400))
    );
    for format in [
        Format::Ac3,
        Format::Eac3,
        Format::Dts,
        Format::DtsHd,
        Format::TrueHd,
    ] {
        assert_eq!(format.carrier(24_000), None);
        assert_eq!(format.packer().carrier(), None);
    }
    let hbr = Format::DtsHd.carrier(48_000).unwrap();
    assert_eq!(
        (
            hbr.bits_per_sample,
            hbr.block_align(),
            hbr.bytes_per_second()
        ),
        (16, 16, 3_072_000)
    );
}

#[test]
fn bursts_swap_words_and_stuff_zeros() {
    let carrier = Carrier::new(Subtype::Dts, 2, 48_000);
    let preamble = Some(Preamble { pc: 0x0B, pd: 24 });
    let burst = burst(carrier, preamble, &[1, 2, 3], Words::BigEndian, 16).unwrap();
    // Pa Pb Pc Pd little-endian, the pair swapped, the odd byte as a word's high byte, then zeros.
    assert_eq!(
        burst.data,
        [
            0x72, 0xF8, 0x1F, 0x4E, 0x0B, 0, 24, 0, 2, 1, 0, 3, 0, 0, 0, 0
        ]
    );
    assert_eq!((burst.frames, burst.carrier), (4, carrier));
    let le = super::burst(carrier, None, &[1, 2, 3, 4], Words::LittleEndian, 8).unwrap();
    assert_eq!(le.data, [1, 2, 3, 4, 0, 0, 0, 0]);
    // A final odd byte needs its whole word.
    assert_eq!(
        super::burst(carrier, preamble, &[1; 9], Words::BigEndian, 16),
        Err(Error::TooLarge)
    );
    assert!(super::burst(carrier, preamble, &[1; 8], Words::BigEndian, 16).is_ok());
}

// --- AC-3 and E-AC-3 ------------------------------------------------------------------------------

fn ac3_frame(fscod: u8, frmsizecod: u8, bsmod: u8, bytes: usize) -> Vec<u8> {
    let mut frame = vec![0x5A; bytes];
    frame[..2].copy_from_slice(&[0x0B, 0x77]);
    frame[4] = fscod << 6 | frmsizecod;
    frame[5] = 8 << 3 | bsmod;
    frame
}

fn eac3_frame(strmtyp: u8, substreamid: u8, bytes: usize, fscod: u8, numblkscod: u8) -> Vec<u8> {
    let mut frame = vec![0x5A; bytes];
    let frmsiz = bytes / 2 - 1;
    frame[..2].copy_from_slice(&[0x0B, 0x77]);
    frame[2] = strmtyp << 6 | substreamid << 3 | (frmsiz >> 8) as u8;
    frame[3] = frmsiz as u8;
    frame[4] = fscod << 6 | numblkscod << 4 | 0x0F;
    frame[5] = 16 << 3;
    frame
}

#[test]
fn ac3_frame_sizes_follow_the_table() {
    // (fscod, frmsizecod, bytes) from ATSC A/52 Table 5.18.
    let sizes = [
        (0, 0, 128),
        (0, 28, 1536),
        (0, 37, 2560),
        (1, 0, 138),
        (1, 1, 140),
        (1, 29, 1672),
        (1, 36, 2786),
        (1, 37, 2788),
        (2, 0, 192),
        (2, 37, 3840),
    ];
    for (fscod, frmsizecod, bytes) in sizes {
        let mut packer = Ac3Packer::new();
        let bursts = packer
            .push(&ac3_frame(fscod, frmsizecod, 2, bytes))
            .unwrap();
        assert_eq!(bursts.len(), 1);
        let [pa, pb, pc, pd] = preamble(&bursts[0]);
        assert_eq!((pa, pb, pc), (0xF872, 0x4E1F, 0x0201));
        assert_eq!(
            usize::from(pd),
            bytes * 8,
            "fscod {fscod} frmsizecod {frmsizecod}"
        );
        assert_eq!((bursts[0].data.len(), bursts[0].frames), (6144, 1536));
        let rate = [48_000, 44_100, 32_000][usize::from(fscod)];
        assert_eq!(
            packer.carrier(),
            Some(Carrier::new(Subtype::DolbyDigital, 2, rate))
        );
        // One byte short of the header's size.
        assert_eq!(
            packer.push(&ac3_frame(fscod, frmsizecod, 2, bytes)[..bytes - 1]),
            Err(Error::Truncated)
        );
    }
}

#[test]
fn ac3_splits_units_and_drops_trailing_bytes() {
    let frame = ac3_frame(0, 8, 0, 256);
    let mut unit = [frame.clone(), frame.clone(), vec![1, 2, 3]].concat();
    assert_eq!(Ac3Packer::new().push(&unit).unwrap().len(), 2);
    unit.truncate(256 + 100);
    let bursts = Ac3Packer::new().push(&unit).unwrap();
    assert_eq!(bursts.len(), 1);
    assert_eq!(preamble(&bursts[0])[3], 256 * 8);
}

#[test]
fn ac3_rejects_what_it_cannot_carry() {
    let mut packer = Ac3Packer::new();
    assert_eq!(packer.push(&[]), Err(Error::Truncated));
    assert_eq!(packer.push(&[0x0B]), Err(Error::Truncated));
    assert_eq!(packer.push(&[0x0B, 0x77, 0, 0]), Err(Error::Truncated));
    assert_eq!(packer.push(&[0x77, 0x0B, 0, 0, 0, 0x40]), Err(Error::Sync));
    assert_eq!(packer.push(&ac3_frame(3, 0, 0, 128)), Err(Error::Header));
    assert_eq!(packer.push(&ac3_frame(0, 38, 0, 128)), Err(Error::Header));
    let mut half_rate = ac3_frame(0, 0, 0, 128);
    half_rate[5] = 9 << 3;
    assert_eq!(packer.push(&half_rate), Err(Error::Unsupported));
    assert_eq!(
        packer.push(&eac3_frame(0, 0, 512, 0, 3)),
        Err(Error::Unsupported)
    );
    assert_eq!(packer.carrier(), None);
}

#[test]
fn eac3_fills_a_burst_with_program_zero_blocks() {
    // 2-block frames: three units a burst.
    let mut packer = Eac3Packer::new();
    let unit = eac3_frame(0, 0, 300, 0, 1);
    assert!(packer.push(&unit).unwrap().is_empty());
    assert!(packer.push(&unit).unwrap().is_empty());
    let bursts = packer.push(&unit).unwrap();
    assert_eq!(bursts.len(), 1);
    assert_eq!(preamble(&bursts[0])[2..], [21, 900]);
    assert_eq!((bursts[0].data.len(), bursts[0].frames), (24576, 6144));

    // Dependent frames and another program's frames ride along without moving time.
    let mut packer = Eac3Packer::new();
    let six = eac3_frame(0, 0, 400, 0, 3);
    assert!(
        packer
            .push(&eac3_frame(1, 0, 100, 0, 3))
            .unwrap()
            .is_empty()
    );
    assert!(
        packer
            .push(&eac3_frame(0, 1, 100, 0, 3))
            .unwrap()
            .is_empty()
    );
    let bursts = packer
        .push(&[six.clone(), eac3_frame(1, 0, 200, 0, 3)].concat())
        .unwrap();
    assert_eq!(preamble(&bursts[0])[3], 100 + 100 + 400 + 200);

    // AC-3 + E-AC-3 dependent (7.1 with an AC-3 core): the AC-3 frame's six blocks.
    let mut packer = Eac3Packer::new();
    let unit = [ac3_frame(0, 28, 0, 1536), eac3_frame(1, 0, 256, 0, 3)].concat();
    assert_eq!(packer.push(&unit).unwrap().len(), 1);
}

#[test]
fn eac3_reset_flush_and_limits() {
    let mut packer = Eac3Packer::new();
    let unit = eac3_frame(0, 0, 200, 1, 0);
    packer.push(&unit).unwrap();
    assert_eq!(
        packer.carrier(),
        Some(Carrier::new(Subtype::DolbyDigitalPlus, 2, 176_400))
    );
    packer.reset();
    assert!(packer.flush().is_empty());
    for _ in 0..5 {
        assert!(packer.push(&unit).unwrap().is_empty());
    }
    let flushed = packer.flush();
    assert_eq!(preamble(&flushed[0])[3], 1000);
    assert!(packer.flush().is_empty());

    // Over the period: the burst being filled goes too.
    let big = eac3_frame(0, 0, 4096, 0, 0);
    for _ in 0..5 {
        packer.push(&big).unwrap();
    }
    assert_eq!(packer.push(&big), Err(Error::TooLarge));
    assert!(packer.flush().is_empty());

    assert_eq!(
        packer.push(&eac3_frame(0, 0, 200, 3, 0)),
        Err(Error::Unsupported)
    );
    assert_eq!(
        packer.push(&eac3_frame(0, 0, 200, 3, 3)),
        Err(Error::Header)
    );
    assert_eq!(
        packer.push(&eac3_frame(3, 0, 200, 0, 0)),
        Err(Error::Header)
    );
    let mut odd_bsid = eac3_frame(0, 0, 200, 0, 0);
    odd_bsid[5] = 17 << 3;
    assert_eq!(packer.push(&odd_bsid), Err(Error::Header));
}

// --- DTS ------------------------------------------------------------------------------------------

/// A 16-bit big-endian core frame: stereo, `sfreq` code, filler after the header.
fn dts_frame(samples: usize, bytes: usize, sfreq: u8) -> Vec<u8> {
    let (nblks, fsize, amode) = (samples / 32 - 1, bytes - 1, 2u8);
    let mut frame = vec![0x5A; bytes];
    frame[..4].copy_from_slice(&[0x7F, 0xFE, 0x80, 0x01]);
    frame[4] = 0xFC | (nblks >> 6) as u8;
    frame[5] = ((nblks & 0x3F) << 2) as u8 | (fsize >> 12) as u8;
    frame[6] = (fsize >> 4) as u8;
    frame[7] = ((fsize & 0x0F) << 4) as u8 | amode >> 2;
    frame[8] = (amode & 0x03) << 6 | sfreq << 2;
    frame
}

fn repack_14(frame: &[u8], little_endian: bool) -> Vec<u8> {
    let bits: Vec<bool> = frame
        .iter()
        .flat_map(|byte| (0..8).rev().map(move |i| byte >> i & 1 == 1))
        .collect();
    bits.chunks(14)
        .flat_map(|chunk| {
            let mut word = chunk
                .iter()
                .fold(0u16, |word, &bit| word << 1 | u16::from(bit))
                << (14 - chunk.len());
            if word & 0x2000 != 0 {
                word |= 0xC000;
            }
            if little_endian {
                word.to_le_bytes()
            } else {
                word.to_be_bytes()
            }
        })
        .collect()
}

#[test]
fn dts_picks_the_burst_type_by_samples() {
    for (samples, data_type) in [(512, 11), (1024, 12), (2048, 13)] {
        let mut packer = DtsPacker::new();
        let bursts = packer.push(&dts_frame(samples, 1000, 13)).unwrap();
        assert_eq!(preamble(&bursts[0])[2..], [data_type, 8000]);
        assert_eq!(
            (bursts[0].data.len(), bursts[0].frames),
            (samples * 4, samples as u32)
        );
        assert_eq!(
            packer.carrier(),
            Some(Carrier::new(Subtype::Dts, 2, 48_000))
        );
    }
    assert_eq!(
        DtsPacker::new().push(&dts_frame(256, 1000, 13)),
        Err(Error::Unsupported)
    );
}

#[test]
fn dts_reads_every_packing() {
    let frame = dts_frame(1024, 1000, 8);
    let le16: Vec<u8> = frame
        .as_chunks::<2>()
        .0
        .iter()
        .flat_map(|&[a, b]| [b, a])
        .collect();
    let (sync16, sync14) = ([0xFE, 0x7F, 0x01, 0x80], [0xFF, 0x1F, 0x00, 0xE8]);
    for (unit, packing, sync) in [
        (frame.clone(), "be16", sync16),
        (le16, "le16", sync16),
        (repack_14(&frame, false), "be14", sync14),
        (repack_14(&frame, true), "le14", sync14),
    ] {
        let core = dts::Core::parse(&unit).unwrap();
        assert_eq!(
            (core.samples, core.sample_rate, core.frame_bytes),
            (1024, 44_100, 1000),
            "{packing}"
        );
        let bursts = DtsPacker::new().push(&unit).unwrap();
        // Little-endian packings go out as they are and big-endian ones swapped, so both packings of a
        // word size give the same carrier bytes.
        assert_eq!(&bursts[0].data[8..12], &sync, "{packing}");
    }
}

#[test]
fn dts_rejects_what_it_cannot_carry() {
    let mut packer = DtsPacker::new();
    assert_eq!(
        packer.push(&[0x64, 0x58, 0x20, 0x25, 0, 0, 0, 0, 0, 0]),
        Err(Error::MissingCore)
    );
    assert_eq!(packer.push(&[0x7F, 0xFE, 0x80]), Err(Error::Truncated));
    assert_eq!(
        packer.push(&dts_frame(512, 1000, 13)[..8]),
        Err(Error::Truncated)
    );
    assert_eq!(
        packer.push(&dts_frame(512, 1000, 13)[..999]),
        Err(Error::Truncated)
    );
    assert_eq!(
        packer.push(&[0x0B, 0x77, 0, 0, 0, 0, 0, 0, 0, 0]),
        Err(Error::Sync)
    );
    assert_eq!(packer.push(&dts_frame(512, 95, 13)), Err(Error::Header));
    assert_eq!(packer.push(&dts_frame(512, 1000, 4)), Err(Error::Header));
    assert_eq!(
        packer.push(&dts_frame(512, 1000, 12)),
        Err(Error::Unsupported)
    );
    // 2041 bytes don't fit 2048 with the preamble; 2048 exactly go without one.
    assert_eq!(packer.push(&dts_frame(512, 2041, 13)), Err(Error::TooLarge));
    let bursts = packer.push(&dts_frame(512, 2048, 13)).unwrap();
    assert_eq!(&bursts[0].data[..4], &[0xFE, 0x7F, 0x01, 0x80]);
}

fn dts_hd_unit(core: &[u8], extension: usize) -> Vec<u8> {
    let mut exss = vec![0x5A; extension];
    exss[..4].copy_from_slice(&[0x64, 0x58, 0x20, 0x25]);
    [core, &exss].concat()
}

#[test]
fn dts_hd_period_follows_the_link() {
    // (core samples, link, repetition subtype, carrier frames)
    let cases = [
        (512, DtsHdLink::HighBitrate, 4, 2048),
        (1024, DtsHdLink::HighBitrate, 5, 4096),
        (512, DtsHdLink::Stereo, 2, 2048),
        (1024, DtsHdLink::Stereo, 3, 4096),
        (2048, DtsHdLink::Stereo, 4, 8192),
    ];
    for (samples, link, subtype, frames) in cases {
        let unit = dts_hd_unit(&dts_frame(samples, 1000, 13), 500);
        let mut packer = DtsHdPacker::new(link);
        let bursts = packer.push(&unit).unwrap();
        let [_, _, pc, pd] = preamble(&bursts[0]);
        assert_eq!(pc, 17 | subtype << 8, "{samples} {link:?}");
        assert_eq!(pd, 1512, "12 + 1500, already 8 past a multiple of 16");
        assert_eq!(bursts[0].frames, frames);
        let payload = words(&bursts[0]);
        assert_eq!(
            &payload[8..20],
            &[1, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0xFE, 0x05, 0xDC]
        );
        assert_eq!(&payload[20..20 + unit.len()], &unit[..]);
    }
    let unit = dts_hd_unit(&dts_frame(2048, 1000, 13), 500);
    assert_eq!(
        DtsHdPacker::new(DtsHdLink::HighBitrate).push(&unit),
        Err(Error::Unsupported)
    );
}

#[test]
fn dts_hd_falls_back_to_the_core_for_a_minute() {
    let core = dts_frame(512, 1000, 13);
    let (big, small) = (dts_hd_unit(&core, 7500), dts_hd_unit(&core, 500));
    let mut packer = DtsHdPacker::new(DtsHdLink::Stereo);
    // The DTS-HD header's length word, the burst's tenth 16-bit sample.
    let length = |bursts: Vec<Burst>| u16::from_le_bytes([bursts[0].data[18], bursts[0].data[19]]);
    assert_eq!(length(packer.push(&small).unwrap()), 1500);
    // 48000 * 60 / 512 = 5625 frames core-only, the overflowing one included.
    assert_eq!(length(packer.push(&big).unwrap()), 1000);
    packer.reset();
    for _ in 1..5625 {
        assert_eq!(length(packer.push(&small).unwrap()), 1000);
    }
    assert_eq!(length(packer.push(&small).unwrap()), 1500);
    // Over HBR the same frame fits.
    assert_eq!(
        length(DtsHdPacker::new(DtsHdLink::HighBitrate).push(&big).unwrap()),
        8500
    );
}

#[test]
fn dts_hd_rejects_what_it_cannot_carry() {
    let mut packer = DtsHdPacker::new(DtsHdLink::HighBitrate);
    let core = dts_frame(512, 1000, 13);
    assert_eq!(
        packer.push(&dts_hd_unit(&core, 500)[1000..]),
        Err(Error::MissingCore)
    );
    assert_eq!(packer.push(&core[..999]), Err(Error::Truncated));
    let le16: Vec<u8> = core
        .as_chunks::<2>()
        .0
        .iter()
        .flat_map(|&[a, b]| [b, a])
        .collect();
    assert_eq!(packer.push(&le16), Err(Error::Unsupported));
    assert_eq!(
        packer.push(&repack_14(&core, false)),
        Err(Error::Unsupported)
    );
    assert_eq!(packer.carrier(), None);
    // A core alone is fine.
    assert_eq!(packer.push(&core).unwrap().len(), 1);
    assert_eq!(
        packer.carrier(),
        Some(Carrier::new(Subtype::DtsHd, 8, 192_000))
    );
}

// --- TrueHD / MLP ---------------------------------------------------------------------------------

/// An access unit of `bytes` filled with `fill`; `major` adds a TrueHD (0xBA) or MLP (0xBB) major sync
/// with that sampling-frequency code.
fn truehd_unit(bytes: usize, timing: u16, major: Option<(u8, u8)>, fill: u8) -> Vec<u8> {
    let mut unit = vec![fill; bytes];
    unit[0] = 0xF0 | ((bytes / 2) >> 8) as u8;
    unit[1] = (bytes / 2) as u8;
    unit[2..4].copy_from_slice(&timing.to_be_bytes());
    if let Some((kind, rate)) = major {
        unit[4..8].copy_from_slice(&[0xF8, 0x72, 0x6F, kind]);
        unit[if kind == 0xBA { 8 } else { 9 }] = rate << 4;
    }
    unit
}

#[test]
fn truehd_lands_each_unit_on_its_time() {
    // 100-byte units 1/1200 s apart: unit n starts 2560 * n bytes into the MAT data (the first one
    // after the start code), the codes inside the padding.
    let mut packer = TrueHdPacker::new();
    let mut bursts = Vec::new();
    for n in 0..25u8 {
        let major = (n % 16 == 0).then_some((0xBA, 0));
        bursts.extend(
            packer
                .push(&truehd_unit(100, u16::from(n) * 40, major, n + 1))
                .unwrap(),
        );
        assert_eq!(bursts.len(), usize::from(n == 24), "unit {n}");
    }
    let burst = &bursts[0];
    assert_eq!(preamble(burst)[2..], [22, 61424]);
    assert_eq!(
        (burst.data.len(), burst.frames, burst.carrier.channels),
        (61440, 3840, 8)
    );
    let data = &words(burst)[8..61432];
    assert_eq!(&data[..20], &mat::START_CODE);
    assert_eq!(&data[30708..30720], &mat::MIDDLE_CODE);
    assert_eq!(&data[61408..], &mat::END_CODE);
    assert_eq!(&words(burst)[61432..], &[0; 8]);
    for n in 0..24 {
        let at = if n == 0 { 20 } else { 2560 * n };
        // Past the header and, on units 0 and 16, the major sync.
        assert_eq!(&data[at + 10..at + 100], &[n as u8 + 1; 90][..], "unit {n}");
        assert!(
            data[at + 100..at + 2460.min(61408 - at)]
                .iter()
                .all(|&b| b == 0),
            "padding after unit {n}"
        );
    }
}

#[test]
fn truehd_treats_jumps_as_discontinuities() {
    for jump in [4000u16, 65496] {
        let mut packer = TrueHdPacker::new();
        packer
            .push(&truehd_unit(100, 0, Some((0xBA, 0)), 1))
            .unwrap();
        packer.push(&truehd_unit(100, jump, None, 2)).unwrap();
        let data = words(&packer.flush()[0]);
        // No padding: the second unit follows the first at once.
        assert_eq!(&data[8 + 120 + 4..8 + 220], &[2; 96][..], "jump {jump}");
    }
}

#[test]
fn truehd_splits_units_and_resets() {
    let units: Vec<Vec<u8>> = (0..30u8)
        .map(|n| {
            truehd_unit(
                200 + 2 * usize::from(n),
                u16::from(n) * 40,
                (n == 0).then_some((0xBA, 0)),
                n,
            )
        })
        .collect();
    let mut separate = TrueHdPacker::new();
    let one_by_one: Vec<Burst> = units
        .iter()
        .flat_map(|unit| separate.push(unit).unwrap())
        .collect();
    let mut joined = TrueHdPacker::new();
    let mut together = joined.push(&units[..10].concat()).unwrap();
    together.extend(
        joined
            .push(&[units[10..].concat(), vec![0xF0]].concat())
            .unwrap(),
    );
    assert_eq!(together, one_by_one);
    assert_eq!(joined.flush(), separate.flush());

    // After a reset the next unit opens a new frame right after the start code.
    separate.push(&units[0]).unwrap();
    separate.reset();
    separate.push(&units[5]).unwrap();
    let data = words(&separate.flush()[0]);
    assert_eq!(&data[8 + 20..8 + 20 + units[5].len()], &units[5][..]);
    assert!(separate.flush().is_empty());
}

#[test]
fn truehd_needs_a_major_sync() {
    let mut packer = TrueHdPacker::new();
    assert_eq!(
        packer.push(&truehd_unit(100, 0, None, 1)),
        Err(Error::AwaitingMajorSync)
    );
    assert_eq!(packer.carrier(), None);
    // A rejected batch changes nothing.
    let batch = [
        truehd_unit(100, 0, Some((0xBB, 9)), 1),
        truehd_unit(100, 40, Some((0xBC, 0)), 1),
    ]
    .concat();
    assert_eq!(packer.push(&batch), Err(Error::Header));
    assert_eq!(packer.carrier(), None);
    assert!(packer.flush().is_empty());
    // MLP at 88.2 kHz: the 44.1 kHz family's carrier.
    packer
        .push(&truehd_unit(100, 0, Some((0xBB, 9)), 1))
        .unwrap();
    assert_eq!(
        packer.carrier(),
        Some(Carrier::new(Subtype::DolbyMlp, 8, 176_400))
    );
    assert_eq!(
        packer.push(&truehd_unit(100, 0, Some((0xBA, 3)), 1)),
        Err(Error::Header)
    );
    let mut short = truehd_unit(8, 0, None, 1);
    short[4..].copy_from_slice(&[0xF8, 0x72, 0x6F, 0xBA]);
    assert_eq!(packer.push(&short), Err(Error::Truncated));
    assert_eq!(packer.push(&[0xF0, 0x01, 0, 0]), Err(Error::Header));
    assert_eq!(packer.push(&[0xF0, 0x40, 0, 0]), Err(Error::Truncated));
    assert_eq!(packer.push(&[0xF0]), Err(Error::Truncated));
    assert_eq!(packer.push(&[]), Err(Error::Truncated));
}

#[test]
fn bursts_report_their_duration() {
    let ac3 = Ac3Packer::new()
        .push(&ac3_frame(0, 28, 0, 1536))
        .unwrap()
        .remove(0);
    let dts = DtsHdPacker::new(DtsHdLink::HighBitrate)
        .push(&dts_frame(512, 1000, 13))
        .unwrap()
        .remove(0);
    assert_eq!((ac3.frames, ac3.carrier.sample_rate), (1536, 48_000));
    assert!((ac3.seconds() - 0.032).abs() < 1e-12);
    assert!((dts.seconds() - 512.0 / 48_000.0).abs() < 1e-12);
}
