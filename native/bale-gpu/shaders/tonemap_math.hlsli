#ifndef BALE_TONEMAP_MATH_HLSLI
#define BALE_TONEMAP_MATH_HLSLI

// Pure fp32 functions only: usable by both SM5 and SM6 without a graphics API binding.
// Independently expressed from ST 2084, ITU-R BT.2100/BT.2390, BT.709/BT.1886 and
// Hable's published rational curve. Rust mirrors each function in src/tonemap/cpu.rs.
// No wave operations, min-precision types, doubles or API-specific intrinsics.

float pq_eotf(float signal) {
    float p = pow(clamp(signal, 0.0, 1.0), 1.0 / 78.84375);
    return pow(max(p - 0.8359375, 0.0) / (18.851563 - 18.6875 * p), 1.0 / 0.15930176) * 10000.0;
}
float pq_encode(float nits) {
    float p = pow(clamp(nits, 0.0, 10000.0) / 10000.0, 0.15930176);
    return pow((0.8359375 + 18.851563 * p) / (1.0 + 18.6875 * p), 78.84375);
}
// Future HLG EOTF (BT.2100 transfer 18) goes here; ABI 1 accepts PQ only.

float hable(float x) {
    return (x * (0.15 * x + 0.05) + 0.004) / (x * (0.15 * x + 0.5) + 0.06) - 0.02 / 0.3;
}
float hable_tonemap(float nits, float source_peak_nits, float target_white_nits) {
    return hable(min(min(max(nits, 0.0), source_peak_nits) / target_white_nits, 1e6))
        / hable(clamp(source_peak_nits / target_white_nits, 1e-3, 1e6));
}
// Defaults: source_peak_nits=1000, target_white_nits=100. The ABI's SDR white 100
// represents the server's internal 203-nit EETF endpoint; normalize its result to 1.
// BT.2390 Hermite knee offset 1 matches the server (the report's example uses 0.5).
// Above-peak inputs clamp to the EETF domain rather than extrapolating its cubic.
float bt2390_eetf(float nits, float source_peak_nits, float target_white_nits) {
    nits = min(max(nits, 0.0), source_peak_nits);
    float target = min(target_white_nits * 2.03, 10000.0);
    if (source_peak_nits <= target) return min(nits / target, 1.0);
    float peak = pq_encode(source_peak_nits);
    float s = pq_encode(nits) / peak;
    float max_lum = pq_encode(target) / peak;
    float knee = 2.0 * max_lum - 1.0;
    float mapped = s;
    if (s >= knee) {
        float t = (s - knee) / (1.0 - knee);
        float t2 = t * t;
        float t3 = t2 * t;
        mapped = (2.0 * t3 - 3.0 * t2 + 1.0) * knee
               + (t3 - 2.0 * t2 + t) * (1.0 - knee)
               + (-2.0 * t3 + 3.0 * t2) * max_lum;
    }
    return clamp(pq_eotf(mapped * peak) / target, 0.0, 1.0);
}
float tone_curve(float nits, float source_peak_nits, float target_white_nits, uint curve) {
    return curve == 2 ? hable_tonemap(nits, source_peak_nits, target_white_nits)
                      : bt2390_eetf(nits, source_peak_nits, target_white_nits);
}
float3 luma_weights(bool bt2020) {
    return bt2020 ? float3(0.2627, 0.6780, 0.0593) : float3(0.2126, 0.7152, 0.0722);
}
// SDR sources are display-referred: decode with the BT.1886 EOTF (power 2.4) to linear 0..1.
float sdr_decode(float signal) { return pow(clamp(signal, 0.0, 1.0), 2.4); }
// Sign-preserving BT.1886 for the BT.709 scale-only pass (saturated colours outside 0..1 RGB survive)
// and for the extended BT.2020 SDR decode; magnitude capped at 1.5.
float sdr_decode_signed(float signal) { return sign(signal) * pow(min(abs(signal), 1.5), 2.4); }
float sdr_encode_signed(float linear_value) { return sign(linear_value) * pow(min(abs(linear_value), 3.0), 1.0 / 2.4); }
// Source modes (flags.w): 0 PQ, 1 SDR (BT.1886), 2 HLG (BT.2100, 1000-nit nominal peak).
float hlg_inverse_oetf(float signal) {
    float e = clamp(signal, 0.0, 1.0);
    return e <= 0.5 ? e * e / 3.0 : (exp((e - 0.55991073) / 0.17883277) + 0.28466892) / 12.0;
}
float3 hlg_ootf(float3 scene) {
    float ys = max(dot(float3(0.2627, 0.6780, 0.0593), scene), 0.0);
    return scene * (1000.0 * pow(ys, 0.2));
}
// extended: sign-preserving (extended-range) transfer decode, the current downscale: a signal below 0
// (footroom, chroma overshoot at a saturated edge) becomes negative light instead of black, so the
// area average keeps it and the one late clip decides; magnitude capped at 1 for PQ (10000 nits)
// and HLG, 1.5 for SDR. false is the pre-2026-11-22 per-pixel clip at 0 (debug `gammachroma`).
// BT.709 SDR is signed either way (its scale-only pass never clipped).
float3 source_decode(float y, float2 uv, bool bt2020, bool full_range, uint mode, bool extended) {
    float3 weights = luma_weights(bt2020);
    y = full_range ? y / 1023.0 : (y - 64.0) / 876.0;
    uv = (uv - 512.0) / (full_range ? 1023.0 : 896.0);
    float r = y + 2.0 * (1.0 - weights.r) * uv.y;
    float b = y + 2.0 * (1.0 - weights.b) * uv.x;
    float g = (y - weights.r * r - weights.b * b) / weights.g;
    // One return: FXC rejects early returns here (X4000).
    // One transfer evaluation per channel for both methods (an HLSL ?: evaluates both sides):
    // signed takes the magnitude and restores the sign, the clipped decode clamps at 0 itself.
    // Clipped: sign 0 below 0 gives the same 0 as clamping (every decode is 0 at 0). Rust spells
    // the same values as pq_eotf_signed / hlg_inverse_oetf_signed / sdr_decode_signed.
    bool is_signed = extended || (mode == 1 && !bt2020);
    float3 e = float3(r, g, b);
    float3 m = abs(e);
    float3 s = float3(sign(e));
    if (!is_signed) s = max(s, 0.0);
    float3 decoded = s * float3(pq_eotf(m.r), pq_eotf(m.g), pq_eotf(m.b));
    // abs() inline (not m): FXC only proves pow's base non-negative when it sees abs() itself (X3571).
    if (mode == 1) decoded = s * pow(min(abs(e), is_signed ? 1.5 : 1.0), 2.4);
    if (mode == 2) decoded = hlg_ootf(s * float3(hlg_inverse_oetf(m.r), hlg_inverse_oetf(m.g), hlg_inverse_oetf(m.b)));
    return decoded;
}
float3 rgb2020_to_709(float3 rgb) {
    return float3(dot(float3(1.660491, -0.5876411, -0.07284986), rgb),
                  dot(float3(-0.1245505, 1.1328999, -0.00834942), rgb),
                  dot(float3(-0.01815076, -0.1005789, 1.1187297), rgb));
}
float3 gamut_clip(float3 rgb) { return clamp(rgb, 0.0, 1.0); }
float3 gamut_to_709(float3 rgb, bool bt2020) {
    return gamut_clip(bt2020 ? rgb2020_to_709(rgb) : rgb);
}
// The one late clip of the current downscale: applied to an averaged output value (an output pixel's
// area, or a chroma footprint), never per source pixel. BT.709 SDR (mode 1, BT.709 matrix) is scale
// only and never clipped: the final quantization alone limits its range, as before.
float3 finish_linear(float3 rgb, bool bt2020, uint mode) {
    return mode == 1 && !bt2020 ? rgb : gamut_clip(rgb);
}
// Scene-referred camera OETF, available for consumers requiring that interpretation.
float bt709_oetf(float linear_value) {
    float x = max(linear_value, 0.0);
    return x < 0.01805397 ? 4.5 * x : 1.0992968 * pow(x, 0.45) - 0.09929683;
}
// Display-referred SDR: zscale t=bt709 defaults to inverse BT.1886, not camera OETF.
float sdr_encode(float linear_value) { return pow(clamp(linear_value, 0.0, 1.0), 1.0 / 2.4); }
float3 rgb709_to_ycbcr(float3 rgb) {
    float y = dot(luma_weights(false), rgb);
    return float3(y, (rgb.b - y) / (2.0 * (1.0 - 0.0722)), (rgb.r - y) / (2.0 * (1.0 - 0.2126)));
}
float3 desaturate(float3 rgb, bool bt2020, float threshold_nits) {
    float luma = dot(luma_weights(bt2020), rgb);
    float over = clamp(max(luma - threshold_nits, 1e-6) / max(luma, 1e-6), 0.0, 1.0);
    return rgb * (1.0 - over) + luma * over;
}
// Tone-mapped display-linear BT.709 RGB: the light domain the area downscale averages in.
// extended (current): unclipped, may leave 0..1 (out-of-gamut colours, sub-black); finish_linear
// clips the average once. !extended (debug `gammachroma`): clipped to 0..1 here, per source pixel.
// Negative and >1 channels are safe below: the curves clamp their input at 0 and the peak, the
// scale divides by max(sig, 1e-6), desaturate guards its divisions; no pow/log of a negative.
float3 tone_map_linear(float y, float2 uv, float source_peak_nits, float target_white_nits,
                       float desat, uint curve, bool bt2020, bool full_range, uint mode, bool extended) {
    float3 rgb = source_decode(y, uv, bt2020, full_range, mode, extended);
    if (mode == 1) {
        // BT.2020 SDR: no tone curve, only BT.2020 -> BT.709 in linear light, clip, re-encode.
        // BT.709 SDR is scale only: no clip, so the final quantization alone limits the range.
        if (bt2020) rgb = rgb2020_to_709(rgb);
        if (bt2020 && !extended) rgb = gamut_clip(rgb);
    } else {
        float original = max(rgb.r, max(rgb.g, rgb.b));
        if (curve == 1 && bt2020) rgb = rgb2020_to_709(rgb);
        if (desat > 0.0) {
            float white = curve == 1 ? target_white_nits * 2.03 : target_white_nits;
            rgb = desaturate(rgb, bt2020, desat * white);
        }
        float sig = curve == 1 ? original : max(rgb.r, max(rgb.g, rgb.b));
        rgb *= tone_curve(sig, source_peak_nits, target_white_nits, curve) / max(sig, 1e-6);
        if (bt2020 && curve == 2) rgb = rgb2020_to_709(rgb);
        if (!extended) rgb = gamut_clip(rgb);
    }
    return rgb;
}
float3 encode_linear(float3 rgb) {
    // Identical to sdr_encode inside 0..1; only the unclipped BT.709 scale-only pass leaves it.
    return rgb709_to_ycbcr(float3(sdr_encode_signed(rgb.r), sdr_encode_signed(rgb.g), sdr_encode_signed(rgb.b)));
}
// One pixel at 1:1 with the current math: extended decode, then the late clip on that pixel.
float3 tone_map_pixel(float y, float2 uv, float source_peak_nits, float target_white_nits,
                      float desat, uint curve, bool bt2020, bool full_range, uint mode) {
    return encode_linear(finish_linear(tone_map_linear(y, uv, source_peak_nits, target_white_nits, desat,
                                                       curve, bt2020, full_range, mode, true), bt2020, mode));
}
// Return integer-valued codes as floats. The binding layer divides by 255 for UNORM RTVs.
float quantize_luma(float y) { return clamp(floor(16.0 + 219.0 * y + 0.5), 16.0, 235.0); }
float2 quantize_chroma(float2 uv) { return clamp(floor(128.0 + 224.0 * uv + 0.5), 16.0, 240.0); }
// 10-bit limited range (ABI 2 P010 output): 64-940 luma, 64-960 chroma.
float quantize_luma10(float y) { return clamp(floor(64.0 + 876.0 * y + 0.5), 64.0, 940.0); }
float2 quantize_chroma10(float2 uv) { return clamp(floor(512.0 + 896.0 * uv + 0.5), 64.0, 960.0); }
// 8-bit NV12 output is ordered-dithered (BALE_TONEMAP_OUTPUT_NV12_DITHERED). Plain rounding adds
// floor(x + 0.5); this adds floor(x + t) with t = (bayer8 + 0.5) / 64, a static 8x8 Bayer matrix at
// the output pixel: 64 evenly spaced thresholds with mean 0.5, so a flat area keeps its mean level
// and a slow ramp's 10-bit steps become a fixed fine pattern instead of 8-bit bands. Bayer rather
// than blue noise: it is computed exactly from the pixel position (no table/texture, bit-identical
// CPU reference) and its period-8 structure is at the highest spatial frequency, invisible at one
// code. No per-frame offset: the pattern is tied to the output pixel and constant over time, so a
// still picture produces identical frames (no crawl, no shimmer); motion only moves the content.
// Chroma V uses the mirrored threshold (63 - bayer8) so Cb and Cr do not step together.
uint bayer8(uint2 p) {
    uint v = 0u;
    [unroll] for (uint i = 0u; i < 3u; i++) {
        uint bx = (p.x >> i) & 1u;
        uint by = (p.y >> i) & 1u;
        v = v * 4u + ((bx ^ by) << 1) + by;
    }
    return v;
}
float dither_threshold(uint v) { return (float(v) + 0.5) / 64.0; }
float quantize_luma_dithered(float y, uint2 p) {
    return clamp(floor(16.0 + 219.0 * y + dither_threshold(bayer8(p))), 16.0, 235.0);
}
float2 quantize_chroma_dithered(float2 uv, uint2 p) {
    uint v = bayer8(p);
    float2 t = float2(dither_threshold(v), dither_threshold(63u - v));
    return clamp(floor(128.0 + 224.0 * uv + t), 16.0, 240.0);
}

#endif
