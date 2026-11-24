#include "tonemap_math.hlsli"
// fp32 reference: src/tonemap/cpu.rs. Register-sized records match gpu::Constants.
cbuffer ToneMap : register(b0) {
    uint4 geometry; // visible source w/h, input texture w/h
    uint4 flags;    // curve, BT.2020, full range, source mode (0 PQ, 1 SDR gamut only, 2 HLG)
    float4 params;  // source peak nits (PQ: the stream's metadata, cpu::source_peak), target white nits,
                    // desat, output depth (0 NV12, 1 P010)
    uint4 output;   // output w/h (smaller than the visible source when downscaling), input 8-bit,
                    // method (cpu::method_word): bits 0-7 input AVChromaLocation, bit 8 legacy math (debug
                    // `gammachroma`), bit 9 bilinear input chroma (debug `bilinearchroma`), bits 10/11
                    // co-sited fast path per axis (cpu::cosited_bits)
};
Texture2DArray<uint> input_y : register(t0);
Texture2DArray<uint2> input_uv : register(t1);

// Debug `gammachroma`: the pre-2026-11-22 math (per-pixel clip, left siting, chroma filtered from
// encoded Cb/Cr). Uniform, so the branches below cost no divergence.
bool legacy_math() { return (output.w & 0x100u) != 0; }
// Debug `bilinearchroma`: the bilinear input chroma of before 2026-11-24 instead of Catmull-Rom. Uniform.
bool bilinear_chroma() { return (output.w & 0x200u) != 0; }

float2 uv_at(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy/2)-1);
    uint2 raw = input_uv.Load(int4(p,0,0));
    // NV12 codes << 2 are exactly the 10-bit codes (16-235 -> 64-940); P010 keeps them in the top bits.
    return float2(output.z != 0 ? raw << 2 : raw >> 6);
}
// Input chroma sample (i, j) sits at luma position (2i, 2j) + chroma_offset, from the stream's
// AVChromaLocation: 1 LEFT (and 0 unspecified, and any unknown value): co-sited horizontally,
// halfway between two rows; 2 CENTER: halfway both ways; 3 TOPLEFT: co-sited both ways; 4 TOP:
// halfway horizontally, on the top row; 5 BOTTOMLEFT / 6 BOTTOM: on the bottom row of the pair.
float2 chroma_offset(uint location) {
    float x = location == 2u || location == 4u || location == 6u ? 0.5 : 0.0;
    float y = location == 3u || location == 4u ? 0.0 : (location == 5u || location == 6u ? 1.0 : 0.5);
    return float2(x, y);
}
// Bilinear input chroma at chroma-sample position c (debug `bilinearchroma`; the read of before
// 2026-11-24, unchanged).
float2 chroma_bilinear(float2 c) {
    int2 base = int2(floor(c));
    float2 f = c-float2(base);
    return lerp(lerp(uv_at(base),uv_at(base+int2(1,0)),f.x),
                lerp(uv_at(base+int2(0,1)),uv_at(base+int2(1,1)),f.x),f.y);
}
// One input chroma sample in the input's own codes: P010's 10-bit code, NV12's 8-bit code (chroma_cubic
// multiplies its result by 4 once: exact, the same as reading code << 2 per tap).
float2 chroma_code(int x, int y) {
    return float2(input_uv.Load(int4(x, y, 0, 0)) >> (output.z != 0 ? 0u : 6u));
}
// Catmull-Rom across the 4 taps xs of chroma row y, and down the 4 taps ys of chroma column x.
float2 chroma_row(int4 xs, int y, float4 w) {
    return w.x * chroma_code(xs.x, y) + w.y * chroma_code(xs.y, y)
         + w.z * chroma_code(xs.z, y) + w.w * chroma_code(xs.w, y);
}
float2 chroma_column(int x, int4 ys, float4 w) {
    return w.x * chroma_code(x, ys.x) + w.y * chroma_code(x, ys.y)
         + w.z * chroma_code(x, ys.z) + w.w * chroma_code(x, ys.w);
}
// Separable 4x4 Catmull-Rom of the input chroma at chroma-sample position c (2026-11-24; xav part 2,
// first half): the same encoded samples and siting as the bilinear read, a sharper filter. Clamped to
// the valid code range, since Catmull-Rom overshoots at sharp edges. The fraction of c is 0, 1/4, 1/2
// or 3/4 per axis, where the weights are exact and the sums of integer codes are too (unless a partial
// sum passes 1024), so cpu.rs reproduces this. 16 loads; fast path (bits 10/11, downscale ratio 2 or
// 4 on that axis): every lane of a wave reads the same phase in each step of the area loop, so the
// branch is uniform, and a co-sited phase (fraction 0, weights 0 1 0 0) reads only its centre taps,
// with the same result: 4 loads (one axis co-sited) or 1 (both).
float2 chroma_cubic(float2 c) {
    int2 base = int2(floor(c));
    float2 f = c - float2(base);
    int2 last = int2(geometry.xy / 2) - 1;
    int4 xs = clamp(base.x + int4(-1, 0, 1, 2), 0, last.x);
    int4 ys = clamp(base.y + int4(-1, 0, 1, 2), 0, last.y);
    float4 wx = catmull_rom(f.x);
    float4 wy = catmull_rom(f.y);
    bool cosited_x = (output.w & 0x400u) != 0 && f.x == 0.0;
    bool cosited_y = (output.w & 0x800u) != 0 && f.y == 0.0;
    float2 uv;
    [branch] if (cosited_y) {
        [branch] if (cosited_x) uv = chroma_code(xs.y, ys.y);
        else uv = chroma_row(xs, ys.y, wx);
    } else {
        [branch] if (cosited_x) uv = chroma_column(xs.y, ys, wy);
        else uv = wy.x * chroma_row(xs, ys.x, wx) + wy.y * chroma_row(xs, ys.y, wx)
                + wy.z * chroma_row(xs, ys.z, wx) + wy.w * chroma_row(xs, ys.w, wx);
    }
    uv *= output.z != 0 ? 4.0 : 1.0;
    return flags.z != 0 ? clamp(uv, 0.0, 1023.0) : clamp(uv, 64.0, 960.0);
}
// Tone-mapped display-linear RGB of one visible source pixel (unclipped unless legacy_math).
float3 sample_linear(int2 p) {
    p = clamp(p, int2(0,0), int2(geometry.xy)-1);
    // Input chroma at this luma pixel's position in chroma-sample coordinates.
    float2 c = (float2(p)-chroma_offset(legacy_math() ? 1u : (output.w & 0xffu)))*0.5;
    float2 uv;
    [branch] if (bilinear_chroma()) uv = chroma_bilinear(c);
    else uv = chroma_cubic(c);
    uint raw = input_y.Load(int4(p,0,0));
    float y = float(output.z != 0 ? raw << 2 : raw >> 6);
    return tone_map_linear(y, uv, params.x, params.y, params.z, flags.x, flags.y != 0, flags.z != 0, flags.w,
                           !legacy_math());
}
// One output pixel's area average in display-linear light, before the late clip. Each covered
// source pixel is weighted by the overlap of its unit square with the output pixel's footprint.
// At 1:1 the footprint is exactly one pixel with weight 1, the plain tone-mapped pixel.
float3 area_linear(int2 o) {
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
    return sum / max(total, 1e-6);
}
// Linear average (or footprint mix of them) -> the one late clip -> Y'CbCr, encoded once.
float3 encode_output(float3 linear_rgb) {
    return encode_linear(finish_linear(linear_rgb, flags.y != 0, flags.w));
}
// The per-output-pixel value the plane passes combine: the linear area average (current math), or
// the encoded Y'CbCr of that average (legacy_math: chroma then filters encoded Cb/Cr, as before).
float3 area_value(int2 o) {
    float3 v = area_linear(o);
    [branch] if (legacy_math()) v = encode_linear(v);
    return v;
}
// area_value -> encoded Y'CbCr of one output pixel (the luma pass).
float3 encode_area_value(float3 v) { return legacy_math() ? v : encode_output(v); }
#ifdef BALE_STAGED
// Downscale: tonemap_area_ps has already written area_value(o) of every output pixel at full fp32,
// so the plane passes read it instead of evaluating the area sum again (luma 1x, chroma 6x).
// Same clamp as area_linear: an out-of-range neighbour reads the edge pixel, never Load's zero.
Texture2D<float4> staged : register(t2);
float3 staged_value(int2 o) {
    o = clamp(o, int2(0,0), int2(output.xy)-1);
    return staged.Load(int3(o,0)).xyz;
}
#define AREA_VALUE staged_value
#else
#define AREA_VALUE area_value
#endif
