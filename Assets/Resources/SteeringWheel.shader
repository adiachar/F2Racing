// The player's steering wheel (SteeringWheel.cs): the whole wheel in one mesh and one draw call. Lit like CarPaint
// (sun with a little wrap + sky SH, a clear coat reflecting the sky probe / the road below the horizon, a sun highlight
// sized by the gloss); the material comes per vertex: colour rgb = base (linear), a = baked occlusion;
// TEXCOORD1 = (clear coat, gloss, metal, kind). Kinds:
//   0 plain (buttons, knobs, gloss-black housing, metal hub)   1 carbon: CarPaint's 2x2 twill, finer (4 mm tows)
//   2 rubber / alcantara grips: matte with a soft sheen towards grazing angles (brushed fabric)
//   3 display: the CPU-drawn _Dash texture, self-lit, behind a glossy glass that still mirrors the sky
//   4 shift LED: TEXCOORD0.x = its number (0..14); _Led.x = how many are lit, _Led.y = 1 all blue / 2 all dark (flash)
// Paddles (TEXCOORD2.x = -1 left / +1 right): turned about their hinge (TEXCOORD0 = hinge x, z) by _Paddle.x / .y
// radians, the tip towards the driver: the flick on a gear change, with no extra object or draw call.
// Self-lit parts (display, LEDs) are added after the colour grade, so they read the same in the sun and in a tunnel.
// PRECISION RULE (GLES3 / Vulkan, see CarPaint.shader): every comparison, step() and mask is FLOAT, never half.
Shader "F2/SteeringWheel"
{
    Properties
    {
        _Dash ("Display (drawn by SteeringWheel.cs)", 2D) = "black" {}
        _Led ("Shift lights: lit, flash", Vector) = (0, 0, 0, 0)
        _Paddle ("Paddle angles left, right (rad)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"

            sampler2D _Dash;
            float4 _Led, _Paddle;
            half _CarLit;
            half4 _GroundRefl;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 mat : TEXCOORD1;
                float2 pad : TEXCOORD2;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wp : TEXCOORD0;
                half3 n : TEXCOORD1;
                float4 col : COLOR;
                float2 uv : TEXCOORD2;
                float4 mat : TEXCOORD3;   // coat, gloss, metal, kind (float: the kind feeds the masks)
                float3 op : TEXCOORD4;    // object-space position and normal (carbon weave)
                float3 on : TEXCOORD5;
                half3 amb : TEXCOORD6;
                half3 envSH : TEXCOORD7;
                UNITY_FOG_COORDS(8)
            };

            // CarPaint's 2x2 twill (see there), at 'k' tows per object unit
            float2 Weave(float3 op, float3 on, float fade, float k)
            {
                float3 an = abs(on);
                float2 w = (an.x > max(an.y, an.z) ? op.zy : (an.y > an.z ? op.xz : op.xy)) * k;
                float2 cell = floor(w), fr = frac(w);
                float sel = step(0.5, frac((cell.x + cell.y) * 0.25));
                float across = lerp(fr.y, fr.x, sel), prof = 4.0 * across * (1.0 - across);
                return float2(lerp(1.0, lerp(0.6, 1.3, prof) * lerp(0.88, 1.12, sel), fade), lerp(1.0, lerp(0.55, 1.45, sel) * prof + 0.3, fade));
            }
            #define TOWS 270.0

            v2f vert (appdata v)
            {
                v2f o;
                // paddles: a turn about the vertical axis through the hinge (non-paddles: angle 0, unchanged)
                float side = v.pad.x;
                float ang = side * lerp(_Paddle.x, _Paddle.y, saturate(side));
                float s, c; sincos(ang, s, c);
                float isPad = step(0.5, abs(side));
                float2 hinge = v.uv * isPad;
                float2 dp = v.vertex.xz - hinge;
                float3 p = float3(hinge.x + dp.x * c + dp.y * s, v.vertex.y, hinge.y - dp.x * s + dp.y * c);
                float3 nrm = float3(v.normal.x * c + v.normal.z * s, v.normal.y, -v.normal.x * s + v.normal.z * c);
                float4 vp = float4(p, 1.0);
                o.pos = UnityObjectToClipPos(vp);
                o.wp = mul(unity_ObjectToWorld, vp).xyz;
                o.n = UnityObjectToWorldNormal(nrm);
                o.amb = ShadeSH9(half4(o.n, 1));
                o.envSH = ShadeSH9(half4(reflect(-normalize(_WorldSpaceCameraPos.xyz - o.wp), o.n), 1));
                o.col = v.color;
                o.uv = v.uv;
                o.mat = v.mat;
                o.op = p; o.on = nrm;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float kind = i.mat.w;
                float isCarbon = step(abs(kind - 1.0), 0.5), isFabric = step(abs(kind - 2.0), 0.5);
                float isScreen = step(abs(kind - 3.0), 0.5), isLed = step(abs(kind - 4.0), 0.5);
                // derivatives outside any branch
                float3 dop = fwidth(i.op) * TOWS;
                float fade = saturate(1.75 - 2.5 * max(dop.x, max(dop.y, dop.z))) * isCarbon;
                float4 dash = tex2D(_Dash, i.uv);

                half3 N = normalize(i.n);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - i.wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half nl = dot(N, L), ndl = saturate(nl);
                half3 base = i.col.rgb;
                half ao = i.col.a;
                half coatK = i.mat.x, gloss = i.mat.y, metal = i.mat.z, specK = 1.0;

                // carbon weave
                float2 wv = Weave(i.op, normalize(i.on), fade, TOWS);
                base *= (half)lerp(1.0, wv.x, isCarbon);
                specK = (half)lerp(1.0, wv.y, isCarbon);

                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                // sun with a little wrap + sky
                half wrapL = saturate((nl + 0.2) * (1.0 / 1.2));
                half3 light = wrapL * _LightColor0.rgb + i.amb;
                // fabric: a soft sheen towards grazing angles (fibres catching the light)
                half sheen = (half)isFabric * x2 * x * 0.9;
                half3 diff = (base * (1.0 - metal * 0.85) + sheen * half3(0.05, 0.05, 0.055)) * light * ao;

                half3 R = reflect(-V, N);
                half3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, (1.0 - gloss) * 5.0), unity_SpecCube0_HDR);
                env = max(env, i.envSH * 0.9);
                half grL = dot(i.envSH, half3(0.2126, 0.7152, 0.0722)) * 0.9;
                half3 gr = max(_GroundRefl.rgb, half3(grL, grL, grL));
                env = lerp(env, gr, 1.0 - smoothstep(lerp(-0.35, -0.05, gloss), 0.015, R.y));
                // deep in the cockpit: the occlusion and the car's shade dim the reflections
                env *= lerp(0.35, 1.0, _CarLit) * (0.25 + 0.75 * ao);
                half f = 0.04 + 0.96 * x2 * x2 * (0.75 + 0.25 * x);
                half coat = saturate(f * coatK);
                half3 H = normalize(L + V);
                float nh = saturate(dot((float3)N, (float3)H));
                half sp = exp2(3.0 + 6.0 * gloss);
                half spec = (half)pow(nh, (float)sp) * ndl * min(sp * 0.015, 6.0) * specK * ao;
                half3 col = diff * (1.0 - coat) + env * coat + env * (metal * base) + spec * _LightColor0.rgb * (coatK + metal);

                // self-lit: the display and the shift LEDs
                float idx = i.uv.x;
                float lit = step(idx + 0.5, _Led.x);
                float allBlue = step(0.5, _Led.y) * step(_Led.y, 1.5), allDark = step(1.5, _Led.y);
                lit = saturate(lit * (1.0 - allDark) + allBlue);
                float3 lc = lerp(lerp(float3(0.03, 0.85, 0.08), float3(1.0, 0.035, 0.02), step(4.5, idx)), float3(0.07, 0.2, 1.0), step(9.5, idx));
                lc = lerp(lc, float3(0.07, 0.2, 1.0), allBlue);
                float3 emis = isLed * (lit * lc * 1.6 + lc * 0.025) + isScreen * dash.rgb * 0.95;

                fixed4 c = fixed4(col, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                c.rgb += (half3)emis;
                return c;
            }
            ENDCG
        }
    }
}
