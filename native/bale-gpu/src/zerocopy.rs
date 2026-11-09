//! Debug-only zero-copy feasibility probe (docs/player-plan.md, D3D12 section).
//!
//! Today every 4K software frame is written twice: dav1d decodes into ordinary memory, then swscale
//! writes P010 into a mapped D3D11 STAGING texture (~25 MB per frame, at the memory-bandwidth floor
//! in the console's input-idle slow state). The zero-copy route lets dav1d decode straight into a
//! persistently mapped D3D12 CUSTOM heap (write-back, L0) that the GPU reads, and hands the result
//! to Media Foundation's D3D11 device through a shared texture and a shared fence. Before any FFmpeg
//! work, this probe answers:
//! 1. Is the CPU as fast on that memory as on ordinary RAM? dav1d also reads its own output
//!    (reference frames, intra edges), so reads matter as much as writes.
//! 2. Does the GPU read it, and see plain CPU stores without Unmap or flushes (cache-coherent UMA)?
//! 3. Can D3D11 open a D3D12 P010 texture and fence in App mode, wait on the fence, and see the data?
//!
//! `d3d12.dll` is loaded by the caller (see [`crate::d3d12`]). Errors are HRESULTs only. Nothing
//! here is used by playback.

use std::fmt::Write as _;
use std::hint::black_box;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{E_FAIL, HMODULE, WAIT_OBJECT_0};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
use windows::core::{Interface, PCWSTR, Result};

use crate::d3d12::{create_device, fails, resource};
use crate::d3d12_repack::{Handle, borrowed, committed, heap, map, transition, write_back};
use crate::grain::gpu::{texture, texture_desc};
use crate::upload::{Target, Timing, Write, fill};

/// A 3840x2160 10-bit 4:2:0 frame as one R16 surface (2160 luma rows plus 1080 chroma rows of 7680
/// bytes), the same bytes as one P010 frame with a 7680-byte pitch.
const WIDTH: u32 = 3840;
const LUMA_ROWS: u32 = 2160;
const ROWS: u32 = 3240;
const ROW_BYTES: usize = WIDTH as usize * 2;
const BYTES: usize = ROW_BYTES * ROWS as usize;
const WARM_UP: u32 = 2;
/// Production swscale/sliced-copy thread count, as in the upload probe.
const THREADS: usize = 4;
const WAIT_MS: u32 = 5000;
const GENERIC_ALL: u32 = 0x1000_0000;
const SEED_COHERENCY: u64 = 0xC0FF_EE00_0000_0001;
const SEED_SHARE: u64 = 0x5EED_0000_0000_0002;

/// One DIRECT queue with a reusable allocator, list and CPU-waitable fence. Every submission is
/// waited for before the next one, so the single allocator is never reset while in use.
struct Gpu {
    queue: ID3D12CommandQueue,
    allocator: ID3D12CommandAllocator,
    list: ID3D12GraphicsCommandList,
    fence: ID3D12Fence,
    event: Handle,
    value: u64,
}

impl Gpu {
    fn new(device: &ID3D12Device) -> Result<Self> {
        let desc = D3D12_COMMAND_QUEUE_DESC {
            Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
            ..Default::default()
        };
        // SAFETY: live device; descriptors outlive the calls; the list is closed before first use.
        unsafe {
            let queue: ID3D12CommandQueue = device.CreateCommandQueue(&desc)?;
            let allocator: ID3D12CommandAllocator =
                device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)?;
            let list: ID3D12GraphicsCommandList = device.CreateCommandList(
                0,
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                &allocator,
                None::<&ID3D12PipelineState>,
            )?;
            list.Close()?;
            let fence: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE)?;
            let event = Handle(CreateEventW(None, false, false, PCWSTR::null())?);
            Ok(Self {
                queue,
                allocator,
                list,
                fence,
                event,
                value: 0,
            })
        }
    }

    /// Records one command list, submits it and returns the fence value that marks it done. The
    /// caller must `wait` for it before the next `execute`.
    fn execute(&mut self, record: impl FnOnce(&ID3D12GraphicsCommandList)) -> Result<u64> {
        // SAFETY: the previous submission was waited for, so the allocator and list are idle.
        unsafe {
            self.allocator.Reset()?;
            self.list
                .Reset(&self.allocator, None::<&ID3D12PipelineState>)?;
        }
        record(&self.list);
        // SAFETY: the list was recorded above on this thread and is executed once.
        unsafe {
            self.list.Close()?;
            self.queue
                .ExecuteCommandLists(&[Some(self.list.cast::<ID3D12CommandList>()?)]);
            self.value += 1;
            self.queue.Signal(&self.fence, self.value)?;
        }
        Ok(self.value)
    }

    fn wait(&self, value: u64) -> Result<()> {
        // SAFETY: live fence and event owned by this struct.
        unsafe {
            if self.fence.GetCompletedValue() < value {
                self.fence.SetEventOnCompletion(value, self.event.0)?;
                if WaitForSingleObject(self.event.0, WAIT_MS) != WAIT_OBJECT_0 {
                    return Err(E_FAIL.into());
                }
            }
        }
        Ok(())
    }
}

impl Drop for Gpu {
    fn drop(&mut self) {
        // Error paths can leave a submission in flight; never release the queue under it.
        let _ = self.wait(self.value);
    }
}

fn page_name(page: D3D12_CPU_PAGE_PROPERTY) -> &'static str {
    match page {
        D3D12_CPU_PAGE_PROPERTY_NOT_AVAILABLE => "not-available",
        D3D12_CPU_PAGE_PROPERTY_WRITE_COMBINE => "write-combine",
        D3D12_CPU_PAGE_PROPERTY_WRITE_BACK => "write-back",
        _ => "unknown",
    }
}

fn pool_name(pool: D3D12_MEMORY_POOL) -> &'static str {
    match pool {
        D3D12_MEMORY_POOL_L0 => "L0",
        D3D12_MEMORY_POOL_L1 => "L1",
        _ => "unknown",
    }
}

fn buffer(
    device: &ID3D12Device,
    properties: &D3D12_HEAP_PROPERTIES,
    bytes: u64,
    state: D3D12_RESOURCE_STATES,
) -> Result<ID3D12Resource> {
    let desc = resource(
        D3D12_RESOURCE_DIMENSION_BUFFER,
        bytes,
        1,
        DXGI_FORMAT_UNKNOWN,
        D3D12_RESOURCE_FLAG_NONE,
    );
    committed(device, properties, D3D12_HEAP_FLAG_NONE, &desc, state)
}

fn texture_2d(width: u32, height: u32, format: DXGI_FORMAT) -> D3D12_RESOURCE_DESC {
    let mut desc = resource(
        D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        u64::from(width),
        height,
        format,
        D3D12_RESOURCE_FLAG_NONE,
    );
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc
}

fn subresource(resource: &ID3D12Resource, index: u32) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION {
        pResource: borrowed(resource),
        Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            SubresourceIndex: index,
        },
    }
}

fn placed(
    resource: &ID3D12Resource,
    layout: D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION {
        pResource: borrowed(resource),
        Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            PlacedFootprint: layout,
        },
    }
}

/// Buffer layout of each subresource of `desc` from offset 0: (footprint, rows, row bytes), total.
fn footprints<const N: usize>(
    device: &ID3D12Device,
    desc: &D3D12_RESOURCE_DESC,
) -> ([(D3D12_PLACED_SUBRESOURCE_FOOTPRINT, u32, u64); N], u64) {
    let mut layouts = [D3D12_PLACED_SUBRESOURCE_FOOTPRINT::default(); N];
    let mut rows = [0u32; N];
    let mut sizes = [0u64; N];
    let mut total = 0u64;
    // SAFETY: every output array holds N entries; N is the subresource count asked for.
    unsafe {
        device.GetCopyableFootprints(
            desc,
            0,
            N as u32,
            0,
            Some(layouts.as_mut_ptr()),
            Some(rows.as_mut_ptr()),
            Some(sizes.as_mut_ptr()),
            Some(&mut total),
        );
    }
    (
        std::array::from_fn(|i| (layouts[i], rows[i], sizes[i])),
        total,
    )
}

fn time(iterations: u32, mut run: impl FnMut()) -> Timing {
    let mut timing = Timing::default();
    for i in 0..WARM_UP + iterations {
        let start = Instant::now();
        run();
        if i >= WARM_UP {
            timing.add(start.elapsed());
        }
    }
    timing
}

/// Sums `bytes` (a multiple of 8) at `base` as u64 words over `threads` scoped threads.
///
/// # Safety
/// `base` must be 8-byte aligned and readable for `bytes` bytes that nobody writes meanwhile.
unsafe fn read_sum(base: usize, bytes: usize, threads: usize) -> u64 {
    let words = bytes / 8;
    std::thread::scope(|scope| {
        let parts: Vec<_> = (0..threads)
            .map(|part| {
                let begin = words * part / threads;
                let end = words * (part + 1) / threads;
                scope.spawn(move || {
                    // SAFETY: the caller guarantees `words` readable aligned words; ranges are
                    // disjoint and in bounds.
                    let slice = unsafe {
                        std::slice::from_raw_parts((base as *const u64).add(begin), end - begin)
                    };
                    slice.iter().fold(0u64, |sum, &word| sum.wrapping_add(word))
                })
            })
            .collect();
        parts
            .into_iter()
            .map(|part| part.join().unwrap_or(0))
            .fold(0u64, u64::wrapping_add)
    })
}

fn word(index: usize, seed: u64) -> u64 {
    (index as u64 ^ seed).wrapping_mul(0x9E37_79B9_7F4A_7C15)
}

/// Writes the seeded test pattern over `BYTES` at `base` with plain (cached) stores, one thread.
///
/// # Safety
/// `base` must be 8-byte aligned and writable for `BYTES` bytes that nothing else accesses.
unsafe fn stamp(base: usize, seed: u64) {
    let words = base as *mut u64;
    for index in 0..BYTES / 8 {
        // SAFETY: index < BYTES / 8 words, guaranteed writable by the caller.
        unsafe { words.add(index).write(word(index, seed)) };
    }
}

/// Counts words at `base` that differ from the seeded pattern.
///
/// # Safety
/// As for [`stamp`], readable.
unsafe fn mismatches(base: usize, seed: u64) -> usize {
    // SAFETY: the caller guarantees BYTES readable aligned bytes.
    let words = unsafe { std::slice::from_raw_parts(base as *const u64, BYTES / 8) };
    words
        .iter()
        .enumerate()
        .filter(|&(index, &value)| value != word(index, seed))
        .count()
}

/// Times the CPU on `BYTES` at `base`: copies from `source` (1 thread, THREADS threads, THREADS
/// threads with streaming stores) and a THREADS-thread read.
///
/// # Safety
/// `base` must be 16-byte aligned and readable and writable for `BYTES` bytes that nothing else
/// (CPU or GPU) accesses until this returns.
unsafe fn cpu_cases(report: &mut String, label: &str, base: usize, source: &[u8], iterations: u32) {
    let target = Target {
        base,
        pitch: ROW_BYTES,
    };
    for (suffix, threads, write) in [
        ("fill 1T", 1, Write::Copy),
        ("fill", THREADS, Write::Copy),
        ("fill stream", THREADS, Write::NonTemporal),
    ] {
        let timing = time(iterations, || {
            // SAFETY: the caller guarantees ROWS rows of ROW_BYTES at `base`, exclusively ours.
            unsafe { fill(source, ROW_BYTES, ROWS as usize, target, threads, write) }
        });
        let _ = writeln!(
            report,
            "  {:<24} {}",
            format!("{label} {suffix}"),
            timing.text(BYTES)
        );
    }
    let timing = time(iterations, || {
        // SAFETY: as above, read only.
        black_box(unsafe { read_sum(base, BYTES, THREADS) });
    });
    let _ = writeln!(
        report,
        "  {:<24} {}",
        format!("{label} read"),
        timing.text(BYTES)
    );
}

/// Runs the probe with `module` (a loaded `d3d12.dll`, or null) and returns the plain-text report.
///
/// # Safety
/// `module` must be null or a module handle that stays loaded for the call.
pub unsafe fn run(module: HMODULE, iterations: u32) -> String {
    let iterations = iterations.max(1);
    let mut report = format!(
        "Zero-copy probe: {WIDTH}x{ROWS} R16 ({:.1} MB per frame), {iterations} frames per variant after {WARM_UP} warm-up, {THREADS} threads unless noted\n",
        BYTES as f64 / 1e6
    );
    // SAFETY: forwarded caller guarantee.
    let device = match unsafe { create_device(module) } {
        Ok(device) => device,
        Err(line) => {
            let _ = writeln!(report, "  {line}");
            return report;
        }
    };
    for (label, kind) in [
        ("UPLOAD heap is", D3D12_HEAP_TYPE_UPLOAD),
        ("READBACK heap is", D3D12_HEAP_TYPE_READBACK),
    ] {
        // SAFETY: live device; node 0 is the only adapter node.
        let properties = unsafe { device.GetCustomHeapProperties(0, kind) };
        let _ = writeln!(
            report,
            "  {label:<24} {} {}",
            page_name(properties.CPUPageProperty),
            pool_name(properties.MemoryPoolPreference)
        );
    }

    let custom = buffer(
        &device,
        &write_back(),
        BYTES as u64,
        D3D12_RESOURCE_STATE_COMMON,
    )
    .and_then(|custom| map(&custom).map(|base| (custom, base)));
    let (custom, custom_base) = match custom {
        Ok(custom) => {
            report.push_str("  custom WB L0 buffer      OK (mapped)\n");
            custom
        }
        Err(error) => {
            let _ = writeln!(report, "  custom WB L0 buffer      {}", fails(&error));
            return report;
        }
    };
    let upload = buffer(
        &device,
        &heap(D3D12_HEAP_TYPE_UPLOAD),
        BYTES as u64,
        D3D12_RESOURCE_STATE_GENERIC_READ,
    )
    .and_then(|upload| map(&upload).map(|base| (upload, base)));

    let source: Vec<u8> = (0..BYTES).map(|i| (i * 7 + i / 4093) as u8).collect();
    {
        let mut ram = vec![0u8; BYTES];
        // SAFETY: a private heap allocation of BYTES (Vec<u8> is at least 16-byte aligned for this
        // size on the Windows allocator; `fill` falls back to plain copies otherwise).
        unsafe {
            cpu_cases(
                &mut report,
                "RAM",
                ram.as_mut_ptr() as usize,
                &source,
                iterations,
            )
        };
        black_box(&ram);
    }
    // SAFETY: mapped BYTES buffer (64 KB aligned); the GPU has no work queued yet.
    unsafe { cpu_cases(&mut report, "custom WB", custom_base, &source, iterations) };
    match &upload {
        // SAFETY: as above, for the UPLOAD buffer.
        Ok((_, base)) => unsafe {
            cpu_cases(&mut report, "upload heap", *base, &source, iterations)
        },
        Err(error) => {
            let _ = writeln!(report, "  upload heap buffer       {}", fails(error));
        }
    }
    drop(source);

    let mut gpu = match Gpu::new(&device) {
        Ok(gpu) => gpu,
        Err(error) => {
            let _ = writeln!(report, "  D3D12 queue              {}", fails(&error));
            return report;
        }
    };
    // SAFETY: the buffers are live and mapped at the given bases; no other thread uses them.
    unsafe {
        gpu_cases(
            &mut report,
            &device,
            &mut gpu,
            (&custom, custom_base),
            upload.as_ref().ok().map(|(upload, _)| upload),
            iterations,
        );
        share(&mut report, &device, &mut gpu, (&custom, custom_base));
    }
    drop(gpu);
    drop(upload);
    report
}

/// GPU copies from the custom (and UPLOAD) buffer into a DEFAULT R16 texture, timed with GPU
/// timestamps, then a round trip that checks the GPU saw plain CPU stores without Unmap.
///
/// # Safety
/// `custom.1` must be the live mapping of `custom.0` (BYTES), used by nobody else meanwhile.
unsafe fn gpu_cases(
    report: &mut String,
    device: &ID3D12Device,
    gpu: &mut Gpu,
    custom: (&ID3D12Resource, usize),
    upload: Option<&ID3D12Resource>,
    iterations: u32,
) {
    let desc = texture_2d(WIDTH, ROWS, DXGI_FORMAT_R16_UNORM);
    let ([(layout, _, _)], total) = footprints::<1>(device, &desc);
    if layout.Footprint.RowPitch as usize != ROW_BYTES || total as usize > BYTES {
        let _ = writeln!(
            report,
            "  R16 footprint            FAILS (pitch {} total {total})",
            layout.Footprint.RowPitch
        );
        return;
    }
    let setup = (|| -> Result<_> {
        let target = committed(
            device,
            &heap(D3D12_HEAP_TYPE_DEFAULT),
            D3D12_HEAP_FLAG_NONE,
            &desc,
            D3D12_RESOURCE_STATE_COPY_DEST,
        )?;
        let readback = buffer(
            device,
            &heap(D3D12_HEAP_TYPE_READBACK),
            BYTES as u64,
            D3D12_RESOURCE_STATE_COPY_DEST,
        )?;
        let stamps = buffer(
            device,
            &heap(D3D12_HEAP_TYPE_READBACK),
            16,
            D3D12_RESOURCE_STATE_COPY_DEST,
        )?;
        let query_desc = D3D12_QUERY_HEAP_DESC {
            Type: D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
            Count: 2,
            NodeMask: 0,
        };
        let mut queries: Option<ID3D12QueryHeap> = None;
        // SAFETY: live device; `queries` is our slot.
        unsafe { device.CreateQueryHeap(&query_desc, &mut queries)? };
        let queries = queries.ok_or(E_FAIL)?;
        // SAFETY: live queue.
        let frequency = unsafe { gpu.queue.GetTimestampFrequency()? };
        Ok((target, readback, stamps, queries, frequency))
    })();
    let (target, readback, stamps, queries, frequency) = match setup {
        Ok(setup) => setup,
        Err(error) => {
            let _ = writeln!(report, "  GPU copy setup           {}", fails(&error));
            return;
        }
    };

    let mut sources = vec![("GPU copy custom", custom.0)];
    sources.extend(upload.map(|upload| ("GPU copy upload", upload)));
    for (label, source) in sources {
        let line = (|| -> Result<String> {
            let stamp_base = map(&stamps)?;
            let (mut on_gpu, mut wall) = (Timing::default(), Timing::default());
            for i in 0..WARM_UP + iterations {
                let start = Instant::now();
                let value = gpu.execute(|list| {
                    // SAFETY: live resources in the states the copies need (texture COPY_DEST,
                    // buffers promoted from COMMON / GENERIC_READ); waited for below.
                    unsafe {
                        list.EndQuery(&queries, D3D12_QUERY_TYPE_TIMESTAMP, 0);
                        list.CopyTextureRegion(
                            &subresource(&target, 0),
                            0,
                            0,
                            0,
                            &placed(source, layout),
                            None,
                        );
                        list.EndQuery(&queries, D3D12_QUERY_TYPE_TIMESTAMP, 1);
                        list.ResolveQueryData(
                            &queries,
                            D3D12_QUERY_TYPE_TIMESTAMP,
                            0,
                            2,
                            &stamps,
                            0,
                        );
                    }
                })?;
                gpu.wait(value)?;
                let elapsed = start.elapsed();
                // SAFETY: the 16-byte readback buffer is mapped and the GPU finished writing it.
                let (begin, end) = unsafe {
                    let ticks = stamp_base as *const u64;
                    (ticks.read(), ticks.add(1).read())
                };
                if i >= WARM_UP {
                    wall.add(elapsed);
                    if frequency > 0 {
                        on_gpu.add(Duration::from_secs_f64(
                            end.wrapping_sub(begin) as f64 / frequency as f64,
                        ));
                    }
                }
            }
            Ok(format!(
                "gpu {}, wall avg {:.2} ms",
                on_gpu.text(BYTES),
                wall.avg_ms()
            ))
        })();
        let _ = writeln!(
            report,
            "  {label:<24} {}",
            line.unwrap_or_else(|error| fails(&error))
        );
    }

    // Coherency: plain cached stores, no Unmap, then buffer -> texture -> readback on the GPU.
    let line = (|| -> Result<String> {
        // SAFETY: the caller's mapping; the GPU is idle (every submission was waited for).
        unsafe { stamp(custom.1, SEED_COHERENCY) };
        let value = gpu.execute(|list| {
            // SAFETY: live resources; the barriers move the texture to COPY_SOURCE and back.
            unsafe {
                list.CopyTextureRegion(
                    &subresource(&target, 0),
                    0,
                    0,
                    0,
                    &placed(custom.0, layout),
                    None,
                );
                list.ResourceBarrier(&[transition(
                    &target,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                    D3D12_RESOURCE_STATE_COPY_SOURCE,
                )]);
                list.CopyTextureRegion(
                    &placed(&readback, layout),
                    0,
                    0,
                    0,
                    &subresource(&target, 0),
                    None,
                );
                list.ResourceBarrier(&[transition(
                    &target,
                    D3D12_RESOURCE_STATE_COPY_SOURCE,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                )]);
            }
        })?;
        gpu.wait(value)?;
        let base = map(&readback)?;
        // SAFETY: the BYTES readback buffer is mapped and the GPU finished writing it.
        let bad = unsafe { mismatches(base, SEED_COHERENCY) };
        Ok(if bad == 0 {
            format!("OK ({:.1} MB match)", BYTES as f64 / 1e6)
        } else {
            format!("MISMATCH {bad} of {} words", BYTES / 8)
        })
    })();
    let _ = writeln!(
        report,
        "  {:<24} {}",
        "GPU sees CPU stores",
        line.unwrap_or_else(|error| fails(&error))
    );
}

/// D3D12 -> D3D11 handoff: a shared P010 texture filled by a D3D12 copy from the custom buffer,
/// a shared fence the D3D11 context waits on, and a D3D11 readback that must match the buffer.
///
/// # Safety
/// As for [`gpu_cases`].
unsafe fn share(
    report: &mut String,
    device: &ID3D12Device,
    gpu: &mut Gpu,
    custom: (&ID3D12Resource, usize),
) {
    let mut support = D3D12_FEATURE_DATA_FORMAT_SUPPORT {
        Format: DXGI_FORMAT_P010,
        ..Default::default()
    };
    // SAFETY: the matching feature struct, sized exactly.
    let checked = unsafe {
        device.CheckFeatureSupport(
            D3D12_FEATURE_FORMAT_SUPPORT,
            (&mut support as *mut D3D12_FEATURE_DATA_FORMAT_SUPPORT).cast(),
            std::mem::size_of::<D3D12_FEATURE_DATA_FORMAT_SUPPORT>() as u32,
        )
    };
    let _ = writeln!(
        report,
        "  {:<24} {}",
        "P010 D3D12 support",
        checked
            .map(|()| format!("0x{:08X} 0x{:08X}", support.Support1.0, support.Support2.0))
            .unwrap_or_else(|error| fails(&error))
    );

    let desc = texture_2d(WIDTH, LUMA_ROWS, DXGI_FORMAT_P010);
    let (planes, total) = footprints::<2>(device, &desc);
    let _ = writeln!(
        report,
        "  {:<24} Y +{} pitch {} rows {}, UV +{} pitch {} rows {}, total {:.1} MB",
        "P010 footprints",
        planes[0].0.Offset,
        planes[0].0.Footprint.RowPitch,
        planes[0].1,
        planes[1].0.Offset,
        planes[1].0.Footprint.RowPitch,
        planes[1].1,
        total as f64 / 1e6
    );
    if total as usize > BYTES {
        report.push_str("  P010 handoff             FAILS (footprint larger than the buffer)\n");
        return;
    }

    macro_rules! step {
        ($label:expr, $result:expr) => {
            match $result {
                Ok(value) => value,
                Err(error) => {
                    let _ = writeln!(report, "  {:<24} {}", $label, fails(&error));
                    return;
                }
            }
        };
    }

    let p010 = step!(
        "P010 shared texture",
        committed(
            device,
            &heap(D3D12_HEAP_TYPE_DEFAULT),
            D3D12_HEAP_FLAG_SHARED,
            &desc,
            D3D12_RESOURCE_STATE_COMMON,
        )
    );
    // SAFETY (both handles): live device and objects; default security, unnamed.
    let texture_handle = Handle(step!("P010 shared handle", unsafe {
        device.CreateSharedHandle(&p010, None, GENERIC_ALL, PCWSTR::null())
    }));
    // SAFETY: live device.
    let fence: ID3D12Fence = step!("shared fence", unsafe {
        device.CreateFence(0, D3D12_FENCE_FLAG_SHARED)
    });
    let fence_handle = Handle(step!("shared fence handle", unsafe {
        device.CreateSharedHandle(&fence, None, GENERIC_ALL, PCWSTR::null())
    }));
    report.push_str("  P010 shared texture      OK (texture + fence handles)\n");

    let (device11, context) = step!("D3D11 device", hardware_d3d11());
    // SAFETY: live D3D11 device.
    let support11 = unsafe { device11.CheckFormatSupport(DXGI_FORMAT_P010) };
    let _ = writeln!(
        report,
        "  {:<24} {}",
        "P010 D3D11 support",
        support11
            .map(|flags| format!("0x{flags:08X}"))
            .unwrap_or_else(|error| fails(&error))
    );
    let device1: ID3D11Device1 = step!("ID3D11Device1", device11.cast());
    // SAFETY: an NT handle from CreateSharedHandle on the same adapter.
    let shared: ID3D11Texture2D = step!("D3D11 open texture", unsafe {
        device1.OpenSharedResource1(texture_handle.0)
    });
    let mut shared_desc = D3D11_TEXTURE2D_DESC::default();
    // SAFETY: live texture; `shared_desc` is our out-slot.
    unsafe { shared.GetDesc(&mut shared_desc) };
    let _ = writeln!(
        report,
        "  {:<24} OK format {} {}x{}",
        "D3D11 open texture", shared_desc.Format.0, shared_desc.Width, shared_desc.Height
    );
    let device5: ID3D11Device5 = step!("ID3D11Device5", device11.cast());
    let mut fence11: Option<ID3D11Fence> = None;
    // SAFETY: an NT fence handle from CreateSharedHandle; `fence11` is our slot.
    step!("D3D11 open fence", unsafe {
        device5.OpenSharedFence(fence_handle.0, &mut fence11)
    });
    let fence11 = step!(
        "D3D11 open fence",
        fence11.ok_or_else(|| windows::core::Error::from(E_FAIL))
    );
    let context4: ID3D11DeviceContext4 = step!("ID3D11DeviceContext4", context.cast());
    report.push_str("  D3D11 open fence         OK\n");

    let mut staging_desc = texture_desc(WIDTH, LUMA_ROWS, DXGI_FORMAT_P010, 0);
    staging_desc.Usage = D3D11_USAGE_STAGING;
    staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let staging = step!(
        "D3D11 P010 staging",
        texture(&device11, &staging_desc, None)
    );

    // SAFETY: the caller's mapping; the GPU is idle.
    unsafe { stamp(custom.1, SEED_SHARE) };
    let start = Instant::now();
    let value = step!(
        "D3D12 copy to P010",
        gpu.execute(|list| {
            // SAFETY: live resources; the shared texture goes COMMON -> COPY_DEST -> COMMON so
            // the other device sees it in the common state.
            unsafe {
                list.ResourceBarrier(&[transition(
                    &p010,
                    D3D12_RESOURCE_STATE_COMMON,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                )]);
                for (plane, (layout, _, _)) in planes.iter().enumerate() {
                    list.CopyTextureRegion(
                        &subresource(&p010, plane as u32),
                        0,
                        0,
                        0,
                        &placed(custom.0, *layout),
                        None,
                    );
                }
                list.ResourceBarrier(&[transition(
                    &p010,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                    D3D12_RESOURCE_STATE_COMMON,
                )]);
            }
        })
    );
    // SAFETY: live queue and shared fence; the D3D11 side waits for this value on the GPU.
    step!("D3D12 signal shared", unsafe {
        gpu.queue.Signal(&fence, 1)
    });
    // SAFETY: live context and fence; Map below waits for the GPU copy.
    step!("D3D11 wait", unsafe { context4.Wait(&fence11, 1) });
    // SAFETY: matching P010 textures of the same size on this device.
    unsafe { context.CopyResource(&staging, &shared) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    // SAFETY: an unmapped READ staging texture on this context.
    step!("D3D11 map", unsafe {
        context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
    });
    let elapsed = start.elapsed();
    let pitch = mapped.RowPitch as usize;
    let mut bad = [0usize; 2];
    if !mapped.pData.is_null() && pitch >= ROW_BYTES {
        // D3D11 maps a planar staging texture with the chroma plane right after `Height` luma rows.
        let bases = [
            mapped.pData as usize,
            mapped.pData as usize + pitch * LUMA_ROWS as usize,
        ];
        for (plane, (layout, rows, row_bytes)) in planes.iter().enumerate() {
            for row in 0..*rows as usize {
                let offset = layout.Offset as usize + row * layout.Footprint.RowPitch as usize;
                // SAFETY: footprint rows lie inside the BYTES buffer (total checked above); the
                // mapping holds Height + Height/2 rows of `pitch` >= row_bytes bytes.
                let (expected, actual) = unsafe {
                    (
                        std::slice::from_raw_parts(
                            (custom.1 + offset) as *const u8,
                            *row_bytes as usize,
                        ),
                        std::slice::from_raw_parts(
                            (bases[plane] + row * pitch) as *const u8,
                            *row_bytes as usize,
                        ),
                    )
                };
                if expected != actual {
                    bad[plane] += 1;
                }
            }
        }
    }
    // SAFETY: mapped above on this context.
    unsafe { context.Unmap(&staging, 0) };
    let waited = gpu.wait(value);
    let line = if mapped.pData.is_null() || pitch < ROW_BYTES {
        format!("FAILS (map pitch {pitch})")
    } else if bad == [0, 0] {
        format!(
            "OK (Y + UV match; copy, fence wait and readback {:.1} ms)",
            elapsed.as_secs_f64() * 1000.0
        )
    } else {
        format!(
            "MISMATCH Y {} of {} rows, UV {} of {} rows",
            bad[0], planes[0].1, bad[1], planes[1].1
        )
    };
    let _ = writeln!(report, "  {:<24} {line}", "D3D11 sees D3D12 data");
    if let Err(error) = waited {
        let _ = writeln!(report, "  {:<24} {}", "D3D12 fence wait", fails(&error));
    }
}

fn hardware_d3d11() -> Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut device = None;
    let mut context = None;
    // SAFETY: no adapter or software module; out-pointers are live locals.
    unsafe {
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
        )?
    };
    Ok((device.ok_or(E_FAIL)?, context.ok_or(E_FAIL)?))
}
