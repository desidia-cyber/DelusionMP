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
        private static int _coopSlot;
        private static bool _logged;

        /// <summary>Ensure OriginalPath has been captured even before the game first queries persistentDataPath.</summary>
        internal static void EnsureInitialized()
        {
            if (string.IsNullOrEmpty(OriginalPath))
            {
                try { _ = UnityEngine.Application.persistentDataPath; } catch { }
            }
        }

        internal static string GetCoopPath()
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(OriginalPath)) return "";
            int slot = Bootstrap.Slot > 0 ? Bootstrap.Slot : 1;
            if (string.IsNullOrEmpty(_coopPath) || _coopSlot != slot)
            {
                var parent = Path.GetDirectoryName(OriginalPath);
                _coopPath = Path.Combine(parent ?? OriginalPath, "Dread Delusion MP" + slot);
                _coopSlot = slot;
                _logged = false;
            }
            return _coopPath;
        }

        internal static void EnsureCoopDirectoryExists()
        {
            var path = GetCoopPath();
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private static void Postfix(ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(OriginalPath) && !string.IsNullOrEmpty(__result))
                    OriginalPath = __result;

                bool redirect = Bootstrap.Slot > 0 || Core.CoopManager.CoopSaveActive;
                if (redirect && !string.IsNullOrEmpty(OriginalPath))
                {
                    int slot = Bootstrap.Slot > 0 ? Bootstrap.Slot : 1;
                    if (string.IsNullOrEmpty(_coopPath) || _coopSlot != slot)
                    {
                        var parent = Path.GetDirectoryName(OriginalPath);
                        _coopPath = Path.Combine(parent ?? OriginalPath, "Dread Delusion MP" + slot);
                        _coopSlot = slot;
                        _logged = false;
                    }
                    // Unity guarantees the default persistentDataPath exists on disk, so the game's
                    // SaveFile() opens files directly without creating the folder first. Make sure
                    // the isolated co-op folder exists before returning it.
                    if (!Directory.Exists(_coopPath))
                        Directory.CreateDirectory(_coopPath);
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

    /// <summary>Disable the built-in tilde/backquote (~ / ` / ё) debug console in co-op so
    /// accidental presses or Russian keyboard typing never pop up the dev debug menu.</summary>
    [HarmonyPatch(typeof(global::DelusionInput), nameof(global::DelusionInput.OnDebugPublic))]
    internal static class Patch_Disable_DebugPublic
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(global::DelusionManager), nameof(global::DelusionManager.DebugConsole))]
    internal static class Patch_Disable_DebugConsole
    {
        private static bool Prefix() => false;
    }
}
