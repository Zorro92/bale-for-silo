// Hand-authored PGS structures; no subtitle text or media data.
pub fn segment(kind: u8, pts_ms: u32, body: &[u8]) -> Vec<u8> {
    let pts = pts_ms * 90;
    let mut out = b"PG".to_vec();
    out.extend_from_slice(&pts.to_be_bytes());
    out.extend_from_slice(&pts.saturating_sub(9000).to_be_bytes());
    out.push(kind);
    out.extend_from_slice(&(body.len() as u16).to_be_bytes());
    out.extend_from_slice(body);
    out
}
pub fn pcs(time: u32, state: u8, objects: &[u8], count: u8) -> Vec<u8> {
    // 1920x1080, 24fps, composition #1, palette #0.
    let mut body = vec![7, 128, 4, 56, 0x10, 0, 1, state, 0, 0, count];
    body.extend_from_slice(objects);
    segment(0x16, time, &body)
}
pub fn object(time: u32, id: u16, w: u16, h: u16, rle: &[u8]) -> Vec<u8> {
    let mut body = id.to_be_bytes().to_vec();
    body.extend_from_slice(&[0, 0xc0]);
    let length = (rle.len() as u32 + 4).to_be_bytes();
    body.extend_from_slice(&length[1..]);
    body.extend_from_slice(&w.to_be_bytes());
    body.extend_from_slice(&h.to_be_bytes());
    body.extend_from_slice(rle);
    segment(0x15, time, &body)
}
pub fn fixture() -> Vec<u8> {
    let mut data = pcs(1000, 0x80, &[0, 7, 0, 0x40, 0, 10, 0, 20], 1);
    data.extend(segment(0x17, 900, &[1, 0, 0, 0, 0, 0, 7, 128, 4, 56]));
    // Index 1: opaque white, index 2: half-transparent black (YCbCr limited range).
    data.extend(segment(
        0x14,
        900,
        &[0, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 128],
    ));
    data.extend(object(900, 7, 3, 2, &[1, 2, 1, 0, 0, 0, 0x83, 1, 0, 0]));
    data.extend(segment(0x80, 800, &[])); // Must display at PCS=1000, not END=800.
    data.extend(pcs(3000, 0, &[], 0));
    data.extend(segment(0x80, 2900, &[]));
    data
}
