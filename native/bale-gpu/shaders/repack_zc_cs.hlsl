// Zero-copy repack (D3D12, zero-copy ABI 1): dav1d's 10-bit planar picture, read straight from the
// buffer dav1d decoded into, becomes a P010 texture. Samples are little-endian 16-bit words with
// the value in the low 10 bits; P010 stores it in the high 10 bits.
//
// One thread per chroma pair: thread (x, y) writes chroma samples (2x, y) and (2x + 1, y) and the
// 4x2 luma block starting at (4x, 2y). Writes past the texture edge (odd chroma width) are
// discarded by the hardware; reads past the buffer end return 0 (raw view of the whole buffer).

cbuffer Frame : register(b0)
{
    uint4 planes; // byte offsets of the Y, U and V planes in the buffer; w unused
    uint4 shape;  // luma stride, chroma stride (bytes), width, height (luma samples)
};

ByteAddressBuffer source : register(t0);
RWTexture2D<uint> luma : register(u0);    // P010 plane 0 as R16_UINT
RWTexture2D<uint2> chroma : register(u1); // P010 plane 1 as R16G16_UINT

uint low(uint word)
{
    return (word & 0x3ff) << 6;
}

uint high(uint word)
{
    return ((word >> 16) & 0x3ff) << 6;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x * 4 >= shape.z || id.y * 2 >= shape.w)
    {
        return;
    }
    uint u = source.Load(planes.y + id.y * shape.y + id.x * 4);
    uint v = source.Load(planes.z + id.y * shape.y + id.x * 4);
    uint2 pair = uint2(id.x * 2, id.y);
    chroma[pair] = uint2(low(u), low(v));
    chroma[pair + uint2(1, 0)] = uint2(high(u), high(v));

    [unroll]
    for (uint r = 0; r < 2; r++)
    {
        uint row = id.y * 2 + r;
        uint2 words = source.Load2(planes.x + row * shape.x + id.x * 8);
        uint2 at = uint2(id.x * 4, row);
        luma[at] = low(words.x);
        luma[at + uint2(1, 0)] = high(words.x);
        luma[at + uint2(2, 0)] = low(words.y);
        luma[at + uint2(3, 0)] = high(words.y);
    }
}
