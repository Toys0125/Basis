using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class BasisFarLodGenerationPerfTests
{
    [Test]
    public void Simplifier_160kTo8k_TimingReport()
    {
        const int side = 284;
        const int targetTriangles = 8000;
        int vertexCount = side * side;
        int sourceTriangles = (side - 1) * (side - 1) * 2;

        List<Vector3> positions = new List<Vector3>(vertexCount);
        List<byte> boneA = new List<byte>(vertexCount);
        List<byte> boneB = new List<byte>(vertexCount);
        List<byte> weightA = new List<byte>(vertexCount);
        List<byte> hidden = new List<byte>(vertexCount);
        for (int y = 0; y < side; y++)
        {
            float v = y / (float)(side - 1);
            for (int x = 0; x < side; x++)
            {
                float u = x / (float)(side - 1);
                float height = Mathf.Sin(u * Mathf.PI * 4f) * Mathf.Cos(v * Mathf.PI * 3f) * 0.035f;
                positions.Add(new Vector3(u, height, v));
                boneA.Add(0);
                boneB.Add(0);
                weightA.Add(255);
                hidden.Add(0);
            }
        }

        List<int> indices = new List<int>(sourceTriangles * 3);
        for (int y = 0; y < side - 1; y++)
        {
            int row = y * side;
            int nextRow = row + side;
            for (int x = 0; x < side - 1; x++)
            {
                int a = row + x;
                int b = a + 1;
                int c = nextRow + x;
                int d = c + 1;
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        BasisFarLodMeshSimplifier.Simplify(positions, boneA, boneB, weightA, hidden, indices, targetTriangles);
        stopwatch.Stop();

        int resultTriangles = indices.Count / 3;
        Debug.Log($"[FarAvatarPerfBench] Simplifier {sourceTriangles} -> {resultTriangles} tris, {positions.Count} verts: {stopwatch.Elapsed.TotalMilliseconds:0.00} ms");
        Assert.LessOrEqual(resultTriangles, targetTriangles);
        Assert.AreEqual(0, indices.Count % 3);
        Assert.AreEqual(positions.Count, boneA.Count);
        Assert.AreEqual(positions.Count, hidden.Count);
    }

    [Test]
    public void AtlasProjection_1024x18Views_TimingReport()
    {
        const int atlasSize = 1024;
        const int captureSize = 1024;
        const int viewCount = 18;

        Vector3[] positions =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f),
        };
        Vector3[] normals = { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        Vector2[] uv = { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        int[] indices = { 0, 1, 2, 0, 2, 3 };
        float[] vertexAo = { 1f, 1f, 1f, 1f };

        Color32[] pixels = new Color32[captureSize * captureSize];
        ushort[] depth = new ushort[pixels.Length];
        Color32 sourceColor = new Color32(81, 137, 211, 255);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = sourceColor;
            depth[i] = 32768;
        }

        Type bakerType = typeof(BasisFarLodAtlasBaker);
        Type captureViewType = bakerType.GetNestedType("CaptureView", BindingFlags.NonPublic);
        Assert.NotNull(captureViewType);
        Type listType = typeof(List<>).MakeGenericType(captureViewType);
        IList views = (IList)Activator.CreateInstance(listType);
        Assert.NotNull(views);

        Matrix4x4 worldToPixel = Matrix4x4.identity;
        worldToPixel.m00 = captureSize - 1;
        worldToPixel.m11 = captureSize - 1;
        for (int i = 0; i < viewCount; i++)
        {
            object view = Activator.CreateInstance(captureViewType);
            SetField(captureViewType, view, "DirectionWorld", Vector3.back);
            SetField(captureViewType, view, "WorldToPixel", worldToPixel);
            SetField(captureViewType, view, "Pixels", pixels);
            SetField(captureViewType, view, "GroupIds", null);
            SetField(captureViewType, view, "Depth16", depth);
            SetField(captureViewType, view, "CameraPositionWorld", new Vector3(0f, 0f, 1f));
            SetField(captureViewType, view, "DepthNear", 0f);
            SetField(captureViewType, view, "DepthFar", 2f);
            SetField(captureViewType, view, "DepthToleranceMeters", 0.1f);
            SetField(captureViewType, view, "Size", captureSize);
            SetField(captureViewType, view, "IsRegion", false);
            SetField(captureViewType, view, "ValidBoundsRoot", new Bounds(Vector3.one * 0.5f, Vector3.one * 4f));
            views.Add(view);
        }

        MethodInfo project = bakerType.GetMethod("ProjectAtlas", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(project);
        object[] arguments =
        {
            views,
            Matrix4x4.identity,
            Quaternion.identity,
            positions,
            normals,
            uv,
            indices,
            atlasSize,
            1f,
            null,
            null,
            vertexAo,
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        Color32[] atlas = (Color32[])project.Invoke(null, arguments);
        stopwatch.Stop();

        Assert.NotNull(atlas);
        Assert.AreEqual(atlasSize * atlasSize, atlas.Length);
        Color32 center = atlas[(atlasSize / 2) * atlasSize + atlasSize / 2];
        Assert.AreEqual(sourceColor.r, center.r);
        Assert.AreEqual(sourceColor.g, center.g);
        Assert.AreEqual(sourceColor.b, center.b);
        Debug.Log($"[FarAvatarPerfBench] Atlas projection {atlasSize}px / {viewCount} depth-backed views: {stopwatch.Elapsed.TotalMilliseconds:0.00} ms");
    }

    private static void SetField(Type type, object instance, string name, object value)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field, name);
        field.SetValue(instance, value);
    }
}
