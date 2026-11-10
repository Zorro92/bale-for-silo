// Subtitle overlay, image subtitles: one premultiplied RGBA bitmap per quad, scaled to the quad
// with bilinear filtering. It shares sub_vs.hlsl with the libass quads: the instance's atlas
// origin is 0, so `texel` runs over the quad in frame pixels, and its colour slot carries
// 1 / quad size in xy to turn that into a texture coordinate.
Texture2D<float4> bitmap : register(t0);
SamplerState bilinear : register(s0);

struct Input
{
    float4 position : SV_Position;
    float2 texel : TEXCOORD0;
    nointerpolation float4 color : COLOR0;
};

float4 main(Input input) : SV_Target
{
    return bitmap.Sample(bilinear, input.texel * input.color.xy);
}
