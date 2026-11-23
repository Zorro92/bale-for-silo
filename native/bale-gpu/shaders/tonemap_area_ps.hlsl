#include "tonemap_common.hlsli"
// Downscale pass 1: the encoded area-downscaled value of each output pixel, once, into the
// R32G32B32A32_FLOAT intermediate (exact fp32, so the plane passes see the same value as before).
float4 main(float4 position : SV_Position) : SV_Target {
    return float4(output_pixel(int2(position.xy)), 0.0);
}
