//! Bale's platform-independent playback logic, ported from `src/Bale.Core` (docs/rust-port.md).
//!
//! The C# side keeps its public API as thin wrappers over one generic call: a function name and a
//! JSON argument in, a JSON result out ([`abi`]). These calls run once per playback decision, not per
//! frame, so JSON costs nothing that matters and keeps the ABI two functions wide.
//!
//! Nothing here logs. Error messages never carry argument values: they could hold URLs, tokens or
//! media titles.

pub mod abi;
pub mod abi_buffering;
pub mod abi_meters;
pub mod abi_progressive;
pub mod abi_stream;
pub mod audio_selection;
pub mod auth_client;
pub mod buffering;
pub mod display;
pub mod dolby_vision7;
pub mod dotnet;
pub mod evidence;
pub mod formatting;
pub mod frames;
pub mod hardware;
pub mod hls;
pub mod http;
pub mod http_probe;
pub mod language;
pub mod meters;
pub mod models;
pub mod progressive_stream;
pub mod range_stream;
pub mod reanchor;
pub mod route;
pub mod software_decode;
pub mod subtitles;
pub mod tone_map;
pub mod version_notice;
pub mod winrt_stream;
