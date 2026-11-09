//! Planar 4:2:0 to NV12/P010 repack on the GPU (repack ABI 1), replacing the CPU swscale step.
//!
//! The CPU copies decoder planes row by row into the uploader's NV12/P010 STAGING texture without
//! converting them: the luma plane holds the raw Y samples (10-bit values unshifted), and each chroma
//! row holds that row's U samples in its left half and V samples in its right half. One draw per
//! plane then shifts (P010: `<< 6`) and interleaves into the caller's render-target texture.
//! Like the grain handle, all context work is serialized by the caller and state-isolated here.

use std::marker::PhantomData;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::rc::Rc;

use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_NOTIMPL};
use windows::Win32::Graphics::Direct3D::{
    D3D_FEATURE_LEVEL_11_0, D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::{HRESULT, IUnknown, Interface, Result};

use crate::grain::gpu::{
    ContextGuard, check_formats, is_device_loss, plane_formats, readback_bytes, render_view,
    same_device, shader_view, texture, texture_desc, unbind,
};

const VERTEX_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/fullscreen_vs.cso"));
const LUMA_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/repack_luma_ps.cso"));
const CHROMA_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/repack_chroma_ps.cso"));

pub const REPACK_ABI_VERSION: u32 = 1;
pub const S_OK: i32 = 0;
const E_UNEXPECTED: i32 = 0x8000ffffu32 as i32;

/// One 16-byte register: shift, chroma half width (samples), sample mask, unused.
#[repr(C)]
struct Constants {
    geometry: [u32; 4],
}

impl Constants {
    fn new(desc: &D3D11_TEXTURE2D_DESC) -> Self {
        let (shift, mask) = if desc.Format == DXGI_FORMAT_P010 {
            (6, 0xffff)
        } else {
            (0, 0xff)
        };
        Self {
            geometry: [shift, desc.Width / 2, mask, 0],
        }
    }
}

struct Intermediate {
    key: (u32, u32, DXGI_FORMAT),
    texture: ID3D11Texture2D,
    views: [ID3D11ShaderResourceView; 2],
}

/// Opaque C-ABI handle owning device/context references, never caller textures.
/// A poisoned handle returns its first fatal HRESULT without further D3D calls.
pub struct BaleRepackGpu {
    device: ID3D11Device1,
    context: ID3D11DeviceContext1,
    multithread: ID3D10Multithread,
    state: ID3DDeviceContextState,
    vs: ID3D11VertexShader,
    luma_ps: ID3D11PixelShader,
    chroma_ps: ID3D11PixelShader,
    constants: ID3D11Buffer,
    frame: Option<Intermediate>,
    poison: Option<i32>,
    // Same contract as the grain handle: sole ownership may move between threads, never shared.
    _not_send_sync: PhantomData<Rc<()>>,
}

impl BaleRepackGpu {
    fn create(device: &ID3D11Device, context: &ID3D11DeviceContext) -> Result<Self> {
        if cfg!(bale_grain_no_shaders) {
            return Err(E_NOTIMPL.into());
        }
        // SAFETY: both interfaces are live borrowed COM references. These are metadata queries.
        if unsafe { context.GetType() } != D3D11_DEVICE_CONTEXT_IMMEDIATE
            || unsafe { device.GetFeatureLevel() }.0 < D3D_FEATURE_LEVEL_11_0.0
            || !same_device(device, &unsafe { context.GetDevice()? })?
        {
            return Err(E_INVALIDARG.into());
        }
        let device: ID3D11Device1 = device.cast()?;
        let context: ID3D11DeviceContext1 = context.cast()?;
        let multithread: ID3D10Multithread = context.cast()?;
        check_formats(&device)?;
        let mut state = None;
        let mut vs = None;
        let mut luma_ps = None;
        let mut chroma_ps = None;
        let mut constants = None;
        // SAFETY: descriptors/bytecode and output locals remain live for the synchronous calls.
        // Flags=0 deliberately excludes SINGLETHREADED; emulate the device's own feature level/IID.
        unsafe {
            device.CreateDeviceContextState(
                0,
                &[device.GetFeatureLevel()],
                D3D11_SDK_VERSION,
                &ID3D11Device::IID,
                None,
                Some(&mut state),
            )?;
            device.CreateVertexShader(VERTEX_SHADER, None, Some(&mut vs))?;
            device.CreatePixelShader(LUMA_SHADER, None, Some(&mut luma_ps))?;
            device.CreatePixelShader(CHROMA_SHADER, None, Some(&mut chroma_ps))?;
            device.CreateBuffer(
                &D3D11_BUFFER_DESC {
                    ByteWidth: size_of::<Constants>() as u32,
                    Usage: D3D11_USAGE_DEFAULT,
                    BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
                    ..Default::default()
                },
                None,
                Some(&mut constants),
            )?;
        }
        let mut gpu = Self {
            device,
            context,
            multithread,
            state: state.ok_or(E_FAIL)?,
            vs: vs.ok_or(E_FAIL)?,
            luma_ps: luma_ps.ok_or(E_FAIL)?,
            chroma_ps: chroma_ps.ok_or(E_FAIL)?,
            constants: constants.ok_or(E_FAIL)?,
            frame: None,
            poison: None,
            _not_send_sync: PhantomData,
        };
        gpu.self_test()?;
        // The self-test's intermediate is tiny; never keep it for the first real frame size.
        gpu.frame = None;
        Ok(gpu)
    }

    fn apply(&mut self, src: &ID3D11Texture2D, dst: &ID3D11Texture2D) -> Result<()> {
        if let Some(hr) = self.poison {
            return Err(HRESULT(hr).into());
        }
        // Argument rejection is complete before entering/swapping/updating the context.
        let desc = validate_textures(&self.device, src, dst)?;
        let _guard = ContextGuard::enter(&self.context, &self.multithread, &self.state);
        let result = self.draw(src, dst, &desc);
        // SAFETY: the device is retained by the handle; this query does not change context state.
        let removed = unsafe { self.device.GetDeviceRemovedReason() };
        match (result, removed) {
            (Err(error), reason) => {
                if reason.is_err() || is_device_loss(error.code()) {
                    self.poison.get_or_insert(error.code().0);
                }
                Err(error)
            }
            (Ok(()), Err(error)) => {
                self.poison.get_or_insert(error.code().0);
                Err(error)
            }
            (Ok(()), Ok(())) => Ok(()),
        }
    }

    /// Caller holds ContextGuard, including during create's readback self-test.
    fn draw(
        &mut self,
        src: &ID3D11Texture2D,
        dst: &ID3D11Texture2D,
        desc: &D3D11_TEXTURE2D_DESC,
    ) -> Result<()> {
        let key = (desc.Width, desc.Height, desc.Format);
        if self.frame.as_ref().is_none_or(|frame| frame.key != key) {
            // Never inherit staging/decoder/shared flags from the caller's texture.
            let texture = texture(
                &self.device,
                &texture_desc(
                    desc.Width,
                    desc.Height,
                    desc.Format,
                    D3D11_BIND_SHADER_RESOURCE.0 as u32,
                ),
                None,
            )?;
            let formats = plane_formats(desc.Format);
            self.frame = Some(Intermediate {
                key,
                views: [
                    shader_view(&self.device, &texture, formats[0])?,
                    shader_view(&self.device, &texture, formats[1])?,
                ],
                texture,
            });
        }
        let frame = self.frame.as_ref().ok_or(E_FAIL)?;
        let formats = plane_formats(desc.Format);
        // Both views are created before any writes to the caller's destination.
        let targets = [
            render_view(&self.device, dst, formats[0])?,
            render_view(&self.device, dst, formats[1])?,
        ];
        let constants = Constants::new(desc);
        let views = [Some(frame.views[0].clone()), Some(frame.views[1].clone())];
        // SAFETY: validated compatible single-subresource textures are distinct and not mapped.
        // ContextGuard owns our isolated pipeline state under the multithread lock; all bound
        // resources/shaders belong to this device and outlive the queued draws. The constants are
        // a fully initialized 16-byte value copied by D3D before UpdateSubresource returns.
        unsafe {
            self.context.CopyResource(&frame.texture, src);
            self.context.UpdateSubresource(
                &self.constants,
                0,
                None,
                (&constants as *const Constants).cast(),
                0,
                0,
            );
            self.context.ClearState();
            self.context
                .IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            self.context.VSSetShader(&self.vs, None);
            self.context
                .PSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
            self.context.PSSetShaderResources(0, Some(&views));
            for (plane, shader) in [&self.luma_ps, &self.chroma_ps].into_iter().enumerate() {
                let sub = plane as u32;
                self.context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                    Width: (desc.Width >> sub) as f32,
                    Height: (desc.Height >> sub) as f32,
                    MinDepth: 0.0,
                    MaxDepth: 1.0,
                    ..Default::default()
                }]));
                self.context
                    .OMSetRenderTargets(Some(&[Some(targets[plane].clone())]), None);
                self.context.PSSetShader(shader, None);
                self.context.Draw(3, 0);
            }
        }
        unbind(&self.context);
        Ok(())
    }

    fn self_test(&mut self) -> Result<()> {
        let _guard = ContextGuard::enter(&self.context, &self.multithread, &self.state);
        for format in [DXGI_FORMAT_NV12, DXGI_FORMAT_P010] {
            let (width, height) = (64, 64);
            let (planar, expected) = self_test_frames(format, width, height);
            let bytes = if format == DXGI_FORMAT_P010 { 2 } else { 1 };
            let pitch = width * bytes;
            let mut staging_desc = texture_desc(width as u32, height as u32, format, 0);
            staging_desc.Usage = D3D11_USAGE_STAGING;
            staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE.0 as u32;
            let src = texture(
                &self.device,
                &staging_desc,
                Some(&D3D11_SUBRESOURCE_DATA {
                    pSysMem: planar.as_ptr().cast(),
                    SysMemPitch: pitch as u32,
                    SysMemSlicePitch: 0,
                }),
            )?;
            let desc = texture_desc(
                width as u32,
                height as u32,
                format,
                (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
            );
            let dst = texture(&self.device, &desc, None)?;
            self.draw(&src, &dst, &desc)?;
            staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
            let readback = texture(&self.device, &staging_desc, None)?;
            // SAFETY: matching unmapped single-subresource textures, on the locked context.
            unsafe { self.context.CopyResource(&readback, &dst) };
            // Every texel is defined, so the whole texture (both planes) must match exactly.
            if readback_bytes(&self.context, &readback, pitch, height * 3 / 2)? != expected {
                return Err(E_FAIL.into());
            }
        }
        // SAFETY: owned device; Map has already synchronized the self-test draws without a Flush.
        unsafe { self.device.GetDeviceRemovedReason() }
    }
}

/// Planar staging bytes and the expected NV12/P010 bytes for a deterministic full-range pattern.
/// Rows are tightly packed (pitch = width * bytes per sample); width must be a multiple of 4.
pub fn self_test_frames(format: DXGI_FORMAT, width: usize, height: usize) -> (Vec<u8>, Vec<u8>) {
    let (bytes, depth) = if format == DXGI_FORMAT_P010 {
        (2, 10)
    } else {
        (1, 8)
    };
    let shift = if bytes == 2 { 6 } else { 0 };
    let pitch = width * bytes;
    let rows = height + height / 2;
    let mut planar = vec![0u8; pitch * rows];
    let mut expected = vec![0u8; pitch * rows];
    let value = |x: usize, y: usize, plane: usize| {
        ((x * 37 + y * 19 + plane * 83 + (x * y) % 29) & ((1 << depth) - 1)) as u16
    };
    let put = |buffer: &mut Vec<u8>, row: usize, sample: usize, word: u16| {
        let index = row * pitch + sample * bytes;
        if bytes == 1 {
            buffer[index] = word as u8;
        } else {
            buffer[index..index + 2].copy_from_slice(&word.to_le_bytes());
        }
    };
    for y in 0..height {
        for x in 0..width {
            put(&mut planar, y, x, value(x, y, 0));
            put(&mut expected, y, x, value(x, y, 0) << shift);
        }
    }
    let half = width / 2;
    for y in 0..height / 2 {
        for x in 0..half {
            let (u, v) = (value(x, y, 1), value(x, y, 2));
            put(&mut planar, height + y, x, u);
            put(&mut planar, height + y, half + x, v);
            put(&mut expected, height + y, 2 * x, u << shift);
            put(&mut expected, height + y, 2 * x + 1, v << shift);
        }
    }
    (planar, expected)
}

fn validate_textures(
    device: &ID3D11Device,
    src: &ID3D11Texture2D,
    dst: &ID3D11Texture2D,
) -> Result<D3D11_TEXTURE2D_DESC> {
    if src.cast::<IUnknown>()? == dst.cast::<IUnknown>()? {
        return Err(E_INVALIDARG.into());
    }
    let mut a = D3D11_TEXTURE2D_DESC::default();
    let mut b = D3D11_TEXTURE2D_DESC::default();
    // SAFETY: borrowed live texture interfaces, writable local descriptors. No GPU commands.
    unsafe {
        src.GetDesc(&mut a);
        dst.GetDesc(&mut b);
    }
    validate_descs(&a, &b)?;
    // SAFETY: live textures, metadata-only queries returning owned COM references.
    if !same_device(device, &unsafe { src.GetDevice()? })?
        || !same_device(device, &unsafe { dst.GetDevice()? })?
    {
        return Err(E_INVALIDARG.into());
    }
    Ok(a)
}

fn validate_descs(src: &D3D11_TEXTURE2D_DESC, dst: &D3D11_TEXTURE2D_DESC) -> Result<()> {
    // Width % 4: the V half of each chroma row must start on a whole two-sample texel.
    if src.Format != dst.Format
        || !matches!(src.Format, DXGI_FORMAT_NV12 | DXGI_FORMAT_P010)
        || src.Width != dst.Width
        || src.Height != dst.Height
        || src.Width == 0
        || src.Height == 0
        || !src.Width.is_multiple_of(4)
        || !src.Height.is_multiple_of(2)
        || dst.BindFlags & D3D11_BIND_RENDER_TARGET.0 as u32 == 0
        || [src, dst].into_iter().any(|d| {
            d.ArraySize != 1
                || d.MipLevels != 1
                || d.SampleDesc.Count != 1
                || d.SampleDesc.Quality != 0
        })
    {
        return Err(E_INVALIDARG.into());
    }
    Ok(())
}

fn boundary(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED)
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_repack_abi_version() -> u32 {
    REPACK_ABI_VERSION
}

/// Create a serialized-owner repack handle. Success includes the NV12/P010 readback self-test.
///
/// # Safety
/// Non-null device/context pointers must be live ID3D11Device/ID3D11DeviceContext interfaces.
/// `out` must be null or writable for one aligned handle pointer; on failure a non-null `out` is
/// set to null. The caller serializes all handle operations, including destruction.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_repack_gpu_create(
    device: *mut core::ffi::c_void,
    context: *mut core::ffi::c_void,
    out: *mut *mut BaleRepackGpu,
) -> i32 {
    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG.0;
        }
        // SAFETY: the caller guarantees writable, aligned output storage.
        unsafe { out.write(std::ptr::null_mut()) };
        // SAFETY: non-null inputs are live borrowed interfaces for this call; casts add our own references.
        let (Some(device), Some(context)) = (unsafe {
            (
                ID3D11Device::from_raw_borrowed(&device),
                ID3D11DeviceContext::from_raw_borrowed(&context),
            )
        }) else {
            return E_INVALIDARG.0;
        };
        match BaleRepackGpu::create(device, context) {
            Ok(gpu) => {
                // SAFETY: the output is valid; ownership transfers to the serialized C-ABI owner.
                unsafe { out.write(Box::into_raw(Box::new(gpu))) };
                S_OK
            }
            Err(error) => error.code().0,
        }
    })
}

/// Repack planar `src` (any usage, unmapped) into `dst` (RENDER_TARGET) without flushing or
/// changing the caller's pipeline state. A panic or device loss permanently poisons the handle.
///
/// # Safety
/// A non-null handle must be live and exclusively used. Non-null texture pointers must be live
/// ID3D11Texture2D interfaces on the handle's device, unmapped and exclusively available for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_repack_gpu_apply(
    handle: *mut BaleRepackGpu,
    src_staging: *mut core::ffi::c_void,
    dst_pooled: *mut core::ffi::c_void,
) -> i32 {
    boundary(|| {
        // SAFETY: a non-null handle is exclusively borrowed under the caller's serialization.
        let Some(gpu) = (unsafe { handle.as_mut() }) else {
            return E_INVALIDARG.0;
        };
        if let Some(hr) = gpu.poison {
            return hr;
        }
        let hr = boundary(|| {
            // SAFETY: the caller supplies live interfaces. Borrow only, never consume references.
            let (Some(src), Some(dst)) = (unsafe {
                (
                    ID3D11Texture2D::from_raw_borrowed(&src_staging),
                    ID3D11Texture2D::from_raw_borrowed(&dst_pooled),
                )
            }) else {
                return E_INVALIDARG.0;
            };
            gpu.apply(src, dst)
                .map_or_else(|error| error.code().0, |()| S_OK)
        });
        if hr == E_UNEXPECTED {
            // ContextGuard has already restored state and released its lock during unwinding.
            gpu.poison.get_or_insert(hr);
        }
        hr
    })
}

/// Release a repack handle. Null is a no-op.
///
/// # Safety
/// A non-null pointer must be a live handle returned by create, destroyed exactly once by its sole owner.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_repack_gpu_destroy(handle: *mut BaleRepackGpu) {
    boundary(|| {
        if !handle.is_null() {
            // SAFETY: ownership of this once-created Box returns exclusively to this call.
            unsafe { drop(Box::from_raw(handle)) };
        }
        S_OK
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn constants_match_the_hlsl_register() {
        assert_eq!(size_of::<Constants>(), 16);
        let p010 = Constants::new(&texture_desc(3840, 2076, DXGI_FORMAT_P010, 0));
        assert_eq!(p010.geometry, [6, 1920, 0xffff, 0]);
        let nv12 = Constants::new(&texture_desc(1920, 1080, DXGI_FORMAT_NV12, 0));
        assert_eq!(nv12.geometry, [0, 960, 0xff, 0]);
    }

    #[test]
    fn self_test_frames_interleave_and_shift() {
        let (planar, expected) = self_test_frames(DXGI_FORMAT_P010, 8, 4);
        let word = |b: &[u8], i: usize| u16::from_le_bytes([b[2 * i], b[2 * i + 1]]);
        // Luma (row 1, x 3) is shifted in place.
        assert_eq!(word(&expected, 8 + 3), word(&planar, 8 + 3) << 6);
        // Chroma row 0: planar U at 1 and V at 4 + 1 become the interleaved pair at 2 and 3.
        let base = 4 * 8;
        assert_eq!(word(&expected, base + 2), word(&planar, base + 1) << 6);
        assert_eq!(word(&expected, base + 3), word(&planar, base + 4 + 1) << 6);
        let (planar, expected) = self_test_frames(DXGI_FORMAT_NV12, 8, 4);
        assert_eq!(expected[base + 3], planar[base + 4 + 1]);
    }

    #[test]
    fn rejects_unrepackable_descriptors() {
        let good = texture_desc(64, 64, DXGI_FORMAT_P010, D3D11_BIND_RENDER_TARGET.0 as u32);
        assert!(validate_descs(&good, &good).is_ok());
        for mutate in [
            (|d: &mut D3D11_TEXTURE2D_DESC| d.Width = 62) as fn(&mut D3D11_TEXTURE2D_DESC),
            |d| d.Height = 63,
            |d| d.Width = 0,
            |d| d.ArraySize = 2,
            |d| d.MipLevels = 2,
            |d| d.SampleDesc.Count = 2,
            |d| d.BindFlags = 0,
            |d| d.Format = DXGI_FORMAT_NV12,
            |d| d.Format = DXGI_FORMAT_R16_UINT,
        ] {
            let mut bad = good;
            mutate(&mut bad);
            assert_eq!(
                validate_descs(&good, &bad).unwrap_err().code(),
                E_INVALIDARG
            );
        }
    }
}
