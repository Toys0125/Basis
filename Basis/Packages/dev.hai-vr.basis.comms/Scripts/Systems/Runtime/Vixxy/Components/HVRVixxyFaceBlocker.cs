using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using HVR.Basis.Comms;
using UnityEngine;

namespace HVR.Vixxy
{
    public enum HVRVixxyFaceBlockTarget
    {
        Blinking,
        Visemes
    }

    /// <summary>
    /// Ref-counted authored-content blocker for Basis' native blink/viseme drivers. Multiple generated
    /// controls may overlap without one control turning the native driver back on while another still blocks it.
    /// </summary>
    public sealed class HVRVixxyFaceBlocker : MonoBehaviour, IHVRInitializable
    {
        [SerializeField] public HVRVixxyFaceBlockTarget target;
        [SerializeField] private bool active;

        private BasisAvatar _avatar;
        private bool _registered;

        private sealed class Counts
        {
            public int Blinking;
            public int Visemes;
        }

        private static readonly Dictionary<BasisAvatar, Counts> CountsByAvatar = new();

        public bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                active = value;
                SyncRegistration();
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _avatar = HVRCommsUtil.GetAvatar(this);
            SyncRegistration();
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            if (_avatar != null && CountsByAvatar.TryGetValue(_avatar, out var counts)) Apply(_avatar, counts);
        }

        private void OnDestroy()
        {
            if (!_registered || _avatar == null) return;
            ChangeCount(_avatar, -1);
            _registered = false;
        }

        private void SyncRegistration()
        {
            if (_avatar == null || active == _registered) return;
            ChangeCount(_avatar, active ? 1 : -1);
            _registered = active;
        }

        private void ChangeCount(BasisAvatar avatar, int delta)
        {
            if (!CountsByAvatar.TryGetValue(avatar, out var counts))
            {
                counts = new Counts();
                CountsByAvatar[avatar] = counts;
            }

            if (target == HVRVixxyFaceBlockTarget.Blinking) counts.Blinking = Mathf.Max(0, counts.Blinking + delta);
            else counts.Visemes = Mathf.Max(0, counts.Visemes + delta);

            Apply(avatar, counts);
            if (counts.Blinking == 0 && counts.Visemes == 0) CountsByAvatar.Remove(avatar);
        }

        private static void Apply(BasisAvatar avatar, Counts counts)
        {
            if (avatar == null) return;
            if (avatar.IsOwnedLocally)
            {
                var local = BasisLocalPlayer.Instance;
                if (local == null || local.BasisAvatar != avatar) return;
                local.FacialBlinkDriver?.SetExternalOverride(counts.Blinking > 0);
                if (local.LocalVisemeDriver != null) local.LocalVisemeDriver.ExternalBlockVisemes = counts.Visemes > 0;
                return;
            }

            if (!BasisNetworkPlayers.AvatarToPlayer(avatar, out var player) || player is not BasisRemotePlayer remote) return;
            remote.RemoteFaceDriver?.SetExternalBlinkOverride(counts.Blinking > 0);
            if (remote.RemoteFaceDriver != null) remote.RemoteFaceDriver.ExternalOverrideViseme = counts.Visemes > 0;
        }
    }
}
