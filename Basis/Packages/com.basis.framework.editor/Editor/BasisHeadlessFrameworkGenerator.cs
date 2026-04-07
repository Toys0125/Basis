#if UNITY_SERVER
using System.IO;
using Basis.Scripts.UI.NamePlate;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEngine;

public static class BasisHeadlessFrameworkGenerator
{
    private const string SourceFrameworkPrefabPath = "Packages/com.basis.framework/Prefabs/BasisFramework.prefab";
    private const string GeneratedFrameworkPrefabPath = "Assets/Basis/Generated/BasisFramework Headless.prefab";
    private const string HeadlessFrameworkAddress = "BasisFrameworkHeadless";
    private const string HeadlessFrameworkGroupName = "Basis Headless Assets";
    private const string GeneratorStampKey = "Basis.HeadlessFrameworkGenerator.Stamp";
    private const int GeneratorVersion = 1;

    public static void PrepareForBuild(bool includeHeadlessAssets)
    {
        if (includeHeadlessAssets)
        {
            EnsureGeneratedAssets();
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedFrameworkPrefabPath) != null)
        {
            EnsureAddressableEntry(false);
            return;
        }

        SetHeadlessGroupIncludeInBuild(false);
    }

    public static void EnsureGeneratedAssets()
    {
        string sourceGuid = AssetDatabase.AssetPathToGUID(SourceFrameworkPrefabPath);
        if (string.IsNullOrEmpty(sourceGuid))
        {
            throw new BuildFailedException($"Unable to find source framework prefab at '{SourceFrameworkPrefabPath}'.");
        }

        string sourceStamp = $"{GeneratorVersion}:{AssetDatabase.GetAssetDependencyHash(SourceFrameworkPrefabPath)}";
        bool generatedPrefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedFrameworkPrefabPath) != null;
        bool shouldRegenerate = !generatedPrefabExists || EditorPrefs.GetString(GeneratorStampKey, string.Empty) != sourceStamp;

        if (shouldRegenerate)
        {
            GenerateHeadlessFrameworkPrefab();
            EditorPrefs.SetString(GeneratorStampKey, sourceStamp);
        }

        EnsureAddressableEntry(true);
    }

    private static void GenerateHeadlessFrameworkPrefab()
    {
        string outputDirectory = Path.GetDirectoryName(GeneratedFrameworkPrefabPath);
        if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            AssetDatabase.Refresh();
        }

        GameObject root = PrefabUtility.LoadPrefabContents(SourceFrameworkPrefabPath);
        try
        {
            RemoveKnownHeadlessUnsafeObjects(root);
            RemoveKnownHeadlessUnsafeComponents(root);
            StripTMProComponents(root);
            RemoveMissingScripts(root);

            PrefabUtility.SaveAsPrefabAsset(root, GeneratedFrameworkPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void RemoveKnownHeadlessUnsafeObjects(GameObject root)
    {
        Transform generatedText = root.transform.Find("NetworkManagement/NamePlateGenerationText");
        if (generatedText != null)
        {
            Object.DestroyImmediate(generatedText.gameObject);
        }
    }

    private static void RemoveKnownHeadlessUnsafeComponents(GameObject root)
    {
        foreach (BasisRemoteNamePlateDriver driver in root.GetComponentsInChildren<BasisRemoteNamePlateDriver>(true))
        {
            Object.DestroyImmediate(driver);
        }
    }

    private static void StripTMProComponents(GameObject root)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
            {
                continue;
            }

            System.Type type = component.GetType();
            if (type.Namespace == "TMPro")
            {
                Object.DestroyImmediate(component);
            }
        }
    }

    private static void RemoveMissingScripts(GameObject root)
    {
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
        }
    }

    private static void EnsureAddressableEntry(bool includeInBuild)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            throw new BuildFailedException("Addressables settings were not found while generating the headless framework.");
        }

        string outputGuid = AssetDatabase.AssetPathToGUID(GeneratedFrameworkPrefabPath);
        if (string.IsNullOrEmpty(outputGuid))
        {
            throw new BuildFailedException($"Unable to resolve guid for generated headless framework at '{GeneratedFrameworkPrefabPath}'.");
        }

        AddressableAssetEntry sourceEntry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(SourceFrameworkPrefabPath));
        AddressableAssetGroup sourceGroup = sourceEntry?.parentGroup ?? settings.DefaultGroup;
        AddressableAssetGroup group = GetOrCreateHeadlessGroup(settings, sourceGroup);
        if (group == null)
        {
            throw new BuildFailedException("Unable to resolve an Addressables group for the generated headless framework.");
        }

        AddressableAssetEntry entry = settings.FindAssetEntry(outputGuid);
        if (entry == null || entry.parentGroup != group)
        {
            entry = settings.CreateOrMoveEntry(outputGuid, group);
        }

        entry.address = HeadlessFrameworkAddress;
        SetHeadlessGroupIncludeInBuild(group, includeInBuild);

        EditorUtility.SetDirty(group);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    private static AddressableAssetGroup GetOrCreateHeadlessGroup(AddressableAssetSettings settings, AddressableAssetGroup sourceGroup)
    {
        AddressableAssetGroup group = settings.FindGroup(HeadlessFrameworkGroupName);
        if (group != null)
        {
            return group;
        }

        if (sourceGroup?.Schemas != null && sourceGroup.Schemas.Count > 0)
        {
            return settings.CreateGroup(HeadlessFrameworkGroupName, false, false, true, sourceGroup.Schemas);
        }

        return settings.CreateGroup(
            HeadlessFrameworkGroupName,
            false,
            false,
            true,
            null,
            typeof(BundledAssetGroupSchema),
            typeof(ContentUpdateGroupSchema));
    }

    private static void SetHeadlessGroupIncludeInBuild(bool includeInBuild)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            return;
        }

        AddressableAssetGroup group = settings.FindGroup(HeadlessFrameworkGroupName);
        if (group == null)
        {
            return;
        }

        SetHeadlessGroupIncludeInBuild(group, includeInBuild);
        EditorUtility.SetDirty(group);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    private static void SetHeadlessGroupIncludeInBuild(AddressableAssetGroup group, bool includeInBuild)
    {
        BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
        if (schema != null && schema.IncludeInBuild != includeInBuild)
        {
            schema.IncludeInBuild = includeInBuild;
            EditorUtility.SetDirty(schema);
        }
    }
}
#endif