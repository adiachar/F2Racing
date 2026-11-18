// World surfaces with lighting baked into the vertex colours (see LightBake.cs): one texture read per pixel,
// no lights, no shadow maps. Fog is kept.
// Two extra variants for the real surroundings (RealWorld.cs), chosen per material by keyword:
//  F2_LAND  the terrain: uv0 = world (x, z) in metres. _LandTex is the land-use colour map over the whole area
//           (alpha picks the ground detail: 0 paved, 1/3 grass, 2/3 rough ground, 1 tree canopy seen from above),
//           _MainTex the fine detail (EnvTex.LandDetail, 4.5 m per tile), _LandMacro the crowns of the woods and a
//           slow brightness drift (EnvTex.LandMacro, ~48 m per tile). Three texture reads.
//  F2_ATLAS buildings: _MainTex is an 8 x 4 atlas of repeating facade / roof tiles (EnvTex.BuildingAtlas); uv0 counts
//           tiles (window bays across, floors up). uv2.x = the cell index; uv2.y = an alternative cell + 0.05 + 0.9 x
//           its share: a hash of the bay (floor(uv0)) swaps that share of the bays to it (closed shutters, doors among
//           shop windows). The tile repeats inside its cell, with the mip level worked out from the unwrapped uv (no
//           seams) and capped so neighbouring cells do not bleed. uv3 = the building's colour (rgb), applied where the
//           texture's alpha is 1 (plaster, brick, cladding; glass and frames keep theirs), and w = the wall's length in
//           tiles (0: none): the last 0.075 tile at each end is a lighter corner trim (quoins / pilasters).
//  F2_GLASS the trackside atlas (TracksideArt.Atlas) on Medium / High (TracksideLod switches it): texels whose alpha is
//           below 1 are glass (curtain walls, stand boxes, balustrades, cabin windows). They mirror the sky and the
//           ground with a view-dependent Fresnel term (Schlick, F0 0.04) and the sun in a small glint, whatever the bake
//           says about their light: shaded glass still shows the bright sky. The sky is worked out from the fog colour
//           (horizon) and a deeper blue overhead; the sun direction is Sky.cs's _F2HazeSun. Everything else unchanged.
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
            #pragma multi_compile_local __ F2_LAND F2_ATLAS F2_GLASS
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            #include "UnityCG.cginc"
            #include "Grade.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            #if defined(F2_ATLAS)
                float2 uv2 : TEXCOORD2;
                float4 uv3 : TEXCOORD3;
            #endif
            #if defined(F2_GLASS)
                float3 normal : NORMAL;
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
                half4 tint : TEXCOORD4;
            #endif
            #if defined(F2_GLASS)
                float3 view : TEXCOORD3; // surface -> camera (world)
                half3 n : TEXCOORD4;
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
                o.cell = v.uv2; o.tint = v.uv3;
            #endif
            #if defined(F2_GLASS)
                o.view = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
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
                // (compares in float: on GLES3 / Vulkan a compare on half fails FXC's min-precision bytecode check and
                // Unity drops the whole shader to its Fallback)
                float a = L.a * 3.0;
                half k = a < 1.0 ? lerp(d.g, d.r, a) : (a < 2.0 ? lerp(d.r, d.b, a - 1.0) : lerp(d.b, d.r, a - 2.0));
                half crown = saturate(a - 2.0);
                fixed4 c = fixed4(L.rgb * (0.55 + 0.9 * k) * lerp(0.88 + 0.24 * m.r, 0.38 + 1.05 * m.g, crown), 1);
            #elif defined(F2_ATLAS)
                float2 g = i.uv * 128.0;                       // texels of one 128 px cell
                float2 dx = ddx(g), dy = ddy(g);
                float lod = clamp(0.5 * log2(max(dot(dx, dx), dot(dy, dy))), 0.0, 4.0);
                float h = frac(sin(dot(floor(i.uv), float2(12.9898, 78.233))) * 43758.5453);
                float alt = frac(i.cell.y);
                float idx = h < (alt - 0.05) * 1.1111 ? floor(i.cell.y) : floor(i.cell.x + 0.5);
                float cy = floor(idx * 0.125 + 0.01);
                float2 a = (float2(idx - cy * 8.0, cy) + (2.0 + frac(i.uv) * 124.0) / 128.0) * float2(0.125, 0.25);
                fixed4 c = tex2Dlod(_MainTex, float4(a, 0, lod));
                half3 tc = i.tint.rgb;
                float tw = i.tint.w, e = min(i.uv.x, tw - i.uv.x); // (float compares, see F2_LAND)
                float trim = (tw > 0.5 && e < 0.075) ? 0.5 : 0.0;
                tc = lerp(tc, half3(1.0, 0.97, 0.9), (half)trim);
                c.rgb *= lerp(half3(1, 1, 1), tc, c.a);
            #else
                fixed4 c = tex2D(_MainTex, i.uv);
            #endif
                c.rgb *= i.color.rgb;
            #if defined(F2_GLASS)
                {
                    // (float maths for the mask and the compares, see F2_LAND)
                    float gm = saturate(1.0 - c.a);
                    float3 V = normalize(i.view), N = normalize((float3)i.n);
                    float3 R = reflect(-V, N);
                    float fv = 1.0 - saturate(dot(V, N)), f2 = fv * fv;
                    float fres = 0.04 + 0.96 * f2 * f2 * fv;
                    half3 hor = unity_FogColor.rgb;
                    half3 sky = lerp(hor * half3(1.04, 1.02, 1.0), hor * half3(0.52, 0.7, 1.02), (half)saturate(R.y * 1.8));
                    half3 gnd = hor * half3(0.3, 0.32, 0.27);                        // the ground and tree line mirrored low down
                    half3 env = lerp(gnd, sky, (half)saturate(R.y * 10.0 + 0.5));
                    float sd = saturate(dot(R, _F2HazeSun.xyz)); sd *= sd; sd *= sd; sd *= sd; sd *= sd; sd *= sd; sd *= sd; // ^64
                    float sunOn = _F2HazeSun.w > 0.0 ? 1.0 : 0.0;
                    half k = (half)(gm * (0.22 + 0.78 * fres));
                    c.rgb = lerp(c.rgb, env, k) + half3(1.0, 0.93, 0.82) * (half)(sd * 5.0 * sunOn * gm * (0.3 + 0.7 * fres));
                }
            #endif
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/Diffuse"
}
