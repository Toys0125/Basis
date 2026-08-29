using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VF.Model;
using VF.Model.Feature;
using VF.Model.StateAction;

namespace VF.Integration.Basis.Shim {
    internal static class BasisVrcfuryAuthoringMenus {
        private const string ComponentRoot = "Component/VRCFury/";
        private const string ToolsRoot = "Tools/VRCFury/BasisVR/";
        internal const string ArmatureLinkMenuPath = ComponentRoot + "Armature Link (VRCFury)";
        internal const string BlendshapeOptimizerMenuPath = ComponentRoot + "Blendshape Optimizer (VRCFury)";
        internal const string MmdCompatibilityMenuPath = ComponentRoot + "MMD Compatibility (VRCFury)";
        internal const string ApplyDuringUploadMenuPath = ComponentRoot + "Apply During Upload (VRCFury)";

        [MenuItem(ToolsRoot + "Status", priority = 0)]
        private static void Status() {
            EditorUtility.DisplayDialog(
                "VRCFury for BasisVR",
                "The BasisVR compatibility layer is active.\n\n" +
                "Supported VRCFury authoring in this build:\n" +
                "• Armature Link\n" +
                "• Blendshape Optimizer\n" +
                "• MMD Compatibility\n" +
                "• Apply During Upload\n\n" +
                "These are normal VRCFury feature components and are processed only on the temporary Basis build clone.",
                "OK"
            );
        }

        [MenuItem(ArmatureLinkMenuPath, false, 0)]
        private static void AddArmatureLink() {
            foreach (var selected in Selection.gameObjects) {
                if (selected == null) continue;
                var guessed = GuessLinkFrom(selected);
                var model = new ArmatureLink {
                    propBone = guessed,
                    recursive = false,
                    alignPosition = false,
                    alignRotation = false,
                    alignScale = false,
                    autoScaleFactor = true,
                    scalingFactorPowersOf10Only = true,
                    skinRewriteScalingFactor = 1
                };
                UpdateOnLinkFromChange(model, null, guessed);
                AddFeature(selected, model, "Add VRCFury Armature Link");
            }
        }

        [MenuItem(ArmatureLinkMenuPath, true)]
        private static bool ValidateAddArmatureLink() => Selection.gameObjects.Any(obj => obj != null);

        [MenuItem(BlendshapeOptimizerMenuPath, false, 1)]
        private static void AddBlendshapeOptimizer() {
            foreach (var selected in Selection.gameObjects) {
                if (selected == null) continue;
                AddFeature(selected, new BlendshapeOptimizer(), "Add VRCFury Blendshape Optimizer");
            }
        }

        [MenuItem(BlendshapeOptimizerMenuPath, true)]
        private static bool ValidateAddBlendshapeOptimizer() => Selection.gameObjects.Any(obj => obj != null);

        [MenuItem(MmdCompatibilityMenuPath, false, 2)]
        private static void AddMmdCompatibility() {
            foreach (var selected in Selection.gameObjects) {
                if (selected == null) continue;
                AddFeature(selected, new MmdCompatibility(), "Add VRCFury MMD Compatibility");
            }
        }

        [MenuItem(MmdCompatibilityMenuPath, true)]
        private static bool ValidateAddMmdCompatibility() => Selection.gameObjects.Any(obj => obj != null);

        [MenuItem(ApplyDuringUploadMenuPath, false, 3)]
        private static void AddApplyDuringUpload() {
            foreach (var selected in Selection.gameObjects) {
                if (selected == null) continue;
                AddFeature(selected, new ApplyDuringUpload { action = new State() }, "Add VRCFury Apply During Upload");
            }
        }

        [MenuItem(ApplyDuringUploadMenuPath, true)]
        private static bool ValidateAddApplyDuringUpload() => Selection.gameObjects.Any(obj => obj != null);

        internal static VRCFury AddFeature(GameObject target, FeatureModel feature, string undoName) {
            if (target == null || feature == null) return null;
            var component = Undo.AddComponent<VRCFury>(target);
            var so = new SerializedObject(component);
            so.FindProperty("content").managedReferenceValue = feature;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(component);
            VRCFury.MarkDirty(component);
            Selection.activeObject = component;
            return component;
        }

        internal static GameObject GuessLinkFrom(GameObject componentObject) {
            if (componentObject == null) return null;

            var avatar = componentObject.GetComponentInParent<BasisAvatar>();
            if (avatar != null && avatar.gameObject == componentObject) return null;

            if (avatar != null && avatar.Animator != null) {
                var avatarHips = avatar.Animator.GetBoneTransform(HumanBodyBones.Hips);
                if (avatarHips != null) {
                    var path = GetPath(avatarHips, avatar.transform);
                    if (!string.IsNullOrEmpty(path)) {
                        var found = componentObject.transform.Find(path);
                        if (found != null) return found.gameObject;
                    }
                }
            }

            var possibleArmatures = new List<Transform>();
            var name = componentObject.name.ToLowerInvariant();
            if (name.Contains("armature") || name.Contains("skeleton")) possibleArmatures.Add(componentObject.transform);
            for (var i = 0; i < componentObject.transform.childCount; i++) {
                var child = componentObject.transform.GetChild(i);
                var childName = child.name.ToLowerInvariant();
                if (childName.Contains("armature") || childName.Contains("skeleton")) possibleArmatures.Add(child);
            }

            foreach (var armature in possibleArmatures) {
                for (var i = 0; i < armature.childCount; i++) {
                    var child = armature.GetChild(i);
                    if (child.name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0) return child.gameObject;
                }
            }

            return componentObject;
        }

        internal static void UpdateOnLinkFromChange(ArmatureLink model, GameObject before, GameObject after) {
            if (model == null || after == null) return;
            var skinAfter = HasExternalSkinBoneReference(after.transform);
            if (before == null || HasExternalSkinBoneReference(before.transform) != skinAfter) {
                model.alignPosition = model.alignRotation = model.alignScale = skinAfter;
                model.recursive = skinAfter;
                model.autoScaleFactor = true;
                model.scalingFactorPowersOf10Only = true;
                model.skinRewriteScalingFactor = 1;
            }
        }

        private static bool HasExternalSkinBoneReference(Transform obj) {
            if (obj == null) return false;
            var avatar = obj.GetComponentInParent<BasisAvatar>();
            var root = avatar != null ? avatar.transform : obj.root;
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                if (skin == null || skin.transform.IsChildOf(obj)) continue;
                if (skin.rootBone != null && skin.rootBone.IsChildOf(obj)) return true;
                if (skin.bones != null && skin.bones.Any(bone => bone != null && bone.IsChildOf(obj))) return true;
            }
            return false;
        }

        private static string GetPath(Transform child, Transform root) {
            if (child == null || root == null || child == root) return string.Empty;
            var names = new Stack<string>();
            var current = child;
            while (current != null && current != root) {
                names.Push(current.name);
                current = current.parent;
            }
            return current == root ? string.Join("/", names) : string.Empty;
        }
    }

    [CustomEditor(typeof(VRCFury), true)]
    internal sealed class BasisVrcfuryAuthoringEditor : UnityEditor.Editor {
        private bool advancedOptions;
        private bool superAdvancedOptions;
        private bool forceAdvancedLinkTargets;
        private GameObject lastPropBone;

        private void OnEnable() {
            if (target is VRCFury fury && fury.content is ArmatureLink model) lastPropBone = model.propBone;
        }

        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            root.Add(BasisVrcfuryHeader.Create(GetFeatureTitle()));
            root.Add(new IMGUIContainer(DrawInspector));
            return root;
        }

        private string GetFeatureTitle() {
            if (!(target is VRCFury fury) || fury.content == null) return "VRCFury";
            if (fury.content is ArmatureLink) return "Armature Link";
            if (fury.content is BlendshapeOptimizer) return "Blendshape Optimizer";
            if (fury.content is MmdCompatibility) return "MMD Compatibility";
            if (fury.content is ApplyDuringUpload) return "Apply During Upload";
            return ObjectNames.NicifyVariableName(fury.content.GetType().Name);
        }

        private void DrawInspector() {
            serializedObject.Update();
            var fury = (VRCFury)target;
            var content = serializedObject.FindProperty("content");

            if (fury.content == null) {
                EditorGUILayout.HelpBox(
                    "This VRCFury component has no feature configured. VRCFury normally creates feature components from Add Component > VRCFury.",
                    MessageType.Error
                );
                if (GUILayout.Button("Armature Link")) SetFeature(new ArmatureLink { propBone = fury.gameObject });
                if (GUILayout.Button("Blendshape Optimizer")) SetFeature(new BlendshapeOptimizer());
                if (GUILayout.Button("MMD Compatibility")) SetFeature(new MmdCompatibility());
                if (GUILayout.Button("Apply During Upload")) SetFeature(new ApplyDuringUpload { action = new State() });
                serializedObject.ApplyModifiedProperties();
                return;
            }

            if (fury.content is ArmatureLink armatureLink) {
                DrawArmatureLink(content, armatureLink);
            } else if (fury.content is BlendshapeOptimizer) {
                DrawBlendshapeOptimizer();
            } else if (fury.content is MmdCompatibility) {
                DrawMmdCompatibility();
            } else if (fury.content is ApplyDuringUpload) {
                DrawApplyDuringUpload(content);
            } else {
                EditorGUILayout.HelpBox(
                    "This VRCFury feature is preserved for source-avatar compatibility, but the Basis compatibility layer does not currently provide its original custom editor/build implementation.",
                    MessageType.Warning
                );
                EditorGUILayout.PropertyField(content, true);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void SetFeature(FeatureModel feature) {
            Undo.RecordObject(target, "Set VRCFury Feature");
            var property = serializedObject.FindProperty("content");
            property.managedReferenceValue = feature;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            VRCFury.MarkDirty((VRCFury)target);
        }

        private void DrawArmatureLink(SerializedProperty prop, ArmatureLink model) {
            EditorGUILayout.HelpBox(
                "This feature will attach a prop (with or without an armature) to the avatar. If 'Link From' is an armature matching the avatar's, the armatures will be merged and the extra bones will not count toward performance rank.",
                MessageType.Info
            );

            var propBoneProp = prop.FindPropertyRelative("propBone");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                propBoneProp,
                new GUIContent(
                    "Link From (Prop / Clothing)",
                    "For clothing, this should be the Hips bone in the clothing's Armature (or the main bone if it doesn't have Hips). For non-clothing objects, this should be the object you want moved."
                )
            );
            if (EditorGUI.EndChangeCheck()) {
                serializedObject.ApplyModifiedProperties();
                var newValue = propBoneProp.objectReferenceValue as GameObject;
                if (lastPropBone != newValue) {
                    BasisVrcfuryAuthoringMenus.UpdateOnLinkFromChange(model, lastPropBone, newValue);
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                    lastPropBone = newValue;
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Link To (Avatar):", EditorStyles.boldLabel);
            var linkTo = prop.FindPropertyRelative("linkTo");
            var simpleLinkToMode = IsSimpleLinkTo(linkTo) && !forceAdvancedLinkTargets;
            if (simpleLinkToMode) {
                EditorGUILayout.PropertyField(linkTo.GetArrayElementAtIndex(0).FindPropertyRelative("bone"), GUIContent.none);
            } else {
                DrawAdvancedLinkTargets(linkTo);
            }

            EditorGUILayout.Space(2);
            advancedOptions = EditorGUILayout.Foldout(advancedOptions, "Advanced Options", true);
            if (advancedOptions) {
                EditorGUI.indentLevel++;
            DrawSectionHeader("Search / Matching");
            if (IsSimpleLinkTo(linkTo) && !forceAdvancedLinkTargets) {
                if (GUILayout.Button("Enable Advanced Link Target Mode")) forceAdvancedLinkTargets = true;
            }

            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("recursive"),
                new GUIContent("Recursive", "If enabled, child objects with matching object names on the avatar will also be linked")
            );
            EditorGUILayout.PropertyField(
                prop.FindPropertyRelative("removeBoneSuffix"),
                new GUIContent(
                    "Ignore name suffix/prefix",
                    "If set, this substring will be ignored when matching object names against the avatar. If empty, the suffix is predicted from the difference between the root bone names."
                )
            );

            DrawSectionHeader("Transform Alignment", "Snap merged objects to the existing transform on the avatar");
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("alignPosition"), new GUIContent("Align Position"));
            EditorGUILayout.PropertyField(prop.FindPropertyRelative("alignRotation"), new GUIContent("Align Rotation"));
            var alignScale = prop.FindPropertyRelative("alignScale");
            EditorGUILayout.PropertyField(alignScale, new GUIContent("Align Scale"));

            if (alignScale.boolValue) {
                EditorGUI.indentLevel++;
                var recursive = prop.FindPropertyRelative("recursive").boolValue;
                var autoScale = prop.FindPropertyRelative("autoScaleFactor");
                if (recursive) {
                    EditorGUILayout.PropertyField(
                        autoScale,
                        new GUIContent(
                            "Automatic Scale Multiplier",
                            "Uses the Link From object's world scale divided by the Link To object's world scale."
                        )
                    );
                    if (autoScale.boolValue) {
                        EditorGUILayout.PropertyField(
                            prop.FindPropertyRelative("scalingFactorPowersOf10Only"),
                            new GUIContent("Restrict multiplier to powers of 10")
                        );
                    } else {
                        EditorGUILayout.PropertyField(prop.FindPropertyRelative("skinRewriteScalingFactor"), new GUIContent("Multiplier"));
                    }
                } else {
                    EditorGUILayout.PropertyField(prop.FindPropertyRelative("skinRewriteScalingFactor"), new GUIContent("Multiplier"));
                }
                EditorGUI.indentLevel--;
            }

            superAdvancedOptions = EditorGUILayout.Foldout(superAdvancedOptions, "Super Advanced Options", true);
            if (superAdvancedOptions) {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox("Danger, changing these options may break things.", MessageType.Warning);
                EditorGUILayout.PropertyField(
                    prop.FindPropertyRelative("removeParentConstraints"),
                    new GUIContent("Remove parent constraints from merged objects")
                );
                EditorGUILayout.PropertyField(
                    prop.FindPropertyRelative("forceMergedName"),
                    new GUIContent(
                        "Force Merged Name",
                        "Force the name of the object at the merged target location. Offset animations and toggles for the merged object may not work when this is used."
                    )
                );
                EditorGUILayout.PropertyField(
                    prop.FindPropertyRelative("forceOneWorldScale"),
                    new GUIContent("Force world scale to 1,1,1")
                );
                EditorGUI.indentLevel--;
            }
                EditorGUI.indentLevel--;
            }

            DrawArmatureWarnings(model);
        }

        private static bool IsSimpleLinkTo(SerializedProperty linkTo) {
            if (linkTo == null || !linkTo.isArray || linkTo.arraySize != 1) return false;
            var entry = linkTo.GetArrayElementAtIndex(0);
            return entry.FindPropertyRelative("useBone").boolValue
                   && !entry.FindPropertyRelative("useObj").boolValue
                   && string.IsNullOrWhiteSpace(entry.FindPropertyRelative("offset").stringValue);
        }

        private static void DrawAdvancedLinkTargets(SerializedProperty linkTo) {
            EditorGUILayout.HelpBox("If multiple targets are provided, the first valid target found on the avatar will be used.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField("Target Object", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("Offset Path", EditorStyles.miniBoldLabel);
                GUILayout.Space(22);
            }

            for (var i = 0; i < linkTo.arraySize; i++) {
                var entry = linkTo.GetArrayElementAtIndex(i);
                var useBone = entry.FindPropertyRelative("useBone");
                var useObj = entry.FindPropertyRelative("useObj");
                var bone = entry.FindPropertyRelative("bone");
                var obj = entry.FindPropertyRelative("obj");
                var offset = entry.FindPropertyRelative("offset");

                using (new EditorGUILayout.HorizontalScope()) {
                    if (useObj.boolValue) {
                        EditorGUILayout.PropertyField(obj, GUIContent.none);
                    } else if (useBone.boolValue) {
                        EditorGUILayout.PropertyField(bone, GUIContent.none);
                    } else {
                        EditorGUILayout.LabelField("Avatar Root");
                    }
                    EditorGUILayout.PropertyField(offset, GUIContent.none);
                    if (GUILayout.Button("−", GUILayout.Width(22))) {
                        linkTo.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }

            if (GUILayout.Button("+", GUILayout.Width(28))) {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Bone"), false, () => AddLinkTarget(linkTo, true, false));
                menu.AddItem(new GUIContent("GameObject"), false, () => AddLinkTarget(linkTo, false, true));
                menu.AddItem(new GUIContent("Avatar Root"), false, () => AddLinkTarget(linkTo, false, false));
                menu.ShowAsContext();
            }
        }

        private static void AddLinkTarget(SerializedProperty linkTo, bool useBone, bool useObj) {
            linkTo.serializedObject.Update();
            var index = linkTo.arraySize;
            linkTo.InsertArrayElementAtIndex(index);
            var entry = linkTo.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("useBone").boolValue = useBone;
            entry.FindPropertyRelative("bone").enumValueIndex = (int)HumanBodyBones.Hips;
            entry.FindPropertyRelative("useObj").boolValue = useObj;
            entry.FindPropertyRelative("obj").objectReferenceValue = null;
            entry.FindPropertyRelative("offset").stringValue = string.Empty;
            linkTo.serializedObject.ApplyModifiedProperties();
        }

        private void DrawArmatureWarnings(ArmatureLink model) {
            if (model.propBone == null) return;
            var guess = BasisVrcfuryAuthoringMenus.GuessLinkFrom(model.propBone);
            if (guess != null && guess != model.propBone) {
                EditorGUILayout.HelpBox(
                    "It appears this object contains clothing with an Armature and Hips bone. If you are linking clothing, Link From should usually be that Hips object rather than the main clothing object.",
                    MessageType.Warning
                );
            }
        }

        private static void DrawBlendshapeOptimizer() {
            EditorGUILayout.HelpBox(
                "This feature will automatically bake all non-animated blendshapes into the mesh, saving VRAM for free!",
                MessageType.Info
            );
        }

        private static void DrawMmdCompatibility() {
            EditorGUILayout.HelpBox(
                "This component improves MMD compatibility by preserving VRCFury's known MMD blendshapes when Blendshape Optimizer runs.",
                MessageType.Info
            );
            EditorGUILayout.HelpBox(
                "VRCFury's advanced MMD layer-detection settings control VRChat FX animator layers. BasisVR does not use those VRChat layers, so those settings are preserved in the component data but are not applied by the Basis backend.",
                MessageType.None
            );
        }

        private static void DrawApplyDuringUpload(SerializedProperty prop) {
            EditorGUILayout.HelpBox(
                "These actions are baked into the temporary Basis build/Test-in-Editor clone before Armature Link and other hierarchy-changing features run. The authored avatar is not modified.",
                MessageType.Info
            );
            EditorGUILayout.HelpBox(
                "Basis supports upload-state actions that map to static avatar state. VRChat-only controller, PhysBone, SPS, and world-drop actions are preserved but skipped during Basis processing.",
                MessageType.None
            );

            var state = prop.FindPropertyRelative("action");
            var actions = state?.FindPropertyRelative("actions");
            if (actions == null) {
                EditorGUILayout.HelpBox("Apply During Upload state data is missing.", MessageType.Error);
                return;
            }

            for (var i = 0; i < actions.arraySize; i++) {
                var action = actions.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    using (new EditorGUILayout.HorizontalScope()) {
                        var value = action.managedReferenceValue;
                        EditorGUILayout.LabelField(
                            value != null ? ObjectNames.NicifyVariableName(value.GetType().Name.Replace("Action", "")) : "Missing Action",
                            EditorStyles.boldLabel
                        );
                        if (GUILayout.Button("Remove", GUILayout.Width(64))) {
                            actions.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                    EditorGUILayout.PropertyField(action, GUIContent.none, true);
                }
            }

            if (GUILayout.Button("Add Action")) {
                var menu = new GenericMenu();
                AddActionMenuItem<ObjectToggleAction>(menu, actions, "Object Toggle");
                AddActionMenuItem<BlendShapeAction>(menu, actions, "Blendshape");
                AddActionMenuItem<ScaleAction>(menu, actions, "Scale");
                AddActionMenuItem<MaterialAction>(menu, actions, "Material Swap");
                AddActionMenuItem<MaterialPropertyAction>(menu, actions, "Material Property");
                AddActionMenuItem<AnimationClipAction>(menu, actions, "Animation Clip");
                AddActionMenuItem<FlipbookAction>(menu, actions, "Poiyomi Flipbook Frame");
                AddActionMenuItem<PoiyomiUVTileAction>(menu, actions, "Poiyomi UV Tile");
                AddActionMenuItem<ShaderInventoryAction>(menu, actions, "SCSS Shader Inventory");
                menu.ShowAsContext();
            }
        }

        private static void AddActionMenuItem<T>(GenericMenu menu, SerializedProperty actions, string label)
            where T : VF.Model.StateAction.Action, new() {
            menu.AddItem(new GUIContent(label), false, () => {
                actions.serializedObject.Update();
                var index = actions.arraySize;
                actions.InsertArrayElementAtIndex(index);
                actions.GetArrayElementAtIndex(index).managedReferenceValue = new T();
                actions.serializedObject.ApplyModifiedProperties();
            });
        }

        private static void DrawSectionHeader(string title, string subtitle = null) {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(subtitle)) EditorGUILayout.LabelField(subtitle, EditorStyles.wordWrappedMiniLabel);
        }
    }

    internal static class BasisVrcfuryHeader {
        internal static VisualElement Create(string title) {
            var anchor = new VisualElement();
            anchor.RegisterCallback<AttachToPanelEvent>(_ => AttachOverlay(anchor, title));
            return anchor;
        }

        private static void AttachOverlay(VisualElement anchor, string title) {
            var inspector = FindInspector(anchor);
            if (inspector == null) {
                anchor.Add(CreateInlineHeader(title));
                return;
            }

            var parent = inspector.parent;
            if (parent == null) {
                anchor.Add(CreateInlineHeader(title));
                return;
            }

            var headerIndex = -1;
            var index = 0;
            foreach (var child in parent.Children()) {
                if (!string.IsNullOrEmpty(child.name) && child.name.EndsWith("Header", StringComparison.Ordinal)) {
                    headerIndex = index;
                    break;
                }
                index++;
            }
            if (headerIndex < 0) {
                anchor.Add(CreateInlineHeader(title));
                return;
            }

            var overlay = CreateOverlayHeader(title);
            parent.Insert(headerIndex + 1, overlay);
            anchor.RegisterCallback<DetachFromPanelEvent>(_ => overlay.parent?.Remove(overlay));
        }

        private static VisualElement FindInspector(VisualElement element) {
            for (var current = element; current != null; current = current.parent) {
                if (current is InspectorElement) return current;
            }
            return null;
        }

        private static VisualElement CreateInlineHeader(string title) {
            var row = CreateHeaderRow(title);
            row.style.marginTop = 4;
            row.style.marginBottom = 6;
            return row;
        }

        internal static VisualElement CreateOverlayHeader(string title) {
            // Match VRCFuryComponentHeader: the absolute overlay must be positioned relative to a
            // zero-height wrapper inserted immediately after Unity's real component header.
            // Inserting the absolute element directly makes top=-21 relative to the whole inspector,
            // which causes it to overlap a neighboring component and breaks collapse click-through.
            var area = new VisualElement {
                pickingMode = PickingMode.Ignore,
                style = {
                    height = 20,
                    width = Length.Percent(100),
                    top = -21,
                    position = Position.Absolute
                }
            };
            var row = CreateHeaderRow(title);
            row.style.marginLeft = 18;
            row.style.marginRight = 60;
            area.Add(row);

            var wrapper = new VisualElement();
            wrapper.Add(area);
            return wrapper;
        }

        private static VisualElement CreateHeaderRow(string title) {
            Color background = EditorGUIUtility.isProSkin
                ? new Color32(61, 61, 61, 255)
                : new Color32(194, 194, 194, 255);
            var row = new VisualElement {
                pickingMode = PickingMode.Ignore,
                style = {
                    flexDirection = FlexDirection.Row,
                    height = 20,
                    backgroundColor = background
                }
            };
            var badge = new Label("VRCFury") {
                pickingMode = PickingMode.Ignore,
                style = {
                    color = new Color(0.8f, 0.4f, 0f),
                    backgroundColor = new Color(0.05f, 0.05f, 0.05f),
                    paddingLeft = 6,
                    paddingRight = 6,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            row.Add(badge);
            var name = new Label(title) {
                pickingMode = PickingMode.Ignore,
                style = {
                    unityTextAlign = TextAnchor.MiddleLeft,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    paddingLeft = 6,
                    flexGrow = 1
                }
            };
            row.Add(name);
            return row;
        }
    }
}
