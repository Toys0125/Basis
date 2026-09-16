using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

internal static class BasisFarLodBurstAtlas
{
    internal struct ViewData
    {
        public Vector3 DirectionWorld;
        public Matrix4x4 WorldToPixel;
        public Color32[] Pixels;
        public byte[] GroupIds;
        public ushort[] Depth16;
        public Vector3 CameraPositionWorld;
        public float DepthNear;
        public float DepthFar;
        public float DepthToleranceMeters;
        public int Size;
        public bool IsRegion;
        public Bounds ValidBoundsRoot;
    }

    private struct BurstView
    {
        public float3 DirectionWorld;
        public float4x4 WorldToPixel;
        public float3 CameraPositionWorld;
        public float DepthNear;
        public float DepthFar;
        public float DepthToleranceMeters;
        public float3 BoundsMin;
        public float3 BoundsMax;
        public int PixelOffset;
        public int GroupOffset;
        public int DepthOffset;
        public int Size;
        public byte IsRegion;
    }

    private struct Candidate
    {
        public int ViewIndex;
        public float Score;
    }

    [BurstCompile]
    private struct ProjectBandJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float3> Normals;
        [ReadOnly] public NativeArray<float2> Uv;
        [ReadOnly] public NativeArray<int> Indices;
        [ReadOnly] public NativeArray<byte> TexelGroups;
        [ReadOnly] public NativeArray<byte> TexelHidden;
        [ReadOnly] public NativeArray<float> VertexAo;
        [ReadOnly] public NativeArray<BurstView> Views;
        [ReadOnly] public NativeArray<Color32> ViewPixels;
        [ReadOnly] public NativeArray<byte> ViewGroups;
        [ReadOnly] public NativeArray<ushort> ViewDepth;

        [NativeDisableParallelForRestriction] public NativeArray<Color32> Atlas;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Quality;

        public float4x4 RootToWorld;
        public quaternion RootRotation;
        public int AtlasSize;
        public int BandHeight;
        public int ViewCount;
        public byte HasTexelGroups;
        public byte HasTexelHidden;
        public byte HasVertexAo;
        public byte FlipSampleY;
        public float AoBakeStrength;
        public float MinViewFacing;
        public float RegionMinFacing;
        public float RegionScoreBias;
        public float MinCoverage;
        public float FallbackCoverage;

        public void Execute(int band)
        {
            int bandStartY = band * BandHeight;
            int bandEndY = math.min(AtlasSize - 1, bandStartY + BandHeight - 1);
            FixedList512Bytes<Candidate> candidates = default;

            for (int t = 0; t + 2 < Indices.Length; t += 3)
            {
                int i0 = Indices[t];
                int i1 = Indices[t + 1];
                int i2 = Indices[t + 2];

                if (HasTexelHidden != 0 && TexelHidden[i0] != 0 && TexelHidden[i1] != 0 && TexelHidden[i2] != 0)
                {
                    continue;
                }

                float2 uv0 = Uv[i0] * AtlasSize;
                float2 uv1 = Uv[i1] * AtlasSize;
                float2 uv2 = Uv[i2] * AtlasSize;

                float minX = math.min(uv0.x, math.min(uv1.x, uv2.x)) - 1f;
                float maxX = math.max(uv0.x, math.max(uv1.x, uv2.x)) + 1f;
                float minY = math.min(uv0.y, math.min(uv1.y, uv2.y)) - 1f;
                float maxY = math.max(uv0.y, math.max(uv1.y, uv2.y)) + 1f;
                if ((int)math.ceil(maxY) < bandStartY || (int)math.floor(minY) > bandEndY)
                {
                    continue;
                }

                int startX = math.clamp((int)math.floor(minX), 0, AtlasSize - 1);
                int endX = math.clamp((int)math.ceil(maxX), 0, AtlasSize - 1);
                int startY = math.clamp((int)math.floor(minY), bandStartY, bandEndY);
                int endY = math.clamp((int)math.ceil(maxY), bandStartY, bandEndY);

                float2 edge0 = uv1 - uv0;
                float2 edge1 = uv2 - uv0;
                float denominator = edge0.x * edge1.y - edge0.y * edge1.x;
                if (math.abs(denominator) < 1e-8f)
                {
                    continue;
                }
                float inverseDenominator = 1f / denominator;

                byte allowed0 = 255;
                byte allowed1 = 255;
                byte allowed2 = 255;
                if (HasTexelGroups != 0)
                {
                    allowed0 = TexelGroups[i0];
                    allowed1 = TexelGroups[i1];
                    allowed2 = TexelGroups[i2];
                }

                float3 p0 = Positions[i0];
                float3 p1 = Positions[i1];
                float3 p2 = Positions[i2];
                float3 n0 = Normals[i0];
                float3 n1 = Normals[i1];
                float3 n2 = Normals[i2];
                float ao0 = HasVertexAo != 0 ? VertexAo[i0] : 1f;
                float ao1 = HasVertexAo != 0 ? VertexAo[i1] : 1f;
                float ao2 = HasVertexAo != 0 ? VertexAo[i2] : 1f;

                float baryBStepX = edge1.y * inverseDenominator;
                float baryCStepX = -edge0.y * inverseDenominator;

                for (int y = startY; y <= endY; y++)
                {
                    float2 rowPoint = new float2(startX + 0.5f, y + 0.5f) - uv0;
                    float baryB = (rowPoint.x * edge1.y - rowPoint.y * edge1.x) * inverseDenominator;
                    float baryC = (edge0.x * rowPoint.y - edge0.y * rowPoint.x) * inverseDenominator;

                    for (int x = startX; x <= endX; x++)
                    {
                        float baryA = 1f - baryB - baryC;
                        const float slack = -0.08f;
                        if (baryA >= slack && baryB >= slack && baryC >= slack)
                        {
                            bool interior = baryA >= 0f && baryB >= 0f && baryC >= 0f;
                            int texelIndex = y * AtlasSize + x;
                            byte wantedQuality = interior ? (byte)2 : (byte)1;
                            if (Quality[texelIndex] < wantedQuality)
                            {
                                float3 positionRoot = p0 * baryA + p1 * baryB + p2 * baryC;
                                float3 normalRoot = n0 * baryA + n1 * baryB + n2 * baryC;
                                float3 positionWorld = TransformPoint3x4(RootToWorld, positionRoot);
                                float3 normalWorld = math.normalizesafe(math.mul(RootRotation, normalRoot));
                                float aoFactor = 1f;
                                if (HasVertexAo != 0)
                                {
                                    float ao = math.saturate(ao0 * baryA + ao1 * baryB + ao2 * baryC);
                                    aoFactor = math.lerp(1f, ao, AoBakeStrength);
                                }

                                candidates.Clear();
                                BuildCandidates(ref candidates, positionRoot, normalWorld, MinViewFacing, true);
                                if (candidates.Length == 0)
                                {
                                    BuildCandidates(ref candidates, positionRoot, normalWorld, 0.05f, false);
                                }
                                SortCandidates(ref candidates);

                                bool sampled = false;
                                Color32 fallbackColor = default;
                                bool hasFallback = false;
                                int consider = math.min(candidates.Length, 6);
                                for (int c = 0; c < consider && !sampled; c++)
                                {
                                    BurstView view = Views[candidates[c].ViewIndex];
                                    if (!TrySampleView(view, positionWorld, allowed0, allowed1, allowed2, 1f, out Color32 color, out float coverage))
                                    {
                                        continue;
                                    }
                                    if (!hasFallback && coverage >= FallbackCoverage)
                                    {
                                        fallbackColor = color;
                                        hasFallback = true;
                                    }
                                    if (coverage < MinCoverage)
                                    {
                                        continue;
                                    }
                                    Atlas[texelIndex] = ApplyAo(color, aoFactor);
                                    Quality[texelIndex] = wantedQuality;
                                    sampled = true;
                                }

                                if (!sampled && hasFallback)
                                {
                                    Atlas[texelIndex] = ApplyAo(fallbackColor, aoFactor);
                                    Quality[texelIndex] = wantedQuality;
                                    sampled = true;
                                }

                                if (!sampled)
                                {
                                    for (int c = 0; c < candidates.Length && !sampled; c++)
                                    {
                                        BurstView view = Views[candidates[c].ViewIndex];
                                        if (TrySampleView(view, positionWorld, allowed0, allowed1, allowed2, 3f, out Color32 rescueColor, out float rescueCoverage)
                                            && rescueCoverage >= 0.05f)
                                        {
                                            Atlas[texelIndex] = ApplyAo(rescueColor, aoFactor);
                                            Quality[texelIndex] = 1;
                                            sampled = true;
                                        }
                                    }
                                }
                            }
                        }

                        baryB += baryBStepX;
                        baryC += baryCStepX;
                    }
                }
            }
        }

        private void BuildCandidates(ref FixedList512Bytes<Candidate> candidates, float3 positionRoot, float3 normalWorld, float facingFloor, bool applyRegionBias)
        {
            for (int v = 0; v < ViewCount; v++)
            {
                BurstView view = Views[v];
                if (view.IsRegion != 0 && !Contains(view.BoundsMin, view.BoundsMax, positionRoot))
                {
                    continue;
                }
                float facing = math.dot(normalWorld, -view.DirectionWorld);
                if (facing > facingFloor)
                {
                    float score = facing;
                    if (applyRegionBias && view.IsRegion != 0 && facing > RegionMinFacing)
                    {
                        score += RegionScoreBias;
                    }
                    candidates.Add(new Candidate { ViewIndex = v, Score = score });
                }
            }
        }

        private static void SortCandidates(ref FixedList512Bytes<Candidate> candidates)
        {
            for (int a = 1; a < candidates.Length; a++)
            {
                Candidate candidate = candidates[a];
                int b = a - 1;
                while (b >= 0 && candidates[b].Score < candidate.Score)
                {
                    candidates[b + 1] = candidates[b];
                    b--;
                }
                candidates[b + 1] = candidate;
            }
        }

        private bool TrySampleView(BurstView view, float3 positionWorld, byte allowed0, byte allowed1, byte allowed2,
            float depthToleranceScale, out Color32 color, out float coverage)
        {
            float3 pixel = TransformPoint(view.WorldToPixel, positionWorld);
            if (FlipSampleY != 0)
            {
                pixel.y = view.Size - pixel.y;
            }

            float depthRange = math.max(view.DepthFar - view.DepthNear, 1e-4f);
            float viewDepth = math.dot(positionWorld - view.CameraPositionWorld, view.DirectionWorld);
            float expectedDepth16 = math.saturate((viewDepth - view.DepthNear) / depthRange) * 65535f;
            float depthTolerance16 = view.DepthToleranceMeters * depthToleranceScale / depthRange * 65535f;

            float fx = pixel.x - 0.5f;
            float fy = pixel.y - 0.5f;
            int x0 = (int)math.floor(fx);
            int y0 = (int)math.floor(fy);
            if (x0 < -1 || y0 < -1 || x0 >= view.Size || y0 >= view.Size)
            {
                color = default;
                coverage = 0f;
                return false;
            }

            float tx = fx - x0;
            float ty = fy - y0;
            float r = 0f;
            float g = 0f;
            float b = 0f;
            float weightedCoverage = 0f;
            float totalWeight = 0f;
            for (int dy = 0; dy <= 1; dy++)
            {
                int sy = y0 + dy;
                if (sy < 0 || sy >= view.Size)
                {
                    continue;
                }
                float wy = dy == 0 ? 1f - ty : ty;
                for (int dx = 0; dx <= 1; dx++)
                {
                    int sx = x0 + dx;
                    if (sx < 0 || sx >= view.Size)
                    {
                        continue;
                    }
                    float weight = wy * (dx == 0 ? 1f - tx : tx);
                    if (weight <= 0f)
                    {
                        continue;
                    }
                    int viewIndex = sy * view.Size + sx;
                    if (view.GroupOffset >= 0 && allowed0 != 255)
                    {
                        byte pixelGroup = ViewGroups[view.GroupOffset + viewIndex];
                        if (pixelGroup != allowed0 && pixelGroup != allowed1 && pixelGroup != allowed2)
                        {
                            totalWeight += weight;
                            continue;
                        }
                    }
                    if (math.abs((float)ViewDepth[view.DepthOffset + viewIndex] - expectedDepth16) > depthTolerance16)
                    {
                        totalWeight += weight;
                        continue;
                    }

                    Color32 sample = ViewPixels[view.PixelOffset + viewIndex];
                    float sampleCoverage = sample.a * (1f / 255f);
                    float colorWeight = weight * sampleCoverage;
                    r += sample.r * colorWeight;
                    g += sample.g * colorWeight;
                    b += sample.b * colorWeight;
                    weightedCoverage += sampleCoverage * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= 0f || weightedCoverage <= 1e-4f)
            {
                color = default;
                coverage = 0f;
                return false;
            }

            float inverseColorWeight = 1f / math.max(weightedCoverage, 1e-4f);
            color = new Color32
            {
                r = ToByte(r * inverseColorWeight),
                g = ToByte(g * inverseColorWeight),
                b = ToByte(b * inverseColorWeight),
                a = 255,
            };
            coverage = weightedCoverage / totalWeight;
            return true;
        }

        private static Color32 ApplyAo(Color32 color, float aoFactor)
        {
            return new Color32
            {
                r = ToByte(color.r * aoFactor),
                g = ToByte(color.g * aoFactor),
                b = ToByte(color.b * aoFactor),
                a = 255,
            };
        }

        private static byte ToByte(float value)
        {
            return (byte)math.clamp((int)math.round(value), 0, 255);
        }

        private static bool Contains(float3 min, float3 max, float3 point)
        {
            return math.all(point >= min) && math.all(point <= max);
        }

        private static float3 TransformPoint3x4(float4x4 matrix, float3 point)
        {
            return math.mul(matrix, new float4(point, 1f)).xyz;
        }

        private static float3 TransformPoint(float4x4 matrix, float3 point)
        {
            float4 transformed = math.mul(matrix, new float4(point, 1f));
            return math.abs(transformed.w) > 1e-8f ? transformed.xyz / transformed.w : transformed.xyz;
        }
    }

    [BurstCompile]
    private struct DilateRowsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Current;
        [ReadOnly] public NativeArray<Color32> AtlasRead;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Next;
        [NativeDisableParallelForRestriction] public NativeArray<Color32> AtlasWrite;
        [WriteOnly] public NativeArray<byte> ChangedRows;
        public int Size;

        public void Execute(int y)
        {
            bool changed = false;
            for (int x = 0; x < Size; x++)
            {
                int index = y * Size + x;
                byte quality = Current[index];
                Next[index] = quality;
                if (quality > 0)
                {
                    AtlasWrite[index] = AtlasRead[index];
                    continue;
                }

                int r = 0;
                int g = 0;
                int b = 0;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= Size)
                    {
                        continue;
                    }
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= Size)
                        {
                            continue;
                        }
                        int neighbor = ny * Size + nx;
                        if (Current[neighbor] == 0)
                        {
                            continue;
                        }
                        Color32 sample = AtlasRead[neighbor];
                        r += sample.r;
                        g += sample.g;
                        b += sample.b;
                        count++;
                    }
                }

                if (count > 0)
                {
                    AtlasWrite[index] = new Color32
                    {
                        r = (byte)(r / count),
                        g = (byte)(g / count),
                        b = (byte)(b / count),
                        a = 255,
                    };
                    Next[index] = 1;
                    changed = true;
                }
                else
                {
                    AtlasWrite[index] = AtlasRead[index];
                }
            }
            ChangedRows[y] = changed ? (byte)1 : (byte)0;
        }
    }

    internal static bool IsAvailable
    {
        get
        {
            try
            {
                return BurstCompiler.IsEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool TryProject(ViewData[] views, Matrix4x4 rootToWorld, Quaternion rootRotation,
        Vector3[] positions, Vector3[] normals, Vector2[] uv, int[] indices, int atlasSize,
        byte[] texelGroups, byte[] texelHidden, float[] vertexAo, bool flipSampleY,
        float aoBakeStrength, float minViewFacing, float regionMinFacing, float regionScoreBias,
        float minCoverage, float fallbackCoverage, out Color32[] atlas, out byte[] quality, out string failure)
    {
        atlas = null;
        quality = null;
        failure = null;
        if (!IsAvailable)
        {
            failure = "Burst is disabled";
            return false;
        }
        if (views == null || views.Length == 0 || views.Length > default(FixedList512Bytes<Candidate>).Capacity)
        {
            failure = views == null || views.Length == 0 ? "no capture views" : $"{views.Length} views exceed the Burst candidate capacity";
            return false;
        }
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i].Depth16 == null || views[i].Pixels == null)
            {
                failure = "a capture view has no depth or pixel buffer";
                return false;
            }
        }

        NativeArray<float3> nativePositions = default;
        NativeArray<float3> nativeNormals = default;
        NativeArray<float2> nativeUv = default;
        NativeArray<int> nativeIndices = default;
        NativeArray<byte> nativeTexelGroups = default;
        NativeArray<byte> nativeTexelHidden = default;
        NativeArray<float> nativeVertexAo = default;
        NativeArray<BurstView> nativeViews = default;
        NativeArray<Color32> nativePixels = default;
        NativeArray<byte> nativeGroups = default;
        NativeArray<ushort> nativeDepth = default;
        NativeArray<Color32> nativeAtlas = default;
        NativeArray<byte> nativeQuality = default;
        try
        {
            nativePositions = new NativeArray<float3>(positions.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativeNormals = new NativeArray<float3>(normals.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativeUv = new NativeArray<float2>(uv.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativeIndices = new NativeArray<int>(indices, Allocator.TempJob);
            for (int i = 0; i < positions.Length; i++) nativePositions[i] = ToFloat3(positions[i]);
            for (int i = 0; i < normals.Length; i++) nativeNormals[i] = ToFloat3(normals[i]);
            for (int i = 0; i < uv.Length; i++) nativeUv[i] = new float2(uv[i].x, uv[i].y);

            if (texelGroups != null) nativeTexelGroups = new NativeArray<byte>(texelGroups, Allocator.TempJob);
            else nativeTexelGroups = new NativeArray<byte>(1, Allocator.TempJob);
            if (texelHidden != null) nativeTexelHidden = new NativeArray<byte>(texelHidden, Allocator.TempJob);
            else nativeTexelHidden = new NativeArray<byte>(1, Allocator.TempJob);
            if (vertexAo != null) nativeVertexAo = new NativeArray<float>(vertexAo, Allocator.TempJob);
            else nativeVertexAo = new NativeArray<float>(1, Allocator.TempJob);

            long pixelTotalLong = 0;
            long groupTotalLong = 0;
            long depthTotalLong = 0;
            for (int i = 0; i < views.Length; i++)
            {
                pixelTotalLong += views[i].Pixels.Length;
                if (views[i].GroupIds != null) groupTotalLong += views[i].GroupIds.Length;
                depthTotalLong += views[i].Depth16.Length;
            }
            if (pixelTotalLong > int.MaxValue || groupTotalLong > int.MaxValue || depthTotalLong > int.MaxValue)
            {
                failure = "capture buffers are too large for the Burst projection path";
                return false;
            }

            nativeViews = new NativeArray<BurstView>(views.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativePixels = new NativeArray<Color32>((int)pixelTotalLong, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativeGroups = new NativeArray<byte>(math.max(1, (int)groupTotalLong), Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nativeDepth = new NativeArray<ushort>((int)depthTotalLong, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            int pixelOffset = 0;
            int groupOffset = 0;
            int depthOffset = 0;
            for (int i = 0; i < views.Length; i++)
            {
                ViewData source = views[i];
                NativeArray<Color32>.Copy(source.Pixels, 0, nativePixels, pixelOffset, source.Pixels.Length);
                int thisGroupOffset = -1;
                if (source.GroupIds != null)
                {
                    thisGroupOffset = groupOffset;
                    NativeArray<byte>.Copy(source.GroupIds, 0, nativeGroups, groupOffset, source.GroupIds.Length);
                    groupOffset += source.GroupIds.Length;
                }
                NativeArray<ushort>.Copy(source.Depth16, 0, nativeDepth, depthOffset, source.Depth16.Length);
                Vector3 boundsMin = source.ValidBoundsRoot.min;
                Vector3 boundsMax = source.ValidBoundsRoot.max;
                nativeViews[i] = new BurstView
                {
                    DirectionWorld = ToFloat3(source.DirectionWorld),
                    WorldToPixel = ToFloat4x4(source.WorldToPixel),
                    CameraPositionWorld = ToFloat3(source.CameraPositionWorld),
                    DepthNear = source.DepthNear,
                    DepthFar = source.DepthFar,
                    DepthToleranceMeters = source.DepthToleranceMeters,
                    BoundsMin = ToFloat3(boundsMin),
                    BoundsMax = ToFloat3(boundsMax),
                    PixelOffset = pixelOffset,
                    GroupOffset = thisGroupOffset,
                    DepthOffset = depthOffset,
                    Size = source.Size,
                    IsRegion = source.IsRegion ? (byte)1 : (byte)0,
                };
                pixelOffset += source.Pixels.Length;
                depthOffset += source.Depth16.Length;
            }

            int texelCount = checked(atlasSize * atlasSize);
            nativeAtlas = new NativeArray<Color32>(texelCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            nativeQuality = new NativeArray<byte>(texelCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            int bandHeight = math.max(16, atlasSize / (math.clamp(SystemInfo.processorCount, 1, 16) * 4));
            int bandCount = (atlasSize + bandHeight - 1) / bandHeight;

            ProjectBandJob job = new ProjectBandJob
            {
                Positions = nativePositions,
                Normals = nativeNormals,
                Uv = nativeUv,
                Indices = nativeIndices,
                TexelGroups = nativeTexelGroups,
                TexelHidden = nativeTexelHidden,
                VertexAo = nativeVertexAo,
                Views = nativeViews,
                ViewPixels = nativePixels,
                ViewGroups = nativeGroups,
                ViewDepth = nativeDepth,
                Atlas = nativeAtlas,
                Quality = nativeQuality,
                RootToWorld = ToFloat4x4(rootToWorld),
                RootRotation = new quaternion(rootRotation.x, rootRotation.y, rootRotation.z, rootRotation.w),
                AtlasSize = atlasSize,
                BandHeight = bandHeight,
                ViewCount = views.Length,
                HasTexelGroups = texelGroups != null ? (byte)1 : (byte)0,
                HasTexelHidden = texelHidden != null ? (byte)1 : (byte)0,
                HasVertexAo = vertexAo != null ? (byte)1 : (byte)0,
                FlipSampleY = flipSampleY ? (byte)1 : (byte)0,
                AoBakeStrength = aoBakeStrength,
                MinViewFacing = minViewFacing,
                RegionMinFacing = regionMinFacing,
                RegionScoreBias = regionScoreBias,
                MinCoverage = minCoverage,
                FallbackCoverage = fallbackCoverage,
            };
            job.Schedule(bandCount, 1).Complete();

            atlas = nativeAtlas.ToArray();
            quality = nativeQuality.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            atlas = null;
            quality = null;
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (nativePositions.IsCreated) nativePositions.Dispose();
            if (nativeNormals.IsCreated) nativeNormals.Dispose();
            if (nativeUv.IsCreated) nativeUv.Dispose();
            if (nativeIndices.IsCreated) nativeIndices.Dispose();
            if (nativeTexelGroups.IsCreated) nativeTexelGroups.Dispose();
            if (nativeTexelHidden.IsCreated) nativeTexelHidden.Dispose();
            if (nativeVertexAo.IsCreated) nativeVertexAo.Dispose();
            if (nativeViews.IsCreated) nativeViews.Dispose();
            if (nativePixels.IsCreated) nativePixels.Dispose();
            if (nativeGroups.IsCreated) nativeGroups.Dispose();
            if (nativeDepth.IsCreated) nativeDepth.Dispose();
            if (nativeAtlas.IsCreated) nativeAtlas.Dispose();
            if (nativeQuality.IsCreated) nativeQuality.Dispose();
        }
    }

    internal static bool TryDilate(Color32[] atlas, byte[] texelQuality, int atlasSize, int passes, out byte[] finalQuality, out string failure)
    {
        finalQuality = null;
        failure = null;
        if (!IsAvailable)
        {
            failure = "Burst is disabled";
            return false;
        }
        if (atlas == null || texelQuality == null || atlas.Length != texelQuality.Length || atlas.Length != atlasSize * atlasSize)
        {
            failure = "invalid atlas buffers";
            return false;
        }

        NativeArray<Color32> atlasA = default;
        NativeArray<Color32> atlasB = default;
        NativeArray<byte> qualityA = default;
        NativeArray<byte> qualityB = default;
        NativeArray<byte> changedRows = default;
        try
        {
            atlasA = new NativeArray<Color32>(atlas, Allocator.TempJob);
            atlasB = new NativeArray<Color32>(atlas.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            qualityA = new NativeArray<byte>(texelQuality, Allocator.TempJob);
            qualityB = new NativeArray<byte>(texelQuality.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            changedRows = new NativeArray<byte>(atlasSize, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            NativeArray<Color32> readAtlas = atlasA;
            NativeArray<Color32> writeAtlas = atlasB;
            NativeArray<byte> current = qualityA;
            NativeArray<byte> next = qualityB;
            for (int pass = 0; pass < passes; pass++)
            {
                DilateRowsJob job = new DilateRowsJob
                {
                    Current = current,
                    AtlasRead = readAtlas,
                    Next = next,
                    AtlasWrite = writeAtlas,
                    ChangedRows = changedRows,
                    Size = atlasSize,
                };
                job.Schedule(atlasSize, 8).Complete();

                bool any = false;
                for (int y = 0; y < atlasSize; y++)
                {
                    if (changedRows[y] != 0)
                    {
                        any = true;
                        break;
                    }
                }

                (readAtlas, writeAtlas) = (writeAtlas, readAtlas);
                (current, next) = (next, current);
                if (!any)
                {
                    break;
                }
            }

            readAtlas.CopyTo(atlas);
            finalQuality = current.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            finalQuality = null;
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (atlasA.IsCreated) atlasA.Dispose();
            if (atlasB.IsCreated) atlasB.Dispose();
            if (qualityA.IsCreated) qualityA.Dispose();
            if (qualityB.IsCreated) qualityB.Dispose();
            if (changedRows.IsCreated) changedRows.Dispose();
        }
    }

    private static float3 ToFloat3(Vector3 value)
    {
        return new float3(value.x, value.y, value.z);
    }

    private static float4x4 ToFloat4x4(Matrix4x4 matrix)
    {
        return new float4x4(
            new float4(matrix.m00, matrix.m10, matrix.m20, matrix.m30),
            new float4(matrix.m01, matrix.m11, matrix.m21, matrix.m31),
            new float4(matrix.m02, matrix.m12, matrix.m22, matrix.m32),
            new float4(matrix.m03, matrix.m13, matrix.m23, matrix.m33));
    }
}
