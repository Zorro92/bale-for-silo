/*
 * Copyright © 2018, Niklas Haas
 * Copyright © 2018, VideoLAN and dav1d authors
 * Copyright © 2018, Two Orioles, LLC
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 * 1. Redistributions of source code must retain the above copyright notice, this
 *    list of conditions and the following disclaimer.
 *
 * 2. Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
 * ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Zero-copy repack with AV1 film grain (D3D12, zero-copy ABI 1, direct writes): repack_zc_cs.hlsl
// plus dav1d 1.5.4's 4:2:0 fgy/fguv, the math of grain_luma_ps.hlsl and grain_chroma_ps.hlsl.
// Same thread mapping, inputs and constants as repack_zc_cs.hlsl. Chroma reads the un-grained
// luma from the source buffer. src/grain/fused.rs builds the grain buffer (root SRV t1) and holds
// a line-by-line Rust model of this shader that is checked against the CPU oracle.
//
// Built twice (build.rs): repack_grain_zc_cs.cso for 10-bit pictures, and
// repack_grain_zc8_cs.cso with BALE_8BIT for 8-bit ones (one byte per sample, read like
// repack_zc_cs.hlsl's 8-bit build). The grain math is the same; its depth-dependent values all
// come from the header, and only the source unpacking and OUT_SHIFT (the P010 shift) differ.
//
// Layout (little-endian, every section 4-byte aligned):
//
//   header, 32 words: 0 width, 1 height, 2 scaling_shift, 3 overlap, 4 csfl, 5 luma_active,
//     6..9 cb: mult, luma_mult, offset << (bit_depth - 8), active; 10..13 cr: the same; 14 luma_min,
//     15 luma_max, 16 chroma_min, 17 chroma_max, 18 grain_min, 19 grain_max,
//     20 offset row pitch in 32x32 blocks, 21..24 byte offsets of template_y, template_uv,
//     scaling and offsets, 25 pixel_max, 26..31 zero
//   template_y: 82x73 i16, row-major, two per word, low half first
//   template_uv: 44x38 words, row-major, cb i16 in the low half, cr in the high half
//   scaling: three rows (Y, Cb, Cr) of 1024 u8 (an 8-bit LUT uses the first 256)
//   offsets: one u8 per 32x32 luma block, row-major
//
// Root SRVs are not bounds-checked. Grain data is read only for pixels inside the frame (frame
// width and height are even); there every index, also of an overlap sample a flattened branch
// computes but does not use, stays inside the buffer (the Rust model asserts it).

cbuffer Frame : register(b0)
{
    uint4 planes; // byte offsets of the Y, U and V planes in the buffer; w unused
    uint4 shape;  // luma stride, chroma stride (bytes), width, height (luma samples)
};

ByteAddressBuffer source : register(t0);
ByteAddressBuffer grain : register(t1);
RWTexture2D<uint> luma : register(u0);    // P010 plane 0 as R16_UINT
RWTexture2D<uint2> chroma : register(u1); // P010 plane 1 as R16G16_UINT

#ifdef BALE_8BIT
#define OUT_SHIFT 8 // an 8-bit sample's P010 word: v << 8
#else
#define OUT_SHIFT 6 // a 10-bit sample's P010 word: v << 6
#endif

struct Grain
{
    int scaling_shift;
    uint overlap;
    uint csfl;
    uint luma_active;
    int4 cb; // mult, luma_mult, offset << (bit_depth - 8), active
    int4 cr;
    int luma_min;
    int luma_max;
    int chroma_min;
    int chroma_max;
    int grain_min;
    int grain_max;
    uint cols;
    uint template_y;
    uint template_uv;
    uint scaling;
    uint offsets;
    int pixel_max;
};

Grain load_grain()
{
    uint4 a = grain.Load4(0);  // words 0..3
    uint4 b = grain.Load4(16); // 4..7
    uint4 c = grain.Load4(32); // 8..11
    uint4 d = grain.Load4(48); // 12..15
    uint4 e = grain.Load4(64); // 16..19
    uint4 f = grain.Load4(80); // 20..23
    uint2 g = grain.Load2(96); // 24..25
    Grain p;
    p.scaling_shift = asint(a.z);
    p.overlap = a.w;
    p.csfl = b.x;
    p.luma_active = b.y;
    p.cb = asint(uint4(b.z, b.w, c.x, c.y));
    p.cr = asint(uint4(c.z, c.w, d.x, d.y));
    p.luma_min = asint(d.z);
    p.luma_max = asint(d.w);
    p.chroma_min = asint(e.x);
    p.chroma_max = asint(e.y);
    p.grain_min = asint(e.z);
    p.grain_max = asint(e.w);
    p.cols = f.x;
    p.template_y = f.y;
    p.template_uv = f.z;
    p.scaling = f.w;
    p.offsets = g.x;
    p.pixel_max = asint(g.y);
    return p;
}

uint load_byte(uint address)
{
    return (grain.Load(address & ~3u) >> ((address & 3u) * 8u)) & 0xffu;
}

int round2(int value, int shift)
{
    return (value + ((1 << shift) >> 1)) >> shift;
}

int blend(Grain p, int old, int current, int2 weights)
{
    return clamp(round2(old * weights.x + current * weights.y, 5), p.grain_min, p.grain_max);
}

int2 blend_uv(Grain p, int2 old, int2 current)
{
    return int2(blend(p, old.x, current.x, int2(23, 22)), blend(p, old.y, current.y, int2(23, 22)));
}

uint block_offset(Grain p, int2 block)
{
    return load_byte(uint(int(p.offsets) + block.y * int(p.cols) + block.x));
}

int luma_template(Grain p, int2 at)
{
    uint address = p.template_y + uint((at.y * 82 + at.x) * 2);
    uint word = grain.Load(address & ~3u);
    // Explicit sign extension of the packed i16.
    return (address & 2u) != 0 ? asint(word) >> 16 : asint(word << 16) >> 16;
}

int2 chroma_template(Grain p, int2 at)
{
    uint word = grain.Load(p.template_uv + uint((at.y * 44 + at.x) * 4));
    return int2(asint(word << 16) >> 16, asint(word) >> 16);
}

int luma_sample(Grain p, int2 block, int2 local, int2 previous)
{
    uint r = block_offset(p, block - previous);
    int2 origin = 3 + 2 * (3 + int2(int(r >> 4), int(r & 15u)));
    return luma_template(p, origin + local + 32 * previous);
}

int2 chroma_sample(Grain p, int2 block, int2 local, int2 previous)
{
    uint r = block_offset(p, block - previous);
    int2 origin = 3 + (3 + int2(int(r >> 4), int(r & 15u)));
    return chroma_template(p, origin + local + 16 * previous);
}

int scale_at(Grain p, uint row, int index)
{
    return int(load_byte(p.scaling + row * 1024u + uint(index)));
}

// `value`: the luma sample at `pixel`. Returns the P010 word.
uint grain_luma(Grain p, int2 pixel, uint value)
{
    if (pixel.x >= int(shape.z) || p.luma_active == 0)
        return value << OUT_SHIFT;

    int2 block = pixel >> 5;
    int2 local = pixel & 31;
    bool horizontal = p.overlap != 0 && block.x > 0 && local.x < 2;
    bool vertical = p.overlap != 0 && block.y > 0 && local.y < 2;
    int2 wx = local.x == 0 ? int2(27, 17) : int2(17, 27);
    int2 wy = local.y == 0 ? int2(27, 17) : int2(17, 27);
    int n = luma_sample(p, block, local, int2(0, 0));
    if (vertical)
    {
        // dav1d's corner order: top horizontal, current horizontal, vertical.
        // Each blend clips independently, including both horizontal intermediates.
        int top = luma_sample(p, block, local, int2(0, 1));
        if (horizontal)
        {
            top = blend(p, luma_sample(p, block, local, int2(1, 1)), top, wx);
            n = blend(p, luma_sample(p, block, local, int2(1, 0)), n, wx);
        }
        n = blend(p, top, n, wy);
    }
    else if (horizontal)
    {
        n = blend(p, luma_sample(p, block, local, int2(1, 0)), n, wx);
    }
    int src = int(value);
    int result = clamp(src + round2(scale_at(p, 0u, src) * n, p.scaling_shift), p.luma_min,
                       p.luma_max);
    return uint(result) << OUT_SHIFT;
}

uint grain_plane(Grain p, uint value, int avg, int n, int4 plane, uint row)
{
    if (plane.w == 0)
        return value << OUT_SHIFT;
    int src = int(value);
    int index = avg;
    if (p.csfl != 0)
        row = 0u;
    else
        index = clamp(((avg * plane.y + src * plane.x) >> 6) + plane.z, 0, p.pixel_max);
    int result = clamp(src + round2(scale_at(p, row, index) * n, p.scaling_shift), p.chroma_min,
                       p.chroma_max);
    return uint(result) << OUT_SHIFT;
}

// `value`: the (cb, cr) pair at `pixel`; `avg`: dav1d's rounded mean of un-grained luma
// (2x, 2y) and (min(2x + 1, width - 1), 2y). Returns the P010 pair.
uint2 grain_chroma(Grain p, int2 pixel, uint2 value, int avg)
{
    if (pixel.x >= int(shape.z >> 1) || (p.cb.w == 0 && p.cr.w == 0))
        return value << OUT_SHIFT;

    int2 block = pixel >> 4;
    int2 local = pixel & 15;
    bool horizontal = p.overlap != 0 && block.x > 0 && local.x == 0;
    bool vertical = p.overlap != 0 && block.y > 0 && local.y == 0;
    int2 n = chroma_sample(p, block, local, int2(0, 0));
    if (vertical)
    {
        int2 top = chroma_sample(p, block, local, int2(0, 1));
        if (horizontal)
        {
            top = blend_uv(p, chroma_sample(p, block, local, int2(1, 1)), top);
            n = blend_uv(p, chroma_sample(p, block, local, int2(1, 0)), n);
        }
        n = blend_uv(p, top, n);
    }
    else if (horizontal)
    {
        n = blend_uv(p, chroma_sample(p, block, local, int2(1, 0)), n);
    }
    return uint2(grain_plane(p, value.x, avg, n.x, p.cb, 1u),
                 grain_plane(p, value.y, avg, n.y, p.cr, 2u));
}

#ifdef BALE_8BIT
uint4 unpack(uint word)
{
    return uint4(word & 0xffu, (word >> 8u) & 0xffu, (word >> 16u) & 0xffu, word >> 24u);
}
#else
uint4 unpack(uint2 words)
{
    return uint4(words.x & 0x3ffu, (words.x >> 16) & 0x3ffu, words.y & 0x3ffu,
                 (words.y >> 16) & 0x3ffu);
}
#endif

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x * 4 >= shape.z || id.y * 2 >= shape.w)
    {
        return;
    }
    Grain p = load_grain();
#ifdef BALE_8BIT
    // Chroma bytes 2x and 2x + 1: the low or high half of the word at byte 4 * (x / 2).
    uint half_shift = (id.x & 1u) * 16u;
    uint u = source.Load(planes.y + id.y * shape.y + (id.x >> 1u) * 4u) >> half_shift;
    uint v = source.Load(planes.z + id.y * shape.y + (id.x >> 1u) * 4u) >> half_shift;
    uint4 rows[2];
    rows[0] = unpack(source.Load(planes.x + id.y * 2 * shape.x + id.x * 4));
    rows[1] = unpack(source.Load(planes.x + (id.y * 2 + 1) * shape.x + id.x * 4));
    uint2 first = uint2(u & 0xffu, v & 0xffu);
    uint2 second = uint2((u >> 8u) & 0xffu, (v >> 8u) & 0xffu);
#else
    uint u = source.Load(planes.y + id.y * shape.y + id.x * 4);
    uint v = source.Load(planes.z + id.y * shape.y + id.x * 4);
    uint4 rows[2];
    rows[0] = unpack(source.Load2(planes.x + id.y * 2 * shape.x + id.x * 8));
    rows[1] = unpack(source.Load2(planes.x + (id.y * 2 + 1) * shape.x + id.x * 8));
    uint2 first = uint2(u & 0x3ffu, v & 0x3ffu);
    uint2 second = uint2((u >> 16) & 0x3ffu, (v >> 16) & 0x3ffu);
#endif

    // The width is even, so both luma samples of each chroma pixel are in this thread's top row.
    int2 pair = int2(int(id.x * 2), int(id.y));
    int avg0 = int((rows[0].x + rows[0].y + 1) >> 1);
    int avg1 = int((rows[0].z + rows[0].w + 1) >> 1);
    chroma[uint2(pair)] = grain_chroma(p, pair, first, avg0);
    chroma[uint2(pair) + uint2(1, 0)] = grain_chroma(p, pair + int2(1, 0), second, avg1);

    [unroll]
    for (uint r = 0; r < 2; r++)
    {
        int2 at = int2(int(id.x * 4), int(id.y * 2 + r));
        uint4 row = rows[r];
        luma[uint2(at)] = grain_luma(p, at, row.x);
        luma[uint2(at) + uint2(1, 0)] = grain_luma(p, at + int2(1, 0), row.y);
        luma[uint2(at) + uint2(2, 0)] = grain_luma(p, at + int2(2, 0), row.z);
        luma[uint2(at) + uint2(3, 0)] = grain_luma(p, at + int2(3, 0), row.w);
    }
}
