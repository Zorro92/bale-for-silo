//! Minimal PNG writer for debug captures: 8-bit RGB, no filtering, stored (uncompressed) deflate
//! blocks. Large but dependency-free; only debug probes use it.

const STORED_BLOCK: usize = 65_535;

/// Encodes `rgb` (`width * height * 3` bytes, rows top to bottom) as a PNG.
pub fn encode_rgb(width: u32, height: u32, rgb: &[u8]) -> Vec<u8> {
    let row = width as usize * 3;
    assert_eq!(rgb.len(), row * height as usize, "rgb buffer size");
    let mut raw = Vec::with_capacity((row + 1) * height as usize);
    for line in rgb.chunks_exact(row.max(1)).take(height as usize) {
        raw.push(0); // filter: none
        raw.extend_from_slice(line);
    }

    let mut zlib = Vec::with_capacity(raw.len() + raw.len() / STORED_BLOCK * 5 + 16);
    zlib.extend_from_slice(&[0x78, 0x01]);
    let blocks = raw.len().div_ceil(STORED_BLOCK).max(1);
    for index in 0..blocks {
        let block = &raw
            [(index * STORED_BLOCK).min(raw.len())..((index + 1) * STORED_BLOCK).min(raw.len())];
        zlib.push(u8::from(index + 1 == blocks));
        let length = block.len() as u16;
        zlib.extend_from_slice(&length.to_le_bytes());
        zlib.extend_from_slice(&(!length).to_le_bytes());
        zlib.extend_from_slice(block);
    }
    zlib.extend_from_slice(&adler32(&raw).to_be_bytes());

    let mut header = Vec::with_capacity(13);
    header.extend_from_slice(&width.to_be_bytes());
    header.extend_from_slice(&height.to_be_bytes());
    header.extend_from_slice(&[8, 2, 0, 0, 0]); // 8-bit, truecolour, deflate, no filter, no interlace

    let mut png = Vec::with_capacity(zlib.len() + 64);
    png.extend_from_slice(b"\x89PNG\r\n\x1a\n");
    chunk(&mut png, b"IHDR", &header);
    chunk(&mut png, b"IDAT", &zlib);
    chunk(&mut png, b"IEND", &[]);
    png
}

fn chunk(png: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    png.extend_from_slice(&(data.len() as u32).to_be_bytes());
    let start = png.len();
    png.extend_from_slice(kind);
    png.extend_from_slice(data);
    let crc = crc32(&png[start..]);
    png.extend_from_slice(&crc.to_be_bytes());
}

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = !0u32;
    for &byte in bytes {
        crc ^= u32::from(byte);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
        }
    }
    !crc
}

fn adler32(bytes: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for chunk in bytes.chunks(5552) {
        for &byte in chunk {
            a += u32::from(byte);
            b += a;
        }
        a %= 65_521;
        b %= 65_521;
    }
    (b << 16) | a
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;

    #[test]
    fn crc_matches_png_iend() {
        assert_eq!(crc32(b"IEND"), 0xAE42_6082);
    }

    #[test]
    fn zlib_stream_round_trips_across_blocks() {
        let (width, height) = (300u32, 200u32); // 180 200 raw bytes: three stored blocks
        let rgb: Vec<u8> = (0..width * height * 3)
            .map(|i| (i * 7 % 251) as u8)
            .collect();
        let png = encode_rgb(width, height, &rgb);
        assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
        assert_eq!(&png[12..16], b"IHDR");
        let idat_length = u32::from_be_bytes(png[33..37].try_into().unwrap()) as usize;
        assert_eq!(&png[37..41], b"IDAT");
        let mut raw = Vec::new();
        flate2::read::ZlibDecoder::new(&png[41..41 + idat_length])
            .read_to_end(&mut raw)
            .expect("valid zlib stream with matching adler32");
        let row = width as usize * 3;
        assert_eq!(raw.len(), (row + 1) * height as usize);
        for (y, line) in raw.chunks_exact(row + 1).enumerate() {
            assert_eq!(line[0], 0);
            assert_eq!(&line[1..], &rgb[y * row..(y + 1) * row]);
        }
        assert!(png.ends_with(&[0, 0, 0, 0, b'I', b'E', b'N', b'D', 0xAE, 0x42, 0x60, 0x82]));
    }
}
