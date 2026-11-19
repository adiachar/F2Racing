// Rain streaks and tyre spray (WeatherFx.cs): particles, alpha blended, one texture read, vertex colour from the
// particle system, fogged. No lighting: the particles take a grey from the weather's light (set in the colour).
// _Fade (WeatherFx.cs): a fade by camera distance (no puff or streak fills the lens) and, for the spray, a soft fade
// towards the road, whose height comes in TEXCOORD0.z (the particle's Custom1.x): no depth texture, no hard edge.
Shader "F2/WeatherFx"
{
    Properties
    {
        _MainTex ("Shape (A)", 2D) = "white" {}
        _Fade ("Cam fade start, 1/len, ground 1/height", Vector) = (0, 1000, 0, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; float4 _Fade;
            struct appdata { float4 vertex : POSITION; float3 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float2 fade : TEXCOORD2; UNITY_FOG_COORDS(1) };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.fade = float2(distance(wp, _WorldSpaceCameraPos.xyz), wp.y - v.uv.z); // camera distance, height above the road
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = i.color;
                float cf = saturate((i.fade.x - _Fade.x) * _Fade.y);
                float gf = _Fade.z > 0.0 ? saturate(i.fade.y * _Fade.z) : 1.0;
                c.a *= tex2D(_MainTex, i.uv).a * cf * gf;
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
