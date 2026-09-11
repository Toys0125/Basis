Shader "Hidden/Universal Render Pipeline/Motion Vector Edge Repair"
{
    HLSLINCLUDE
        #pragma target 3.5

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"

        TEXTURE2D_X(_MotionVectorRepairDepth);
        float4 _SourceSize;

        float SampleLinearDepth(float2 uv)
        {
            float rawDepth = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorRepairDepth, sampler_PointClamp, uv, 0).r;
            return LinearEyeDepth(rawDepth, _ZBufferParams);
        }

        float2 SampleMotion(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0).xy;
        }

        void AccumulateSameSurface(
            float2 uv,
            float centerDepth,
            inout float2 motionSum,
            inout float2 motionSquareSum,
            inout float sampleCount)
        {
            float sampleDepth = SampleLinearDepth(uv);
            float depthTolerance = max(0.01, centerDepth * 0.005);
            if (abs(sampleDepth - centerDepth) > depthTolerance)
                return;

            float2 motion = SampleMotion(uv);
            motionSum += motion;
            motionSquareSum += motion * motion;
            sampleCount += 1.0;
        }

        half4 FragMotionVectorEdgeRepair(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            float2 centerMotion = SampleMotion(uv);
            float centerDepth = SampleLinearDepth(uv);
            float2 texel = _SourceSize.zw;

            float2 motionSum = 0.0;
            float2 motionSquareSum = 0.0;
            float sampleCount = 0.0;

            // Only same-depth neighbours participate. This intentionally avoids the foreground-to-background
            // velocity expansion that produced halos with conservative rasterization.
            AccumulateSameSurface(uv + texel * float2(-1.0, -1.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2( 0.0, -1.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2( 1.0, -1.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2(-1.0,  0.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2( 1.0,  0.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2(-1.0,  1.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2( 0.0,  1.0), centerDepth, motionSum, motionSquareSum, sampleCount);
            AccumulateSameSurface(uv + texel * float2( 1.0,  1.0), centerDepth, motionSum, motionSquareSum, sampleCount);

            // Require a local consensus before touching a valid URP motion vector. This is an outlier repair,
            // not a blur: coherent vectors are left unchanged and motion discontinuities are preserved.
            if (sampleCount >= 3.0)
            {
                float2 averageMotion = motionSum / sampleCount;
                float2 variance = max(motionSquareSum / sampleCount - averageMotion * averageMotion, 0.0);
                float2 pixelScale = _SourceSize.xy;
                float neighborRmsPixels = sqrt(dot(variance, pixelScale * pixelScale));
                float centerErrorPixels = length((centerMotion - averageMotion) * pixelScale);

                if (centerErrorPixels > 0.5
                    && neighborRmsPixels < 1.0
                    && neighborRmsPixels < centerErrorPixels * 0.5)
                {
                    centerMotion = averageMotion;
                }
            }

            return half4(centerMotion, 0.0, 0.0);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off
        Blend Off

        Pass
        {
            Name "MotionVectorEdgeRepair"

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragMotionVectorEdgeRepair
            ENDHLSL
        }
    }

    Fallback Off
}
