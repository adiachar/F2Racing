// Road surface for phones: asphalt, rubbered racing line, painted lines, kerbs, grid slots and the start line,
// all in one material and one draw call. Lighting is baked into the vertex colours (LightBake.cs), as in Baked.shader.
//
// Mesh data (WorldBuilder builds it):
//   uv0 = (lateral offset from the centreline in metres, + = left; distance along the track in metres)
//   uv2 = road:  (racing-line offset at this row in metres, rubber amount 0..1)
//         kerb:  (0 at the track edge .. 1 at the outer lip, -(1 + type + 0.45 x wear))   <- y < -0.5 marks a kerb
//                vertex; type 0 kerb, 1 stepped kerb, 2 sausage (WorldBuilder.KerbLayout)
//   uv3 = edge dirt (left, right) 0..1: dust film, gravel (WorldBuilder.EdgeDirt); on a kerb vertex the other side's
//         component holds the saw-tooth ridge amount
// Paint edges are drawn with box-filtered maths in metres (fwidth), so they stay crisp near and do not
// shimmer far away, whatever the screen resolution.
//
// Real Racing 3 look: calm, clean asphalt that stays still at 300 km/h (see Road.cginc for why and how).
// Two levels: LOD 300 (Medium/High) = 2 texture reads (_F2RoadMacro long tonal drifts, _F2RoadDetail fine grain that
//                                     fades with mip, distance and camera speed) + the shadow mask, soft rubber streaks
//                                     and tyre marks along the line, a broad smooth sun sheen, Fresnel sky;
//             LOD 150 (Low)         = 1 texture read (_F2RoadMacro) + the shadow mask, no grain, no sheen.
// Both levels also read the sharp sun-shadow mask (_F2RoadShadow, RoadShadow.cs): trees, stands, bridges, fences.
// RoadSurface.cs picks one from the graphics level and sets the globals _F2RoadMacro, _F2RoadDetail, _F2RoadMotion.
// (_MainTex is no longer read: WorldBuilder still assigns one, which is harmless; the Fallback uses it.)
Shader "F2/Road"
{
    Properties
    {
        _MainTex ("Unused (Fallback only)", 2D) = "gray" {}
        _Asphalt ("Asphalt colour", Color) = (0.34, 0.34, 0.35, 1)
        _Dust ("Dusty edge colour", Color) = (0.50, 0.48, 0.45, 1)
        _Paint ("Paint", Color) = (0.93, 0.93, 0.90, 1)
        _KerbRed ("Kerb red", Color) = (0.70, 0.14, 0.13, 1)
        _KerbYellow ("Sausage kerb yellow", Color) = (0.93, 0.74, 0.10, 1)
        _HalfWidth ("Half track width (m)", Float) = 6
        _Grid ("Grid: first slot (m), pitch (m), slots, slot offset (m)", Vector) = (0, 8, 0, 1.8)
        _Loops ("Timing loops: pitch (m), sector 1 end (m), sector 2 end (m)", Vector) = (100000, -1000, -1000, 0)
        _SunDir ("Towards the sun", Vector) = (0, 1, 0, 0)
        _SunCol ("Sun sheen colour", Color) = (1, 0.95, 0.86, 1)
    }

    // Medium / High
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 300
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            #pragma target 3.0
            #define ROAD_HQ 1
            #include "Road.cginc"
            ENDCG
        }
    }
    // Low: one texture read, no grain, no sheen
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 150
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            #pragma target 3.0
            #include "Road.cginc"
            ENDCG
        }
    }
    Fallback "F2/Baked"
}
