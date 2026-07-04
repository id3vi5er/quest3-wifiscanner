// Unlit transparent URP shader with optional dynamic occlusion via the Quest's
// real-time environment depth ("Meta Quest: Occlusion" OpenXR feature).
//
// AR Foundation's ARShaderOcclusion sets these globals every frame:
//   _EnvironmentDepthTexture            Texture2DArray, 1 slice per eye, NDC depth
//   _EnvironmentDepthProjectionMatrices float4x4[2], depth-camera view-proj per eye
//   _NdcLinearConversionParameters      x = -2fn/(f-n), y = -(f+n)/(f-n)
//   _IsOcclusionOn                      int 1/0 (component enabled/disabled)
// plus the global keyword XR_HARD_OCCLUSION (enabled after the first depth frame).
//
// Without the keyword (Editor, occlusion off) this renders exactly like a
// transparent URP Unlit. multi_compile keeps the occlusion variant in the
// build even though the keyword is only enabled at runtime, and the stereo
// macros keep the single-pass-instanced variant alive (magenta-trap lessons).
Shader "WifiScanner/OccludableUnlit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ XR_HARD_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cull;
            CBUFFER_END

            #if defined(XR_HARD_OCCLUSION)
            TEXTURE2D_ARRAY(_EnvironmentDepthTexture);
            SAMPLER(sampler_EnvironmentDepthTexture);
            float4x4 _EnvironmentDepthProjectionMatrices[2];
            float4 _NdcLinearConversionParameters;
            int _IsOcclusionOn;
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

            #if defined(XR_HARD_OCCLUSION)
                if (_IsOcclusionOn == 1)
                {
                    uint eye = unity_StereoEyeIndex;
                    float4 depthClip = mul(_EnvironmentDepthProjectionMatrices[eye],
                                           float4(input.positionWS, 1.0));
                    // w = linear distance along the depth camera's forward axis
                    // (projection row 3 is (0,0,-1,0)).
                    if (depthClip.w > 0.0)
                    {
                        float3 ndc = depthClip.xyz / depthClip.w;
                        float2 depthUv = ndc.xy * 0.5 + 0.5;
                        // Outside the depth camera's view: leave visible.
                        if (all(depthUv >= 0.0) && all(depthUv <= 1.0))
                        {
                            float envNdc = SAMPLE_TEXTURE2D_ARRAY(
                                _EnvironmentDepthTexture,
                                sampler_EnvironmentDepthTexture,
                                depthUv, eye).r * 2.0 - 1.0;
                            float envLinear = _NdcLinearConversionParameters.x
                                / (envNdc + _NdcLinearConversionParameters.y);
                            // Bias against shimmering at grazing distances.
                            float bias = max(0.04, envLinear * 0.02);
                            if (depthClip.w > envLinear + bias)
                                discard;
                        }
                    }
                }
            #endif

                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
