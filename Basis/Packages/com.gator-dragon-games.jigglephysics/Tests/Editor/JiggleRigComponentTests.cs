using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleRigEditorLifecycleTests {
    private readonly List<GameObject> spawned = new();

    [TearDown]
    public void TearDown() {
        for (int i = 0; i < spawned.Count; i++) {
            if (spawned[i]) {
                UnityEngine.Object.DestroyImmediate(spawned[i]);
            }
        }
        spawned.Clear();
    }

    private GameObject Spawn(string name, Transform parent = null) {
        var gameObject = new GameObject(name);
        spawned.Add(gameObject);
        if (parent != null) {
            gameObject.transform.SetParent(parent, false);
        }
        return gameObject;
    }

    private static void SetRigData(JiggleRig rig, JiggleRigData data) {
        const string fieldName = "jiggleRigData";
        var field = typeof(JiggleRig).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) {
            throw new InvalidOperationException($"JiggleRig.{fieldName} was renamed; the test helper needs updating.");
        }
        field.SetValue(rig, data);
    }

    [Test]
    public void AddingJiggleRig_InEditMode_LeavesRuntimeUninitialized() {
        Assert.IsFalse(Application.isPlaying);
        var host = Spawn("editorRig");

        var rig = host.AddComponent<JiggleRig>();
        rig.OnInitialize();

        Assert.IsTrue(rig.enabled);
        Assert.IsNull(rig.GetJiggleTree());
    }

    [Test]
    public void PrepareSerializedDataForEditor_AfterSerializedAssignment_PreservesRootAndBuildsCache() {
        Assert.IsFalse(Application.isPlaying);
        var root = Spawn("root");
        var child = Spawn("child", root.transform);
        child.transform.localPosition = new Vector3(0f, -0.25f, 0f);
        var host = Spawn("host");
        var rig = host.AddComponent<JiggleRig>();

        var serializedObject = new SerializedObject(rig);
        serializedObject.Update();
        serializedObject.FindProperty("jiggleRigData")
            .FindPropertyRelative(nameof(JiggleRigData.rootBone)).objectReferenceValue = root.transform;
        serializedObject.ApplyModifiedProperties();

        rig.PrepareSerializedDataForEditor();

        var data = rig.GetJiggleRigData();
        Assert.IsTrue(data.hasSerializedData);
        Assert.AreSame(root.transform, data.rootBone);
        Assert.AreEqual(2, data.transformCachedData.Length);
        Assert.AreSame(root.transform, data.transformCachedData[0].bone);
        Assert.AreSame(child.transform, data.transformCachedData[1].bone);
    }

    [Test]
    public void PrepareSerializedDataForEditor_WhenDataIsUninitialized_AppliesDefaults() {
        var host = Spawn("host");
        var rig = host.AddComponent<JiggleRig>();
        SetRigData(rig, default);

        rig.PrepareSerializedDataForEditor();

        var data = rig.GetJiggleRigData();
        Assert.IsTrue(data.hasSerializedData);
        Assert.AreEqual("v0.0.2", data.serializedVersion);
        Assert.NotNull(data.excludedTransforms);
        Assert.NotNull(data.transformCachedData);
        Assert.NotNull(data.jiggleColliders);
    }
}

}
