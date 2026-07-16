using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.Build;

public enum BasisBundleContentKind
{
    Avatar,
    Prop
}

public sealed class BasisPrefabBuildContext
{
    public BuildTarget Target { get; set; }
    public BuildTargetGroup TargetGroup { get; set; }
    public NamedBuildTarget NamedTarget { get; set; }
    public BasisBundleContentKind ContentKind { get; set; }
    public BasisAssetBundleObject Settings { get; set; }
    public bool IsActiveEditorTarget { get; set; }
    public string[] GraphicsApis { get; set; }
}
