// Ground for phones: grass, terrain, verges with mowing stripes, gravel traps with tyre ruts (Ground.cginc).
// Lighting is baked into the vertex colours (LightBake.cs); two texture reads per pixel at most.
// Two levels: LOD 300 (Medium/High) = detail + macro, wandering stripes, barrier wear, gravel ruts / rakes / stones /
//                                     damp edges, grass clumps / patches / long grass at the barrier / worn soil at the
//                                     verge edge (detail alpha), run-off strip (_Turf: astroturf fibres or green / blue
//                                     painted run-off, tyre marks crossing it);
//             LOD 150 (Low)         = the same reads with a positive mip bias, stripes, damp gravel edges, the run-off
//                                     strip's paint bands and worn edges (no ruts, rakes, stones or marks).
// EnvSurface.cs picks one from the graphics level.
Shader "F2/Ground"
{
    Properties
    {
        _MainTex ("Detail (world space)", 2D) = "white" {}
        _MacroTex ("Macro (R brightness, G dry, B clumps)", 2D) = "gray" {}
        _MacroScale ("Macro tiles per metre", Float) = 0.0105
        _MipBias ("Detail mip bias (>= 0)", Float) = 0
        _Gain ("Photo albedo gain (linear rgb)", Vector) = (1, 1, 1, 1)
        _DryTint ("Dry patch tint (a = amount)", Color) = (1.2, 1.08, 0.72, 0.5)
        _Stripe ("Stripes: from m, to m, width m, strength", Vector) = (0, 0, 3, 0)
        _Wall ("Barrier: line m, darkening", Vector) = (-100, 0, 0, 0)
        _Ruts ("Ruts: inner edge m, strength", Vector) = (1000, 0, 0, 0)
        _Clumps ("Grass clumps from the detail alpha (0 off, 1 on)", Float) = 0
        _Turf ("Run-off strip behind the kerbs: turf / painted run-off (0 off, 1 on)", Float) = 0
        _Forest ("Forest-floor mask in uv3.y (1 only where the mesh has it: RealWorld.VergeFloor)", Float) = 0
        _RunGreen ("Run-off paint green", Color) = (0.25, 0.48, 0.29, 1)
        _RunBlue ("Run-off paint blue", Color) = (0.19, 0.33, 0.58, 1)
    }
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
            #define GROUND_HQ 1
            #include "Ground.cginc"
            ENDCG
        }
    }
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
            #include "Ground.cginc"
            ENDCG
        }
    }
    Fallback "F2/Baked"
}
