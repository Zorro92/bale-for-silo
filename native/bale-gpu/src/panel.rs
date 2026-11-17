//! XAML `SwapChainPanel` interop (windows.ui.xaml.media.dxinterop.h); the `windows` crate no longer
//! ships these interfaces. Call them on the UI thread.
#![allow(non_snake_case)]
use windows_core::{HRESULT, IUnknown, IUnknown_Vtbl, Interface, interface};

#[interface("F92F19D2-3ADE-45A6-A20C-F6F1EA90554B")]
pub unsafe trait ISwapChainPanelNative: IUnknown {
    pub unsafe fn SetSwapChain(&self, swap_chain: *mut core::ffi::c_void) -> HRESULT;
}

/// Adds a swap chain *handle* (composition surface, such as IMFMediaEngine's windowless swap chain).
#[interface("D5A2F60C-37B2-44A2-937B-8D8EB9726821")]
pub unsafe trait ISwapChainPanelNative2: ISwapChainPanelNative {
    pub unsafe fn SetSwapChainHandle(&self, handle: *mut core::ffi::c_void) -> HRESULT;
}

/// Sets `handle` on `panel` (any interface of a SwapChainPanel); returns the HRESULT.
///
/// # Safety
/// `panel` must be a live COM pointer for the call; call on the panel's UI thread.
pub unsafe fn set_swap_chain_handle(panel: *mut core::ffi::c_void, handle: isize) -> HRESULT {
    // SAFETY: the caller guarantees a live COM pointer.
    let Some(panel) = (unsafe { IUnknown::from_raw_borrowed(&panel) }) else {
        return HRESULT(0x8000_4003u32 as i32); // E_POINTER
    };
    match panel.cast::<ISwapChainPanelNative2>() {
        // SAFETY: a live interface; the panel takes its own reference to the surface.
        Ok(native) => unsafe { native.SetSwapChainHandle(handle as *mut core::ffi::c_void) },
        Err(error) => error.code(),
    }
}
