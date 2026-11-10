// Subtitle overlay: premultiplied colour times the image's 8-bit coverage. Quads map texels to
// pixels 1:1, so the atlas is read with Load (a point sample without a sampler or filtering).
Texture2D<float> atlas : register(t0);

struct Input
{
    float4 position : SV_Position;
    float2 texel : TEXCOORD0;
    nointerpolation float4 color : COLOR0;
};

float4 main(Input input) : SV_Target
{
    float coverage = atlas.Load(int3((int2)input.texel, 0));
    return input.color * coverage;
}
