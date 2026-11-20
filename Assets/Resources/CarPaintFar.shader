// F2/CarPaintFar: the rivals' simplified LOD levels (CarLod.cs: car_lod beyond 36 m, car_far beyond 75 m), RR3-style:
// always the per-vertex lighting pass of CarPaint (CarPaintLow.cginc: sun, sky, coat and highlight per vertex, no
// reflection-probe read, no per-pixel pow), whatever the graphics level. A far car is a few hundred pixels and its
// simplified mesh keeps the lighting smooth, so the full per-pixel pass bought nothing visible there.
// Same properties, keywords, instanced props and ghost variant as CarPaint (a clone of the rival material).
Shader "F2/CarPaintFar"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.05, 0.07, 1)
        _Coat ("Clear coat", Range(0, 1)) = 1
        _Gloss ("Gloss", Range(0, 1)) = 0.9
        _Metal ("Metal", Range(0, 1)) = 0.2
        _Color2 ("Livery secondary", Color) = (0.93, 0.93, 0.94, 1)
        _Color3 ("Livery accent", Color) = (0.1, 0.1, 0.11, 1)
        _Livery ("Livery split (0 off, 1 + style 0..3)", Float) = 0
        _AO ("Baked AO strength (vertex R)", Range(0, 1)) = 0
        _Under ("Carbon only underneath", Float) = 0
        _Wear ("Tyre wear", Range(0, 1)) = 0
        _Dirt ("Tyre dirt", Range(0, 1)) = 0
        _TyreR ("Tyre radius (object units)", Float) = 0.33
        _Dust ("Brake dust", Range(0, 1)) = 0
        _Glow ("Brake disc glow", Color) = (0, 0, 0, 0)
        _TyreRad ("Merged: tyre radius front, rear", Vector) = (0.33, 0.33, 0, 0)
        _Far ("Merged: far model (no carbon weave, no tyre sheen)", Float) = 0
        _Mirror ("Mirror glass where |x| > 0.45 (metal part)", Float) = 0
        _Rim ("Carbon: halo look (titanium edge sheen, dark inside face)", Float) = 0
        _Cockpit ("Carbon: cockpit surround (padded headrest, shade inside the tub)", Float) = 0
        _Ghost ("Ghost fade (penalty; per renderer via MaterialPropertyBlock, CarGhost.cs)", Range(0, 1)) = 0
    }
    CGINCLUDE
    #include "CarPaintCommon.cginc"
    ENDCG
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #pragma multi_compile_local __ _MERGED
            #pragma multi_compile_local __ F2_GHOST // ghost twin materials only (CarGhost.cs)
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"
            #include "CarPaintLow.cginc"
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
