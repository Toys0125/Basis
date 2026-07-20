#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Detects which build backends are actually available in the current Unity
/// installation and resolves a requested backend without making an unavailable
/// IL2CPP module break an otherwise valid build.
/// </summary>
public static class BasisBuildTargetCapabilities
{
    private const string Il2CppToken = "il2cpp";
    private const string MonoToken = "mono";

    public static bool TryResolveBackend(
        BuildTarget target,
        ScriptingImplementation requested,
        out ScriptingImplementation resolved,
        out string reason)
    {
        resolved = requested;
        reason = null;

        if (requested != ScriptingImplementation.IL2CPP)
        {
            return true;
        }

        if (IsScriptingBackendAvailable(target, requested, out reason))
        {
            return true;
        }

        string monoReason;
        if (IsScriptingBackendAvailable(target, ScriptingImplementation.Mono2x, out monoReason))
        {
            resolved = ScriptingImplementation.Mono2x;
            reason = $"IL2CPP is unavailable for {target} ({reason}). Mono is available ({monoReason}).";
            return true;
        }

        reason = $"Neither IL2CPP nor Mono is available for {target}. IL2CPP: {reason}; Mono: {monoReason}.";
        return false;
    }

    public static bool IsScriptingBackendAvailable(
        BuildTarget target,
        ScriptingImplementation backend,
        out string reason)
    {
        BuildTargetGroup group;
        try
        {
            group = BuildPipeline.GetBuildTargetGroup(target);
        }
        catch (Exception ex)
        {
            reason = $"Unity could not resolve the build target group: {ex.Message}";
            return false;
        }

        // Standalone has one group for Windows, Linux, and macOS, while the
        // installed Mac support module can expose only Mono variations. Use
        // the target-specific module layout before the group-level reflection
        // APIs so a Windows editor cannot mistake Windows IL2CPP for Mac IL2CPP.
        bool targetSpecificAvailability;
        if (TryGetMacIl2CppAvailability(target, backend, out targetSpecificAvailability, out reason))
        {
            return targetSpecificAvailability;
        }

        return IsScriptingBackendAvailable(group, backend, out reason);
    }

    public static bool IsScriptingBackendAvailable(
        BuildTargetGroup group,
        ScriptingImplementation backend,
        out string reason)
    {
        ScriptingImplementation[] available;
        string source;
        if (TryGetAvailableScriptingBackends(group, out available, out source))
        {
            bool found = Contains(available, backend);
            reason = found
                ? $"Unity reported {backend} for {group} via {source}."
                : $"Unity reported [{FormatBackends(available)}] for {group} via {source}.";
            return found;
        }

        // Unknown is intentionally treated as available. This keeps older
        // Unity versions/build modules working when their internal API changes;
        // target-specific Mac module probing above still provides the needed
        // fail-closed behavior for the common Windows-editor case.
        reason = $"Unity did not expose scripting backend availability for {group}; assuming {backend} is available.";
        return true;
    }

    /// <summary>
    /// Downgrade an already-selected IL2CPP backend only when the selected
    /// target cannot provide IL2CPP. The returned scope restores the original
    /// backend when that backend is still valid after the build.
    /// </summary>
    public static IDisposable EnsureCompatibleBackend(BuildTarget target)
    {
        return EnsureCompatibleBackend(target, target);
    }

    public static IDisposable EnsureCompatibleBackend(
        BuildTarget target,
        BuildTarget restoreValidationTarget)
    {
        BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
        NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(group);
        ScriptingImplementation original = PlayerSettings.GetScriptingBackend(namedBuildTarget);

        if (original != ScriptingImplementation.IL2CPP)
        {
            return null;
        }

        string il2CppReason;
        if (IsScriptingBackendAvailable(target, original, out il2CppReason))
        {
            return null;
        }

        string monoReason;
        if (!IsScriptingBackendAvailable(target, ScriptingImplementation.Mono2x, out monoReason))
        {
            throw new InvalidOperationException(
                $"IL2CPP is unavailable for {target}, and Mono is not available. " +
                $"IL2CPP: {il2CppReason}; Mono: {monoReason}.");
        }

        PlayerSettings.SetScriptingBackend(namedBuildTarget, ScriptingImplementation.Mono2x);
        Debug.LogWarning(
            $"[BasisBuild] IL2CPP is unavailable for {target}; automatically using Mono for this build. " +
            $"{il2CppReason}");
        return new BackendRestoreScope(target, restoreValidationTarget, namedBuildTarget, original);
    }

    public static bool TryGetAvailableScriptingBackends(
        BuildTargetGroup group,
        out ScriptingImplementation[] backends,
        out string source)
    {
        if (TryInvokeBackendProvider(
                typeof(PlayerSettings),
                "GetAvailableScriptingBackends",
                group,
                out backends))
        {
            source = "PlayerSettings.GetAvailableScriptingBackends";
            return true;
        }

        Type moduleManagerType = Type.GetType("UnityEditor.Modules.ModuleManager, UnityEditor.dll");
        if (moduleManagerType != null && TryInvokeBackendProvider(
                moduleManagerType,
                "GetScriptingImplementations",
                group,
                out backends))
        {
            source = "UnityEditor.Modules.ModuleManager.GetScriptingImplementations";
            return true;
        }

        backends = null;
        source = null;
        return false;
    }

    private static bool TryGetMacIl2CppAvailability(
        BuildTarget target,
        ScriptingImplementation backend,
        out bool available,
        out string reason)
    {
        available = false;
        reason = null;
        if (target != BuildTarget.StandaloneOSX)
        {
            return false;
        }

        string editorContentsPath = EditorApplication.applicationContentsPath;
        if (string.IsNullOrEmpty(editorContentsPath))
        {
            return false;
        }

        string variationsPath = Path.Combine(
            editorContentsPath,
            "PlaybackEngines",
            "MacStandaloneSupport",
            "Variations");
        if (!Directory.Exists(variationsPath))
        {
            return false;
        }

        string[] variations = Directory.GetFileSystemEntries(variationsPath);
        if (variations.Length == 0)
        {
            return false;
        }

        string backendToken = backend == ScriptingImplementation.IL2CPP
            ? Il2CppToken
            : backend == ScriptingImplementation.Mono2x ? MonoToken : null;
        if (backendToken == null)
        {
            return false;
        }

        for (int index = 0; index < variations.Length; index++)
        {
            string variationName = Path.GetFileName(variations[index]);
            if (variationName.IndexOf(backendToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                available = true;
                reason = $"MacStandaloneSupport contains a {backendToken} variation ({variationName}).";
                return true;
            }
        }

        available = false;
        reason = $"MacStandaloneSupport contains no {backendToken} variation.";
        return true;
    }

    private static bool TryInvokeBackendProvider(
        Type providerType,
        string methodName,
        BuildTargetGroup group,
        out ScriptingImplementation[] backends)
    {
        backends = null;
        MethodInfo[] methods = providerType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int index = 0; index < methods.Length; index++)
        {
            MethodInfo method = methods[index];
            if (method.Name != methodName || method.ReturnType != typeof(ScriptingImplementation[]))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || !TryCreateGroupArgument(parameters[0].ParameterType, group, out object argument))
            {
                continue;
            }

            try
            {
                backends = method.Invoke(null, new[] { argument }) as ScriptingImplementation[];
                if (backends != null)
                {
                    return true;
                }
            }
            catch
            {
                // Unity has changed these internal signatures between editor
                // versions; try the next provider/overload.
            }
        }

        return false;
    }

    private static bool TryCreateGroupArgument(Type parameterType, BuildTargetGroup group, out object argument)
    {
        if (parameterType == typeof(BuildTargetGroup))
        {
            argument = group;
            return true;
        }

        if (parameterType == typeof(NamedBuildTarget))
        {
            argument = NamedBuildTarget.FromBuildTargetGroup(group);
            return true;
        }

        argument = null;
        return false;
    }

    private static bool Contains(ScriptingImplementation[] values, ScriptingImplementation value)
    {
        if (values == null)
        {
            return false;
        }

        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatBackends(ScriptingImplementation[] values)
    {
        if (values == null || values.Length == 0)
        {
            return "none";
        }

        List<string> names = new List<string>(values.Length);
        for (int index = 0; index < values.Length; index++)
        {
            names.Add(values[index].ToString());
        }

        return string.Join(", ", names.ToArray());
    }

    private sealed class BackendRestoreScope : IDisposable
    {
        private readonly BuildTarget target;
        private readonly BuildTarget restoreValidationTarget;
        private readonly NamedBuildTarget namedBuildTarget;
        private readonly ScriptingImplementation original;
        private bool disposed;

        public BackendRestoreScope(
            BuildTarget target,
            BuildTarget restoreValidationTarget,
            NamedBuildTarget namedBuildTarget,
            ScriptingImplementation original)
        {
            this.target = target;
            this.restoreValidationTarget = restoreValidationTarget;
            this.namedBuildTarget = namedBuildTarget;
            this.original = original;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            string reason;
            if (!IsScriptingBackendAvailable(restoreValidationTarget, original, out reason))
            {
                Debug.LogWarning(
                    $"[BasisBuild] Leaving Mono selected after {target}; the original {original} backend is still unavailable. {reason}");
                return;
            }

            try
            {
                PlayerSettings.SetScriptingBackend(namedBuildTarget, original);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BasisBuild] Could not restore the original {original} backend after {target}: {ex.Message}");
            }
        }
    }
}
#endif
