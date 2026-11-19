// The cars' sun shadow: a PLANAR PROJECTED SHADOW of the car's own silhouette (CarModel.AddSunShadow).
// A low-poly copy of the car (car_far's shape, positions only) is drawn with this shader: every vertex is pushed along
// the sun's rays onto the ground plane under the car (the car root's plane, given per car), lifted 2 cm against
// z-fighting, and filled with one flat shade. A stencil bit makes every pixel darken once only, however many of the
// car's triangles (or other cars' shadows) land on it. Its length follows the sun's height by itself.
//  _ShadowPlane: xyz = ground normal, w = -dot(normal, point on the ground)       (world space, metres)
//  _ShadowSun:   xyz = towards the sun (unit), w = alpha (0 = off; set per car: sun height, cloud, rain, sun visibility)
//  _ShadowAway:  xyz = away from the sun along the ground (unit), w = dot(that, car centre): the shade lightens with
//                the distance from the car (wider penumbra, more sky light), so the long tip of a low sun's shadow fades
// Shade: a MULTIPLY (DstColor) by the same ratio the baked shade has on the road, worked out per vertex from the road's
// mean ambient (_F2RoadAmb, RoadShadow.cs) and sun (_F2SunLin) through the grade: graded(ambient) / graded(ambient + sun),
// so a car's shadow and a tree's shadow on the asphalt are the same darkness and the same cool blue-grey.
// Edge: one pass. CarModel.ShadowSoften bakes a softness into the vertex alpha at load (0 = full shade: the low floor;
// towards 1: high parts such as the rear wing, the outer tyre walls and the wing tips) and sorts the triangles softest
// last, so with the stencil's "first triangle wins" the floor's full shade covers the core and only the overhang shows
// the gradient: a soft, lighter penumbra round a crisp core, no second draw. The rim helper is kept (rim = 0 only).
// Everything is float (no half compares: the min-precision bytecode fallback).
Shader "F2/CarShadow"
{
    Properties
    {
        _ShadowCol ("Fallback shade ratio (no road data)", Color) = (0.50, 0.55, 0.66, 1)
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Grade.cginc"

    float4 _ShadowCol, _ShadowPlane, _ShadowSun, _ShadowAway;
    float4 _F2RoadAmb, _F2SunLin;

    struct v2f { float4 pos : SV_POSITION; float4 col : TEXCOORD0; };

    // rim: 0 = the core, 1 = the soft outer rim
    v2f vertShadow(float4 vertex, float soft, float rim)
    {
        v2f o;
        float3 wp = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0)).xyz;
        float3 n = _ShadowPlane.xyz, l = _ShadowSun.xyz;
        float h = max(dot(n, wp) + _ShadowPlane.w, 0.0);       // height above the ground (parts below it: on it)
        float ndl = max(dot(n, l), 0.15);                      // (the car hides the shadow when the sun is lower)
        float t = min(h / ndl, 6.0);                           // along the ray to the ground, at most 6 m
        float3 p = wp - l * t;
        p -= n * (dot(n, p) + _ShadowPlane.w);                 // exactly on the plane (also where t was capped)
        // the rim: pushed out from the car's centre in the ground plane, wider for parts higher up (penumbra)
        float3 cc = mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;
        float3 rd = p - cc; rd -= n * dot(n, rd);
        float rl = length(rd);
        p += rim * (rl > 0.01 ? rd / rl : float3(0.0, 0.0, 0.0)) * (0.09 + 0.03 * t);
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
        // the baked shade's ratio on the road, through the grade (a mid asphalt albedo)
        float3 ratio = _ShadowCol.rgb;
        if (_F2RoadAmb.w > 0.05)
        {
            float3 amb = _F2RoadAmb.rgb * 0.13, lit = amb + _F2SunLin.rgb * (_F2RoadAmb.w * 0.13);
            float3 gs = F2Grade((half3)amb), gl = F2Grade((half3)lit);
            ratio = saturate(gs / max(gl, 1e-3));
        }
        // _ShadowSun.w: up to ~0.52 (sun height, cloud, rain, the car in baked shade): 0.52 = the full baked shade
        float k = saturate(_ShadowSun.w / 0.52) * fade * fog * (1.0 - 0.5 * rim) * (1.0 - 0.7 * soft);
        o.col = float4(lerp(float3(1.0, 1.0, 1.0), ratio, k), 1.0);
        return o;
    }
    v2f vertCore(float4 vertex : POSITION, float4 color : COLOR) { return vertShadow(vertex, color.a, 0.0); }
    float4 frag(v2f i) : SV_Target { return i.col; }
    ENDCG

    SubShader
    {
        // after every opaque (the road and the cars write depth first), before the clouds and the contact blobs
        Tags { "Queue"="Transparent-110" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Pass
        {
            Tags { "LightMode"="Always" }
            Blend DstColor Zero
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
            #pragma vertex vertCore
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            ENDCG
        }
    }
    Fallback Off
}
