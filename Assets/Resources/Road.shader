// Road surface for phones: asphalt, rubbered racing line, painted lines, kerbs, grid slots and the start line,
// all in one material and one draw call. Lighting is baked into the vertex colours (LightBake.cs), as in Baked.shader.
//
// Mesh data (WorldBuilder builds it):
//   uv0 = (lateral offset from the centreline in metres, + = left; distance along the track in metres)
//   uv2 = road:  (racing-line offset at this row in metres, rubber amount 0..1)
//         kerb:  (0 at the track edge .. 1 at the outer lip, -1)   <- y < -0.5 marks a kerb vertex
// Paint edges are drawn with box-filtered maths in metres (fwidth), so they stay crisp near and do not
// shimmer far away, whatever the screen resolution.
//
// Two levels: LOD 300 (Medium/High) = 3 texture reads (5 m detail, 43 m macro, 1.4 m fine aggregate from the global
//                                     _F2RoadFine) + the shadow mask, sun sheen with glinting stones, tar seams, rubber streaks, Fresnel,
//                                     stone-by-stone sun relief, tyre marks, sealed cracks and paver joints, kerb paint wear;
//             LOD 150 (Low)         = 1 texture read + the shadow mask, no sheen; section shades, patches and kerb relief are maths only.
// Both levels also read the sharp sun-shadow mask (_F2RoadShadow, RoadShadow.cs): trees, stands, bridges, fences.
// RoadSurface.cs picks one from the graphics level and sets _F2RoadFine.
Shader "F2/Road"
{
    Properties
    {
        _MainTex ("Asphalt (R grain, G blotches, B patches, A stones)", 2D) = "gray" {}
        _Asphalt ("Asphalt colour", Color) = (0.34, 0.34, 0.35, 1)
        _Dust ("Dusty edge colour", Color) = (0.50, 0.48, 0.45, 1)
        _Paint ("Paint", Color) = (0.93, 0.93, 0.90, 1)
        _KerbRed ("Kerb red", Color) = (0.70, 0.14, 0.13, 1)
        _HalfWidth ("Half track width (m)", Float) = 6
        _Grid ("Grid: first slot (m), pitch (m), slots, slot offset (m)", Vector) = (0, 8, 0, 1.8)
        _SunDir ("Towards the sun", Vector) = (0, 1, 0, 0)
        _SunCol ("Sun sheen colour", Color) = (1, 0.95, 0.86, 1)
        _MipBias ("Detail mip bias", Float) = 0.3
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
            #pragma multi_compile_fog
            #pragma target 3.0
            #define ROAD_HQ 1
            #include "Road.cginc"
            ENDCG
        }
    }
    // Low: one texture read, no sheen
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
            #pragma multi_compile_fog
            #pragma target 3.0
            #include "Road.cginc"
            ENDCG
        }
    }
    Fallback "F2/Baked"
}
