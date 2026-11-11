// Soft sun glare for phones (see Sky.cs): a camera-facing quad, one texture read, additive. It is pushed to the
// far plane in the vertex shader, so trees, buildings and the Monaco tunnel roof hide it, and it only fills the
// sky pixels around the sun. Brightness (_Color) is set from script: it fades as the sun leaves the screen.
Shader "F2/SunGlare"
{
    Properties
    {
        _MainTex ("Glare (A)", 2D) = "white" {}
        _Color ("Colour x intensity", Color) = (1, 0.9, 0.75, 1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent-90" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            half4 _Color;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                #if defined(UNITY_REVERSED_Z)
                o.pos.z = o.pos.w * 0.000001;
                #else
                o.pos.z = o.pos.w * 0.999999;
                #endif
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                half g = tex2D(_MainTex, i.uv).a;
                return fixed4(_Color.rgb * (g * _Color.a), 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
