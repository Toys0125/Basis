using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public class BasisImagePickupLifecycleTests
    {
        private static readonly Color32 Red = new(255, 0, 0, 255);

        [SetUp]
        public void ResetImagePickupManager()
        {
            BasisImagePickupManager.Shutdown();
        }

        [Test]
        public void DestroyedPickupReleasesManagerOwnedAnimationPayload()
        {
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            var pickupHost = new GameObject("BasisImagePickupDisposeTest");
            BasisNativeAnimationPayload payload = null;
            try
            {
                using BasisAnimatedImageData data = CreateData();
                using var encode = new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload, Is.Not.Null);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.GreaterThan(payloadBytesBefore));

                var pickup = pickupHost.AddComponent<BasisImagePickupObject>();
                System.Guid id = System.Guid.NewGuid();
                pickup.ImageId = id;
                SetPickupManaged(pickup);

                IDictionary images = GetManagerDictionary("_images");
                images.Add(id, pickup);

                System.Type ownedImageType = typeof(BasisImagePickupManager).GetNestedType(
                    "OwnedImage",
                    BindingFlags.NonPublic
                );
                Assert.That(ownedImageType, Is.Not.Null);
                object ownedImage = System.Activator.CreateInstance(ownedImageType);
                FieldInfo ownedObjectField = ownedImageType.GetField("Object");
                FieldInfo ownedPayloadField = ownedImageType.GetField("AnimationPayload");
                Assert.That(ownedObjectField, Is.Not.Null);
                Assert.That(ownedPayloadField, Is.Not.Null);
                ownedObjectField.SetValue(ownedImage, pickup);
                ownedPayloadField.SetValue(ownedImage, payload);

                IDictionary ownedImages = GetManagerDictionary("_owned");
                ownedImages.Add(id, ownedImage);

                Object.DestroyImmediate(pickupHost);
                Assert.That(images.Contains(id), Is.False);
                Assert.That(ownedImages.Contains(id), Is.False);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
            }
            finally
            {
                payload?.Dispose();
                if (pickupHost != null)
                    Object.DestroyImmediate(pickupHost);
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void DestroyedRemotePickupReleasesRetainedAnimationPayload()
        {
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            var pickupHost = new GameObject("BasisRemoteImagePickupDisposeTest");
            BasisNativeAnimationPayload payload = null;
            try
            {
                using BasisAnimatedImageData data = CreateData();
                using var encode = new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload, Is.Not.Null);

                var pickup = pickupHost.AddComponent<BasisImagePickupObject>();
                System.Guid id = System.Guid.NewGuid();
                pickup.ImageId = id;
                SetPickupManaged(pickup);

                IDictionary images = GetManagerDictionary("_images");
                images.Add(id, pickup);

                IDictionary payloads = GetManagerDictionary("_remoteAnimationPayloads");
                payloads.Add(id, payload);

                Object.DestroyImmediate(pickupHost);
                Assert.That(images.Contains(id), Is.False);
                Assert.That(payloads.Contains(id), Is.False);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
            }
            finally
            {
                payload?.Dispose();
                if (pickupHost != null)
                    Object.DestroyImmediate(pickupHost);
                BasisImagePickupManager.Shutdown();
            }
        }

        private static BasisAnimatedImageData CreateData()
        {
            Assert.That(
                BasisAnimatedImageData.TryCreate(
                    2,
                    1,
                    0,
                    new Color32(0, 0, 0, 0),
                    new[]
                    {
                        new BasisAnimatedImageFrameSource(
                            new RectInt(0, 0, 1, 1),
                            50000,
                            BasisAnimationBlend.Source,
                            BasisAnimationDisposal.None,
                            new[] { Red }
                        )
                    },
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.True,
                error
            );
            return data;
        }

        private static IDictionary GetManagerDictionary(string fieldName)
        {
            FieldInfo field = typeof(BasisImagePickupManager).GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(field, Is.Not.Null, fieldName);
            var dictionary = (IDictionary)field.GetValue(null);
            Assert.That(dictionary, Is.Not.Null, fieldName);
            dictionary.Clear();
            return dictionary;
        }

        private static void SetPickupManaged(BasisImagePickupObject pickup)
        {
            FieldInfo managedField = typeof(BasisImagePickupObject).GetField(
                "_managed",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(managedField, Is.Not.Null);
            managedField.SetValue(pickup, true);
        }
    }
}
