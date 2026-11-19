// Deliberately include only the math: no resources/cbuffers supplied by this harness.
#include "tonemap_math.hlsli"
float4 main(float4 position : SV_Position) : SV_Target {
    uint curve = position.x > 0.5 ? 1 : 2;
    float3 yuv = tone_map_pixel(position.x, position.yz, 1000.0, 100.0, 0.0, curve, true, false, (uint)clamp(position.w * 3.0, 0.0, 2.0));
    // Plain and dithered 8-bit quantisation both compile on SM5 and SM6.
    float dithered = quantize_luma_dithered(yuv.x, uint2(position.xy));
    float2 dithered_uv = quantize_chroma_dithered(yuv.yz, uint2(position.xy));
    return float4(quantize_luma(yuv.x) + dithered, quantize_chroma(yuv.yz) + dithered_uv, bt709_oetf(position.w));
}
