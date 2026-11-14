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
float3 source_decode(float y, float2 uv, bool bt2020, bool full_range, bool sdr) {
    float3 weights = luma_weights(bt2020);
    y = full_range ? y / 1023.0 : (y - 64.0) / 876.0;
    uv = (uv - 512.0) / (full_range ? 1023.0 : 896.0);
    float r = y + 2.0 * (1.0 - weights.r) * uv.y;
    float b = y + 2.0 * (1.0 - weights.b) * uv.x;
    float g = (y - weights.r * r - weights.b * b) / weights.g;
    return sdr ? float3(sdr_decode(r), sdr_decode(g), sdr_decode(b))
               : float3(pq_eotf(r), pq_eotf(g), pq_eotf(b));
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
float3 tone_map_pixel(float y, float2 uv, float source_peak_nits, float target_white_nits,
                      float desat, uint curve, bool bt2020, bool full_range, bool sdr) {
    float3 rgb = source_decode(y, uv, bt2020, full_range, sdr);
    if (sdr) {
        // BT.2020 SDR: no tone curve, only BT.2020 -> BT.709 in linear light, clip, re-encode.
        rgb = gamut_to_709(rgb, bt2020);
    } else {
        float original = max(rgb.r, max(rgb.g, rgb.b));
        if (curve == 1 && bt2020) rgb = rgb2020_to_709(rgb);
        if (desat > 0.0) {
            float white = curve == 1 ? target_white_nits * 2.03 : target_white_nits;
            rgb = desaturate(rgb, bt2020, desat * white);
        }
        float sig = curve == 1 ? original : max(rgb.r, max(rgb.g, rgb.b));
        rgb *= tone_curve(sig, source_peak_nits, target_white_nits, curve) / max(sig, 1e-6);
        rgb = gamut_to_709(rgb, bt2020 && curve == 2);
    }
    return rgb709_to_ycbcr(float3(sdr_encode(rgb.r), sdr_encode(rgb.g), sdr_encode(rgb.b)));
}
// Return integer-valued codes as floats. The binding layer divides by 255 for UNORM RTVs.
float quantize_luma(float y) { return clamp(floor(16.0 + 219.0 * y + 0.5), 16.0, 235.0); }
float2 quantize_chroma(float2 uv) { return clamp(floor(128.0 + 224.0 * uv + 0.5), 16.0, 240.0); }

#endif
