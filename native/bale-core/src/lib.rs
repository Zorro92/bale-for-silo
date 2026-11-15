//! Bale's platform-independent playback logic, ported from `src/Bale.Core` (docs/rust-port.md).
//!
//! The C# side keeps its public API as thin wrappers over one generic call: a function name and a
//! JSON argument in, a JSON result out ([`abi`]). These calls run once per playback decision, not per
//! frame, so JSON costs nothing that matters and keeps the ABI two functions wide.
//!
//! Nothing here logs. Error messages never carry argument values: they could hold URLs, tokens or
//! media titles.

pub mod abi;
pub mod display;
pub mod dolby_vision7;
pub mod dotnet;
pub mod evidence;
pub mod hardware;
pub mod models;
pub mod route;
pub mod software_decode;
pub mod tone_map;
