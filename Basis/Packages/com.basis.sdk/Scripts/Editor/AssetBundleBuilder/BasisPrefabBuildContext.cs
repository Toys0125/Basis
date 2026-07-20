using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public enum BasisBundleContentKind
{
    Avatar,
    Prop
}

public sealed class BasisPrefabBuildContext
{
    // The hierarchy currently being evaluated. Active-target requirement checks
    // receive the prepared source; build processors receive the isolated target clone.
    public GameObject PrefabRoot { get; internal set; }
    public BuildTarget Target { get; set; }
    public BuildTargetGroup TargetGroup { get; set; }
    public NamedBuildTarget NamedTarget { get; set; }
    public BasisBundleContentKind ContentKind { get; set; }
    public BasisAssetBundleObject Settings { get; set; }
    public bool IsActiveEditorTarget { get; set; }
    public string[] GraphicsApis { get; set; }
}
