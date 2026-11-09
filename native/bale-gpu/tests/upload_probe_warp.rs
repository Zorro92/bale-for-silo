//! Debug upload probe on WARP at a small size: every variant must run and report, none may fail.
#![cfg(windows)]

use bale_gpu::upload;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D11::*;

#[test]
fn every_variant_reports_on_warp() {
    let mut device = None;
    let mut context = None;
    // SAFETY: no adapter/software module; out-pointers are live local COM slots.
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_WARP,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
        .expect("WARP FL11_0 is required; the upload probe test must not skip");
    }
    let report = upload::run(&device.unwrap(), &context.unwrap(), 256, 96, 2);
    println!("{report}");
    assert!(report.starts_with("Upload probe: 256x96 R16"));
    for label in [
        "compute 1T",
        "compute ",
        "cache copy 1T",
        "RAM copy 1T",
        "RAM copy ",
        "staging write 1T",
        "staging write ",
        "staging write 6T",
        "staging write stream",
        "staging P010 write",
        "staging read+write",
        "dynamic discard ",
        "dynamic discard stream",
        "UpdateSubresource 1T",
        "read staging write-only",
        "read staging read+write",
    ] {
        assert!(report.contains(&format!("  {label}")), "missing {label}");
    }
    assert_eq!(report.lines().count(), 17);
    assert!(!report.contains("FAILS"), "{report}");
    assert!(
        !report.contains("NaN") && !report.contains("inf"),
        "{report}"
    );
}
