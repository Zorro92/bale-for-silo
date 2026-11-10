// Zero-copy repack (D3D12, zero-copy ABI 1): dav1d's 10-bit planar picture, read straight from the
// buffer dav1d decoded into, becomes a P010 texture. Samples are little-endian 16-bit words with
// the value in the low 10 bits; P010 stores it in the high 10 bits.
//
// Built twice (build.rs): repack_zc_cs.cso as above, and repack_zc8_cs.cso with BALE_8BIT for
// dav1d's 8-bit planar picture (one byte per sample, strides and offsets still in bytes), which
// P010 stores as v << 8. Same thread mapping, root constants and dispatch at both depths. The
// 8-bit build reads whole words: a thread's 2-byte chroma pair is half of the word it shares with
// its neighbour (BaleZcFrame::fits_bits checks those reads).
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

#ifdef BALE_8BIT
// Byte `k` of `word` as a P010 sample.
uint byte_at(uint word, uint k)
{
    return ((word >> (k * 8u)) & 0xffu) << 8;
}
#endif

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x * 4 >= shape.z || id.y * 2 >= shape.w)
    {
        return;
    }
#ifdef BALE_8BIT
    // Chroma bytes 2x and 2x + 1: the low or high half of the word at byte 4 * (x / 2).
    uint half_shift = (id.x & 1u) * 16u;
    uint u = source.Load(planes.y + id.y * shape.y + (id.x >> 1u) * 4u) >> half_shift;
    uint v = source.Load(planes.z + id.y * shape.y + (id.x >> 1u) * 4u) >> half_shift;
    uint2 pair = uint2(id.x * 2u, id.y);
    chroma[pair] = uint2(byte_at(u, 0u), byte_at(v, 0u));
    chroma[pair + uint2(1, 0)] = uint2(byte_at(u, 1u), byte_at(v, 1u));

    [unroll]
    for (uint r = 0; r < 2; r++)
    {
        uint row = id.y * 2 + r;
        uint word = source.Load(planes.x + row * shape.x + id.x * 4);
        uint2 at = uint2(id.x * 4, row);
        luma[at] = byte_at(word, 0u);
        luma[at + uint2(1, 0)] = byte_at(word, 1u);
        luma[at + uint2(2, 0)] = byte_at(word, 2u);
        luma[at + uint2(3, 0)] = byte_at(word, 3u);
    }
#else
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
#endif
}
