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
		private const int ReferenceBytes = 8;
		private const int StackElementBytes = 24;
		private const int StringCharBytes = 2;

		private sealed class TrackedCilbox
		{
			public Cilbox.Cilbox Box;
			public BasisCilboxStatusType Type;
		}

		private struct ProxyAggregate
		{
			public int Count;
			public long EstimatedMemoryBytes;
		}

		private static readonly List<TrackedCilbox> Tracked = new List<TrackedCilbox>();
		private static readonly List<Cilbox.CilboxProxy> ProxySnapshot = new List<Cilbox.CilboxProxy>();
		private static readonly Dictionary<Cilbox.Cilbox, ProxyAggregate> ProxyAggregates = new Dictionary<Cilbox.Cilbox, ProxyAggregate>();

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetRuntimeState()
		{
			Tracked.Clear();
			ProxySnapshot.Clear();
			ProxyAggregates.Clear();
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

			Cilbox.CilboxProxy.CopyTrackedProxies(ProxySnapshot);
			BuildProxyAggregates(ProxySnapshot, ProxyAggregates);

			for (int i = 0; i < Tracked.Count; i++)
			{
				TrackedCilbox tracked = Tracked[i];
				Cilbox.Cilbox box = tracked.Box;
				if (box == null) continue;

				ProxyAggregates.TryGetValue(box, out ProxyAggregate proxyAggregate);
				long memoryBytes = EstimateMemoryBytes(box) + proxyAggregate.EstimatedMemoryBytes;
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
					ProxyCount = proxyAggregate.Count,
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
			builder.Append("Known Memory: ~").Append(FormatBytes(totalMemoryBytes));
			builder.Append(" | Cilboxes: ").Append(snapshots.Count);
			builder.Append(" | Proxies: ").Append(totalProxyCount).AppendLine();

			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				builder.AppendLine();
				builder.Append(snapshot.Type).Append(" • ").Append(string.IsNullOrEmpty(snapshot.Name) ? "(unnamed)" : snapshot.Name).AppendLine();
				builder.Append("CPU: ").Append(FormatCpu(snapshot.CpuUsedUs, snapshot.CpuBudgetUs));
				builder.Append(" | Known Memory: ~").Append(FormatBytes(snapshot.EstimatedMemoryBytes));
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

		private static void BuildProxyAggregates(List<Cilbox.CilboxProxy> proxies, Dictionary<Cilbox.Cilbox, ProxyAggregate> aggregates)
		{
			aggregates.Clear();
			if (proxies == null) return;

			for (int i = 0; i < proxies.Count; i++)
			{
				Cilbox.CilboxProxy proxy = proxies[i];
				if (proxy == null || proxy.box == null) continue;

				aggregates.TryGetValue(proxy.box, out ProxyAggregate aggregate);
				aggregate.Count++;
				aggregate.EstimatedMemoryBytes += EstimateProxyBytes(proxy);
				aggregates[proxy.box] = aggregate;
			}
		}

		private static long EstimateMemoryBytes(Cilbox.Cilbox box)
		{
			// ponytail: known managed buffers only; Unity/profiler owns real memory accounting.
			return box == null ? 0 : EstimateStringBytes(box.assemblyData) + EstimateStringBytes(box.disabledReason);
		}

		private static long EstimateProxyBytes(Cilbox.CilboxProxy proxy)
		{
			if (proxy == null) return 0;
			long bytes = EstimateStringBytes(proxy.className)
				+ EstimateStringBytes(proxy.serializedObjectData)
				+ EstimateStringBytes(proxy.buildTimeGuid)
				+ EstimateStringBytes(proxy.initialLoadPath);
			if (proxy.fields != null) bytes += proxy.fields.LongLength * StackElementBytes;
			if (proxy.fieldsObjects != null) bytes += proxy.fieldsObjects.Count * ReferenceBytes;
			return bytes;
		}

		private static long EstimateStringBytes(string value)
		{
			return string.IsNullOrEmpty(value) ? 0 : value.Length * StringCharBytes;
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
