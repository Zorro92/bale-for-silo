#include "tonemap_math.hlsli"
// fp32 reference: src/tonemap/cpu.rs. Register-sized records match gpu::Constants.
cbuffer ToneMap : register(b0) {
    uint4 geometry; // visible source w/h, input texture w/h
    uint4 flags;    // curve, BT.2020, full range, source mode (0 PQ, 1 SDR gamut only, 2 HLG)
    float4 params;  // source peak nits, target white nits, desat, output depth (0 NV12, 1 P010)
    uint4 output;   // output w/h (smaller than the visible source when downscaling), input 8-bit, unused
};
Texture2DArray<uint> input_y : register(t0);
Texture2DArray<uint2> input_uv : register(t1);

float2 uv_at(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy/2)-1);
    uint2 raw = input_uv.Load(int4(p,0,0));
    // NV12 codes << 2 are exactly the 10-bit codes (16-235 -> 64-940); P010 keeps them in the top bits.
    return float2(output.z != 0 ? raw << 2 : raw >> 6);
}
// Tone-mapped display-linear RGB of one visible source pixel.
float3 sample_linear(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy)-1);
    // AVCHROMA_LOC_LEFT: horizontally co-sited; vertically halfway between two luma rows.
    float2 c = (float2(p)-float2(0,0.5))*0.5;
    int2 base = int2(floor(c));
    float2 f = c-float2(base);
    float2 uv = lerp(lerp(uv_at(base),uv_at(base+int2(1,0)),f.x),
                     lerp(uv_at(base+int2(0,1)),uv_at(base+int2(1,1)),f.x),f.y);
    uint raw = input_y.Load(int4(p,0,0));
    float y = float(output.z != 0 ? raw << 2 : raw >> 6);
    return tone_map_linear(y, uv, params.x, params.y, params.z, flags.x, flags.y != 0, flags.z != 0, flags.w);
}
// One output pixel: area downscale in display-linear light. Each covered source pixel is weighted
// by the overlap of its unit square with the output pixel's footprint, then the average is encoded
// once. At 1:1 the footprint is exactly one pixel with weight 1, the plain tone-mapped pixel.
float3 output_pixel(int2 o) {
    o = clamp(o, int2(0,0), int2(output.xy)-1);
    float2 scale = float2(geometry.xy) / float2(output.xy);
    float2 lo = float2(o) * scale;
    float2 hi = lo + scale;
    int2 first = int2(floor(lo));
    int2 last = min(int2(ceil(hi)) - 1, int2(geometry.xy) - 1);
    float3 sum = float3(0,0,0);
    float total = 0.0;
    [loop] for (int sy = first.y; sy <= last.y; sy++) {
        float wy = min(float(sy+1), hi.y) - max(float(sy), lo.y);
        [loop] for (int sx = first.x; sx <= last.x; sx++) {
            float w = wy * (min(float(sx+1), hi.x) - max(float(sx), lo.x));
            sum += w * sample_linear(int2(sx, sy));
            total += w;
        }
    }
    return encode_linear(sum / max(total, 1e-6));
}
#ifdef BALE_STAGED
// Downscale: tonemap_area_ps has already written output_pixel(o) of every output pixel at full fp32,
// so the plane passes read it instead of evaluating the area sum again (luma 1x, chroma 6x).
// Same clamp as output_pixel: an out-of-range neighbour reads the edge pixel, never Load's zero.
Texture2D<float4> staged : register(t2);
float3 staged_pixel(int2 o) {
    o = clamp(o, int2(0,0), int2(output.xy)-1);
    return staged.Load(int3(o,0)).xyz;
}
#define OUTPUT_PIXEL staged_pixel
#else
#define OUTPUT_PIXEL output_pixel
#endif
