#include "repack_common.hlsli"

// Sample `index` of a chroma row stored two samples per R16G16/R8G8 texel.
uint sample_at(uint index, int row)
{
    uint2 texel = source_uv.Load(int3(int(index >> 1), row, 0));
    return (index & 1) != 0 ? texel.y : texel.x;
}

uint2 main(float4 position : SV_Position) : SV_Target
{
    int2 pixel = int2(position.xy);
    uint u = sample_at(uint(pixel.x), pixel.y);
    uint v = sample_at(uint(pixel.x) + geometry.y, pixel.y);
    return (uint2(u, v) << geometry.x) & geometry.z;
}
