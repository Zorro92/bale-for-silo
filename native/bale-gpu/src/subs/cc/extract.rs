//! ATSC A/53 caption data (`cc_data`) in compressed video packets, as the FFmpegInteropX tap hands
//! them over (`bale_cc_push`): one demuxed packet, H.264 or HEVC in Annex B (MPEG-TS) or with
//! big-endian length prefixes (avcC/hvcC, MP4/MKV), or MPEG-2 video.
//!
//! - H.264 and HEVC: SEI NAL units (H.264 type 6; HEVC prefix 39 and suffix 40), emulation
//!   prevention removed, carrying `user_data_registered_itu_t_t35` (payload type 4) with country
//!   0xB5 and either ATSC's provider 0x0031, user identifier "GA94" and `user_data_type_code` 3
//!   (A/53 Part 4, SCTE 128) or DirecTV's provider 0x002F with type 3 and a length byte.
//! - MPEG-2: picture user data (start code 0x000001B2) "GA94" with `user_data_type_code` 3
//!   (A/53 Part 4), before the first slice.
//!
//! Both carry the same `cc_data()`: `process_cc_data_flag`, `cc_count`, `em_data` and `cc_count`
//! triplets. The SEI walk (payload sizes, the 0xFF-extended type and size, the DirecTV variant and
//! skipping a message that claims more bytes than are left) and the user data check are ported from
//! androidx Media3's `CeaUtil.java` (Copyright (C) 2017) and `UserDataReader.java` (Copyright (C)
//! 2018 The Android Open Source Project, Apache License 2.0; changed for Bale: see
//! `native/bale-gpu/NOTICE`); the NAL framing is H.264/H.265 Annex B.
//!
//! Nothing here allocates per packet once the scratch buffer has grown, and nothing panics on any
//! input: a malformed unit is skipped.

/// `codec` values of `bale_cc_push`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Codec {
    H264,
    Hevc,
    Mpeg2,
}

impl Codec {
    /// The ABI value: 1 = H.264, 2 = HEVC, 3 = MPEG-2.
    pub fn from_abi(codec: u32) -> Option<Codec> {
        match codec {
            1 => Some(Codec::H264),
            2 => Some(Codec::Hevc),
            3 => Some(Codec::Mpeg2),
            _ => None,
        }
    }
}

/// One `cc_data` construct: `marker_bits(5) cc_valid(1) cc_type(2)`, `cc_data_1`, `cc_data_2`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Triplet(pub [u8; 3]);

impl Triplet {
    pub fn valid(self) -> bool {
        self.0[0] & 0x04 != 0
    }

    /// 0 = CEA-608 field 1, 1 = field 2, 2 = DTVCC packet data, 3 = DTVCC packet start.
    pub fn cc_type(self) -> u8 {
        self.0[0] & 0x03
    }

    pub fn data(self) -> [u8; 2] {
        [self.0[1], self.0[2]]
    }
}

/// SEI payload type of `user_data_registered_itu_t_t35`.
const PAYLOAD_TYPE_T35: usize = 4;
/// ITU-T T.35 country code of the United States.
const COUNTRY_USA: u8 = 0xB5;
const PROVIDER_ATSC: u16 = 0x0031;
const PROVIDER_DIRECTV: u16 = 0x002F;
/// A/53 `user_identifier` "GA94".
const GA94: [u8; 4] = *b"GA94";
/// A/53 `user_data_type_code` of `cc_data()`.
const TYPE_CC_DATA: u8 = 0x03;

const H264_SEI: u8 = 6;
const HEVC_PREFIX_SEI: u8 = 39;
const HEVC_SUFFIX_SEI: u8 = 40;
const MPEG2_USER_DATA: u8 = 0xB2;

/// Reusable state of the packet walk.
#[derive(Default)]
pub struct Extractor {
    /// One SEI NAL unit's RBSP (emulation prevention removed).
    rbsp: Vec<u8>,
}

impl Extractor {
    /// Appends every `cc_data` triplet in `packet` to `out`, in bitstream order. `nal_length_size`
    /// is 1 to 4 for length-prefixed H.264/HEVC and 0 for Annex B and MPEG-2; another size reads
    /// nothing.
    pub fn extract(
        &mut self,
        packet: &[u8],
        codec: Codec,
        nal_length_size: u8,
        out: &mut Vec<Triplet>,
    ) {
        match codec {
            Codec::Mpeg2 => mpeg2_user_data(packet, out),
            Codec::H264 | Codec::Hevc => {
                let hevc = codec == Codec::Hevc;
                let mut each = |nal: &[u8]| self.nal_unit(nal, hevc, out);
                match nal_length_size {
                    0 => annex_b_units(packet, &mut each),
                    1..=4 => length_prefixed_units(packet, usize::from(nal_length_size), &mut each),
                    _ => {}
                }
            }
        }
    }

    fn nal_unit(&mut self, nal: &[u8], hevc: bool, out: &mut Vec<Triplet>) {
        let (sei, header) = if hevc {
            let kind = nal.first().map_or(0, |b| (b >> 1) & 0x3F);
            (kind == HEVC_PREFIX_SEI || kind == HEVC_SUFFIX_SEI, 2)
        } else {
            (nal.first().is_some_and(|b| b & 0x1F == H264_SEI), 1)
        };
        if !sei || nal.len() <= header {
            return;
        }
        unescape(&nal[header..], &mut self.rbsp);
        sei_messages(&self.rbsp, out);
    }
}

/// Calls `each` with every NAL unit (header included) of an Annex B byte stream.
fn annex_b_units(data: &[u8], each: &mut impl FnMut(&[u8])) {
    let Some(mut start) = next_start_code(data, 0) else {
        return;
    };
    loop {
        match next_start_code(data, start) {
            Some(next) => {
                // `next` is just past the next 00 00 01; a four-byte start code's leading zero
                // (trailing_zero_8bits) belongs to neither unit.
                let mut end = next - 3;
                while end > start && data[end - 1] == 0 {
                    end -= 1;
                }
                each(&data[start..end]);
                start = next;
            }
            None => {
                each(&data[start..]);
                return;
            }
        }
    }
}

/// The index just past the first `00 00 01` at or after `from`. Steps three bytes at a time where
/// the window cannot hold one (a byte above 1 three ahead), so slice data is skimmed, not walked.
fn next_start_code(data: &[u8], from: usize) -> Option<usize> {
    let mut at = from;
    while at + 2 < data.len() {
        match data[at + 2] {
            0 => at += 1,
            1 => {
                if data[at] == 0 && data[at + 1] == 0 {
                    return Some(at + 3);
                }
                at += 3;
            }
            _ => at += 3,
        }
    }
    None
}

/// Calls `each` with every NAL unit of a length-prefixed (avcC/hvcC) sample. Stops at a length that
/// runs past the end.
fn length_prefixed_units(data: &[u8], size: usize, each: &mut impl FnMut(&[u8])) {
    let mut at = 0;
    while at + size <= data.len() {
        let length = data[at..at + size]
            .iter()
            .fold(0usize, |value, &b| (value << 8) | usize::from(b));
        at += size;
        let Some(unit) = data.get(at..at.saturating_add(length)) else {
            return;
        };
        each(unit);
        at += length;
    }
}

/// `src` with every emulation prevention byte (the 03 of 00 00 03) removed, into `dst`.
pub(crate) fn unescape(src: &[u8], dst: &mut Vec<u8>) {
    dst.clear();
    let mut zeros = 0;
    for &b in src {
        if zeros >= 2 && b == 3 {
            zeros = 0;
            continue;
        }
        dst.push(b);
        zeros = if b == 0 { zeros + 1 } else { 0 };
    }
}

/// A `payloadType`/`payloadSize` value: 0xFF bytes add 255 each, the first other byte ends it.
fn ff_coded(data: &[u8], at: &mut usize) -> Option<usize> {
    let mut value = 0usize;
    loop {
        let b = *data.get(*at)?;
        *at += 1;
        value += usize::from(b);
        if b != 0xFF {
            return Some(value);
        }
    }
}

/// Walks the SEI messages of one RBSP (Media3 `CeaUtil.consume`).
fn sei_messages(rbsp: &[u8], out: &mut Vec<Triplet>) {
    let mut at = 0;
    // The last byte is rbsp_trailing_bits.
    while rbsp.len().saturating_sub(at) > 1 {
        let (Some(kind), Some(size)) = (ff_coded(rbsp, &mut at), ff_coded(rbsp, &mut at)) else {
            return;
        };
        let Some(payload) = rbsp.get(at..at.saturating_add(size)) else {
            // A message bigger than what is left (an encrypted or cut unit): skip the rest.
            return;
        };
        if kind == PAYLOAD_TYPE_T35 && size >= 8 {
            t35_payload(payload, out);
        }
        at += size;
    }
}

/// One `user_data_registered_itu_t_t35` payload.
fn t35_payload(payload: &[u8], out: &mut Vec<Triplet>) {
    let [country, provider_hi, provider_lo, rest @ ..] = payload else {
        return;
    };
    if *country != COUNTRY_USA {
        return;
    }
    match u16::from_be_bytes([*provider_hi, *provider_lo]) {
        PROVIDER_ATSC => {
            if let [a, b, c, d, TYPE_CC_DATA, cc @ ..] = rest
                && [*a, *b, *c, *d] == GA94
            {
                cc_data(cc, out);
            }
        }
        PROVIDER_DIRECTV => {
            // user_data_type_code, then user_data_length.
            if let [TYPE_CC_DATA, _length, cc @ ..] = rest {
                cc_data(cc, out);
            }
        }
        _ => {}
    }
}

/// `cc_data()` (A/53 Part 4 Table 6.9): the triplets when `process_cc_data_flag` is set
/// (Media3 `CeaUtil.consumeCcData`). A count larger than the data takes what is there.
fn cc_data(data: &[u8], out: &mut Vec<Triplet>) {
    let [flags, _em_data, triplets @ ..] = data else {
        return;
    };
    if flags & 0x40 == 0 {
        return;
    }
    let count = usize::from(flags & 0x1F);
    out.extend(
        triplets
            .as_chunks::<3>()
            .0
            .iter()
            .take(count)
            .map(|&t| Triplet(t)),
    );
}

/// MPEG-2 video: every `user_data` (00 00 01 B2) "GA94" type 3 before the first slice.
fn mpeg2_user_data(data: &[u8], out: &mut Vec<Triplet>) {
    let mut next = next_start_code(data, 0);
    while let Some(start) = next {
        let Some(&code) = data.get(start) else {
            return;
        };
        // Slices (01..AF) end the headers; picture user data comes before them.
        if (0x01..=0xAF).contains(&code) {
            return;
        }
        next = next_start_code(data, start + 1);
        if code != MPEG2_USER_DATA {
            continue;
        }
        let end = next.map_or(data.len(), |next| next - 3);
        if let [a, b, c, d, TYPE_CC_DATA, cc @ ..] = &data[start + 1..end]
            && [*a, *b, *c, *d] == GA94
        {
            cc_data(cc, out);
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// A/53 `cc_data()` of `pairs` (field 1 when the bool is false), each valid, then `padding`
    /// invalid DTVCC triplets and the marker byte.
    pub(crate) fn cc_data_bytes(pairs: &[(bool, [u8; 2])], padding: usize) -> Vec<u8> {
        let count = pairs.len() + padding;
        let mut out = vec![0x40 | count as u8, 0xFF];
        for &(field2, [a, b]) in pairs {
            out.extend_from_slice(&[0xFC | u8::from(field2), a, b]);
        }
        for _ in 0..padding {
            out.extend_from_slice(&[0xFA, 0, 0]);
        }
        out.push(0xFF);
        out
    }

    /// The `user_data_registered_itu_t_t35` "GA94" payload around `cc`.
    pub(crate) fn ga94_payload(cc: &[u8]) -> Vec<u8> {
        let mut out = vec![COUNTRY_USA, 0x00, 0x31];
        out.extend_from_slice(b"GA94");
        out.push(TYPE_CC_DATA);
        out.extend_from_slice(cc);
        out
    }

    /// One SEI message (type 4) around `payload`, 0xFF-extending the size past 254.
    pub(crate) fn sei_message(payload: &[u8]) -> Vec<u8> {
        let mut out = vec![PAYLOAD_TYPE_T35 as u8];
        let mut size = payload.len();
        while size >= 255 {
            out.push(0xFF);
            size -= 255;
        }
        out.push(size as u8);
        out.extend_from_slice(payload);
        out
    }

    /// `rbsp` with emulation prevention bytes inserted (H.264 7.4.1).
    pub(crate) fn escape(rbsp: &[u8]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut zeros = 0;
        for &b in rbsp {
            if zeros >= 2 && b <= 3 {
                out.push(3);
                zeros = 0;
            }
            out.push(b);
            zeros = if b == 0 { zeros + 1 } else { 0 };
        }
        out
    }

    /// An H.264 SEI NAL unit (header 0x06) holding `messages`, escaped.
    pub(crate) fn h264_sei(messages: &[Vec<u8>]) -> Vec<u8> {
        let mut rbsp: Vec<u8> = messages.concat();
        rbsp.push(0x80);
        let mut nal = vec![0x06];
        nal.extend(escape(&rbsp));
        nal
    }

    /// An HEVC SEI NAL unit of `kind` (39 prefix, 40 suffix) holding `messages`, escaped.
    pub(crate) fn hevc_sei(kind: u8, messages: &[Vec<u8>]) -> Vec<u8> {
        let mut rbsp: Vec<u8> = messages.concat();
        rbsp.push(0x80);
        let mut nal = vec![kind << 1, 0x01];
        nal.extend(escape(&rbsp));
        nal
    }

    pub(crate) fn annex_b(units: &[&[u8]]) -> Vec<u8> {
        let mut out = Vec::new();
        for (i, unit) in units.iter().enumerate() {
            // Mix three- and four-byte start codes.
            if i % 2 == 0 {
                out.extend_from_slice(&[0, 0, 0, 1]);
            } else {
                out.extend_from_slice(&[0, 0, 1]);
            }
            out.extend_from_slice(unit);
        }
        out
    }

    pub(crate) fn length_prefixed(units: &[&[u8]], size: usize) -> Vec<u8> {
        let mut out = Vec::new();
        for unit in units {
            let length = (unit.len() as u32).to_be_bytes();
            out.extend_from_slice(&length[4 - size..]);
            out.extend_from_slice(unit);
        }
        out
    }

    fn extract(packet: &[u8], codec: Codec, size: u8) -> Vec<Triplet> {
        let mut out = Vec::new();
        Extractor::default().extract(packet, codec, size, &mut out);
        out
    }

    /// A slice NAL unit whose payload holds zero runs and fake start-code-like bytes.
    fn slice(kind: u8) -> Vec<u8> {
        let mut nal = vec![kind];
        nal.extend(escape(&[0x88, 0, 0, 1, 0, 0, 0, 2, 0xFF, 0, 0, 3, 0x42]));
        nal
    }

    fn pairs(triplets: &[Triplet]) -> Vec<[u8; 3]> {
        triplets.iter().map(|t| t.0).collect()
    }

    #[test]
    fn codec_abi_values() {
        assert_eq!(Codec::from_abi(1), Some(Codec::H264));
        assert_eq!(Codec::from_abi(2), Some(Codec::Hevc));
        assert_eq!(Codec::from_abi(3), Some(Codec::Mpeg2));
        assert_eq!(Codec::from_abi(0), None);
        assert_eq!(Codec::from_abi(4), None);
    }

    #[test]
    fn h264_sei_in_annex_b_and_every_length_prefix() {
        // A user_data_unregistered message with a 00 00 01 run makes the unit need escaping.
        let cc = cc_data_bytes(&[(false, [0x94, 0x20]), (true, [0x00, 0x00])], 2);
        let unregistered = vec![5, 6, 0, 0, 1, 0, 0, 2];
        let sei = h264_sei(&[unregistered, sei_message(&ga94_payload(&cc))]);
        assert!(
            sei.windows(3).any(|w| w == [0, 0, 3]),
            "test needs escaping"
        );
        let aud = [0x09, 0xF0];
        let sps = [0x67, 0x64, 0x00, 0x28];
        let idr = slice(0x65);
        let expected = vec![
            [0xFC, 0x94, 0x20],
            [0xFD, 0x00, 0x00],
            [0xFA, 0, 0],
            [0xFA, 0, 0],
        ];
        let units: [&[u8]; 4] = [&aud, &sps, &sei, &idr];
        assert_eq!(pairs(&extract(&annex_b(&units), Codec::H264, 0)), expected);
        for size in [1usize, 2, 4] {
            let packet = length_prefixed(&units, size);
            assert_eq!(
                pairs(&extract(&packet, Codec::H264, size as u8)),
                expected,
                "length size {size}"
            );
        }
        // A length size the fork never sends reads nothing.
        assert!(extract(&length_prefixed(&units, 4), Codec::H264, 5).is_empty());
    }

    #[test]
    fn several_messages_and_units_keep_bitstream_order() {
        let first = cc_data_bytes(&[(false, [0x41, 0x42])], 0);
        let second = cc_data_bytes(&[(false, [0x43, 0x44])], 0);
        let other = vec![5, 3, 1, 2, 3]; // user_data_unregistered, skipped
        let sei1 = h264_sei(&[
            other.clone(),
            sei_message(&ga94_payload(&first)),
            sei_message(&ga94_payload(&second)),
        ]);
        let third = cc_data_bytes(&[(true, [0x45, 0x46])], 0);
        let sei2 = h264_sei(&[sei_message(&ga94_payload(&third))]);
        let slice = slice(0x01);
        let packet = annex_b(&[&sei1, &slice, &sei2, &slice]);
        let got: Vec<[u8; 2]> = extract(&packet, Codec::H264, 0)
            .iter()
            .map(|t| t.data())
            .collect();
        assert_eq!(got, [[0x41, 0x42], [0x43, 0x44], [0x45, 0x46]]);
    }

    #[test]
    fn large_payload_sizes_use_ff_extension() {
        // 31 triplets plus padding past 255 bytes in one message.
        let pairs_in: Vec<(bool, [u8; 2])> = (0..31).map(|i| (false, [0x40 + i, 0x41])).collect();
        let mut cc = cc_data_bytes(&pairs_in, 0);
        cc.extend(std::iter::repeat_n(0xFF, 200));
        let message = sei_message(&ga94_payload(&cc));
        assert_eq!(message[1], 0xFF);
        let sei = h264_sei(&[message]);
        let got = extract(&length_prefixed(&[&sei], 4), Codec::H264, 4);
        assert_eq!(got.len(), 31);
        assert_eq!(got[30].data(), [0x40 + 30, 0x41]);
    }

    #[test]
    fn hevc_prefix_and_suffix_sei() {
        let a = cc_data_bytes(&[(false, [0x14, 0x2F])], 0);
        let b = cc_data_bytes(&[(false, [0x14, 0x2C])], 0);
        let prefix = hevc_sei(HEVC_PREFIX_SEI, &[sei_message(&ga94_payload(&a))]);
        let suffix = hevc_sei(HEVC_SUFFIX_SEI, &[sei_message(&ga94_payload(&b))]);
        let vps = [32 << 1, 1, 0x0C];
        let idr = [19 << 1, 1, 0xAF, 0, 0, 3, 0];
        let units: [&[u8]; 4] = [&vps, &prefix, &idr, &suffix];
        let expected = [[0x14, 0x2F], [0x14, 0x2C]];
        let data = |t: Vec<Triplet>| t.iter().map(|t| t.data()).collect::<Vec<_>>();
        assert_eq!(data(extract(&annex_b(&units), Codec::Hevc, 0)), expected);
        assert_eq!(
            data(extract(&length_prefixed(&units, 4), Codec::Hevc, 4)),
            expected
        );
        // The H.264 reading of the same bytes finds no SEI (type 39 << 1 is not 6).
        assert!(extract(&annex_b(&units), Codec::H264, 0).is_empty());
    }

    #[test]
    fn directv_and_foreign_payloads() {
        let cc = cc_data_bytes(&[(false, [0x20, 0x21])], 0);
        let mut directv = vec![COUNTRY_USA, 0x00, 0x2F, TYPE_CC_DATA, cc.len() as u8];
        directv.extend_from_slice(&cc);
        let mut wrong_country = ga94_payload(&cc);
        wrong_country[0] = 0xB4;
        let mut wrong_id = ga94_payload(&cc);
        wrong_id[3] = b'X';
        let mut afd = ga94_payload(&cc);
        afd[7] = 0x06; // bar data type
        let sei = h264_sei(&[
            sei_message(&wrong_country),
            sei_message(&wrong_id),
            sei_message(&afd),
            sei_message(&directv),
        ]);
        let got = extract(&annex_b(&[&sei]), Codec::H264, 0);
        assert_eq!(pairs(&got), [[0xFC, 0x20, 0x21]]);
    }

    #[test]
    fn process_flag_count_and_truncation() {
        let mut off = cc_data_bytes(&[(false, [0x41, 0x41])], 0);
        off[0] &= !0x40;
        let mut short = cc_data_bytes(&[(false, [0x42, 0x42]), (false, [0x43, 0x43])], 0);
        short[0] = 0x40 | 9; // claims nine triplets
        short.truncate(2 + 3 * 2);
        let sei = h264_sei(&[
            sei_message(&ga94_payload(&off)),
            sei_message(&ga94_payload(&short)),
        ]);
        let got = extract(&annex_b(&[&sei]), Codec::H264, 0);
        assert_eq!(pairs(&got), [[0xFC, 0x42, 0x42], [0xFC, 0x43, 0x43]]);
    }

    #[test]
    fn malformed_units_never_panic() {
        let cc = cc_data_bytes(&[(false, [0x41, 0x42])], 0);
        let good = h264_sei(&[sei_message(&ga94_payload(&cc))]);
        // A message claiming more than is left is skipped with the rest of its unit.
        let mut oversized = vec![0x06, 4, 200];
        oversized.extend_from_slice(&ga94_payload(&cc));
        let cases: Vec<Vec<u8>> = vec![
            vec![],
            vec![0],
            vec![0, 0, 1],
            vec![0, 0, 0, 1, 6],
            vec![0, 0, 1, 6, 0xFF, 0xFF],
            annex_b(&[&oversized, &good]),
            vec![0xFF; 64],
        ];
        for case in &cases {
            for size in 0..=5u8 {
                let _ = extract(case, Codec::H264, size);
                let _ = extract(case, Codec::Hevc, size);
                let _ = extract(case, Codec::Mpeg2, size);
            }
        }
        // The good unit after an oversized one is still read.
        assert_eq!(
            extract(&annex_b(&[&oversized, &good]), Codec::H264, 0).len(),
            1
        );
        // A length prefix running past the packet stops the walk.
        let mut cut = length_prefixed(&[&good], 4);
        cut.truncate(cut.len() - 1);
        assert!(extract(&cut, Codec::H264, 4).is_empty());
    }

    #[test]
    fn mpeg2_user_data_before_the_first_slice() {
        let cc = cc_data_bytes(&[(false, [0x94, 0x2C]), (true, [0x80, 0x80])], 1);
        let mut packet = vec![0, 0, 1, 0xB3, 0x78, 0x04, 0x38, 0x35]; // sequence header
        packet.extend_from_slice(&[0, 0, 1, 0xB2]);
        packet.extend_from_slice(b"DTG1\x41"); // AFD user data: not captions
        packet.extend_from_slice(&[0, 0, 1, 0x00, 0x01, 0x58]); // picture header
        packet.extend_from_slice(&[0, 0, 1, 0xB2]);
        packet.extend_from_slice(b"GA94\x03");
        packet.extend_from_slice(&cc);
        packet.extend_from_slice(&[0, 0, 1, 0x01, 0x22, 0x00, 0x00, 0x01, 0xB2]); // slice
        packet.extend_from_slice(b"GA94\x03");
        packet.extend_from_slice(&cc);
        let got = extract(&packet, Codec::Mpeg2, 0);
        assert_eq!(
            pairs(&got),
            [[0xFC, 0x94, 0x2C], [0xFD, 0x80, 0x80], [0xFA, 0, 0]]
        );
    }

    #[test]
    fn unescape_removes_only_emulation_prevention() {
        let mut out = Vec::new();
        unescape(&[0, 0, 3, 1, 0, 0, 3, 0, 0, 3, 3, 4, 0, 3, 0], &mut out);
        assert_eq!(out, [0, 0, 1, 0, 0, 0, 0, 3, 4, 0, 3, 0]);
        for len in 0..64 {
            let data: Vec<u8> = (0..len).map(|i| [0, 0, 1, 2, 3, 0, 7][i % 7]).collect();
            unescape(&escape(&data), &mut out);
            assert_eq!(out, data);
        }
    }

    #[test]
    fn start_code_scan_finds_every_code() {
        let mut data = vec![7u8; 5];
        let mut expected = Vec::new();
        for gap in 0..40 {
            data.extend_from_slice(&[0, 0, 1]);
            expected.push(data.len());
            data.extend(std::iter::repeat_n(
                if gap % 3 == 0 { 0 } else { 0x55 },
                gap % 5,
            ));
            data.extend_from_slice(&[0x10, 0, 2, 1]);
        }
        let mut found = Vec::new();
        let mut at = 0;
        while let Some(next) = next_start_code(&data, at) {
            found.push(next);
            at = next;
        }
        let naive: Vec<usize> = (0..data.len().saturating_sub(2))
            .filter(|&i| data[i..i + 3] == [0, 0, 1])
            .map(|i| i + 3)
            .collect();
        assert_eq!(found, naive);
        assert!(expected.iter().all(|e| found.contains(e)));
    }
}
