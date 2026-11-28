use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

/// (output name, source name, profile, preprocessor defines). The tone-map plane shaders are built
/// again with `BALE_STAGED` for the downscale path, and both of those again with `BALE_DITHER_R2`
/// for rav1g's NV12 threshold (the stage's default since 2026-11-26; the builds without it are the
/// static Bayer ones of debug `bayerdither`).
const SHADERS: [(&str, &str, &str, &[&str]); 15] = [
    ("fullscreen_vs", "fullscreen_vs", "vs_5_0", &[]),
    ("tonemap_luma_ps", "tonemap_luma_ps", "ps_5_0", &[]),
    ("tonemap_chroma_ps", "tonemap_chroma_ps", "ps_5_0", &[]),
    // Downscale: one area pass into an fp32 intermediate, then plane passes that read it.
    ("tonemap_area_ps", "tonemap_area_ps", "ps_5_0", &[]),
    (
        "tonemap_luma_staged_ps",
        "tonemap_luma_ps",
        "ps_5_0",
        &["BALE_STAGED=1"],
    ),
    (
        "tonemap_chroma_staged_ps",
        "tonemap_chroma_ps",
        "ps_5_0",
        &["BALE_STAGED=1"],
    ),
    // The same four plane shaders with rav1g's R2 dither threshold instead of Bayer: the default set.
    (
        "tonemap_luma_r2_ps",
        "tonemap_luma_ps",
        "ps_5_0",
        &["BALE_DITHER_R2=1"],
    ),
    (
        "tonemap_chroma_r2_ps",
        "tonemap_chroma_ps",
        "ps_5_0",
        &["BALE_DITHER_R2=1"],
    ),
    (
        "tonemap_luma_staged_r2_ps",
        "tonemap_luma_ps",
        "ps_5_0",
        &["BALE_STAGED=1", "BALE_DITHER_R2=1"],
    ),
    (
        "tonemap_chroma_staged_r2_ps",
        "tonemap_chroma_ps",
        "ps_5_0",
        &["BALE_STAGED=1", "BALE_DITHER_R2=1"],
    ),
    ("grain_luma_ps", "grain_luma_ps", "ps_5_0", &[]),
    ("grain_chroma_ps", "grain_chroma_ps", "ps_5_0", &[]),
    // Subtitle overlay (`subs::overlay`).
    ("sub_vs", "sub_vs", "vs_5_0", &[]),
    ("sub_ps", "sub_ps", "ps_5_0", &[]),
    ("sub_rgba_ps", "sub_rgba_ps", "ps_5_0", &[]),
];

fn main() {
    println!("cargo::rustc-check-cfg=cfg(bale_grain_no_shaders)");
    println!("cargo:rerun-if-env-changed=FXC");
    println!("cargo:rerun-if-env-changed=BALE_LIBASS_DIR");
    println!("cargo:rerun-if-changed=shaders/grain_common.hlsli");
    println!("cargo:rerun-if-changed=shaders/tonemap_common.hlsli");
    println!("cargo:rerun-if-changed=shaders/tonemap_math.hlsli");
    for (_, source, _, _) in SHADERS {
        println!("cargo:rerun-if-changed=shaders/{source}.hlsl");
    }
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    link_libass();
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("Cargo sets OUT_DIR"));
    // Another host compiles the real shaders only when FXC names a compiler (scripts/check-fxc.sh points it at
    // scripts/fxc-wine.sh, Microsoft's fxc.exe under Wine); the Windows flags below are then the only shader list.
    let fxc = env::var_os("FXC").map(PathBuf::from);
    if !cfg!(windows) && fxc.is_none() {
        // Cross-check Rust types/lints only. A DLL built this way cannot create a grain handle.
        println!("cargo:rustc-cfg=bale_grain_no_shaders");
        for (name, _, _, _) in SHADERS {
            fs::write(out.join(format!("{name}.cso")), []).expect("write shader placeholder");
        }
        return;
    }
    let fxc = fxc.unwrap_or_else(find_fxc);
    let shaders = Path::new("shaders");
    for (name, source, profile, defines) in SHADERS {
        let blob = out.join(format!("{name}.cso"));
        let result = Command::new(&fxc)
            .args(["/nologo", "/O3", "/WX", "/Qstrip_debug", "/Qstrip_reflect"])
            .args(["/T", profile, "/E", "main"])
            .args(defines.iter().flat_map(|define| ["/D", define]))
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
