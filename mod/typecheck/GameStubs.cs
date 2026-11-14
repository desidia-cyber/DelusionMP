// Compile-only stubs. NOT shipped, NOT loaded by the mod.
//
// Purpose: let `dotnet build` typecheck the whole plugin source outside the game, so
// syntax errors, wrong member names and arity mistakes are caught in CI instead of on
// the first in-game launch. The real build references the interop assemblies that
// BepInEx generates from the game's IL2CPP metadata; signatures here mirror the
// shipping build's dump (work/dump/dump.cs) for the members the mod actually touches.

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => b;
    }

    public class Object
    {
        public string name;
        public int GetInstanceID() => 0;
        public static T Instantiate<T>(T o) where T : Object => o;
        public static void Destroy(Object o) { }
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive = false) => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => Array.Empty<T>();
    }

    public class Behaviour : Component { public bool enabled; }
    public class MonoBehaviour : Behaviour { }

    public class GameObject : Object
    {
        public Transform transform;
        public bool activeInHierarchy;
        public bool activeSelf;
        public void SetActive(bool v) { }
        public T GetComponent<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive = false) => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => Array.Empty<T>();
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Vector3 eulerAngles;
        public Quaternion rotation;
        public Transform parent;
        public int GetSiblingIndex() => 0;
    }

    public struct Quaternion
    {
        public static Quaternion Euler(float x, float y, float z) => default;
    }

    public class Camera : Behaviour { }
    public class AudioListener : Behaviour { }
    public class Animator : Behaviour
    {
        public void SetFloat(string n, float v) { }
        public void SetBool(string n, bool v) { }
        public void SetTrigger(string n) { }
    }

    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() => Array.Empty<T>();
    }

    public static class Time
    {
        public static float realtimeSinceStartup;
    }

    public static class Input
    {
        public static bool GetKeyDown(KeyCode k) => false;
    }

    public enum KeyCode { F8, F9 }

    public static class Mathf
    {
        public static float LerpAngle(float a, float b, float t) => b;
        public static float Clamp01(float v) => v;
        public static float Atan2(float y, float x) => 0f;
    }

    public static class Application
    {
        public static string dataPath = "";
    }
}

namespace UnityEngine.AI
{
    public class NavMeshAgent : UnityEngine.Behaviour { }
}

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPluginAttribute : Attribute
    {
        public BepInPluginAttribute(string guid, string name, string version) { }
    }

    public class BasePlugin
    {
        public BepInEx.Logging.ManualLogSource Log;
        public T AddComponent<T>() where T : UnityEngine.Component => default;
        public virtual void Load() { }
        public virtual bool Unload() => true;
    }
}

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogInfo(object o) { }
        public void LogWarning(object o) { }
        public void LogError(object o) { }
        public void LogDebug(object o) { }
    }
}

namespace BepInEx.Unity.IL2CPP
{
    public class BasePlugin : BepInEx.BasePlugin { }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatchAttribute : Attribute
    {
        public HarmonyPatchAttribute(Type t, string m) { }
        public HarmonyPatchAttribute(Type t, string m, Type[] args) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPostfixAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPrefixAttribute : Attribute { }

    public class Harmony
    {
        public Harmony(string id) { }
        public void PatchAll(System.Reflection.Assembly a) { }
        public IEnumerable<System.Reflection.MethodBase> GetPatchedMethods() => Array.Empty<System.Reflection.MethodBase>();
        public void UnpatchSelf() { }
    }

    public class Traverse
    {
        public static Traverse Create(object o) => new Traverse();
        public Traverse Field(string n) => this;
        public Traverse Property(string n) => this;
        public T Value<T>() => default;
        public T Field<T>(string n) => default;
        public T GetValue<T>() => default;
        public void SetValue(object v) { }
    }
}

// ---- Game types: signatures mirror work/dump/dump.cs ----
public enum DoorState { unlocked = 0, locked = 1, open = 2, smashed = 3, midClose = 4, midOpen = 5, midPick = 6 }
public enum TypeOfDay { Dawn = 0, Noon = 1, Dusk = 2, Night = 3 }

public class FactDB
{
    public void SetFact(string key, bool value) { }
    public void SetFact(string key, int value) { }
    public bool GetFact(string key, out bool value) { value = default; return false; }
    public bool GetFact(string key, out int value) { value = default; return false; }
    public bool ChangeOrCreateIntFact(string factKey, int addedValue) => false;
    public Dictionary<string, int> GetIntFacts() => new Dictionary<string, int>();
    public Dictionary<string, bool> GetBoolFacts() => new Dictionary<string, bool>();
    public void LoadFacts(FactSaveData data) { }
    public FactSaveData GetFactsSaveData() => null;
}

public class FactSaveData { }

public class CharacterStats
{
    public int daysPlayed;
    public float currentHealth;
    public float currentMana;
    public float currentStamina;
    public int coin;
    public int glimmers;
}

public class DelusionSave
{
    public static DelusionSave Instance { get; }
    public FactDB Facts { get; }
}

public class DelusionManager
{
    public enum GameState
    {
        PREGAME = 0, SCENE_SWITCH = 1, RUNNING = 2, CHARPANELS = 3, SPEECH = 4,
        PAUSED = 5, ROLL = 6, REST = 7, BOOK = 8, PLAYER_DISABLED = 9,
        FORCED_LOOK = 10, CINEMATIC = 11, CINEMATIC_FULLSPEED = 12,
    }
    public static DelusionManager Instance { get; }
    public GameState CurrentGameState { get; }
    public UnityEngine.GameObject GetPlayer(bool getCamera) => null;
    public void RepositionPlayer(UnityEngine.Transform t, bool lockPlayerDuringLoad = false) { }
}

public class DelusionQuest
{
    public static DelusionQuest Instance { get; }
    public void ActivateQuest(string ID) { }
    public void ActivateQuestByTitle(string title) { }
    public void NotifyQuestStarted(string ID) { }
    public void NotifyQuestUpdated(string ID) { }
    public void NotifyQuestFinished(string ID) { }
}

public class DelusionTasks
{
    public struct PersistentTaskData { public string id; public int stage; }
    public class PersistentTaskDataList { public List<PersistentTaskData> list = new List<PersistentTaskData>(); }
    public static DelusionTasks Instance { get; }
    public bool TaskStageTrigger(string newTaskID, int newStageID, bool hasPopup) => false;
    public PersistentTaskDataList GetPersistentTaskData() => null;
    public void RestoreFromPersistentTaskData(PersistentTaskDataList dataList) { }
}

public class DelusionSceneControl
{
    public static DelusionSceneControl Instance { get; }
    public string currentSceneName;
}

public class RegionSceneManager { public static RegionSceneManager Instance { get; } }

public class DelusionDayNight
{
    public static DelusionDayNight Instance { get; }
    public TypeOfDay typeOfDay;
}

public class DelusionPlayerActions : UnityEngine.MonoBehaviour
{
    public enum MoveState { Idle = 0 }
    public static DelusionPlayerActions Instance { get; }
    public MoveState moveState;
    public bool isGrounded;
    public bool isCrouched;
    public bool isSprinting;
    public bool isDodging;
}

public class DelusionInventory { public static DelusionInventory Instance { get; } }
public class DelusionCinematics { public static DelusionCinematics Instance { get; } }
public class DelusionStats { public static DelusionStats Instance { get; } public CharacterStats characterStats; }

public class SaveableObject : UnityEngine.MonoBehaviour
{
    public string GetUniqueIdentifier() => "";
    public void RestoreIndividualState(string uniqueID) { }
}

public class DoorsDelusion : UnityEngine.MonoBehaviour { }
public class DoorsUltimate : UnityEngine.MonoBehaviour { }
public class DoorsDelusionVert : DoorsDelusion { }
public class DoorsDelusionBridge : DoorsDelusion { }

public class DelusionLift : UnityEngine.MonoBehaviour
{
    public enum LiftPosition { Bottom = 0, Top = 1 }
    public LiftPosition liftPosition;
}

public class ItemPickup : UnityEngine.MonoBehaviour { }
public class ItemChest : UnityEngine.MonoBehaviour { }
public class PlantPickup : UnityEngine.MonoBehaviour { }
public class ShopSpeaker : UnityEngine.MonoBehaviour { }
public class CompletableObject : UnityEngine.MonoBehaviour { }

public class EnemyStats : UnityEngine.MonoBehaviour
{
    public int maxHealth;
    public int currentHealth { get; set; }
}

public class EnemyController : UnityEngine.MonoBehaviour
{
    public enum CombatModes { InCombat = 0, FollowingFriend = 1, Patrolling = 2 }
    public enum HostileType { Aggressive = 0, Passive = 1, CannotFight = 2 }
    public enum FollowType { NeverFollows = 0, PlayerOnly = 1, FollowsFriends = 2 }
    public enum CombatantLevel { Level_1 = 0, Level_2 = 1, Level_3 = 2 }
    public HostileType hostileType;
    public CombatModes combatMode;
    public FollowType followType;
    public CombatantLevel myLevel;
    public bool isRunning;
    public bool hasBeenProvoked;
    public bool canOpenDoors;
    public EnemyStats myStats;
    public void Provoke() { }
    public void Respawn() { }
    public void OnDeath(bool killedByPlayer) { }
}

public class EnemySpawner : UnityEngine.MonoBehaviour
{
    public string optionalEnemyID;
    public bool spawnerActive;
    public bool canRespawn;
    public void CheckRespawn() { }
    public void EnemyKilled(bool killedByPlayer) { }
    public void RespawnEnemy(bool overrideCanRespawn, Action onRespawn) { }
}

public class EnemyCombatEncounter : UnityEngine.MonoBehaviour
{
    public string factMustBeTrue;
    public int intEqualOrHigher;
    public bool neverDisables;
}

public class SimpleTaskTrigger : UnityEngine.MonoBehaviour
{
    public string taskID;
    public int taskStage;
}

public class ItemInstance { public string itemInstanceID; public int amount; }
public class InventoryData { }

public class PlayerMove : UnityEngine.MonoBehaviour { }
public class PlayerLook : UnityEngine.MonoBehaviour { }
public class PlayerColliderTrigger : UnityEngine.MonoBehaviour { }

namespace UnityEngine
{
    public class CharacterController : Component { }
}