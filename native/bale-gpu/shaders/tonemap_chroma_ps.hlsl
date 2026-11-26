#include "tonemap_common.hlsli"
// Output chroma is left-sited (what the output declares): sample (i, j) sits at output luma
// position (2i, 2j + 0.5). Its footprint is the 2x2-luma area centred there: 1/4, 1/2, 1/4 over
// columns 2i-1..2i+1 and 1/2, 1/2 over rows 2j, 2j+1. The footprint mixes the output pixels' LINEAR
// RGB, then clips and encodes once, so saturated fine detail keeps its brightness and hue (an
// average of encoded Cb/Cr darkens red/green detail). legacy_math mixes encoded Cb/Cr instead.
float2 main(float4 position : SV_Position) : SV_Target {
    int2 p = int2(position.xy)*2;
    float3 acc = float3(0,0,0);
    [unroll] for (int dy=0;dy<2;dy++) {
        acc += 0.125*AREA_VALUE(p+int2(-1,dy));
        acc += 0.25*AREA_VALUE(p+int2(0,dy));
        acc += 0.125*AREA_VALUE(p+int2(1,dy));
    }
    float2 uv = legacy_math() ? acc.yz : encode_output(acc).yz;
    // The dither pattern is indexed by the chroma sample's own position in its half-size plane (R2: Cr
    // at the transposed position, as rav1g).
    return params.w > 0.5 ? quantize_chroma10(uv)*64.0/65535.0
                          : quantize_chroma8(uv, uint2(position.xy))/255.0;
}
