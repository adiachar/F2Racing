// Tree cards (Environment.Trees): alpha-tested crossed quads from one atlas (EnvTex.Foliage), both faces drawn,
// lighting baked into the vertex colours (LightBake.cs) like Baked.shader. One texture read per pixel.
// uv2 = (sway weight: 0 at the foot .. ~1 at the crown, phase): on Medium/High (LOD 300) the crowns sway in a light
// breeze in the vertex shader; Low (LOD 150) has no sway and a positive mip bias. EnvSurface.cs picks the level.
Shader "F2/Foliage"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Wind ("Wind: dir x, dir z, amplitude m, speed", Vector) = (0.8, 0.6, 0.22, 1.3)
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
        half3 color : TEXCOORD1;
        UNITY_FOG_COORDS(2)
    };
    sampler2D _MainTex;
    fixed _Cutoff;
    float4 _Wind;

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
        o.color = v.color.rgb * 2.0; // stored light 0.5 = full brightness (LightBake)
        UNITY_TRANSFER_FOG(o, o.pos);
        return o;
    }
    fixed4 fragF (v2f i, float bias)
    {
        fixed4 c = tex2Dbias(_MainTex, float4(i.uv, 0, bias));
        clip(c.a - _Cutoff);
        c.rgb *= i.color;
        UNITY_APPLY_FOG(i.fogCoord, c);
        c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
        c.a = 1;
        return c;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 300
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            v2f vert (appdata v) { return vertF(v, 1.0); }
            fixed4 frag (v2f i) : SV_Target { return fragF(i, 0.0); } // no negative bias: cut-outs would crawl
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
                o.color = v.color.rgb * 2.0;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target { return fragF(i, 0.5); }
            ENDCG
        }
    }
    Fallback "F2/BakedCutout"
}
