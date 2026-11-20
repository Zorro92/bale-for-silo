use super::texture_height;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{E_FAIL, E_INVALIDARG, E_OUTOFMEMORY, E_POINTER};
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::IDXGIDevice3;
use windows::core::{BOOL, HRESULT, Interface, Result};

pub(super) struct D11 {
    textures: Vec<ID3D11Texture2D>,
    start: ID3D11Query,
    end: ID3D11Query,
    disjoint: ID3D11Query,
    event: ID3D11Query,
    context: ID3D11DeviceContext,
    device: ID3D11Device,
}

fn query(device: &ID3D11Device, kind: D3D11_QUERY) -> Result<ID3D11Query> {
    let mut out = None;
    unsafe {
        device.CreateQuery(
            &D3D11_QUERY_DESC {
                Query: kind,
                MiscFlags: 0,
            },
            Some(&mut out),
        )?
    };
    out.ok_or_else(|| E_POINTER.into())
}

impl D11 {
    pub(super) fn new(device: ID3D11Device) -> Result<Self> {
        Ok(Self {
            textures: Vec::new(),
            start: query(&device, D3D11_QUERY_TIMESTAMP)?,
            end: query(&device, D3D11_QUERY_TIMESTAMP)?,
            disjoint: query(&device, D3D11_QUERY_TIMESTAMP_DISJOINT)?,
            event: query(&device, D3D11_QUERY_EVENT)?,
            context: unsafe { device.GetImmediateContext()? },
            device,
        })
    }

    pub(super) fn allocate(&mut self, bytes: u64) -> Result<()> {
        self.textures.try_reserve(1).map_err(|_| E_OUTOFMEMORY)?;
        let desc = D3D11_TEXTURE2D_DESC {
            Width: 4096,
            Height: texture_height(bytes).ok_or(E_INVALIDARG)?,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_RENDER_TARGET.0 as u32,
            ..Default::default()
        };
        let mut texture = None;
        unsafe {
            self.device
                .CreateTexture2D(&desc, None, Some(&mut texture))?
        };
        self.textures.push(texture.ok_or(E_POINTER)?);
        Ok(())
    }

    /// windows-rs maps both S_OK and S_FALSE to Ok(()); inspect the raw HRESULT so that
    /// an unfinished query can never be mistaken for completed work (MS GetData docs).
    fn data<T>(&self, query: &ID3D11Query, value: &mut T) -> Result<()> {
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            let hr = unsafe {
                (Interface::vtable(&self.context).GetData)(
                    self.context.as_raw(),
                    query.as_raw(),
                    (value as *mut T).cast(),
                    std::mem::size_of::<T>() as u32,
                    D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
                )
            };
            if hr == HRESULT(0) {
                return Ok(());
            }
            hr.ok()?;
            unsafe { self.device.GetDeviceRemovedReason()? };
            if Instant::now() >= deadline {
                return Err(HRESULT(0x8007_0102u32 as i32).into());
            }
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    fn wait(&self) -> Result<()> {
        unsafe {
            self.context.End(&self.event);
            self.context.Flush();
        }
        self.data(&self.event, &mut BOOL(0))
    }

    pub(super) fn touch_last(&self) -> Result<()> {
        let mut view = None;
        unsafe {
            self.device.CreateRenderTargetView(
                self.textures.last().ok_or(E_FAIL)?,
                None,
                Some(&mut view),
            )?;
            self.context
                .ClearRenderTargetView(&view.ok_or(E_POINTER)?, &[0.25, 0.5, 0.75, 1.0]);
        }
        self.wait()
    }

    pub(super) fn copy_ms(&self, baseline: bool) -> Result<f64> {
        // Warm up ONLY the baseline; warming later steps could hide the paging we are measuring.
        if baseline {
            unsafe {
                self.context
                    .CopyResource(&self.textures[1], &self.textures[0])
            };
            self.wait()?;
        }
        unsafe {
            self.context.Begin(&self.disjoint);
            self.context.End(&self.start);
            self.context
                .CopyResource(&self.textures[1], &self.textures[0]);
            self.context.End(&self.end);
            self.context.End(&self.disjoint);
        }
        self.wait()?;
        let (mut start, mut end) = (0u64, 0u64);
        let mut disjoint = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        self.data(&self.start, &mut start)?;
        self.data(&self.end, &mut end)?;
        self.data(&self.disjoint, &mut disjoint)?;
        if disjoint.Disjoint.as_bool() || disjoint.Frequency == 0 || end <= start {
            return Err(E_FAIL.into());
        }
        Ok((end - start) as f64 * 1000.0 / disjoint.Frequency as f64)
    }

    pub(super) fn removed(&self) -> i32 {
        unsafe { self.device.GetDeviceRemovedReason() }
            .err()
            .map_or(0, |e| e.code().0)
    }
}

impl Drop for D11 {
    fn drop(&mut self) {
        // No state bindings survive the worker. Flush/trim after releasing all probe resources.
        unsafe {
            self.context.ClearState();
            self.context.Flush();
        }
        self.textures.clear();
        if let Ok(device) = self.device.cast::<IDXGIDevice3>() {
            unsafe { device.Trim() };
        }
    }
}
