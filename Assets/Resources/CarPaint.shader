// Car paint for phones: one pass, no textures.
//  * base colour lit by the car's sun and the sky (spherical harmonics),
//  * a clear coat that reflects the sky probe, strongest at grazing angles (Fresnel),
//  * below the horizon the coat reflects the road instead of the sky, which gives the body its horizon line,
//  * a tight sun highlight, and a metallic tint for metal parts.
// _CarLit (global, 0..1) dims reflections when the car is in a baked shadow (tunnel, under buildings).
// Livery (_Livery = 1, body paint only): a second colour on the engine cover, the sidepod tops and the nose tip, with a
// thin accent line along its edge. The areas are found from the car's own shape in object space (position and which
// way the surface faces), so the imported body needs no UVs, mask texture or extra mesh data.
Shader "F2/CarPaint"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.05, 0.07, 1)
        _Coat ("Clear coat", Range(0, 1)) = 1
        _Gloss ("Gloss", Range(0, 1)) = 0.9
        _Metal ("Metal", Range(0, 1)) = 0.2
        _Color2 ("Livery secondary", Color) = (0.93, 0.93, 0.94, 1)
        _Color3 ("Livery accent", Color) = (0.1, 0.1, 0.11, 1)
        _Livery ("Livery split (0 off, 1 on)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            fixed4 _Color, _Color2, _Color3;
            half _Livery;
            half _Coat, _Gloss, _Metal;
            half _CarLit;
            half4 _GroundRefl;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wp : TEXCOORD0;
                half3 n : TEXCOORD1;
                half3 amb : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                float3 op : TEXCOORD4; // object-space position and normal, for the livery split
                half3 on : TEXCOORD5;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.amb = ShadeSH9(half4(o.n, 1));
                o.op = v.vertex.xyz; o.on = v.normal;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                half3 N = normalize(i.n);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - i.wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 base = _Color.rgb;
                if (_Livery > 0.5)
                {
                    // car space: +z forward (nose tip at z 2.8), +y up, x across; ny = how much the surface faces up
                    float3 p = i.op; half ny = normalize(i.on).y; float ax = abs(p.x);
                    // sidepods: the part beside the cockpit, from the inlets back
                    half side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
                    half sideSec = side * step(p.z, 0.95) * step(0.55, ny);
                    half sideAcc = side * step(0.42, ny) * (1.0 - sideSec);
                    // engine cover: the spine behind the driver (rear-wing endplates are wider and stay primary)
                    half eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
                    half engSec = eng * step(p.z, -0.05) * step(0.30, ny);
                    half engAcc = eng * step(0.18, ny) * (1.0 - engSec);
                    // nose tip, in the front wing's colour
                    half noseSec = step(2.45, p.z);
                    half noseAcc = step(2.40, p.z) * (1.0 - noseSec);
                    half sec = saturate(sideSec + engSec + noseSec);
                    half acc = saturate(sideAcc + engAcc + noseAcc) * (1.0 - sec);
                    base = lerp(lerp(base, _Color2.rgb, sec), _Color3.rgb, acc);
                }
                half3 diff = base * (ndl * _LightColor0.rgb + i.amb) * (1.0 - _Metal * 0.6);
                half3 R = reflect(-V, N);
                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, (1.0 - _Gloss) * 6.0);
                half3 env = DecodeHDR(envRaw, unity_SpecCube0_HDR);
                env = lerp(env, _GroundRefl.rgb, smoothstep(0.02, -0.12, R.y));
                env *= lerp(0.35, 1.0, _CarLit);
                half nv = saturate(dot(N, V));
                half f = 0.04 + 0.96 * pow(1.0 - nv, 5.0);
                half coat = f * _Coat;
                half3 H = normalize(L + V);
                half spec = pow(saturate(dot(N, H)), lerp(16.0, 512.0, _Gloss)) * ndl * lerp(0.5, 6.0, _Gloss);
                half3 col = diff * (1.0 - coat) + env * (coat + _Metal * base) + spec * _LightColor0.rgb * (_Coat + _Metal);
                fixed4 c = fixed4(col, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
