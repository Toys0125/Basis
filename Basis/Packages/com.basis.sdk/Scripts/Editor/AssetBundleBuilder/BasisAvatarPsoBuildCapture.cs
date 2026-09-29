using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class BasisAvatarPsoBuildCapture
{
    public sealed class CaptureResult : IDisposable
    {
        public string Path;
        public int VariantCount;

        public void Dispose()
        {
            try
            {
                if (!string.IsNullOrEmpty(Path) && File.Exists(Path))
                    File.Delete(Path);
            }
            catch
            {
            }
            Path = null;
        }
    }

    public static CaptureResult Capture(GameObject source, string buildId, BuildTarget target)
    {
        if (source == null || !IsWindowsTarget(target))
            return null;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            BasisDebug.LogWarning(
                $"Avatar PSO capture requested for {target}, but this Unity Editor is running {SystemInfo.graphicsDeviceType}. " +
                "Launch the Editor with -force-vulkan to create a Vulkan GraphicsStateCollection.");
            return null;
        }

        PreviewRenderUtility preview = new PreviewRenderUtility();
        GameObject clone = null;
        GraphicsStateCollection collection = null;
        try
        {
            clone = Object.Instantiate(source);
            preview.AddSingleGO(clone);
            clone.SetActive(true);
            Canvas.ForceUpdateCanvases();

            if (!TryComputeRendererBounds(clone, out Bounds bounds))
            {
                BasisDebug.LogWarning("Avatar PSO capture skipped: avatar has no renderers.");
                return null;
            }

            Camera camera = preview.camera;
            camera.fieldOfView = 35f;
            camera.nearClipPlane = 0.01f;
            camera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;

            preview.lights[0].intensity = 1.2f;
            preview.lights[0].shadows = LightShadows.Soft;
            preview.lights[1].intensity = 0.45f;
            preview.lights[1].shadows = LightShadows.Hard;
            preview.ambientColor = new Color(0.35f, 0.35f, 0.35f, 1f);

            collection = new GraphicsStateCollection();
            collection.BeginTrace();
            if (!collection.isTracing)
            {
                BasisDebug.LogWarning("Avatar PSO capture could not begin GraphicsStateCollection tracing.");
                return null;
            }

            float[] yawAngles = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
            float[] pitches = { 0f, 12f, -8f };
            int frame = 0;
            for (int pitchIndex = 0; pitchIndex < pitches.Length; pitchIndex++)
            {
                for (int yawIndex = 0; yawIndex < yawAngles.Length; yawIndex++)
                {
                    FrameCamera(preview, bounds, yawAngles[yawIndex], pitches[pitchIndex]);
                    // A tiny per-frame movement gives motion-vector-enabled renderers an actual
                    // object transform delta while remaining visually inside the same framing.
                    clone.transform.localPosition = new Vector3((frame & 1) == 0 ? 0f : 0.002f, 0f, 0f);
                    Texture2D frameTexture = null;
                    try
                    {
                        preview.BeginStaticPreview(new Rect(0f, 0f, 512f, 512f));
                        preview.Render(true);
                        frameTexture = preview.EndStaticPreview();
                    }
                    finally
                    {
                        if (frameTexture != null)
                            Object.DestroyImmediate(frameTexture);
                    }
                    frame++;
                }
            }

            collection.EndTrace();
            int variants = collection.variantCount;
            if (variants <= 0)
            {
                BasisDebug.LogWarning("Avatar PSO capture completed but traced zero graphics states.");
                return null;
            }

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BasisAvatarPso");
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{Sanitize(buildId)}.{target}.{Guid.NewGuid():N}.gpsc");
            collection.SaveToFile(path);
            BasisDebug.Log($"Avatar PSO capture: {variants} Vulkan graphics state(s) -> {path}", BasisDebug.LogTag.Event);
            return new CaptureResult { Path = path, VariantCount = variants };
        }
        catch (Exception ex)
        {
            if (collection != null && collection.isTracing)
            {
                try { collection.EndTrace(); } catch { }
            }
            BasisDebug.LogWarning($"Avatar PSO capture failed; bundle will be built without a PSO sidecar ({ex.Message})");
            return null;
        }
        finally
        {
            if (clone != null)
                Object.DestroyImmediate(clone);
            preview.Cleanup();
        }
    }

    public static async Task<(BasisBundleGenerated Generated, string EncryptedPath)> EncryptCaptureAsync(CaptureResult capture, BasisBundleGenerated contentSection, string password)
    {
        if (capture == null || string.IsNullOrEmpty(capture.Path) || !File.Exists(capture.Path) || contentSection == null)
            return (null, null);

        byte[] raw = await File.ReadAllBytesAsync(capture.Path);
        if (raw.Length == 0)
            return (null, null);

        string sectionKey = $"{contentSection.AssetBundleHash}.Vulkan.{Application.unityVersion}.BasisPsoV{BasisBundleConnector.AvatarPsoRenderConfigVersion}";
        contentSection.PsoSectionKey = sectionKey;

        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
        byte[] encrypted = await BasisEncryptionWrapper.EncryptToBytesAsync(
            BasisGenerateUniqueID.GenerateUniqueID(),
            basisPassword,
            raw,
            new BasisProgressReport());

        if (encrypted == null || encrypted.Length == 0)
        {
            contentSection.PsoSectionKey = null;
            return (null, null);
        }

        string encryptedPath = System.IO.Path.ChangeExtension(capture.Path, ".gpsc.beb");
        await File.WriteAllBytesAsync(encryptedPath, encrypted);

        BasisBundleGenerated generated = new BasisBundleGenerated(
            ComputeSha256(raw),
            BasisBundleConnector.GraphicsStateCollectionAssetMode,
            System.IO.Path.GetFileName(capture.Path),
            0,
            true,
            password,
            contentSection.Platform + ".VulkanPSO",
            encrypted.LongLength,
            new[] { GraphicsDeviceType.Vulkan.ToString() })
        {
            PsoSectionKey = sectionKey,
            PsoFormatVersion = BasisBundleConnector.AvatarPsoFormatVersion,
            PsoGraphicsApi = GraphicsDeviceType.Vulkan.ToString(),
            PsoUnityVersion = Application.unityVersion,
            PsoRenderConfigVersion = BasisBundleConnector.AvatarPsoRenderConfigVersion,
            PsoForAssetBundleHash = contentSection.AssetBundleHash,
        };

        return (generated, encryptedPath);
    }

    public static bool IsWindowsTarget(BuildTarget target)
    {
        return target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64;
    }

    private static bool TryComputeRendererBounds(GameObject root, out Bounds bounds)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        bounds = new Bounds(root.transform.position, Vector3.one);
        bool found = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled)
                continue;
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        return found;
    }

    private static void FrameCamera(PreviewRenderUtility preview, Bounds bounds, float yaw, float pitch)
    {
        Camera camera = preview.camera;
        float distance = bounds.extents.magnitude * 2.3f + 0.1f;
        Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
        camera.transform.SetPositionAndRotation(bounds.center + orbit * (Vector3.back * distance), orbit);
        camera.farClipPlane = distance * 6f + 10f;
        preview.lights[0].transform.rotation = Quaternion.Euler(38f, yaw - 35f, 0f);
        preview.lights[1].transform.rotation = Quaternion.Euler(315f, yaw + 145f, 0f);
    }

    private static string ComputeSha256(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "avatar";
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_'))
                chars[i] = '_';
        }
        return new string(chars);
    }
}
