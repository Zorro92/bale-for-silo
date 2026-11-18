#include "tonemap_common.hlsli"
float main(float4 position : SV_Position) : SV_Target {
    float y = output_pixel(int2(position.xy)).x;
    // params.w: 0 = NV12 (8-bit UNORM views), 1 = P010 (10-bit code in the high bits of R16_UNORM).
    return params.w > 0.5 ? quantize_luma10(y)*64.0/65535.0 : quantize_luma(y)/255.0;
}
