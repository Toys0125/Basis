using System;
using System.Reflection;
using NUnit.Framework;
using HVR.Vixxy;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms.Tests
{
    public class HVRVixxyLayeredPropertyTests
    {
        private GameObject _target;
        private GameObject _owners;
        private Light _light;
        private HVRVixxyControl _low;
        private HVRVixxyControl _high;
        private object _resolver;
        private MethodInfo _submit;
        private MethodInfo _submitActivation;
        private MethodInfo _begin;
        private MethodInfo _release;
        private MethodInfo _apply;

        [SetUp]
        public void SetUp()
        {
            _target = new GameObject("Layered property target");
            _light = _target.AddComponent<Light>();
            _light.intensity = 0.4f;
            _owners = new GameObject("Inactive Vixxy test owners");
            _owners.SetActive(false); // Avoid running the real avatar initialization in these resolver unit tests.
            _low = _owners.AddComponent<HVRVixxyControl>();
            _high = _owners.AddComponent<HVRVixxyControl>();
            _low.layerPriority = 0;
            _high.layerPriority = 10;

            var type = typeof(HVRVixxyControl).Assembly.GetType("HVR.Vixxy.HVRVixxyLayeredPropertyResolver", true);
            _resolver = Activator.CreateInstance(type, true);
            _submit = type.GetMethod("SubmitProperty", BindingFlags.Instance | BindingFlags.Public);
            _submitActivation = type.GetMethod("SubmitActivation", BindingFlags.Instance | BindingFlags.Public);
            _begin = type.GetMethod("BeginControl", BindingFlags.Instance | BindingFlags.Public);
            _release = type.GetMethod("ReleaseControl", BindingFlags.Instance | BindingFlags.Public);
            _apply = type.GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_owners);
            Object.DestroyImmediate(_target);
        }

        private static HVRVixxyPropertyFloat FloatProperty(string name, float off, float on, bool offPassthrough = false)
        {
            var property = new HVRVixxyPropertyFloat
            {
                propertyName = name,
                choices = new[] { off, on },
                passthroughChoices = new[] { offPassthrough, false }
            };
            var baseType = typeof(HVRVixxyPropertyBase);
            var kindField = baseType.GetField("KindMarker", BindingFlags.Instance | BindingFlags.NonPublic);
            kindField.SetValue(property, Enum.Parse(kindField.FieldType, "PropertyAccess"));
            baseType.GetField("TPropertyIfMarkedAsTPropertyAccess", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(property, typeof(Light).GetProperty(name));
            return property;
        }

        private void Submit(HVRVixxyControl owner, HVRVixxyPropertyFloat property,
            int from = 1, int to = 1, float fraction = 0f)
        {
            _submit.Invoke(_resolver, new object[] { owner, property, _light, from, to, fraction, fraction });
        }

        private void Apply() => _apply.Invoke(_resolver, null);
        private void Release(HVRVixxyControl owner) => _release.Invoke(_resolver, new object[] { owner });

        [Test]
        public void ExplicitFalseOverridesTrueButPassthroughReleasesIt()
        {
            _submitActivation.Invoke(_resolver, new object[] { _low, _target.transform, true, false });
            _submitActivation.Invoke(_resolver, new object[] { _high, _target.transform, false, false });
            Apply();
            Assert.That(_target.activeSelf, Is.False);

            // The control clears its previous contributions at the beginning of each actuation.
            _begin.Invoke(_resolver, new object[] { _high });
            _submitActivation.Invoke(_resolver, new object[] { _high, _target.transform, false, true });
            Apply();
            Assert.That(_target.activeSelf, Is.True);

            Release(_low);
            Apply();
            Assert.That(_target.activeSelf, Is.True);
        }

        [Test]
        public void HigherPriorityPassthroughRevealsLowerAndRestoresOriginal()
        {
            var low = FloatProperty("intensity", 2f, 4f);
            var high = FloatProperty("intensity", 1f, 9f, true);
            Submit(_low, low);
            Submit(_high, high, 0, 0);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(4f).Within(0.001f));

            Submit(_high, high, 1, 1);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(9f).Within(0.001f));

            Release(_high);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(4f).Within(0.001f));

            Release(_low);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(0.4f).Within(0.001f));
        }

        [Test]
        public void ActuationOrderCannotStealPriority()
        {
            var low = FloatProperty("intensity", 2f, 2f);
            var high = FloatProperty("intensity", 8f, 8f);
            Submit(_high, high);
            Submit(_low, low);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(8f).Within(0.001f));

            _begin.Invoke(_resolver, new object[] { _low });
            Submit(_low, low);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(8f).Within(0.001f));
        }

        [Test]
        public void PassthroughInterpolatesFromResolvedLowerValue()
        {
            Submit(_low, FloatProperty("intensity", 2f, 2f));
            Submit(_high, FloatProperty("intensity", -5f, 10f, true), 0, 1, 0.5f);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(6f).Within(0.001f));
        }

        [Test]
        public void ReleasingHigherLayerDoesNotDiscardIndependentProperties()
        {
            Submit(_low, FloatProperty("intensity", 2f, 2f));
            Submit(_low, FloatProperty("range", 7f, 7f));
            Submit(_high, FloatProperty("intensity", 5f, 5f));
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(5f).Within(0.001f));
            Assert.That(_light.range, Is.EqualTo(7f).Within(0.001f));

            Release(_high);
            Apply();
            Assert.That(_light.intensity, Is.EqualTo(2f).Within(0.001f));
            Assert.That(_light.range, Is.EqualTo(7f).Within(0.001f));
        }
    }
}
