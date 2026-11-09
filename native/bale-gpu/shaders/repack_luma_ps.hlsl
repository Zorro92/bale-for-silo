#include "repack_common.hlsli"

uint main(float4 position : SV_Position) : SV_Target
{
    return (source_y.Load(int3(int2(position.xy), 0)) << geometry.x) & geometry.z;
}
