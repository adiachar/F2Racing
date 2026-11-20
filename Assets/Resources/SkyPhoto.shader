// Photo sky (Sky.cs, afternoon only): a real CC0 sky panorama as the skybox, and so also in the cars' reflection probe
// and the trackside reflection cubes (CarEnv), which render the skybox. Resources/Photo/sky_day.png (Tools/Photo/
// make_photos.py): the upper hemisphere of Poly Haven's kloofendal_48d_partly_cloudy_puresky at 8K (fair-weather
// cumulus, the sun 47.9 degrees up, as the game's afternoon sun at 48), tone-mapped to display values offline, 8192 x
// 2048, ASTC 6x6 (7.5 MB), no mips (always magnified: ~1400 texels across a 62-degree view on a ~1700 px 3D image).
// Layout: u = azimuth with the sun at u = 0 (turned at runtime to the world sun, _SunAz), v = sin(elevation), so the
// row needs no asin, only the azimuth an atan2. Below the horizon: the horizon row (the haze band of Clouds.shader and
// the world cover it). Per pixel: 1 read, ~20 ALU (atan2, normalize). Replaces Unity's procedural skybox, which on the
// afternoon was drawn under the cloud dome's two to four reads; with this sky the dome keeps only its haze band (Sky.cs
// sets _CloudOn 0 in the dry), so sky pixels cost fewer reads than before.
// Not graded (like the procedural skybox it replaces); _Tint / _Exposure carry the circuit's look and the rain greying.
// (float-only rule: no compares or step() on half values.)
Shader "F2/SkyPhoto"
{
    Properties
    {
        _MainTex ("Sky (u azimuth from the sun, v = sin elevation)", 2D) = "grey" {}
        _Tint ("Tint (linear multiplier)", Color) = (1, 1, 1, 1)
        _Exposure ("Exposure", Float) = 1
        _Grey ("Desaturate (rain)", Range(0, 1)) = 0
        _SunAz ("Sun azimuth atan2(z, x), radians", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "IgnoreProjector"="True" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Tint;
            float _Exposure, _Grey, _SunAz;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz; // the skybox mesh is centred on the camera: object position = view direction
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                // Unity's panorama convention (u = 0.5 - atan2(z, x) / 2pi) turned so the sun's column (u = 0) faces _SunAz
                float u = (_SunAz - atan2(d.z, d.x)) * 0.15915494;
                float v = saturate(d.y);
                // level 0 explicitly: no mip jump along the atan2 seam behind the sun (the texture has no mips anyway)
                float3 c = tex2Dlod(_MainTex, float4(u, v, 0.0, 0.0)).rgb;
                float l = dot(c, float3(0.2126, 0.7152, 0.0722));
                c = lerp(c, float3(l, l, l), _Grey) * _Tint.rgb * _Exposure;
                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
