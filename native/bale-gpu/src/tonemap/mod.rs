//! HDR to SDR tone mapping, independent C ABI 1. Production routing remains server-side.
#[cfg(windows)]
pub mod churn;
pub mod cpu;
pub mod ffi;
#[cfg(windows)]
pub mod gpu;
mod peak;
pub mod types;

#[cfg(any(feature = "stage-timing", test))]
mod timing;
#[cfg(all(windows, feature = "stage-timing"))]
mod timing_gpu;
