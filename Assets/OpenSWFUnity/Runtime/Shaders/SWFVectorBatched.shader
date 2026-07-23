Shader "OpenSWFUnity/SWF Vector Batched"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        // Vector (not Color) prevents Unity applying an extra colour-space
        // conversion to values already converted from SWF sRGB on the CPU.
        [PerRendererData] _Color ("Tint", Vector) = (1,1,1,1)
        [PerRendererData] _UseSwfMatrix ("Use SWF Matrix", Float) = 0
        [PerRendererData] _SwfMatrix0 ("SWF Matrix Row 0", Vector) = (1,0,0,0)
        [PerRendererData] _SwfMatrix1 ("SWF Matrix Row 1", Vector) = (0,1,0,0)
        _StencilRef ("Stencil Ref", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 0
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
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
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Stencil
        {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass [_StencilPass]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
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
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _UseSwfMatrix;
            float4 _SwfMatrix0;
            float4 _SwfMatrix1;

            // Every fill carries its colour in the vertex stream, so thousands of
            // differently coloured shapes collapse into a single batched draw.
            // Solid fills bind a 1x1 white texture and rely purely on that colour;
            // bitmap fills bind their own texture and supply real UVs, so the same
            // shader and the same batch path serves both.
            v2f vert(appdata input)
            {
                v2f output;
                float4 vertex = input.vertex;

                if (_UseSwfMatrix > 0.5)
                {
                    vertex = float4(
                        dot(_SwfMatrix0.xy, input.vertex.xy) + _SwfMatrix0.w,
                        dot(_SwfMatrix1.xy, input.vertex.xy) + _SwfMatrix1.w,
                        -0.02,
                        1.0);
                }

                output.vertex = UnityObjectToClipPos(vertex);
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * input.color;
                clip(color.a - 0.002);
                return color;
            }
            ENDCG
        }
    }
}
