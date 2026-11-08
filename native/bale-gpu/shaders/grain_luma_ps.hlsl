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

int sample_grain(int2 block, int2 local, int2 previous)
{
    uint r = offsets.Load(int3(block - previous, 0));
    int2 origin = 3 + 2 * (3 + int2(r >> 4, r & 15));
    return template_y.Load(int3(origin + local + 32 * previous, 0));
}

uint main(float4 position : SV_Position) : SV_Target
{
    int2 pixel = int2(position.xy);
    uint stored = source_y.Load(int3(pixel, 0));
    if (pixel.x >= int(width) || pixel.y >= int(height) || luma_active == 0)
        return stored;

    int2 block = pixel >> 5;
    int2 local = pixel & 31;
    bool horizontal = overlap != 0 && block.x > 0 && local.x < 2;
    bool vertical = overlap != 0 && block.y > 0 && local.y < 2;
    int2 wx = local.x == 0 ? int2(27, 17) : int2(17, 27);
    int2 wy = local.y == 0 ? int2(27, 17) : int2(17, 27);
    int grain = sample_grain(block, local, int2(0, 0));
    if (vertical)
    {
        // dav1d's corner order: top horizontal, current horizontal, vertical.
        // Each blend clips independently, including both horizontal intermediates.
        int top = sample_grain(block, local, int2(0, 1));
        if (horizontal)
        {
            top = blend(sample_grain(block, local, int2(1, 1)), top, wx);
            grain = blend(sample_grain(block, local, int2(1, 0)), grain, wx);
        }
        grain = blend(top, grain, wy);
    }
    else if (horizontal)
    {
        grain = blend(sample_grain(block, local, int2(1, 0)), grain, wx);
    }
    int src = int(stored >> storage_shift);
    int scale = int(scaling.Load(int3(src, 0, 0)));
    int value = clamp(src + round2(scale * grain, int(scaling_shift)), luma_min, luma_max);
    return uint(value) << storage_shift;
}
