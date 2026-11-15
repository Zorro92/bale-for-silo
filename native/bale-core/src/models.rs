//! The JSON shapes the C# side sends. Server models (plan, file versions) arrive in the server's
//! own wire format, written by Kiota's JSON serializer; Bale's records arrive snake_case. Only the
//! fields the ported logic reads are declared; serde ignores the rest. Never log these: plans carry
//! stream URLs and headers, and file versions carry server paths.

use serde::{Deserialize, Serialize};

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct PlaybackPlan {
    pub delivery: Option<String>,
    pub decision_reason: Option<String>,
    pub effective_media_file_id: Option<String>,
    pub source: Option<PlaybackSource>,
    pub effective_recipe: Option<EffectiveRecipe>,
    pub stream: Option<StreamInfo>,
    pub selected_tracks: Option<SelectedTracks>,
    pub claims: Option<ValidationClaims>,
    pub transformations: Option<Vec<Transformation>>,
    pub subtitle: Option<SubtitleDecision>,
    pub degradation_warnings: Option<Vec<DegradationWarning>>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct SubtitleDecision {
    pub mode: Option<String>,
    pub embedded: Option<EmbeddedSubtitle>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct EmbeddedSubtitle {
    pub stream_index: Option<i64>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct DegradationWarning {
    pub code: Option<String>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct PlaybackSource {
    pub media_file_id: Option<String>,
    pub container: Option<String>,
    pub video_codec: Option<String>,
    pub audio_codec: Option<String>,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub bit_depth: Option<i64>,
    pub dynamic_range: Option<String>,
    pub dolby_vision_profile: Option<i64>,
    pub dv_bl_compat_id: Option<i64>,
    pub hdr10_plus: Option<bool>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct EffectiveRecipe {
    pub video_codec: Option<String>,
    pub audio_codec: Option<String>,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub dynamic_range: Option<String>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct StreamInfo {
    pub protocol: Option<String>,
    pub container: Option<String>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct SelectedTracks {
    pub audio: Option<TrackIdentity>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct TrackIdentity {
    pub index: Option<i64>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct ValidationClaims {
    pub video: Option<VideoClaims>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct VideoClaims {
    pub hdr10: Option<bool>,
    pub hdr10_plus: Option<bool>,
    pub hlg: Option<bool>,
    pub dolby_vision: Option<bool>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct Transformation {
    pub name: Option<String>,
    pub executor: Option<String>,
    pub recipe_version: Option<String>,
    pub validated_claims: Option<Vec<String>>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct FileVersion {
    pub file_id: Option<String>,
    pub codec_video: Option<String>,
    pub resolution: Option<String>,
    pub bitrate: Option<i64>,
    pub file_size: Option<i64>,
    pub video_tracks: Option<Vec<VideoTrack>>,
}

#[derive(Debug, Default, Clone, Deserialize)]
#[serde(default)]
pub struct VideoTrack {
    pub codec: Option<String>,
    pub height: Option<i64>,
    pub width: Option<i64>,
    pub bitrate: Option<i64>,
    pub frame_rate: Option<String>,
    pub profile: Option<String>,
    pub dolby_vision: Option<String>,
    pub video_range: Option<String>,
    pub video_range_type: Option<String>,
    pub bit_depth: Option<i64>,
    pub pixel_format: Option<String>,
    pub color_transfer: Option<String>,
    pub color_primaries: Option<String>,
    pub color_space: Option<String>,
    pub dv_profile: Option<i64>,
    pub dv_bl_present: Option<bool>,
    pub dv_config_present: Option<bool>,
    pub dv_el_present: Option<bool>,
    pub dv_bl_compat_id: Option<i64>,
    pub dv_bl_compat_id_present: Option<bool>,
}

/// Bale's `DeviceCapabilities` record, the fields the ported logic reads. Bale's own records have
/// no serde defaults: a field the C# side names differently is a loud error, not a silent false.
#[derive(Debug, Default, Clone, Deserialize)]
pub struct DeviceCapabilities {
    pub video_codecs: Vec<String>,
    pub video_decode: Vec<DecodeClaim>,
    pub audio_decode_codecs: Vec<String>,
    pub containers: Vec<String>,
    pub hdr: HdrCapabilities,
    pub display_hdr_evidence: String,
    pub original_http_supported: bool,
    pub original_dolby_vision7_base_layer_supported: bool,
    pub original_dynamic_range_mapping_supported: bool,
}

/// Bale's `VideoDecodeCapability`, complete (C# always writes every field).
#[derive(Debug, Default, Clone, Deserialize)]
pub struct DecodeClaim {
    pub codec: String,
    pub profiles: Vec<String>,
    pub bit_depths: Vec<i32>,
    pub max_width: Option<i32>,
    pub max_height: Option<i32>,
    #[serde(deserialize_with = "crate::dotnet::optional_double")]
    pub max_frame_rate: Option<f64>,
    pub max_bitrate_kbps: Option<i32>,
    pub hardware: bool,
}

/// The two fields of a `VideoDecodeCapability` the capability filters read.
#[derive(Debug, Default, Clone, Deserialize)]
pub struct VideoDecode {
    pub codec: String,
    pub hardware: bool,
}

#[derive(Debug, Default, Clone, PartialEq, Deserialize, Serialize)]
pub struct HdrCapabilities {
    pub hdr10: bool,
    pub hdr10_plus: bool,
    pub hlg: bool,
    pub dolby_vision_profiles: Vec<i32>,
}

/// Bale's `DisplayModeFacts`. Equality is the C# record's: doubles compare with `Equals`, so NaN
/// equals NaN.
#[derive(Debug, Default, Clone, Copy, Deserialize)]
pub struct DisplayMode {
    pub smpte2084: bool,
    pub metadata2086: bool,
    pub stereo: bool,
    pub width: u32,
    pub height: u32,
    #[serde(deserialize_with = "crate::dotnet::double")]
    pub refresh_rate: f64,
}

impl PartialEq for DisplayMode {
    fn eq(&self, other: &Self) -> bool {
        self.smpte2084 == other.smpte2084
            && self.metadata2086 == other.metadata2086
            && self.stereo == other.stereo
            && self.width == other.width
            && self.height == other.height
            && (self.refresh_rate == other.refresh_rate
                || self.refresh_rate.is_nan() && other.refresh_rate.is_nan())
    }
}

/// A result the C# wrapper turns into `InvalidDataException(message)`.
#[derive(Debug, Serialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum Checked<T> {
    Ok(T),
    InvalidData(String),
}

impl<T> From<Result<T, &str>> for Checked<T> {
    fn from(result: Result<T, &str>) -> Self {
        match result {
            Ok(value) => Checked::Ok(value),
            Err(message) => Checked::InvalidData(message.to_owned()),
        }
    }
}
