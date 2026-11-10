use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

const SHADERS: [(&str, &str); 7] = [
    ("fullscreen_vs", "vs_5_0"),
    ("grain_luma_ps", "ps_5_0"),
    ("grain_chroma_ps", "ps_5_0"),
    ("repack_luma_ps", "ps_5_0"),
    ("repack_chroma_ps", "ps_5_0"),
    ("repack_zc_cs", "cs_5_0"),
    ("repack_grain_zc_cs", "cs_5_0"),
];

fn main() {
    println!("cargo::rustc-check-cfg=cfg(bale_grain_no_shaders)");
    println!("cargo:rerun-if-env-changed=FXC");
    println!("cargo:rerun-if-changed=shaders/grain_common.hlsli");
    println!("cargo:rerun-if-changed=shaders/repack_common.hlsli");
    for (name, _) in SHADERS {
        println!("cargo:rerun-if-changed=shaders/{name}.hlsl");
    }
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("Cargo sets OUT_DIR"));
    if !cfg!(windows) {
        // Cross-check Rust types/lints only. A DLL built this way cannot create a grain handle.
        println!("cargo:rustc-cfg=bale_grain_no_shaders");
        for (name, _) in SHADERS {
            fs::write(out.join(format!("{name}.cso")), []).expect("write shader placeholder");
        }
        return;
    }
    let fxc = env::var_os("FXC")
        .map(PathBuf::from)
        .unwrap_or_else(find_fxc);
    let shaders = Path::new("shaders");
    for (name, profile) in SHADERS {
        let blob = out.join(format!("{name}.cso"));
        let result = Command::new(&fxc)
            .args(["/nologo", "/O3", "/WX", "/Qstrip_debug", "/Qstrip_reflect"])
            .args(["/T", profile, "/E", "main", "/I"])
            .arg(shaders)
            .arg("/Fo")
            .arg(&blob)
            .arg(shaders.join(format!("{name}.hlsl")))
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
