//! Whether an HLS media segment holds media (hls.rs). Silo answers a segment past a finished transcode's
//! real end with one that holds none (silo-server `4bee8f8`, `past_end_segment.go`): the PAT and PMT of the
//! session's MPEG-TS, an fMP4 `moof` whose tracks have no samples with an empty `mdat`, or no bytes at all.
//! Those concatenate into FFmpeg's input harmlessly, but a mount whose segments from its start to the
//! playlist's end are all like that has nothing FFmpeg can open: the title ended where it starts.
//!
//! The scan reads only what it needs: an MPEG-TS packet that starts a PES (the `00 00 01` start code),
//! or an fMP4 `mdat` box with a payload. It decides within the first packets or boxes of a real
//! segment and then stops looking.

use std::io::Read;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

const TS_PACKET: usize = 188;
const TS_SYNC: u8 = 0x47;

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
enum Format {
    /// No byte seen yet: the first one tells (MPEG-TS packets start with 0x47).
    #[default]
    Unknown,
    Ts,
    Mp4,
}

/// The scan state of one segment's bytes, fed in any pieces.
#[derive(Debug, Default)]
pub struct MediaScan {
    format: Format,
    media: bool,
    /// MPEG-TS: the packet being assembled. fMP4: the box header being assembled.
    held: Vec<u8>,
    /// fMP4: payload bytes of the current box still to skip.
    skip: u64,
    /// fMP4: the box being skipped is an `mdat` that runs to the end of the segment.
    open_mdat: bool,
    /// MPEG-TS: the segment's first PAT packet and the PMT packet it names, once seen (the debug empty tail
    /// repeats them, as the server does).
    pat: Option<[u8; TS_PACKET]>,
    pmt_pid: Option<u16>,
    tables: Option<Vec<u8>>,
}

impl MediaScan {
    /// Media found so far.
    pub fn media(&self) -> bool {
        self.media
    }

    /// The segment's PAT and PMT packets, when it is MPEG-TS and they came before its media.
    pub fn ts_tables(&self) -> Option<&[u8]> {
        self.tables.as_deref()
    }

    /// Feeds the segment's next bytes; true once media was found.
    pub fn feed(&mut self, mut bytes: &[u8]) -> bool {
        while !self.media && !bytes.is_empty() {
            if self.format == Format::Unknown {
                self.format = if bytes[0] == TS_SYNC {
                    Format::Ts
                } else {
                    Format::Mp4
                };
            }
            bytes = match self.format {
                Format::Ts => self.feed_ts(bytes),
                _ => self.feed_mp4(bytes),
            };
        }
        self.media
    }

    fn feed_ts<'a>(&mut self, bytes: &'a [u8]) -> &'a [u8] {
        let take = (TS_PACKET - self.held.len()).min(bytes.len());
        self.held.extend_from_slice(&bytes[..take]);
        if self.held.len() == TS_PACKET {
            let packet: [u8; TS_PACKET] = self.held[..].try_into().expect("a whole packet");
            self.held.clear();
            self.ts_packet(&packet);
        }
        &bytes[take..]
    }

    fn ts_packet(&mut self, packet: &[u8; TS_PACKET]) {
        // Lost sync is something other than Silo's empty answer: count it as media.
        if packet[0] != TS_SYNC {
            self.media = true;
            return;
        }
        let unit_start = packet[1] & 0x40 != 0;
        let pid = u16::from_be_bytes([packet[1] & 0x1f, packet[2]]);
        let payload = match (packet[3] >> 4) & 0x3 {
            0x1 => &packet[4..],
            0x3 => packet.get(5 + usize::from(packet[4])..).unwrap_or(&[]),
            _ => &[],
        };
        if unit_start && payload.starts_with(&[0, 0, 1]) {
            self.media = true;
            return;
        }
        // PSI sections start with a pointer field, then the table: PAT (PID 0) names the PMT's PID.
        if unit_start && pid == 0 && self.pat.is_none() {
            let section = payload
                .first()
                .and_then(|&pointer| payload.get(1 + usize::from(pointer)..));
            if let Some(section) = section.filter(|s| s.len() >= 12 && s[0] == 0x00) {
                let length = usize::from(u16::from_be_bytes([section[1] & 0x0f, section[2]]));
                let programs = &section[8..section.len().min((3 + length).saturating_sub(4))];
                let pmt = programs
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .find(|p| u16::from_be_bytes([p[0], p[1]]) != 0)
                    .map(|p| u16::from_be_bytes([p[2] & 0x1f, p[3]]));
                if let Some(pmt) = pmt {
                    self.pat = Some(*packet);
                    self.pmt_pid = Some(pmt);
                }
            }
        } else if unit_start && self.tables.is_none() && Some(pid) == self.pmt_pid {
            let mut tables = self.pat.expect("the PAT named this PMT").to_vec();
            tables.extend_from_slice(packet);
            self.tables = Some(tables);
        }
    }

    fn feed_mp4<'a>(&mut self, mut bytes: &'a [u8]) -> &'a [u8] {
        if self.skip > 0 {
            if self.open_mdat {
                // Any byte of an `mdat` that runs to the end is a sample.
                self.media = true;
                return &[];
            }
            let skipped = self.skip.min(bytes.len() as u64);
            self.skip -= skipped;
            return &bytes[skipped as usize..];
        }
        // A box header: size and type, then a 64-bit size when size is 1.
        let wanted = if self.held.len() >= 8 && self.held[..4] == [0, 0, 0, 1] {
            16
        } else {
            8
        };
        let take = (wanted - self.held.len()).min(bytes.len());
        self.held.extend_from_slice(&bytes[..take]);
        bytes = &bytes[take..];
        if self.held.len() < wanted {
            return bytes;
        }
        let size = u32::from_be_bytes(self.held[..4].try_into().expect("four bytes"));
        if size == 1 && self.held.len() < 16 {
            return bytes;
        }
        let (size, header) = match size {
            1 => (
                u64::from_be_bytes(self.held[8..16].try_into().expect("eight bytes")),
                16,
            ),
            // A box that runs to the end of the segment.
            0 => (u64::MAX, 8),
            size => (u64::from(size), 8),
        };
        let mdat = &self.held[4..8] == b"mdat";
        self.held.clear();
        if size < header {
            // Not a box: something other than Silo's empty answer.
            self.media = true;
        } else if mdat && size == u64::MAX {
            // An `mdat` that runs to the end holds media once any byte follows its header.
            self.media = !bytes.is_empty();
            self.open_mdat = true;
            self.skip = u64::MAX;
        } else if mdat {
            self.media = size > header;
        } else {
            self.skip = size - header;
        }
        bytes
    }
}

/// A segment body that tells `media` once its bytes held media, and keeps its MPEG-TS tables for the
/// debug empty tail.
pub struct Scanned<R> {
    inner: R,
    scan: MediaScan,
    media: Arc<AtomicBool>,
    tables: Arc<std::sync::Mutex<Option<Vec<u8>>>>,
    ended: bool,
}

impl<R> Scanned<R> {
    pub fn new(
        inner: R,
        media: Arc<AtomicBool>,
        tables: Arc<std::sync::Mutex<Option<Vec<u8>>>>,
    ) -> Self {
        Scanned {
            inner,
            scan: MediaScan::default(),
            media,
            tables,
            ended: false,
        }
    }
}

impl<R: Read> Read for Scanned<R> {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        let count = self.inner.read(buffer)?;
        if count == 0 && !self.ended {
            self.ended = true;
            if !self.scan.media() {
                crate::media_fault::count(&crate::media_fault::HLS_EMPTY_SEGMENTS);
            }
        }
        if count > 0 && !self.scan.media() && self.scan.feed(&buffer[..count]) {
            self.media.store(true, Ordering::Release);
            if let Some(tables) = self.scan.ts_tables() {
                *self
                    .tables
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner) = Some(tables.to_vec());
            }
        }
        Ok(count)
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// One MPEG-TS packet: PID, unit start, payload (stuffed to 184 bytes).
    pub(crate) fn ts_packet(pid: u16, unit_start: bool, payload: &[u8]) -> Vec<u8> {
        let mut packet = vec![
            TS_SYNC,
            (if unit_start { 0x40 } else { 0 }) | (pid >> 8) as u8,
            pid as u8,
            0x10,
        ];
        packet.extend_from_slice(payload);
        packet.resize(TS_PACKET, 0xff);
        packet
    }

    /// A PAT naming program 1's PMT at 0x1000, and a PMT with one H.264 stream at 0x100: FFmpeg's layout.
    pub(crate) fn ts_tables() -> Vec<u8> {
        let mut out = ts_packet(
            0,
            true,
            &[
                0x00, 0x00, 0xb0, 0x0d, 0x00, 0x01, 0xc1, 0x00, 0x00, 0x00, 0x01, 0xf0, 0x00, 0, 0,
                0, 0,
            ],
        );
        out.extend(ts_packet(
            0x1000,
            true,
            &[
                0x00, 0x02, 0xb0, 0x12, 0x00, 0x01, 0xc1, 0x00, 0x00, 0xe1, 0x00, 0xf0, 0x00, 0x1b,
                0xe1, 0x00, 0xf0, 0x00, 0, 0, 0, 0,
            ],
        ));
        out
    }

    /// A real-looking MPEG-TS segment: tables, then PES packets.
    pub(crate) fn ts_media(packets: usize) -> Vec<u8> {
        let mut out = ts_tables();
        out.extend(ts_packet(0x100, true, &[0, 0, 1, 0xe0, 0, 0, 0x80, 0x80]));
        for _ in 1..packets {
            out.extend(ts_packet(0x100, false, &[0x42; 16]));
        }
        out
    }

    fn mp4_box(kind: &[u8; 4], payload: &[u8]) -> Vec<u8> {
        let mut out = ((8 + payload.len()) as u32).to_be_bytes().to_vec();
        out.extend_from_slice(kind);
        out.extend_from_slice(payload);
        out
    }

    /// The server's empty fragment shape: `moof` (mfhd, a traf with tfhd, tfdt and a trun of no samples) and an
    /// empty `mdat`.
    pub(crate) fn mp4_empty() -> Vec<u8> {
        let traf = [
            mp4_box(b"tfhd", &[0, 2, 0, 0, 0, 0, 0, 1]),
            mp4_box(b"tfdt", &[1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x75, 0x30]),
            mp4_box(b"trun", &[0, 0, 0, 0, 0, 0, 0, 0]),
        ]
        .concat();
        let moof = [
            mp4_box(b"mfhd", &[0, 0, 0, 0, 0, 0, 0, 4]),
            mp4_box(b"traf", &traf),
        ]
        .concat();
        [mp4_box(b"moof", &moof), mp4_box(b"mdat", &[])].concat()
    }

    /// A real-looking fragment: `styp`, `moof`, `mdat` with samples.
    pub(crate) fn mp4_media(bytes: usize) -> Vec<u8> {
        [
            mp4_box(b"styp", b"msdh\0\0\0\0msdhmsix"),
            mp4_box(b"moof", &[0; 96]),
            mp4_box(b"mdat", &vec![0x42; bytes]),
        ]
        .concat()
    }

    /// Feeds `bytes` in pieces of `piece` and reports media.
    fn scan(bytes: &[u8], piece: usize) -> (bool, Option<Vec<u8>>) {
        let mut scan = MediaScan::default();
        for chunk in bytes.chunks(piece.max(1)) {
            scan.feed(chunk);
        }
        (scan.media(), scan.ts_tables().map(<[u8]>::to_vec))
    }

    #[test]
    fn silos_empty_answers_hold_no_media() {
        for piece in [1, 7, 188, 4096] {
            assert_eq!(scan(&ts_tables(), piece), (false, Some(ts_tables())));
            assert!(!scan(&[ts_tables(), ts_tables()].concat(), piece).0);
            assert_eq!(scan(&mp4_empty(), piece), (false, None));
            assert_eq!(scan(&[], piece), (false, None));
        }
    }

    #[test]
    fn real_segments_hold_media_in_any_pieces() {
        for piece in [1, 5, 188, 189, 4096] {
            let (media, tables) = scan(&ts_media(50), piece);
            assert!(media, "ts in {piece}");
            assert_eq!(tables, Some(ts_tables()));
            assert!(scan(&mp4_media(1000), piece).0, "mp4 in {piece}");
        }
        // A PES behind an adaptation field (a keyframe's PCR) still counts.
        let mut adapted = ts_packet(0x100, true, &[]);
        adapted[3] = 0x30;
        adapted[4] = 7;
        adapted[12..15].copy_from_slice(&[0, 0, 1]);
        assert!(scan(&[ts_tables(), adapted].concat(), 188).0);
    }

    #[test]
    fn anything_unrecognised_counts_as_media() {
        assert!(!scan(&[0x47; 10], 10).0, "too short to judge yet");
        let mut lost = ts_tables();
        lost.extend(vec![0x00; TS_PACKET]);
        assert!(scan(&lost, 64).0, "lost sync");
        assert!(
            scan(&[0, 0, 0, 4, b'b', b'a', b'd', b'!'], 3).0,
            "a box smaller than its header"
        );
        // A 64-bit mdat size, and an mdat that runs to the end, with bytes after the header.
        let mut large = vec![0, 0, 0, 1];
        large.extend_from_slice(b"mdat");
        large.extend_from_slice(&20u64.to_be_bytes());
        large.extend_from_slice(&[1, 2, 3, 4]);
        assert!(scan(&large, 3).0);
        let mut open_ended = vec![0, 0, 0, 0];
        open_ended.extend_from_slice(b"mdat");
        assert!(!scan(&open_ended, 3).0);
        open_ended.push(9);
        for piece in [1, 3, 4, 8, 9] {
            assert!(scan(&open_ended, piece).0, "open-ended mdat in {piece}");
        }
    }

    #[test]
    fn the_reader_reports_media_and_keeps_the_tables() {
        let media = Arc::new(AtomicBool::new(false));
        let tables = Arc::new(std::sync::Mutex::new(None));
        let (empty_bytes, real_bytes) = (ts_tables(), ts_media(20));
        let mut empty = Scanned::new(&empty_bytes[..], media.clone(), tables.clone());
        std::io::copy(&mut empty, &mut std::io::sink()).unwrap();
        assert!(!media.load(Ordering::Acquire));
        let mut real = Scanned::new(&real_bytes[..], media.clone(), tables.clone());
        let mut copied = Vec::new();
        real.read_to_end(&mut copied).unwrap();
        assert_eq!(copied, ts_media(20));
        assert!(media.load(Ordering::Acquire));
        assert_eq!(tables.lock().unwrap().as_deref(), Some(&ts_tables()[..]));
    }
}
