#include "tonemap_common.hlsli"
float main(float4 position : SV_Position) : SV_Target {
    float y = encode_area_value(AREA_VALUE(int2(position.xy))).x;
    // params.w: 0 = NV12 (8-bit UNORM views, ordered-dithered), 1 = P010 (10-bit code in the high bits
    // of R16_UNORM, plainly rounded as before).
    return params.w > 0.5 ? quantize_luma10(y)*64.0/65535.0
                          : quantize_luma_dithered(y, uint2(position.xy))/255.0;
}
