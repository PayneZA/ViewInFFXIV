// Draws a window's picture scaled into the TV texture. The viewport is the letterbox rectangle; one triangle
// covers it, and the trilinear sampler reads the mip level that matches the scale (no shimmering text).

Texture2D Picture : register(t0);
SamplerState Linear : register(s0);

struct VsOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOut vs(uint id : SV_VertexID)
{
    VsOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4((o.uv * float2(2, -2)) + float2(-1, 1), 0, 1);
    return o;
}

float4 ps(VsOut i) : SV_Target
{
    return float4(Picture.Sample(Linear, i.uv).rgb, 1);
}
