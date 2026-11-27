Shader "EG/BoltAlpha"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _ZTest ("ZTest", Int) = 4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest [_ZTest]
        Cull Off
        Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // uv.x=0/0.34/0.66/1 四列：中段平顶不透明，两侧渐隐
            fixed4 frag (v2f i) : SV_Target
            {
                float u = abs(i.uv.x * 2.0 - 1.0);
                float a = 1.0 - smoothstep(0.36, 1.0, u);
                return fixed4(_Color.rgb, _Color.a * a);
            }
            ENDCG
        }
    }
    FallBack Off
}
