using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using HVR.IK.FullTiger;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
//using UnityEngine.Animations.Rigging;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local rig driver that wires up Unity Animation Rigging constraints for a player avatar,
    /// filters tracker noise (One Euro Filter), and manually evaluates the rig graph each frame.
    /// Sets up spine, head, hands, feet, and toes, and toggles layers based on available rigs.
    /// </summary>
    [Serializable]
    public class BasisLocalRigDriver
    {
        public HIKFullTiger HIKFullTiger;
        public HIKEffectors HIKEffectors;
        /// <summary>
        /// Minimum cutoff for the One Euro filter. Lower = smoother; higher = more responsive.
        /// </summary>
        [Header("Smoothing (One Euro Filter)")]
        [Tooltip("Lower = more smoothing; Higher = more responsive.")]
        [Range(0.01f, 10f)]
        public float MinCutoff = 5.5f;

        /// <summary>
        /// Beta term for the One Euro filter: raises cutoff during fast motion to reduce lag.
        /// </summary>
        [Tooltip("How much to raise cutoff when motion is fast (reduces lag during quick moves).")]
        [Range(0f, 10f)]
        public float Beta = 3.25f;

        /// <summary>
        /// Cutoff for derivative smoothing in the One Euro filter.
        /// </summary>
        [Tooltip("Cutoff for derivative smoothing.")]
        [Range(0.01f, 10f)]
        public float DerivativeCutoff = 3f;

        /// <summary>Owning local player instance.</summary>
        private BasisLocalPlayer localPlayer;
        /// <summary>Bone reference mapping (hips, chest, hands, etc.).</summary>
        private BasisTransformMapping references;

        // === Per-role smoothers ===

        /// <summary>Position filters per tracked role (One Euro).</summary>
        private readonly Dictionary<BasisBoneTrackedRole, OneEuroFilterVector3> posFilters = new();
        /// <summary>Rotation filters per tracked role (One Euro).</summary>
        private readonly Dictionary<BasisBoneTrackedRole, OneEuroFilterQuaternion> rotFilters = new();

        /// <summary>Monotonic time accumulator for filter evaluation.</summary>
        private float _timeAccumulator;

        /// <summary>
        /// Fetches or creates a One Euro position filter for a specific role
        /// and keeps its parameters in sync with the public fields.
        /// </summary>
        private OneEuroFilterVector3 GetPosFilter(BasisBoneTrackedRole role)
        {
            if (!posFilters.TryGetValue(role, out var f))
            {
                f = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);
                posFilters[role] = f;
            }
            else
            {
                // keep runtime params in sync if adjusted at runtime
                f.minCutoff = MinCutoff; f.beta = Beta; f.dCutoff = DerivativeCutoff;
            }
            return f;
        }

        /// <summary>
        /// Initializes the rig driver with a local player and bone references.
        /// </summary>
        /// <param name="localPlayer">Local player providing animator and scale context.</param>
        /// <param name="references">Captured bone references for rig construction.</param>
        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            this.references = references;
            _timeAccumulator = 0f;
        }

        /// <summary>
        /// Updates IK targets and hints, applies One Euro filtering (hooks left in place but commented),
        /// and manually evaluates the rig playable graph for the given delta time.
        /// </summary>
        /// <param name="DeltaTime">Simulation delta time.</param>
        public void SimulateIKDestinations(float DeltaTime)
        {
            _timeAccumulator += Mathf.Max(DeltaTime, 1e-6f);

            // --- IK Target ---
            // Spine (hips + head targets come from calibrated coords)
            var hipsCoords = BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

            var hipsPos = GetPosFilter(BasisBoneTrackedRole.Hips).Filter(hipsCoords.position, _timeAccumulator);
            // var hipsRot = GetRotFilter(BasisBoneTrackedRole.Hips).Filter(hipsCoords.rotation, _timeAccumulator);

            //  ApplySpineIKTarget(
            //    new BasisCalibratedCoords
            //    {
            //       position = hipsPos,
            //       rotation = hipsCoords.rotation
            //   }
            //   );

            // Direction for knee/neck hints relative to hips orientation (unchanged)
            Vector3 Direction = BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation * Vector3.right;
        }

        /// <summary>
        /// Maps a tracked role to its outgoing world-space calibrated coordinates from the local bone driver.
        /// </summary>
        private BasisCalibratedCoords GetCoordsForRole(BasisBoneTrackedRole role)
        {
            // Map roles to driver controls
            switch (role)
            {
                case BasisBoneTrackedRole.Head: return BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
                case BasisBoneTrackedRole.Hips: return BasisLocalBoneDriver.HipsControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftHand: return BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightHand: return BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerArm: return BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerArm: return BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftFoot: return BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightFoot: return BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftLowerLeg: return BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightLowerLeg: return BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;

                case BasisBoneTrackedRole.LeftToes: return BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;
                case BasisBoneTrackedRole.RightToes: return BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;

                case BasisBoneTrackedRole.Chest: return BasisLocalBoneDriver.ChestControl.OutgoingWorldData;

                default:
                    // Fallback: return identity to avoid null ref
                    return new BasisCalibratedCoords { position = Vector3.zero, rotation = Quaternion.identity };
            }
        }

        /// <summary>
        /// Builds the rig's playable graph from the animator and switches the graph to manual update mode.
        /// </summary>
        public void BuildBuilder()
        {
        }

        /// <summary>
        /// Overload convenience: toggles layers based on current TPose state.
        /// </summary>
        public void OnTPose()
        {
            OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);
        }
        /// <summary>
        /// Enables/disables rig layers during TPose and notifies bone controls when exiting TPose.
        /// </summary>
        /// <param name="currentlyTposing">Whether the avatar is currently in TPose.</param>
        public void OnTPose(bool currentlyTposing)
        {

            if (currentlyTposing == false)
            {
                foreach (BasisLocalBoneControl control in BasisLocalPlayer.Instance.LocalBoneDriver.Controls)
                {
                    control.OnHasRigChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Cleans up created rig GameObjects (head/spine/hands/feet/toes/shoulders) before rebuilding.
        /// </summary>
        public void CleanupBeforeContinue()
        {
        }

        /// <summary>
        /// Sets up core body rigs (spine, head, hands, feet, toes) and ensures a <see cref="RigTransform"/> exists on hips.
        /// </summary>
        public void SetBodySettings(BasisLocalBoneDriver driver)
        {
            SetupSpine(driver);
            SetupHeadRig(driver);
            LeftHand(driver);
            RightHand(driver);
            LeftFoot(driver);
            RightFoot(driver);

            LeftToe(driver);
            RightToe(driver);
         //   if (references.Hips.gameObject.TryGetComponent<RigTransform>(out RigTransform RigTransform) == false)
            {
            //    RigTransform Hips = references.Hips.gameObject.AddComponent<RigTransform>();
            }
            BasisLocalBoneControl.HasEvents = true;
        }

        /// <summary>
        /// Creates head/neck/chest rig and two-bone IK based on available references.
        /// Registers rig-layer events for the relevant bone controls.
        /// </summary>
        private void SetupHeadRig(BasisLocalBoneDriver driver)
        {
           // GameObject GameobjectHeadRig = CreateOrGetRig("Chest, Neck, Head", true, out HeadRig, out HeadLayer);
            if (references.HasUpperchest)
            {
            //    BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, references.Upperchest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK);
            }
            else
            {
                if (references.Haschest)
                {
                //    BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, references.chest, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK);

                }
                else
                {
                //    BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, GameobjectHeadRig, null, references.neck, references.head, BasisBoneTrackedRole.Head, BasisBoneTrackedRole.Chest, true, out HeadTwoBoneIK);

                }
            }
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
            {
                controls.Add(Head);
            }
            if (driver.FindBone(out BasisLocalBoneControl Chest, BasisBoneTrackedRole.Chest))
            {
                controls.Add(Chest);
            }
          //  WriteUpEvents(controls, HeadLayer);
        }

        /// <summary>
        /// Creates the spine rig and hips/head IK, wiring events for head/hips controls.
        /// </summary>
        private void SetupSpine(BasisLocalBoneDriver driver)
        {
          //  var spineRig = CreateOrGetRig("Rig Spine", true, out SpineRig, out RigSpineLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl Hip, BasisBoneTrackedRole.Hips))
            {
                controls.Add(Hip);
            }
            if (driver.FindBone(out BasisLocalBoneControl Head, BasisBoneTrackedRole.Head))
            {
                controls.Add(Head);
            }
          //  WriteUpEvents(controls, RigSpineLayer);
          //  BasisAnimationRiggingHelper.CreateSpine(localPlayer, spineRig, references.Hips, references.head, BasisBoneTrackedRole.Hips, out SpineIK);
        }

        /// <summary>
        /// Creates right-shoulder damping rig and registers layer toggling events.
        /// </summary>
        private void SetupRightShoulderRig(BasisLocalBoneDriver driver)
        {
         //   GameObject RightShoulder = CreateOrGetRig("RightShoulder", false, out RightShoulderRig, out RightShoulderLayer);
         //   BasisAnimationRiggingHelper.Damp(localPlayer, RightShoulder, references.RightShoulder, BasisBoneTrackedRole.RightShoulder);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl RightShoulderRole, BasisBoneTrackedRole.RightShoulder))
            {
                controls.Add(RightShoulderRole);
            }
          //  WriteUpEvents(controls, RightShoulderLayer);
        }

        /// <summary>
        /// Creates left-shoulder damping rig and registers layer toggling events.
        /// </summary>
        private void SetupLeftShoulderRig(BasisLocalBoneDriver driver)
        {
           // GameObject LeftShoulder = CreateOrGetRig("LeftShoulder", false, out LeftShoulderRig, out LeftShoulderLayer);
          //  BasisAnimationRiggingHelper.Damp(localPlayer, LeftShoulder, references.leftShoulder, BasisBoneTrackedRole.LeftShoulder);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl LeftShoulderRole, BasisBoneTrackedRole.LeftShoulder))
            {
                controls.Add(LeftShoulderRole);
            }
          //  WriteUpEvents(controls, LeftShoulderLayer);
        }

        /// <summary>
        /// Sets up left hand two-bone IK and layer events for hand/lower arm controls.
        /// </summary>
        public void LeftHand(BasisLocalBoneDriver driver)
        {
          //  GameObject Hands = CreateOrGetRig("LeftUpperArm, LeftLowerArm, LeftHand", false, out LeftHandRig, out LeftHandLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl LeftHand, BasisBoneTrackedRole.LeftHand))
            {
                controls.Add(LeftHand);
            }
            if (driver.FindBone(out BasisLocalBoneControl LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm))
            {
                controls.Add(LeftLowerArm);
            }
          //  WriteUpEvents(controls, LeftHandLayer);
          //  BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.leftUpperArm, references.leftLowerArm, references.leftHand, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.LeftLowerArm, true, out LeftHandTwoBoneIK);
        }

        /// <summary>
        /// Sets up right hand two-bone IK and layer events for hand/lower arm controls.
        /// </summary>
        public void RightHand(BasisLocalBoneDriver driver)
        {
          //  GameObject Hands = CreateOrGetRig("RightUpperArm, RightLowerArm, RightHand", false, out RightHandRig, out RightHandLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl RightHand, BasisBoneTrackedRole.RightHand))
            {
                controls.Add(RightHand);
            }
            if (driver.FindBone(out BasisLocalBoneControl RightLowerArm, BasisBoneTrackedRole.RightLowerArm))
            {
                controls.Add(RightLowerArm);
            }
         //   WriteUpEvents(controls, RightHandLayer);
          //  BasisAnimationRiggingHelper.CreateTwoBoneHand(localPlayer, Hands, references.Hips, references.chest, references.RightUpperArm, references.RightLowerArm, references.rightHand, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.RightLowerArm, true, out RightHandTwoBoneIK);
        }

        /// <summary>
        /// Sets up left foot two-bone IK and layer events for foot/lower leg controls.
        /// </summary>
        public void LeftFoot(BasisLocalBoneDriver driver)
        {
       //     GameObject feet = CreateOrGetRig("LeftUpperLeg, LeftLowerLeg, LeftFoot", false, out LeftFootRig, out LeftFootLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl LeftFoot, BasisBoneTrackedRole.LeftFoot))
            {
                controls.Add(LeftFoot);
            }
            if (driver.FindBone(out BasisLocalBoneControl LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg))
            {
                controls.Add(LeftLowerLeg);
            }

         //   WriteUpEvents(controls, LeftFootLayer);

            //BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, feet, references.LeftUpperLeg, references.LeftLowerLeg, references.leftFoot, BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.LeftLowerLeg, true, out LeftFootTwoBoneIK);
        }

        /// <summary>
        /// Sets up right foot two-bone IK and layer events for foot/lower leg controls.
        /// </summary>
        public void RightFoot(BasisLocalBoneDriver driver)
        {
           // GameObject feet = CreateOrGetRig("RightUpperLeg, RightLowerLeg, RightFoot", false, out RightFootRig, out RightFootLayer);
            List<BasisLocalBoneControl> controls = new List<BasisLocalBoneControl>();
            if (driver.FindBone(out BasisLocalBoneControl RightFoot, BasisBoneTrackedRole.RightFoot))
            {
                controls.Add(RightFoot);
            }
            if (driver.FindBone(out BasisLocalBoneControl RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg))
            {
                controls.Add(RightLowerLeg);
            }

          //  WriteUpEvents(controls, RightFootLayer);

          //  BasisAnimationRiggingHelper.CreateTwoBone(localPlayer, feet, references.RightUpperLeg, references.RightLowerLeg, references.rightFoot, BasisBoneTrackedRole.RightFoot, BasisBoneTrackedRole.RightLowerLeg, true, out RightFootTwoBoneIK);
        }

        /// <summary>
        /// Sets up left toe damping rig and registers layer toggling for the left toe control.
        /// </summary>
        public void LeftToe(BasisLocalBoneDriver driver)
        {
          //  GameObject LeftToe = CreateOrGetRig("LeftToe", false, out LeftToeRig, out LeftToeLayer);
            if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.LeftToes))
            {
           //     WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, LeftToeLayer);
            }
           // LeftToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, LeftToe, references.leftToes, BasisBoneTrackedRole.LeftToes);
        }

        /// <summary>
        /// Sets up right toe damping rig and registers layer toggling for the right toe control.
        /// </summary>
        public void RightToe(BasisLocalBoneDriver driver)
        {
         //   GameObject RightToe = CreateOrGetRig("RightToe", false, out RightToeRig, out RightToeLayer);
         //   if (driver.FindBone(out BasisLocalBoneControl Control, BasisBoneTrackedRole.RightToes))
          //  {
          //      WriteUpEvents(new List<BasisLocalBoneControl>() { Control }, RightToeLayer);
         //   }
          //  RightToeConstraint = BasisAnimationRiggingHelper.Damp(localPlayer, RightToe, references.rightToes, BasisBoneTrackedRole.RightToes);
        }

        /// <summary>
        /// Sets hint weights based on connected input devices and clears all hints first.
        /// </summary>
        public void CalibrateRoles()
        {
            foreach (BasisBoneTrackedRole Role in Enum.GetValues(typeof(BasisBoneTrackedRole)))
            {
                ApplyHint(Role, false);
            }
            for (int Index = 0; Index < BasisDeviceManagement.Instance.AllInputDevices.Count; Index++)
            {
                Device_Management.Devices.BasisInput BasisInput = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (BasisInput.TryGetRole(out BasisBoneTrackedRole Role))
                {
                    ApplyHint(Role, true);
                }
            }
        }

        /// <summary>
        /// Applies a hint weight to the appropriate constraint given a tracked role.
        /// </summary>
        /// <param name="RoleWithHint">The role whose hint should be toggled.</param>
        /// <param name="weight">True to enable the hint; false to disable.</param>
        public void ApplyHint(BasisBoneTrackedRole RoleWithHint, bool weight)
        {
            try
            {
                switch (RoleWithHint)
                {
                    case BasisBoneTrackedRole.Chest:
                      //  HeadTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerLeg:
                      //  RightFootTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.LeftLowerLeg:
                      //  LeftFootTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightUpperArm:
                       // RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.LeftUpperArm:
                       // LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    case BasisBoneTrackedRole.LeftLowerArm:
                       // RightHandTwoBoneIK.data.hintWeight = weight;
                        break;

                    case BasisBoneTrackedRole.RightLowerArm:
                       // LeftHandTwoBoneIK.data.hintWeight = weight;
                        break;
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.Log($"{e.Message} {e.StackTrace}");
            }
        }

        /// <summary>
        /// Wires change events from controls to a rig layer so the layer auto-activates
        /// when any control reports an active rig layer.
        /// </summary>
        public void WriteUpEvents(List<BasisLocalBoneControl> Controls)
        {
            foreach (var control in Controls)
            {
                control.OnHasRigChanged += delegate { UpdateLayerActiveState(Controls); };
            }
            UpdateLayerActiveState(Controls);
        }

        /// <summary>
        /// Updates a layer's active flag based on whether any control reports an active rig layer.
        /// </summary>
        void UpdateLayerActiveState(List<BasisLocalBoneControl> Controls)
        {
            bool IsActive = Controls.Any(control => control.HasRigLayer == BasisHasRigLayer.HasRigLayer);
        }

    }
}
