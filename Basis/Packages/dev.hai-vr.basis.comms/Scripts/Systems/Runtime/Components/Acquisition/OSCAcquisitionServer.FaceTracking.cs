using System;
using System.Collections.Generic;
using System.Linq;
using HVR.Osushi;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public partial class OSCAcquisitionServer
    {
        private const string RelativeFaceTrackingPrefix = "FT/v2/";
        [NoAutoStaticsCleanup] private static readonly HashSet<string> NativeFaceTrackingPaths =
            new HashSet<string>(OsushiUtil.FaceTrackingParameterNames.Select(name => FaceTrackingParameterPrefix + name), StringComparer.Ordinal);

        private readonly Dictionary<EntityId, HashSet<string>> _faceTrackingSnapshots = new();
        private readonly Dictionary<string, int> _faceTrackingCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ownedFaceTrackingNodes = new(StringComparer.Ordinal);

        internal static bool IsFaceTrackingAddress(string address)
        {
            return address != null && address.StartsWith(RelativeFaceTrackingPrefix, StringComparison.Ordinal) && address.Length > RelativeFaceTrackingPrefix.Length;
        }

        internal void UpdateFaceTrackingParameters(EntityId ownerId, IEnumerable<string> addresses)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            if (addresses != null)
            {
                foreach (string address in addresses)
                {
                    if (IsFaceTrackingAddress(address))
                    {
                        next.Add("/avatar/parameters/" + address);
                    }
                }
            }

            lock (_queryLock)
            {
                _faceTrackingSnapshots.TryGetValue(ownerId, out var previous);
                if (previous == null ? next.Count == 0 : previous.SetEquals(next)) return;

                EnsureOscQueryRoot();
                UpdateSubscriptionCounts(previous, next, _faceTrackingCounts, EnsureFaceTrackingNode, RemoveFaceTrackingNode);
                if (next.Count == 0) _faceTrackingSnapshots.Remove(ownerId);
                else _faceTrackingSnapshots[ownerId] = next;
                RefreshFaceTrackingMode();
            }
        }

        internal void ClearFaceTrackingParameters(EntityId ownerId)
        {
            lock (_queryLock)
            {
                if (!_faceTrackingSnapshots.TryGetValue(ownerId, out var previous)) return;

                UpdateSubscriptionCounts(previous, null, _faceTrackingCounts, EnsureFaceTrackingNode, RemoveFaceTrackingNode);
                _faceTrackingSnapshots.Remove(ownerId);
                RefreshFaceTrackingMode();
            }
        }

        private void EnsureFaceTrackingNode(string fullPath)
        {
            OsushiNode node = EnsureQueryNode(fullPath);
            if (node.TYPE != null || node.VALUE != null) return;

            node.ACCESS = SubscriptionAccess;
            node.TYPE = "f";
            node.VALUE = new List<object> { 0f };
            _ownedFaceTrackingNodes.Add(fullPath);
        }

        private void RemoveFaceTrackingNode(string fullPath)
        {
            // Static parameters and separately published values retain their own metadata.
            if (!_ownedFaceTrackingNodes.Remove(fullPath)) return;
            OsushiNode node = ResolveExactQueryNode(fullPath);
            if (node == null) return;

            node.TYPE = null;
            node.VALUE = null;
            node.ACCESS = _exactSubscriptionCounts.ContainsKey(fullPath) ? SubscriptionAccess : 0;
            if (!_prefixSubscriptionCounts.ContainsKey(fullPath)) PruneQueryNode(fullPath);
        }

        private void RefreshFaceTrackingMode()
        {
            bool useNative = NativeFaceData;
            foreach (string path in _faceTrackingCounts.Keys)
            {
                if (!NativeFaceTrackingPaths.Contains(path))
                {
                    useNative = false;
                    break;
                }
            }

            // Native VRCFT disables individual parameters. Unsupported inputs must keep VRCFT's own float calculations.
            if (_useNativeFaceData != useNative)
            {
                _useNativeFaceData = useNative;
                _lastNativeFaceTime = float.NegativeInfinity;
                _nativeFace?.Reset();
            }
            OsushiNode nativeNode = _oscQueryRoot.CONTENTS["avatar"].CONTENTS["avatarfullfacetracking"];
            nativeNode.TYPE = useNative ? "T" : "F";
            nativeNode.VALUE = new List<object> { useNative };
            if (_lastWakeUp != null) _wakeUpPending = true;
        }
    }
}
