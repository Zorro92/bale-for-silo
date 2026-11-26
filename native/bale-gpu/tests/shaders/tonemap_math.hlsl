// Deliberately include only the math: no resources/cbuffers supplied by this harness.
#include "tonemap_math.hlsli"
float4 main(float4 position : SV_Position) : SV_Target {
    uint curve = position.x > 0.5 ? 1 : 2;
    float3 yuv = tone_map_pixel(position.x, position.yz, 1000.0, 100.0, 0.0, curve, true, false, (uint)clamp(position.w * 3.0, 0.0, 2.0));
    // Plain and dithered (Bayer and R2) 8-bit quantisation all compile on SM5 and SM6.
    float dithered = quantize_luma_dithered(yuv.x, uint2(position.xy)) + quantize_luma_r2(yuv.x, uint2(position.xy));
    float2 dithered_uv = quantize_chroma_dithered(yuv.yz, uint2(position.xy)) + quantize_chroma_r2(yuv.yz, uint2(position.xy));
    // So do the Catmull-Rom chroma upsampling weights.
    float4 cubic = catmull_rom(frac(position.z));
    return float4(quantize_luma(yuv.x) + dithered, quantize_chroma(yuv.yz) + dithered_uv + cubic.xy * cubic.zw,
                  bt709_oetf(position.w));
}
