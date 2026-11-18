// GPU-instanced model trees and shrubs (ModelInstancing.cs): the same shading as Foliage.shader (Foliage.cginc), but one
// shared mesh per model and LOD drawn with Graphics.DrawMeshInstanced instead of every tree's vertices merged into big
// static meshes. What the merged meshes carried per vertex comes from elsewhere:
//  * the baked light x tint (LightBake.cs + the leaf / wood tint, exactly the values the merged meshes got) is read in
//    the vertex shader from _BakeTex, an RGBA half-float texture 2048 texels wide: texel = instance base + vertex index
//    (uv1.x), row-major, point-sampled. (A per-instance colour cannot do it: the bake shades every leaf clump on its own,
//    crown self-shadowing and the ground below included.) Vertex textures work on GLES3, Vulkan, Metal and WebGL2.
//  * uv1.y = the sway weight (height share squared, as the merged meshes' uv2.x for a sway of 1); per instance:
//  * _Inst.x = the instance's first texel in _BakeTex for this LOD, .y = sway phase, .z = sway amount (1 trees,
//    0.5 shrubs) + 4 x species (the combined impostor mesh only), .w = LOD cross-fade: 1 = whole, f in (0,1) = the
//    pixels whose screen dither is under f (the coarser LOD fading in), -f = those whose dither is at or above 1 - f (the
//    finer LOD fading out): the two together cover every pixel exactly once.
//  * the impostor mesh holds the two-card impostors of all five tree species one after the other (uv1.z = species,
//    -1 on every other mesh); an instance keeps only its own species' block and collapses the rest to a point outside
//    the clip volume, so the fallback impostors of every species are one instanced draw.
// Floats only in every compare / step (half compares fail silently on some GLES3 / Vulkan compilers).
Shader "F2/FoliageInst"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _BakeTex ("Baked light per instance vertex", 2D) = "gray" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Wind ("Wind: dir x, dir z, amplitude m, speed", Vector) = (0.8, 0.6, 0.22, 1.3)
        _SunDir ("Towards the sun (world)", Vector) = (0.4, 0.6, -0.5, 0)
        _Trans ("Back-light: tint rgb, strength", Vector) = (1.2, 1.4, 0.5, 0.9)
    }
    CGINCLUDE
    #define F2_FOLIAGE_INST 1
    #include "Foliage.cginc"
    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
        float4 uv1 : TEXCOORD1; // x vertex index, y sway weight, z species (impostor mesh) or -1
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };
    sampler2D _BakeTex;
    float4 _BakeTex_TexelSize; // (1/2048, 1/rows, 2048, rows)
    UNITY_INSTANCING_BUFFER_START(FoliageProps)
        UNITY_DEFINE_INSTANCED_PROP(float4, _Inst) // float4 only (half members in an instanced buffer break on some mobile drivers)
    UNITY_INSTANCING_BUFFER_END(FoliageProps)

    v2f vertI (appdata v, float sway)
    {
        v2f o;
        UNITY_INITIALIZE_OUTPUT(v2f, o);
        UNITY_SETUP_INSTANCE_ID(v);
        float4 inst = UNITY_ACCESS_INSTANCED_PROP(FoliageProps, _Inst);
        float species = floor(inst.z * 0.25);
        float swayK = inst.z - 4.0 * species;
        // this vertex's texel (exact in float: indices stay far under 2^24, and the row width is a power of two)
        float idx = inst.x + v.uv1.x;
        float row = floor(idx * _BakeTex_TexelSize.x);
        float col = idx - row * _BakeTex_TexelSize.z;
        float4 bake = tex2Dlod(_BakeTex, float4((col + 0.5) * _BakeTex_TexelSize.x, (row + 0.5) * _BakeTex_TexelSize.y, 0, 0));
        o.uv = v.uv;
        FoliageVert(mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)), bake.rgb, v.uv1.y * swayK, inst.y, sway, o);
        // impostor mesh: another species' block collapses to one point outside the clip volume (no triangle left)
        float other = step(0.5, abs(v.uv1.z - species)) * step(-0.5, v.uv1.z);
        o.pos = lerp(o.pos, float4(0.0, 0.0, 2.0, 1.0), other);
        o.fade = inst.w;
        return o;
    }
    // the LOD cross-fade: a fixed screen-space dither (interleaved gradient noise), complementary for the two LODs
    void CrossFade(v2f i)
    {
        float d = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));
        float dd = lerp(1.0 - d, d, step(0.0, i.fade));
        clip(abs(i.fade) - dd);
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
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap nolodfade
            #pragma multi_compile __ FOG_LINEAR
            v2f vert (appdata v) { return vertI(v, 1.0); }
            fixed4 frag (v2f i) : SV_Target
            {
                half4 c = FoliageFragHi(i);
                CrossFade(i); // (after the shading: its fwidth()s must not follow a discard)
                return c;
            }
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
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap nolodfade
            #pragma multi_compile __ F2_GRADE_LOW
            #pragma multi_compile __ FOG_LINEAR
            // Low (Foliage.shader LOD 150): no sway; the world position is still needed for the back-light
            v2f vert (appdata v) { return vertI(v, 0.0); }
            fixed4 frag (v2f i) : SV_Target
            {
                half4 c = FoliageFragLow(i);
                CrossFade(i); // (after the texture read: no implicit derivatives after a discard)
                return c;
            }
            ENDCG
        }
    }
}
