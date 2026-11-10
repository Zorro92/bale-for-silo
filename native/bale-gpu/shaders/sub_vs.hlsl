// Subtitle overlay: one instanced quad per libass image. No vertex buffer for the corners; the
// quad comes from SV_VertexID (triangle strip 0..3) and the per-instance rectangle.
cbuffer Frame : register(b0)
{
    // 2 / frame width, 2 / frame height (pixels to clip space).
    float2 clip_scale;
    float2 unused;
};

struct Input
{
    float4 rect : RECT;      // destination x, y, width, height in frame pixels
    float2 atlas : ATLAS;    // top-left texel of the bitmap in the atlas
    float4 color : COLOR;    // premultiplied RGBA
    uint vertex : SV_VertexID;
};

struct Output
{
    float4 position : SV_Position;
    float2 texel : TEXCOORD0;
    nointerpolation float4 color : COLOR0;
};

Output main(Input input)
{
    float2 corner = float2((float)(input.vertex & 1u), (float)(input.vertex >> 1u));
    float2 offset = corner * input.rect.zw;
    float2 pixel = input.rect.xy + offset;
    Output output;
    output.position = float4(pixel.x * clip_scale.x - 1.0f, 1.0f - pixel.y * clip_scale.y, 0.0f, 1.0f);
    output.texel = input.atlas + offset;
    output.color = input.color;
    return output;
}
