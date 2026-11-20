//! Resource-owned RAII markers, matching lifecycle.rs's device release sentinel.
use super::{Tracked, texture_size};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BUFFER_DESC, D3D11_TEXTURE2D_DESC, ID3D11Buffer, ID3D11DeviceChild, ID3D11Texture2D,
};
use windows::Win32::Graphics::Direct3D12::{D3D12_RESOURCE_DIMENSION_BUFFER, ID3D12Resource};
use windows::Win32::Graphics::Dxgi::IDXGISwapChain1;
use windows::Win32::System::Com::{IAgileObject, IAgileObject_Impl};
use windows::core::{GUID, Interface, Result, implement};

const MEMORY_SENTINEL: GUID = GUID::from_u128(0xf1d1_728c_0589_4f40_a191_5e8a_54b4_3d92);

#[implement(IAgileObject)]
struct MemorySentinel {
    _tracked: Tracked,
}
impl IAgileObject_Impl for MemorySentinel_Impl {}

fn sentinel(category: u32, bytes: u64, count: i32) -> IAgileObject {
    MemorySentinel {
        _tracked: Tracked::with_count(category, bytes, count),
    }
    .into()
}

/// Logical size; Xbox tiling/alignment padding is not counted.
pub fn texture_bytes(desc: &D3D11_TEXTURE2D_DESC) -> u64 {
    texture_size(
        u64::from(desc.Width),
        desc.Height,
        desc.Format.0,
        desc.MipLevels,
        desc.ArraySize,
        desc.SampleDesc.Count,
    )
}

pub fn buffer_bytes(desc: &D3D11_BUFFER_DESC) -> u64 {
    u64::from(desc.ByteWidth)
}

/// Call once immediately after allocation, before creating views or publishing any reference.
pub(crate) fn track_texture(texture: &ID3D11Texture2D, category: u32) -> Result<()> {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    // SAFETY: live resource and writable local descriptor.
    unsafe { texture.GetDesc(&mut desc) };
    track_child(&texture.cast()?, category, texture_bytes(&desc), 1)
}

pub(crate) fn track_buffer(buffer: &ID3D11Buffer, category: u32) -> Result<()> {
    let mut desc = D3D11_BUFFER_DESC::default();
    // SAFETY: live resource and writable local descriptor.
    unsafe { buffer.GetDesc(&mut desc) };
    track_child(&buffer.cast()?, category, buffer_bytes(&desc), 1)
}

fn track_child(resource: &ID3D11DeviceChild, category: u32, bytes: u64, count: i32) -> Result<()> {
    let sentinel = sentinel(category, bytes, count);
    // SAFETY: private data retains the marker but the marker owns no COM resource (no cycle).
    // A failed attach drops the local marker and reverses accounting before returning the error.
    unsafe { resource.SetPrivateDataInterface(&MEMORY_SENTINEL, &sentinel) }
}

pub(crate) fn track_committed(resource: &ID3D12Resource, category: u32) -> Result<()> {
    // SAFETY: live resource, metadata only.
    let desc = unsafe { resource.GetDesc() };
    let bytes = if desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER {
        desc.Width
    } else {
        texture_size(
            desc.Width,
            desc.Height,
            desc.Format.0,
            u32::from(desc.MipLevels),
            u32::from(desc.DepthOrArraySize),
            desc.SampleDesc.Count,
        )
    };
    let sentinel = sentinel(category, bytes, 1);
    // SAFETY: as for D3D11, held until the resource's last Release.
    unsafe { resource.SetPrivateDataInterface(&MEMORY_SENTINEL, &sentinel) }
}

/// One marker on buffer zero covers every buffer in the chain. DXGI destroys the old buffers
/// during a successful ResizeBuffers, which drops the old guard before the new one is attached.
/// A failed resize retains the old buffers and their accounting. No extra buffer reference is kept.
pub(crate) fn track_swap_chain(swap_chain: &IDXGISwapChain1, category: u32) -> Result<()> {
    // SAFETY: live swap chain; temporary buffer reference released before the next ResizeBuffers.
    let (desc, buffer): (_, ID3D11Texture2D) =
        unsafe { (swap_chain.GetDesc1()?, swap_chain.GetBuffer(0)?) };
    let mut texture = D3D11_TEXTURE2D_DESC::default();
    // SAFETY: live back buffer and writable descriptor.
    unsafe { buffer.GetDesc(&mut texture) };
    track_child(
        &buffer.cast()?,
        category,
        texture_bytes(&texture).saturating_mul(u64::from(desc.BufferCount)),
        desc.BufferCount.min(i32::MAX as u32) as i32,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::memledger::{BaleMemEntry, bale_mem_snapshot, bale_mem_underflows};
    use windows::Win32::Foundation::HMODULE;
    use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_WARP;
    use windows::Win32::Graphics::Direct3D11::*;
    use windows::Win32::Graphics::Dxgi::Common::*;
    use windows::Win32::Graphics::Dxgi::*;

    fn device() -> ID3D11Device {
        let mut device = None;
        // SAFETY: WARP device with no adapter/software module; writable local output slot.
        unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_WARP,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                None,
            )
            .unwrap();
        }
        device.unwrap()
    }

    fn current(category: u32) -> (i64, i32) {
        let mut rows = [BaleMemEntry::default(); 64];
        // SAFETY: 64 writable slots, enough for all categories.
        unsafe { bale_mem_snapshot(rows.as_mut_ptr(), 64) };
        let row = rows
            .into_iter()
            .find(|e| e.category == category)
            .unwrap_or_default();
        (row.bytes, row.count)
    }

    #[test]
    fn marker_survives_texture_clones_and_views_and_pairs_buffer_release() {
        let _serial = super::super::TEST_LOCK.lock().unwrap();
        let device = device();
        let desc = crate::grain::gpu::texture_desc(
            16,
            8,
            DXGI_FORMAT_R8G8B8A8_UNORM,
            D3D11_BIND_SHADER_RESOURCE.0 as u32,
        );
        assert_eq!(texture_bytes(&desc), 512);
        let texture = crate::grain::gpu::texture(60, &device, &desc, None).unwrap();
        let clone = texture.clone();
        let mut view = None;
        // SAFETY: compatible texture descriptor, live output slot.
        unsafe {
            device
                .CreateShaderResourceView(&texture, None, Some(&mut view))
                .unwrap()
        };
        drop((texture, clone));
        assert_eq!(current(60), (512, 1));
        drop(view);
        assert_eq!(current(60), (0, 0));

        let desc = D3D11_BUFFER_DESC {
            ByteWidth: 256,
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
            ..Default::default()
        };
        assert_eq!(buffer_bytes(&desc), 256);
        let mut buffer = None;
        // SAFETY: valid constant buffer descriptor and output slot, no initial data.
        unsafe { device.CreateBuffer(&desc, None, Some(&mut buffer)).unwrap() };
        let buffer = buffer.unwrap();
        track_buffer(&buffer, 60).unwrap();
        assert_eq!(current(60), (256, 1));
        drop(buffer);
        assert_eq!(current(60), (0, 0));
        assert_eq!(bale_mem_underflows(60), 0);
    }

    #[test]
    fn swap_chain_resize_and_failure_preserve_accounting() {
        let _serial = super::super::TEST_LOCK.lock().unwrap();
        let device = device();
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: 16,
            Height: 8,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            Scaling: DXGI_SCALING_STRETCH,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            ..Default::default()
        };
        // SAFETY: WARP composition chain, no panel/window required (like subtitle offscreen tests).
        let swap_chain = unsafe {
            let factory: IDXGIFactory2 = device
                .cast::<IDXGIDevice>()
                .unwrap()
                .GetAdapter()
                .unwrap()
                .GetParent()
                .unwrap();
            factory
                .CreateSwapChainForComposition(&device, &desc, None)
                .unwrap()
        };
        track_swap_chain(&swap_chain, 61).unwrap();
        assert_eq!(current(61), (16 * 8 * 4 * 2, 2));
        // Holding a back-buffer reference must make ResizeBuffers fail, retaining the old charge.
        let buffer: ID3D11Texture2D = unsafe { swap_chain.GetBuffer(0).unwrap() };
        // SAFETY: deliberately refused while buffer is referenced; no resources are changed.
        assert!(
            unsafe { swap_chain.ResizeBuffers(2, 32, 16, desc.Format, DXGI_SWAP_CHAIN_FLAG(0)) }
                .is_err()
        );
        assert_eq!(current(61), (16 * 8 * 4 * 2, 2));
        drop(buffer);
        // SAFETY: all back-buffer references have been released.
        unsafe {
            swap_chain
                .ResizeBuffers(2, 32, 16, desc.Format, DXGI_SWAP_CHAIN_FLAG(0))
                .unwrap()
        };
        assert_eq!(current(61), (0, 0));
        track_swap_chain(&swap_chain, 61).unwrap();
        assert_eq!(current(61), (32 * 16 * 4 * 2, 2));
        drop(swap_chain);
        assert_eq!(current(61), (0, 0));
        assert_eq!(bale_mem_underflows(61), 0);
    }
}
