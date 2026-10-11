// The wall screen: bezel and picture in one draw.
// Compiled to Wall.vs.cso / Wall.ps.cso (see ViewInFFXIV.csproj, target CompileWallShaders). The .cso files are
// committed: the embedded-resource list is built before the compile step runs, so a checkout without them would
// build a plugin with no shaders (and fall back to the basic renderer).
//
// The constant buffer must match WallRenderer.Constants field for field (16-byte rows; matrices row_major, filled
// from System.Numerics matrices as they are). Add new fields at the end on both sides.
//
// Maintainer guide, including what each GAME ASSUMPTION means: docs/renderer.md.

cbuffer Wall : register(b0)
{
    row_major float4x4 viewProj;
    float3 center;      float nearPlane;
    float3 axisRight;   float worldTolerance; // axisRight/axisDown: from the centre to the outer edge, in metres
    float3 axisDown;    float hasVideo;
    float2 outerHalf;   float2 innerHalf;  // half sizes in metres: whole screen, picture
    float2 depthScale;  float2 depthMax;   // target pixel -> depth texel, last valid texel
    float outerRadius;  float innerRadius; float useDepth; float opacity;
    float2 infoScale;   float2 infoMax;    // target pixel -> scene-info texel, last valid texel
    float characterTolerance; float useInfo; float debugView; float hideWorldUi;
    row_major float4x4 invViewProj;
    float2 targetSize;  float2 padding2;
    float3 cameraPos;   float padding3;
};

Texture2D<float4> video : register(t0);
Texture2D<float> sceneDepth : register(t1);
Texture2D<float4> sceneInfo : register(t2); // GAME ASSUMPTION A7: G-buffer 3, black on characters (and sky), coloured on scenery
Texture2D<float> liveDepth : register(t3); // /matinee rprobe only: the game's own depth, next to the copy in t1
SamplerState videoSampler : register(s0);

// Slightly larger than the screen so the outer edge can fade over a pixel instead of being cut by the raster.
static const float Margin = 1.02;

struct VSOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0; // metres from the centre, +y down
    float3 world : TEXCOORD1;
};

struct PSOut
{
    float4 color : SV_Target;
    float depth : SV_Depth;
};

VSOut vs(uint id : SV_VertexID)
{
    float2 corner = float2(id & 1, id >> 1) * 2.0 - 1.0;
    float3 world = center + (corner.x * axisRight + corner.y * axisDown) * Margin;

    VSOut o;
    o.pos = mul(float4(world, 1.0), viewProj);
    o.local = corner * outerHalf * Margin;
    o.world = world;
    return o;
}

float roundedBox(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// Characters hide the screen as soon as they are in front of it. Scenery only when it is more than
// worldTolerance in front: a screen hung over furniture or set slightly into a wall stays whole.
float toleranceAt(float2 pixel)
{
    if (useInfo < 0.5)
        return worldTolerance;
    int2 texel = clamp(int2(pixel * infoScale), int2(0, 0), int2(infoMax));
    float3 info = sceneInfo.Load(int3(texel, 0)).rgb;
    return max(info.r, max(info.g, info.b)) < 0.05 ? characterTolerance : worldTolerance;
}

// 1 where the screen shows at this depth texel, 0 where something hides it.
// GAME ASSUMPTION A6: reverse-Z with an infinite far plane, so view distance = near / depth and the sky is 0.
// "In front" is measured along the screen's own facing direction, so the result does not change with the viewing
// angle: a screen set 20 cm into a wall stays 20 cm into it seen from the side.
float visibleAt(int2 texel, float pixelDistance, float3 normal, float tolerance)
{
    texel = clamp(texel, int2(0, 0), int2(depthMax));
    float scene = sceneDepth.Load(int3(texel, 0));
    if (scene <= 1e-7)
        return 1.0; // sky
    if (nearPlane / scene >= pixelDistance * 0.998)
        return 1.0; // the scene is behind the screen here

    float2 pixel = (float2(texel) + 0.5) / depthScale;
    float2 ndc = float2(pixel.x / targetSize.x * 2.0 - 1.0, 1.0 - pixel.y / targetSize.y * 2.0);
    float4 h = mul(float4(ndc, scene, 1.0), invViewProj);
    float3 world = h.xyz / h.w;
    return abs(dot(world - center, normal)) <= tolerance ? 1.0 : 0.0;
}

float occlusion(float4 pos)
{
    if (useDepth < 0.5)
        return 1.0;

    // Bilinear blend of four depth tests: soft, stable edges when the depth buffer is lower resolution.
    float pixelDistance = nearPlane / max(pos.z, 1e-7);
    float3 normal = normalize(cross(axisRight, axisDown));
    float2 t = pos.xy * depthScale - 0.5;
    int2 b = int2(floor(t));
    float2 f = frac(t);
    float tolerance = toleranceAt(pos.xy);
    float a = lerp(visibleAt(b, pixelDistance, normal, tolerance), visibleAt(b + int2(1, 0), pixelDistance, normal, tolerance), f.x);
    float c = lerp(visibleAt(b + int2(0, 1), pixelDistance, normal, tolerance), visibleAt(b + int2(1, 1), pixelDistance, normal, tolerance), f.x);
    return lerp(a, c, f.y);
}

float3 screenOff(float2 uv)
{
    // A switched-off panel: near black with a faint diagonal sheen.
    float sheen = saturate(1.0 - abs(uv.x * 0.8 + uv.y - 0.55) * 2.5) * (1.0 - uv.y * 0.7);
    return 0.012 + sheen * 0.03;
}

// The depth the screen writes (when it writes depth): pulled toward the camera by the cover distance along the
// screen's facing direction, so what the screen covers (furniture in front of it, and the markers over that
// furniture) stays behind it for everything the game draws afterwards with depth testing.
float coverDepth(VSOut i, float distance)
{
    float3 normal = normalize(cross(axisRight, axisDown));
    if (dot(normal, cameraPos - i.world) < 0.0)
        normal = -normal;
    float4 clip = mul(float4(i.world + normal * distance, 1.0), viewProj);
    // Pulled past the camera: everything in front of the screen is covered.
    return clip.w > nearPlane ? saturate(clip.z / clip.w) : 1.0;
}

PSOut ps(VSOut i)
{
    PSOut o;
    o.depth = i.pos.z;
    float outer = roundedBox(i.local, outerHalf, outerRadius);
    float coverage = saturate(0.5 - outer / max(fwidth(outer), 1e-6));
    if (coverage <= 0.0)
        discard;

    float inner = roundedBox(i.local, innerHalf, innerRadius);
    float picture = saturate(0.5 - inner / max(fwidth(inner), 1e-6));

    // Bezel: soft top-to-bottom falloff, a thin highlight on the outer rim and a shadowed lip at the picture.
    float thickness = max(outerHalf.x - innerHalf.x, 1e-4);
    float v = saturate(i.local.y / outerHalf.y * 0.5 + 0.5);
    float3 bezel = lerp(float3(0.082, 0.082, 0.088), float3(0.028, 0.028, 0.032), v);
    bezel += saturate(1.0 + outer / (thickness * 0.3)) * 0.05;
    bezel *= 1.0 - saturate(1.0 - inner / (thickness * 0.3)) * 0.45;

    float2 uv = i.local / innerHalf * 0.5 + 0.5;
    float3 content = hasVideo > 0.5 ? video.Sample(videoSampler, uv).rgb : screenOff(uv);

    float3 color = lerp(bezel, content, picture);
    if (debugView > 1.5)
    {
        // Debug: what the occlusion test reads, raw. Red = the depth texel is not sky; green = scene depth * 20;
        // blue = the screen's own depth * 20. Reverse-Z: bigger is nearer. Green above blue = scene in front.
        int2 texel = clamp(int2(i.pos.xy * depthScale), int2(0, 0), int2(depthMax));
        float scene = sceneDepth.Load(int3(texel, 0));
        o.color = float4(scene > 1e-7 ? 1.0 : 0.0, saturate(scene * 20.0), saturate(i.pos.z * 20.0), coverage);
        return o;
    }
    if (debugView > 0.5)
    {
        // Debug: what the occlusion test sees behind each pixel. Red = character, green = scenery (G-buffer 3 shown dimmed).
        int2 texel = clamp(int2(i.pos.xy * infoScale), int2(0, 0), int2(infoMax));
        float3 info = sceneInfo.Load(int3(texel, 0)).rgb;
        bool character = max(info.r, max(info.g, info.b)) < 0.05;
        o.color = float4(character ? float3(1, 0, 0) : lerp(float3(0, 0.6, 0), saturate(info), 0.5), coverage);
        return o;
    }
    float alpha = coverage * opacity * occlusion(i.pos);
    // Where nothing shows, write nothing (not even depth, when the screen writes depth).
    if (alpha < 0.02)
        discard;
    o.color = float4(color, alpha);
    // Hiding name plates and markers over the screen: the nearest depth, so nothing the game draws afterwards with
    // depth testing (its name plate pass) shows on it. Otherwise the screen behaves like a real object.
    // GAME ASSUMPTION A8: name plates and housing markers are depth-tested; chat bubbles and the HUD are not.
    o.depth = hideWorldUi > 0.5 ? 1.0 : coverDepth(i, worldTolerance);
    return o;
}

// /matinee rprobe: one pixel per sample of a 64x36 grid laid over the depth texture. Writes the game's own depth (R)
// and our copy of it (G) as plain floats into an ordinary colour target, the same on every GPU driver, and the
// texel it read (B, A). The CPU reads that target back; it never touches the depth texture's memory layout.
float4 probeVs(uint id : SV_VertexID) : SV_Position
{
    float2 corner = float2(id & 1, id >> 1) * 2.0 - 1.0;
    return float4(corner, 0.0, 1.0);
}

float4 probePs(float4 pos : SV_Position) : SV_Target
{
    uint w, h;
    liveDepth.GetDimensions(w, h);
    float2 cell = floor(pos.xy);
    int2 texel = clamp(int2((cell + 0.5) / float2(64.0, 36.0) * float2(w, h)), int2(0, 0), int2(w - 1, h - 1));
    return float4(liveDepth.Load(int3(texel, 0)), sceneDepth.Load(int3(texel, 0)), texel.x, texel.y);
}
