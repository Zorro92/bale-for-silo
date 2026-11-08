// Matches gpu.rs::Constants, six complete 16-byte cbuffer registers.
cbuffer GrainParams : register(b0)
{
    uint width, height, bit_depth, storage_shift;
    uint scaling_shift, overlap, csfl, luma_active;
    int4 cb_params; // mult, luma_mult, offset, active
    int4 cr_params;
    int luma_min, luma_max, chroma_min, chroma_max;
    int grain_min, grain_max, pixel_max, reserved;
};

Texture2D<uint> source_y : register(t0);
Texture2D<uint2> source_uv : register(t1);
Texture2D<int> template_y : register(t2);
Texture2D<int2> template_uv : register(t3);
Texture2D<uint> scaling : register(t4);
Texture2D<uint> offsets : register(t5);

int round2(int value, int shift)
{
    return (value + ((1 << shift) >> 1)) >> shift;
}

int blend(int old, int current, int2 weights)
{
    return clamp(round2(old * weights.x + current * weights.y, 5), grain_min, grain_max);
}

int2 blend_uv(int2 old, int2 current)
{
    return clamp(int2(round2(old.x * 23 + current.x * 22, 5),
                      round2(old.y * 23 + current.y * 22, 5)), grain_min, grain_max);
}
