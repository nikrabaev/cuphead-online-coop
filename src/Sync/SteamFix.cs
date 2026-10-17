using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Cuphead's SteamManager (internal) is created lazily as a plain scene object and never marked
    /// DontDestroyOnLoad, so the first scene change destroys it (SteamAPI.Shutdown) and the next access throws
    /// "Tried to Initialize the SteamAPI twice in one session!" (observed in Player.log). After that Steam P2P and
    /// DLC checks fail. Keeping the first instance alive fixes both.
    /// </summary>
    [HarmonyPatch]
    internal static class SteamManagerPersistence
    {
        private static FieldInfo instanceField;

        /// <summary>Skip the patch on game builds without Steam (GOG, Microsoft Store) instead of failing PatchAll.</summary>
        private static bool Prepare()
        {
            return AccessTools.TypeByName("SteamManager") != null;
        }

        private static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("SteamManager");
            instanceField = AccessTools.Field(t, "s_instance");
            return AccessTools.Method(t, "Awake");
        }

        private static void Postfix(MonoBehaviour __instance)
        {
            if (__instance == null || instanceField == null) return;
            if (!ReferenceEquals(instanceField.GetValue(null), __instance)) return;
            if (__instance.transform.parent != null) __instance.transform.SetParent(null, false);
            UnityEngine.Object.DontDestroyOnLoad(__instance.gameObject);
        }
    }
}
