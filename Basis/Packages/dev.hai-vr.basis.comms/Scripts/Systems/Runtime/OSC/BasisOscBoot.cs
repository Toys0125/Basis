using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using UnityEngine;

namespace HVR.Basis.Comms
{
    internal static class BasisOscBoot
    {
        private static bool _subscribedToLocalAvatarChanges;
        private static bool _subscribedToOscSetting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            _subscribedToLocalAvatarChanges = false;
            _subscribedToOscSetting = false;
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
            if (!_subscribedToOscSetting)
            {
                BasisSettingsDefaults.EnableOSC.OnChanged -= OnEnableOscChanged;
                BasisSettingsDefaults.EnableOSC.OnChanged += OnEnableOscChanged;
                _subscribedToOscSetting = true;
            }

            if (!_subscribedToLocalAvatarChanges)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= EnsureLocalAvatarOscAcquisition;
                BasisLocalPlayer.OnLocalAvatarChanged += EnsureLocalAvatarOscAcquisition;
                _subscribedToLocalAvatarChanges = true;
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

            acquisition.OnAvatarReady(true);
        }
    }
}
