Shader "OpenSWFUnity/SWF Fill Masked"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _StencilRef ("Stencil Ref", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue"="Geometry+1"
            "RenderType"="Opaque"
        }

        Cull Off
        ZWrite Off
        ZTest Always

        Stencil
        {
            Ref [_StencilRef]
            Comp NotEqual
        }

        Pass
        {
            Color [_Color]
        }
    }
}