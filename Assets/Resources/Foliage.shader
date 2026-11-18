// Tree cards (Environment.Trees, RealWorld.Trees, the forest-edge bands): alpha-tested crossed quads from one atlas
// (EnvTex.Foliage), both faces drawn, lighting baked into the vertex colours (LightBake.cs) like Baked.shader: the cards'
// hand-made normals make the sun side of a crown bright and the far side and the foot shaded; the atlas paints the
// crown's own inner shade. One texture read per pixel.
// uv2 = (sway weight: 0 at the foot .. ~1 at the crown, phase): on Medium/High (LOD 300) the crowns sway in a light
// breeze in the vertex shader; Low (LOD 150) has no sway and a positive mip bias. EnvSurface.cs picks the level.
// Back-light: looking towards the sun the light shines through the leaves (strongest in the thin, bright outer
// leaves), a warm yellow-green glow (_SunDir / _Trans, set by Environment.FoliageMaterial; off at night).
// Edges (Medium/High): alpha to coverage. The alpha is sharpened to a one-pixel ramp at the cut-off (fwidth), so with
// MSAA the silhouettes are anti-aliased instead of stair-stepped and crawling; without MSAA the coverage is ignored
// and the clip below keeps the usual hard cut-out (half a texel fuller). The atlas's mips keep each card's coverage.
// Mid distance (Medium/High): the cut-off follows the painted shade (gaps between clumps open), and the crowns' colour
// drifts in ~2.5 m patches per vertex, so tree lines do not read as smooth blobs.
Shader "F2/Foliage"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Wind ("Wind: dir x, dir z, amplitude m, speed", Vector) = (0.8, 0.6, 0.22, 1.3)
        _SunDir ("Towards the sun (world)", Vector) = (0.4, 0.6, -0.5, 0)
        _Trans ("Back-light: tint rgb, strength", Vector) = (1.2, 1.4, 0.5, 0.9)
    }
    CGINCLUDE
    // the shading itself lives in Foliage.cginc, shared with FoliageInst.shader (the GPU-instanced model trees)
    #include "Foliage.cginc"
    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
        float2 uv2 : TEXCOORD2;
        float4 color : COLOR;
    };
    v2f vertF (appdata v, float sway)
    {
        v2f o;
        UNITY_INITIALIZE_OUTPUT(v2f, o);
        o.uv = v.uv;
        FoliageVert(mul(unity_ObjectToWorld, v.vertex), v.color.rgb, v.uv2.x, v.uv2.y, sway, o);
        return o;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 300
        Cull Off
        AlphaToMask On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            v2f vert (appdata v) { return vertF(v, 1.0); }
            fixed4 frag (v2f i) : SV_Target { return FoliageFragHi(i); }
            ENDCG
        }
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 150
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = half4(v.color.rgb * 2.0, BackLight(mul(unity_ObjectToWorld, v.vertex).xyz));
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target { return FoliageFragLow(i); }
            ENDCG
        }
    }
    Fallback "F2/BakedCutout"
}
