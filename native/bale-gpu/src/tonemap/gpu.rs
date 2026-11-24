//! Two D3D11.1 plane RTV passes. All fallible preparation precedes caller-output writes.
//! The C owner serializes calls and holds the MF device's multithread protection.
use super::{cpu, types::*};
use crate::grain::gpu::{
    ContextGuard, readback_bytes, render_view, same_device, shader_view, texture, texture_desc,
};
use std::{marker::PhantomData, rc::Rc};
use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_NOTIMPL};
use windows::Win32::Graphics::Direct3D::{
    D3D_FEATURE_LEVEL_11_0, D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST, D3D_SRV_DIMENSION_TEXTURE2DARRAY,
};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::{HRESULT, Interface, Result};

const VS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/fullscreen_vs.cso"));
const Y_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/tonemap_luma_ps.cso"));
const UV_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/tonemap_chroma_ps.cso"));
const AREA_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/tonemap_area_ps.cso"));
const Y_STAGED_PS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/tonemap_luma_staged_ps.cso"));
const UV_STAGED_PS: &[u8] =
    include_bytes!(concat!(env!("OUT_DIR"), "/tonemap_chroma_staged_ps.cso"));
/// Downscale intermediate: area_value's float3 per output pixel (the unclipped linear average, or
/// its encoded Y'CbCr under the legacy math), exact fp32 (16 B/pixel).
const STAGED_FORMAT: DXGI_FORMAT = DXGI_FORMAT_R32G32B32A32_FLOAT;

#[repr(C)]
struct Constants {
    geometry: [u32; 4],
    flags: [u32; 4],
    params: [f32; 4],
    output: [u32; 4],
}
struct Scratch {
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
    texture: ID3D11Texture2D,
    views: [ID3D11ShaderResourceView; 2],
}
/// Output-sized fp32 intermediate of the downscale path, created and released with the stage.
struct Staged {
    width: u32,
    height: u32,
    target: ID3D11RenderTargetView,
    view: ID3D11ShaderResourceView,
    _texture: ID3D11Texture2D,
}

/// Exclusive serialized C-ABI owner; retains the device, never the caller's textures.
pub struct ToneMapGpu {
    device: ID3D11Device1,
    config: BaleToneMapConfig,
    /// None under the debug `nostate` switch: explicit save/restore of our bindings instead.
    state: Option<ID3DDeviceContextState>,
    vs: ID3D11VertexShader,
    y_ps: ID3D11PixelShader,
    uv_ps: ID3D11PixelShader,
    area_ps: ID3D11PixelShader,
    y_staged_ps: ID3D11PixelShader,
    uv_staged_ps: ID3D11PixelShader,
    constants: ID3D11Buffer,
    scratch: Option<Scratch>,
    /// Present when the configured output is smaller than the visible source and the debug
    /// `nostaged` switch is off: one area pass per output pixel instead of one per plane read.
    staged: Option<Staged>,
    /// Current linear-light math, or the legacy one (debug `gammachroma`, console A/B).
    method: cpu::Method,
    #[cfg(feature = "stage-timing")]
    timing: Option<super::timing_gpu::Timing>,
    _not_send_sync: PhantomData<Rc<()>>,
}

impl ToneMapGpu {
    pub(super) fn create(device: &ID3D11Device, config: BaleToneMapConfig) -> Result<Self> {
        config.validate().map_err(HRESULT)?;
        if cfg!(bale_grain_no_shaders) {
            return Err(E_NOTIMPL.into());
        }
        // SAFETY: live borrowed device, metadata-only queries.
        if unsafe { device.GetFeatureLevel() }.0 < D3D_FEATURE_LEVEL_11_0.0
            || config.width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION
            || config.height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION
        {
            return Err(E_INVALIDARG.into());
        }
        let context = unsafe { device.GetImmediateContext()? };
        let device: ID3D11Device1 = device.cast()?;
        let debug = DebugSwitches::read();
        let mut state = None;
        let mut vs = None;
        let mut y_ps = None;
        let mut uv_ps = None;
        let mut area_ps = None;
        let mut y_staged_ps = None;
        let mut uv_staged_ps = None;
        let mut constants = None;
        // SAFETY: valid bytecode/initialized descriptor, local output slots, retained device.
        unsafe {
            if !debug.no_state {
                device.CreateDeviceContextState(
                    0,
                    &[device.GetFeatureLevel()],
                    D3D11_SDK_VERSION,
                    &ID3D11Device::IID,
                    None,
                    Some(&mut state),
                )?;
            }
            device.CreateVertexShader(VS, None, Some(&mut vs))?;
            device.CreatePixelShader(Y_PS, None, Some(&mut y_ps))?;
            device.CreatePixelShader(UV_PS, None, Some(&mut uv_ps))?;
            device.CreatePixelShader(AREA_PS, None, Some(&mut area_ps))?;
            device.CreatePixelShader(Y_STAGED_PS, None, Some(&mut y_staged_ps))?;
            device.CreatePixelShader(UV_STAGED_PS, None, Some(&mut uv_staged_ps))?;
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
        let constants = constants.ok_or(E_FAIL)?;
        crate::memledger::track_buffer(&constants, crate::memledger::TONE_MAP)?;
        let staged = match !debug.no_staged && downscales(&config) {
            true => Some(Staged::create(&device, config.width, config.height)?),
            false => None,
        };
        let mut gpu = Self {
            device,
            config,
            state: match (debug.no_state, state) {
                (true, _) => None,
                (false, Some(state)) => Some(state),
                (false, None) => return Err(E_FAIL.into()),
            },
            vs: vs.ok_or(E_FAIL)?,
            y_ps: y_ps.ok_or(E_FAIL)?,
            uv_ps: uv_ps.ok_or(E_FAIL)?,
            area_ps: area_ps.ok_or(E_FAIL)?,
            y_staged_ps: y_staged_ps.ok_or(E_FAIL)?,
            uv_staged_ps: uv_staged_ps.ok_or(E_FAIL)?,
            constants,
            scratch: None,
            staged,
            method: if debug.gamma_chroma {
                cpu::Method::Legacy
            } else {
                cpu::Method::Linear
            },
            #[cfg(feature = "stage-timing")]
            timing: None,
            _not_send_sync: PhantomData,
        };
        if !debug.no_self_test {
            gpu.self_test(&context)?;
        }
        // Do not keep the small self-test scratch; first decoder input chooses the cache size.
        gpu.scratch = None;
        #[cfg(feature = "stage-timing")]
        {
            gpu.timing = super::timing_gpu::Timing::create(&gpu.device);
        }
        Ok(gpu)
    }

    pub(super) fn apply(
        &mut self,
        context: &ID3D11DeviceContext,
        input: &ID3D11Texture2D,
        subresource: u32,
        output: &ID3D11Texture2D,
        frame: &BaleToneMapFrameInfo,
    ) -> Result<()> {
        let config = self.config;
        self.draw(&config, context, input, subresource, output, frame)
    }

    /// Test hook: drop (false) or recreate (true) the downscale intermediate, so a WARP test can
    /// render the same frame through the per-plane path and the staged path on one handle.
    #[doc(hidden)]
    pub fn set_staged_downscale(&mut self, on: bool) -> Result<()> {
        self.staged = match on && downscales(&self.config) {
            true => Some(Staged::create(
                &self.device,
                self.config.width,
                self.config.height,
            )?),
            false => None,
        };
        Ok(())
    }

    /// Test hook: switch between the current math and the legacy one (debug `gammachroma`) on one
    /// handle, so a WARP test can compare both on the same frame.
    #[doc(hidden)]
    pub fn set_method(&mut self, method: cpu::Method) {
        self.method = method;
    }

    /// Whether the downscale intermediate exists (tests, and the ledger reading in docs).
    #[doc(hidden)]
    pub fn staged_downscale(&self) -> bool {
        self.staged.is_some()
    }

    fn draw(
        &mut self,
        config: &BaleToneMapConfig,
        context: &ID3D11DeviceContext,
        input: &ID3D11Texture2D,
        subresource: u32,
        output: &ID3D11Texture2D,
        frame: &BaleToneMapFrameInfo,
    ) -> Result<()> {
        frame.validate().map_err(HRESULT)?;
        let mut src = D3D11_TEXTURE2D_DESC::default();
        let mut dst = D3D11_TEXTURE2D_DESC::default();
        // SAFETY: live borrowed resources; local descriptors and COM metadata queries only.
        unsafe {
            input.GetDesc(&mut src);
            output.GetDesc(&mut dst);
            if context.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE
                || !same_device(&self.device, &context.GetDevice()?)?
                || !same_device(&self.device, &input.GetDevice()?)?
                || !same_device(&self.device, &output.GetDevice()?)?
            {
                return Err(E_INVALIDARG.into());
            }
        }
        validate_descs(config, &src, &dst, subresource)?;
        let context: ID3D11DeviceContext1 = context.cast()?;
        let multithread: ID3D10Multithread = context.cast()?;
        // Views and all allocation are prepared before a copy, draw or context swap.
        // ABI 2: the output's own format picks the plane views and the shaders' quantization.
        let ten_bit = dst.Format == DXGI_FORMAT_P010;
        let (luma_view, chroma_view) = if ten_bit {
            (DXGI_FORMAT_R16_UNORM, DXGI_FORMAT_R16G16_UNORM)
        } else {
            (DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R8G8_UNORM)
        };
        let targets = [
            render_view(&self.device, output, luma_view)?,
            render_view(&self.device, output, chroma_view)?,
        ];
        // Read shader-readable sources in place (e.g. rav1g's output textures). Decoder arrays are
        // always copied first, even with an SRV bind: desktop D3D11 refuses such views, and on the
        // Xbox reading d3d11va arrays through SRVs cost more memory than the copy (retired 2026-11-24).
        let eight_bit_input = src.Format == DXGI_FORMAT_NV12;
        let direct = read_in_place(src.BindFlags);
        let views = if direct {
            source_views(&self.device, input, subresource, eight_bit_input)?
        } else {
            if self.scratch.as_ref().is_none_or(|s| {
                s.width != src.Width || s.height != src.Height || s.format != src.Format
            }) {
                let texture = texture(
                    crate::memledger::TONE_MAP,
                    &self.device,
                    &texture_desc(
                        src.Width,
                        src.Height,
                        src.Format,
                        D3D11_BIND_SHADER_RESOURCE.0 as u32,
                    ),
                    None,
                )?;
                let views = source_views(&self.device, &texture, 0, eight_bit_input)?;
                self.scratch = Some(Scratch {
                    width: src.Width,
                    height: src.Height,
                    format: src.Format,
                    texture,
                    views,
                });
            }
            self.scratch.as_ref().ok_or(E_FAIL)?.views.clone()
        };
        let (source_width, source_height) = config.source_size();
        // The self-test draws its own 1:1 config: only the configured downscale uses the intermediate.
        let staged = self
            .staged
            .as_ref()
            .filter(|s| s.width == config.width && s.height == config.height && downscales(config))
            .map(|s| (s.target.clone(), s.view.clone()));
        let shaders = match staged {
            Some(_) => [&self.y_staged_ps, &self.uv_staged_ps],
            None => [&self.y_ps, &self.uv_ps],
        };
        let constants = Constants {
            geometry: [source_width, source_height, src.Width, src.Height],
            flags: [
                config.curve,
                u32::from(frame.matrix == 9),
                u32::from(frame.full_range),
                cpu::mode(frame.transfer),
            ],
            params: [
                config.source_peak_nits,
                config.target_white_nits,
                config.desat,
                if ten_bit { 1.0 } else { 0.0 },
            ],
            output: [
                config.width,
                config.height,
                u32::from(eight_bit_input),
                cpu::method_word(frame, self.method),
            ],
        };
        // Fail before queueing work, never return an error after changing the caller's output.
        // D3D11 Draw/Copy/Update queue void commands; asynchronous loss belongs to the caller.
        unsafe { self.device.GetDeviceRemovedReason()? };
        let copy_source = if direct {
            None
        } else {
            Some(&self.scratch.as_ref().ok_or(E_FAIL)?.texture)
        };
        // Private state isolates all bindings and is restored even if Rust unwinds. The debug
        // `nostate` path saves and restores only what the draw touches, without ClearState.
        let guard = match &self.state {
            Some(state) => {
                let guard = Guard::Swapped(ContextGuard::enter(&context, &multithread, state));
                // SAFETY: our private context state is current; clearing it leaves the caller's intact.
                unsafe { context.ClearState() };
                guard
            }
            None => Guard::Saved(Box::new(SavedBindings::enter(&context, &multithread))),
        };
        // Keep query issue/read/close inside this lock and private state. Desktop permits active
        // queries across state swaps, but avoid exercising that path on the shared Xbox MF device.
        #[cfg(feature = "stage-timing")]
        let timing_slot = self
            .timing
            .as_mut()
            .and_then(|t| t.begin(&context, !direct));
        #[cfg(feature = "stage-timing")]
        if let (Some(timing), Some(slot)) = (&mut self.timing, timing_slot) {
            timing.mark(&context, slot, 1);
        }
        // SAFETY: descriptors, devices, slice, dimensions and binds checked above. No flush in apply.
        unsafe {
            if let Some(copy_source) = copy_source {
                context.CopySubresourceRegion(copy_source, 0, 0, 0, 0, input, subresource, None);
            }
            #[cfg(feature = "stage-timing")]
            if let (Some(timing), Some(slot)) = (&mut self.timing, timing_slot) {
                timing.mark(&context, slot, 2);
            }
            context.UpdateSubresource(
                &self.constants,
                0,
                None,
                (&constants as *const Constants).cast(),
                0,
                0,
            );
            context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context.VSSetShader(&self.vs, None);
            context.PSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
            context.PSSetShaderResources(0, Some(&views.map(Some)));
            // Downscale: each output pixel's area sum once (counted in the luma timing interval).
            if let Some((target, _)) = &staged {
                context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                    Width: config.width as f32,
                    Height: config.height as f32,
                    MinDepth: 0.0,
                    MaxDepth: 1.0,
                    ..Default::default()
                }]));
                context.OMSetRenderTargets(Some(&[Some(target.clone())]), None);
                context.PSSetShader(&self.area_ps, None);
                context.Draw(3, 0);
            }
            for (plane, shader) in shaders.into_iter().enumerate() {
                context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                    Width: (config.width >> plane) as f32,
                    Height: (config.height >> plane) as f32,
                    MinDepth: 0.0,
                    MaxDepth: 1.0,
                    ..Default::default()
                }]));
                context.OMSetRenderTargets(Some(&[Some(targets[plane].clone())]), None);
                // Bound only once the intermediate is no longer a render target.
                if let (0, Some((_, view))) = (plane, &staged) {
                    context.PSSetShaderResources(2, Some(&[Some(view.clone())]));
                }
                context.PSSetShader(shader, None);
                context.Draw(3, 0);
                #[cfg(feature = "stage-timing")]
                if let (Some(timing), Some(slot)) = (&mut self.timing, timing_slot) {
                    timing.mark(&context, slot, 3 + plane);
                }
            }
        }
        #[cfg(feature = "stage-timing")]
        if let (Some(timing), Some(slot)) = (&mut self.timing, timing_slot) {
            timing.end(&context, slot);
        }
        drop(guard);
        Ok(())
    }

    fn self_test(&mut self, context: &ID3D11DeviceContext) -> Result<()> {
        // Always 1:1: the self-test checks the curve, and its 16x8 input is smaller than any
        // configured downscale source.
        let config = BaleToneMapConfig {
            width: 16,
            height: 8,
            reserved: [0; 4],
            ..self.config
        };
        for full_range in [0, 1] {
            let frame = BaleToneMapFrameInfo {
                transfer: 16,
                primaries: 9,
                matrix: 9,
                full_range,
                ..Default::default()
            };
            let pattern = cpu::test_pattern(16, 8, true, full_range == 1);
            let expected = cpu::reference_with(&config, &frame, &pattern, 16, 8, self.method)
                .map_err(HRESULT)?;
            let expected10 =
                cpu::reference_p010_with(&config, &frame, &pattern, 16, 8, self.method)
                    .map_err(HRESULT)?;
            let data = D3D11_SUBRESOURCE_DATA {
                pSysMem: pattern.as_ptr().cast(),
                SysMemPitch: 32,
                SysMemSlicePitch: 0,
            };
            for bind in [0, D3D11_BIND_SHADER_RESOURCE.0 as u32] {
                let input = texture(
                    crate::memledger::TONE_MAP,
                    &self.device,
                    &texture_desc(16, 8, DXGI_FORMAT_P010, bind),
                    Some(&data),
                )?;
                for format in [DXGI_FORMAT_NV12, DXGI_FORMAT_P010] {
                    let output = texture(
                        crate::memledger::TONE_MAP,
                        &self.device,
                        &texture_desc(
                            16,
                            8,
                            format,
                            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
                        ),
                        None,
                    )?;
                    self.draw(&config, context, &input, 0, &output, &frame)?;
                    let mut read_desc = texture_desc(16, 8, format, 0);
                    read_desc.Usage = D3D11_USAGE_STAGING;
                    read_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
                    let readback =
                        texture(crate::memledger::TONE_MAP, &self.device, &read_desc, None)?;
                    // SAFETY: private matching resources on the caller's protected immediate context.
                    unsafe { context.CopyResource(&readback, &output) };
                    if format == DXGI_FORMAT_P010 {
                        let actual = readback_bytes(context, &readback, 32, 12)?;
                        compare_self_test_p010(&actual, &expected10).map_err(HRESULT)?;
                    } else {
                        let actual = readback_bytes(context, &readback, 16, 12)?;
                        compare_self_test(&actual, &expected).map_err(HRESULT)?;
                    }
                }
            }
        }
        Ok(())
    }
}

/// The configured output is smaller than the visible source (area downscale).
fn downscales(config: &BaleToneMapConfig) -> bool {
    config.source_size() != (config.width, config.height)
}

impl Staged {
    fn create(device: &ID3D11Device1, width: u32, height: u32) -> Result<Self> {
        let texture = texture(
            crate::memledger::TONE_MAP,
            device,
            &texture_desc(
                width,
                height,
                STAGED_FORMAT,
                (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
            ),
            None,
        )?;
        Ok(Self {
            width,
            height,
            target: render_view(device, &texture, STAGED_FORMAT)?,
            view: shader_view(device, &texture, STAGED_FORMAT)?,
            _texture: texture,
        })
    }
}

fn validate_descs(
    config: &BaleToneMapConfig,
    src: &D3D11_TEXTURE2D_DESC,
    dst: &D3D11_TEXTURE2D_DESC,
    subresource: u32,
) -> Result<()> {
    let required = (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32;
    if (src.Format != DXGI_FORMAT_P010 && src.Format != DXGI_FORMAT_NV12)
        || (dst.Format != DXGI_FORMAT_NV12 && dst.Format != DXGI_FORMAT_P010)
        || src.MipLevels != 1
        || subresource >= src.ArraySize
        || src.Width < config.source_size().0
        || src.Height < config.source_size().1
        || !src.Width.is_multiple_of(2)
        || !src.Height.is_multiple_of(2)
        || dst.Width != config.width
        || dst.Height != config.height
        || dst.MipLevels != 1
        || dst.ArraySize != 1
        || dst.Usage != D3D11_USAGE_DEFAULT
        || dst.BindFlags & required != required
        || [src, dst]
            .iter()
            .any(|d| d.SampleDesc.Count != 1 || d.SampleDesc.Quality != 0)
    {
        return Err(E_INVALIDARG.into());
    }
    Ok(())
}

/// P010 readback bytes (little-endian u16, code << 6) against unshifted 10-bit codes; the
/// 8-bit tolerance of 3 codes scales to 12, and the six padding bits must be zero.
pub(crate) fn compare_self_test_p010(
    actual: &[u8],
    expected: &[u16],
) -> std::result::Result<(), i32> {
    if actual.len() != expected.len() * 2
        || actual
            .as_chunks::<2>()
            .0
            .iter()
            .map(|&b| u16::from_le_bytes(b))
            .zip(expected)
            .any(|(a, &b)| a & 0x3F != 0 || (a >> 6).abs_diff(b) > 12)
    {
        Err(BALE_TONEMAP_E_SELFTEST)
    } else {
        Ok(())
    }
}

pub(crate) fn compare_self_test(actual: &[u8], expected: &[u8]) -> std::result::Result<(), i32> {
    if actual.len() != expected.len()
        || actual
            .iter()
            .zip(expected)
            .any(|(&a, &b)| a.abs_diff(b) > 3)
    {
        Err(BALE_TONEMAP_E_SELFTEST)
    } else {
        Ok(())
    }
}

/// Whether apply reads the source in place: it needs a shader-resource bind and must be neither a
/// decoder nor an encoder surface. Everything else is copied into the private scratch texture first.
fn read_in_place(bind_flags: u32) -> bool {
    bind_flags & D3D11_BIND_SHADER_RESOURCE.0 as u32 != 0
        && bind_flags & (D3D11_BIND_DECODER | D3D11_BIND_VIDEO_ENCODER).0 as u32 == 0
}

fn source_views(
    device: &ID3D11Device,
    texture: &ID3D11Texture2D,
    slice: u32,
    eight_bit: bool,
) -> Result<[ID3D11ShaderResourceView; 2]> {
    let view = |format| -> Result<ID3D11ShaderResourceView> {
        let desc = D3D11_SHADER_RESOURCE_VIEW_DESC {
            Format: format,
            ViewDimension: D3D_SRV_DIMENSION_TEXTURE2DARRAY,
            Anonymous: D3D11_SHADER_RESOURCE_VIEW_DESC_0 {
                Texture2DArray: D3D11_TEX2D_ARRAY_SRV {
                    MostDetailedMip: 0,
                    MipLevels: 1,
                    FirstArraySlice: slice,
                    ArraySize: 1,
                },
            },
        };
        let mut result = None;
        // SAFETY: validated P010/NV12 SRV texture and array slice; format selects D3D11.1's plane.
        unsafe { device.CreateShaderResourceView(texture, Some(&desc), Some(&mut result))? };
        result.ok_or_else(|| E_FAIL.into())
    };
    if eight_bit {
        Ok([view(DXGI_FORMAT_R8_UINT)?, view(DXGI_FORMAT_R8G8_UINT)?])
    } else {
        Ok([view(DXGI_FORMAT_R16_UINT)?, view(DXGI_FORMAT_R16G16_UINT)?])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn decoder_arrays_accept_each_slice_and_reject_out_of_bounds_before_commands() {
        let config = BaleToneMapConfig {
            width: 12,
            height: 6,
            curve: 1,
            source_peak_nits: 1000.0,
            target_white_nits: 100.0,
            ..Default::default()
        };
        let mut src = texture_desc(16, 8, DXGI_FORMAT_P010, D3D11_BIND_DECODER.0 as u32);
        src.ArraySize = 16;
        let dst = texture_desc(
            12,
            6,
            DXGI_FORMAT_NV12,
            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
        );
        for slice in 0..16 {
            assert!(validate_descs(&config, &src, &dst, slice).is_ok());
        }
        assert_eq!(
            validate_descs(&config, &src, &dst, 16).unwrap_err().code(),
            E_INVALIDARG
        );
        assert_eq!(
            validate_descs(&config, &src, &dst, u32::MAX)
                .unwrap_err()
                .code(),
            E_INVALIDARG
        );
        for bind in [
            0,
            D3D11_BIND_DECODER.0 as u32,
            D3D11_BIND_SHADER_RESOURCE.0 as u32,
        ] {
            src.BindFlags = bind;
            assert!(validate_descs(&config, &src, &dst, 15).is_ok());
        }
    }

    #[test]
    fn only_shader_readable_non_decoder_sources_are_read_in_place() {
        let srv = D3D11_BIND_SHADER_RESOURCE.0 as u32;
        let decoder = D3D11_BIND_DECODER.0 as u32;
        let encoder = D3D11_BIND_VIDEO_ENCODER.0 as u32;
        let writable = (D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS).0 as u32;
        assert!(read_in_place(srv));
        assert!(read_in_place(srv | writable));
        // Decoder arrays are always copied first, with or without a shader-resource bind.
        assert!(!read_in_place(decoder | srv));
        assert!(!read_in_place(decoder));
        assert!(!read_in_place(0));
        assert!(!read_in_place(writable));
        assert!(!read_in_place(encoder | srv));
    }

    #[test]
    fn self_test_mismatch_has_its_own_hresult() {
        assert!(compare_self_test(&[16, 128, 235], &[19, 128, 232]).is_ok());
        assert_eq!(
            compare_self_test(&[16, 128, 235], &[20, 128, 235]),
            Err(BALE_TONEMAP_E_SELFTEST)
        );
        assert_eq!(compare_self_test(&[], &[16]), Err(BALE_TONEMAP_E_SELFTEST));
    }
}

/// Debug-only switches for the opens-to-failure investigation (2026-11-17), read once per create
/// from the process environment variable BALE_TONEMAP_DEBUG: `noselftest` skips the create-time
/// self-test; `nostate` replaces the ID3DDeviceContextState swap with an explicit save/restore;
/// `nostaged` (2026-11-22, GPU cost A/B) skips the downscale intermediate and evaluates the area
/// sum in each plane pass as before; `gammachroma` (2026-11-22, quality/cost A/B) restores the
/// pre-2026-11-22 math: per-source-pixel clip, left input siting, output chroma filtered from
/// encoded Cb/Cr ([`cpu::Method::Legacy`]).
#[derive(Clone, Copy, Default)]
struct DebugSwitches {
    no_self_test: bool,
    no_state: bool,
    no_staged: bool,
    gamma_chroma: bool,
}

impl DebugSwitches {
    fn read() -> Self {
        let value = std::env::var("BALE_TONEMAP_DEBUG").unwrap_or_default();
        Self::parse(&value)
    }

    fn parse(value: &str) -> Self {
        let mut switches = Self::default();
        for word in value.split([',', ' ']).filter(|w| !w.is_empty()) {
            match word {
                "noselftest" => switches.no_self_test = true,
                "nostate" => switches.no_state = true,
                "nostaged" => switches.no_staged = true,
                "gammachroma" => switches.gamma_chroma = true,
                _ => {}
            }
        }
        switches
    }
}

enum Guard {
    Swapped(#[allow(dead_code)] ContextGuard),
    Saved(#[allow(dead_code)] Box<SavedBindings>),
}

/// Holds the multithread lock and every binding the tone-map draw changes, and puts them back on
/// drop, so the caller's (MF's) state survives without a context-state swap or ClearState.
struct SavedBindings {
    context: ID3D11DeviceContext1,
    multithread: ID3D10Multithread,
    topology: windows::Win32::Graphics::Direct3D::D3D_PRIMITIVE_TOPOLOGY,
    layout: Option<ID3D11InputLayout>,
    vs: Option<ID3D11VertexShader>,
    ps: Option<ID3D11PixelShader>,
    gs: Option<ID3D11GeometryShader>,
    hs: Option<ID3D11HullShader>,
    ds: Option<ID3D11DomainShader>,
    ps_constants: [Option<ID3D11Buffer>; 1],
    ps_views: [Option<ID3D11ShaderResourceView>; 3],
    rasterizer: Option<ID3D11RasterizerState>,
    viewport_count: u32,
    viewports: [D3D11_VIEWPORT; D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE as usize],
    targets: [Option<ID3D11RenderTargetView>; D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT as usize],
    depth: Option<ID3D11DepthStencilView>,
    blend: Option<ID3D11BlendState>,
    blend_factor: [f32; 4],
    sample_mask: u32,
    depth_state: Option<ID3D11DepthStencilState>,
    stencil_ref: u32,
}

impl SavedBindings {
    fn enter(context: &ID3D11DeviceContext1, multithread: &ID3D10Multithread) -> Self {
        // SAFETY: live interfaces; the lock is taken before any read and released in Drop.
        unsafe {
            multithread.Enter();
            let mut saved = Self {
                context: context.clone(),
                multithread: multithread.clone(),
                topology: context.IAGetPrimitiveTopology(),
                layout: context.IAGetInputLayout().ok(),
                vs: None,
                ps: None,
                gs: None,
                hs: None,
                ds: None,
                ps_constants: Default::default(),
                ps_views: Default::default(),
                rasterizer: context.RSGetState().ok(),
                viewport_count: D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE,
                viewports: Default::default(),
                targets: Default::default(),
                depth: None,
                blend: None,
                blend_factor: [0.0; 4],
                sample_mask: 0,
                depth_state: None,
                stencil_ref: 0,
            };
            context.VSGetShader(&mut saved.vs, None, None);
            context.PSGetShader(&mut saved.ps, None, None);
            context.GSGetShader(&mut saved.gs, None, None);
            context.HSGetShader(&mut saved.hs, None, None);
            context.DSGetShader(&mut saved.ds, None, None);
            context.PSGetConstantBuffers(0, Some(&mut saved.ps_constants));
            context.PSGetShaderResources(0, Some(&mut saved.ps_views));
            context.RSGetViewports(
                &mut saved.viewport_count,
                Some(saved.viewports.as_mut_ptr()),
            );
            context.OMGetRenderTargets(Some(&mut saved.targets), Some(&mut saved.depth));
            context.OMGetBlendState(
                Some(&mut saved.blend),
                Some(&mut saved.blend_factor),
                Some(&mut saved.sample_mask),
            );
            context
                .OMGetDepthStencilState(Some(&mut saved.depth_state), Some(&mut saved.stencil_ref));
            // Neutral fixed-function state for a full-screen triangle; the draw sets the rest.
            context.IASetInputLayout(None);
            context.GSSetShader(None, None);
            context.HSSetShader(None, None);
            context.DSSetShader(None, None);
            context.RSSetState(None);
            context.OMSetBlendState(None, None, u32::MAX);
            context.OMSetDepthStencilState(None, 0);
            saved
        }
    }
}

impl Drop for SavedBindings {
    fn drop(&mut self) {
        let c = &self.context;
        // SAFETY: restores the bindings captured in enter, still under the lock. No allocation,
        // no fallible calls and no Flush, including during unwinding.
        unsafe {
            c.PSSetShaderResources(0, Some(&[None, None, None]));
            c.OMSetRenderTargets(Some(&self.targets), self.depth.as_ref());
            c.IASetPrimitiveTopology(self.topology);
            c.IASetInputLayout(self.layout.as_ref());
            c.VSSetShader(self.vs.as_ref(), None);
            c.PSSetShader(self.ps.as_ref(), None);
            c.GSSetShader(self.gs.as_ref(), None);
            c.HSSetShader(self.hs.as_ref(), None);
            c.DSSetShader(self.ds.as_ref(), None);
            c.PSSetConstantBuffers(0, Some(&self.ps_constants));
            c.PSSetShaderResources(0, Some(&self.ps_views));
            c.RSSetState(self.rasterizer.as_ref());
            let count = (self.viewport_count as usize).min(self.viewports.len());
            c.RSSetViewports(Some(&self.viewports[..count]));
            c.OMSetBlendState(
                self.blend.as_ref(),
                Some(&self.blend_factor),
                self.sample_mask,
            );
            c.OMSetDepthStencilState(self.depth_state.as_ref(), self.stencil_ref);
            self.multithread.Leave();
        }
    }
}

#[cfg(test)]
mod debug_switch_tests {
    use super::DebugSwitches;

    #[test]
    fn parses_known_words_only() {
        let both = DebugSwitches::parse("noselftest,nostate");
        assert!(both.no_self_test && both.no_state);
        let none = DebugSwitches::parse("");
        assert!(!none.no_self_test && !none.no_state);
        let one = DebugSwitches::parse("nostate other");
        assert!(!one.no_self_test && one.no_state && !one.no_staged);
        assert!(DebugSwitches::parse("noselftest,nostaged").no_staged);
        assert!(!one.gamma_chroma);
        let ab = DebugSwitches::parse("gammachroma,nostaged");
        assert!(ab.gamma_chroma && ab.no_staged && !ab.no_state);
    }
}
