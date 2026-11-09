//! Zero-copy 10-bit repack (zero-copy ABI 1, docs/player-plan.md "Zero-copy design").
//!
//! dav1d decodes straight into persistently mapped D3D12 buffers on a CPU-cached (write-back, L0)
//! custom heap ([`bale_zc_alloc`]). A D3D12 compute shader reads the picture from that buffer and
//! writes P010 into one of two shared landing textures; Media Foundation's D3D11 device opens both
//! once and copies from the one [`bale_zc_repack`] returns. Nothing is copied on the CPU. Where
//! D3D11 cannot open a shared UAV P010 texture (WARP), the dispatch writes a private texture and
//! a GPU copy fills a plain shared one (`landing_copies` in the stats).
//!
//! Two shared fences order the devices without CPU stalls:
//! - `repacked`: the D3D12 queue signals n after frame n's dispatch; the D3D11 context waits for n
//!   (GPU-side) before the caller's copy.
//! - `released`: each repack first signals the D3D11 side of `released` for the previous landing
//!   texture, which covers the caller's copy from it; the D3D12 queue waits for that value before
//!   writing the texture again.
//!
//! Buffers are reused only after the `repacked` fence passes the last dispatch that read them, so
//! the caller can release a picture as soon as `bale_zc_repack` returns. `d3d12.dll` is registered
//! by the caller ([`bale_zc_register_d3d12`]) and never imported.

use std::collections::BTreeMap;
use std::ffi::c_void;
use std::mem::ManuallyDrop;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{
    CloseHandle, E_FAIL, E_INVALIDARG, E_NOINTERFACE, E_NOTIMPL, E_OUTOFMEMORY, E_UNEXPECTED,
    HANDLE, HMODULE,
};
use windows::Win32::Graphics::Direct3D::{D3D_FEATURE_LEVEL_11_0, ID3DBlob};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::{IDXGIAdapter, IDXGIDevice};
use windows::Win32::System::LibraryLoader::GetProcAddress;
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
use windows::core::{Error, HRESULT, Interface, PCWSTR, Result, s};

use crate::d3d12::{CreateDevice, resource};
use crate::grain::gpu::{readback_bytes, same_device, texture, texture_desc};
use crate::zc_frame::{BaleZcFrame, MAX_BYTES, fill_test_planes};

const SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/repack_zc_cs.cso"));

pub const ZERO_COPY_ABI_VERSION: u32 = 1;
pub const S_OK: i32 = 0;
/// `bale_zc_repack`: not a buffer of this handle; the caller takes its ordinary path.
pub const S_FALSE: i32 = 1;
const WAIT_TIMEOUT: HRESULT = HRESULT(0x8007_0102_u32 as i32);
const GENERIC_ALL: u32 = 0x1000_0000;
/// Landing textures: the caller copies from one while the next frame lands in the other.
const SLOTS: usize = 2;
/// Buffers alive or pooled per handle; dav1d keeps at most ~10 pictures (4K: ~25 MB each).
const MAX_BUFFERS: usize = 16;
const ALIGNMENT: u64 = 64 << 10;
const WAIT_MS: u64 = 5000;
const ALLOC_WAIT_MS: u64 = 100;

type SerializeRootSignature = unsafe extern "system" fn(
    *const D3D12_ROOT_SIGNATURE_DESC,
    D3D_ROOT_SIGNATURE_VERSION,
    *mut *mut c_void,
    *mut *mut c_void,
) -> HRESULT;

/// The caller's `d3d12.dll` (null until registered). It stays loaded for the process.
static D3D12: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
/// Every buffer handed out by any handle, by odd id. Ids are never dereferenced, so a stale or
/// foreign id is harmless.
static LIVE: Mutex<BTreeMap<usize, Arc<Buffer>>> = Mutex::new(BTreeMap::new());
static NEXT_ID: AtomicUsize = AtomicUsize::new(0);

/// An owned kernel handle (event or shared NT handle), closed on drop.
pub(crate) struct Handle(pub(crate) HANDLE);

// SAFETY: a kernel handle value is usable from any thread; this wrapper only closes it once.
unsafe impl Send for Handle {}
// SAFETY: as above; shared access never closes it.
unsafe impl Sync for Handle {}

impl Drop for Handle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            // SAFETY: this wrapper owns the handle and closes it once.
            let _ = unsafe { CloseHandle(self.0) };
        }
    }
}

pub(crate) fn heap(kind: D3D12_HEAP_TYPE) -> D3D12_HEAP_PROPERTIES {
    D3D12_HEAP_PROPERTIES {
        Type: kind,
        CPUPageProperty: D3D12_CPU_PAGE_PROPERTY_UNKNOWN,
        MemoryPoolPreference: D3D12_MEMORY_POOL_UNKNOWN,
        CreationNodeMask: 0,
        VisibleNodeMask: 0,
    }
}

/// CPU-cached system memory the GPU reads directly: the decode target.
pub(crate) fn write_back() -> D3D12_HEAP_PROPERTIES {
    D3D12_HEAP_PROPERTIES {
        Type: D3D12_HEAP_TYPE_CUSTOM,
        CPUPageProperty: D3D12_CPU_PAGE_PROPERTY_WRITE_BACK,
        MemoryPoolPreference: D3D12_MEMORY_POOL_L0,
        CreationNodeMask: 0,
        VisibleNodeMask: 0,
    }
}

pub(crate) fn committed(
    device: &ID3D12Device,
    properties: &D3D12_HEAP_PROPERTIES,
    flags: D3D12_HEAP_FLAGS,
    desc: &D3D12_RESOURCE_DESC,
    state: D3D12_RESOURCE_STATES,
) -> Result<ID3D12Resource> {
    let mut out = None;
    // SAFETY: live device; descriptors outlive the call; `out` is our slot.
    unsafe { device.CreateCommittedResource(properties, flags, desc, state, None, &mut out)? };
    out.ok_or_else(|| E_FAIL.into())
}

/// Maps a buffer for CPU reads and writes and leaves it mapped; D3D12 allows releasing a mapped
/// resource, which ends the mapping.
pub(crate) fn map(resource: &ID3D12Resource) -> Result<usize> {
    let mut data = std::ptr::null_mut();
    // SAFETY: a CPU-visible buffer; `data` is our out-slot.
    unsafe { resource.Map(0, None, Some(&mut data))? };
    if data.is_null() {
        return Err(E_FAIL.into());
    }
    Ok(data as usize)
}

/// A non-owning copy of a COM pointer for D3D12 structs that hold it in `ManuallyDrop` and so
/// never release it.
pub(crate) fn borrowed<T: Interface>(object: &T) -> ManuallyDrop<Option<T>> {
    // SAFETY: `Option<T>` is one non-null COM pointer, the same layout as `T`; `ManuallyDrop`
    // skips the Release this copy did not AddRef. Callers keep `object` alive for every use of
    // the struct (each submission is waited for before the object is released).
    unsafe { std::mem::transmute_copy(object) }
}

pub(crate) fn transition(
    resource: &ID3D12Resource,
    before: D3D12_RESOURCE_STATES,
    after: D3D12_RESOURCE_STATES,
) -> D3D12_RESOURCE_BARRIER {
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: borrowed(resource),
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: before,
                StateAfter: after,
            }),
        },
    }
}

/// Waits on the CPU until `fence` reaches `value`, at most `timeout`.
fn wait_fence(fence: &ID3D12Fence, value: u64, event: HANDLE, timeout: Duration) -> Result<()> {
    let deadline = Instant::now() + timeout;
    loop {
        // SAFETY: live fence and event; a stale wake-up only repeats the check.
        unsafe {
            if fence.GetCompletedValue() >= value {
                return Ok(());
            }
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return Err(WAIT_TIMEOUT.into());
            }
            fence.SetEventOnCompletion(value, event)?;
            let _ = WaitForSingleObject(event, left.as_millis().clamp(1, 60_000) as u32);
        }
    }
}

/// One mapped decode buffer. `gpu_fence` is the `repacked` value after which no submitted
/// dispatch reads it.
struct Memory {
    resource: ID3D12Resource,
    data: usize,
    bytes: u64,
    gpu_fence: u64,
}

/// A buffer handed to the decoder. Dropping it (after the decoder released it and no repack holds
/// it) returns the memory to its pool.
struct Buffer {
    memory: Option<Memory>,
    gpu_fence: AtomicU64,
    pool: Arc<Pool>,
}

impl Drop for Buffer {
    fn drop(&mut self) {
        if let Some(mut memory) = self.memory.take() {
            memory.gpu_fence = self.gpu_fence.load(Ordering::Acquire);
            self.pool.recycle(memory);
        }
    }
}

#[derive(Default)]
struct PoolState {
    free: Vec<Memory>,
    live: usize,
    created: u64,
    peak: u64,
    failures: u64,
    waits: u64,
}

/// Decode buffers of one handle. Shared with every live buffer, so it outlives the handle.
struct Pool {
    device: ID3D12Device,
    repacked: ID3D12Fence,
    state: Mutex<PoolState>,
    /// A repack failed: no new buffers, so the decoder falls back to its own pool.
    poisoned: AtomicBool,
    /// The handle is gone: returned memory is released instead of pooled.
    closed: AtomicBool,
    frames: AtomicU64,
    /// Frames repacked through the copy fallback (see [`Landing::copy`]).
    copies: AtomicU64,
}

impl Pool {
    fn alloc(self: &Arc<Self>, bytes: u64) -> Result<Arc<Buffer>> {
        if bytes == 0 || bytes > MAX_BYTES {
            return Err(E_INVALIDARG.into());
        }
        if self.poisoned.load(Ordering::Acquire) {
            return Err(E_FAIL.into());
        }
        let size = bytes.next_multiple_of(ALIGNMENT);
        for attempt in 0..2 {
            // SAFETY: live fence.
            let completed = unsafe { self.repacked.GetCompletedValue() };
            let mut state = self.state.lock().map_err(|_| E_UNEXPECTED)?;
            // Idle memory of another size is released; memory the GPU may still read never is.
            state
                .free
                .retain(|memory| memory.bytes == size || memory.gpu_fence > completed);
            let ready = state
                .free
                .iter()
                .position(|memory| memory.bytes == size && memory.gpu_fence <= completed);
            if let Some(index) = ready {
                let memory = state.free.swap_remove(index);
                Self::lend(&mut state);
                return Ok(self.wrap(memory));
            }
            if state.live + state.free.len() < MAX_BUFFERS {
                Self::lend(&mut state);
                drop(state);
                return match self.create(size) {
                    Ok(memory) => {
                        self.state.lock().map_err(|_| E_UNEXPECTED)?.created += 1;
                        Ok(self.wrap(memory))
                    }
                    Err(error) => {
                        let mut state = self.state.lock().map_err(|_| E_UNEXPECTED)?;
                        state.live -= 1;
                        state.failures += 1;
                        Err(error)
                    }
                };
            }
            let oldest = state.free.iter().map(|memory| memory.gpu_fence).min();
            let Some(oldest) = oldest.filter(|_| attempt == 0) else {
                state.failures += 1;
                return Err(E_OUTOFMEMORY.into());
            };
            state.waits += 1;
            drop(state);
            // SAFETY: default security, auto-reset, unnamed.
            let event = Handle(unsafe { CreateEventW(None, false, false, PCWSTR::null())? });
            let _ = wait_fence(
                &self.repacked,
                oldest,
                event.0,
                Duration::from_millis(ALLOC_WAIT_MS),
            );
        }
        let mut state = self.state.lock().map_err(|_| E_UNEXPECTED)?;
        state.failures += 1;
        Err(E_OUTOFMEMORY.into())
    }

    fn lend(state: &mut PoolState) {
        state.live += 1;
        state.peak = state.peak.max(state.live as u64);
    }

    fn create(&self, size: u64) -> Result<Memory> {
        let desc = resource(
            D3D12_RESOURCE_DIMENSION_BUFFER,
            size,
            1,
            DXGI_FORMAT_UNKNOWN,
            D3D12_RESOURCE_FLAG_NONE,
        );
        let resource = committed(
            &self.device,
            &write_back(),
            D3D12_HEAP_FLAG_NONE,
            &desc,
            D3D12_RESOURCE_STATE_COMMON,
        )?;
        let data = map(&resource)?;
        Ok(Memory {
            resource,
            data,
            bytes: size,
            gpu_fence: 0,
        })
    }

    fn wrap(self: &Arc<Self>, memory: Memory) -> Arc<Buffer> {
        Arc::new(Buffer {
            gpu_fence: AtomicU64::new(memory.gpu_fence),
            memory: Some(memory),
            pool: Arc::clone(self),
        })
    }

    fn recycle(&self, memory: Memory) {
        let Ok(mut state) = self.state.lock() else {
            // Unknown GPU use: leaking is safe, releasing might not be.
            std::mem::forget(memory);
            return;
        };
        state.live = state.live.saturating_sub(1);
        if !self.closed.load(Ordering::Acquire) {
            state.free.push(memory);
            return;
        }
        drop(state);
        // SAFETY: live fence.
        if memory.gpu_fence > unsafe { self.repacked.GetCompletedValue() } {
            // Only after a failed idle wait at destroy: never release memory the GPU may read.
            std::mem::forget(memory);
        }
    }
}

/// Holds the D3D11 multithread lock (shared with Media Foundation) for the guard's lifetime.
struct Locked(ID3D10Multithread);

impl Locked {
    fn enter(multithread: &ID3D10Multithread) -> Self {
        let guard = Self(multithread.clone());
        // SAFETY: live interface; Drop leaves exactly once, also during unwinding.
        unsafe { guard.0.Enter() };
        guard
    }
}

impl Drop for Locked {
    fn drop(&mut self) {
        // SAFETY: paired with the Enter in `enter`.
        unsafe { self.0.Leave() };
    }
}

struct Landing {
    /// Written by the dispatch through P010 plane UAVs.
    texture: ID3D12Resource,
    /// The shared texture when D3D11 refuses a shared UAV P010 texture (WARP does): `texture`
    /// is then private and copied into this one after the dispatch.
    copy: Option<ID3D12Resource>,
    /// D3D11's view of `copy`, or of `texture` when there is no copy.
    texture11: ID3D11Texture2D,
}

struct Slot {
    allocator: ID3D12CommandAllocator,
    list: ID3D12GraphicsCommandList,
    /// `repacked` value of the last dispatch recorded with this allocator.
    submitted: u64,
    /// `released` value after which the caller no longer reads this slot's landing texture.
    released: u64,
    landing: Option<Landing>,
}

struct Inner {
    device: ID3D12Device,
    queue: ID3D12CommandQueue,
    root: ID3D12RootSignature,
    pso: ID3D12PipelineState,
    heap: ID3D12DescriptorHeap,
    cpu_start: D3D12_CPU_DESCRIPTOR_HANDLE,
    gpu_start: D3D12_GPU_DESCRIPTOR_HANDLE,
    increment: u32,
    repacked: ID3D12Fence,
    released: ID3D12Fence,
    repacked11: ID3D11Fence,
    released11: ID3D11Fence,
    device11: ID3D11Device1,
    context: ID3D11DeviceContext4,
    multithread: ID3D10Multithread,
    slots: [Slot; SLOTS],
    size: Option<(u32, u32)>,
    next: usize,
    previous: Option<usize>,
    /// D3D11 opens the shared UAV landing texture. Cleared for good once it refuses.
    direct: bool,
    repacked_value: u64,
    released_value: u64,
    event: Handle,
    poison: Option<i32>,
}

impl Inner {
    /// Repacks `frame` from `buffer` into the next landing texture and returns it (a new
    /// reference). The D3D11 context waits for the dispatch on the GPU before later commands.
    fn repack(&mut self, buffer: &Buffer, frame: &BaleZcFrame) -> Result<ID3D11Texture2D> {
        let memory = buffer.memory.as_ref().ok_or(E_FAIL)?;
        let _lock = Locked::enter(&self.multithread);
        let slot = self.next;
        if let Some(previous) = self.previous {
            // Queued after the caller's copy from the previous landing texture.
            self.released_value += 1;
            // SAFETY: live context and fence, under the multithread lock.
            unsafe { self.context.Signal(&self.released11, self.released_value) }
                .step("D3D11 signal released")?;
            self.slots[previous].released = self.released_value;
        }
        // The slot's allocator and descriptors are reused only after its last dispatch ran.
        self.wait(&self.repacked, self.slots[slot].submitted)
            .step("slot idle wait")?;
        if self.size != Some((frame.width, frame.height)) {
            self.rebuild(frame.width, frame.height)
                .step("landing rebuild")?;
        }
        let base = 3 * slot as u32;
        let view = D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format: DXGI_FORMAT_R32_TYPELESS,
            ViewDimension: D3D12_SRV_DIMENSION_BUFFER,
            Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
            Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 {
                Buffer: D3D12_BUFFER_SRV {
                    FirstElement: 0,
                    NumElements: (memory.bytes / 4) as u32,
                    StructureByteStride: 0,
                    Flags: D3D12_BUFFER_SRV_FLAG_RAW,
                },
            },
        };
        // SAFETY: live device and buffer; the descriptor is not in use (slot idle, waited above).
        unsafe {
            self.device
                .CreateShaderResourceView(&memory.resource, Some(&view), self.cpu(base))
        };
        let current = &self.slots[slot];
        let landing = current.landing.as_ref().ok_or(E_FAIL)?;
        let constants = frame.constants();
        let [x, y] = frame.groups();
        let value = self.repacked_value + 1;
        // SAFETY: the slot's allocator and list are idle; every bound object lives until the
        // dispatch completes (buffers are reused only after `repacked` passes `value`, landing
        // textures and descriptors only after an idle wait). Every texture ends in COMMON, so
        // D3D11 sees the shared one in the common state.
        unsafe {
            current.allocator.Reset()?;
            current.list.Reset(&current.allocator, &self.pso)?;
            current.list.SetComputeRootSignature(&self.root);
            current.list.SetDescriptorHeaps(&[Some(self.heap.clone())]);
            current
                .list
                .SetComputeRoot32BitConstants(0, 8, constants.as_ptr().cast(), 0);
            current
                .list
                .SetComputeRootDescriptorTable(1, self.gpu(base));
            current.list.ResourceBarrier(&[transition(
                &landing.texture,
                D3D12_RESOURCE_STATE_COMMON,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            )]);
            current.list.Dispatch(x, y, 1);
            match &landing.copy {
                None => current.list.ResourceBarrier(&[transition(
                    &landing.texture,
                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                    D3D12_RESOURCE_STATE_COMMON,
                )]),
                Some(copy) => {
                    current.list.ResourceBarrier(&[
                        transition(
                            &landing.texture,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                            D3D12_RESOURCE_STATE_COPY_SOURCE,
                        ),
                        transition(
                            copy,
                            D3D12_RESOURCE_STATE_COMMON,
                            D3D12_RESOURCE_STATE_COPY_DEST,
                        ),
                    ]);
                    current.list.CopyResource(copy, &landing.texture);
                    current.list.ResourceBarrier(&[
                        transition(
                            &landing.texture,
                            D3D12_RESOURCE_STATE_COPY_SOURCE,
                            D3D12_RESOURCE_STATE_COMMON,
                        ),
                        transition(
                            copy,
                            D3D12_RESOURCE_STATE_COPY_DEST,
                            D3D12_RESOURCE_STATE_COMMON,
                        ),
                    ]);
                }
            }
            current.list.Close()?;
            if current.released > 0 {
                self.queue.Wait(&self.released, current.released)?;
            }
            self.queue
                .ExecuteCommandLists(&[Some(current.list.cast::<ID3D12CommandList>()?)]);
            self.queue.Signal(&self.repacked, value)?;
        }
        let texture11 = landing.texture11.clone();
        self.repacked_value = value;
        self.slots[slot].submitted = value;
        buffer.gpu_fence.fetch_max(value, Ordering::AcqRel);
        // SAFETY: live context and fence, under the multithread lock.
        unsafe { self.context.Wait(&self.repacked11, value) }.step("D3D11 wait repacked")?;
        self.previous = Some(slot);
        self.next = (slot + 1) % SLOTS;
        Ok(texture11)
    }

    /// Replaces both landing textures once nothing reads or writes them.
    fn rebuild(&mut self, width: u32, height: u32) -> Result<()> {
        self.wait(&self.released, self.released_value)?;
        self.wait(&self.repacked, self.repacked_value)?;
        self.size = None;
        for slot in &mut self.slots {
            slot.landing = None;
        }
        for index in 0..SLOTS {
            let landing = self.landing(width, height)?;
            for plane in 0..2 {
                let view = D3D12_UNORDERED_ACCESS_VIEW_DESC {
                    Format: [DXGI_FORMAT_R16_UINT, DXGI_FORMAT_R16G16_UINT][plane as usize],
                    ViewDimension: D3D12_UAV_DIMENSION_TEXTURE2D,
                    Anonymous: D3D12_UNORDERED_ACCESS_VIEW_DESC_0 {
                        Texture2D: D3D12_TEX2D_UAV {
                            MipSlice: 0,
                            PlaneSlice: plane,
                        },
                    },
                };
                // SAFETY: live device and texture; the descriptors are idle (waited above).
                unsafe {
                    self.device.CreateUnorderedAccessView(
                        &landing.texture,
                        None::<&ID3D12Resource>,
                        Some(&view),
                        self.cpu(3 * index as u32 + 1 + plane),
                    )
                };
            }
            self.slots[index].landing = Some(landing);
        }
        self.size = Some((width, height));
        Ok(())
    }

    /// A landing texture written in place and shared with D3D11, or, once D3D11 has refused that
    /// (`direct` cleared for the handle), a private one plus a shared copy target.
    fn landing(&mut self, width: u32, height: u32) -> Result<Landing> {
        let p010 = |flags| {
            let mut desc = resource(
                D3D12_RESOURCE_DIMENSION_TEXTURE2D,
                u64::from(width),
                height,
                DXGI_FORMAT_P010,
                flags,
            );
            desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
            desc
        };
        let target = p010(D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        let create = |heap_flags, desc: &D3D12_RESOURCE_DESC| {
            committed(
                &self.device,
                &heap(D3D12_HEAP_TYPE_DEFAULT),
                heap_flags,
                desc,
                D3D12_RESOURCE_STATE_COMMON,
            )
        };
        if self.direct {
            let texture = create(D3D12_HEAP_FLAG_SHARED, &target).step("landing texture")?;
            match self.share(&texture, width, height) {
                Ok(texture11) => {
                    return Ok(Landing {
                        texture,
                        copy: None,
                        texture11,
                    });
                }
                // WARP's D3D11 rejects a shared UAV P010 texture (E_INVALIDARG); hardware
                // may too. Fall back for the rest of the handle's life.
                Err(_) => self.direct = false,
            }
        }
        let texture = create(D3D12_HEAP_FLAG_NONE, &target).step("landing texture")?;
        let copy = create(D3D12_HEAP_FLAG_SHARED, &p010(D3D12_RESOURCE_FLAG_NONE))
            .step("landing copy texture")?;
        let texture11 = self.share(&copy, width, height)?;
        Ok(Landing {
            texture,
            copy: Some(copy),
            texture11,
        })
    }

    /// Opens a shared D3D12 P010 texture on the D3D11 device and checks what D3D11 sees.
    fn share(&self, texture: &ID3D12Resource, width: u32, height: u32) -> Result<ID3D11Texture2D> {
        // SAFETY: live objects; default security, unnamed; the NT handle closes after the open.
        let shared = Handle(
            unsafe {
                self.device
                    .CreateSharedHandle(texture, None, GENERIC_ALL, PCWSTR::null())
            }
            .step("landing shared handle")?,
        );
        // SAFETY: an NT handle from CreateSharedHandle on the same adapter, open for the call.
        let texture11: ID3D11Texture2D = unsafe { self.device11.OpenSharedResource1(shared.0) }
            .step("D3D11 open landing texture")?;
        drop(shared);
        let mut opened = D3D11_TEXTURE2D_DESC::default();
        // SAFETY: live texture; `opened` is our out-slot.
        unsafe { texture11.GetDesc(&mut opened) };
        if (opened.Width, opened.Height, opened.Format) != (width, height, DXGI_FORMAT_P010)
            || opened.ArraySize != 1
            || opened.MipLevels != 1
        {
            return Err(E_FAIL.into()).step("D3D11 landing texture description");
        }
        Ok(texture11)
    }

    /// Waits on the CPU for `fence`, first flushing the D3D11 context: D3D12 work may wait for a
    /// `released` signal still in its command buffer. Caller holds the multithread lock.
    fn wait(&self, fence: &ID3D12Fence, value: u64) -> Result<()> {
        // SAFETY: live fence and context.
        unsafe {
            if fence.GetCompletedValue() >= value {
                return Ok(());
            }
            self.context.Flush();
        }
        wait_fence(fence, value, self.event.0, Duration::from_millis(WAIT_MS))
    }

    fn cpu(&self, index: u32) -> D3D12_CPU_DESCRIPTOR_HANDLE {
        D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr: self.cpu_start.ptr + (index * self.increment) as usize,
        }
    }

    fn gpu(&self, index: u32) -> D3D12_GPU_DESCRIPTOR_HANDLE {
        D3D12_GPU_DESCRIPTOR_HANDLE {
            ptr: self.gpu_start.ptr + u64::from(index * self.increment),
        }
    }
}

impl Drop for Inner {
    fn drop(&mut self) {
        // Never release textures, lists or the queue under in-flight work of either device.
        let _lock = Locked::enter(&self.multithread);
        if self.previous.is_some() {
            // SAFETY: live context and fence, under the multithread lock.
            if unsafe {
                self.context
                    .Signal(&self.released11, self.released_value + 1)
            }
            .is_ok()
            {
                self.released_value += 1;
            }
        }
        let _ = self.wait(&self.released, self.released_value);
        let _ = self.wait(&self.repacked, self.repacked_value);
    }
}

/// Opaque C-ABI handle. `bale_zc_alloc` and `bale_zc_stats` may run on any thread at any time;
/// `bale_zc_repack` calls are serialized by the caller; destroy runs after every other call.
pub struct BaleZeroCopy {
    pool: Arc<Pool>,
    inner: Mutex<Inner>,
}

impl BaleZeroCopy {
    fn create(device11: &ID3D11Device, context: &ID3D11DeviceContext) -> Result<Box<Self>> {
        let module = HMODULE(D3D12.load(Ordering::Acquire));
        if module.0.is_null() {
            return Err(E_NOINTERFACE.into()).step("d3d12.dll not registered");
        }
        if cfg!(bale_grain_no_shaders) {
            return Err(E_NOTIMPL.into()).step("no compiled shader");
        }
        // SAFETY: both interfaces are live borrowed COM references. These are metadata queries.
        if unsafe { context.GetType() } != D3D11_DEVICE_CONTEXT_IMMEDIATE
            || unsafe { device11.GetFeatureLevel() }.0 < D3D_FEATURE_LEVEL_11_0.0
            || !same_device(device11, &unsafe { context.GetDevice()? })?
        {
            return Err(E_INVALIDARG.into()).step("D3D11 device and immediate context");
        }
        let (device1, device5, context4, multithread) = (|| -> Result<_> {
            let device1: ID3D11Device1 = device11.cast()?;
            let device5: ID3D11Device5 = device11.cast()?;
            let context4: ID3D11DeviceContext4 = context.cast()?;
            let multithread: ID3D10Multithread = context.cast()?;
            Ok((device1, device5, context4, multithread))
        })()
        .step("D3D11 interfaces")?;
        // SAFETY: live device; the adapter reference is ours.
        let adapter =
            unsafe { device11.cast::<IDXGIDevice>()?.GetAdapter() }.step("DXGI adapter")?;
        // SAFETY: the registered module stays loaded for the process.
        let device = unsafe { create_device(module, &adapter) }.step("D3D12CreateDevice")?;
        check_p010(&device).step("P010 typed UAV support")?;
        // SAFETY: live device; descriptors outlive the calls.
        let queue: ID3D12CommandQueue = unsafe {
            device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC {
                Type: D3D12_COMMAND_LIST_TYPE_COMPUTE,
                ..Default::default()
            })
        }
        .step("compute queue")?;
        // SAFETY: as above.
        let heap: ID3D12DescriptorHeap = unsafe {
            device.CreateDescriptorHeap(&D3D12_DESCRIPTOR_HEAP_DESC {
                Type: D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
                NumDescriptors: 3 * SLOTS as u32,
                Flags: D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
                NodeMask: 0,
            })
        }
        .step("descriptor heap")?;
        let (repacked, repacked11) = shared_fence(&device, &device5).step("repacked fence")?;
        let (released, released11) = shared_fence(&device, &device5).step("released fence")?;
        // SAFETY: the registered module stays loaded for the process.
        let root = unsafe { root_signature(module, &device) }.step("root signature")?;
        // SAFETY: live device and root signature; the bytecode is a static compiled shader.
        let pso: ID3D12PipelineState = unsafe {
            device.CreateComputePipelineState(&D3D12_COMPUTE_PIPELINE_STATE_DESC {
                pRootSignature: borrowed(&root),
                CS: D3D12_SHADER_BYTECODE {
                    pShaderBytecode: SHADER.as_ptr().cast(),
                    BytecodeLength: SHADER.len(),
                },
                NodeMask: 0,
                CachedPSO: D3D12_CACHED_PIPELINE_STATE::default(),
                Flags: D3D12_PIPELINE_STATE_FLAG_NONE,
            })
        }
        .step("compute pipeline")?;
        let slot = || -> Result<Slot> {
            // SAFETY: live device and pipeline; the list is closed before its first Reset.
            unsafe {
                let allocator: ID3D12CommandAllocator =
                    device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE)?;
                let list: ID3D12GraphicsCommandList = device.CreateCommandList(
                    0,
                    D3D12_COMMAND_LIST_TYPE_COMPUTE,
                    &allocator,
                    &pso,
                )?;
                list.Close()?;
                Ok(Slot {
                    allocator,
                    list,
                    submitted: 0,
                    released: 0,
                    landing: None,
                })
            }
        };
        let slots = (|| -> Result<_> { Ok([slot()?, slot()?]) })().step("command lists")?;
        // SAFETY: default security, auto-reset, unnamed; live device and heap.
        let (event, increment, cpu_start, gpu_start) = unsafe {
            (
                Handle(CreateEventW(None, false, false, PCWSTR::null())?),
                device.GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV),
                heap.GetCPUDescriptorHandleForHeapStart(),
                heap.GetGPUDescriptorHandleForHeapStart(),
            )
        };
        let pool = Arc::new(Pool {
            device: device.clone(),
            repacked: repacked.clone(),
            state: Mutex::new(PoolState::default()),
            poisoned: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            frames: AtomicU64::new(0),
            copies: AtomicU64::new(0),
        });
        let zero_copy = Box::new(Self {
            pool,
            inner: Mutex::new(Inner {
                device,
                queue,
                root,
                pso,
                heap,
                cpu_start,
                gpu_start,
                increment,
                repacked,
                released,
                repacked11,
                released11,
                device11: device1,
                context: context4,
                multithread,
                slots,
                size: None,
                next: 0,
                previous: None,
                direct: true,
                repacked_value: 0,
                released_value: 0,
                event,
                poison: None,
            }),
        });
        zero_copy.self_test()?;
        Ok(zero_copy)
    }

    /// Both landing slots, then a size change with an odd chroma width, through the real alloc
    /// and repack paths; padded layouts with garbage in the padding and in the top 6 bits.
    fn self_test(&self) -> Result<()> {
        let mut inner = self.inner.lock().map_err(|_| E_UNEXPECTED)?;
        for (index, (width, height)) in [(64, 64), (64, 64), (66, 18)].into_iter().enumerate() {
            let (frame, bytes) = BaleZcFrame::test_layout(width, height);
            let buffer = self.pool.alloc(bytes).step("self-test alloc")?;
            let memory = buffer.memory.as_ref().ok_or(E_FAIL)?;
            let mut source = vec![0u8; bytes as usize];
            fill_test_planes(&frame, &mut source, index as u32 + 1);
            // SAFETY: a fresh mapped buffer of at least `bytes` that the GPU does not use yet.
            unsafe {
                std::ptr::copy_nonoverlapping(source.as_ptr(), memory.data as *mut u8, source.len())
            };
            let landing = inner.repack(&buffer, &frame).step("self-test repack")?;
            let mut desc = texture_desc(width, height, DXGI_FORMAT_P010, 0);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
            let _lock = Locked::enter(&inner.multithread);
            let staging = texture(&inner.device11, &desc, None).step("self-test staging")?;
            // SAFETY: matching unmapped P010 textures on this device, under the lock.
            unsafe { inner.context.CopyResource(&staging, &landing) };
            let actual = readback_bytes(
                &inner.context,
                &staging,
                2 * width as usize,
                (height + height / 2) as usize,
            )
            .step("self-test readback")?;
            if actual != frame.reference_p010(&source) {
                return Err(E_FAIL.into()).step(&format!("self-test mismatch at {width}x{height}"));
            }
        }
        Ok(())
    }
}

/// Creates a FL11_0 D3D12 device on the D3D11 device's adapter.
///
/// # Safety
/// `module` must be a loaded `d3d12.dll`.
unsafe fn create_device(module: HMODULE, adapter: &IDXGIAdapter) -> Result<ID3D12Device> {
    // SAFETY: the caller guarantees a loaded module; the name is a static C string.
    let proc = unsafe { GetProcAddress(module, s!("D3D12CreateDevice")) }.ok_or(E_NOINTERFACE)?;
    // SAFETY: D3D12CreateDevice's documented signature; both are plain `system` fn pointers.
    let create: CreateDevice = unsafe { std::mem::transmute(proc) };
    let mut raw = std::ptr::null_mut();
    // SAFETY: a live adapter borrowed for the call; `raw` is a live out-slot.
    unsafe {
        create(
            adapter.as_raw(),
            D3D_FEATURE_LEVEL_11_0,
            &ID3D12Device::IID,
            &mut raw,
        )
        .ok()?
    };
    if raw.is_null() {
        return Err(E_FAIL.into());
    }
    // SAFETY: a successful create returned an owned ID3D12Device reference.
    Ok(unsafe { ID3D12Device::from_raw(raw) })
}

/// The landing textures are written through typed P010 plane UAVs.
fn check_p010(device: &ID3D12Device) -> Result<()> {
    let mut support = D3D12_FEATURE_DATA_FORMAT_SUPPORT {
        Format: DXGI_FORMAT_P010,
        ..Default::default()
    };
    // SAFETY: the matching feature struct, sized exactly.
    unsafe {
        device.CheckFeatureSupport(
            D3D12_FEATURE_FORMAT_SUPPORT,
            (&mut support as *mut D3D12_FEATURE_DATA_FORMAT_SUPPORT).cast(),
            size_of::<D3D12_FEATURE_DATA_FORMAT_SUPPORT>() as u32,
        )?
    };
    if support.Support1.0 & D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW.0 == 0 {
        return Err(E_NOTIMPL.into());
    }
    Ok(())
}

fn shared_fence(
    device: &ID3D12Device,
    device11: &ID3D11Device5,
) -> Result<(ID3D12Fence, ID3D11Fence)> {
    // SAFETY: live devices; default security, unnamed; the NT handle closes after the open.
    unsafe {
        let fence: ID3D12Fence = device
            .CreateFence(0, D3D12_FENCE_FLAG_SHARED)
            .step("D3D12 shared fence")?;
        let shared = Handle(
            device
                .CreateSharedHandle(&fence, None, GENERIC_ALL, PCWSTR::null())
                .step("fence shared handle")?,
        );
        let mut opened: Option<ID3D11Fence> = None;
        device11
            .OpenSharedFence(shared.0, &mut opened)
            .step("D3D11 open fence")?;
        Ok((fence, opened.ok_or(E_FAIL)?))
    }
}

/// Root signature: 8 constants at b0 (param 0); a table of t0 then u0..u1 (param 1).
///
/// # Safety
/// `module` must be a loaded `d3d12.dll`.
unsafe fn root_signature(module: HMODULE, device: &ID3D12Device) -> Result<ID3D12RootSignature> {
    // SAFETY: the caller guarantees a loaded module; the name is a static C string.
    let proc = unsafe { GetProcAddress(module, s!("D3D12SerializeRootSignature")) }
        .ok_or(E_NOINTERFACE)?;
    // SAFETY: D3D12SerializeRootSignature's documented signature.
    let serialize: SerializeRootSignature = unsafe { std::mem::transmute(proc) };
    let ranges = [
        D3D12_DESCRIPTOR_RANGE {
            RangeType: D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors: 1,
            BaseShaderRegister: 0,
            RegisterSpace: 0,
            OffsetInDescriptorsFromTableStart: 0,
        },
        D3D12_DESCRIPTOR_RANGE {
            RangeType: D3D12_DESCRIPTOR_RANGE_TYPE_UAV,
            NumDescriptors: 2,
            BaseShaderRegister: 0,
            RegisterSpace: 0,
            OffsetInDescriptorsFromTableStart: 1,
        },
    ];
    let parameters = [
        D3D12_ROOT_PARAMETER {
            ParameterType: D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
            Anonymous: D3D12_ROOT_PARAMETER_0 {
                Constants: D3D12_ROOT_CONSTANTS {
                    ShaderRegister: 0,
                    RegisterSpace: 0,
                    Num32BitValues: 8,
                },
            },
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
        },
        D3D12_ROOT_PARAMETER {
            ParameterType: D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            Anonymous: D3D12_ROOT_PARAMETER_0 {
                DescriptorTable: D3D12_ROOT_DESCRIPTOR_TABLE {
                    NumDescriptorRanges: ranges.len() as u32,
                    pDescriptorRanges: ranges.as_ptr(),
                },
            },
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
        },
    ];
    let desc = D3D12_ROOT_SIGNATURE_DESC {
        NumParameters: parameters.len() as u32,
        pParameters: parameters.as_ptr(),
        NumStaticSamplers: 0,
        pStaticSamplers: std::ptr::null(),
        Flags: D3D12_ROOT_SIGNATURE_FLAG_NONE,
    };
    let (mut blob, mut error) = (std::ptr::null_mut(), std::ptr::null_mut());
    // SAFETY: the description and its arrays outlive the call; both out-slots are ours.
    let result = unsafe { serialize(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &mut blob, &mut error) };
    if !error.is_null() {
        // SAFETY: an owned blob reference returned by the call.
        drop(unsafe { ID3DBlob::from_raw(error) });
    }
    if blob.is_null() {
        result.ok()?;
        return Err(E_FAIL.into());
    }
    // SAFETY: an owned blob reference returned by the call.
    let blob = unsafe { ID3DBlob::from_raw(blob) };
    result.ok()?;
    // SAFETY: the blob owns GetBufferSize readable bytes while it lives.
    unsafe {
        let bytes =
            std::slice::from_raw_parts(blob.GetBufferPointer() as *const u8, blob.GetBufferSize());
        device.CreateRootSignature(0, bytes)
    }
}

fn live() -> Result<MutexGuard<'static, BTreeMap<usize, Arc<Buffer>>>> {
    LIVE.lock().map_err(|_| E_UNEXPECTED.into())
}

fn boundary(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED.0)
}

/// Prefixes an error with the step that failed. Only the HRESULT crosses the C ABI; the text is
/// for [`diagnose_create`] (tests and the debug probe), and is built on failure only.
trait Step<T> {
    fn step(self, name: &str) -> Result<T>;
}

impl<T> Step<T> for Result<T> {
    fn step(self, name: &str) -> Result<T> {
        self.map_err(|error| Error::new(error.code(), format!("{name}: {}", error.message())))
    }
}

/// Runs a zero-copy create (with its self-test) and destroys it again: `Ok` or the failing step
/// and HRESULT. Not part of the C ABI.
///
/// # Safety
/// As for [`bale_zc_create`]; both pointers must be non-null.
#[doc(hidden)]
pub unsafe fn diagnose_create(
    device: *mut c_void,
    context: *mut c_void,
) -> std::result::Result<(), String> {
    // SAFETY: the caller guarantees live interfaces.
    let (Some(device), Some(context)) = (unsafe {
        (
            ID3D11Device::from_raw_borrowed(&device),
            ID3D11DeviceContext::from_raw_borrowed(&context),
        )
    }) else {
        return Err("null device or context".into());
    };
    match BaleZeroCopy::create(device, context) {
        Ok(handle) => {
            // SAFETY: a handle created above and never shared.
            unsafe { bale_zc_destroy(Box::into_raw(handle)) };
            Ok(())
        }
        Err(error) => Err(format!(
            "{} (HRESULT=0x{:08x})",
            error.message(),
            error.code().0 as u32
        )),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_zc_abi_version() -> u32 {
    ZERO_COPY_ABI_VERSION
}

/// Registers the caller's `d3d12.dll` for later creates. Null is rejected.
///
/// # Safety
/// `module` must be a module handle that stays loaded for the rest of the process.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_register_d3d12(module: *mut c_void) -> i32 {
    if module.is_null() {
        return E_INVALIDARG.0;
    }
    D3D12.store(module, Ordering::Release);
    S_OK
}

/// Creates a zero-copy handle on Media Foundation's D3D11 device and immediate context (which must
/// be multithread-protected). Success includes a repack readback self-test. Fails with
/// E_NOINTERFACE before `bale_zc_register_d3d12`, and E_NOTIMPL without the shader or without
/// P010 UAV support.
///
/// # Safety
/// Non-null device/context pointers must be live ID3D11Device/ID3D11DeviceContext interfaces.
/// `out` must be null or writable for one handle pointer; on failure a non-null `out` is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_create(
    device: *mut c_void,
    context: *mut c_void,
    out: *mut *mut BaleZeroCopy,
) -> i32 {
    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG.0;
        }
        // SAFETY: the caller guarantees writable, aligned output storage.
        unsafe { out.write(std::ptr::null_mut()) };
        // SAFETY: non-null inputs are live borrowed interfaces; casts add our own references.
        let (Some(device), Some(context)) = (unsafe {
            (
                ID3D11Device::from_raw_borrowed(&device),
                ID3D11DeviceContext::from_raw_borrowed(&context),
            )
        }) else {
            return E_INVALIDARG.0;
        };
        match BaleZeroCopy::create(device, context) {
            Ok(handle) => {
                // SAFETY: the output is valid; ownership transfers to the caller.
                unsafe { out.write(Box::into_raw(handle)) };
                S_OK
            }
            Err(error) => error.code().0,
        }
    })
}

/// Allocates a mapped decode buffer of at least `bytes` (64 KiB aligned, at most 512 MiB) from any
/// thread. `out_data` receives the CPU address, `out_buffer` an opaque id for repack and release.
/// Fails (E_OUTOFMEMORY, E_FAIL after a repack failure) instead of blocking for long; the caller
/// then uses ordinary memory.
///
/// # Safety
/// A non-null handle must be live. Outputs must be null or writable; on failure non-null outputs
/// are null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_alloc(
    handle: *const BaleZeroCopy,
    bytes: u64,
    out_data: *mut *mut u8,
    out_buffer: *mut *mut c_void,
) -> i32 {
    boundary(|| {
        if out_data.is_null() || out_buffer.is_null() {
            return E_INVALIDARG.0;
        }
        // SAFETY: the caller guarantees writable outputs.
        unsafe {
            out_data.write(std::ptr::null_mut());
            out_buffer.write(std::ptr::null_mut());
        }
        // SAFETY: a non-null handle is live; alloc only uses the shared pool.
        let Some(zero_copy) = (unsafe { handle.as_ref() }) else {
            return E_INVALIDARG.0;
        };
        let buffer = match zero_copy.pool.alloc(bytes) {
            Ok(buffer) => buffer,
            Err(error) => return error.code().0,
        };
        let Some(data) = buffer.memory.as_ref().map(|memory| memory.data) else {
            return E_FAIL.0;
        };
        let id = NEXT_ID.fetch_add(1, Ordering::Relaxed).wrapping_mul(2) | 1;
        match live() {
            Ok(mut live) => live.insert(id, buffer),
            Err(error) => return error.code().0,
        };
        // SAFETY: the caller guarantees writable outputs.
        unsafe {
            out_data.write(data as *mut u8);
            out_buffer.write(id as *mut c_void);
        }
        S_OK
    })
}

/// Releases a buffer id from any thread. Its memory is reused after any repack reading it ran.
/// An unknown id returns E_INVALIDARG and is never dereferenced.
///
/// # Safety
/// The CPU memory of a released buffer must not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_release(buffer: *mut c_void) -> i32 {
    boundary(|| {
        let removed = match live() {
            Ok(mut live) => live.remove(&(buffer as usize)),
            Err(error) => return error.code().0,
        };
        // Dropped outside the LIVE lock; returns the memory to its pool.
        match removed {
            Some(_) => S_OK,
            None => E_INVALIDARG.0,
        }
    })
}

/// Repacks the 10-bit picture `frame` in `buffer` into a P010 landing texture and writes a
/// borrowed ID3D11Texture2D to `out_texture`: P010, `width` x `height`, valid until the next
/// repack or destroy. The immediate context waits for the repack on the GPU, so commands queued
/// after this call see the picture. The caller must queue its last use of the texture before the
/// next repack call and should flush the context once per frame.
///
/// Returns S_FALSE with a null texture when `buffer` is not a live buffer of this handle (the
/// caller then takes its ordinary path), E_INVALIDARG for a layout that does not fit the buffer,
/// and otherwise a failure that poisons the handle: later repacks return it and allocs fail.
///
/// # Safety
/// A non-null handle must be live; repack calls are serialized. A non-null `frame` must be
/// readable; `out_texture` must be null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_repack(
    handle: *const BaleZeroCopy,
    buffer: *mut c_void,
    frame: *const BaleZcFrame,
    out_texture: *mut *mut c_void,
) -> i32 {
    boundary(|| {
        if out_texture.is_null() {
            return E_INVALIDARG.0;
        }
        // SAFETY: the caller guarantees a writable output.
        unsafe { out_texture.write(std::ptr::null_mut()) };
        // SAFETY: non-null pointers are live and readable per the caller.
        let (Some(zero_copy), Some(frame)) =
            (unsafe { handle.as_ref() }, unsafe { frame.as_ref() })
        else {
            return E_INVALIDARG.0;
        };
        let buffer = match live() {
            Ok(live) => live.get(&(buffer as usize)).cloned(),
            Err(error) => return error.code().0,
        };
        let Some(buffer) = buffer.filter(|buffer| Arc::ptr_eq(&buffer.pool, &zero_copy.pool))
        else {
            return S_FALSE;
        };
        let Some(bytes) = buffer.memory.as_ref().map(|memory| memory.bytes) else {
            return E_FAIL.0;
        };
        if !frame.fits(bytes) {
            return E_INVALIDARG.0;
        }
        let Ok(mut inner) = zero_copy.inner.lock() else {
            zero_copy.pool.poisoned.store(true, Ordering::Release);
            return E_UNEXPECTED.0;
        };
        if let Some(hr) = inner.poison {
            return hr;
        }
        let hr = match inner.repack(&buffer, frame) {
            Ok(texture) => {
                // SAFETY: the caller guarantees a writable output. The landing slot keeps its
                // own reference, so the borrowed pointer outlives this clone.
                unsafe { out_texture.write(texture.as_raw()) };
                zero_copy.pool.frames.fetch_add(1, Ordering::Relaxed);
                if !inner.direct {
                    zero_copy.pool.copies.fetch_add(1, Ordering::Relaxed);
                }
                return S_OK;
            }
            Err(error) => error.code().0,
        };
        inner.poison = Some(hr);
        zero_copy.pool.poisoned.store(true, Ordering::Release);
        hr
    })
}

/// Counters for one handle. Buffers include the self-test's.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleZcStats {
    pub buffers_created: u64,
    pub buffers_live: u64,
    pub buffers_peak: u64,
    pub buffers_pooled: u64,
    pub frames_repacked: u64,
    pub alloc_failures: u64,
    pub alloc_waits: u64,
    /// Frames that took the extra GPU copy because D3D11 cannot open a shared UAV P010 texture.
    pub landing_copies: u64,
}

/// Writes the handle's counters, from any thread.
///
/// # Safety
/// A non-null handle must be live; `out` must be null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_stats(handle: *const BaleZeroCopy, out: *mut BaleZcStats) -> i32 {
    boundary(|| {
        // SAFETY: non-null pointers are live and writable per the caller.
        let (Some(zero_copy), Some(out)) = (unsafe { handle.as_ref() }, unsafe { out.as_mut() })
        else {
            return E_INVALIDARG.0;
        };
        let Ok(state) = zero_copy.pool.state.lock() else {
            return E_UNEXPECTED.0;
        };
        *out = BaleZcStats {
            buffers_created: state.created,
            buffers_live: state.live as u64,
            buffers_peak: state.peak,
            buffers_pooled: state.free.len() as u64,
            frames_repacked: zero_copy.pool.frames.load(Ordering::Relaxed),
            alloc_failures: state.failures,
            alloc_waits: state.waits,
            landing_copies: zero_copy.pool.copies.load(Ordering::Relaxed),
        };
        S_OK
    })
}

/// Destroys a handle after waiting for both devices to finish with its textures. Buffers still
/// held by the decoder stay valid and are freed by `bale_zc_release`. Null is a no-op.
///
/// # Safety
/// A non-null pointer must be a live handle from create, destroyed once, with no call running.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_zc_destroy(handle: *mut BaleZeroCopy) {
    boundary(|| {
        if handle.is_null() {
            return S_OK;
        }
        // SAFETY: ownership of this once-created Box returns exclusively to this call.
        let BaleZeroCopy { pool, inner } = *unsafe { Box::from_raw(handle) };
        pool.closed.store(true, Ordering::Release);
        drop(inner);
        let free = pool
            .state
            .lock()
            .map(|mut state| std::mem::take(&mut state.free));
        if let Ok(free) = free {
            // SAFETY: live fence.
            let completed = unsafe { pool.repacked.GetCompletedValue() };
            for memory in free {
                if memory.gpu_fence > completed {
                    std::mem::forget(memory);
                }
            }
        }
        S_OK
    });
}
