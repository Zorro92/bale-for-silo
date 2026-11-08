//! Bit-exact AV1 film grain for 4:2:0 NV12/P010 frames (dav1d 1.5.4).
//! Synthesis is safe Rust; only the C ABI boundary dereferences raw pointers.

#![deny(unsafe_code)]

pub mod cpu;
#[allow(unsafe_code)]
pub mod ffi;
mod gaussian;
mod lfsr;
mod offsets;
pub mod params;
mod scaling;
mod template;

pub use params::{BaleGrainParams, InvalidGrainParams, ValidGrainParams};
