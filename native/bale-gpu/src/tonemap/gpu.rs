//! Two D3D11.1 plane RTV passes. All fallible preparation precedes caller-output writes.
//! The C owner serializes calls and holds the MF device's multithread protection.
use super::{cpu, types::*};
use crate::grain::gpu::{
    ContextGuard, readback_bytes, render_view, same_device, texture, texture_desc,
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

#[repr(C)]
struct Constants {
    geometry: [u32; 4],
    flags: [u32; 4],
    params: [f32; 4],
}
struct Scratch {
    width: u32,
    height: u32,
    texture: ID3D11Texture2D,
    views: [ID3D11ShaderResourceView; 2],
}

/// Exclusive serialized C-ABI owner; retains the device, never the caller's textures.
pub struct ToneMapGpu {
    device: ID3D11Device1,
    config: BaleToneMapConfig,
    state: ID3DDeviceContextState,
    vs: ID3D11VertexShader,
    y_ps: ID3D11PixelShader,
    uv_ps: ID3D11PixelShader,
    constants: ID3D11Buffer,
    scratch: Option<Scratch>,
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
        let mut state = None;
        let mut vs = None;
        let mut y_ps = None;
        let mut uv_ps = None;
        let mut constants = None;
        // SAFETY: valid bytecode/initialized descriptor, local output slots, retained device.
        unsafe {
            device.CreateDeviceContextState(
                0,
                &[device.GetFeatureLevel()],
                D3D11_SDK_VERSION,
                &ID3D11Device::IID,
                None,
                Some(&mut state),
            )?;
            device.CreateVertexShader(VS, None, Some(&mut vs))?;
            device.CreatePixelShader(Y_PS, None, Some(&mut y_ps))?;
            device.CreatePixelShader(UV_PS, None, Some(&mut uv_ps))?;
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
            config,
            state: state.ok_or(E_FAIL)?,
            vs: vs.ok_or(E_FAIL)?,
            y_ps: y_ps.ok_or(E_FAIL)?,
            uv_ps: uv_ps.ok_or(E_FAIL)?,
            constants: constants.ok_or(E_FAIL)?,
            scratch: None,
            _not_send_sync: PhantomData,
        };
        gpu.self_test(&context)?;
        // Do not keep the small self-test scratch; first decoder input chooses the cache size.
        gpu.scratch = None;
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
        let targets = [
            render_view(&self.device, output, DXGI_FORMAT_R8_UNORM)?,
            render_view(&self.device, output, DXGI_FORMAT_R8G8_UNORM)?,
        ];
        // D3D11 forbids SRVs on decoder/encoder arrays even with an SRV bind present.
        let direct = src.BindFlags & D3D11_BIND_SHADER_RESOURCE.0 as u32 != 0
            && src.BindFlags & (D3D11_BIND_DECODER | D3D11_BIND_VIDEO_ENCODER).0 as u32 == 0;
        let views = if direct {
            source_views(&self.device, input, subresource)?
        } else {
            if self
                .scratch
                .as_ref()
                .is_none_or(|s| s.width != src.Width || s.height != src.Height)
            {
                let texture = texture(
                    &self.device,
                    &texture_desc(
                        src.Width,
                        src.Height,
                        DXGI_FORMAT_P010,
                        D3D11_BIND_SHADER_RESOURCE.0 as u32,
                    ),
                    None,
                )?;
                let views = source_views(&self.device, &texture, 0)?;
                self.scratch = Some(Scratch {
                    width: src.Width,
                    height: src.Height,
                    texture,
                    views,
                });
            }
            self.scratch.as_ref().ok_or(E_FAIL)?.views.clone()
        };
        let constants = Constants {
            geometry: [config.width, config.height, src.Width, src.Height],
            flags: [
                config.curve,
                u32::from(frame.matrix == 9),
                u32::from(frame.full_range),
                0,
            ],
            params: [
                config.source_peak_nits,
                config.target_white_nits,
                config.desat,
                0.0,
            ],
        };
        // Fail before queueing work, never return an error after changing the caller's output.
        // D3D11 Draw/Copy/Update queue void commands; asynchronous loss belongs to the caller.
        unsafe { self.device.GetDeviceRemovedReason()? };
        let _guard = ContextGuard::enter(&context, &multithread, &self.state);
        // SAFETY: descriptors, devices, slice, dimensions and binds checked above. Private state
        // isolates all bindings and is restored even if Rust unwinds. No flush in apply.
        unsafe {
            context.ClearState();
            if !direct {
                context.CopySubresourceRegion(
                    &self.scratch.as_ref().ok_or(E_FAIL)?.texture,
                    0,
                    0,
                    0,
                    0,
                    input,
                    subresource,
                    None,
                );
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
            for (plane, shader) in [&self.y_ps, &self.uv_ps].into_iter().enumerate() {
                context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                    Width: (config.width >> plane) as f32,
                    Height: (config.height >> plane) as f32,
                    MinDepth: 0.0,
                    MaxDepth: 1.0,
                    ..Default::default()
                }]));
                context.OMSetRenderTargets(Some(&[Some(targets[plane].clone())]), None);
                context.PSSetShader(shader, None);
                context.Draw(3, 0);
            }
        }
        Ok(())
    }

    fn self_test(&mut self, context: &ID3D11DeviceContext) -> Result<()> {
        let config = BaleToneMapConfig {
            width: 16,
            height: 8,
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
            let expected = cpu::reference(&config, &frame, &pattern, 16, 8).map_err(HRESULT)?;
            let data = D3D11_SUBRESOURCE_DATA {
                pSysMem: pattern.as_ptr().cast(),
                SysMemPitch: 32,
                SysMemSlicePitch: 0,
            };
            for bind in [0, D3D11_BIND_SHADER_RESOURCE.0 as u32] {
                let input = texture(
                    &self.device,
                    &texture_desc(16, 8, DXGI_FORMAT_P010, bind),
                    Some(&data),
                )?;
                let output = texture(
                    &self.device,
                    &texture_desc(
                        16,
                        8,
                        DXGI_FORMAT_NV12,
                        (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
                    ),
                    None,
                )?;
                self.draw(&config, context, &input, 0, &output, &frame)?;
                let mut read_desc = texture_desc(16, 8, DXGI_FORMAT_NV12, 0);
                read_desc.Usage = D3D11_USAGE_STAGING;
                read_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
                let readback = texture(&self.device, &read_desc, None)?;
                // SAFETY: private matching resources on the caller's protected immediate context.
                unsafe { context.CopyResource(&readback, &output) };
                let actual = readback_bytes(context, &readback, 16, 12)?;
                compare_self_test(&actual, &expected).map_err(HRESULT)?;
            }
        }
        Ok(())
    }
}

fn validate_descs(
    config: &BaleToneMapConfig,
    src: &D3D11_TEXTURE2D_DESC,
    dst: &D3D11_TEXTURE2D_DESC,
    subresource: u32,
) -> Result<()> {
    let required = (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32;
    if src.Format != DXGI_FORMAT_P010
        || dst.Format != DXGI_FORMAT_NV12
        || src.MipLevels != 1
        || subresource >= src.ArraySize
        || src.Width < config.width
        || src.Height < config.height
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

fn source_views(
    device: &ID3D11Device,
    texture: &ID3D11Texture2D,
    slice: u32,
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
        // SAFETY: validated P010 SRV texture and array slice; format selects D3D11.1's plane.
        unsafe { device.CreateShaderResourceView(texture, Some(&desc), Some(&mut result))? };
        result.ok_or_else(|| E_FAIL.into())
    };
    Ok([view(DXGI_FORMAT_R16_UINT)?, view(DXGI_FORMAT_R16G16_UINT)?])
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
    fn self_test_mismatch_has_its_own_hresult() {
        assert!(compare_self_test(&[16, 128, 235], &[19, 128, 232]).is_ok());
        assert_eq!(
            compare_self_test(&[16, 128, 235], &[20, 128, 235]),
            Err(BALE_TONEMAP_E_SELFTEST)
        );
        assert_eq!(compare_self_test(&[], &[16]), Err(BALE_TONEMAP_E_SELFTEST));
    }
}
