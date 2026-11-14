//! Compile the public header as C and C++, checking it against Rust's actual layout.
//! Windows additionally checks the Rust layout at compile time; Linux CI has cc/c++.
#![cfg(not(windows))]
use bale_gpu::tonemap::types::*;
use std::mem::{align_of, offset_of, size_of};
use std::process::Command;

#[test]
fn c_and_cpp_header_match_rust() {
    let mut source = String::from("#include <stddef.h>\n#include \"bale_tonemap.h\"\n");
    macro_rules! record {
        ($ty:ty, $($field:ident),+ $(,)?) => {{
            source.push_str(&format!("CHECK(sizeof({}) == {});\nCHECK(ALIGN({}) == {});\n",
                stringify!($ty), size_of::<$ty>(), stringify!($ty), align_of::<$ty>()));
            $(source.push_str(&format!("CHECK(offsetof({}, {}) == {});\n",
                stringify!($ty), stringify!($field), offset_of!($ty, $field)));)+
        }};
    }
    record!(
        BaleToneMapConfig,
        width,
        height,
        curve,
        source_peak_nits,
        target_white_nits,
        desat,
        reserved
    );
    record!(
        BaleToneMapFrameInfo,
        transfer,
        primaries,
        matrix,
        full_range,
        chroma_location,
        has_mastering,
        has_content_light,
        reserved0,
        mastering_primaries,
        mastering_white,
        mastering_min_nits,
        mastering_max_nits,
        max_cll,
        max_fall,
        reserved
    );
    source.push_str(&format!(
        "CHECK(BALE_TONEMAP_E_SELFTEST == (HRESULT){});\n",
        BALE_TONEMAP_E_SELFTEST
    ));
    let dir = std::env::temp_dir().join(format!("bale-tonemap-header-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    for (compiler, extension, standard, prefix) in [
        (
            "cc",
            "c",
            "-std=c11",
            "#define CHECK(x) _Static_assert(x, #x)\n#define ALIGN(x) _Alignof(x)\n",
        ),
        (
            "c++",
            "cpp",
            "-std=c++11",
            "#define CHECK(x) static_assert(x, #x)\n#define ALIGN(x) alignof(x)\n",
        ),
    ] {
        let path = dir.join(format!("layout.{extension}"));
        std::fs::write(&path, format!("{prefix}{source}")).unwrap();
        let result = Command::new(compiler)
            .args([standard, "-Werror", "-fsyntax-only", "-I"])
            .arg(concat!(env!("CARGO_MANIFEST_DIR"), "/include"))
            .arg(&path)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "header layout: {}",
            String::from_utf8_lossy(&result.stderr)
        );
    }
    std::fs::remove_dir_all(dir).unwrap();
}
