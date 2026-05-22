using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public static class BasisOscBoot
    {
        public static bool SubscribedToLocalAvatarChanges;
        public static bool SubscribedToOscSetting;
        public static OSCAcquisition LocalAvatarOscAcquisition;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            SubscribedToLocalAvatarChanges = false;
            SubscribedToOscSetting = false;
            LocalAvatarOscAcquisition = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterBootHooks()
        {
            BasisDeviceManagement.OnSettingsLoaded -= OnSettingsLoaded;
            BasisDeviceManagement.OnSettingsLoaded += OnSettingsLoaded;

            if (BasisDeviceManagement.SettingsLoaded)
            {
                OnSettingsLoaded();
            }
        }

        private static void OnSettingsLoaded()
        {
            if (!SubscribedToOscSetting)
            {
                BasisSettingsDefaults.EnableOSC.OnChanged -= OnEnableOscChanged;
                BasisSettingsDefaults.EnableOSC.OnChanged += OnEnableOscChanged;
                SubscribedToOscSetting = true;
            }

            if (!SubscribedToLocalAvatarChanges)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= EnsureLocalAvatarOscAcquisition;
                BasisLocalPlayer.OnLocalAvatarChanged += EnsureLocalAvatarOscAcquisition;
                SubscribedToLocalAvatarChanges = true;
            }

            if (!BasisSettingsDefaults.EnableOSC.RawValue)
            {
                return;
            }

            BasisOscService.EnsureInitialized();
            EnsureLocalAvatarOscAcquisition();
        }

        private static void OnEnableOscChanged(bool enabled)
        {
            if (enabled)
            {
                BasisOscService.EnsureInitialized();
                EnsureLocalAvatarOscAcquisition();
            }
        }

        private static void EnsureLocalAvatarOscAcquisition()
        {
            if (!BasisSettingsDefaults.EnableOSC.RawValue)
            {
                return;
            }

            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer == null || localPlayer.BasisAvatar == null)
            {
                return;
            }

            if (LocalAvatarOscAcquisition != null &&
                LocalAvatarOscAcquisition.transform.IsChildOf(localPlayer.BasisAvatar.transform))
            {
                LocalAvatarOscAcquisition.OnAvatarReady(true);
                return;
            }

            OSCAcquisition acquisition = localPlayer.BasisAvatar.GetComponentInChildren<OSCAcquisition>(true);
            if (acquisition == null)
            {
                GameObject acquisitionGo = new GameObject(nameof(OSCAcquisition))
                {
                    transform =
                    {
                        parent = localPlayer.BasisAvatar.transform,
                    }
                };
                acquisition = acquisitionGo.AddComponent<OSCAcquisition>();
            }

            LocalAvatarOscAcquisition = acquisition;
            acquisition.OnAvatarReady(true);
        }
    }
}
