use super::texture_height;
use std::mem::ManuallyDrop;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_OUTOFMEMORY, E_POINTER};
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::{HRESULT, Interface, Result};

pub(super) struct D12 {
    textures: Vec<ID3D12Resource>,
    readback: ID3D12Resource,
    queries: ID3D12QueryHeap,
    descriptors: ID3D12DescriptorHeap,
    list: ID3D12GraphicsCommandList,
    allocator: ID3D12CommandAllocator,
    queue: ID3D12CommandQueue,
    fence: ID3D12Fence,
    serial: u64,
    device: ID3D12Device,
}

fn committed(
    device: &ID3D12Device,
    heap: D3D12_HEAP_TYPE,
    desc: &D3D12_RESOURCE_DESC,
    state: D3D12_RESOURCE_STATES,
) -> Result<ID3D12Resource> {
    let mut out = None;
    let properties = D3D12_HEAP_PROPERTIES {
        Type: heap,
        CreationNodeMask: 1,
        VisibleNodeMask: 1,
        ..Default::default()
    };
    unsafe {
        device.CreateCommittedResource(
            &properties,
            D3D12_HEAP_FLAG_NONE,
            desc,
            state,
            None,
            &mut out,
        )?
    };
    out.ok_or_else(|| E_POINTER.into())
}

fn barrier(
    resource: &ID3D12Resource,
    before: D3D12_RESOURCE_STATES,
    after: D3D12_RESOURCE_STATES,
) -> D3D12_RESOURCE_BARRIER {
    // Borrow the pointer without AddRef; ManuallyDrop in the D3D12 barrier does not Release it.
    // Our resource remains live through submission AND fence completion.
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: unsafe { std::mem::transmute_copy(resource) },
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: before,
                StateAfter: after,
            }),
        },
    }
}

impl D12 {
    pub(super) fn new(device: ID3D12Device) -> Result<Self> {
        unsafe {
            let queue = device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC {
                Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
                ..Default::default()
            })?;
            let allocator = device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)?;
            let list: ID3D12GraphicsCommandList =
                device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None)?;
            list.Close()?;
            let mut queries = None;
            device.CreateQueryHeap(
                &D3D12_QUERY_HEAP_DESC {
                    Type: D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
                    Count: 2,
                    NodeMask: 0,
                },
                &mut queries,
            )?;
            let readback = committed(
                &device,
                D3D12_HEAP_TYPE_READBACK,
                &crate::d3d12::resource(
                    D3D12_RESOURCE_DIMENSION_BUFFER,
                    16,
                    1,
                    DXGI_FORMAT_UNKNOWN,
                    D3D12_RESOURCE_FLAG_NONE,
                ),
                D3D12_RESOURCE_STATE_COPY_DEST,
            )?;
            let descriptors = device.CreateDescriptorHeap(&D3D12_DESCRIPTOR_HEAP_DESC {
                Type: D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
                NumDescriptors: 1,
                ..Default::default()
            })?;
            let fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE)?;
            Ok(Self {
                textures: Vec::new(),
                readback,
                queries: queries.ok_or(E_POINTER)?,
                descriptors,
                list,
                allocator,
                queue,
                fence,
                serial: 0,
                device,
            })
        }
    }

    pub(super) fn allocate(&mut self, bytes: u64) -> Result<()> {
        self.textures.try_reserve(1).map_err(|_| E_OUTOFMEMORY)?;
        let mut desc = crate::d3d12::resource(
            D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            4096,
            texture_height(bytes).ok_or(E_INVALIDARG)?,
            DXGI_FORMAT_R8G8B8A8_UNORM,
            D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
        );
        desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        self.textures.push(committed(
            &self.device,
            D3D12_HEAP_TYPE_DEFAULT,
            &desc,
            D3D12_RESOURCE_STATE_RENDER_TARGET,
        )?);
        Ok(())
    }

    fn reset(&self) -> Result<()> {
        unsafe {
            self.allocator.Reset()?;
            self.list.Reset(&self.allocator, None)
        }
    }

    fn submit(&mut self) -> Result<()> {
        unsafe {
            self.list.Close()?;
            self.queue.ExecuteCommandLists(&[Some(self.list.cast()?)]);
            self.serial += 1;
            self.queue.Signal(&self.fence, self.serial)?;
        }
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            unsafe {
                self.device.GetDeviceRemovedReason()?;
                if self.fence.GetCompletedValue() >= self.serial {
                    return Ok(());
                }
            }
            if Instant::now() >= deadline {
                return Err(HRESULT(0x8007_0102u32 as i32).into());
            }
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    pub(super) fn touch_last(&mut self) -> Result<()> {
        self.reset()?;
        unsafe {
            let handle = self.descriptors.GetCPUDescriptorHandleForHeapStart();
            self.device
                .CreateRenderTargetView(self.textures.last().ok_or(E_FAIL)?, None, handle);
            self.list
                .ClearRenderTargetView(handle, &[0.25, 0.5, 0.75, 1.0], None);
        }
        self.submit()
    }

    fn copy(&mut self, timed: bool) -> Result<()> {
        self.reset()?;
        unsafe {
            self.list.ResourceBarrier(&[
                barrier(
                    &self.textures[0],
                    D3D12_RESOURCE_STATE_RENDER_TARGET,
                    D3D12_RESOURCE_STATE_COPY_SOURCE,
                ),
                barrier(
                    &self.textures[1],
                    D3D12_RESOURCE_STATE_RENDER_TARGET,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                ),
            ]);
            if timed {
                self.list
                    .EndQuery(&self.queries, D3D12_QUERY_TYPE_TIMESTAMP, 0);
            }
            self.list.CopyResource(&self.textures[1], &self.textures[0]);
            if timed {
                self.list
                    .EndQuery(&self.queries, D3D12_QUERY_TYPE_TIMESTAMP, 1);
                self.list.ResolveQueryData(
                    &self.queries,
                    D3D12_QUERY_TYPE_TIMESTAMP,
                    0,
                    2,
                    &self.readback,
                    0,
                );
            }
            self.list.ResourceBarrier(&[
                barrier(
                    &self.textures[0],
                    D3D12_RESOURCE_STATE_COPY_SOURCE,
                    D3D12_RESOURCE_STATE_RENDER_TARGET,
                ),
                barrier(
                    &self.textures[1],
                    D3D12_RESOURCE_STATE_COPY_DEST,
                    D3D12_RESOURCE_STATE_RENDER_TARGET,
                ),
            ]);
        }
        self.submit()
    }

    pub(super) fn copy_ms(&mut self, baseline: bool) -> Result<f64> {
        // Do not warm up later steps: the timed copy must include any first-use paging cost.
        if baseline {
            self.copy(false)?;
        }
        self.copy(true)?;
        let mut ptr = std::ptr::null_mut();
        let (start, end, frequency) = unsafe {
            self.readback
                .Map(0, Some(&D3D12_RANGE { Begin: 0, End: 16 }), Some(&mut ptr))?;
            if ptr.is_null() {
                return Err(E_POINTER.into());
            }
            let timestamps = std::slice::from_raw_parts(ptr.cast::<u64>(), 2);
            let result = (timestamps[0], timestamps[1]);
            self.readback
                .Unmap(0, Some(&D3D12_RANGE { Begin: 0, End: 0 }));
            (result.0, result.1, self.queue.GetTimestampFrequency()?)
        };
        if frequency == 0 || end <= start {
            return Err(E_FAIL.into());
        }
        Ok((end - start) as f64 * 1000.0 / frequency as f64)
    }

    pub(super) fn removed(&self) -> i32 {
        unsafe { self.device.GetDeviceRemovedReason() }
            .err()
            .map_or(0, |e| e.code().0)
    }
}
