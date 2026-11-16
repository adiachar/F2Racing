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
    #include "UnityCG.cginc"
    #include "Grade.cginc"
    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
        float2 uv2 : TEXCOORD2;
        float4 color : COLOR;
    };
    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        half4 color : TEXCOORD1; // rgb baked light x tint, a = back-light
        UNITY_FOG_COORDS(2)
    };
    sampler2D _MainTex;
    fixed _Cutoff;
    float4 _Wind, _SunDir, _Trans;

    // how much the view looks into the sun (0 .. strength): a narrow lobe round the sun's direction
    half BackLight(float3 wp)
    {
        half b = saturate(dot(normalize(wp - _WorldSpaceCameraPos.xyz), _SunDir.xyz) * 1.4 - 0.4);
        return b * b * _Trans.w;
    }
    v2f vertF (appdata v, float sway)
    {
        v2f o;
        float4 wp = mul(unity_ObjectToWorld, v.vertex);
        // two sines at unrelated rates per tree (phase), plus a slow gust travelling across the land
        float t = _Time.y * _Wind.w + v.uv2.y;
        float gust = 0.6 + 0.4 * sin(_Time.y * 0.35 + wp.x * 0.013 + wp.z * 0.009);
        float s = (sin(t) + 0.45 * sin(t * 2.31 + 1.7)) * gust * v.uv2.x * _Wind.z * sway;
        wp.xz += _Wind.xy * s;
        o.pos = mul(UNITY_MATRIX_VP, wp);
        o.uv = v.uv;
        o.color = half4(v.color.rgb * 2.0, BackLight(wp.xyz)); // stored light 0.5 = full brightness (LightBake)
        UNITY_TRANSFER_FOG(o, o.pos);
        return o;
    }
    // lit leaf colour: baked light, plus the light through the leaves when looking towards the sun (the brighter,
    // outer leaves of the painted crown are the thin ones: they glow, the dark interior does not)
    half3 Shade (fixed4 t, half4 col)
    {
        half thin = saturate(dot(t.rgb, half3(0.3, 0.59, 0.11)) * 7.0 - 0.15);
        return t.rgb * (col.rgb + _Trans.rgb * (col.a * thin));
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
            #pragma multi_compile_fog
            v2f vert (appdata v) { return vertF(v, 1.0); }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv); // (no negative bias: cut-outs would crawl)
                // alpha sharpened to a one-pixel ramp centred on the cut-off: the coverage of each MSAA sample
                half a = (t.a - _Cutoff) / max(fwidth(t.a), 0.0001) + 0.5;
                clip(a);
                half4 c = half4(Shade(t, i.color), saturate(a));
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
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
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile_fog
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = half4(v.color.rgb * 2.0, BackLight(mul(unity_ObjectToWorld, v.vertex).xyz));
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2Dbias(_MainTex, float4(i.uv, 0, 0.5));
                clip(t.a - _Cutoff);
                half4 c = half4(Shade(t, i.color), 1);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "F2/BakedCutout"
}
