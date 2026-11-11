//! Bounded decoder for Silo's lossless PGS (.sup) windows. SUP headers carry 90 kHz source
//! timestamps; a display set becomes an event at PCS time, not ODS decode time or END time.
//! No FFmpeg dependency: decoded PAL8 rectangles use the same queue/placement as embedded PGS.

use std::collections::BTreeMap;

use super::bitmap::{self, Event, Rect};
use super::{E_INVALIDARG, MAX_FRAME_SIZE};

pub const MAX_INPUT: usize = 16 * 1024 * 1024;
pub const MAX_DECODED: usize = 64 * 1024 * 1024;
const MAX_EVENTS: usize = 4096;
const MAX_OBJECTS: usize = 64;
const MAX_PALETTES: usize = 8;

type Result<T> = std::result::Result<T, i32>;

struct Bytes<'a>(&'a [u8]);
impl<'a> Bytes<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let value = self.0.get(..n).ok_or(E_INVALIDARG)?;
        self.0 = &self.0[n..];
        Ok(value)
    }
    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_be_bytes(self.take(2)?.try_into().unwrap()))
    }
    fn u24(&mut self) -> Result<usize> {
        let b = self.take(3)?;
        Ok((usize::from(b[0]) << 16) | (usize::from(b[1]) << 8) | usize::from(b[2]))
    }
    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn done(&self) -> Result<()> {
        if self.0.is_empty() {
            Ok(())
        } else {
            Err(E_INVALIDARG)
        }
    }
}

#[derive(Clone, Copy)]
struct Area {
    x: u16,
    y: u16,
    w: u16,
    h: u16,
}
impl Area {
    fn read(b: &mut Bytes<'_>) -> Result<Self> {
        Ok(Self {
            x: b.u16()?,
            y: b.u16()?,
            w: b.u16()?,
            h: b.u16()?,
        })
    }
}
struct Reference {
    id: u16,
    window: u8,
    flags: u8,
    x: u16,
    y: u16,
    crop: Option<Area>,
}
struct Composition {
    time: i64,
    canvas: (u32, u32),
    palette: u8,
    objects: Vec<Reference>,
}
struct Object {
    version: u8,
    w: u16,
    h: u16,
    expected: usize,
    rle: Vec<u8>,
    complete: bool,
}

#[derive(Default)]
struct Decoder {
    objects: BTreeMap<u16, Object>,
    palettes: BTreeMap<u8, [u32; 256]>,
    windows: BTreeMap<u8, Area>,
    composition: Option<Composition>,
    events: Vec<Event>,
    // Charge all decoded rectangles, including duplicate compositions. Together with MAX_INPUT,
    // bounded object/palette counts and MAX_EVENTS, this bounds both retained and temporary memory.
    decoded_bytes: usize,
}

impl Decoder {
    fn pcs(&mut self, b: &mut Bytes<'_>, time: i64) -> Result<()> {
        let canvas = (u32::from(b.u16()?), u32::from(b.u16()?));
        if canvas.0 == 0 || canvas.1 == 0 || canvas.0 > MAX_FRAME_SIZE || canvas.1 > MAX_FRAME_SIZE
        {
            return Err(E_INVALIDARG);
        }
        b.u8()?; // Frame rate; subtitle timing comes from the SUP PTS.
        b.u16()?; // Composition sequence number.
        if b.u8()? & 0xc0 != 0 {
            // Epoch or acquisition: cached resources no longer apply.
            self.objects.clear();
            self.palettes.clear();
            self.windows.clear();
        }
        b.u8()?; // Palette update; a normal composition still reuses its objects.
        let palette = b.u8()?;
        let count = b.u8()?;
        if count > 2 {
            return Err(E_INVALIDARG);
        }
        let mut objects = Vec::new();
        for _ in 0..count {
            let id = b.u16()?;
            let window = b.u8()?;
            let flags = b.u8()?;
            let x = b.u16()?;
            let y = b.u16()?;
            let crop = if flags & 0x80 != 0 {
                Some(Area::read(b)?)
            } else {
                None
            };
            objects.push(Reference {
                id,
                window,
                flags,
                x,
                y,
                crop,
            });
        }
        b.done()?;
        self.composition = Some(Composition {
            time,
            canvas,
            palette,
            objects,
        });
        Ok(())
    }
    fn pds(&mut self, b: &mut Bytes<'_>) -> Result<()> {
        let id = b.u8()?;
        b.u8()?; // Palette version; entries are partial updates.
        if !self.palettes.contains_key(&id) && self.palettes.len() == MAX_PALETTES {
            return Err(E_INVALIDARG);
        }
        let hd = self.composition.as_ref().is_none_or(|c| c.canvas.1 > 576);
        let palette = self.palettes.entry(id).or_insert([0; 256]);
        while !b.0.is_empty() {
            let index = usize::from(b.u8()?);
            let y = b.u8()?;
            let cr = b.u8()?;
            let cb = b.u8()?;
            let alpha = b.u8()?;
            palette[index] = color(y, cr, cb, alpha, hd);
        }
        Ok(())
    }
    fn ods(&mut self, b: &mut Bytes<'_>) -> Result<()> {
        let id = b.u16()?;
        let version = b.u8()?;
        let flags = b.u8()?;
        if flags & 0x80 != 0 {
            let expected = b.u24()?.checked_sub(4).ok_or(E_INVALIDARG)?;
            let w = b.u16()?;
            let h = b.u16()?;
            let pixels = usize::from(w) * usize::from(h);
            if w == 0
                || h == 0
                || u32::from(w) > MAX_FRAME_SIZE
                || u32::from(h) > MAX_FRAME_SIZE
                || pixels > bitmap::MAX_RECT_PIXELS as usize
                || expected > MAX_INPUT
                || (!self.objects.contains_key(&id) && self.objects.len() == MAX_OBJECTS)
            {
                return Err(E_INVALIDARG);
            }
            // Never reserve the advertised size: malformed ODS lengths must not allocate it.
            self.objects.insert(
                id,
                Object {
                    version,
                    w,
                    h,
                    expected,
                    rle: Vec::new(),
                    complete: false,
                },
            );
        }
        let Some(object) = self.objects.get_mut(&id) else {
            // A positioned server window can begin inside a fragmented display set.
            return Ok(());
        };
        if object.version != version
            || object.complete
            || object.rle.len() + b.0.len() > object.expected
        {
            return Err(E_INVALIDARG);
        }
        object.rle.extend_from_slice(b.0);
        if flags & 0x40 != 0 {
            if object.rle.len() != object.expected {
                return Err(E_INVALIDARG);
            }
            object.complete = true;
        }
        Ok(())
    }
    fn wds(&mut self, b: &mut Bytes<'_>) -> Result<()> {
        let count = b.u8()?;
        if count > 2 {
            return Err(E_INVALIDARG);
        }
        self.windows.clear();
        for _ in 0..count {
            let id = b.u8()?;
            self.windows.insert(id, Area::read(b)?);
        }
        b.done()
    }
    fn end(&mut self, video: Option<(u32, u32)>, window_end: i64) -> Result<()> {
        let Some(c) = self.composition.take() else {
            return Ok(());
        };
        if self.events.len() == MAX_EVENTS {
            return Err(E_INVALIDARG);
        }
        let mut rects = Vec::new();
        for reference in c.objects {
            let Some(palette) = self.palettes.get(&c.palette) else {
                continue;
            };
            let Some(object) = self.objects.get(&reference.id).filter(|o| o.complete) else {
                continue;
            };
            let pixels = usize::from(object.w) * usize::from(object.h);
            self.decoded_bytes = self
                .decoded_bytes
                .checked_add(pixels + 1024)
                .ok_or(E_INVALIDARG)?;
            if self.decoded_bytes > MAX_DECODED {
                return Err(E_INVALIDARG);
            }
            let indices = rle(&object.rle, object.w, object.h)?;
            // PCS crops refer to the decoded object. WDS clips in canvas coordinates.
            let crop = reference.crop.unwrap_or(Area {
                x: 0,
                y: 0,
                w: object.w,
                h: object.h,
            });
            let (sx, sy, w, h) = (
                u32::from(crop.x),
                u32::from(crop.y),
                u32::from(crop.w),
                u32::from(crop.h),
            );
            if sx + w > u32::from(object.w) || sy + h > u32::from(object.h) {
                return Err(E_INVALIDARG);
            }
            let (x, y) = (u32::from(reference.x), u32::from(reference.y));
            let window = self
                .windows
                .get(&reference.window)
                .copied()
                .unwrap_or(Area {
                    x: 0,
                    y: 0,
                    w: c.canvas.0 as u16,
                    h: c.canvas.1 as u16,
                });
            let left = x.max(u32::from(window.x));
            let top = y.max(u32::from(window.y));
            let right = (x + w)
                .min(u32::from(window.x) + u32::from(window.w))
                .min(c.canvas.0);
            let bottom = (y + h)
                .min(u32::from(window.y) + u32::from(window.h))
                .min(c.canvas.1);
            if right <= left || bottom <= top {
                continue;
            }
            let mut clipped = Vec::with_capacity(((right - left) * (bottom - top)) as usize);
            for row in top..bottom {
                let at = ((sy + row - y) * u32::from(object.w) + sx + left - x) as usize;
                clipped.extend_from_slice(&indices[at..at + (right - left) as usize]);
            }
            let mut rect = Rect::new(
                left as i32,
                top as i32,
                right - left,
                bottom - top,
                clipped,
                palette,
            );
            rect.forced = reference.flags & 0x40 != 0;
            rects.push(rect);
        }
        if c.time < window_end {
            self.events.push(Event {
                start_ms: c.time,
                end_ms: Some(window_end),
                canvas: Some(c.canvas),
                video,
                rects,
            });
        }
        Ok(())
    }
}

/// Decode one bounded window. The final event ends at coverage's tail even when the server cuts
/// off its clearing display set. Empty windows are valid (a track may have long gaps).
pub fn decode(data: &[u8], end_ms: i64, video: Option<(u32, u32)>) -> Result<Vec<Event>> {
    if data.len() > MAX_INPUT || end_ms <= 0 {
        return Err(E_INVALIDARG);
    }
    let mut decoder = Decoder::default();
    let mut input = Bytes(data);
    while !input.0.is_empty() {
        if input.take(2)? != b"PG" {
            return Err(E_INVALIDARG);
        }
        let pts = input.u32()?;
        input.u32()?; // Decode timestamp is not presentation time.
        let kind = input.u8()?;
        let length = usize::from(input.u16()?);
        let mut body = Bytes(input.take(length)?);
        match kind {
            0x14 => decoder.pds(&mut body)?,
            0x15 => decoder.ods(&mut body)?,
            0x16 => decoder.pcs(&mut body, (i64::from(pts) * 1000 + 45000) / 90000)?,
            0x17 => decoder.wds(&mut body)?,
            0x80 => {
                body.done()?;
                decoder.end(video, end_ms)?;
            }
            _ => return Err(E_INVALIDARG),
        }
    }
    // A duration cutoff may end between whole segments of a set; never publish it without END.
    decoder.events.sort_by_key(|e| e.start_ms);
    Ok(decoder.events)
}

/// PGS has PAL8 indices and zero escapes: short/long transparent or colored runs, and 00 00 EOL.
fn rle(data: &[u8], w: u16, h: u16) -> Result<Vec<u8>> {
    let (w, h) = (usize::from(w), usize::from(h));
    let mut pixels = vec![0; w * h];
    let mut b = Bytes(data);
    let (mut x, mut y) = (0, 0);
    while !b.0.is_empty() && y < h {
        let value = b.u8()?;
        let (run, index) = if value != 0 {
            (1, value)
        } else {
            let flags = b.u8()?;
            if flags == 0 {
                x = 0;
                y += 1;
                continue;
            }
            let mut run = usize::from(flags & 0x3f);
            if flags & 0x40 != 0 {
                run = (run << 8) | usize::from(b.u8()?);
            }
            let index = if flags & 0x80 != 0 { b.u8()? } else { 0 };
            (run, index)
        };
        if run == 0 || x + run > w {
            return Err(E_INVALIDARG);
        }
        pixels[y * w + x..y * w + x + run].fill(index);
        x += run;
    }
    if y < h && !(y == h - 1 && x == w) {
        return Err(E_INVALIDARG);
    }
    b.done()?;
    Ok(pixels)
}

/// Limited-range YCbCr, matching pgssub's BT.709 default (BT.601 for SD canvases), straight alpha.
fn color(y: u8, cr: u8, cb: u8, a: u8, hd: bool) -> u32 {
    let y = 1.16438356 * (f64::from(y) - 16.0);
    let (cr, cb) = (f64::from(cr) - 128.0, f64::from(cb) - 128.0);
    let (r, g, b) = if hd {
        (
            y + 1.79274107 * cr,
            y - 0.53290933 * cr - 0.21324861 * cb,
            y + 2.11240179 * cb,
        )
    } else {
        (
            y + 1.59602678 * cr,
            y - 0.81296765 * cr - 0.39176229 * cb,
            y + 2.01723214 * cb,
        )
    };
    let channel = |c: f64| c.round().clamp(0.0, 255.0) as u32;
    (u32::from(a) << 24) | (channel(r) << 16) | (channel(g) << 8) | channel(b)
}

#[cfg(test)]
#[path = "pgs_tests.rs"]
mod tests;
