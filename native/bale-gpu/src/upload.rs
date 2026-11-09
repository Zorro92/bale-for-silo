//! Debug-only CPU-to-GPU upload bandwidth probe.
//!
//! The 4K software path writes ~25 MB per frame (10-bit 4:2:0) into a STAGING texture, and that write
//! runs at only ~2 GB/s on the console. This probe times the same amount of data written several
//! ways on its own hardware device, so the upload method can be chosen by measurement:
//! plain RAM copies (the ceiling), the production STAGING ring (CPU write only, and read+write),
//! DYNAMIC + WRITE_DISCARD, `UpdateSubresource` into a DEFAULT texture, and non-temporal stores.
//! It also reads back from mapped memory, which is very slow when the mapping is write-combined.
//! Nothing here is used by playback.

use std::fmt::Write as _;
use std::hint::black_box;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{E_FAIL, HMODULE};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::Result;

use crate::grain::gpu::{texture, texture_desc};

/// A 3840x2160 10-bit 4:2:0 frame as one R16 surface: 2160 luma rows plus 1080 chroma rows, each
/// 7680 bytes (the repack staging layout: U in the left half of a chroma row, V in the right).
pub const FRAME_WIDTH: u32 = 3840;
pub const FRAME_ROWS: u32 = 3240;
const WARM_UP: u32 = 3;
const RING: usize = 3;
/// Production swscale/sliced-copy thread count (`SwsThreads`).
const THREADS: usize = 4;

#[derive(Clone, Copy, PartialEq)]
enum Write {
    Copy,
    NonTemporal,
}

type Case<'a> = (&'static str, Box<dyn Fn() -> Result<String> + 'a>);

/// Where the probe writes: tightly described by a base pointer and a row pitch.
#[derive(Clone, Copy)]
struct Target {
    base: usize,
    pitch: usize,
}

#[derive(Default)]
struct Timing {
    sum: Duration,
    min: Option<Duration>,
    max: Duration,
    count: u32,
}

impl Timing {
    fn add(&mut self, value: Duration) {
        self.sum += value;
        self.min = Some(self.min.map_or(value, |min| min.min(value)));
        self.max = self.max.max(value);
        self.count += 1;
    }

    fn avg_ms(&self) -> f64 {
        if self.count == 0 {
            return 0.0;
        }
        self.sum.as_secs_f64() * 1000.0 / f64::from(self.count)
    }

    fn text(&self, bytes: usize) -> String {
        let avg = self.avg_ms();
        let rate = if avg > 0.0 {
            bytes as f64 / (avg / 1000.0) / 1e9
        } else {
            0.0
        };
        format!(
            "avg {avg:.2} ms min {:.2} max {:.2} ({rate:.2} GB/s)",
            self.min.unwrap_or_default().as_secs_f64() * 1000.0,
            self.max.as_secs_f64() * 1000.0,
        )
    }
}

/// Probe input: a source frame in ordinary (cached) heap memory.
struct Probe<'a> {
    device: &'a ID3D11Device,
    context: &'a ID3D11DeviceContext,
    width: u32,
    rows: usize,
    row_bytes: usize,
    source: Vec<u8>,
    iterations: u32,
}

/// Copies `rows` rows of `row_bytes` from `source` (tight pitch) into `target`, split by rows over
/// `threads` scoped threads (the caller's thread runs no slice; spawn cost is ~tens of µs).
///
/// # Safety
/// `target.base` must point to `rows` writable rows of `target.pitch >= row_bytes` bytes that nothing
/// else accesses until this returns.
unsafe fn fill(
    source: &[u8],
    row_bytes: usize,
    rows: usize,
    target: Target,
    threads: usize,
    write: Write,
) {
    let threads = threads.clamp(1, rows.max(1));
    std::thread::scope(|scope| {
        for slice in 0..threads {
            let begin = rows * slice / threads;
            let end = rows * (slice + 1) / threads;
            scope.spawn(move || {
                for row in begin..end {
                    let src = &source[row * row_bytes..(row + 1) * row_bytes];
                    let dst = (target.base + row * target.pitch) as *mut u8;
                    // SAFETY: the caller guarantees the row is writable and exclusively ours; the
                    // threads' row ranges are disjoint.
                    unsafe { copy_row(dst, src, write) };
                }
                if write == Write::NonTemporal {
                    fence();
                }
            });
        }
    });
}

/// # Safety
/// `dst` must be valid for `src.len()` byte writes.
unsafe fn copy_row(dst: *mut u8, src: &[u8], write: Write) {
    #[cfg(target_arch = "x86_64")]
    if write == Write::NonTemporal && (dst as usize).is_multiple_of(16) {
        use core::arch::x86_64::{__m128i, _mm_loadu_si128, _mm_stream_si128};
        let len = src.len();
        let mut offset = 0;
        while offset + 64 <= len {
            // SAFETY: SSE2 is part of x86_64; offset+64 <= len stays inside both rows and the
            // destination is 16-byte aligned at every multiple of 16.
            unsafe {
                let s = src.as_ptr().add(offset);
                let d = dst.add(offset);
                let a = _mm_loadu_si128(s.cast::<__m128i>());
                let b = _mm_loadu_si128(s.add(16).cast::<__m128i>());
                let c = _mm_loadu_si128(s.add(32).cast::<__m128i>());
                let e = _mm_loadu_si128(s.add(48).cast::<__m128i>());
                _mm_stream_si128(d.cast::<__m128i>(), a);
                _mm_stream_si128(d.add(16).cast::<__m128i>(), b);
                _mm_stream_si128(d.add(32).cast::<__m128i>(), c);
                _mm_stream_si128(d.add(48).cast::<__m128i>(), e);
            }
            offset += 64;
        }
        // SAFETY: the remaining tail is inside both rows.
        unsafe {
            std::ptr::copy_nonoverlapping(src.as_ptr().add(offset), dst.add(offset), len - offset)
        };
        return;
    }
    let _ = write;
    // SAFETY: the caller guarantees `src.len()` writable bytes at `dst`; heap source never overlaps.
    unsafe { std::ptr::copy_nonoverlapping(src.as_ptr(), dst, src.len()) };
}

fn fence() {
    #[cfg(target_arch = "x86_64")]
    // SAFETY: SSE is part of x86_64; orders this thread's streaming stores before it finishes.
    unsafe {
        core::arch::x86_64::_mm_sfence()
    };
}

impl Probe<'_> {
    fn bytes(&self) -> usize {
        self.row_bytes * self.rows
    }

    fn desc(
        &self,
        format: DXGI_FORMAT,
        usage: D3D11_USAGE,
        cpu: u32,
        bind: u32,
    ) -> D3D11_TEXTURE2D_DESC {
        let height = if format == DXGI_FORMAT_P010 {
            (self.rows * 2 / 3) as u32
        } else {
            self.rows as u32
        };
        let mut desc = texture_desc(self.width, height, format, bind);
        desc.Usage = usage;
        desc.CPUAccessFlags = cpu;
        desc
    }

    fn ram(&self, threads: usize, write: Write) -> String {
        let mut target = vec![0u8; self.bytes()];
        let mut timing = Timing::default();
        for i in 0..WARM_UP + self.iterations {
            let start = Instant::now();
            let base = Target {
                base: target.as_mut_ptr() as usize,
                pitch: self.row_bytes,
            };
            // SAFETY: `target` holds rows*row_bytes bytes and is only borrowed here.
            unsafe {
                fill(
                    &self.source,
                    self.row_bytes,
                    self.rows,
                    base,
                    threads,
                    write,
                )
            };
            if i >= WARM_UP {
                timing.add(start.elapsed());
            }
        }
        black_box(&target);
        format!("fill {}", timing.text(self.bytes()))
    }

    /// The production pattern: a ring of STAGING textures, Map(WRITE), fill, Unmap, CopyResource to a
    /// DEFAULT texture, Flush.
    fn staging(
        &self,
        format: DXGI_FORMAT,
        cpu: u32,
        threads: usize,
        write: Write,
    ) -> Result<String> {
        let desc = self.desc(format, D3D11_USAGE_STAGING, cpu, 0);
        let ring = (0..RING)
            .map(|_| texture(self.device, &desc, None))
            .collect::<Result<Vec<_>>>()?;
        let dst = texture(
            self.device,
            &self.desc(
                format,
                D3D11_USAGE_DEFAULT,
                0,
                D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ),
            None,
        )?;
        let map_type = if cpu & D3D11_CPU_ACCESS_READ.0 as u32 != 0 {
            D3D11_MAP_READ_WRITE
        } else {
            D3D11_MAP_WRITE
        };
        let (mut map, mut timing) = (Timing::default(), Timing::default());
        for i in 0..WARM_UP + self.iterations {
            let staging = &ring[i as usize % RING];
            let start = Instant::now();
            let target = self.map(staging, map_type)?;
            let mapped = Instant::now();
            // SAFETY: `map` checked that the mapping covers rows*pitch bytes; unmapped right after.
            unsafe {
                fill(
                    &self.source,
                    self.row_bytes,
                    self.rows,
                    target,
                    threads,
                    write,
                )
            };
            let filled = Instant::now();
            // SAFETY: the texture was mapped above on this context; matching single-subresource copy.
            unsafe {
                self.context.Unmap(staging, 0);
                self.context.CopyResource(&dst, staging);
                self.context.Flush();
            }
            if i >= WARM_UP {
                map.add(mapped - start);
                timing.add(filled - mapped);
            }
        }
        Ok(format!(
            "map avg {:.2} ms max {:.1}, fill {}",
            map.avg_ms(),
            map.max.as_secs_f64() * 1000.0,
            timing.text(self.bytes())
        ))
    }

    fn dynamic(&self, threads: usize, write: Write) -> Result<String> {
        let desc = self.desc(
            DXGI_FORMAT_R16_UNORM,
            D3D11_USAGE_DYNAMIC,
            D3D11_CPU_ACCESS_WRITE.0 as u32,
            D3D11_BIND_SHADER_RESOURCE.0 as u32,
        );
        let source = texture(self.device, &desc, None)?;
        let dst = texture(
            self.device,
            &self.desc(
                DXGI_FORMAT_R16_UNORM,
                D3D11_USAGE_DEFAULT,
                0,
                D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ),
            None,
        )?;
        let (mut map, mut timing) = (Timing::default(), Timing::default());
        for i in 0..WARM_UP + self.iterations {
            let start = Instant::now();
            let target = self.map(&source, D3D11_MAP_WRITE_DISCARD)?;
            let mapped = Instant::now();
            // SAFETY: as in `staging`.
            unsafe {
                fill(
                    &self.source,
                    self.row_bytes,
                    self.rows,
                    target,
                    threads,
                    write,
                )
            };
            let filled = Instant::now();
            // SAFETY: mapped above on this context; matching single-subresource copy.
            unsafe {
                self.context.Unmap(&source, 0);
                self.context.CopyResource(&dst, &source);
                self.context.Flush();
            }
            if i >= WARM_UP {
                map.add(mapped - start);
                timing.add(filled - mapped);
            }
        }
        Ok(format!(
            "map avg {:.2} ms max {:.1}, fill {}",
            map.avg_ms(),
            map.max.as_secs_f64() * 1000.0,
            timing.text(self.bytes())
        ))
    }

    /// One `UpdateSubresource` call from the heap frame into a DEFAULT texture (the runtime/driver
    /// does the copy on this thread), then Flush.
    fn update(&self) -> Result<String> {
        let dst = texture(
            self.device,
            &self.desc(
                DXGI_FORMAT_R16_UNORM,
                D3D11_USAGE_DEFAULT,
                0,
                D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ),
            None,
        )?;
        let (mut call, mut flush) = (Timing::default(), Timing::default());
        for i in 0..WARM_UP + self.iterations {
            let start = Instant::now();
            // SAFETY: `source` holds rows tightly packed at row_bytes, covering the whole texture.
            unsafe {
                self.context.UpdateSubresource(
                    &dst,
                    0,
                    None,
                    self.source.as_ptr().cast(),
                    self.row_bytes as u32,
                    0,
                );
            }
            let called = Instant::now();
            // SAFETY: plain flush of this probe's own immediate context.
            unsafe { self.context.Flush() };
            if i >= WARM_UP {
                call.add(called - start);
                flush.add(called.elapsed());
            }
        }
        // SAFETY: owned device; reports a removal the void calls above could not.
        unsafe { self.device.GetDeviceRemovedReason()? };
        Ok(format!(
            "call {}, flush avg {:.2} ms",
            call.text(self.bytes()),
            flush.avg_ms()
        ))
    }

    /// Reads up to 8 MB from a mapping on one thread. Write-combined (uncached) memory reads at a
    /// small fraction of RAM speed, cached memory at RAM speed.
    fn read(&self, cpu: u32) -> Result<String> {
        let staging = texture(
            self.device,
            &self.desc(DXGI_FORMAT_R16_UNORM, D3D11_USAGE_STAGING, cpu, 0),
            None,
        )?;
        let map_type = if cpu & D3D11_CPU_ACCESS_READ.0 as u32 != 0 {
            D3D11_MAP_READ_WRITE
        } else {
            D3D11_MAP_WRITE
        };
        let target = self.map(&staging, map_type)?;
        let rows = self.rows.min((8 << 20) / target.pitch).max(1);
        let start = Instant::now();
        let mut sum = 0u64;
        for row in 0..rows {
            // SAFETY: `map` checked rows*pitch mapped bytes; row < rows <= self.rows; u64 reads
            // stay inside row_bytes (a multiple of 8) and the pitch is 8-byte aligned.
            let words = unsafe {
                std::slice::from_raw_parts(
                    (target.base + row * target.pitch) as *const u64,
                    self.row_bytes / 8,
                )
            };
            for &word in words {
                sum = sum.wrapping_add(word);
            }
        }
        let elapsed = start.elapsed();
        black_box(sum);
        // SAFETY: mapped above on this context.
        unsafe { self.context.Unmap(&staging, 0) };
        let bytes = rows * self.row_bytes;
        Ok(format!(
            "{:.1} MB in {:.2} ms ({:.2} GB/s)",
            bytes as f64 / 1e6,
            elapsed.as_secs_f64() * 1000.0,
            bytes as f64 / elapsed.as_secs_f64().max(1e-9) / 1e9
        ))
    }

    fn map(&self, texture: &ID3D11Texture2D, map_type: D3D11_MAP) -> Result<Target> {
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        // SAFETY: an unmapped probe texture with matching CPU access, on this probe's context.
        unsafe {
            self.context
                .Map(texture, 0, map_type, 0, Some(&mut mapped))?
        };
        let pitch = mapped.RowPitch as usize;
        let valid = !mapped.pData.is_null()
            && pitch >= self.row_bytes
            && pitch.is_multiple_of(16)
            && pitch
                .checked_mul(self.rows)
                .is_some_and(|n| n <= isize::MAX as usize);
        if !valid {
            // SAFETY: mapped just above.
            unsafe { self.context.Unmap(texture, 0) };
            return Err(E_FAIL.into());
        }
        Ok(Target {
            base: mapped.pData as usize,
            pitch,
        })
    }
}

/// Runs every variant on `device` for a `width` (multiple of 8) x `rows` (multiple of 3) R16 frame and
/// returns the report. Errors are reported per variant as HRESULTs only.
pub fn run(
    device: &ID3D11Device,
    context: &ID3D11DeviceContext,
    width: u32,
    rows: u32,
    iterations: u32,
) -> String {
    let row_bytes = width as usize * 2;
    let rows = rows as usize;
    let probe = Probe {
        device,
        context,
        width,
        rows,
        row_bytes,
        source: (0..row_bytes * rows)
            .map(|i| (i * 7 + i / 4093) as u8)
            .collect(),
        iterations: iterations.max(1),
    };
    let mut report = format!(
        "Upload probe: {width}x{rows} R16 ({:.1} MB per frame), {} frames per variant after {WARM_UP} warm-up, {THREADS} threads unless noted\n",
        probe.bytes() as f64 / 1e6,
        probe.iterations,
    );
    let write = D3D11_CPU_ACCESS_WRITE.0 as u32;
    let read_write = (D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE).0 as u32;
    let cases: Vec<Case<'_>> = vec![
        ("RAM copy 1T", Box::new(|| Ok(probe.ram(1, Write::Copy)))),
        ("RAM copy", Box::new(|| Ok(probe.ram(THREADS, Write::Copy)))),
        (
            "staging write 1T",
            Box::new(|| probe.staging(DXGI_FORMAT_R16_UNORM, write, 1, Write::Copy)),
        ),
        (
            "staging write",
            Box::new(|| probe.staging(DXGI_FORMAT_R16_UNORM, write, THREADS, Write::Copy)),
        ),
        (
            "staging write 6T",
            Box::new(|| probe.staging(DXGI_FORMAT_R16_UNORM, write, 6, Write::Copy)),
        ),
        (
            "staging write stream",
            Box::new(|| probe.staging(DXGI_FORMAT_R16_UNORM, write, THREADS, Write::NonTemporal)),
        ),
        (
            "staging P010 write",
            Box::new(|| probe.staging(DXGI_FORMAT_P010, write, THREADS, Write::Copy)),
        ),
        (
            "staging read+write",
            Box::new(|| probe.staging(DXGI_FORMAT_R16_UNORM, read_write, THREADS, Write::Copy)),
        ),
        (
            "dynamic discard",
            Box::new(|| probe.dynamic(THREADS, Write::Copy)),
        ),
        (
            "dynamic discard stream",
            Box::new(|| probe.dynamic(THREADS, Write::NonTemporal)),
        ),
        ("UpdateSubresource 1T", Box::new(|| probe.update())),
        ("read staging write-only", Box::new(|| probe.read(write))),
        (
            "read staging read+write",
            Box::new(|| probe.read(read_write)),
        ),
    ];
    for (label, case) in cases {
        let line = case().unwrap_or_else(|error| format!("FAILS 0x{:08X}", error.code().0 as u32));
        let _ = writeln!(report, "  {label:<24} {line}");
    }
    report
}

/// Creates a hardware device (never WARP) and runs the 4K probe.
pub fn run_hardware(iterations: u32) -> String {
    let mut device = None;
    let mut context = None;
    // SAFETY: no adapter or software module; out-pointers are live locals.
    let created = unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
    };
    match (created, device, context) {
        (Ok(()), Some(device), Some(context)) => {
            run(&device, &context, FRAME_WIDTH, FRAME_ROWS, iterations)
        }
        (Err(error), _, _) => format!(
            "Upload probe: hardware device FAILS 0x{:08X}\n",
            error.code().0 as u32
        ),
        _ => "Upload probe: hardware device FAILS (no device)\n".to_owned(),
    }
}
