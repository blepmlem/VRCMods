using System.Linq;
using System.Reflection;
using Harmony;
using MelonLoader;
using RootMotion.FinalIK;
using UnhollowerBaseLib.Attributes;
using UnhollowerBaseLib.Maps;
using UnhollowerRuntimeLib.XrefScans;
using VRC.Core;

namespace IKTweaks
{
    public static class VrIkHandling
    {
        internal static VRIK LastInitializedIk;

        internal static void Update()
        {
            if (LastInitializedIk != null) 
                ApplyVrIkSettings(LastInitializedIk);
        }

        public static void HookVrIkInit(HarmonyInstance harmony)
        {
            var vrikInitMethod = typeof(VRCVrIkController).GetMethod(nameof(VRCVrIkController
                .Method_Public_Virtual_Final_New_Boolean_VRC_AnimationController_Animator_VRCPlayer_Boolean_0));
            harmony.Patch(vrikInitMethod,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(VrIkHandling), nameof(VrikInitPatch))));

            var methodThatChecksHipTracking = typeof(VRCVrIkController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single(it =>
                    XrefScanner.XrefScan(it).Any(jt =>
                        jt.Type == XrefType.Global && "Hip Tracking: Hip tracker found. tracking enabled." ==
                        jt.ReadAsObject()?.ToString()));
            
            var canSupportHipTrackingCandidates = XrefScanner.XrefScan(methodThatChecksHipTracking).Where(it =>
            {
                if (it.Type != XrefType.Method) return false;
                var resolved = it.TryResolve();
                if (resolved == null || !resolved.IsStatic) return false;
                if(!(resolved.DeclaringType == typeof(VRCTrackingManager) && resolved is MethodInfo mi && mi.ReturnType == typeof(bool) && resolved.GetParameters().Length == 0)) return false;
                return XrefScanner.UsedBy(resolved).Any(jt =>
                    jt.Type == XrefType.Method && jt.TryResolve().DeclaringType == typeof(QuickMenu));
            }).ToList();

            var canSupportHipTracking = canSupportHipTrackingCandidates.Single().TryResolve();

            harmony.Patch(canSupportHipTracking, new HarmonyMethod(AccessTools.Method(typeof(VrIkHandling), nameof(SupportsHipTrackingPatch))));
        }

        private static bool SupportsHipTrackingPatch(ref bool __result)
        {
            if (IkTweaksSettings.DisableFbt)
            {
                __result = false;
                return false;
            }

            return true;
        }

        private static void VrikInitPatch(VRCVrIkController __instance, VRCPlayer? __2)
        {
            if (__2 != null && __2.prop_Player_0?.prop_APIUser_0?.id == APIUser.CurrentUser?.id)
            {
                FullBodyHandling.LastCalibrationWasInCustomIk = false;
                LastInitializedIk = __instance.field_Private_VRIK_0;
            }
        }

        private static void ApplyVrIkSettings(VRIK ik)
        {
            var ikSolverVr = ik.solver;
            var shoulderMode = IkTweaksSettings.ShoulderMode;
            ikSolverVr.leftArm.shoulderRotationMode = shoulderMode;
            ikSolverVr.rightArm.shoulderRotationMode = shoulderMode;
            ikSolverVr.plantFeet = IkTweaksSettings.PlantFeet;
        }
    }
}