using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisCilboxExecutionBudgetTests
    {
        readonly List<GameObject> createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in createdObjects)
            {
                if (gameObject != null)
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
            createdObjects.Clear();
        }

        CilboxSceneBasis CreateBox(string name, long timeoutUs)
        {
            var gameObject = new GameObject(name);
            createdObjects.Add(gameObject);
            CilboxSceneBasis box = gameObject.AddComponent<CilboxSceneBasis>();
            box.timeoutLengthUs = timeoutUs;
            return box;
        }

        static string SignatureOf(string methodName)
        {
            MethodInfo method = typeof(CilboxPublicUtils).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            Assert.IsNotNull(method);
            return method.ToString();
        }

        static CilMetadataTokenInfo Bind(global::Cilbox.Cilbox box, string methodName)
        {
            var declaringType = new SerializedTypeDescriptor
            {
                typeName = typeof(CilboxPublicUtils).FullName,
            };
            var token = new CilMetadataTokenInfo(MetaTokenType.mtMethod);
            bool overridden = box.usage.OptionallyOverride(
                methodName,
                declaringType,
                SignatureOf(methodName),
                true,
                Array.Empty<SerializedTypeDescriptor>(),
                ref token);

            Assert.IsTrue(overridden, $"{methodName} did not bind to the Cilbox intrinsic path.");
            return token;
        }

        static long InvokeLong(CilMetadataTokenInfo token)
        {
            StackElement result = token.shim(token, default, default);
            Assert.AreEqual(StackType.Long, result.type);
            return result.l;
        }

        [Test]
        public void BudgetIntrinsics_AreBoundToTheirOwningBox()
        {
            CilboxSceneBasis first = CreateBox("Cilbox budget A", 123456);
            CilboxSceneBasis second = CreateBox("Cilbox budget B", 654321);

            CilMetadataTokenInfo firstToken = Bind(first, nameof(CilboxPublicUtils.GetExecutionBudgetUs));
            CilMetadataTokenInfo secondToken = Bind(second, nameof(CilboxPublicUtils.GetExecutionBudgetUs));

            Assert.AreSame(first, firstToken.opaque);
            Assert.AreSame(second, secondToken.opaque);
            Assert.AreNotSame(firstToken.opaque, secondToken.opaque);
            Assert.IsTrue(firstToken.shimIsStatic);
            Assert.IsFalse(firstToken.shimIsVoid);
            Assert.AreEqual(0, firstToken.shimParameterCount);
            Assert.AreEqual(123456, InvokeLong(firstToken));
            Assert.AreEqual(654321, InvokeLong(secondToken));
        }

        [Test]
        public void RemainingBudgetIntrinsic_ReadsTheLiveDeadlineAndClampsAtZero()
        {
            CilboxSceneBasis box = CreateBox("Cilbox remaining budget", 500000);
            CilMetadataTokenInfo token = Bind(box, nameof(CilboxPublicUtils.GetRemainingExecutionBudgetUs));

            const long expectedRemainingUs = 250000;
            box.interpreterAccountingDropDead = Stopwatch.GetTimestamp() + expectedRemainingUs * box.interpreterTicksInUs;
            long remainingUs = InvokeLong(token);

            Assert.Greater(remainingUs, 0);
            Assert.LessOrEqual(remainingUs, expectedRemainingUs);

            box.interpreterAccountingDropDead = Stopwatch.GetTimestamp() - box.interpreterTicksInUs;
            Assert.AreEqual(0, InvokeLong(token));
        }

        [Test]
        public void BudgetIntrinsics_RejectSpoofedMetadataShapes()
        {
            CilboxSceneBasis box = CreateBox("Cilbox spoof checks", 500000);
            var declaringType = new SerializedTypeDescriptor
            {
                typeName = typeof(CilboxPublicUtils).FullName,
            };

            void AssertRejected(bool isStatic, string signature, SerializedTypeDescriptor[] genericArguments)
            {
                var token = new CilMetadataTokenInfo(MetaTokenType.mtMethod);
                bool overridden = box.usage.OptionallyOverride(
                    nameof(CilboxPublicUtils.GetExecutionBudgetUs),
                    declaringType,
                    signature,
                    isStatic,
                    genericArguments,
                    ref token);

                Assert.IsFalse(overridden);
                Assert.IsNull(token.opaque);
                Assert.IsNull(token.shim);
            }

            AssertRejected(false, SignatureOf(nameof(CilboxPublicUtils.GetExecutionBudgetUs)), Array.Empty<SerializedTypeDescriptor>());
            AssertRejected(true, "Int64 GetExecutionBudgetUs(Int32)", Array.Empty<SerializedTypeDescriptor>());
            AssertRejected(true, SignatureOf(nameof(CilboxPublicUtils.GetExecutionBudgetUs)), new[] { new SerializedTypeDescriptor { typeName = "System.Int32" } });
        }

        [Test]
        public void BudgetUtilityMethods_CannotFallbackToNativeExecution()
        {
            Assert.Throws<InvalidOperationException>(() => CilboxPublicUtils.GetExecutionBudgetUs());
            Assert.Throws<InvalidOperationException>(() => CilboxPublicUtils.GetRemainingExecutionBudgetUs());
        }
    }
}
