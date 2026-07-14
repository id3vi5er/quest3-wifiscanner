Shader "WifiScanner/Dynamic Room Mesh Normal Colors"
{
    Properties
    {
        [Toggle] _ColorMode("Use WiFi vertex colours", Float) = 0
        _Opacity("Opacity", Range(0, 1)) = 1
    }
    SubShader
    {
        // Render before world-space UI (default transparent queue 3000).
        // Large transparent mesh-chunk bounds otherwise make Unity's distance
        // sorting place the mesh over menus that are physically in front.
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-100" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            half _ColorMode;
            half _Opacity;
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                if (_ColorMode > 0.5h)
                    return half4(input.color.rgb, _Opacity);
                float3 normal = normalize(cross(ddy(input.positionWS), ddx(input.positionWS)));
                return half4(normal * 0.5 + 0.5, _Opacity);
            }
            ENDHLSL
        }
    }
}
