// CarPaint per-vertex pass (Low level; rival far LODs always): shared by CarPaint.shader and CarPaintFar.shader (one copy of the code; edit here).

            #if !defined(_MERGED)
            fixed4 _Color;
            #endif
            fixed4 _Color2, _Color3;
            float _Mirror, _Livery; // (compared / used in step(): full float, see the note at the top)
            half _AO, _Dust, _Coat, _Gloss, _Metal, _CarLit, _Wet;
            half4 _Glow, _GroundRefl;

            struct v2f
            {
                float4 pos : SV_POSITION;
                half3 diff : TEXCOORD0;   // light on the base colour (sun + sky, less what the coat reflects)
                half3 add : TEXCOORD1;    // coat reflection (KeepHue per pixel)
                half4 envM : TEXCOORD2;   // metallic reflection (tinted by the base colour per pixel); w = (1 - N.V)^4
                half spec : TEXCOORD7;    // sun highlight x (coat + metal), times the light colour per pixel
                float3 op : TEXCOORD3;    // object position, for the livery split and the helmet visor
                float ny : TEXCOORD4;     // object normal y
                float4 col : COLOR;       // baked AO (r), disc mask (g), part ID (a, merged); float: feeds step()
                UNITY_FOG_COORDS(5)
                #if defined(_MERGED)
                half3 mbase : TEXCOORD6;  // merged rival: the part's base colour (MMat)
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v, o);
                half coat0 = _Coat, gloss0 = _Gloss, metal0 = _Metal;
                #if defined(_MERGED)
                    half3 cgm; MMat(MId(v.color.a), o.mbase, cgm); coat0 = cgm.x; gloss0 = cgm.y; metal0 = cgm.z;
                    float mir = MIs(MId(v.color.a), 3);
                #else
                    float mir = _Mirror;
                #endif
                mir *= step(0.45, abs(v.vertex.x)); // mirror glass (as the full pass)
                coat0 = lerp(coat0, 0.6, mir); gloss0 = lerp(gloss0, 0.92, mir); metal0 = lerp(metal0, 0.0, mir);
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 N = UnityObjectToWorldNormal(v.normal);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 R = reflect(-V, N);
                half gloss = saturate(gloss0 + 0.08 * _Wet);
                half3 env = ShadeSH9(half4(R, 1)) * 0.9;
                half grL = dot(env, half3(0.2126, 0.7152, 0.0722));
                half3 gr = max(_GroundRefl.rgb, half3(grL, grL, grL)); // (as the full pass)
                env = lerp(env, gr, 1.0 - smoothstep(lerp(-0.35, -0.05, gloss), 0.015, R.y));
                env *= lerp(0.35, 1.0, _CarLit);
                env *= 1.0 - 0.2 * saturate((coat0 - 0.9) * 20.0); // (as the full pass)
                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                float coat = max(saturate((0.04 + 0.96 * x2 * x2 * (0.75 + 0.25 * x)) * coat0 * (1.0 + 0.35 * _Wet) + 0.03 * _Wet), 0.22 * mir);
                half nh = saturate(dot(N, normalize(L + V)));
                half sp = min(exp2(3.0 + 6.0 * gloss), 128.0); // (per vertex: no tighter than the mesh can show)
                half spec = pow(nh, sp) * ndl * min(sp * 0.02, 3.0);
                half wrapL = saturate((dot(N, L) + 0.2) * (1.0 / 1.2));
                o.diff = (wrapL * _LightColor0.rgb + ShadeSH9(half4(N, 1))) * (1.0 - metal0 * 0.85) * (1.0 - 0.12 * _Wet) * (1.0 - coat);
                o.add = env * coat;
                o.spec = spec * (coat0 + metal0);
                o.envM = half4(env * metal0, x2 * x2);
                o.op = v.vertex.xyz; o.ny = v.normal.y;
                o.col = v.color;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(_MERGED)
                half3 base = MP(_Color).rgb;
                #else
                half3 base = _Color.rgb;
                #endif
                if (_Livery > 0.5)
                {
                    float2 lw = LiverySplit(i.op, i.ny, _Livery - 1.0);
                    base = lerp(lerp(base, _Color2.rgb, lw.x), _Color3.rgb, lw.y); // (float weights: never cast a mask to half)
                }
                float3 emis = 0; // (built from part masks: float)
                half ao = lerp(1.0, i.col.r, _AO);
                #if defined(_MERGED)
                    float id = MId(i.col.a); base = i.mbase; ao = i.col.r;
                    base = lerp(base, MLivery(i.op, i.ny, MP(_LivC).a - 1.0, MP(_Color).rgb, MP(_LivB).rgb, MP(_LivC).rgb), MIs(id, 0) * step(0.5, MP(_LivC).a));
                    float front = step(id, 4.5) + MIs(id, 6), hub = step(5.5, id) * step(id, 7.5);
                    float dust = lerp(MP(_Axle).y, MP(_Axle).x, front) * hub, disc = i.col.g * hub;
                    base = lerp(base * (1.0 - 0.55 * dust), half3(0.06, 0.06, 0.065), disc);
                    emis = lerp(MP(_GlowR).rgb, MP(_GlowF).rgb, front) * disc + MP(_LightCol).rgb * MIs(id, 8);
                #elif defined(_WHEEL)
                    half disc = i.col.g;
                    base = lerp(base * (1.0 - 0.55 * _Dust), half3(0.06, 0.06, 0.065), disc);
                    emis = _Glow.rgb * disc;
                #elif defined(_HELMET)
                    float3 hp = i.op;
                    float visor = saturate((hp.z - 0.12) * 25.0) * saturate((hp.y + 0.06) * 40.0) * saturate((0.16 - hp.y) * 40.0);
                    float stripe = (1.0 - visor) * step(abs(hp.x), 0.045) * step(0.0, hp.y);
                    base = lerp(lerp(base, _Color3.rgb, stripe), half3(0.3, 0.22, 0.12), visor);
                #endif
                #if defined(_MERGED)
                float mirP = MIs(MId(i.col.a), 3);
                #else
                float mirP = _Mirror;
                #endif
                base = lerp(base, half3(0.05, 0.052, 0.056), mirP * step(0.45, abs(i.op.x))); // mirror glass
                half3 c3 = (half3)PaintRoll((float3)(base * i.diff * ao), 0.0) + (KeepHue(i.add, base, i.envM.w) + i.spec * _LightColor0.rgb + i.envM.rgb * base) * (0.3 + 0.7 * ao) + emis;
                #if defined(F2_GHOST)
                { float ghost = GHOST_G; F2GhostClip(i.pos.xy, ghost); c3 = (half3)F2GhostTint((float3)c3, ghost, sqrt((float)i.envM.w)); } // (as the full pass; graze = sqrt((1 - N.V)^4))
                #endif
                fixed4 c = fixed4(c3, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
