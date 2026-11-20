using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms.OSC;
using HVR.Basis.Comms.OSC.Lyuma;
using HVR.Osushi;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms.Tests
{
    [NonParallelizable]
    public class OSCSupplementalFaceTrackingTests
    {
        private const string PupilAddress = "FT/v2/PupilDilation";
        private const string PupilPath = "/avatar/parameters/" + PupilAddress;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo SceneInstanceField = typeof(OSCAcquisitionServer).GetField("_sceneInstance", BindingFlags.Static | BindingFlags.NonPublic);

        private GameObject _serverObject;
        private GameObject _ownerA;
        private GameObject _ownerB;
        private OSCAcquisitionServer _server;
        private OSCAcquisitionServer _previousServer;
        private bool _previousNativeFaceData;

        [SetUp]
        public void SetUp()
        {
            _previousNativeFaceData = OSCAcquisitionServer.NativeFaceData;
            _previousServer = (OSCAcquisitionServer)SceneInstanceField.GetValue(null);
            OSCAcquisitionServer.NativeFaceData = true;
            _serverObject = new GameObject("SupplementalOscQueryServer");
            _serverObject.SetActive(false);
            _server = _serverObject.AddComponent<OSCAcquisitionServer>();
            SceneInstanceField.SetValue(null, _server);
            _ownerA = new GameObject("SupplementalOscOwnerA");
            _ownerB = new GameObject("SupplementalOscOwnerB");
        }

        [TearDown]
        public void TearDown()
        {
            SceneInstanceField.SetValue(null, _previousServer);
            OSCAcquisitionServer.NativeFaceData = _previousNativeFaceData;
            Object.DestroyImmediate(_ownerA);
            Object.DestroyImmediate(_ownerB);
            Object.DestroyImmediate(_serverObject);
        }

        [Test]
        public void SupplementalParameter_IsDiscoverableAsFloatBeforeAnyOscValueArrives()
        {
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });

            OsushiNode pupil = Node("avatar", "parameters", "FT", "v2", "PupilDilation");
            Assert.That(pupil, Is.Not.Null);
            Assert.That(pupil.FULL_PATH, Is.EqualTo(PupilPath));
            Assert.That(pupil.TYPE, Is.EqualTo("f"));
            Assert.That(pupil.ACCESS & 2, Is.EqualTo(2));
            string json = Query("/avatar");
            StringAssert.Contains("\"FULL_PATH\": \"" + PupilPath + "\"", json);
            StringAssert.Contains("\"TYPE\": \"f\"", Query(PupilPath));
            AssertNativeFaceData(false);
        }

        [Test]
        public void Registration_IgnoresAddressesOutsideTheRelativeFaceTrackingNamespace()
        {
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[]
            {
                null, "", " ", "FT/v2/", "HVR/Internal/FaceTrackingActive",
                "Other/v2/PupilDilation", "/avatar/parameters/FT/v2/PupilDilation",
                "FT/v2/JawOpen"
            });

            Assert.That(Node("avatar", "parameters", "FT", "v2").CONTENTS.Count,
                Is.EqualTo(OsushiUtil.FaceTrackingParameterNames.Count));
            Assert.That(Node("avatar", "parameters", "HVR"), Is.Null);
            Assert.That(Node("avatar", "parameters", "Other"), Is.Null);
            Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Null);
            AssertNativeFaceData(true);
        }

        [Test]
        public void OwnerUpdates_DeduplicateSharedInputsAndRemoveOnlyTheLastOwner()
        {
            EntityId ownerA = _ownerA.GetEntityId();
            EntityId ownerB = _ownerB.GetEntityId();
            _server.UpdateFaceTrackingParameters(ownerA, new[] { PupilAddress, PupilAddress, "FT/v2/JawOpen" });
            _server.UpdateFaceTrackingParameters(ownerA, new[] { PupilAddress, "FT/v2/JawOpen" });
            _server.UpdateFaceTrackingParameters(ownerB, new[] { PupilAddress });
            _server.ClearFaceTrackingParameters(ownerA);
            _server.ClearFaceTrackingParameters(ownerA);

            Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Not.Null);
            AssertNativeFaceData(false);

            _server.UpdateFaceTrackingParameters(ownerB, new[] { "FT/v2/CustomExpression" });
            Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Null);
            Assert.That(Node("avatar", "parameters", "FT", "v2", "CustomExpression").TYPE, Is.EqualTo("f"));
            AssertNativeFaceData(false);

            _server.ClearFaceTrackingParameters(ownerB);
            Assert.That(Node("avatar", "parameters", "FT", "v2", "CustomExpression"), Is.Null);
            Assert.That(Node("avatar", "parameters", "FT", "v2", "JawOpen").TYPE, Is.EqualTo("f"));
            Assert.That(Node("avatar", "parameters", "FT", "v2").CONTENTS.Count,
                Is.EqualTo(OsushiUtil.FaceTrackingParameterNames.Count));
            Assert.That(Node("chatbox", "input"), Is.Not.Null);
            AssertNativeFaceData(true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Removal_PreservesAnIndependentExactOrPrefixSubscription(bool prefix)
        {
            _server.UpdateSubscriptions(_ownerB.GetEntityId(),
                prefix ? null : new[] { PupilPath },
                prefix ? new[] { PupilPath } : null);
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });
            _server.ClearFaceTrackingParameters(_ownerA.GetEntityId());

            OsushiNode retained = Node("avatar", "parameters", "FT", "v2", "PupilDilation");
            Assert.That(retained, Is.Not.Null);
            Assert.That(retained.ACCESS, Is.EqualTo(prefix ? 0 : 2));
            Assert.That(retained.TYPE, Is.Null);
            Assert.That(retained.VALUE, Is.Null);

            _server.ClearSubscriptions(_ownerB.GetEntityId());
            Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Null);
        }

        [Test]
        public void Removal_PreservesAnIndependentlySubscribedAncestor()
        {
            _server.UpdateSubscriptions(_ownerB.GetEntityId(), null,
                new[] { "/avatar/parameters/FT/v2/Custom" });
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(),
                new[] { "FT/v2/Custom/PupilDilation" });

            _server.ClearFaceTrackingParameters(_ownerA.GetEntityId());

            Assert.That(Node("avatar", "parameters", "FT", "v2", "Custom", "PupilDilation"), Is.Null);
            Assert.That(Node("avatar", "parameters", "FT", "v2", "Custom"), Is.Not.Null);

            _server.ClearSubscriptions(_ownerB.GetEntityId());
            Assert.That(Node("avatar", "parameters", "FT", "v2", "Custom"), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Removal_PreservesAnIndependentlyPublishedValue(bool publishBeforeRegistration)
        {
            if (publishBeforeRegistration) _server.PublishValue(PupilAddress, OscData.Float32(0.75f));
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });
            if (!publishBeforeRegistration) _server.PublishValue(PupilAddress, OscData.Float32(0.75f));
            _server.ClearFaceTrackingParameters(_ownerA.GetEntityId());

            OsushiNode retained = Node("avatar", "parameters", "FT", "v2", "PupilDilation");
            Assert.That(retained, Is.Not.Null);
            Assert.That(retained.FULL_PATH, Is.EqualTo(PupilPath));
            Assert.That(retained.TYPE, Is.EqualTo(",f"));
            Assert.That(retained.VALUE, Is.EqualTo(new object[] { 0.75f }));
            Assert.That(retained.ACCESS, Is.EqualTo(3));
        }

        [Test]
        public void RemovingSupplementalInputs_RespectsNativeFaceDataDisabledByTheUser()
        {
            OSCAcquisitionServer.NativeFaceData = false;
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });
            _server.ClearFaceTrackingParameters(_ownerA.GetEntityId());

            AssertNativeFaceData(false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClassicFallback_PreservesFloatsDespiteRecentNativeDataOrAnInFlightBlob(bool includeBlob)
        {
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });
            SetPrivate(_server, "_lastNativeFaceTime", Time.unscaledTime);
            var messages = new List<SimpleOSC.OSCMessage>
            {
                new() { path = PupilPath, arguments = new object[] { 0.7f } },
                new() { path = "/avatar/parameters/FT/v2/JawOpen", arguments = new object[] { 0.25f } }
            };
            if (includeBlob)
            {
                messages.Add(new SimpleOSC.OSCMessage
                {
                    path = BasisVrcftNativeFaceDecoder.Address,
                    arguments = new object[] { new byte[102] }
                });
            }

            InvokePrivate(_server, "ExpandNativeFaceData", messages);

            Assert.That(messages.Count, Is.EqualTo(includeBlob ? 3 : 2));
            Assert.That(messages[0].path, Is.EqualTo(PupilPath));
            Assert.That(messages[0].arguments[0], Is.EqualTo(0.7f));
            Assert.That(messages[1].path, Is.EqualTo("/avatar/parameters/FT/v2/JawOpen"));
            Assert.That(messages[1].arguments[0], Is.EqualTo(0.25f));
        }

        [Test]
        public void AvatarWakeUp_RemainsQueuedWhileTheClientIsUnavailable()
        {
            _server.SendWakeUpMessage("avtr_supplemental_test");
            _server.UpdateFaceTrackingParameters(_ownerA.GetEntityId(), new[] { PupilAddress });

            Assert.That(GetPrivate(_server, "_wakeUpPending"), Is.True);
            Assert.That(GetPrivate(_server, "_lastWakeUp"), Is.EqualTo("avtr_supplemental_test"));
            Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Not.Null);

            InvokePrivate(_server, "FlushWakeUpMessage");

            Assert.That(GetPrivate(_server, "_wakeUpPending"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BlendshapeBinding_AdvertisesOnlyMatchedInputsForTheWearer(bool isWearer)
        {
            var avatarObject = new GameObject("SupplementalBindingAvatar");
            avatarObject.SetActive(false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            var file = ScriptableObject.CreateInstance<BlendshapeActuationDefinitionFile>();
            try
            {
                mesh.AddBlendShapeFrame("EyeDilation", 100f, new[] { Vector3.up }, null, null);
                BasisAvatar avatar = avatarObject.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = isWearer;
                HVRAvatarComms comms = avatarObject.AddComponent<HVRAvatarComms>();
                typeof(HVRAvatarComms).GetProperty(nameof(HVRAvatarComms.VariableStore))
                    .SetValue(comms, new HVRVariableStore());
                // Keep the fixture inactive to exercise binding without starting sockets or the avatar network.
                avatarObject.AddComponent<FaceTrackingActivityRelay>();
                SkinnedMeshRenderer renderer = avatarObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                file.definitions = new[]
                {
                    new BlendshapeActuationDefinition
                    {
                        address = PupilAddress, blendshapes = new[] { "EyeDilation" },
                        inStart = 0f, inEnd = 1f, outStart = 0f, outEnd = 1f
                    },
                    new BlendshapeActuationDefinition
                    {
                        address = "FT/v2/UnboundExpression", blendshapes = new[] { "MissingShape" },
                        inStart = 0f, inEnd = 1f, outStart = 0f, outEnd = 1f
                    }
                };
                BlendshapeActuation actuation = avatarObject.AddComponent<BlendshapeActuation>();
                SetPrivate(actuation, "avatar", avatar);
                actuation.AutoDefine(new[] { file }, new List<SkinnedMeshRenderer> { renderer });

                actuation.OnHVRAvatarReady(isWearer);

                Assert.That(actuation.debugAddresses, Is.EqualTo(new[] { PupilAddress }));
                Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation") != null, Is.EqualTo(isWearer));
                Assert.That(Node("avatar", "parameters", "FT", "v2", "UnboundExpression"), Is.Null);

                BlendshapeActuation secondActuation = null;
                if (isWearer)
                {
                    secondActuation = avatarObject.AddComponent<BlendshapeActuation>();
                    SetPrivate(secondActuation, "avatar", avatar);
                    secondActuation.AutoDefine(new[] { file }, new List<SkinnedMeshRenderer> { renderer });
                    secondActuation.OnHVRAvatarReady(true);
                }

                InvokePrivate(actuation, "OnDestroy");
                Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation") != null, Is.EqualTo(isWearer));
                if (secondActuation != null)
                {
                    InvokePrivate(secondActuation, "OnDestroy");
                    Assert.That(Node("avatar", "parameters", "FT", "v2", "PupilDilation"), Is.Null);
                }
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(file);
                Object.DestroyImmediate(mesh);
            }
        }

        private string Query(string path)
        {
            return (string)InvokePrivate(_server, "GetOscQueryResponse", path);
        }

        private OsushiNode Node(params string[] path)
        {
            Query("/");
            OsushiNode node = (OsushiNode)GetPrivate(_server, "_oscQueryRoot");
            foreach (string segment in path)
            {
                if (node.CONTENTS == null || !node.CONTENTS.TryGetValue(segment, out node)) return null;
            }
            return node;
        }

        private void AssertNativeFaceData(bool expected)
        {
            OsushiNode marker = Node("avatar", "avatarfullfacetracking");
            Assert.That(marker.TYPE, Is.EqualTo(expected ? "T" : "F"));
            Assert.That(marker.VALUE, Is.EqualTo(new object[] { expected }));
        }

        private static object GetPrivate(object target, string name)
        {
            return target.GetType().GetField(name, PrivateInstance).GetValue(target);
        }

        private static void SetPrivate(object target, string name, object value)
        {
            target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
        }

        private static object InvokePrivate(object target, string name, params object[] arguments)
        {
            return target.GetType().GetMethod(name, PrivateInstance).Invoke(target, arguments);
        }
    }
}
