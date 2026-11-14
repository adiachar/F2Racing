// Particles of the car effects (CarModel): plank sparks (additive) and tyre smoke (alpha blended). No texture: a
// soft round dot from the quad's uv, tinted by the particle colour. Blend mode set per material (_Src, _Dst).
Shader "F2/CarFx"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.BlendMode)] _Src ("Src", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _Dst ("Dst", Float) = 10
        _Soft ("Edge softness power", Float) = 1.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend [_Src] [_Dst]
            ZWrite Off Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            half _Soft, _Dst;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; UNITY_FOG_COORDS(1) };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.color = v.color; o.uv = v.uv * 2 - 1; UNITY_TRANSFER_FOG(o, o.pos); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                half a = pow(saturate(1.0 - length(i.uv)), _Soft);
                fixed4 c = fixed4(i.color.rgb, i.color.a * a);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = _Dst == 1 ? F2GradeAdd(c.rgb) : F2Grade(c.rgb); // sparks add light; smoke is graded like the world
                return c;
            }
            ENDCG
        }
    }
}
