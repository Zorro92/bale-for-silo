// Planar 4:2:0 repack. The source is an NV12/P010-shaped texture whose luma plane holds the raw
// (unshifted) Y plane and whose chroma rows hold the U row in the left half and the V row in the
// right half. Integer-only: Load, shift and mask, no filtering or float conversion.
cbuffer Repack : register(b0)
{
    uint4 geometry; // x: left shift (P010 6, NV12 0), y: chroma half width in samples, z: sample mask
};

Texture2D<uint> source_y : register(t0);
Texture2D<uint2> source_uv : register(t1);
