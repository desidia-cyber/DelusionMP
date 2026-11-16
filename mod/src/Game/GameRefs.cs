using System;
using HarmonyLib;

namespace DelusionMP.Game
{
    /// <summary>
    /// Access to private members the interop wrappers do not expose.
    ///
    /// Verified against the shipping build's metadata: several fields the mod needs are
    /// private or have private setters, so a direct C# access will not compile and a raw
    /// pointer write would be fragile. Harmony's Traverse resolves by name at runtime and
    /// survives a metadata regeneration.
    /// </summary>
    public static class Priv
    {
        public static T GetField<T>(object obj, string field)
        {
            try { return Traverse.Create(obj).Field(field).GetValue<T>(); }
            catch (Exception e) { Log.Debug($"Priv.GetField {field} failed: {e.Message}"); return default; }
        }

        public static void SetField(object obj, string field, object value)
        {
            try { Traverse.Create(obj).Field(field).SetValue(value); }
            catch (Exception e) { Log.Debug($"Priv.SetField {field} failed: {e.Message}"); }
        }

        public static T GetProp<T>(object obj, string prop)
        {
            try { return Traverse.Create(obj).Property(prop).GetValue<T>(); }
            catch (Exception e) { Log.Debug($"Priv.GetProp {prop} failed: {e.Message}"); return default; }
        }

        public static void SetProp(object obj, string prop, object value)
        {
            try { Traverse.Create(obj).Property(prop).SetValue(value); }
            catch (Exception e) { Log.Debug($"Priv.SetProp {prop} failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// Typed access to the game's singletons.
    ///
    /// Dread Delusion routes everything through a small MonoSingleton&lt;T&gt; layer
    /// (Assembly-CSharp, TypeDefIndex 3433), which exposes `public static T Instance`.
    /// Each accessor is defensive: during the title screen, scene switches and shutdown
    /// these are legitimately null, and a NullReferenceException on the main thread
    /// crashes the game.
    /// </summary>
    public static class GameRefs
    {
        private static T Safe<T>(Func<T> get, string name) where T : class
        {
            try { return get(); }
            catch (Exception e)
            {
                Log.Debug($"GameRefs.{name} unavailable: {e.GetType().Name}");
                return null;
            }
        }

        public static global::DelusionSave Save =>
            Safe(() => global::DelusionSave.Instance, nameof(Save));

        public static global::FactDB Facts =>
            Safe(() => global::DelusionSave.Instance?.Facts, nameof(Facts));

        public static global::DelusionManager Manager =>
            Safe(() => global::DelusionManager.Instance, nameof(Manager));

        public static global::DelusionQuest Quest =>
            Safe(() => global::DelusionQuest.Instance, nameof(Quest));

        public static global::DelusionTasks Tasks =>
            Safe(() => global::DelusionTasks.Instance, nameof(Tasks));

        public static global::DelusionSceneControl Scene =>
            Safe(() => global::DelusionSceneControl.Instance, nameof(Scene));

        public static global::RegionSceneManager Regions =>
            Safe(() => global::RegionSceneManager.Instance, nameof(Regions));

        public static global::DelusionDayNight DayNight =>
            Safe(() => global::DelusionDayNight.Instance, nameof(DayNight));

        public static global::DelusionPlayerActions PlayerActions =>
            Safe(() => global::DelusionPlayerActions.Instance, nameof(PlayerActions));

        public static global::DelusionInventory Inventory =>
            Safe(() => global::DelusionInventory.Instance, nameof(Inventory));

        public static global::DelusionCinematics Cinematics =>
            Safe(() => global::DelusionCinematics.Instance, nameof(Cinematics));

        /// <summary>
        /// The canonical CharacterStats. DelusionStats holds it at field 0x28; PlayerData
        /// (the per-save payload) is internal and is not a component, so it cannot be
        /// fetched with GetComponent.
        /// </summary>
        public static global::CharacterStats Stats =>
            Safe(() => global::DelusionStats.Instance?.characterStats, nameof(Stats));

        /// <summary>DelusionManager.player is private; GetPlayer is the public accessor.</summary>
        public static UnityEngine.GameObject Player =>
            Safe(() => global::DelusionManager.Instance?.GetPlayer(false), nameof(Player));

        /// <summary>DelusionManager.playerCamera is private.</summary>
        public static UnityEngine.Camera PlayerCamera =>
            Safe(() => global::DelusionManager.Instance?.playerCamera,
                 nameof(PlayerCamera));

        /// <summary>
        /// True once a save is loaded and the world is simulating.
        ///
        /// The menu also reports RUNNING/CINEMATIC through DelusionManager and its scene
        /// still holds stray SaveableObjects; running sync against that state is how a
        /// session dies before it starts. Require a live player as well.
        /// </summary>
        public static bool InWorld
        {
            get
            {
                try
                {
                    var m = Manager;
                    if (m == null) return false;
                    var s = m.CurrentGameState;
                    if (s != global::DelusionManager.GameState.RUNNING
                        && s != global::DelusionManager.GameState.CINEMATIC
                        && s != global::DelusionManager.GameState.CINEMATIC_FULLSPEED
                        && s != global::DelusionManager.GameState.PLAYER_DISABLED
                        && s != global::DelusionManager.GameState.FORCED_LOOK)
                        return false;
                    return Player != null;
                }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// Field offsets from the shipping build's metadata dump (work/dump/dump.cs).
    ///
    /// These are NOT stable across game updates and must be re-derived after a patch.
    /// They are recorded here for the few places that need a raw read, and as
    /// documentation of the layouts the sync design depends on.
    /// </summary>
    public static class Offsets
    {
        // FactDB (TypeDefIndex 2868)
        public const int FactsBoolDict = 0x10;
        public const int FactsIntDict = 0x18;

        // DelusionSave (3581)
        public const int SaveFacts = 0x50;
        public const int SaveLoadedState = 0x48;

        // EnemyStats (3041) - currentHealth is a public property over this backing field
        public const int EnemyStatsCurrentHealth = 0x24;
        public const int EnemyStatsMaxHealth = 0x20;

        // EnemyController (3032) - agent is private
        public const int EnemyAgent = 0x130;

        // DelusionLift (3333): liftPosition public, isMoving private
        public const int LiftPosition = 0xA4;
        public const int LiftIsMoving = 0xCC;

        // DoorsUltimate (3348): st8 is private
        public const int DoorUltimateState = 0x100;

        // DoorsDelusion (3339): isOpen is private
        public const int DoorIsOpen = 0x29;

        // DelusionDayNight (3463): _starTimeSeconds private, typeOfDay public
        public const int StarTimeSeconds = 0x20;
        public const int TypeOfDay = 0x5C;

        // CharacterStats (3629) - all public fields
        public const int DaysPlayed = 0x28;
        public const int CurrentHealth = 0x60;
        public const int CurrentMana = 0x64;
        public const int CurrentStamina = 0x68;
        public const int Coin = 0x88;
        public const int Glimmers = 0x94;

        // DelusionStats (3596)
        public const int CharacterStats = 0x28;
    }

    internal static class Log
    {
        private static BepInEx.Logging.ManualLogSource _src;
        private static System.IO.StreamWriter _file;

        /// <summary>Also writes BepInEx/ddmp_log.txt: the mod's own log survives a
        /// BepInEx config with disk logging off (the rig's default).</summary>
        internal static void Bind(BepInEx.Logging.ManualLogSource src)
        {
            _src = src;
            try
            {
                var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_log.txt");
                _file = new System.IO.StreamWriter(path, false) { AutoFlush = true };
            }
            catch { _file = null; }
        }

        private static void File(string level, string m)
        {
            try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {m}"); } catch { }
        }

        internal static void Info(string m) { _src?.LogInfo(m); File("INF", m); }
        internal static void Warn(string m) { _src?.LogWarning(m); File("WRN", m); }
        internal static void Error(string m) { _src?.LogError(m); File("ERR", m); }
        internal static void Debug(string m) => _src?.LogDebug(m);
    }
}
