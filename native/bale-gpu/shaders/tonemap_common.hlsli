#include "tonemap_math.hlsli"
// fp32 reference: src/tonemap/cpu.rs. Register-sized records match gpu::Constants.
cbuffer ToneMap : register(b0) {
    uint4 geometry; // visible w/h, input w/h
    uint4 flags;    // curve, BT.2020, full range, SDR source (BT.2020 SDR: gamut conversion only)
    float4 params; // source peak nits, target white nits, desat, reserved
};
Texture2DArray<uint> input_y : register(t0);
Texture2DArray<uint2> input_uv : register(t1);

float2 uv_at(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy/2)-1);
    return float2(input_uv.Load(int4(p,0,0)) >> 6);
}
float3 sample_pixel(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy)-1);
    // AVCHROMA_LOC_LEFT: horizontally co-sited; vertically halfway between two luma rows.
    float2 c = (float2(p)-float2(0,0.5))*0.5;
    int2 base = int2(floor(c));
    float2 f = c-float2(base);
    float2 uv = lerp(lerp(uv_at(base),uv_at(base+int2(1,0)),f.x),
                     lerp(uv_at(base+int2(0,1)),uv_at(base+int2(1,1)),f.x),f.y);
    float y = float(input_y.Load(int4(p,0,0)) >> 6);
    return tone_map_pixel(y, uv, params.x, params.y, params.z, flags.x, flags.y != 0, flags.z != 0, flags.w != 0);
}
