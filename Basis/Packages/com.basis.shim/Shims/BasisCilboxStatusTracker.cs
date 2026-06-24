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
		public Cilbox.Cilbox Box;
		public BasisCilboxStatusType Type;
		public string Name;
		public bool IsActiveAndEnabled;
		public bool IsDisabledByCilbox;
		public string DisabledReason;
		public string FailureReason;
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

		private struct TrackedCilbox
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

		public static bool WatchdogEnabled { get; private set; }

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetRuntimeState()
		{
			Tracked.Clear();
			ProxySnapshot.Clear();
			ProxyAggregates.Clear();
			WatchdogEnabled = false;
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
				TrackedCilbox tracked = Tracked[index];
				tracked.Type = type;
				Tracked[index] = tracked;
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

			int index = IndexOf(box);
			if (index >= 0) Tracked.RemoveAt(index);
		}

		public static void FillSnapshots(List<BasisCilboxStatusSnapshot> snapshots)
		{
			if (snapshots == null) return;
			snapshots.Clear();
			CleanupDestroyed();
			if (Tracked.Count == 0) return;

			EnsureCapacity(snapshots, Tracked.Count);
			Cilbox.CilboxProxy.CopyTrackedProxies(ProxySnapshot);
			BuildProxyAggregates(ProxySnapshot, ProxyAggregates);

			for (int i = 0; i < Tracked.Count; i++)
			{
				TrackedCilbox tracked = Tracked[i];
				Cilbox.Cilbox box = tracked.Box;
				if (box == null) continue;

				ProxyAggregates.TryGetValue(box, out ProxyAggregate proxyAggregate);
				long memoryBytes = EstimateMemoryBytes(box) + proxyAggregate.EstimatedMemoryBytes;
				long cpuUsedUs = Math.Max(0, box.usSpentLastFrame);
				long cpuBudgetUs = Math.Max(0, box.timeoutLengthUs);
				bool disabled = box.disabled;
				snapshots.Add(new BasisCilboxStatusSnapshot
				{
					Box = box,
					Type = tracked.Type,
					Name = box.gameObject != null ? box.gameObject.name : box.name,
					IsActiveAndEnabled = box.isActiveAndEnabled,
					IsDisabledByCilbox = disabled,
					DisabledReason = box.disabledReason,
					FailureReason = GetFailureReason(disabled, box.disabledReason, IsCpuBudgetExceeded(cpuUsedUs, cpuBudgetUs)),
					CpuUsedUs = cpuUsedUs,
					CpuBudgetUs = cpuBudgetUs,
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

			PanelElementDescriptor failedField = PanelElementDescriptor.CreateNew(
				PanelElementDescriptor.ElementStyles.Group,
				parent);
			failedField.SetTitle("Failed Cilboxes");
			failedField.SetDescription("No failed Cilboxes.");

			PanelToggle watchdogToggle = PanelToggle.CreateNewEntry(failedField.ContentParent);
			if (watchdogToggle != null)
			{
				watchdogToggle.Descriptor.SetTitle("Cilbox Watchdog");
				watchdogToggle.Descriptor.SetDescription("Off by default.");
				watchdogToggle.Descriptor.SetTooltip("When enabled, failed Cilboxes are soft-restarted after 5 seconds. This clears the disabled flag and CPU accounting only; cold proxy reload is not implemented yet.");
				watchdogToggle.SetValueWithoutNotify(WatchdogEnabled);
				watchdogToggle.OnValueChanged += SetWatchdogEnabled;
			}

			statusField.gameObject.AddComponent<BasisCilboxStatusPanelUpdater>().Initialize(statusField, failedField);
		}

		private static void SetWatchdogEnabled(bool value)
		{
			WatchdogEnabled = value;
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

			builder.Append("Total CPU: ");
			AppendCpu(builder, totalCpuUsedUs, totalCpuBudgetUs).AppendLine();
			builder.Append("Known Memory: ~");
			AppendBytes(builder, totalMemoryBytes);
			builder.Append(" | Cilboxes: ").Append(snapshots.Count);
			builder.Append(" | Proxies: ").Append(totalProxyCount).AppendLine();

			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				builder.AppendLine();
				builder.Append(snapshot.Type).Append(" • ").Append(string.IsNullOrEmpty(snapshot.Name) ? "(unnamed)" : snapshot.Name).AppendLine();
				builder.Append("CPU: ");
				AppendCpu(builder, snapshot.CpuUsedUs, snapshot.CpuBudgetUs);
				builder.Append(" | Known Memory: ~");
				AppendBytes(builder, snapshot.EstimatedMemoryBytes);
				builder.Append(" | Proxies: ").Append(snapshot.ProxyCount);

				if (!snapshot.IsActiveAndEnabled)
				{
					builder.Append(" | Inactive");
				}

				if (IsFailed(snapshot))
				{
					builder.Append(" | Failed");
					if (!string.IsNullOrEmpty(snapshot.FailureReason))
					{
						builder.Append(": ").Append(snapshot.FailureReason);
					}
				}

				builder.AppendLine();
			}

			return builder.ToString();
		}

		public static bool Restart(Cilbox.Cilbox box)
		{
			if (box == null) return false;

			// ponytail: soft restart only. Cold restart needs CilboxProxy to retain
			// serializedObjectData and replay proxy Awake/Start; add that later if needed.
			box.disabled = false;
			box.disabledReason = string.Empty;
			box.interpreterAccountingDepth = 0;
			box.interpreterAccountingDropDead = 0;
			box.interpreterAccountingCumulitiveTicks = 0;
			box.interpreterInstructionsCount = 0;
			box.usSpentLastFrame = 0;

			try
			{
				box.BoxInitialize();
				return true;
			}
			catch (Exception ex)
			{
				box.disabled = true;
				box.disabledReason = "Restart failed: " + ex.Message;
				return false;
			}
		}

		public static bool IsFailed(BasisCilboxStatusSnapshot snapshot)
		{
			return snapshot.IsDisabledByCilbox || IsCpuBudgetExceeded(snapshot.CpuUsedUs, snapshot.CpuBudgetUs);
		}

		private static bool IsCpuBudgetExceeded(long cpuUsedUs, long cpuBudgetUs)
		{
			return cpuBudgetUs > 0 && cpuUsedUs >= cpuBudgetUs;
		}

		private static string GetFailureReason(bool disabled, string disabledReason, bool cpuBudgetExceeded)
		{
			if (disabled) return string.IsNullOrEmpty(disabledReason) ? "Disabled" : TrimReason(disabledReason);
			return cpuBudgetExceeded ? "CPU budget exceeded" : string.Empty;
		}

		private static int IndexOf(Cilbox.Cilbox box)
		{
			return Tracked.FindIndex(tracked => ReferenceEquals(tracked.Box, box));
		}

		private static void EnsureCapacity<T>(List<T> list, int count)
		{
			if (list != null && list.Capacity < count) list.Capacity = count;
		}

		private static void CleanupDestroyed()
		{
			Tracked.RemoveAll(tracked => tracked.Box == null);
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

		private static StringBuilder AppendCpu(StringBuilder target, long usedUs, long budgetUs)
		{
			AppendMicroseconds(target, usedUs);
			if (budgetUs <= 0) return target.Append(" / N/A");

			double percentage = usedUs * 100.0 / budgetUs;
			AppendMicroseconds(target.Append(" / "), budgetUs);
			return target.Append(" (").Append(percentage.ToString("0.0")).Append("%)");
		}

		private static StringBuilder AppendMicroseconds(StringBuilder target, long microseconds)
		{
			if (microseconds >= 1000)
			{
				return target.Append((microseconds / 1000.0).ToString("0.###")).Append(" ms");
			}

			return target.Append(microseconds).Append(" us");
		}

		private static StringBuilder AppendBytes(StringBuilder target, long bytes)
		{
			const double kib = 1024.0;
			const double mib = kib * 1024.0;
			const double gib = mib * 1024.0;

			if (bytes >= (long)gib) return target.Append((bytes / gib).ToString("0.##")).Append(" GiB");
			if (bytes >= (long)mib) return target.Append((bytes / mib).ToString("0.##")).Append(" MiB");
			if (bytes >= (long)kib) return target.Append((bytes / kib).ToString("0.##")).Append(" KiB");
			return target.Append(bytes).Append(" B");
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
		private const float WatchdogRestartDelaySeconds = 5f;

		private readonly List<BasisCilboxStatusSnapshot> snapshots = new List<BasisCilboxStatusSnapshot>();
		private readonly List<RestartButtonSlot> restartButtonSlots = new List<RestartButtonSlot>();
		private readonly Dictionary<Cilbox.Cilbox, float> failedSince = new Dictionary<Cilbox.Cilbox, float>();
		private readonly StringBuilder builder = new StringBuilder(1024);

		private PanelElementDescriptor statusField;
		private PanelElementDescriptor failedField;
		private float updateTimer;
		private string lastDescription;
		private int lastFailedCount = -1;
		private bool richTextDisabled;

		private sealed class RestartButtonSlot
		{
			private readonly BasisCilboxStatusPanelUpdater owner;
			private PanelButton button;
			private Cilbox.Cilbox box;
			private BasisCilboxStatusType type;
			private string name;
			private string reason;

			public RestartButtonSlot(BasisCilboxStatusPanelUpdater owner, PanelButton button)
			{
				this.owner = owner;
				this.button = button;
				if (button != null) button.OnClicked += Restart;
			}

			public void Bind(BasisCilboxStatusSnapshot snapshot)
			{
				if (button == null) return;
				if (!button.gameObject.activeSelf) button.gameObject.SetActive(true);
				box = snapshot.Box;

				if (type != snapshot.Type || !string.Equals(name, snapshot.Name, StringComparison.Ordinal))
				{
					type = snapshot.Type;
					name = snapshot.Name;
					button.Descriptor.SetTitle("Restart " + type + ": " + (string.IsNullOrEmpty(name) ? "(unnamed)" : name));
				}

				if (!string.Equals(reason, snapshot.FailureReason, StringComparison.Ordinal))
				{
					reason = snapshot.FailureReason;
					button.Descriptor.SetDescription(reason);
				}
			}

			public void Hide()
			{
				box = null;
				if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
			}

			public void Release()
			{
				box = null;
				if (button != null && !button.IsReleased) button.ReleaseInstance();
				button = null;
			}

			private void Restart()
			{
				if (box == null) return;
				BasisCilboxStatusTracker.Restart(box);
				owner.ForceRefreshSoon();
			}
		}

		public void Initialize(PanelElementDescriptor field, PanelElementDescriptor failed)
		{
			statusField = field;
			failedField = failed;
			Refresh();
		}

		private void ForceRefreshSoon()
		{
			updateTimer = UpdateInterval;
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
			RefreshRestartButtons();
			WatchdogRestartFailures();

			string description = BasisCilboxStatusTracker.BuildStatusText(snapshots, builder);
			if (!string.Equals(description, lastDescription, StringComparison.Ordinal))
			{
				statusField.SetDescription(description);
				lastDescription = description;
			}
		}

		private void RefreshRestartButtons()
		{
			if (failedField == null) return;

			int failedCount = 0;
			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				if (BasisCilboxStatusTracker.IsFailed(snapshot) && snapshot.Box != null) failedCount++;
			}

			if (failedCount != lastFailedCount)
			{
				lastFailedCount = failedCount;
				failedField.SetDescription(failedCount == 0 ? "No failed Cilboxes." : failedCount + " failed Cilbox instance(s).");
			}

			EnsureRestartButtonCapacity(failedCount);
			int slotIndex = 0;
			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				if (!BasisCilboxStatusTracker.IsFailed(snapshot) || snapshot.Box == null) continue;
				if (slotIndex >= restartButtonSlots.Count) break;
				restartButtonSlots[slotIndex++].Bind(snapshot);
			}

			for (; slotIndex < restartButtonSlots.Count; slotIndex++)
			{
				restartButtonSlots[slotIndex].Hide();
			}
		}

		private void EnsureRestartButtonCapacity(int count)
		{
			if (restartButtonSlots.Capacity < count) restartButtonSlots.Capacity = count;
			while (restartButtonSlots.Count < count)
			{
				PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, failedField.ContentParent);
				if (button == null) return;
				restartButtonSlots.Add(new RestartButtonSlot(this, button));
			}
		}

		private void WatchdogRestartFailures()
		{
			if (!BasisCilboxStatusTracker.WatchdogEnabled)
			{
				failedSince.Clear();
				return;
			}

			float now = Time.unscaledTime;
			for (int i = 0; i < snapshots.Count; i++)
			{
				BasisCilboxStatusSnapshot snapshot = snapshots[i];
				if (snapshot.Box == null) continue;

				if (!BasisCilboxStatusTracker.IsFailed(snapshot))
				{
					failedSince.Remove(snapshot.Box);
					continue;
				}

				if (!failedSince.TryGetValue(snapshot.Box, out float since))
				{
					failedSince[snapshot.Box] = now;
					continue;
				}

				if (now - since < WatchdogRestartDelaySeconds) continue;

				// ponytail: one watchdog retry loop; add backoff/max attempts if this spams logs.
				BasisCilboxStatusTracker.Restart(snapshot.Box);
				failedSince[snapshot.Box] = now;
			}
		}

		private void ReleaseRestartButtonCache()
		{
			int count = restartButtonSlots.Count;
			for (int i = 0; i < count; i++)
			{
				restartButtonSlots[i].Release();
			}
			restartButtonSlots.Clear();
		}

		private void OnDestroy()
		{
			ReleaseRestartButtonCache();
		}
	}
}
