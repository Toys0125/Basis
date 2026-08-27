using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;
using VF.Model;
using VF.Model.Feature;

namespace VF.Integration.Basis.Shim {
    internal static class BasisVrcfuryAuthoringMenus {
        private const string ToolsRoot = "Tools/VRCFury/BasisVR/";
        private const string GameObjectRoot = "GameObject/VRCFury/";

        [MenuItem(ToolsRoot + "Status", priority = 0)]
        private static void Status() {
            EditorUtility.DisplayDialog(
                "VRCFury for BasisVR",
                "The BasisVR auto-shim is active.\n\n" +
                "Supported authoring in this build:\n" +
                "• Armature Link\n" +
                "• Blendshape Optimizer\n\n" +
                "VRCFury metadata is processed on the temporary Basis build clone and is not included in the uploaded bundle.",
                "OK"
            );
        }

        [MenuItem(ToolsRoot + "Add Armature Link To Selected Object", priority = 20)]
        [MenuItem(GameObjectRoot + "Add Armature Link (BasisVR)", priority = 20)]
        private static void AddArmatureLink() {
            var selected = Selection.activeGameObject;
            if (selected == null) {
                EditorUtility.DisplayDialog("VRCFury for BasisVR", "Select the root object of the clothing/prop armature first.", "OK");
                return;
            }

            AddFeature(selected, new ArmatureLink {
                propBone = selected,
                recursive = true,
                alignPosition = true,
                alignRotation = true,
                alignScale = true
            }, "Add VRCFury Armature Link");
        }

        [MenuItem(ToolsRoot + "Add Armature Link To Selected Object", true)]
        [MenuItem(GameObjectRoot + "Add Armature Link (BasisVR)", true)]
        private static bool ValidateAddArmatureLink() => Selection.activeGameObject != null;

        [MenuItem(ToolsRoot + "Add Blendshape Optimizer To Selected Avatar", priority = 21)]
        [MenuItem(GameObjectRoot + "Add Blendshape Optimizer (BasisVR)", priority = 21)]
        private static void AddBlendshapeOptimizer() {
            var selected = Selection.activeGameObject;
            if (selected == null) {
                EditorUtility.DisplayDialog("VRCFury for BasisVR", "Select a Basis avatar or an object under one first.", "OK");
                return;
            }

            var avatar = selected.GetComponentInParent<BasisAvatar>();
            var target = avatar != null ? avatar.gameObject : selected;
            AddFeature(target, new BlendshapeOptimizer(), "Add VRCFury Blendshape Optimizer");
        }

        [MenuItem(ToolsRoot + "Add Blendshape Optimizer To Selected Avatar", true)]
        [MenuItem(GameObjectRoot + "Add Blendshape Optimizer (BasisVR)", true)]
        private static bool ValidateAddBlendshapeOptimizer() => Selection.activeGameObject != null;

        internal static VRCFury AddFeature(GameObject target, FeatureModel feature, string undoName) {
            if (target == null || feature == null) return null;
            var component = (VRCFury)Undo.AddComponent(target, typeof(VRCFury));
            Undo.RecordObject(component, undoName);
            component.content = feature;
            EditorUtility.SetDirty(component);
            VRCFury.MarkDirty(component);
            Selection.activeObject = component;
            return component;
        }
    }

    [CustomEditor(typeof(VRCFury), true)]
    internal sealed class BasisVrcfuryAuthoringEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();
            var fury = (VRCFury)target;
            var content = serializedObject.FindProperty("content");

            EditorGUILayout.LabelField("VRCFury for BasisVR", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Basis-only authoring UI is active. Armature Link and Blendshape Optimizer are currently supported by the auto-shim.",
                MessageType.Info
            );

            if (fury.content == null) {
                DrawFeatureChooser(fury);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space();
            if (fury.content is ArmatureLink) {
                DrawArmatureLink(content);
            } else if (fury.content is BlendshapeOptimizer) {
                DrawBlendshapeOptimizer();
            } else {
                DrawUnsupported(content, fury.content);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            if (GUILayout.Button("Remove VRCFury Feature")) {
                Undo.DestroyObjectImmediate(fury);
                GUIUtility.ExitGUI();
            }
        }

        private static void DrawFeatureChooser(VRCFury fury) {
            EditorGUILayout.HelpBox("This VRCFury component has no feature yet.", MessageType.Warning);
            if (GUILayout.Button("Armature Link")) {
                Undo.RecordObject(fury, "Set VRCFury Armature Link");
                fury.content = new ArmatureLink {
                    propBone = fury.gameObject,
                    recursive = true,
                    alignPosition = true,
                    alignRotation = true,
                    alignScale = true
                };
                EditorUtility.SetDirty(fury);
                VRCFury.MarkDirty(fury);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Blendshape Optimizer")) {
                Undo.RecordObject(fury, "Set VRCFury Blendshape Optimizer");
                fury.content = new BlendshapeOptimizer();
                EditorUtility.SetDirty(fury);
                VRCFury.MarkDirty(fury);
                GUIUtility.ExitGUI();
            }
        }

        private static void DrawArmatureLink(SerializedProperty content) {
            EditorGUILayout.LabelField("Armature Link", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Attach this feature to the clothing/prop armature root. During the Basis build, matching bones are rebound to the avatar armature on the temporary build clone.",
                MessageType.None
            );

            Draw(content, "propBone", "Prop Armature Root");
            Draw(content, "linkTo", "Link To", true);
            Draw(content, "recursive", "Match Armature Recursively");
            Draw(content, "removeBoneSuffix", "Remove Bone Suffix");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Alignment", EditorStyles.boldLabel);
            Draw(content, "alignPosition", "Align Position");
            Draw(content, "alignRotation", "Align Rotation");
            Draw(content, "alignScale", "Align Scale");
            Draw(content, "autoScaleFactor", "Automatic Scale Factor");
            Draw(content, "scalingFactorPowersOf10Only", "Scale Factor Powers Of 10 Only");
            Draw(content, "skinRewriteScalingFactor", "Scale Factor");
            Draw(content, "forceOneWorldScale", "Force World Scale To One");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            Draw(content, "removeParentConstraints", "Remove Parent Constraints");
            Draw(content, "forceMergedName", "Force Merged Name");
        }

        private static void DrawBlendshapeOptimizer() {
            EditorGUILayout.LabelField("Blendshape Optimizer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "During the Basis build, blendshapes that are not required by Basis face animation or Vixxy are baked into the mesh at their authored weights. Required blendshapes remain live and their Basis indices are remapped by name.",
                MessageType.None
            );
        }

        private static void DrawUnsupported(SerializedProperty content, FeatureModel feature) {
            EditorGUILayout.LabelField(feature.GetType().Name, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This VRCFury feature is preserved for source-avatar compatibility, but this Basis auto-shim does not currently provide a supported authoring/build implementation for it. Its serialized data is shown below for inspection.",
                MessageType.Warning
            );
            EditorGUILayout.PropertyField(content, true);
        }

        private static void Draw(SerializedProperty parent, string name, string label, bool includeChildren = false) {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) EditorGUILayout.PropertyField(property, new GUIContent(label), includeChildren);
        }
    }
}
