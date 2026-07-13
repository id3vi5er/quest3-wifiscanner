Texture2DArray<float> drmDepthTexture;
Texture2DArray<float4> drmDepthNormalTexture;
uint2 drmDepthTextureSize;
SamplerState drmPointClampSampler;

float4x4 drmDepthProjection[2];
float4x4 drmDepthProjectionInverse[2];
float4x4 drmDepthView[2];
float4x4 drmDepthViewInverse[2];

float3 drmDepthEyePosition(int eye = 0)
{
    return drmDepthViewInverse[eye]._m03_m13_m23;
}

float drmSampleDepth(float2 uv, int eye = 0)
{
    return drmDepthTexture.SampleLevel(drmPointClampSampler, float3(uv, eye), 0);
}

float3 drmSampleNormal(float2 uv, int eye = 0)
{
    return drmDepthNormalTexture.SampleLevel(
        drmPointClampSampler, float3(uv, eye), 0).xyz;
}

float4 drmWorldToClip(float3 world, int eye = 0)
{
    return mul(drmDepthProjection[eye],
        mul(drmDepthView[eye], float4(world, 1)));
}

float3 drmClipToNdc(float4 clip)
{
    return clip.xyz / clip.w * 0.5 + 0.5;
}

float3 drmNdcToWorld(float3 ndc, int eye = 0)
{
    float4 clip = float4(ndc * 2.0 - 1.0, 1.0);
    float4 world = mul(drmDepthViewInverse[eye],
        mul(drmDepthProjectionInverse[eye], clip));
    return world.xyz / world.w;
}

float3 drmDepthPixelToWorld(uint2 pixel, int eye = 0)
{
    float2 uv = (float2)pixel / drmDepthTextureSize;
    return drmNdcToWorld(float3(uv, drmSampleDepth(uv, eye)), eye);
}
