using System;
using System.Collections.Generic;
using System.Text;
using Basis.BasisUI;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisCilboxStatusType
	{
		Avatar,
		Prop,
		Scene,
	}

	public struct BasisCilboxStatusSnapshot
	{
		public BasisCilboxStatusType Type;
		public string Name;
		public bool IsActiveAndEnabled;
		public bool IsDisabledByCilbox;
		public string DisabledReason;
		public long CpuUsedUs;
		public long CpuBudgetUs;
		public long EstimatedMemoryBytes;
		public int ProxyCount;
	}

	public static class BasisCilboxStatusTracker
	{
		private const int ObjectOverheadBytes = 24;
		private const int ReferenceBytes = 8;
		private const int StackElementBytes = 24;
		private const int MaxObjectEstimateDepth = 2;

		private sealed class TrackedCilbox
		{
			public Cilbox.Cilbox Box;
			public BasisCilboxStatusType Type;
		}

		private static readonly List<TrackedCilbox> Tracked = new List<TrackedCilbox>();

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetRuntimeState()
		{
			Tracked.Clear();
			SettingsProvider.CilboxStatusBuilder -= BuildSettingsSection;
		}

		[RuntimeInitializeOnLoadMethod]
		private static void RegisterSettingsBuilder()
		{
			SettingsProvider.CilboxStatusBuilder -= BuildSettingsSection;
			SettingsProvider.CilboxStatusBuilder += BuildSettingsSection;
		}

		public static void Register(Cilbox.Cilbox box, BasisCilboxStatusType type)
		{
			if (ReferenceEquals(box, null)) return;

			int index = IndexOf(box);
			if (index >= 0)
			{
				Tracked[index].Type = type;
				return;
			}

			Tracked.Add(new TrackedCilbox
			{
				Box = box,
				Type = type,
			});
		}

		public static void Unregister(Cilbox.Cilbox box)
		{
			if (ReferenceEquals(box, null)) return;

			for (int i = Tracked.Count - 1; i >= 0; i--)
			{
				if (ReferenceEquals(Tracked[i].Box, box))
				{
					Tracked.RemoveAt(i);
				}
			}
		}

		public static void FillSnapshots(List<BasisCilboxStatusSnapshot> snapshots)
		{
			if (snapshots == null) return;
			snapshots.Clear();
			CleanupDestroyed();
			if (Tracked.Count == 0) return;

			Cilbox.CilboxProxy[] proxies = UnityEngine.Object.FindObjectsByType<Cilbox.CilboxProxy>(
				FindObjectsInactive.Include,
				FindObjectsSortMode.None);

			for (int i = 0; i < Tracked.Count; i++)
			{
				TrackedCilbox tracked = Tracked[i];
				Cilbox.Cilbox box = tracked.Box;
				if (box == null) continue;

				long memoryBytes = EstimateMemoryBytes(box, proxies, out int proxyCount);
				snapshots.Add(new BasisCilboxStatusSnapshot
				{
					Type = tracked.Type,
					Name = box.gameObject != null ? box.gameObject.name : box.name,
					IsActiveAndEnabled = box.isActiveAndEnabled,
					IsDisabledByCilbox = box.disabled,
					DisabledReason = box.disabledReason,
					CpuUsedUs = Math.Max(0, box.usSpentLastFrame),
					CpuBudgetUs = Math.Max(0, box.timeoutLengthUs),
					EstimatedMemoryBytes = memoryBytes,
					ProxyCount = proxyCount,
				});
			}
		}

		public static void BuildSettingsSection(RectTransform parent)
		{
			PanelElementDescriptor statusField = PanelElementDescriptor.CreateNew(
				PanelElementDescriptor.ElementStyles.Group,
				parent);
			statusField.SetTitle("Tracked Cilboxes");
			statusField.SetDescription("Scanning...");
			statusField.IsolateAsCanvas();

			GameObject updaterGO = new GameObject("CilboxStatusPanelUpdater");
			updaterGO.transform.SetParent(statusField.transform, false);
			updaterGO.AddComponent<BasisCilboxStatusPanelUpdater>().Initialize(statusField);
		}

		public static string BuildStatusText(List<BasisCilboxStatusSnapshot> snapshots, StringBuilder builder)
		{
			if (builder == null) builder = new StringBuilder(512);
			builder.Clear();

			if (snapshots == null || snapshots.Count == 0)
			{
				return "No Cilbox instances are registered.";
			}

			long totalCpuUsedUs = 0;
			long totalCpuBudgetUs = 0;
			long totalMemoryBytes = 0;
			int totalProxyCount = 0;
			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				totalCpuUsedUs += snapshot.CpuUsedUs;
				totalCpuBudgetUs += snapshot.CpuBudgetUs;
				totalMemoryBytes += snapshot.EstimatedMemoryBytes;
				totalProxyCount += snapshot.ProxyCount;
			}

			builder.Append("Total CPU: ").Append(FormatCpu(totalCpuUsedUs, totalCpuBudgetUs)).AppendLine();
			builder.Append("Total Memory: ~").Append(FormatBytes(totalMemoryBytes));
			builder.Append(" | Cilboxes: ").Append(snapshots.Count);
			builder.Append(" | Proxies: ").Append(totalProxyCount).AppendLine();

			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				builder.AppendLine();
				builder.Append(snapshot.Type).Append(" • ").Append(string.IsNullOrEmpty(snapshot.Name) ? "(unnamed)" : snapshot.Name).AppendLine();
				builder.Append("CPU: ").Append(FormatCpu(snapshot.CpuUsedUs, snapshot.CpuBudgetUs));
				builder.Append(" | Memory: ~").Append(FormatBytes(snapshot.EstimatedMemoryBytes));
				builder.Append(" | Proxies: ").Append(snapshot.ProxyCount);

				if (!snapshot.IsActiveAndEnabled)
				{
					builder.Append(" | Inactive");
				}

				if (snapshot.IsDisabledByCilbox)
				{
					builder.Append(" | Disabled");
					if (!string.IsNullOrEmpty(snapshot.DisabledReason))
					{
						builder.Append(": ").Append(TrimReason(snapshot.DisabledReason));
					}
				}

				builder.AppendLine();
			}

			return builder.ToString();
		}

		private static int IndexOf(Cilbox.Cilbox box)
		{
			for (int i = 0; i < Tracked.Count; i++)
			{
				if (ReferenceEquals(Tracked[i].Box, box)) return i;
			}
			return -1;
		}

		private static void CleanupDestroyed()
		{
			for (int i = Tracked.Count - 1; i >= 0; i--)
			{
				if (Tracked[i].Box == null)
				{
					Tracked.RemoveAt(i);
				}
			}
		}

		private static long EstimateMemoryBytes(Cilbox.Cilbox box, Cilbox.CilboxProxy[] proxies, out int proxyCount)
		{
			proxyCount = 0;
			if (box == null) return 0;

			long bytes = 128;
			bytes += EstimateString(box.assemblyData);
			bytes += EstimateString(box.disabledReason);
			bytes += EstimateStringIntDictionary(box.classes);
			bytes += EstimateEnums(box.cilboxEnums);

			if (box.classesList != null)
			{
				bytes += ObjectOverheadBytes + box.classesList.Length * ReferenceBytes;
				for (int i = 0; i < box.classesList.Length; i++)
				{
					bytes += EstimateClass(box.classesList[i]);
				}
			}

			if (box.metadatas != null)
			{
				bytes += ObjectOverheadBytes + box.metadatas.Length * ReferenceBytes;
				for (int i = 0; i < box.metadatas.Length; i++)
				{
					bytes += EstimateMetadata(box.metadatas[i]);
				}
			}

			if (proxies != null)
			{
				for (int i = 0; i < proxies.Length; i++)
				{
					Cilbox.CilboxProxy proxy = proxies[i];
					if (proxy == null || !ReferenceEquals(proxy.box, box)) continue;
					proxyCount++;
					bytes += EstimateProxy(proxy);
				}
			}

			return Math.Max(0, bytes);
		}

		private static long EstimateClass(Cilbox.CilboxClass cls)
		{
			if (cls == null) return 0;

			long bytes = 128;
			bytes += EstimateString(cls.className);
			bytes += EstimateObjectArray(cls.staticFields);
			bytes += EstimateStringArray(cls.staticFieldNames);
			bytes += EstimateReferenceArray(cls.staticFieldTypes);
			bytes += EstimateStringArray(cls.instanceFieldNames);
			bytes += EstimateReferenceArray(cls.instanceFieldTypes);
			bytes += EstimateStringUIntDictionary(cls.methodNameToIndex);
			bytes += EstimateStringUIntDictionary(cls.methodFullSignatureToIndex);
			bytes += EstimateUIntArray(cls.importFunctionToId);

			if (cls.methods != null)
			{
				bytes += ObjectOverheadBytes + cls.methods.Length * ReferenceBytes;
				for (int i = 0; i < cls.methods.Length; i++)
				{
					bytes += EstimateMethod(cls.methods[i]);
				}
			}

			return bytes;
		}

		private static long EstimateMethod(Cilbox.CilboxMethod method)
		{
			if (method == null) return 0;

			long bytes = 128;
			bytes += EstimateString(method.methodName);
			bytes += EstimateString(method.fullSignature);
			bytes += EstimateStringArray(method.methodLocals);
			bytes += EstimateReferenceArray(method.typeLocals);
			bytes += EstimateByteArray(method.byteCode);
			bytes += EstimateStringArray(method.signatureParameters);
			bytes += EstimateReferenceArray(method.typeParameters);

			if (method.exceptionClauses != null)
			{
				bytes += ObjectOverheadBytes + method.exceptionClauses.Length * 96;
				for (int i = 0; i < method.exceptionClauses.Length; i++)
				{
					bytes += EstimateString(method.exceptionClauses[i]?.CatchTypeName);
				}
			}

			if (method.handlerOffsetToClauseMap != null)
			{
				bytes += ObjectOverheadBytes + method.handlerOffsetToClauseMap.Count * 48;
			}

			return bytes;
		}

		private static long EstimateMetadata(Cilbox.CilMetadataTokenInfo metadata)
		{
			if (metadata == null) return 0;

			long bytes = 96;
			bytes += EstimateString(metadata.Name);
			bytes += EstimateString(metadata.declaringTypeName);
			bytes += EstimateByteArray(metadata.arrayInitializerData);
			bytes += EstimateReferenceArray(metadata.nativeParameterTypes);
			return bytes;
		}

		private static long EstimateEnums(Dictionary<string, Cilbox.CilboxEnum> enums)
		{
			if (enums == null) return 0;

			long bytes = ObjectOverheadBytes + enums.Count * 64;
			foreach (KeyValuePair<string, Cilbox.CilboxEnum> kv in enums)
			{
				bytes += EstimateString(kv.Key);
				if (kv.Value == null) continue;
				bytes += 96;
				bytes += EstimateString(kv.Value.enumName);
				if (kv.Value.valueToName != null)
				{
					bytes += ObjectOverheadBytes + kv.Value.valueToName.Count * 48;
					foreach (KeyValuePair<long, string> name in kv.Value.valueToName)
					{
						bytes += EstimateString(name.Value);
					}
				}
			}

			return bytes;
		}

		private static long EstimateProxy(Cilbox.CilboxProxy proxy)
		{
			if (proxy == null) return 0;

			long bytes = 160;
			bytes += EstimateString(proxy.className);
			bytes += EstimateString(proxy.serializedObjectData);
			bytes += EstimateString(proxy.buildTimeGuid);
			bytes += EstimateString(proxy.initialLoadPath);
			bytes += EstimateStackElementArray(proxy.fields);
			bytes += EstimateUnityObjectList(proxy.fieldsObjects);
			return bytes;
		}

		private static long EstimateString(string value)
		{
			return string.IsNullOrEmpty(value) ? 0 : ObjectOverheadBytes + value.Length * 2L;
		}

		private static long EstimateByteArray(byte[] value)
		{
			return value == null ? 0 : ObjectOverheadBytes + value.LongLength;
		}

		private static long EstimateUIntArray(uint[] value)
		{
			return value == null ? 0 : ObjectOverheadBytes + value.LongLength * 4L;
		}

		private static long EstimateStackElementArray(Cilbox.StackElement[] value)
		{
			return value == null ? 0 : ObjectOverheadBytes + value.LongLength * StackElementBytes;
		}

		private static long EstimateStringArray(string[] values)
		{
			if (values == null) return 0;

			long bytes = ObjectOverheadBytes + values.LongLength * ReferenceBytes;
			for (int i = 0; i < values.Length; i++)
			{
				bytes += EstimateString(values[i]);
			}
			return bytes;
		}

		private static long EstimateReferenceArray(Array values)
		{
			return values == null ? 0 : ObjectOverheadBytes + values.LongLength * ReferenceBytes;
		}

		private static long EstimateObjectArray(object[] values)
		{
			if (values == null) return 0;

			long bytes = ObjectOverheadBytes + values.LongLength * ReferenceBytes;
			for (int i = 0; i < values.Length; i++)
			{
				bytes += EstimateKnownObject(values[i], 0);
			}
			return bytes;
		}

		private static long EstimateUnityObjectList(List<UnityEngine.Object> values)
		{
			return values == null ? 0 : ObjectOverheadBytes + values.Count * ReferenceBytes;
		}

		private static long EstimateStringIntDictionary(Dictionary<string, int> values)
		{
			if (values == null) return 0;

			long bytes = ObjectOverheadBytes + values.Count * 48;
			foreach (KeyValuePair<string, int> kv in values)
			{
				bytes += EstimateString(kv.Key);
			}
			return bytes;
		}

		private static long EstimateStringUIntDictionary(Dictionary<string, uint> values)
		{
			if (values == null) return 0;

			long bytes = ObjectOverheadBytes + values.Count * 48;
			foreach (KeyValuePair<string, uint> kv in values)
			{
				bytes += EstimateString(kv.Key);
			}
			return bytes;
		}

		private static long EstimateKnownObject(object value, int depth)
		{
			if (value == null || depth > MaxObjectEstimateDepth) return 0;
			if (value is string str) return EstimateString(str);
			if (value is byte[] bytes) return EstimateByteArray(bytes);
			if (value is Array array)
			{
				Type elementType = array.GetType().GetElementType();
				long total = ObjectOverheadBytes + array.LongLength * EstimateArrayElementBytes(elementType);

				// Avoid walking huge arrays every UI tick. For small reference arrays, add a
				// bounded estimate of the objects they retain.
				if (depth < MaxObjectEstimateDepth && array.LongLength <= 128 && elementType != null && !elementType.IsValueType)
				{
					foreach (object item in array)
					{
						total += EstimateKnownObject(item, depth + 1);
					}
				}
				return total;
			}

			Type type = value.GetType();
			if (type.IsPrimitive || type.IsEnum) return 16;
			if (type.IsValueType) return 32;
			return 64;
		}

		private static long EstimateArrayElementBytes(Type elementType)
		{
			if (elementType == null) return ReferenceBytes;
			if (!elementType.IsValueType) return ReferenceBytes;
			if (elementType == typeof(bool) || elementType == typeof(byte) || elementType == typeof(sbyte)) return 1;
			if (elementType == typeof(short) || elementType == typeof(ushort) || elementType == typeof(char)) return 2;
			if (elementType == typeof(int) || elementType == typeof(uint) || elementType == typeof(float)) return 4;
			if (elementType == typeof(long) || elementType == typeof(ulong) || elementType == typeof(double)) return 8;
			return 16;
		}

		private static string FormatCpu(long usedUs, long budgetUs)
		{
			if (budgetUs <= 0)
			{
				return FormatMicroseconds(usedUs) + " / N/A";
			}

			double percentage = usedUs * 100.0 / budgetUs;
			return FormatMicroseconds(usedUs) + " / " + FormatMicroseconds(budgetUs) + " (" + percentage.ToString("0.0") + "%)";
		}

		private static string FormatMicroseconds(long microseconds)
		{
			if (microseconds >= 1000)
			{
				return (microseconds / 1000.0).ToString("0.###") + " ms";
			}

			return microseconds + " us";
		}

		private static string FormatBytes(long bytes)
		{
			const double kib = 1024.0;
			const double mib = kib * 1024.0;
			const double gib = mib * 1024.0;

			if (bytes >= (long)gib) return (bytes / gib).ToString("0.##") + " GiB";
			if (bytes >= (long)mib) return (bytes / mib).ToString("0.##") + " MiB";
			if (bytes >= (long)kib) return (bytes / kib).ToString("0.##") + " KiB";
			return bytes + " B";
		}

		private static string TrimReason(string reason)
		{
			if (string.IsNullOrEmpty(reason)) return string.Empty;
			const int maxLength = 160;
			return reason.Length <= maxLength ? reason : reason.Substring(0, maxLength) + "...";
		}
	}

	public sealed class BasisCilboxStatusPanelUpdater : MonoBehaviour
	{
		private const float UpdateInterval = 0.25f;

		private readonly List<BasisCilboxStatusSnapshot> snapshots = new List<BasisCilboxStatusSnapshot>();
		private readonly StringBuilder builder = new StringBuilder(1024);

		private PanelElementDescriptor statusField;
		private float updateTimer;
		private string lastDescription;
		private bool richTextDisabled;

		public void Initialize(PanelElementDescriptor field)
		{
			statusField = field;
			Refresh();
		}

		private void Update()
		{
			updateTimer += Time.unscaledDeltaTime;
			if (updateTimer < UpdateInterval) return;
			updateTimer = 0f;
			Refresh();
		}

		private void Refresh()
		{
			if (statusField == null) return;

			if (!richTextDisabled)
			{
				statusField.DisableRichText();
				richTextDisabled = true;
			}

			BasisCilboxStatusTracker.FillSnapshots(snapshots);
			string description = BasisCilboxStatusTracker.BuildStatusText(snapshots, builder);
			if (!string.Equals(description, lastDescription, StringComparison.Ordinal))
			{
				statusField.SetDescription(description);
				lastDescription = description;
			}
		}
	}
}
