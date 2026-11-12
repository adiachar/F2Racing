// Shift lights on the steering wheel (see ShiftLights in DriverFeel.cs): one small mesh of quads, each quad's
// colour stored in its vertices. Self-lit, no texture, no fog, so the LEDs read the same in sun and in the tunnel.
Shader "F2/ShiftLed"
{
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" }
        LOD 100
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return fixed4(i.color.rgb, 1); }
            ENDCG
        }
    }
}
