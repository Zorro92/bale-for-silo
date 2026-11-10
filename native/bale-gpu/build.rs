use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

/// (output name, source name, profile, preprocessor define). The zero-copy compute shaders are
/// built a second time with `BALE_8BIT` for 8-bit pictures; their 10-bit builds take no define.
const SHADERS: [(&str, &str, &str, Option<&str>); 9] = [
    ("fullscreen_vs", "fullscreen_vs", "vs_5_0", None),
    ("grain_luma_ps", "grain_luma_ps", "ps_5_0", None),
    ("grain_chroma_ps", "grain_chroma_ps", "ps_5_0", None),
    ("repack_zc_cs", "repack_zc_cs", "cs_5_0", None),
    (
        "repack_zc8_cs",
        "repack_zc_cs",
        "cs_5_0",
        Some("BALE_8BIT=1"),
    ),
    ("repack_grain_zc_cs", "repack_grain_zc_cs", "cs_5_0", None),
    (
        "repack_grain_zc8_cs",
        "repack_grain_zc_cs",
        "cs_5_0",
        Some("BALE_8BIT=1"),
    ),
    // Subtitle overlay (`subs::overlay`).
    ("sub_vs", "sub_vs", "vs_5_0", None),
    ("sub_ps", "sub_ps", "ps_5_0", None),
];

fn main() {
    println!("cargo::rustc-check-cfg=cfg(bale_grain_no_shaders)");
    println!("cargo:rerun-if-env-changed=FXC");
    println!("cargo:rerun-if-env-changed=BALE_LIBASS_DIR");
    println!("cargo:rerun-if-changed=shaders/grain_common.hlsli");
    for (_, source, _, _) in SHADERS {
        println!("cargo:rerun-if-changed=shaders/{source}.hlsl");
    }
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    link_libass();
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("Cargo sets OUT_DIR"));
    if !cfg!(windows) {
        // Cross-check Rust types/lints only. A DLL built this way cannot create a grain handle.
        println!("cargo:rustc-cfg=bale_grain_no_shaders");
        for (name, _, _, _) in SHADERS {
            fs::write(out.join(format!("{name}.cso")), []).expect("write shader placeholder");
        }
        return;
    }
    let fxc = env::var_os("FXC")
        .map(PathBuf::from)
        .unwrap_or_else(find_fxc);
    let shaders = Path::new("shaders");
    for (name, source, profile, define) in SHADERS {
        let blob = out.join(format!("{name}.cso"));
        let result = Command::new(&fxc)
            .args(["/nologo", "/O3", "/WX", "/Qstrip_debug", "/Qstrip_reflect"])
            .args(["/T", profile, "/E", "main"])
            .args(define.into_iter().flat_map(|define| ["/D", define]))
            .arg("/I")
            .arg(shaders)
            .arg("/Fo")
            .arg(&blob)
            .arg(shaders.join(format!("{source}.hlsl")))
            .output()
            .expect("run fxc (install the Windows SDK or set FXC to fxc.exe)");
        assert!(
            result.status.success(),
            "fxc failed for {name}:\n{}\n{}",
            String::from_utf8_lossy(&result.stdout),
            String::from_utf8_lossy(&result.stderr),
        );
        assert!(
            fs::metadata(blob).expect("fxc must write its output").len() > 0,
            "fxc produced an empty shader for {name}",
        );
    }
}

/// Links the static libass build from `scripts/build-libass-native.ps1` (subtitle rendering). A
/// Windows host must have it; cross-checking from another host only type-checks, so it is optional.
fn link_libass() {
    let Some(dir) = env::var_os("BALE_LIBASS_DIR") else {
        if cfg!(windows) {
            panic!(
                "BALE_LIBASS_DIR is not set; run scripts/build-libass-native.ps1 and point it at the output"
            );
        }
        return;
    };
    let lib = PathBuf::from(dir).join("lib");
    println!("cargo:rustc-link-search=native={}", lib.display());
    for name in ["ass", "harfbuzz", "fribidi", "freetype"] {
        let file = lib.join(format!("{name}.lib"));
        assert!(file.is_file(), "missing {}", file.display());
        println!("cargo:rerun-if-changed={}", file.display());
        println!("cargo:rustc-link-lib=static={name}");
    }
    // libass's WinRT font provider calls DWriteCreateFactory directly.
    println!("cargo:rustc-link-lib=dylib=dwrite");
}

fn find_fxc() -> PathBuf {
    // SDK versions are numeric, not lexicographic (10.0.9 sorts before 10.0.10).
    ["ProgramFiles(x86)", "ProgramFiles"]
        .into_iter()
        .filter_map(env::var_os)
        .flat_map(|root| {
            fs::read_dir(PathBuf::from(root).join("Windows Kits/10/bin"))
                .into_iter()
                .flatten()
                .filter_map(Result::ok)
        })
        .filter_map(|entry| {
            let version: Vec<u32> = entry
                .file_name()
                .to_str()?
                .split('.')
                .map(str::parse)
                .collect::<Result<_, _>>()
                .ok()?;
            let path = entry.path().join("x64/fxc.exe");
            path.is_file().then_some((version, path))
        })
        .max_by(|a, b| a.0.cmp(&b.0))
        .map(|(_, path)| path)
        .expect("fxc.exe not found in Windows Kits/10/bin; install the Windows SDK or set FXC")
}
