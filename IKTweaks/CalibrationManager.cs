using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Il2CppSystem.Text;
using MelonLoader;
using UnhollowerRuntimeLib.XrefScans;
using UnityEngine;
using Valve.VR;
using VRC.Core;
using Object = UnityEngine.Object;

namespace IKTweaks
{
    public static class CalibrationManager
    {
        private static readonly Dictionary<string, Dictionary<CalibrationPoint, CalibrationData>> SavedAvatars = new Dictionary<string, Dictionary<CalibrationPoint, CalibrationData>>();

        private static readonly Dictionary<CalibrationPoint, CalibrationData> UniversalData = new Dictionary<CalibrationPoint, CalibrationData>();

        public static bool HasSavedCalibration(string avatarId) => SavedAvatars.ContainsKey(avatarId);
        public static void Clear()
        {
            SavedAvatars.Clear();
            UniversalData.Clear();
        }

        public static void ClearNonUniversal()
        {
            SavedAvatars.Clear();
        }

        public static void Clear(string avatarId)
        {
            SavedAvatars.Remove(avatarId);
            UniversalData.Clear();
        }

        public static void Save(string avatarId, CalibrationPoint point, CalibrationData data)
        {
            if (!SavedAvatars.ContainsKey(avatarId))
                SavedAvatars[avatarId] = new Dictionary<CalibrationPoint, CalibrationData>();

            SavedAvatars[avatarId][point] = data;
        }

        public struct CalibrationData
        {
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public string TrackerSerial;

            public CalibrationData(Vector3 localPosition, Quaternion localRotation, string trackerSerial)
            {
                LocalPosition = localPosition;
                LocalRotation = localRotation;
                TrackerSerial = trackerSerial;
            }
        }

        public enum CalibrationPoint
        {
            Head,
            LeftHand,
            RightHand,
            Hip,
            LeftFoot,
            RightFoot,
            LeftKnee,
            RightKnee,
            LeftElbow,
            RightElbow,
            Chest
        }

        internal static SteamVR_ControllerManager GetControllerManager()
        {
            foreach (var vrcTracking in VRCTrackingManager.field_Private_Static_VRCTrackingManager_0
                .field_Private_List_1_VRCTracking_0)
            {
                var vrcTrackingSteam = vrcTracking.TryCast<VRCTrackingSteam>();
                if (vrcTrackingSteam == null) continue;

                return vrcTrackingSteam.field_Private_SteamVR_ControllerManager_0;
            }

            throw new ApplicationException("SteamVR tracking not found");
        }

        public static void Calibrate(GameObject avatarRoot)
        {
            CalibrateCore(avatarRoot).ContinueWith(t =>
            {
                if (t.Exception != null) 
                    MelonLogger.LogError($"Task failed with exception: {t.Exception}");
            });
        }

        private static readonly float[] TPoseMuscles = {
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.6001086f, 8.6213E-05f, -0.0003308152f,
            0.9999163f, -9.559652E-06f, 3.41413E-08f, -3.415095E-06f, -1.024528E-07f, 0.6001086f, 8.602679E-05f, -0.0003311098f,
            0.9999163f, -9.510122E-06f, 1.707468E-07f, -2.732077E-06f, 2.035554E-15f, -2.748694E-07f, 2.619475E-07f, 0.401967f,
            0.3005583f, 0.04102772f, 0.9998822f, -0.04634236f, 0.002522987f, 0.0003842837f, -2.369134E-07f, -2.232262E-07f,
            0.4019674f, 0.3005582f, 0.04103433f, 0.9998825f, -0.04634996f, 0.00252335f, 0.000383302f, -1.52127f, 0.2634507f,
            0.4322457f, 0.6443988f, 0.6669409f, -0.4663372f, 0.8116828f, 0.8116829f, 0.6678119f, -0.6186608f, 0.8116842f,
            0.8116842f, 0.6677991f, -0.619225f, 0.8116842f, 0.811684f, 0.6670032f, -0.465875f, 0.811684f, 0.8116836f, -1.520098f,
            0.2613016f, 0.432256f, 0.6444503f, 0.6668426f, -0.4670413f, 0.8116828f, 0.8116828f, 0.6677986f, -0.6192409f,
            0.8116841f, 0.811684f, 0.6677839f, -0.6198869f, 0.8116839f, 0.8116838f, 0.6668782f, -0.4667901f, 0.8116842f, 0.811684f
        };
        private static string? GetTrackerSerial(int trackerId)
        {
            var sb = new StringBuilder();
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            OpenVR.System.GetStringTrackedDeviceProperty((uint) trackerId, ETrackedDeviceProperty.Prop_SerialNumber_String, sb, OpenVR.k_unMaxPropertyStringSize, ref err);
            if (err == ETrackedPropertyError.TrackedProp_Success)
                return sb.ToString();
            
            MelonLogger.LogWarning($"Can't get serial for tracker ID {trackerId}");
            return null;
        }
        
        private static Transform? FindTracker(string serial, SteamVR_ControllerManager? steamVrControllerManager)
        {
            steamVrControllerManager ??= GetControllerManager();

            return steamVrControllerManager.objects
                .Where(it => it != steamVrControllerManager.left && it != steamVrControllerManager.right && it != null)
                .First(it => GetTrackerSerial((int) it.GetComponent<SteamVR_TrackedObject>().index) == serial)
                .transform;
        }

        private static GameObject[] ourTargets = new GameObject[0];
        private static async Task ApplyStoredCalibration(GameObject avatarRoot, string avatarId)
        {
            // await IKTweaksMod.AwaitLateUpdate();

            var gameObject = avatarRoot;
            FullBodyHandling.PreSetupVrIk(gameObject);
            var vrik = FullBodyHandling.SetupVrIk(FullBodyHandling.LastInitializedController, gameObject);

            foreach (var target in ourTargets)
                Object.DestroyImmediate(target);


            var steamVrControllerManager = GetControllerManager();
            var newTargets = new List<GameObject>();

            var datas = SavedAvatars[avatarId];

            Transform? GetTarget(CalibrationPoint point)
            {
                if (!datas.TryGetValue(point, out var data))
                    return null;

                var bestTracker = FindTracker(data.TrackerSerial, steamVrControllerManager);

                if (bestTracker == null)
                {
                    MelonLogger.Log($"Null target for tracker {data.TrackerSerial}");
                    return null;
                }

                MelonLogger.Log($"Found tracker with serial {data.TrackerSerial} for point {point}");

                var result = bestTracker;

                var newTarget = new GameObject("CustomIkTarget-For-" + data.TrackerSerial + "-" + point);
                newTargets.Add(newTarget);
                var targetTransform = newTarget.transform;
                targetTransform.SetParent(result);
                targetTransform.localPosition = data.LocalPosition;
                targetTransform.localRotation = data.LocalRotation;

                return targetTransform;
            }

            Transform MakeHandTarget(Quaternion localRotation, Transform parent)
            {
                var targetGo = new GameObject("CustomIkHandTarget");
                var targetTransform = targetGo.transform;
                targetTransform.SetParent(parent, false);
                targetTransform.localRotation = localRotation;

                newTargets.Add(targetGo);
                return targetTransform;
            }

            var hips = GetTarget(CalibrationPoint.Hip);
            var leftFoot = GetTarget(CalibrationPoint.LeftFoot);
            var rightFoot = GetTarget(CalibrationPoint.RightFoot);

            vrik.solver.leftArm.target = MakeHandTarget(datas[CalibrationPoint.LeftHand].LocalRotation,
                FullBodyHandling.LastInitializedController.field_Private_FullBodyBipedIK_0.solver.leftHandEffector.target);
            vrik.solver.rightArm.target = MakeHandTarget(datas[CalibrationPoint.RightHand].LocalRotation,
                FullBodyHandling.LastInitializedController.field_Private_FullBodyBipedIK_0.solver.rightHandEffector.target);

            vrik.solver.leftLeg.bendGoal = GetTarget(CalibrationPoint.LeftKnee);
            vrik.solver.rightLeg.bendGoal = GetTarget(CalibrationPoint.RightKnee);

            vrik.solver.leftArm.bendGoal = GetTarget(CalibrationPoint.LeftElbow);
            vrik.solver.rightArm.bendGoal = GetTarget(CalibrationPoint.RightElbow);

            vrik.solver.spine.chestGoal = GetTarget(CalibrationPoint.Chest);

            ourTargets = newTargets.ToArray();

            vrik.solver.spine.pelvisTarget = hips;
            vrik.solver.leftLeg.target = leftFoot;
            vrik.solver.rightLeg.target = rightFoot;

            MelonLogger.Log("Applied stored calibration");
        }

        private static Action<VRCTrackingSteam, bool>? ourSetVisibilityDelegate;
        private static void SetTrackerVisibility(bool visible)
        {
            if (ourSetVisibilityDelegate == null)
            {
                var method = typeof(VRCTrackingSteam)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single(it =>
                        it.ReturnType == typeof(void) && it.GetParameters().Length == 1 &&
                        it.GetParameters()[0].ParameterType == typeof(bool) && XrefScanner.XrefScan(it)
                            .Any(jt => jt.Type == XrefType.Global && jt.ReadAsObject()?.ToString() == "Model"));

                ourSetVisibilityDelegate = (Action<VRCTrackingSteam, bool>) Delegate.CreateDelegate(typeof(Action<VRCTrackingSteam, bool>), method);
            }
            
            foreach (var vrcTracking in VRCTrackingManager.field_Private_Static_VRCTrackingManager_0.field_Private_List_1_VRCTracking_0)
            {
                var vrcTrackingSteam = vrcTracking.TryCast<VRCTrackingSteam>();
                if (vrcTrackingSteam == null) continue;

                ourSetVisibilityDelegate(vrcTrackingSteam, visible);
                return;
            }
        }

        private static void MoveTrackersToStoredPositions()
        {
            var ikController = FullBodyHandling.LastInitializedController;
            var headTracker = ikController.field_Private_FBBIKHeadEffector_0.transform.parent;
            var currentHeadForwardProjected = Vector3.ProjectOnPlane(headTracker.forward, Vector3.up);
            var steamVrControllerManager = GetControllerManager();
            var trackersParent = steamVrControllerManager.objects[3].transform.parent;
            
            var headData = UniversalData[CalibrationPoint.Head];

            headTracker.position = trackersParent.TransformPoint(headData.LocalPosition);
            headTracker.rotation = headData.LocalRotation * trackersParent.rotation;

            var newHeadForwardProjected = Vector3.ProjectOnPlane(headTracker.forward, Vector3.up);

            var rotation = Quaternion.FromToRotation(newHeadForwardProjected, currentHeadForwardProjected);
            rotation.ToAngleAxis(out var angle, out var axis);

            var headTrackerPosition = headTracker.position;
            headTracker.RotateAround(headTrackerPosition, axis, angle);
            
            void DoConvert(CalibrationPoint point, ref float weightOut)
            {
                weightOut = 0f;
                
                if (!UniversalData.TryGetValue(point, out var data)) return;

                var tracker = FindTracker(data.TrackerSerial, steamVrControllerManager);
                if (tracker == null) return;
                
                tracker.localPosition = data.LocalPosition;
                tracker.localRotation = data.LocalRotation;
                    
                tracker.RotateAround(headTrackerPosition, axis, angle);

                weightOut = 1;
            }

            float _ = 0f;
            DoConvert(CalibrationPoint.Hip, ref _);
            DoConvert(CalibrationPoint.LeftFoot, ref _);
            DoConvert(CalibrationPoint.RightFoot, ref _);
            DoConvert(CalibrationPoint.LeftElbow, ref FullBodyHandling.LeftElbowWeight);
            DoConvert(CalibrationPoint.RightElbow, ref FullBodyHandling.RightElbowWeight);
            DoConvert(CalibrationPoint.LeftKnee, ref FullBodyHandling.LeftKneeWeight);
            DoConvert(CalibrationPoint.RightKnee, ref FullBodyHandling.RightKneeWeight);
            DoConvert(CalibrationPoint.Chest, ref FullBodyHandling.ChestWeight);
        }

        private static async Task CalibrateCore(GameObject avatarRoot)
        {
            var avatarId = avatarRoot.GetComponent<PipelineManager>().blueprintId;
            if (IkTweaksSettings.CalibrateStorePerAvatar && HasSavedCalibration(avatarId))
            {
                await ApplyStoredCalibration(avatarRoot, avatarId);
                return;
            }

            await ManualCalibrateCoro(avatarRoot);
            
            await IKTweaksMod.AwaitVeryLateUpdate();
            
            await ApplyStoredCalibration(avatarRoot, avatarId);
        }

        private static Vector3 GetLocalPosition(Transform parent, Transform child)
        {
            return parent.InverseTransformPoint(child.position);
        }

        private static Quaternion GetLocalRotation(Transform parent, Transform child)
        {
            return Quaternion.Inverse(parent.rotation) * child.rotation;
        }

        private static int spammy = 0;
        private static async Task ManualCalibrateCoro(GameObject avatarRoot)
        {
            var animator = avatarRoot.GetComponent<Animator>();
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            var avatarId = avatarRoot.GetComponent<PipelineManager>().blueprintId;
            var avatarRootTransform = avatarRoot.transform;

            for (var i = 0; i < 30; i++)
                await IKTweaksMod.AwaitVeryLateUpdate();

            if (!avatarRoot) 
                return;

            var playerApi = FullBodyHandling.LastInitializedController.field_Private_VRCPlayer_0.prop_VRCPlayerApi_0;
            playerApi.PushAnimations(BundleHolder.TPoseController);

            var headTarget = FullBodyHandling.LastInitializedController.field_Private_FBBIKHeadEffector_0.transform;
            
            SetTrackerVisibility(true);

            var oldHipPos = hips.position;

            var mirrorCloneRoot = avatarRootTransform.parent.Find("_AvatarMirrorClone");
            Transform mirrorHips = null;
            if (mirrorCloneRoot != null)
            {
                var mirrorCloneAnimator = mirrorCloneRoot.GetComponent<Animator>();
                if (mirrorCloneAnimator != null) mirrorHips = mirrorCloneAnimator.GetBoneTransform(HumanBodyBones.Hips);
            }
            
            var willUniversallyCalibrate = false;

            while (avatarRoot)
            {
                await IKTweaksMod.AwaitVeryLateUpdate();

                var trigger1 = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger");
                var trigger2 = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger");
                
                if (IkTweaksSettings.CalibrateUseUniversal && UniversalData.Count >= 4)
                {
                    MoveTrackersToStoredPositions();
                    willUniversallyCalibrate = true;
                }

                if (IkTweaksSettings.CalibrateHalfFreeze && trigger1 + trigger2 > 0.75f)
                {
                    hips.position = oldHipPos;
                    if (mirrorHips != null) mirrorHips.position = oldHipPos;
                }
                else if(IkTweaksSettings.CalibrateFollowHead)
                {
                    var delta = headTarget.position - head.position;
                    hips.position += delta;
                    if (mirrorHips != null) mirrorHips.position = hips.position;
                    oldHipPos = hips.position;
                }

                if (trigger1 + trigger2 > 1.75f || willUniversallyCalibrate)
                {
                    break;
                }
            }
            
            SetTrackerVisibility(false);

            if (avatarRoot == null)
                return;

            var steamVrControllerManager = GetControllerManager();
            var possibleTrackers = new List<Transform>(steamVrControllerManager.objects
                .Where(it => it != steamVrControllerManager.left && it != steamVrControllerManager.right && it != null)
                .Select(it => it.transform));

            (Transform Tracker, Transform Bone)? GetTracker(HumanBodyBones bone, HumanBodyBones fallback = HumanBodyBones.Hips)
            {
                var boneTransform = animator.GetBoneTransform(bone) ?? animator.GetBoneTransform(fallback);
                var bonePosition = boneTransform.position;
                var bestTracker = -1;
                var bestDistance = float.PositiveInfinity;
                for (var index = 0; index < possibleTrackers.Count; index++)
                {
                    var possibleTracker = possibleTrackers[index];
                    var steamVRTrackedObject = possibleTracker.GetComponent<SteamVR_TrackedObject>();
                    if (steamVRTrackedObject.index ==
                        SteamVR_TrackedObject.EnumNPublicSealedvaNoHmDe18DeDeDeDeDeUnique.None)
                        continue;

                    var distance = Vector3.Distance(possibleTracker.position, bonePosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestTracker = index;
                    }
                }

                if (bestTracker == -1)
                {
                    MelonLogger.Log($"Null target for bone {bone}");
                    return null;
                }

                var result = possibleTrackers[bestTracker]!;
                possibleTrackers.RemoveAt(bestTracker);

                return (result, boneTransform);
            }

            void StoreData(CalibrationPoint point, (Transform tracker, Transform bone)? pair)
            {
                if (pair == null) return;
                var tracker = pair.Value.tracker;
                var bone = pair.Value.bone;

                var serial = GetTrackerSerial((int) tracker.GetComponent<SteamVR_TrackedObject>().index);

                var trackerRelativeData = new CalibrationData(GetLocalPosition(tracker, bone),
                    GetLocalRotation(tracker, bone), serial);

                Save(avatarId, point, trackerRelativeData);

                if (!willUniversallyCalibrate)
                {
                    var avatarSpaceData = new CalibrationData(GetLocalPosition(tracker.parent, tracker),
                        GetLocalRotation(tracker.parent, tracker), serial);
                    UniversalData[point] = avatarSpaceData;
                }
            }

            void StoreHand(Vector3 angles, HumanBodyBones handBone, CalibrationPoint point)
            {
                var handRotation = animator.GetBoneTransform(handBone).rotation;
                var bodyRotation = animator.transform.rotation;

                var storedData = new CalibrationData(Vector3.zero,
                    Quaternion.Euler(angles) * Quaternion.Inverse(bodyRotation) * handRotation, point.ToString());

                Save(avatarId, point, storedData);
            }

            void StoreBendGoal(CalibrationPoint point, (Transform tracker, Transform bone)? pair, Vector3 offset)
            {
                if (pair == null) return;

                var tracker = pair.Value.tracker;
                var bone = pair.Value.bone;

                var serial = GetTrackerSerial((int) tracker.GetComponent<SteamVR_TrackedObject>().index);

                var trackerRelativeData = new CalibrationData(tracker.InverseTransformPoint(bone.position + offset),
                    Quaternion.identity, serial);

                Save(avatarId, point, trackerRelativeData);

                if (!willUniversallyCalibrate)
                {
                    var avatarSpaceData = new CalibrationData(GetLocalPosition(tracker.parent, tracker),
                        GetLocalRotation(tracker.parent, tracker), serial);
                    UniversalData[point] = avatarSpaceData;
                }
            }

            var hipsTracker = GetTracker(HumanBodyBones.Hips);
            var leftFootTracker =
                GetTracker(IkTweaksSettings.MapToes ? HumanBodyBones.LeftToes : HumanBodyBones.LeftFoot,
                    HumanBodyBones.LeftFoot);
            var rightFootTracker =
                GetTracker(IkTweaksSettings.MapToes ? HumanBodyBones.RightToes : HumanBodyBones.RightFoot,
                    HumanBodyBones.RightFoot);

            StoreData(CalibrationPoint.Hip, hipsTracker);
            StoreData(CalibrationPoint.LeftFoot, leftFootTracker);
            StoreData(CalibrationPoint.RightFoot, rightFootTracker);

            var leftLowerLegPosition = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position;
            var rightLowerLegPosition = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg).position;
            var avatarForward = Vector3.Cross(rightLowerLegPosition - leftLowerLegPosition, Vector3.up).normalized;

            if (IkTweaksSettings.UseElbowTrackers)
            {
                var leftElbowTracker = GetTracker(HumanBodyBones.LeftLowerArm);
                var rightElbowTracker = GetTracker(HumanBodyBones.RightLowerArm);

                StoreBendGoal(CalibrationPoint.LeftElbow, leftElbowTracker, avatarForward * -0.1f);
                StoreBendGoal(CalibrationPoint.RightElbow, rightElbowTracker, avatarForward * -0.1f);
            }

            if (IkTweaksSettings.UseKneeTrackers)
            {
                var leftKneeTracker = GetTracker(HumanBodyBones.LeftLowerLeg);
                var rightKneeTracker = GetTracker(HumanBodyBones.RightLowerLeg);

                StoreBendGoal(CalibrationPoint.LeftKnee, leftKneeTracker, avatarForward * 0.1f);
                StoreBendGoal(CalibrationPoint.RightKnee, rightKneeTracker, avatarForward * 0.1f);
            }

            if (IkTweaksSettings.UseChestTracker)
            {
                var chestTracker = GetTracker(HumanBodyBones.UpperChest, HumanBodyBones.Chest);

                StoreBendGoal(CalibrationPoint.Chest, chestTracker, avatarForward * .5f);
            }

            StoreHand(new Vector3(15, 90 + 10, 0), HumanBodyBones.LeftHand, CalibrationPoint.LeftHand);
            StoreHand(new Vector3(15, -90 - 10, 0), HumanBodyBones.RightHand, CalibrationPoint.RightHand);

            if (!willUniversallyCalibrate)
            {
                UniversalData[CalibrationPoint.Head] = new CalibrationData(
                    GetLocalPosition(hipsTracker.Value.Tracker.parent, headTarget.parent),
                    GetLocalRotation(hipsTracker.Value.Tracker.parent, headTarget.parent), "HEAD");
            }

            playerApi.PopAnimations();
        }
    }
}