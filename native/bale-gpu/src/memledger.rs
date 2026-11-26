//! Memory ledger ABI 1 (independent of the GPU/decoder ABIs). Fixed category IDs:
//! 1 tone map, 2 film grain, 3 subtitles, 4 engine target/staging, 5 zero-copy resources;
//! 6–15 reserved for bale_gpu; 16 d3d11va decoder texture array (slices), 17 stage output,
//! 18 dav1d upload pool/staging, 19 rav1g output images, 20 rav1g process device present, 21 decoder copy
//! pool/rav1g direct-copy textures, 22 rav1g decoder buffers (streams); 23–31 reserved for
//! FFmpegInteropX; 32 player swap chain (estimate); 33–63 reserved.
//! 0 is unassigned. See `include/bale_mem.h` and docs/gpu-tone-map.md. Every build accounts (bale_gpu and the
//! FFmpegInteropX fork call `bale_mem_account`); reading the ledger back is a debug probe (`debug-probes` feature).

use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};

#[cfg(windows)]
mod gpu;
#[cfg(windows)]
pub use gpu::{buffer_bytes, texture_bytes};
#[cfg(windows)]
pub(crate) use gpu::{track_buffer, track_committed, track_swap_chain, track_texture};

pub const TONE_MAP: u32 = 1;
pub const FILM_GRAIN: u32 = 2;
pub const SUBTITLES: u32 = 3;
pub const ENGINE: u32 = 4;
pub const ZERO_COPY: u32 = 5;
const CATEGORIES: usize = 64;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(C)]
pub struct BaleMemEntry {
    pub category: u32,
    pub count: i32,
    pub bytes: i64,
    pub peak_bytes: i64,
}

struct Category {
    used: AtomicBool,
    bytes: AtomicI64,
    count: AtomicI64,
    peak: AtomicI64,
    underflows: AtomicU64,
}

impl Category {
    const fn new() -> Self {
        Self {
            used: AtomicBool::new(false),
            bytes: AtomicI64::new(0),
            count: AtomicI64::new(0),
            peak: AtomicI64::new(0),
            underflows: AtomicU64::new(0),
        }
    }

    fn account(&self, delta_bytes: i64, delta_count: i32) {
        // Resource destruction may call us while D3D11 holds its own locks, on any thread.
        // No lock (including a spin lock), allocation, callback or D3D call is allowed here.
        let (bytes, byte_underflow) = adjust(&self.bytes, delta_bytes, i64::MAX);
        let (_, count_underflow) = adjust(&self.count, i64::from(delta_count), i64::from(i32::MAX));
        if byte_underflow || count_underflow {
            // One error per account call, even if both counters underflow. Never wraps.
            let mut value = self.underflows.load(Ordering::Relaxed);
            loop {
                match self.underflows.compare_exchange_weak(
                    value,
                    value.saturating_add(1),
                    Ordering::Relaxed,
                    Ordering::Relaxed,
                ) {
                    Ok(_) => break,
                    Err(current) => value = current,
                }
            }
        }
        self.peak.fetch_max(bytes, Ordering::Relaxed);
        self.used.store(true, Ordering::Release);
    }
}

/// CAS retry is lock-free: a suspended writer can never prevent another caller's progress.
/// Keep count in an i64 atomic too, capped at i32::MAX, to share the exact clamp logic.
fn adjust(counter: &AtomicI64, delta: i64, ceiling: i64) -> (i64, bool) {
    let mut current = counter.load(Ordering::Relaxed);
    loop {
        let sum = current.saturating_add(delta);
        let next = sum.max(0).min(ceiling);
        match counter.compare_exchange_weak(current, next, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => return (next, sum < 0),
            Err(value) => current = value,
        }
    }
}

struct Ledger([Category; CATEGORIES]);

impl Ledger {
    const fn new() -> Self {
        Self([const { Category::new() }; CATEGORIES])
    }

    fn account(&self, category: u32, bytes: i64, count: i32) {
        if let Some(entry) = self.0.get(category as usize) {
            entry.account(bytes, count);
        }
    }

    #[cfg(any(feature = "debug-probes", test))]
    fn snapshot(&self, mut write: impl FnMut(BaleMemEntry)) -> u32 {
        let mut needed = 0;
        for (category, entry) in self.0.iter().enumerate() {
            if entry.used.load(Ordering::Acquire) {
                let bytes = entry.bytes.load(Ordering::Relaxed);
                write(BaleMemEntry {
                    category: category as u32,
                    count: entry.count.load(Ordering::Relaxed) as i32,
                    bytes,
                    // Counters are independently sampled; an account can be between its byte
                    // and peak updates, so never show a peak below the observed current bytes.
                    peak_bytes: entry.peak.load(Ordering::Relaxed).max(bytes),
                });
                needed += 1;
            }
        }
        needed
    }

    #[cfg(any(feature = "debug-probes", test))]
    fn reset_peaks(&self) {
        for entry in &self.0 {
            entry
                .peak
                .store(entry.bytes.load(Ordering::Relaxed), Ordering::Relaxed);
        }
    }
}

static LEDGER: Ledger = Ledger::new();

#[cfg(test)]
static TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[unsafe(no_mangle)]
pub extern "C" fn bale_mem_abi_version() -> u32 {
    1
}

/// Saturating counters: underflows clamp independently at zero, positive overflows at MAX.
/// Invalid categories are ignored. Calls allocate nothing and never panic.
#[unsafe(no_mangle)]
pub extern "C" fn bale_mem_account(category: u32, delta_bytes: i64, delta_count: i32) {
    LEDGER.account(category, delta_bytes, delta_count);
}

/// Returns the TOTAL needed, writing only min(capacity, total) entries, in ascending ID order.
/// Null out is a size query (no writes regardless of capacity). Ever-used zero entries remain.
/// Counters are independently sampled during concurrent accounting; no lock or D3D call is taken.
///
/// # Safety
/// A non-null `out` points to `capacity` writable BaleMemEntry slots.
#[cfg(any(feature = "debug-probes", test))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_mem_snapshot(out: *mut BaleMemEntry, capacity: u32) -> u32 {
    let mut written = 0;
    LEDGER.snapshot(|entry| {
        if !out.is_null() && written < capacity {
            // SAFETY: caller provides capacity slots; written is strictly below capacity.
            unsafe { out.add(written as usize).write_unaligned(entry) };
            written += 1;
        }
    })
}

/// Sets each peak to its current bytes, leaving residency, history and underflow counts intact.
#[cfg(any(feature = "debug-probes", test))]
#[unsafe(no_mangle)]
pub extern "C" fn bale_mem_reset_peaks() {
    LEDGER.reset_peaks();
}

/// Lifetime number of underflowing account calls for a category; 0 for an invalid category.
#[cfg(any(feature = "debug-probes", test))]
#[unsafe(no_mangle)]
pub extern "C" fn bale_mem_underflows(category: u32) -> u64 {
    LEDGER
        .0
        .get(category as usize)
        .map_or(0, |entry| entry.underflows.load(Ordering::Relaxed))
}

/// Paired allocation/release accounting. GPU objects keep this in COM private data, so views,
/// cloned interfaces and queued bindings keep the entry alive until the final resource Release.
pub struct Tracked {
    category: u32,
    bytes: i64,
    count: i32,
}

impl Tracked {
    pub fn new(category: u32, bytes: u64) -> Self {
        Self::with_count(category, bytes, 1)
    }

    fn with_count(category: u32, bytes: u64, count: i32) -> Self {
        let bytes = bytes.min(i64::MAX as u64) as i64;
        bale_mem_account(category, bytes, count);
        Self {
            category,
            bytes,
            count,
        }
    }
}

impl Drop for Tracked {
    fn drop(&mut self) {
        bale_mem_account(self.category, -self.bytes, -self.count);
    }
}

/// Logical texture bytes only: Xbox tiling/alignment padding is not counted. DXGI format numbers
/// are used here so the same size routine is tested on Linux without a Windows SDK dependency.
/// MipLevels=0 means the full chain, with each dimension floored at 1; array and MSAA multiply it.
#[cfg(any(windows, test))]
fn texture_size(width: u64, height: u32, format: i32, mips: u32, array: u32, samples: u32) -> u64 {
    if width == 0 || height == 0 {
        return 0;
    }
    let levels = 64 - width.max(u64::from(height)).leading_zeros();
    let mips = if mips == 0 { levels } else { mips.min(levels) };
    let mut bytes = 0u64;
    for level in 0..mips {
        let w = (width >> level).max(1);
        let h = (u64::from(height) >> level).max(1);
        let pixels = w.saturating_mul(h);
        let mip = match format {
            1..=4 => pixels.saturating_mul(16),       // R32G32B32A32
            5..=8 => pixels.saturating_mul(12),       // R32G32B32
            9..=22 | 102 => pixels.saturating_mul(8), // RGBA16F, RG32, depth/stencil, Y416
            48..=59 | 85..=86 | 115 => pixels.saturating_mul(2), // RG8, R16, B5/B4
            60..=65 | 111..=113 => pixels,            // R8, A8, palettes
            66 => w.div_ceil(8).saturating_mul(h),    // R1
            103 | 106 => pixels.saturating_add(pixels.div_ceil(2)), // NV12 / 420_OPAQUE
            104..=105 => pixels.saturating_mul(3),    // P010 / P016
            68..=69 | 107 => w.div_ceil(2).saturating_mul(4).saturating_mul(h), // packed pairs
            108..=109 => w.div_ceil(2).saturating_mul(8).saturating_mul(h), // Y210 / Y216
            70..=72 | 79..=81 => w
                .div_ceil(4)
                .saturating_mul(h.div_ceil(4))
                .saturating_mul(8), // BC1/4
            73..=78 | 82..=84 | 94..=99 => w
                .div_ceil(4)
                .saturating_mul(h.div_ceil(4))
                .saturating_mul(16), // BC2/3/5/6/7
            _ => pixels.saturating_mul(4),            // RGBA8, BGRA8, RGB10A2, RG16, R32, unknown
        };
        bytes = bytes.saturating_add(mip);
    }
    bytes
        .saturating_mul(u64::from(array))
        .saturating_mul(u64::from(samples.max(1)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn texture_sizes_cover_video_formats_mips_arrays_and_blocks() {
        for (format, bytes) in [
            (103, 3),
            (104, 6),
            (105, 6),
            (28, 8),
            (87, 8),
            (24, 8),
            (10, 16),
        ] {
            assert_eq!(texture_size(2, 1, format, 1, 1, 1), bytes);
        }
        assert_eq!(texture_size(1920, 1080, 103, 1, 2, 1), 1920 * 1080 * 3);
        assert_eq!(texture_size(8, 4, 28, 0, 2, 1), (32 + 8 + 2 + 1) * 4 * 2);
        assert_eq!(texture_size(8, 4, 24, 2, 1, 4), (32 + 8) * 4 * 4);
        assert_eq!(texture_size(1, 1, 71, 1, 1, 1), 8);
        assert_eq!(texture_size(5, 5, 98, 1, 1, 1), 64);
        assert_eq!(texture_size(8, 4, -1, 1, 1, 1), 128);
        assert_eq!(texture_size(0, 4, 10, 0, 1, 1), 0);
        assert_eq!(
            texture_size(u64::MAX, u32::MAX, 10, 0, u32::MAX, 8),
            u64::MAX
        );
    }

    #[test]
    fn account_snapshot_underflow_peaks_and_saturation() {
        let ledger = Ledger::new();
        let mut rows = Vec::new();
        assert_eq!(ledger.snapshot(|e| rows.push(e)), 0);
        ledger.account(64, 123, 1);
        ledger.account(u32::MAX, 123, 1);
        ledger.account(2, 100, 2);
        ledger.account(2, -40, -1);
        assert_eq!(ledger.snapshot(|e| rows.push(e)), 1);
        assert_eq!(
            rows,
            [BaleMemEntry {
                category: 2,
                count: 1,
                bytes: 60,
                peak_bytes: 100
            }]
        );
        ledger.reset_peaks();
        rows.clear();
        ledger.snapshot(|e| rows.push(e));
        assert_eq!(rows[0].peak_bytes, 60);
        ledger.account(2, i64::MIN, i32::MIN);
        assert_eq!(ledger.0[2].underflows.load(Ordering::Relaxed), 1);
        ledger.reset_peaks();
        rows.clear();
        assert_eq!(ledger.snapshot(|e| rows.push(e)), 1); // ever used, even after peak reset
        assert_eq!(
            rows[0],
            BaleMemEntry {
                category: 2,
                ..Default::default()
            }
        );
        ledger.account(2, i64::MAX, i32::MAX);
        ledger.account(2, 1, 1);
        rows.clear();
        ledger.snapshot(|e| rows.push(e));
        assert_eq!(rows[0].bytes, i64::MAX);
        assert_eq!(rows[0].count, i32::MAX);
    }

    #[test]
    fn concurrent_accounting_and_snapshots_do_not_lose_updates() {
        let ledger = Ledger::new();
        std::thread::scope(|scope| {
            for _ in 0..8 {
                scope.spawn(|| {
                    for _ in 0..2000 {
                        ledger.account(1, 8, 1);
                        ledger.snapshot(|e| {
                            assert!((0..=8 * 8).contains(&e.bytes));
                            assert!((0..=8).contains(&e.count));
                            assert!(e.peak_bytes >= e.bytes);
                        });
                        ledger.reset_peaks();
                        ledger.account(1, -8, -1);
                    }
                });
            }
        });
        ledger.snapshot(|e| assert_eq!((e.bytes, e.count), (0, 0)));
        assert_eq!(ledger.0[1].underflows.load(Ordering::Relaxed), 0);
    }

    #[test]
    fn snapshot_samples_an_incomplete_account_without_waiting() {
        let ledger = Ledger::new();
        // A writer may be suspended after its byte CAS and before its count/peak updates.
        ledger.0[1].used.store(true, Ordering::Release);
        ledger.0[1].bytes.store(4096, Ordering::Relaxed);
        assert_eq!(
            ledger.snapshot(|e| {
                assert_eq!(e.bytes, 4096);
                assert_eq!(e.count, 0);
                assert_eq!(e.peak_bytes, 4096);
            }),
            1
        );
        ledger.account(2, 8, -1); // count underflows, bytes survive
        ledger.account(2, -9, 2); // bytes underflow, count survives
        assert_eq!(ledger.0[2].bytes.load(Ordering::Relaxed), 0);
        assert_eq!(ledger.0[2].count.load(Ordering::Relaxed), 2);
        assert_eq!(ledger.0[2].underflows.load(Ordering::Relaxed), 2);
    }

    #[test]
    fn ffi_capacity_layout_and_guard_drop() {
        let _serial = TEST_LOCK.lock().unwrap();
        assert_eq!(bale_mem_abi_version(), 1);
        assert_eq!(size_of::<BaleMemEntry>(), 24);
        assert_eq!(std::mem::offset_of!(BaleMemEntry, bytes), 8);
        assert_eq!(std::mem::offset_of!(BaleMemEntry, peak_bytes), 16);
        let guard = Tracked::new(62, 4096);
        let zero = Tracked::new(63, 0); // unknown size, but still resident
        let snapshot = || {
            let mut rows = [BaleMemEntry::default(); CATEGORIES];
            // SAFETY: the array holds 64 writable slots, the maximum category count.
            let needed = unsafe { bale_mem_snapshot(rows.as_mut_ptr(), CATEGORIES as u32) };
            (rows, needed)
        };
        let row = || snapshot().0.into_iter().find(|e| e.category == 62).unwrap();
        // Other Windows tests can have used the GPU categories already; only 62/63 belong here.
        let (all, needed) = snapshot();
        assert!(needed >= 2);
        let mut short = [BaleMemEntry::default(); 2];
        // SAFETY: null is a size query; short has more than its advertised one writable slot.
        unsafe {
            assert_eq!(bale_mem_snapshot(std::ptr::null_mut(), 0), needed);
            assert_eq!(bale_mem_snapshot(std::ptr::null_mut(), 64), needed);
            assert_eq!(bale_mem_snapshot(short.as_mut_ptr(), 0), needed);
            assert_eq!(short[0], BaleMemEntry::default());
            assert_eq!(bale_mem_snapshot(short.as_mut_ptr(), 1), needed);
        }
        assert_eq!(short[0], all[0]);
        assert_eq!(short[1], BaleMemEntry::default());
        assert_eq!(
            row(),
            BaleMemEntry {
                category: 62,
                count: 1,
                bytes: 4096,
                peak_bytes: 4096
            }
        );
        assert_eq!(all.into_iter().find(|e| e.category == 63).unwrap().count, 1);
        drop((guard, zero));
        assert_eq!((row().bytes, row().count, row().peak_bytes), (0, 0, 4096));
        bale_mem_reset_peaks();
        assert_eq!(row().peak_bytes, 0);
        bale_mem_account(62, -1, -1);
        assert_eq!(bale_mem_underflows(62), 1);
        assert_eq!(bale_mem_underflows(64), 0);
    }
}
