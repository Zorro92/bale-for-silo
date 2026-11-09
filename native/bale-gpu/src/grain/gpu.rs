//! Integer D3D11 grain passes. All context work is serialized and state-isolated.
//! The C-ABI caller provides one serialized owner for create/apply/destroy. Ownership may
//! move between producer, seek and close threads; there must be no concurrent handle calls.

use std::marker::PhantomData;
use std::rc::Rc;

use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_NOTIMPL};
use windows::Win32::Graphics::Direct3D::{
    D3D_FEATURE_LEVEL_11_0, D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST, D3D_SRV_DIMENSION_TEXTURE2D,
};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::{
    DXGI_ERROR_DEVICE_HUNG, DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_RESET,
    DXGI_ERROR_DRIVER_INTERNAL_ERROR,
};
use windows::core::{HRESULT, IUnknown, Interface, Result};

use super::cpu::{self, PixelFormat, Prepared};
use super::params::{BaleGrainParams, PARAMS_SIZE, ValidGrainParams};
use super::template::{GRAIN_HEIGHT, GRAIN_WIDTH, SUB_GRAIN_HEIGHT, SUB_GRAIN_WIDTH};

const VERTEX_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/fullscreen_vs.cso"));
const LUMA_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/grain_luma_ps.cso"));
const CHROMA_SHADER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/grain_chroma_ps.cso"));

/// Six 16-byte registers; arrays avoid HLSL's scalar/array packing differences.
#[repr(C)]
struct Constants {
    geometry: [u32; 4],
    flags: [u32; 4],
    cb: [i32; 4],
    cr: [i32; 4],
    clips: [i32; 4],
    limits: [i32; 4],
}

impl Constants {
    fn new(params: &ValidGrainParams) -> Self {
        let p = params.as_raw();
        let uv = |plane| {
            [
                i32::from(p.uv_mult[plane]),
                i32::from(p.uv_luma_mult[plane]),
                i32::from(p.uv_offset[plane]),
                i32::from(params.chroma_active(plane)),
            ]
        };
        let (ymin, ymax) = cpu::clip_range(params, false);
        let (uvmin, uvmax) = cpu::clip_range(params, true);
        let grain_ctr = 128 << params.bitdepth_min_8();
        Self {
            geometry: [
                p.width,
                p.height,
                u32::from(p.bit_depth),
                if p.bit_depth == 10 { 6 } else { 0 },
            ],
            flags: [
                u32::from(p.scaling_shift),
                u32::from(p.overlap_flag),
                u32::from(p.chroma_scaling_from_luma),
                u32::from(p.num_y_points != 0),
            ],
            cb: uv(0),
            cr: uv(1),
            clips: [ymin, ymax, uvmin, uvmax],
            limits: [-grain_ctr, grain_ctr - 1, params.pixel_max(), 0],
        }
    }
}

struct InputTexture {
    texture: ID3D11Texture2D,
    view: ID3D11ShaderResourceView,
}

impl InputTexture {
    fn new(device: &ID3D11Device, width: u32, height: u32, format: DXGI_FORMAT) -> Result<Self> {
        let texture = texture(
            device,
            &texture_desc(width, height, format, D3D11_BIND_SHADER_RESOURCE.0 as u32),
            None,
        )?;
        let view = shader_view(device, &texture, format)?;
        Ok(Self { texture, view })
    }
}

struct FrameResources {
    key: (u32, u32, DXGI_FORMAT),
    intermediate: ID3D11Texture2D,
    source_views: [ID3D11ShaderResourceView; 2],
    scaling: InputTexture,
    offsets: InputTexture,
}

impl FrameResources {
    fn new(device: &ID3D11Device, desc: &D3D11_TEXTURE2D_DESC) -> Result<Self> {
        // Never inherit staging/decoder/shared flags from the caller's texture.
        let intermediate = texture(
            device,
            &texture_desc(
                desc.Width,
                desc.Height,
                desc.Format,
                D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ),
            None,
        )?;
        let formats = plane_formats(desc.Format);
        Ok(Self {
            key: (desc.Width, desc.Height, desc.Format),
            source_views: [
                shader_view(device, &intermediate, formats[0])?,
                shader_view(device, &intermediate, formats[1])?,
            ],
            intermediate,
            scaling: InputTexture::new(
                device,
                if desc.Format == DXGI_FORMAT_P010 {
                    1024
                } else {
                    256
                },
                3,
                DXGI_FORMAT_R8_UINT,
            )?,
            // Capacity follows the texture, not visible dimensions. Only the visible grid is uploaded.
            offsets: InputTexture::new(
                device,
                desc.Width.div_ceil(32),
                desc.Height.div_ceil(32),
                DXGI_FORMAT_R8_UINT,
            )?,
        })
    }
}

/// Opaque C-ABI handle. It owns references to the device and context, not caller textures.
/// A poisoned handle returns its first fatal HRESULT without further D3D calls.
pub struct BaleGrainGpu {
    device: ID3D11Device1,
    context: ID3D11DeviceContext1,
    multithread: ID3D10Multithread,
    state: ID3DDeviceContextState,
    vs: ID3D11VertexShader,
    luma_ps: ID3D11PixelShader,
    chroma_ps: ID3D11PixelShader,
    constants: ID3D11Buffer,
    luma: InputTexture,
    chroma: InputTexture,
    frame: Option<FrameResources>,
    poison: Option<i32>,
    // Keep safe Rust conservative (!Send/!Sync). The unsafe C ABI may transfer sole
    // ownership between threads under its external serialization contract, not affinity.
    _not_send_sync: PhantomData<Rc<()>>,
}

impl BaleGrainGpu {
    pub(super) fn create(device: &ID3D11Device, context: &ID3D11DeviceContext) -> Result<Self> {
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
            luma: InputTexture::new(
                &device,
                GRAIN_WIDTH as u32,
                GRAIN_HEIGHT as u32,
                DXGI_FORMAT_R16_SINT,
            )?,
            chroma: InputTexture::new(
                &device,
                SUB_GRAIN_WIDTH as u32,
                SUB_GRAIN_HEIGHT as u32,
                DXGI_FORMAT_R16G16_SINT,
            )?,
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
        Ok(gpu)
    }

    pub(super) fn failure(&self) -> Option<i32> {
        self.poison
    }

    pub(super) fn poison(&mut self, hr: i32) {
        self.poison.get_or_insert(hr);
    }

    pub(super) fn apply(
        &mut self,
        params: &ValidGrainParams,
        src: &ID3D11Texture2D,
        dst: &ID3D11Texture2D,
    ) -> Result<()> {
        if let Some(hr) = self.poison {
            return Err(HRESULT(hr).into());
        }
        // Argument rejection is complete before entering/swapping/updating the context.
        let desc = validate_textures(&self.device, params, src, dst)?;
        let _guard = ContextGuard::enter(&self.context, &self.multithread, &self.state);
        let result = self.draw_frame(params, src, dst, &desc);
        // SAFETY: the device is retained by the handle; this query does not change context state.
        let removed = unsafe { self.device.GetDeviceRemovedReason() };
        match (result, removed) {
            (Err(error), reason) => {
                if reason.is_err() || is_device_loss(error.code()) {
                    self.poison(error.code().0);
                }
                Err(error)
            }
            (Ok(()), Err(error)) => {
                self.poison(error.code().0);
                Err(error)
            }
            (Ok(()), Ok(())) => Ok(()),
        }
    }

    /// Caller holds ContextGuard, including during create's readback self-test.
    fn draw_frame(
        &mut self,
        params: &ValidGrainParams,
        src: &ID3D11Texture2D,
        dst: &ID3D11Texture2D,
        desc: &D3D11_TEXTURE2D_DESC,
    ) -> Result<()> {
        if self
            .frame
            .as_ref()
            .is_none_or(|frame| frame.key != (desc.Width, desc.Height, desc.Format))
        {
            self.frame = Some(FrameResources::new(&self.device, desc)?);
        }
        let frame = self.frame.as_ref().ok_or(E_FAIL)?;
        let formats = plane_formats(desc.Format);
        // Both views are created before any writes to the caller's destination.
        let targets = [
            render_view(&self.device, dst, formats[0])?,
            render_view(&self.device, dst, formats[1])?,
        ];
        // SAFETY: validated compatible single-subresource textures are distinct and not mapped.
        unsafe { self.context.CopyResource(&frame.intermediate, src) };
        cpu::with_prepared(params, |prepared| self.upload(params, prepared, frame));
        let views = [
            Some(frame.source_views[0].clone()),
            Some(frame.source_views[1].clone()),
            Some(self.luma.view.clone()),
            Some(self.chroma.view.clone()),
            Some(frame.scaling.view.clone()),
            Some(frame.offsets.view.clone()),
        ];
        // SAFETY: ContextGuard owns our isolated pipeline state under the multithread lock.
        // All bound resources/shaders belong to this device and outlive the queued draws.
        unsafe {
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

    fn upload(&self, params: &ValidGrainParams, prepared: &Prepared, frame: &FrameResources) {
        let chroma: Vec<[i16; 2]> = (0..SUB_GRAIN_HEIGHT)
            .flat_map(|y| {
                (0..SUB_GRAIN_WIDTH).map(move |x| {
                    std::array::from_fn(|plane| {
                        prepared.templates.uv[plane]
                            .as_ref()
                            .map_or(0, |lut| lut[y][x])
                    })
                })
            })
            .collect();
        let scaling = prepared.scaling.concat();
        let constants = Constants::new(params);
        let cols = params.width().div_ceil(32) as u32;
        let rows = params.height().div_ceil(32) as u32;
        let visible_grid = D3D11_BOX {
            left: 0,
            top: 0,
            front: 0,
            right: cols,
            bottom: rows,
            back: 1,
        };
        // SAFETY: all data is fully initialized contiguous integer arrays. Pitches match the
        // destination formats and the full texture (or visible offset box) exactly. D3D copies
        // these CPU inputs before returning; none of these pointers escape UpdateSubresource.
        unsafe {
            self.context.UpdateSubresource(
                &self.luma.texture,
                0,
                None,
                prepared.templates.y.as_ptr().cast(),
                (GRAIN_WIDTH * 2) as u32,
                0,
            );
            self.context.UpdateSubresource(
                &self.chroma.texture,
                0,
                None,
                chroma.as_ptr().cast(),
                (SUB_GRAIN_WIDTH * 4) as u32,
                0,
            );
            self.context.UpdateSubresource(
                &frame.scaling.texture,
                0,
                None,
                scaling.as_ptr().cast(),
                prepared.scaling[0].len() as u32,
                0,
            );
            self.context.UpdateSubresource(
                &frame.offsets.texture,
                0,
                Some(&visible_grid),
                prepared.offsets.as_bytes().as_ptr().cast(),
                cols,
                0,
            );
            self.context.UpdateSubresource(
                &self.constants,
                0,
                None,
                (&constants as *const Constants).cast(),
                0,
                0,
            );
        }
    }

    fn self_test(&mut self) -> Result<()> {
        let _guard = ContextGuard::enter(&self.context, &self.multithread, &self.state);
        for bit_depth in [8, 10] {
            for csfl in [false, true] {
                let params = self_test_params(bit_depth, csfl)
                    .validate()
                    .map_err(|_| E_FAIL)?;
                let format = if bit_depth == 8 {
                    DXGI_FORMAT_NV12
                } else {
                    DXGI_FORMAT_P010
                };
                let bytes = if bit_depth == 8 { 1 } else { 2 };
                let pitch = 64 * bytes;
                let mut plain = vec![0xa5; pitch * 96];
                for plane in 0..3 {
                    let (w, h) = if plane == 0 { (63, 61) } else { (32, 31) };
                    for y in 0..h {
                        for x in 0..w {
                            let value = ((x * 37 + y * 19 + plane * 83 + (x * y) % 29)
                                & ((1 << bit_depth) - 1))
                                as u16;
                            let index = if plane == 0 {
                                y * pitch + x * bytes
                            } else {
                                64 * pitch + y * pitch + (2 * x + plane - 1) * bytes
                            };
                            if bytes == 1 {
                                plain[index] = value as u8;
                            } else {
                                plain[index..index + 2]
                                    .copy_from_slice(&(value << 6).to_le_bytes());
                            }
                        }
                    }
                }
                let mut expected = plain.clone();
                let (y, uv) = expected.split_at_mut(64 * pitch);
                cpu::apply(
                    &params,
                    if bytes == 1 {
                        PixelFormat::Nv12
                    } else {
                        PixelFormat::P010
                    },
                    y,
                    pitch as isize,
                    uv,
                    pitch as isize,
                    1,
                )
                .map_err(|_| E_FAIL)?;
                let mut staging_desc = texture_desc(64, 64, format, 0);
                staging_desc.Usage = D3D11_USAGE_STAGING;
                staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE.0 as u32;
                let src = texture(
                    &self.device,
                    &staging_desc,
                    Some(&D3D11_SUBRESOURCE_DATA {
                        pSysMem: plain.as_ptr().cast(),
                        SysMemPitch: pitch as u32,
                        SysMemSlicePitch: 0,
                    }),
                )?;
                let desc = texture_desc(
                    64,
                    64,
                    format,
                    (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
                );
                let dst = texture(&self.device, &desc, None)?;
                self.draw_frame(&params, &src, &dst, &desc)?;
                staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
                let readback = texture(&self.device, &staging_desc, None)?;
                // SAFETY: matching unmapped single-subresource textures, on the locked context.
                unsafe { self.context.CopyResource(&readback, &dst) };
                let actual = readback_bytes(&self.context, &readback, pitch, 96)?;
                // Compare visible samples AND all even-texture padding, including P010's low bits.
                if actual != expected {
                    return Err(E_FAIL.into());
                }
            }
        }
        // SAFETY: owned device; Map has already synchronized the self-test draws without a Flush.
        unsafe { self.device.GetDeviceRemovedReason() }
    }
}

/// Owns the lock and previous state, without borrowing the handle across mutable resource changes.
pub(crate) struct ContextGuard {
    context: ID3D11DeviceContext1,
    multithread: ID3D10Multithread,
    previous: Option<ID3DDeviceContextState>,
}

impl ContextGuard {
    pub(crate) fn enter(
        context: &ID3D11DeviceContext1,
        multithread: &ID3D10Multithread,
        state: &ID3DDeviceContextState,
    ) -> Self {
        let mut guard = Self {
            context: context.clone(),
            multithread: multithread.clone(),
            previous: None,
        };
        // SAFETY: live interfaces; guard exists before acquiring the lock and always releases it.
        unsafe {
            guard.multithread.Enter();
            guard
                .context
                .SwapDeviceContextState(state, Some(&mut guard.previous));
        }
        guard
    }
}

impl Drop for ContextGuard {
    fn drop(&mut self) {
        unbind(&self.context);
        // SAFETY: Enter/Swap were paired in enter. Restore the renderer's entire state.
        // No fallible calls, allocation or Flush in Drop, including during panic unwinding.
        unsafe {
            self.context
                .SwapDeviceContextState(self.previous.as_ref(), None);
            self.multithread.Leave();
        }
    }
}

pub(crate) fn unbind(context: &ID3D11DeviceContext) {
    // SAFETY: called only under ContextGuard, and only clears our isolated state's bindings.
    unsafe {
        context.PSSetShaderResources(0, Some(&[None, None, None, None, None, None]));
        context.OMSetRenderTargets(None, None);
    }
}

pub(crate) fn check_formats(device: &ID3D11Device) -> Result<()> {
    for (format, required) in required_formats() {
        // SAFETY: live device, format is a known DXGI enum value.
        if unsafe { device.CheckFormatSupport(format)? } & required != required {
            return Err(E_NOTIMPL.into());
        }
    }
    Ok(())
}

/// Shared with the console probe so diagnostics check exactly the create-time requirements.
pub(super) fn required_formats() -> impl Iterator<Item = (DXGI_FORMAT, u32)> {
    [
        DXGI_FORMAT_NV12,
        DXGI_FORMAT_P010,
        DXGI_FORMAT_R8_UINT,
        DXGI_FORMAT_R8G8_UINT,
        DXGI_FORMAT_R16_UINT,
        DXGI_FORMAT_R16G16_UINT,
        DXGI_FORMAT_R16_SINT,
        DXGI_FORMAT_R16G16_SINT,
    ]
    .into_iter()
    .map(|format| {
        let mut required = D3D11_FORMAT_SUPPORT_TEXTURE2D.0 as u32;
        if !matches!(format, DXGI_FORMAT_NV12 | DXGI_FORMAT_P010) {
            required |= D3D11_FORMAT_SUPPORT_SHADER_LOAD.0 as u32;
        }
        if matches!(
            format,
            DXGI_FORMAT_R8_UINT
                | DXGI_FORMAT_R8G8_UINT
                | DXGI_FORMAT_R16_UINT
                | DXGI_FORMAT_R16G16_UINT
        ) {
            required |= D3D11_FORMAT_SUPPORT_RENDER_TARGET.0 as u32;
        }
        (format, required)
    })
}

pub(crate) fn plane_formats(format: DXGI_FORMAT) -> [DXGI_FORMAT; 2] {
    if format == DXGI_FORMAT_P010 {
        [DXGI_FORMAT_R16_UINT, DXGI_FORMAT_R16G16_UINT]
    } else {
        [DXGI_FORMAT_R8_UINT, DXGI_FORMAT_R8G8_UINT]
    }
}

pub(crate) fn texture_desc(
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
    bind: u32,
) -> D3D11_TEXTURE2D_DESC {
    D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: bind,
        ..Default::default()
    }
}

pub(crate) fn texture(
    device: &ID3D11Device,
    desc: &D3D11_TEXTURE2D_DESC,
    data: Option<&D3D11_SUBRESOURCE_DATA>,
) -> Result<ID3D11Texture2D> {
    let mut texture = None;
    // SAFETY: private callers supply one subresource and, if present, initialized data covering
    // desc's complete extent with the declared pitch. Descriptors/data/output live through the call.
    unsafe { device.CreateTexture2D(desc, data.map(std::ptr::from_ref), Some(&mut texture))? };
    texture.ok_or_else(|| E_FAIL.into())
}

pub(crate) fn shader_view(
    device: &ID3D11Device,
    texture: &ID3D11Texture2D,
    format: DXGI_FORMAT,
) -> Result<ID3D11ShaderResourceView> {
    let mut view = None;
    let desc = D3D11_SHADER_RESOURCE_VIEW_DESC {
        Format: format,
        ViewDimension: D3D_SRV_DIMENSION_TEXTURE2D,
        Anonymous: D3D11_SHADER_RESOURCE_VIEW_DESC_0 {
            Texture2D: D3D11_TEX2D_SRV {
                MostDetailedMip: 0,
                MipLevels: 1,
            },
        },
    };
    // SAFETY: known view format and one-mip texture on this device. D3D11.1 selects NV12/P010's
    // plane by format (R vs RG), so Device3/PlaneSlice is not needed. The self-test fails closed.
    unsafe { device.CreateShaderResourceView(texture, Some(&desc), Some(&mut view))? };
    view.ok_or_else(|| E_FAIL.into())
}

pub(crate) fn render_view(
    device: &ID3D11Device,
    texture: &ID3D11Texture2D,
    format: DXGI_FORMAT,
) -> Result<ID3D11RenderTargetView> {
    let mut view = None;
    let desc = D3D11_RENDER_TARGET_VIEW_DESC {
        Format: format,
        ViewDimension: D3D11_RTV_DIMENSION_TEXTURE2D,
        Anonymous: D3D11_RENDER_TARGET_VIEW_DESC_0 {
            Texture2D: D3D11_TEX2D_RTV { MipSlice: 0 },
        },
    };
    // SAFETY: argument validation establishes the render-target bind and compatible dimensions.
    unsafe { device.CreateRenderTargetView(texture, Some(&desc), Some(&mut view))? };
    view.ok_or_else(|| E_FAIL.into())
}

pub(crate) fn same_device(a: &ID3D11Device, b: &ID3D11Device) -> Result<bool> {
    Ok(a.cast::<IUnknown>()? == b.cast::<IUnknown>()?)
}

fn validate_textures(
    device: &ID3D11Device,
    params: &ValidGrainParams,
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
    validate_descs(params, &a, &b)?;
    // SAFETY: live textures, metadata-only queries returning owned COM references.
    if !same_device(device, &unsafe { src.GetDevice()? })?
        || !same_device(device, &unsafe { dst.GetDevice()? })?
    {
        return Err(E_INVALIDARG.into());
    }
    Ok(a)
}

fn validate_descs(
    params: &ValidGrainParams,
    src: &D3D11_TEXTURE2D_DESC,
    dst: &D3D11_TEXTURE2D_DESC,
) -> Result<()> {
    if src.Format != dst.Format
        || src.Width != dst.Width
        || src.Height != dst.Height
        || !matches!(
            (src.Format, params.as_raw().bit_depth),
            (DXGI_FORMAT_NV12, 8) | (DXGI_FORMAT_P010, 10)
        )
        || src.Width < params.as_raw().width
        || src.Height < params.as_raw().height
        || !src.Width.is_multiple_of(2)
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

pub(crate) fn is_device_loss(hr: HRESULT) -> bool {
    matches!(
        hr,
        DXGI_ERROR_DEVICE_REMOVED
            | DXGI_ERROR_DEVICE_RESET
            | DXGI_ERROR_DEVICE_HUNG
            | DXGI_ERROR_DRIVER_INTERNAL_ERROR
    )
}

struct MappedRead<'a> {
    context: &'a ID3D11DeviceContext,
    texture: &'a ID3D11Texture2D,
}

impl Drop for MappedRead<'_> {
    fn drop(&mut self) {
        // SAFETY: created only after a successful Map, with exactly one matching Unmap.
        unsafe { self.context.Unmap(self.texture, 0) };
    }
}

pub(crate) fn readback_bytes(
    context: &ID3D11DeviceContext,
    texture: &ID3D11Texture2D,
    row_bytes: usize,
    rows: usize,
) -> Result<Vec<u8>> {
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    // SAFETY: self-test's unmapped STAGING/CPU_READ texture, locked immediate context, local out.
    unsafe { context.Map(texture, 0, D3D11_MAP_READ, 0, Some(&mut mapped))? };
    let _unmap = MappedRead { context, texture };
    let pitch = mapped.RowPitch as usize;
    let len = pitch
        .checked_mul(rows)
        .filter(|&n| n <= isize::MAX as usize)
        .ok_or(E_FAIL)?;
    if mapped.pData.is_null() || pitch < row_bytes {
        return Err(E_FAIL.into());
    }
    // SAFETY: Map guarantees RowPitch bytes per row. For NV12/P010 the chroma plane starts
    // at RowPitch*Height and has Height/2 rows, so the checked extent covers both planes.
    let bytes = unsafe { std::slice::from_raw_parts(mapped.pData.cast::<u8>(), len) };
    let mut result = Vec::with_capacity(row_bytes * rows);
    for row in bytes.chunks_exact(pitch) {
        result.extend_from_slice(&row[..row_bytes]);
    }
    Ok(result)
}

pub(super) fn self_test_params(bit_depth: u8, csfl: bool) -> BaleGrainParams {
    let mut p = BaleGrainParams {
        struct_size: PARAMS_SIZE,
        seed: 45231,
        width: 63,
        height: 61,
        bit_depth,
        matrix_identity: u8::from(csfl),
        num_y_points: 4,
        chroma_scaling_from_luma: u8::from(csfl),
        num_uv_points: if csfl { [0; 2] } else { [3; 2] },
        scaling_shift: 8,
        ar_coeff_lag: 3,
        ar_coeff_shift: 7,
        grain_scale_shift: 0,
        overlap_flag: 1,
        clip_to_restricted_range: 1,
        y_points: [[0; 2]; 14],
        uv_points: [[[0; 2]; 10]; 2],
        ar_coeffs_y: std::array::from_fn(|i| ((i * 7) % 17) as i8 - 8),
        ar_coeffs_uv: std::array::from_fn(|plane| {
            std::array::from_fn(|i| ((i * 11 + plane * 5) % 19) as i8 - 9)
        }),
        reserved0: [0; 2],
        uv_mult: [-43, 31],
        uv_luma_mult: [81, -27],
        uv_offset: [-12, 117],
    };
    p.y_points[..4].copy_from_slice(&[[0, 47], [59, 173], [177, 91], [255, 211]]);
    p.uv_points[0][..3].copy_from_slice(&[[0, 139], [103, 39], [255, 217]]);
    p.uv_points[1][..3].copy_from_slice(&[[0, 73], [191, 229], [255, 119]]);
    p
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn constants_match_hlsl_registers() {
        assert_eq!(size_of::<Constants>(), 6 * 16);
        assert_eq!(std::mem::offset_of!(Constants, flags), 16);
        assert_eq!(std::mem::offset_of!(Constants, cb), 32);
        assert_eq!(std::mem::offset_of!(Constants, cr), 48);
        assert_eq!(std::mem::offset_of!(Constants, clips), 64);
        assert_eq!(std::mem::offset_of!(Constants, limits), 80);
    }

    #[test]
    fn rejects_invalid_descriptors_including_uncreatable_odd_planar_sizes() {
        let p = self_test_params(8, false).validate().unwrap();
        let good = texture_desc(64, 64, DXGI_FORMAT_NV12, D3D11_BIND_RENDER_TARGET.0 as u32);
        assert!(validate_descs(&p, &good, &good).is_ok());
        for mutate in [
            (|d: &mut D3D11_TEXTURE2D_DESC| d.Width = 63) as fn(&mut D3D11_TEXTURE2D_DESC),
            |d| d.Height = 63,
            |d| d.ArraySize = 2,
            |d| d.MipLevels = 2,
            |d| d.SampleDesc.Count = 2,
            |d| d.SampleDesc.Quality = 1,
            |d| d.BindFlags = 0,
            |d| d.Format = DXGI_FORMAT_P010,
            |d| d.Width = 62,
            |d| d.Height = 60,
        ] {
            let mut bad = good;
            mutate(&mut bad);
            assert_eq!(
                validate_descs(&p, &bad, &bad).unwrap_err().code(),
                E_INVALIDARG
            );
            assert_eq!(
                validate_descs(&p, &good, &bad).unwrap_err().code(),
                E_INVALIDARG
            );
        }
    }
}
