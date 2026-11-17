#include "tonemap_common.hlsli"
float2 main(float4 position : SV_Position) : SV_Target {
    int2 p = int2(position.xy)*2;
    float2 uv = float2(0,0);
    [unroll] for (int dy=0;dy<2;dy++) {
        uv += 0.125*sample_pixel(p+int2(-1,dy)).yz;
        uv += 0.25*sample_pixel(p+int2(0,dy)).yz;
        uv += 0.125*sample_pixel(p+int2(1,dy)).yz;
    }
    return params.w > 0.5 ? quantize_chroma10(uv)*64.0/65535.0 : quantize_chroma(uv)/255.0;
}
