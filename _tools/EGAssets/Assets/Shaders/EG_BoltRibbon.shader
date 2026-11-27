Shader "EG/BoltRibbon"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _Power ("Falloff", Float) = 1.5
        _ZTest ("ZTest", Int) = 4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
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
            float _Power;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float u = abs(i.uv.x * 2.0 - 1.0);
                float a = pow(saturate(1.0 - u), 1.5);
                return fixed4(_Color.rgb, _Color.a * a);
            }
            ENDCG
        }
    }
    FallBack Off
}
