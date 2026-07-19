Shader "OpenSWFUnity/SWF Raster Stencil"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilRef ("Stencil Ref", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 0
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Stencil
        {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass [_StencilPass]
            ReadMask 255
            WriteMask 255
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * _Color;
                clip(color.a - 0.001);
                color.rgb *= color.a;
                return color;
            }
            ENDCG
        }
    }
}
