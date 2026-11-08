// The only floating-point math is clip-space geometry; pixel/grain math is integer-only.
float4 main(uint vertex : SV_VertexID) : SV_Position
{
    // Clockwise fullscreen triangle. No input layout, vertex buffer or interpolants.
    int2 corner = int2((vertex << 1) & 2, vertex & 2);
    return float4(corner.x * 2 - 1, 1 - corner.y * 2, 0, 1);
}
