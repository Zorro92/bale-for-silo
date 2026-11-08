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

#include "grain_common.hlsli"

int2 sample_grain(int2 block, int2 local, int2 previous)
{
    uint r = offsets.Load(int3(block - previous, 0));
    int2 origin = 3 + (3 + int2(r >> 4, r & 15));
    return template_uv.Load(int3(origin + local + 16 * previous, 0));
}

uint apply_plane(uint stored, int avg, int grain, int4 params, int row)
{
    if (params.w == 0)
        return stored;
    int src = int(stored >> storage_shift);
    int index = avg;
    if (csfl != 0)
        row = 0;
    else
        index = clamp(((avg * params.y + src * params.x) >> 6) +
                      (params.z << int(bit_depth - 8)), 0, pixel_max);
    int scale = int(scaling.Load(int3(index, row, 0)));
    int value = clamp(src + round2(scale * grain, int(scaling_shift)), chroma_min, chroma_max);
    return uint(value) << storage_shift;
}

uint2 main(float4 position : SV_Position) : SV_Target
{
    int2 pixel = int2(position.xy);
    uint2 stored = source_uv.Load(int3(pixel, 0));
    if (pixel.x >= int((width + 1) >> 1) || pixel.y >= int((height + 1) >> 1) ||
        (cb_params.w == 0 && cr_params.w == 0))
        return stored;

    int2 block = pixel >> 4;
    int2 local = pixel & 15;
    bool horizontal = overlap != 0 && block.x > 0 && local.x == 0;
    bool vertical = overlap != 0 && block.y > 0 && local.y == 0;
    int2 grain = sample_grain(block, local, int2(0, 0));
    if (vertical)
    {
        int2 top = sample_grain(block, local, int2(0, 1));
        if (horizontal)
        {
            top = blend_uv(sample_grain(block, local, int2(1, 1)), top);
            grain = blend_uv(sample_grain(block, local, int2(1, 0)), grain);
        }
        grain = blend_uv(top, grain);
    }
    else if (horizontal)
    {
        grain = blend_uv(sample_grain(block, local, int2(1, 0)), grain);
    }
    int2 lp = pixel << 1;
    int left = int(source_y.Load(int3(lp, 0)) >> storage_shift);
    int right = int(source_y.Load(int3(min(lp.x + 1, int(width) - 1), lp.y, 0)) >> storage_shift);
    int avg = (left + right + 1) >> 1;
    return uint2(apply_plane(stored.x, avg, grain.x, cb_params, 1),
                 apply_plane(stored.y, avg, grain.y, cr_params, 2));
}
