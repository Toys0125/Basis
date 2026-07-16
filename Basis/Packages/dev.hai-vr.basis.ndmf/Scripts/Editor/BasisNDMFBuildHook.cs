#if BASISNDMF_NDMF_IS_INSTALLED
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.NDMF
{
    [InitializeOnLoad]
    internal class BasisNDMFBuildHook
    {
        static BasisNDMFBuildHook()
        {
            BasisAssetBundlePipeline.OnBeforeBuildTargetPrefab += HandleBeforeBuildTargetPrefab;
            BasisAssetBundlePipeline.OnPrefabBuildTargetRequiresActiveEditorTarget += RequiresActiveEditorTarget;
            BasisAvatarSDKInspector.OnBeforeTestInEditor += prefab => BasisAvatarPrefabProcessor(prefab);
        }

        private static void HandleBeforeBuildTargetPrefab(GameObject prefab, BasisPrefabBuildContext context)
        {
            if (context.ContentKind == BasisBundleContentKind.Avatar)
            {
                BasisAvatarPrefabProcessor(prefab);
            }
        }

        private static bool RequiresActiveEditorTarget(BasisPrefabBuildContext context)
        {
            // The current Modular Avatar/NDMF chain reads activeBuildTarget directly.
            // Keep the compatibility switch until that chain accepts an explicit target.
            return context.ContentKind == BasisBundleContentKind.Avatar;
        }

        private static GameObject BasisAvatarPrefabProcessor(GameObject copy)
        {
            AvatarProcessor.ProcessAvatar(copy, BasisFrameworkPlatform.Instance);
            return copy;
        }
    }
}
#endif
