using System;
using System.Collections.Generic;
using System.Reflection;
using HVR.Vixxy;
using UnityEditor;
using UnityEngine;
using VF.Model;
using Object = UnityEngine.Object;

namespace VF.Integration.Basis {
    internal static class BasisVrcfuryUtil {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static GameObject CreateChild(Transform parent, string name) {
            var go = new GameObject(SafeName(name));
            Undo.RegisterCreatedObjectUndo(go, "Generate VRCFury BasisVR compatibility");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        public static void SetField<T>(Object target, string fieldName, T value) {
            var field = target.GetType().GetField(fieldName, InstanceFields);
            if (field == null) throw new MissingFieldException(target.GetType().FullName, fieldName);
            field.SetValue(target, value);
            EditorUtility.SetDirty(target);
        }

        public static T GetField<T>(Object target, string fieldName, T fallback = default) {
            if (target == null) return fallback;
            var field = target.GetType().GetField(fieldName, InstanceFields);
            if (field?.GetValue(target) is T value) return value;
            return fallback;
        }

        public static HVRVixxySubject Subject(GameObject target, HVRVixxyPropertyBase property) {
            return new HVRVixxySubject {
                selection = HVRVixxySelection.Normal,
                targets = new[] { target },
                childrenOf = Array.Empty<GameObject>(),
                exceptions = Array.Empty<GameObject>(),
                properties = new List<HVRVixxyPropertyBase> { property }
            };
        }

        public static T ResolveAsset<T>(GuidWrapper wrapper) where T : Object {
            if (wrapper == null) return null;
            if (wrapper.objRef is T direct) return direct;
            if (string.IsNullOrWhiteSpace(wrapper.id)) return null;
            var separator = wrapper.id.IndexOf(':');
            var guid = separator >= 0 ? wrapper.id.Substring(0, separator) : wrapper.id;
            if (string.IsNullOrWhiteSpace(guid)) return null;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        public static string SafeName(string value) {
            if (string.IsNullOrWhiteSpace(value)) return "VRCFury";
            return value.Replace('/', '／').Replace('\\', '＼').Replace(':', '：');
        }

        public static bool TryReadMember<T>(object source, string name, out T value) {
            value = default;
            if (source == null) return false;
            var type = source.GetType();
            var field = type.GetField(name, InstanceFields);
            object raw = field?.GetValue(source);
            if (field == null) {
                var property = type.GetProperty(name, InstanceFields);
                if (property != null && property.GetIndexParameters().Length == 0) raw = property.GetValue(source);
            }
            if (raw is not T typed) return false;
            value = typed;
            return true;
        }

        public static Component FindComponentByTypeName(GameObject obj, string fullName) {
            if (obj == null) return null;
            foreach (var component in obj.GetComponents<Component>()) {
                if (component != null && component.GetType().FullName == fullName) return component;
            }
            return null;
        }

        public static Component FindComponentInParentsByTypeName(GameObject obj, string fullName) {
            for (var t = obj != null ? obj.transform : null; t != null; t = t.parent) {
                var found = FindComponentByTypeName(t.gameObject, fullName);
                if (found != null) return found;
            }
            return null;
        }
    }
}
