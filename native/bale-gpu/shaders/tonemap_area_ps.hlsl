#include "tonemap_common.hlsli"
// Downscale pass 1: each output pixel's area_value, once, into the R32G32B32A32_FLOAT intermediate:
// the unclipped linear average (legacy_math: its encoded Y'CbCr). Exact fp32, so the plane passes
// see the same value the per-plane path computes.
float4 main(float4 position : SV_Position) : SV_Target {
    return float4(area_value(int2(position.xy)), 0.0);
}
