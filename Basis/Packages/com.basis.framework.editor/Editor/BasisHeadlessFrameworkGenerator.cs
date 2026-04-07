using System.IO;
using Basis.Scripts.UI.NamePlate;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEngine;

public static class BasisHeadlessFrameworkGenerator
{
    private const string SourceFrameworkPrefabPath = "Packages/com.basis.framework/Prefabs/BasisFramework.prefab";
    private const string GeneratedFrameworkPrefabPath = "Assets/Basis/Generated/BasisFramework Headless.prefab";
    private const string HeadlessFrameworkAddress = "BasisFrameworkHeadless";
    private const string GeneratorStampKey = "Basis.HeadlessFrameworkGenerator.Stamp";
    private const int GeneratorVersion = 1;

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

        EnsureAddressableEntry();
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

    private static void EnsureAddressableEntry()
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
        AddressableAssetGroup group = sourceEntry?.parentGroup ?? settings.DefaultGroup;
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

        EditorUtility.SetDirty(group);
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }
}
