﻿Shader "Shadow/ShadowCaster"
{
    Properties { }
    SubShader
    {
        Tags { "LightMode" = "ShadowCaster" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ShadowCaster"
            
            ZWrite On ZTest LEqual Cull Off
            
            CGPROGRAM
            
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            
            struct v2f
            {
                V2F_SHADOW_CASTER;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            v2f vert(appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            
            float4 frag(v2f i): SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
            
        }
    }
}
