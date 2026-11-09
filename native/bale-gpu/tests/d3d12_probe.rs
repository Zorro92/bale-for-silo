//! Debug D3D12 probe: reports a missing d3d12.dll, and on a real one reports every line without
//! panicking (CI runners may have no hardware adapter, so the device line may say FAILS).
#![cfg(windows)]

use bale_gpu::d3d12;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::System::LibraryLoader::LoadLibraryW;
use windows::core::w;

#[test]
fn missing_dll_is_reported() {
    // SAFETY: a null module is the documented "not loadable" input.
    let report = unsafe { d3d12::run(HMODULE::default()) };
    assert_eq!(
        report,
        "D3D12 probe:\n  d3d12.dll                not loadable\n"
    );
}

#[test]
fn loaded_dll_reports_without_panicking() {
    // SAFETY: loads a system DLL by name; it stays loaded for the test process.
    let module = unsafe { LoadLibraryW(w!("d3d12.dll")) }.expect("d3d12.dll ships with Windows");
    // SAFETY: `module` was just loaded and is never freed.
    let report = unsafe { d3d12::run(module) };
    println!("{report}");
    assert!(
        report.starts_with("D3D12 probe:\n  hardware device FL11_0   "),
        "{report}"
    );
    if report.contains("hardware device FL11_0   OK") {
        for label in [
            "max feature level",
            "architecture",
            "row-major cross-adapter",
            "existing heaps",
        ] {
            assert!(
                report.contains(&format!("  {label}")),
                "missing {label}: {report}"
            );
        }
    }
}
