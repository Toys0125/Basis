using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleRigComponentTests {
    private const float FixedDeltaTime = 0.02f;

    private sealed class RuntimeScene : IDisposable {
        private readonly List<GameObject> spawned = new();

        public Transform Spawn(string name, Transform parent = null, Vector3 localPosition = default) {
            var gameObject = new GameObject(name);
            spawned.Add(gameObject);
            var transform = gameObject.transform;
            if (parent != null) {
                transform.SetParent(parent, false);
            }
            transform.localPosition = localPosition;
            return transform;
        }

        public Transform Chain(int boneCount, float spacing = 0.25f, string prefix = "bone", Vector3 direction = default) {
            if (boneCount < 1) {
                throw new ArgumentOutOfRangeException(nameof(boneCount));
            }
            var step = direction == default ? new Vector3(0f, -spacing, 0f) : direction.normalized * spacing;
            Transform root = null;
            Transform parent = null;
            for (int i = 0; i < boneCount; i++) {
                var offset = i == 0 ? Vector3.zero : step;
                var bone = Spawn($"{prefix}{i}", parent, offset);
                root ??= bone;
                parent = bone;
            }
            return root;
        }

        public static Transform[] Descend(Transform root, int boneCount) {
            var bones = new Transform[boneCount];
            var current = root;
            for (int i = 0; i < boneCount; i++) {
                bones[i] = current;
                if (i + 1 < boneCount) {
                    current = current.GetChild(0);
                }
            }
            return bones;
        }

        public void Dispose() {
            for (int i = 0; i < spawned.Count; i++) {
                if (spawned[i]) {
                    Object.DestroyImmediate(spawned[i]);
                }
            }
            spawned.Clear();
        }
    }

    private sealed class RigFixture {
        public JiggleRig component;
        public Transform[] bones;
        public Transform Tip => bones[bones.Length - 1];
    }

    private RuntimeScene scene;
    private double time;

    [SetUp]
    public void SetUp() {
        Assert.IsTrue(Application.isPlaying, "JiggleRig runtime tests must execute in Play Mode.");
        ResetJigglePhysicsRuntime();
        scene = new RuntimeScene();
        time = 0.0;
    }

    [TearDown]
    public void TearDown() {
        scene?.Dispose();
        scene = null;
        JigglePhysics.Dispose();
    }

    private static void ResetJigglePhysicsRuntime() {
        var methods = typeof(JigglePhysics).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++) {
            var method = methods[i];
            if (method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>() == null || method.GetParameters().Length != 0) {
                continue;
            }
            method.Invoke(null, null);
            return;
        }
        throw new InvalidOperationException("JigglePhysics runtime initializer was not found.");
    }

    private static void SetRigData(JiggleRig rig, JiggleRigData data) {
        const string fieldName = "jiggleRigData";
        var field = typeof(JiggleRig).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) {
            throw new InvalidOperationException($"JiggleRig.{fieldName} was renamed; the test helper needs updating.");
        }
        field.SetValue(rig, data);
    }

    private static JiggleRigData BuildRigData(Transform root) {
        var data = JiggleRigData.Default();
        data.rootBone = root;
        data.BuildNormalizedDistanceFromRootList();
        data.RegenerateCacheLookup();
        return data;
    }

    private RigFixture CreateRig(string prefix, int boneCount = 4, float stiffness = 0.8f, float gravity = 0f,
        float drag = 0.1f) {
        var root = scene.Chain(boneCount, 0.25f, prefix, new Vector3(0.25f, 0f, 0f));
        var host = scene.Spawn($"{prefix}host");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();
        var data = BuildRigData(root);
        data.jiggleTreeInputParameters.stiffness.value = stiffness;
        data.jiggleTreeInputParameters.gravity.value = gravity;
        data.jiggleTreeInputParameters.drag.value = drag;
        SetRigData(component, data);
        host.gameObject.SetActive(true);
        return new RigFixture { component = component, bones = RuntimeScene.Descend(root, boneCount) };
    }

    private void Frame(int count = 1) {
        for (int i = 0; i < count; i++) {
            time += FixedDeltaTime + 0.001;
            JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
            JigglePhysics.SchedulePose(time);
            JigglePhysics.CompletePose();
        }
        JigglePhysics.CompleteSimulate();
    }

    [Test]
    public void OnEnable_WithoutARootBone_DisablesRig() {
        var host = scene.Spawn("host");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();
        SetRigData(component, JiggleRigData.Default());

        LogAssert.Expect(LogType.Error, "Jiggle Rig on 'host' enabled without a root bone assigned, disabling it.");
        host.gameObject.SetActive(true);

        Assert.IsFalse(component.enabled);
    }

    [Test]
    public void OnEnable_MakesTheBonesSimulate() {
        var rig = CreateRig("live", stiffness: 0.2f, gravity: 6f);
        Frame(4);
        var startY = rig.Tip.position.y;

        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the rig never reached the simulation");
    }

    [Test]
    public void OnInitialize_Twice_DoesNotRegisterTwice() {
        var rig = CreateRig("double");

        rig.component.OnInitialize();

        Assert.DoesNotThrow(() => rig.component.OnInitialize());
        Assert.DoesNotThrow(() => Frame(4));
    }

    [Test]
    public void OnDisable_StopsTheRigFromBeingSimulated() {
        var rig = CreateRig("removed", stiffness: 0.2f, gravity: 6f);
        Frame(20);

        rig.component.enabled = false;
        Frame(6);
        var settled = rig.Tip.position;
        Frame(30);

        Assert.AreEqual(0f, Vector3.Distance(settled, rig.Tip.position), 1e-4f,
            "the rig is still being written to after removal");
    }

    [Test]
    public void OnRemove_BeforeInitialize_IsANoOp() {
        var host = scene.Spawn("never");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();

        Assert.DoesNotThrow(() => component.OnRemove());
    }

    [Test]
    public void OnDisable_ThenOnEnable_ReRegistersTheRig() {
        var rig = CreateRig("recycled", stiffness: 0.2f, gravity: 6f);
        Frame(6);
        rig.component.enabled = false;
        Frame(4);

        rig.component.enabled = true;
        Frame(4);
        var startY = rig.Tip.position.y;
        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the rig did not come back after being re-enabled");
    }

    [Test]
    public void GetJiggleRigData_ReturnsTheConfiguredData() {
        var rig = CreateRig("data");

        var data = rig.component.GetJiggleRigData();

        Assert.AreSame(rig.bones[0], data.rootBone);
    }

    [Test]
    public void GetInputParameters_ReturnsTheAuthoredParameters() {
        var rig = CreateRig("params", stiffness: 0.35f);

        var parameters = rig.component.GetInputParameters();

        Assert.AreEqual(0.35f, parameters.stiffness.value, 1e-6f);
    }

    [Test]
    public void SetInputParameters_ReplacesThemLocally() {
        var rig = CreateRig("params");
        var replacement = JiggleTreeInputParameters.Default();
        replacement.stiffness.value = 0.15f;

        rig.component.SetInputParameters(replacement);

        Assert.AreEqual(0.15f, rig.component.GetInputParameters().stiffness.value, 1e-6f);
    }

    [Test]
    public void SetInputParameters_ThenUpdateParameters_ReachesTheRunningRig() {
        var rig = CreateRig("push", stiffness: 1f, gravity: 0f);
        Frame(20);
        var startY = rig.Tip.position.y;

        var loosened = rig.component.GetInputParameters();
        loosened.stiffness.value = 0f;
        loosened.gravity.value = 20f;
        rig.component.SetInputParameters(loosened);
        rig.component.UpdateParameters();
        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the pushed parameters never took effect");
    }

    [Test]
    public void UpdateParameters_BeforeInitialize_IsANoOp() {
        var host = scene.Spawn("early");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();

        Assert.DoesNotThrow(() => component.UpdateParameters());
    }

    [Test]
    public void HasAnimatedParameters_RoundTrips() {
        var rig = CreateRig("animated");

        Assert.IsFalse(rig.component.HasAnimatedParameters);
        rig.component.HasAnimatedParameters = true;
        Assert.IsTrue(rig.component.HasAnimatedParameters);
    }

    [Test]
    public void SnapToRestPose_RestoresTheBones() {
        var rig = CreateRig("snap");
        rig.bones[1].localPosition = new Vector3(9f, 9f, 9f);

        rig.component.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0.25f, 0f, 0f), rig.bones[1].localPosition), 1e-4f);
    }

    [Test]
    public void ResampleRestPose_AdoptsTheCurrentPoseAsTheNewRest() {
        var rig = CreateRig("resample");
        rig.bones[1].localPosition = new Vector3(0.75f, 0f, 0f);

        rig.component.ResampleRestPose();
        rig.bones[1].localPosition = Vector3.zero;
        rig.component.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0.75f, 0f, 0f), rig.bones[1].localPosition), 1e-4f);
    }

    [Test]
    public void ResampleRestPose_OnALiveRig_DoesNotDuplicateTheTree() {
        var rig = CreateRig("resampleLive");
        Frame(6);

        rig.component.ResampleRestPose();

        Assert.DoesNotThrow(() => Frame(6));
    }

    [Test]
    public void Teleport_BeforeInitialize_IsANoOp() {
        var host = scene.Spawn("teleportEarly");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();

        Assert.DoesNotThrow(() => component.Teleport(new Vector3(1f, 2f, 3f)));
    }

    [Test]
    public void Teleport_CarriesTheSimulationWithTheAvatar() {
        var rig = CreateRig("teleport", stiffness: 0.2f, gravity: 6f, drag: 0.9f);
        Frame(150);
        var offsetBefore = rig.Tip.position - rig.bones[0].position;

        var jump = new Vector3(0f, 0f, 50f);
        rig.bones[0].position += jump;
        rig.component.Teleport(jump);
        Frame(2);

        var offsetAfter = rig.Tip.position - rig.bones[0].position;
        Assert.AreEqual(0f, Vector3.Distance(offsetBefore, offsetAfter), 0.02f,
            "the rig did not travel with the avatar");
    }
}

}
