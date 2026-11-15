// World surfaces with lighting baked into the vertex colours (see LightBake.cs): one texture read per pixel,
// no lights, no shadow maps. Fog is kept.
// Two extra variants for the real surroundings (RealWorld.cs), chosen per material by keyword:
//  F2_LAND  the terrain: uv0 = world (x, z) in metres. _LandTex is the land-use colour map over the whole area
//           (alpha picks the ground detail: 0 paved, 1/3 grass, 2/3 rough ground, 1 tree canopy seen from above),
//           _MainTex the fine detail (EnvTex.LandDetail, 4.5 m per tile), _LandMacro the crowns of the woods and a
//           slow brightness drift (EnvTex.LandMacro, ~48 m per tile). Three texture reads.
//  F2_ATLAS buildings: _MainTex is a 4 x 4 atlas of repeating facade / roof tiles (EnvTex.BuildingAtlas); uv0 counts
//           tiles (window bays across, floors up), uv2 = the atlas cell. The tile repeats inside its cell, with the
//           mip level worked out from the unwrapped uv (no seams) and capped so neighbouring cells do not bleed.
Shader "F2/Baked"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _LandTex ("Land colour map (F2_LAND)", 2D) = "white" {}
        _LandMacro ("Land macro (F2_LAND)", 2D) = "gray" {}
        _LandRect ("Land map: x0, z0, 1/width, 1/depth (F2_LAND)", Vector) = (0, 0, 0.001, 0.001)
        _DetailScale ("Detail tiles per metre (F2_LAND)", Float) = 0.222
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile_local __ F2_LAND F2_ATLAS
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Grade.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            #if defined(F2_ATLAS)
                float2 uv2 : TEXCOORD2;
            #endif
                float4 color : COLOR;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            #if defined(F2_ATLAS)
                float2 cell : TEXCOORD3;
            #endif
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
        #if defined(F2_LAND)
            sampler2D _LandTex, _LandMacro;
            float4 _LandRect;
            float _DetailScale;
        #endif

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            #if defined(F2_ATLAS)
                o.cell = v.uv2;
            #endif
                o.color = v.color * _Color * 2.0; // stored light 0.5 = full brightness, so sunlit areas can go above 1
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
            #if defined(F2_LAND)
                float2 w = i.uv;
                fixed4 L = tex2D(_LandTex, (w - _LandRect.xy) * _LandRect.zw);
                fixed4 d = tex2D(_MainTex, w * _DetailScale);
                fixed4 m = tex2D(_LandMacro, float2(w.x * 0.8 - w.y * 0.6, w.x * 0.6 + w.y * 0.8) * (1.0 / 48.0));
                half a = L.a * 3.0;
                half k = a < 1.0 ? lerp(d.g, d.r, a) : (a < 2.0 ? lerp(d.r, d.b, a - 1.0) : lerp(d.b, d.r, a - 2.0));
                half crown = saturate(a - 2.0);
                fixed4 c = fixed4(L.rgb * (0.55 + 0.9 * k) * lerp(0.88 + 0.24 * m.r, 0.38 + 1.05 * m.g, crown), 1);
            #elif defined(F2_ATLAS)
                float2 g = i.uv * 128.0;                       // texels of one 128 px cell
                float2 dx = ddx(g), dy = ddy(g);
                float lod = clamp(0.5 * log2(max(dot(dx, dx), dot(dy, dy))), 0.0, 4.0);
                float2 a = i.cell * 0.25 + 0.004 + frac(i.uv) * 0.242;
                fixed4 c = tex2Dlod(_MainTex, float4(a, 0, lod));
            #else
                fixed4 c = tex2D(_MainTex, i.uv);
            #endif
                c.rgb *= i.color.rgb;
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/Diffuse"
}
