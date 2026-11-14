#include "tonemap_common.hlsli"
float main(float4 position : SV_Position) : SV_Target {
    return quantize_luma(sample_pixel(int2(position.xy)).x)/255.0;
}
