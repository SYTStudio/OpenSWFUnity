Shader "OpenSWFUnity/SWF Stencil Hole"
{
    Properties
    {
        _StencilRef ("Stencil Ref", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue"="Geometry"
            "RenderType"="Opaque"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        ColorMask 0

        Stencil
        {
            Ref [_StencilRef]
            Comp Always
            Pass Replace
        }

        Pass
        {
        }
    }
}