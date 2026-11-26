//! HDR to SDR tone mapping, independent C ABI 1. Production routing remains server-side.
#[cfg(all(windows, feature = "debug-probes"))]
pub mod churn;
pub mod cpu;
pub mod ffi;
#[cfg(windows)]
pub mod gpu;
mod peak;
pub mod types;

// Debug packages: the stage's GPU time per dither on a private device (`tonemap-bench`).
#[cfg(all(windows, feature = "stage-timing"))]
mod bench;
#[cfg(any(feature = "stage-timing", test))]
mod timing;
#[cfg(all(windows, feature = "stage-timing"))]
mod timing_gpu;
