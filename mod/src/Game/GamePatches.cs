using System;
using System.IO;
using HarmonyLib;

namespace DelusionMP.Game
{
    /// <summary>
    /// Per-process save isolation.
    ///
    /// Two game copies on one PC share Unity's persistentDataPath
    /// (%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;), so both would fight over
    /// the same save index. Changing app.info does not move it - the path is baked into the
    /// player data. Postfixing the property is the smallest lever that redirects every
    /// save/login/PlayerPrefs write for this process only: the game then reads and writes
    /// &lt;...&gt;\Dread Delusion MP&lt;slot&gt;.
    ///
    /// A real player runs without -ddmp-slot and is unaffected.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Application), "get_persistentDataPath")]
    internal static class Patch_PersistentDataPath
    {
        internal static string OriginalPath = "";
        private static string _coopPath = "";
        private static bool _logged;

        private static void Postfix(ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(OriginalPath) && !string.IsNullOrEmpty(__result))
                {
                    OriginalPath = __result;
                    var parent = Path.GetDirectoryName(__result);
                    int slot = Bootstrap.Slot > 0 ? Bootstrap.Slot : 1;
                    _coopPath = Path.Combine(parent ?? __result, "Dread Delusion MP" + slot);
                }

                bool redirect = Bootstrap.Slot > 0 || Core.CoopManager.CoopSaveActive;
                if (redirect && !string.IsNullOrEmpty(_coopPath))
                {
                    if (!_logged)
                    {
                        _logged = true;
                        Log.Info("persistentDataPath -> " + _coopPath);
                    }
                    __result = _coopPath;
                }
            }
            catch (Exception e) { Log.Debug("persistentDataPath patch: " + e.Message); }
        }
    }
}

namespace DelusionMP.Game
{
    /// <summary>Logs every game save (when the game saves on its own matters for co-op:
    /// each player's personal progress lives in their own save).</summary>
    [HarmonyPatch(typeof(global::DelusionSave), nameof(global::DelusionSave.Save))]
    internal static class Patch_DelusionSave_SaveLog
    {
        private static void Postfix()
        {
            string gs = "-";
            try { gs = GameRefs.Manager != null ? GameRefs.Manager.CurrentGameState.ToString() : "-"; } catch { }
            Log.Info($"game saved (state={gs})");
        }
    }
}
