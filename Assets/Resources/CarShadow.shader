// The cars' sun shadow: a PLANAR PROJECTED SHADOW of the car's own silhouette (CarModel.AddSunShadow).
// A low-poly copy of the car (car_far's shape, positions only) is drawn with this shader: every vertex is pushed along
// the sun's rays onto the ground plane under the car (the car root's plane, given per car), lifted 2 cm against
// z-fighting, and filled with one flat shade. A stencil bit makes every pixel darken once only, however many of the
// car's triangles (or other cars' shadows) land on it. Its length follows the sun's height by itself.
//  _ShadowPlane: xyz = ground normal, w = -dot(normal, point on the ground)       (world space, metres)
//  _ShadowSun:   xyz = towards the sun (unit), w = alpha (0 = off; set per car: sun height, cloud, rain, sun visibility)
//  _ShadowAway:  xyz = away from the sun along the ground (unit), w = dot(that, car centre): the shade lightens with
//                the distance from the car (wider penumbra, more sky light), so the long tip of a low sun's shadow fades
// Everything is float (no half compares: the min-precision bytecode fallback).
Shader "F2/CarShadow"
{
    Properties
    {
        _ShadowCol ("Shadow colour (linear)", Color) = (0.03, 0.04, 0.065, 1)
    }
    SubShader
    {
        // after every opaque (the road and the cars write depth first), before the clouds and the contact blobs
        Tags { "Queue"="Transparent-110" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Pass
        {
            Tags { "LightMode"="Always" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp NotEqual
                Pass Replace
            }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            float4 _ShadowCol, _ShadowPlane, _ShadowSun, _ShadowAway;

            struct v2f { float4 pos : SV_POSITION; float2 k : TEXCOORD0; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0)).xyz;
                float3 n = _ShadowPlane.xyz, l = _ShadowSun.xyz;
                float h = max(dot(n, wp) + _ShadowPlane.w, 0.0);       // height above the ground (parts below it: on it)
                float ndl = max(dot(n, l), 0.15);                      // (the car hides the shadow when the sun is lower)
                float t = min(h / ndl, 6.0);                           // along the ray to the ground, at most 6 m
                float3 p = wp - l * t;
                p -= n * (dot(n, p) + _ShadowPlane.w);                 // exactly on the plane (also where t was capped)
                p += n * (0.02 + 0.004 * t);                           // a hair above the road: no z-fight on slopes ahead
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1.0));
                // the shade at this ground point: full near the car, ~55 % at 5 m behind it (consistent for every
                // triangle landing on the same pixel, so the stencil's "first one wins" never shows)
                float d = dot(_ShadowAway.xyz, p) - _ShadowAway.w;
                float fade = 1.0 - 0.45 * saturate((d - 1.2) / 4.0);
                float fog = 1.0;
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                    UNITY_CALC_FOG_FACTOR(o.pos.z);
                    fog = saturate(unityFogFactor);
                #endif
                o.k = float2(fade, fog);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float a = _ShadowSun.w * i.k.x * i.k.y;
                return float4(_ShadowCol.rgb, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
