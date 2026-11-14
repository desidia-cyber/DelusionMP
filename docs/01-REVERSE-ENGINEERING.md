# Dread Delusion — Co-op Sync Surface (Reverse Engineering)

Everything a 2-player co-op mod has to replicate, with the evidence for each claim.

Method: full IL2CPP metadata dump (`Il2CppDumper` 6.7.46) of the shipping build, a
type/method index built from `dump.cs`, string-literal mining, and direct
`ScriptableObject`/`MonoBehaviour` extraction from the asset files using type trees
generated from the IL2CPP image.

Every class, field offset and method RVA in this document is read out of the game's own
metadata — not guessed. Where something is inferred rather than observed, it is marked
**inferred**.

---

## 1. Game identity

| | |
|---|---|
| Title | Dread Delusion (Lovely Hellplace / DreadXP) |
| Executable | `windows_content\Dread Delusion.exe`, `FileVersion 2022.3.60.6251517` (Unity **2022.3.60f1**) |
| EXE SHA-256 | `0125AC741A7A31EE0BA0F0C17CB38076F71EC25BE72C8F8DCA904B04ACC304356` |
| GameAssembly.dll SHA-256 | `0D8FA5D4E25D58F47799679CA99CA84F588DB9D98BC3D5CBCACF9D9305FAF0D1` |
| Runtime | Unity **IL2CPP**, `global-metadata.dat` version **31**, 10 961 568 bytes |
| Graphics | Universal Render Pipeline (URP) + custom `RetroPostProcessEffect` |
| Scripting | 64 dummy assemblies; all game code in **`Assembly-CSharp.dll`** (962 types) |
| Steam | `Facepunch.Steamworks.Win64` is compiled in; `steam_api64.dll` ships in `StreamingAssets` |
| Dump anchors | `CodeRegistration 0x181E1CEB0`, `MetadataRegistration 0x182190490` |

All method RVAs below are offsets from the `GameAssembly.dll` image base
`0x180000000` (so `VA = 0x180000000 + RVA`).

---

## 2. Reproducing the dump

```
Il2CppDumper.exe GameAssembly.dll global-metadata.dat out\
python tools/parse_dump.py work/dump/dump.cs work/dump/index.json
python tools/q.py types                 # every game type, grouped
python tools/q.py members <Type>        # fields + methods + RVAs for one type
python tools/q.py find <regex>          # search field/method names
python tools/extract_databases.py       # ScriptableObject content -> work/data/
```

`tools/parse_dump.py` produces a 10 787-type index (962 of them `Assembly-CSharp`);
`tools/q.py` is the query front-end used throughout this document.

---

## 3. Engine architecture that matters for co-op

Dread Delusion is a **single-player game with no netcode**. It is a normal Unity
`MonoBehaviour` scene game: 26 build scenes, 25 loadable regions, one persistent
"main" scene, and a global singleton layer.

```
DelusionPreBoot  -> DelusionBoot -> DelusionAudio -> DelusionMain (persistent)
                                                   -> DelusionMenu  -> REGION_* scenes
```

The systems that hold all state:

| Layer | Type | Role |
|---|---|---|
| Story flags | `FactDB` (plain class, owned by `DelusionSave`) | every boolean/int story switch |
| Save/restore | `DelusionSave` + `ISaveable` | generic keyed per-object state capture |
| Quests | `DelusionQuest` + `QuestSystemDB` | active quest list, quest/task state |
| Game flow | `DelusionManager` | game state machine, player refs, world state, cheats |
| Scene flow | `DelusionSceneControl` | loads/unloads region scenes |
| Region streaming | `RegionSceneManager` + `RegionDetector` | additive load/unload by player region |
| Entities | `EnemyController` / `EnemySpawner` / `NPCBase` | foes, spawners, dialogue NPCs |
| Interactables | `DoorsDelusion`, `DoorsUltimate`, `DelusionLift`, `DelusionLever`, `CompletableObject` | world machinery |
| Player | `DelusionPlayerActions`, `PlayerMove`, `PlayerLook`, `CharacterStats`, `DelusionInventory` | movement, vitals, gear |
| Time | `DelusionDayNight` | day/night cycle, rest |
| Dialogue | `DelusionDialogueController`, `DialogueTree`, `DelusionDiceRoll` | conversations, skill rolls |

### The single most important discovery

**The game already has a generic, keyed, per-object state system — the save layer.**
`DelusionSave` builds `Dictionary<string, object> loadedState` from every `ISaveable`
component in the scene (`GatherComponentIdentifiers` @ `0x404CE0`), and pushes it back
with `RestoreState` @ `0x405D20`:

```csharp
public interface ISaveable {            // TypeDefIndex 3121
    object CaptureState();              // slot 0
    void   RestoreState(object state);  // slot 1
}
```

`SaveableObject` @ `0x377B90` supplies the stable key (`uniqueIdentifier`,
`[SerializeField] private string`, captured at `0x42D620` / restored at `0x42DD80`).

That gives co-op two orthogonal levers per world object:

1. **Semantic events** — call the same public API the player interaction calls.
2. **State mirroring** — `CaptureState()` → wire → `RestoreState()` on the peer.

Lever 2 is the safe default for anything animated or multi-step: a door, lift, chest or
quest object restores to exactly the state the host reached, without replaying animation
timing.

---

## 4. Sync surface catalogue

Legend: **RVA** = `GameAssembly.dll` offset. **offset** = IL2CPP object field offset.

### 4.1 Story flags — `FactDB` (TypeDefIndex 2868)

The whole story is boolean and integer flags in two dictionaries. This is the single
highest-value sync target.

```
FactDB
  private Dictionary<string,bool> m_boolFacts;   // 0x10
  private Dictionary<string,int>  m_intFacts;    // 0x18
```

| Method | RVA | Notes |
|---|---|---|
| `SetFact(string,bool)` | `0x3A4440` | primary writer |
| `SetFact(string,int)` | `0x3A46C0` | primary writer |
| `GetFact(string, out bool)` | `0x3A3C60` | |
| `GetFact(string, out int)` | `0x3A3D10` | |
| `ChangeOrCreateIntFact(string,int)` | `0x3A3580` | additive counters |
| `GetBoolFacts()` | `0x365F00` | full snapshot |
| `GetIntFacts()` | `0x352390` | full snapshot |
| `GetFactsSaveData()` | `0x3A3DC0` | `FactSaveData` |
| `LoadFacts(FactSaveData)` | `0x3A4060` | |
| `ResetFacts()` | `0x3A4220` | |
| `ValidateFactCondition(string,string,EFactValueCondition,bool)` (static) | `0x3A4A10` | condition evaluator |
| `ValidateCharmCondition` (static) | `0x3A4900` | |
| `DoesFactExist(string,string)` (static) | `0x3A3B00` | |

Serialized form (`FactSaveData`, TypeDefIndex 2867): four parallel lists —
`BoolFactKeys`/`BoolFactValues`, `IntFactKeys`/`IntFactValues`.

`EFactValueCondition`: `Equal 0, More 1, Less 2, NotEqual 3`.
`EIfFactIsNull`: present as an enum; used by `ValidateFactCondition`'s `ifNull`.

**Write site count is enormous.** Every trigger, door, quest stage, dialogue action and
enemy death calls `DelusionSave.Instance.Facts.SetFact(...)`. In co-op the host is the
only writer; peers apply remote facts (see §6.2 for loop suppression).

Fact events on `DelusionQuest` (all `UnityEvent`, fired by the game, not by you):

```
QuestEvent       OnQuestStarted     0x20
QuestEvent       OnQuestFinished    0x28
FactUpdatedEvent FactUpdated        0x40
FactUpdatedEvent FactItemAdded      0x48
FactUpdatedEvent FactItemRemoved    0x50
FactIntStateEvent  FactIntState     0x58
FactBoolStateEvent FactBoolState    0x60
```

These are the observation points that let the host capture fact changes without hooking
`SetFact` — but hooking `SetFact`/`SetFact(int)` is cheaper and catches everything.

### 4.2 Quests and tasks — `DelusionQuest` (3208), `QuestSystemDB` (3225)

```
DelusionQuest : MonoSingleton<DelusionQuest>
  QuestSystemDB     QuestDatabase;      // 0x30
  List<Quest>       ActiveQuests;       // 0x38
```

| Method | RVA |
|---|---|
| `ActivateQuest(string ID)` | `0x489820` |
| `ActivateQuestByTitle(string)` | `0x489410` |
| `NotifyQuestStarted(string ID)` | `0x48A170` |
| `NotifyQuestUpdated(string ID)` | `0x48A210` |
| `NotifyQuestFinished(string ID)` | `0x48A0D0` |
| `GetAllQuestInfos()` | `0x489BC0` |
| `GetLatestQuestInfo()` | `0x489D60` |
| `OnQuestCompleted()` (private) | `0x48A2B0` |

Data model:

```
QuestBase            m_completed 0x10 | m_data 0x18 | NotifyCompleted 0x20 | OnQuestUpdated 0x28 | m_questID 0x30
  Quest              Active 0x38 | MainScenario 0x40 | BranchingScenarios 0x48
  QuestScenario      SubQuests 0x38 (List<SubQuest>)
  SubQuest
QuestBaseData
QuestData            -> Quests in QuestSystemDB (List<QuestData> at 0x18)
QuestScenarioData    SubQuests 0x28 (List<SubQuestData>)
SubQuestData         Mandatory 0x28 | QuestType 0x2C | QuestObjectiveByRef 0x30
                     QuestObjectiveByFact 0x38 | QuestCondition 0x40 | QuestConditionValue 0x48
```

`QuestBase` methods: `CheckCompletion()` `0x493990` (virtual), `SetCompleted(bool)`
`0x493AD0`, `Complete()` `0x493AB0`, `SetQuestID(string)` `0x4271B0`.

`QuestObject` (3213, on world objects that satisfy objectives):
```
OnKilled 0x20 | OnApproached 0x28 | OnUsed 0x30 | OnTalked 0x38
taskString 0x40 | taskStage 0x48 | hasTaskPopup 0x4C
```
`EQuestType`: `Kill 0, Approach 1, Use 2, Talk 3`.
`EQuestObjectiveCondition`: `Equal 0, More 1, Less 2, Add 3, Remove 4`.

`Task` (3603) / `TaskStage` (3604) is the player-facing journal model:
```
Task        taskID 0x10 | taskTitle 0x18 | stages 0x20 | taskSpriteID 0x28 | isComplete 0x30 | activeStage 0x38 | sortNum 0x40
TaskStage   stageID 0x10 | stageDescription 0x18 | canStartTask 0x20 | ignoresStageHeirarchy 0x21
            requiresActiveStage 0x28 | completesQuest 0x30
            factsToSetTrue 0x38 | factsToSetFalse 0x40
            glimmersGiven 0x48 | keyItemsNeeded 0x50 | itemsGiven 0x58
            itemRarity 0x60 | coinGiven 0x64 | needsItem 0x68 | itemRemoved 0x70
            itemsToRemove 0x78 | removeItemHasRarity 0x80 | removedRarity 0x84
```

`SimpleTaskTrigger` (3287, `UsableBase`) advances tasks on interaction:
```
taskID 0x28 | taskStage 0x30 | hasPopup 0x34 | hasTransition 0x35
isWaitingForTransition 0x36 | triggersTaskUpdate 0x37 | requiresStage 0x38
requiresExactStage 0x39 | willDisableSelf 0x3A
```

**Sync rule:** task/quest *stage transitions* are facts. Because `TaskStage` grants items,
coins and glimmers, replaying a stage on the peer would duplicate rewards — so peers apply
the resulting fact set and completion state, never the reward-granting side.

### 4.3 Save/restore — `DelusionSave` (3581)

```
saveSlot 0x20 | saveGameName 0x28 | demoSaveGameName 0x30 | InitialFactsDB 0x38
saveIconScript 0x40 | loadedState 0x48 (Dictionary<string,object>) | m_facts 0x50
OnFactChange 0x58 | OnLoad 0x60
FactDB Facts { get; }        // get_Facts @ 0x352450
```

| Method | RVA |
|---|---|
| `Save()` | `0x406730` |
| `Load()` | `0x405750` |
| `SaveFile(object)` (private) | `0x406330` |
| `LoadFile()` (private) | `0x4055C0` |
| `CaptureState(Dictionary<string,object>)` (private) | `0x403A20` |
| `RestoreState(Dictionary<string,object>, bool)` (private) | `0x405D20` |
| `GatherComponentIdentifiers()` (private) | `0x404CE0` |
| `GetLoadedState()` | `0x352430` |
| `StartNewGame(SaveSlot)` (private) | `0x406AC0` |
| `StartGameScene(SaveSlot,string)` (private) | `0x4068F0` |
| `ResetGameSave()`, `EraseData(SaveSlot)` | `0x405C70`, `0x404C90` |

Save file: a `BinaryFormatter`-serialized `System.Collections.Hashtable` (confirmed by
the on-disk `SaveData` file: bytes begin with the Hashtable type header). Not JSON — do
not attempt to diff it textually.

`PlayerData` (3577) is the per-slot character payload:
```
dataSaveSlot 0x10 | savedStats 0x18 (CharacterStats) | savedInventory 0x20 (InventoryData)
```

**Sync rule:** never transfer save files between players. Each player keeps their own
"career" file; the shared world state (facts, quests, world objects) is replicated
live. This mirrors the decision in Crime Scene Cleaner Co-op (see
`reference/coop-anything/games/crime-scene-cleaner`).

### 4.4 Enemies — `EnemyController` (3032), `EnemySpawner` (3038)

`EnemyController : Agent`. 90 methods; the state that must replicate:

```
combatMode 0xE8 | hostileType 0xB8 | followType 0xAC | hasBeenProvoked 0xC0
myFaction 0x8C | friendlyFactions 0x90 | enemyFactions 0xA0 | alwaysFriendlyToPlayer 0x98
canOpenDoors 0x60 | traversingDoor 0x1B9
isRunning 0xEE | walking 0x17E | movementSlowed 0x17C | movementStopped 0x17D
attackedByPlayer 0x17F | isSpeakingToPlayer 0xEC | isFacingTarget 0x1A4
targetEnemyObj 0x110 | targetFriendObj 0x118 | player 0x108
enemyStartPos 0x120 | destPoint 0xF8 | patrolPoints 0xF0
myStats 0x140 (EnemyStats) | combat 0x138 (EnemyCombat) | agent 0x130 (NavMeshAgent)
mySpawner 0x160 | myAnim 0x148 | myDetector 0x150 | myAudio 0x158
```

Key methods: `Provoke()` `0x44D270`, `Respawn()` `0x44D4F0`,
`OnDeath(bool killedByPlayer)` `0x44CEF0`.

`EnemyController.CombatantLevel`: `Level_1 0, Level_2 1, Level_3 2`
`HostileType`: `Aggressive 0, Passive 1, CannotFight 2`
`FollowType`: `NeverFollows 0, PlayerOnly 1, FollowsFriends 2`
`CombatModes`: `InCombat 0, FollowingFriend 1, Patrolling 2`

`EnemyStats` (3041) — the replica payload:
```
maxHealth 0x20 | <currentHealth> 0x24 | defaultMaxHealth 0x28
enemyDamage 0x2C | defaultDamage 0x30 | enemyDefense 0x34 | defaultDefense 0x38
maxStamina 0x3C | currentStamina 0x40 | isPoisoned 0x68 | isSlowed 0x6C
isBurned 0x70 | isBuffed 0x74 | isCharmed 0x78 | speedMultiplier 0x88
```

`EnemySpawner` (3038) is the authority for a given foe — it already implements
`ISaveable`:
```
QuestObjectRef 0x20 | enemyObject 0x28 | enemyController 0x30 | myLevel 0x38
difficultyMode 0x3C | respawn 0x40 (RespawnTimer) | maxDaysUntilRespawn 0x48
canRespawn 0x4C | respawnsOnPlayerDeath 0x4D | optionalEnemyID 0x50
savesKillData 0x58 | setNormalFact 0x60 | setFactBool 0x68
simpleTaskTrig 0x70 | enemyLoot 0x78 | spawnerActive 0x88
```
Methods: `ActivateSpawner(bool)` `0x45AF60`, `CheckRespawn()` `0x45B030`,
`EnemyKilled(bool)` `0x45B330`, `DisableEnemy()` `0x45B1B0`,
`RespawnEnemy(bool,Action)` `0x45BD80`, `StoreKillData()` `0x45C6B0`,
`[ISaveable] CaptureState` `0x45B6D0`, `[ISaveable] RestoreState` `0x45B710`.

`RespawnTimer` (3610): `isActive 0x11 | respawnTimer 0x14 | daysUntilRespawn 0x18`.

**The cleanest enemy model here: replicate the spawner, not the enemy.** `EnemySpawner`
already owns kill state and persistence; mirroring `spawnerActive` + enemy
`currentHealth` + a death signal reproduces the world exactly, and stops the classic
co-op failure where one player kills an enemy that the other still has.

`EnemyCombatEncounter` (3401) gates encounter waves:
```
enemiesToSpawn 0x20 | neverDisables 0x28 | factMustBeTrue 0x30 | intEqualOrHigher 0x38
```

### 4.5 Bosses

There is **no `Boss` type in the assembly** (verified: `python tools/q.py grep boss` →
no matches). Bosses in Dread Delusion are ordinary `EnemyController` objects with
`CombatantLevel` set high, usually driven by scripted scenes:

- `CinematicsManager` holds the boss-set-piece `PlayableDirector`s: `fortressScene`,
  `cutterLiftoff`, `angelAwaken`, `velaKillPlayer` (offsets `0x30`, `0x38`, `0x40`,
  `0x48`).
- `VelaFortressLift` (3381) is the endgame fortress/mech mechanism, gated on
  `CharWorldState`.
- `DelusionPlayerSpell.SummonReferences` / `summonSpawners` (`0xA0`/`0xA8`) spawn charmed
  or summoned `EnemyController`s — these must be host-authored too.
- Quest gating for boss fights is `FactDB` + `TriggerByFactOrTask` (§4.7).

**Sync rule:** treat boss fights as (a) host-owned `EnemyController` vitals, (b)
one-shot world events for phase triggers, (c) host-driven `PlayableDirector` playback
that peers are told to play by ID, never started independently.

### 4.6 Doors and locks

| Type | TDI | Notes |
|---|---|---|
| `DoorsDelusion` | 3339 | normal door, `UsableBase` + `ISaveable` |
| `DoorsDelusionVert` | 3340 | vertical variant |
| `DoorsDelusionBridge` | 3334 | |
| `DoorsUltimate` | 3348 | the fully-featured door: pick/smash/item/fact/lore locks |
| `DoorsUltimate_InteractTrigger` / `_SmashTrigger` | 3349 / 3350 | |
| `DoorGuard` | 2819 | state-machine guard |
| `DoorDelusionClosed` | 2818 | animator state behaviour |
| `DoorsDelusionStats` | — | scriptable config: `EnemyCanOpen`, `IsLocked` |

`DoorsDelusion` fields: `isAnimating 0x28 | isOpen 0x29 | doorStats 0x30 | silverLocks 0x38
| goldLocks 0x40 | bar 0x48 | itemLock 0x50 | staysOpen 0x68 | doorAnim 0x70` — 35 methods.

`DoorsUltimate` (35 methods) is the one that matters:
```
unlockableByPick 0x20 | lockDifficulty 0x24 | unlockableByItem 0x28 | itemNeededID 0x30
destroyItemOnUse 0x38 | requireRarity 0x39 | itemNeededRarity 0x3C
unlockableBySmash 0x40 | mightRequired 0x44 | unlockableByFact 0x48 | factID 0x50
factOpensItself 0x58 | unlockableByLore 0x59 | loreTriggers 0x60 | loreOpensItself 0x68
enemyCanOpen 0x6C | factToSet 0x70 | doorBasic 0x78 | doorSmashable 0x80
doorSmashableRubble 0x88 | silverLocks 0x90 | goldLocks 0x98 | itemLock 0xA0
specialLock 0xA8 | disableOnSmash 0xB0 | enableOnSmash 0xB8
doorAnim 0xE8 | st8 0x100 (DoorState) | smashOnly 0x104 | loreComplete 0x105
factIsSet 0x106 | saveData 0x120 (SaveData)
```

`DoorState`: `unlocked 0, locked 1, open 2, smashed 3, midClose 4, midOpen 5, midPick 6`.

**Sync rule:** door state is `st8` + `saveData`. Replicate `DoorState` transitions as
ordered events; do not replicate the animator. A door going `locked→unlocked` must also
replicate the fact it sets (`factToSet`), or the peer's quest state diverges.

### 4.7 Moving platforms and world machinery

| Type | TDI | Axis of motion |
|---|---|---|
| `DelusionLift` | 3333 | vertical lift, lever-driven, `ISaveable` |
| `DelusionLiftLever` | — | lever for lifts |
| `VelaFortressLift` | 3381 | endgame fortress lift |
| `MovingFactory` | 3408 | A↔B translate + rotate machine |
| `MovingSkiff` | 3409 | boat |
| `AirshipController` | 2961 | the airship (its own camera + flight model) |
| `Gatebridge` | — | drawbridge |
| `RotateByTime` / `RotationConstant` / `RotationConstController` | — | decorative rotators (local) |
| `LiftAnimationComplete` | — | animation callback |
| `SorterArm`, `Trap`, `TrapRetracted`, `StatueSmasher` | — | one-shot mechanisms |

`DelusionLift` (a textbook replicable mover):
```
recallLocked 0x20 | liftDisabled 0x21 | overrideAnimations 0x22 | onlyTriggersAfterJourney 0x23
questObjectRef 0x28 | factToSetWhenUsed 0x30 | keyItemNeeded 0x38
liftCarriage 0x40 | liftAnim 0x48 | leverTop 0x70 | leverBot 0x78
topPos 0x80 | botPos 0x88 | liftTimer 0x90 | liftPosition 0xA4 | startingLiftPosition 0xA8
factIsSet 0xAC | currentMovePos 0xB0 | moveDestination 0xBC | timeLeft 0xC8
isMoving 0xCC | saveData 0xD0 (SaveData)
```
`DelusionLift.LiftPosition`: top/bottom enum; 26 methods.

`MovingFactory`:
```
isMoving 0x20 | isRotating 0x21 | timeLeft 0x24 | moveTime 0x28 | rotateTime 0x2C
pauseTime 0x30 | currentMoveState 0x34 | moveObjectTransform 0x38
pointA 0x40 | pointB 0x48 | rotateA 0x50 | rotateB 0x58 | rotateC 0x60
currentMovePos 0x78 | moveDestination 0x84 | currentRotatePos 0x90 | rotateDestination 0x9C
```

**Sync rule — the important one.** Do **not** replicate transform. A lift/platform is a
*deterministic local animation* once started. Replicate the **transition**
(`liftPosition`, `isMoving`, `currentMoveState`, `timeLeft`) at the moment it changes and
let each client run its own animation. Peers who join mid-ride must be snapped to the
host's `currentMovePos`/`moveDestination` once, then run locally. This is exactly the
pattern that avoids rubber-banding on moving platforms.

### 4.8 Items, inventory, shops, loot

```
Item            3621   itemID 0x10 | itemNames 0x18 | itemCategory 0x20 | subItemCategory 0x24
                       sellCategory 0x28 | SpriteIDs 0x30 | itemDescriptions 0x38
                       isStackable 0x40 | isSellable 0x41 | hasRarity 0x42 | usableEquip 0x43
                       isAlchemyIngredient 0x44 | buyValues 0x48 | statValues 0x50
                       castTime 0x58 | skillModifier 0x5C | skillModAmount 0x60 | skillModMultiplier 0x64
ItemInstance    3138   itemInstanceID 0x10 | item 0x18 | instantUse 0x20 | rarity 0x24
                       amount 0x28 | isEquipped 0x2C | equippedSlot 0x30
                       notEnoughUpgradeMats 0x34 | soldToShop 0x35 | barterValue 0x38 | statModifier 0x40
InventoryData   3636   inventoryDataList 0x18   (ScriptableObject)
ItemDatabaseData 3637  alchemyMasterComboList 0x20 | playerHouseUpgradeData 0x28 | itemDEBUG_List 0x30
ItemPickup      3146   itemInstance 0x38 | respawn 0x20 | canRespawn 0x28 | itemModel 0x48
                       foundStats 0x70 | isAdding 0x78   (ISaveable)
ShopSpeaker     3168   shopRestock 0x20 | shopInventory 0x28 | sellCategories 0x30
                       expensiveGoods 0x38 | cheapGoods 0x40 | savedShop 0x50 (SaveItemList)  (ISaveable)
```

`ItemCategory`: `EQUIPMENT, ACCESSORY, USEABLE, SPELL, KEY, AIRSHIP`.
`ItemRarity`: `Poor, Common, Fine, Shroomweave, Clockwork, Void, Emberian`.
`EquippedSlot`: 21 values (Weapon1/2, Head, Body, Useable1..6, Spell1..6, Accessory1/2, Ring1/2).

**Sync rule — per-player inventory.** Each player keeps their own `InventoryData`,
`CharacterStats` and `PlayerData`. What *is* shared is the **world pickup**
(`ItemPickup`): when one player takes a static pickup, it must vanish for both. Player
drops are per-player. Shop stock (`ShopSpeaker.savedShop`, `lastDayRestocked`) is shared
because it is world state and is `ISaveable`.

### 4.9 Player state and combat

```
DelusionManager 3507  player 0x60 | playerCamera 0x70 | playerMove 0x80 | playerLook 0x88
                      currentGameState 0x20 | hardMode 0x9F | debugMode 0x9D
                      OnGameStateChanged 0x28 | OnWorldStateChange 0x30
DelusionPlayerActions 3549
                      playerState 0x20 | moveState 0x28 | lookState 0x2C
                      isGrounded 0x31 | currentEquipmentObj 0x58 | isDodging 0x60
                      isCrouched 0x61 | isMoving 0x62 | isSprinting 0x63 | deathTime 0x64
                      OnPlayerDeath 0x70 | OnPlayerDeathRespawn 0x78
                      OnQuestTransition 0x80 | OnTeleport 0x88 | OnItemUsed 0x90
                      playerIsDying 0xA0 | questTransition 0xA1 | lastItemUsed 0xB8
PlayerMove      2884   controller 0x20 | speed 0x28 | acceleration 0x2C | gravity 0x34
                       jumpHeight 0x44 | isCrouched 0x78 | velocity 0xB0 | isGrounded 0xBC
                       moveDirection 0xC0
PlayerLook      2881   playerBody 0x20 | xRotation 0x38 | isForcedLook 0x3C
CharacterStats  3629   currentHealth 0x60 | currentMana 0x64 | currentStamina 0x68
                       coin 0x88 | picks 0x8C | glimmers 0x94 | delusions 0x98
                       might 0xA8 | guile 0xB0 | wisdom 0xB8 | persona 0xC0
                       armour 0xC8 | weapon1Damage 0xD0 | weapon2Damage 0xD8
                       daysPlayed 0x28 | charWorldState 0x24 | charHardMode 0x2C
                       lastSavePoint 0x58 | booksReadList 0x38 | mapMarkersList 0x40
                       factionList 0x48 | otherStatsList 0x50
DelusionPlayerSpell 3559  currentSpell 0x38 | _currentSpellState 0x40 | castCharge 0x44
                       summonEffect 0x68 | canTeleport 0x8A | teleportTransform 0x90
                       summonSpawners 0xA8 | charmed 0xB8
```

`CharWorldState`: `NewGame 0, InqCell 1, Tutorial 2, CharQuests 3, MidGame 4, LateGame 5,
Ending 6, GameComplete 7`. This is a *global* progression switch and must be host-owned —
`TriggerByFactOrTask` and several cutoff scripts key off it.

`DelusionManager.GameState`: `PREGAME 0, SCENE_SWITCH 1, RUNNING 2, CHARPANELS 3,
SPEECH 4, PAUSED 5, ROLL 6, REST 7, BOOK 8, PLAYER_DISABLED 9, FORCED_LOOK 10,
CINEMATIC 11, CINEMATIC_FULLSPEED 12`.

**Sync rule:** each player is authoritative over their own `PlayerMove`, `PlayerLook`,
`DelusionPlayerActions` and `CharacterStats`. Send position/rotation/vitals/move-state at
~20 Hz; remote puppets are interpolation-only. Do not replicate camera or forced-look.

### 4.10 Dialogue

```
DelusionDialogueController 3108  tree 0xA0 | currentNode 0xA8 | Speaker 0x80
                                  OnDialogueChanged 0x30 | OnDialogueTextUpdate 0x38
                                  OnDialogueStarted 0x40 | OnDialogueFinished 0x48 | OnDialogueSkip 0x50
NPCBase 3167                     NPCTag 0x20 | dialogueTree 0x30 | QuestObjectRef 0x38
                                  nameRevealedOnFact 0x48 | speakerFaction 0x50
                                  questTitleString 0x98 | questStage 0xA0 | questStageAlt 0xA4
                                  questStage3 0xA8 | questStagePopup 0xAC | shopSpeaker 0xC0 | transaction 0xC8
DialogueNodeActionData 3117      Type 0x10 (EDialogueActionType) | Name 0x18 | Param 0x20
DelusionDiceRoll 3470            OnDiceRollComplete 0x20 | currentResult 0x98
                                  EasyTable 0xA8 | MediumTable 0xB0 | HardTable 0xB8 | BrutalTable 0xC0
```

`EDialogueActionType`: `None 0, SetFact 1, CallOnPlayer 2, CallOnSpeaker 3,
EndDialogue 4, RefreshHiddenChoices 5`.

**Sync rule:** dialogue is **local-only**. Both players may talk to the same NPC
independently. Only the *effects* are shared: the `SetFact` actions inside
`DialogueNodeActionData`, the quest stage advances on `NPCBase`, and dice-roll outcomes
that gate content. Do not stream dialogue UI.

### 4.11 Scene and region streaming

```
DelusionSceneControl 3589  currentSceneName 0x28 | mainPlayScene 0x38 | bootScene 0x40
                           menuScene 0x48 | audioScene 0x50 | debugScene 0x58
                           creditsScene 0x60 | additiveDebugScene 0x68
                           _loadOperations 0x30 | isFading 0x94 | OnSceneSwitch 0x98
RegionSceneManager  3006   regions 0x20 (List<RegionScene>) | allowLoading 0x28
                           allowUnloading 0x29 | initialLoadComplete 0x2A
RegionSceneManager.RegionScene 3004
                           region 0x10 (DreadMasterRegions) | scene 0x14 | state 0x18
                           needsLoading 0x1C | needsUnloading 0x1D | op 0x20
RegionDetector     3001    colliders 0x20 | center 0x28 | halfExtents 0x34
                           player 0x48 | masterRegions 0x50 | subRegions 0x58
RegionTrigger      3412    thisRegionType 0x20 | thisMasterRegion 0x24 | thisSubRegion 0x28
                           skybox override, fog override, death respawn points
```

APIs: `AllowLoadingRegions(bool)` `0x453E40`, `AllowLoadingRegions(bool,bool)` `0x453E20`,
`DisallowLoadingExcept(DreadMasterRegions)` `0x453E60`, `IsLoadingAllowed(...)`,
`ResetRegions()` `0x454410`, `IsLoadingOrUnloading()` `0x453EB0`.

**There are 25 `DreadMasterRegions` but only ~19 region scenes** — several regions share a
scene, and the manager is purely local streaming around the local player. Co-op must let
each client stream independently; forcing the peer into the host's region would fight the
`RegionDetector`.

**Sync rule:** location sync is *soft*. Send each player's `DreadMasterRegions` +
`DreadSubRegions`; when they differ, the far player's puppet is hidden rather than
teleported. Scene transitions (`DelusionSceneControl`) are coordinated only for
story-mandated jumps (a `RepositionPlayer` / `EPTransition` on the host must move the
peer too).

### 4.12 Day/night and rest — `DelusionDayNight` (3463)

```
_starTimeSeconds 0x20 | _starTimeHours 0x24 | starIsActive 0x28 | manualChange 0x29
OnNewDay 0x30 | DayPassed 0x38 | OnTypeOfDayChanged 0x40 | OnRest 0x48
hourAfterScreenFade 0x58 | typeOfDay 0x5C | newTypeOfDay 0x60
restingInBed 0x64 | badSleep 0x65 | playerCannotRest 0x66
```
`TypeOfDay`: `Dawn, Noon, Dusk, Night`.

**Sync rule:** the clock is world state. Host owns `_starTimeSeconds` and
`daysPlayed`; rest advances the day for both. Enemy and item respawn both key off
`DayPassed` / `PlayerRest`, so if the clock diverges, spawners diverge.

### 4.13 Airship — `AirshipController` (2961)

`ISaveable`. Own camera, flight model and docking: `anchor 0x30`, `defaultCamOffset`,
`dockingSpringConstant 0x8C`, `dockRememberDistance 0x90`, `ignoreRegions 0x98`.
Support types: `AirshipAnchor` (+`DropAnchorAfterLoad`), `AirshipDock`,
`AirshipDockingPoint`, `AirshipUpgradeSystem`, `AirshipPartPivot`, `SlotAirship`.

**Sync rule:** the airship is a shared vehicle. Host-simulate it, replicate its
transform, velocity and docking state; the second player rides as a passenger (their
camera stays local). `AirshipUpgradeStation`/`AirshipUpgradeSystem` upgrades are world
state and must be shared.

### 4.14 One-shot world events and transitions

```
EPTransition     3239  fadeInTime 0x20 | fadeOutTime 0x24
EPZone           3235  objectsToActivate 0x20 | objectsToDeactivate 0x28 | transition 0x38 | playerInside 0x40
EPTransition_Door / _Fade / _Shared, EPSharedBarrier, EPTeleportable
TriggerOnPlayerEnter 3305  (objects on/off, animator bools, factToSet, setsNewWorldState)
TriggerByFactOrTask  3296  (fact/task/worldstate conditions -> object toggles)
TriggerByTask        3300  (task + allowed stages -> object toggles)
TriggerWithItem      ~     TriggerWithScript, TriggerByRest, TriggerByTimeOfDay
TurnOver / ResetGlimmers / WorldStartScript / WorldStateScript
```

**Sync rule:** these are the highest-offender duplicates. A `TriggerOnPlayerEnter` fires
once per player entering — in co-op it will fire twice. Every such trigger must be
converted to a host-authored one-shot event keyed by (object key, trigger index) with
at-most-once delivery, then replayed to late joiners.

### 4.15 Achievements and Steam — `DelusionManager`, `DelusionSteam` (3597)

`DelusionManager` methods: `TriggerSteamAchievement` `0x3E4220`,
`TriggerSteamAchievement_OnceOnly` `0x3E4140`, `TriggerSteamAchievement_AndFact`
`0x3E4010`, `TriggerSteamAchievement_IfFact` `0x3E4090`, `CheckSteamAchievement`
`0x3DE5B0`, `CheckItemIsAchievement` `0x3DE440`.

Existing achievement IDs mined from string literals: `ACHIEVEMENT_BANISHED`,
`ACHIEVEMENT_MERGED` (more exist as ScriptableObject data).

**Sync rule:** achievements are per-player. Do not replicate `TriggerSteamAchievement`;
let each player's own facts drive their own achievements.

---

## 5. Content catalogue

### 5.1 Build scenes (26, in build order == `level0..level25`)

| idx | scene | file size |
|---|---|---|
| 0 | `DelusionPreBoot` | 8 940 |
| 1 | `DelusionBoot` | 1 346 444 |
| 2 | `DelusionAudio` | 480 056 |
| 3 | `DelusionMain` | 39 683 768 |
| 4 | `DelusionMenu` | 1 388 708 |
| 5 | `DelusionCredits` | 73 372 |
| 6 | `DelusionCombatTest` | 21 968 240 |
| 7 | `REGION_Starting` | 23 756 056 |
| 8 | `REGION_Hallowshire` | 31 163 600 |
| 9 | `REGION_MinesHallow` | 4 933 276 |
| 10 | `REGION_Clockwork` | 39 683 768 |
| 11 | `REGION_DungeonEmberian` | 5 138 328 |
| 12 | `REGION_Endless` | 23 115 132 |
| 13 | `REGION_MushroomIsle` | 50 132 |
| 14 | `REGION_WikkanIsle` | 1 416 452 |
| 15 | `REGION_MinesEndless` | 6 547 424 |
| 16 | `REGION_DungeonEndless` | 5 716 228 |
| 17 | `REGION_PlayerHouse` | 4 103 232 |
| 18 | `REGION_ObservatoryIsle` | 2 293 844 |
| 19 | `REGION_Underlands` | 14 753 560 |
| 20 | `REGION_DesertIsland` | 5 613 124 |
| 21 | `REGION_GodWarIsland` | 8 710 300 |
| 22 | `REGION_CreatureIsland` | 13 805 392 |
| 23 | `REGION_TinyIslands` | 142 428 |
| 24 | `REGION_Cradle` | 8 759 004 |
| 25 | `REGION_DemoIsle` | 1 871 876 |

Size mapping to `levelN` is by build index; verify per-build before relying on it.

### 5.2 Master regions (`DreadMasterRegions`, 25 values)

`None 0, Hallowshire 1, Endless 2, Clockwork 3, Starting 4, Underlands 5, VelaShip 6,
MushroomIsle 7, ObservatoryIsle 8, DemoIsle 9, WikkanIsle 10, MinesHallow 11,
MinesEndless 12, MinesClockwork 13, PlayerHouse 14, DungeonEmberian 15,
DungeonEndless 16, DungeonClockwork 17, DesertIsland 18, GodWarIsland 19,
CreatureIsland 20, Cradle 21, TinyIslands 22, CombatTestQuestionMark 23,
CombatTestFortress 24`

### 5.3 Sub-regions (`DreadSubRegions`, 23 values)

`None 0, HallowTown 1, EndlessTown 2, ClockworkTown 3, HallowCastle 4, PaeguthRuins 5,
BigOlSphere 6, Pwyll_Tutorial 7, Pwyll_BlindingLight 8, Pwyll_Town 9,
endgame_GodWarMech 10, endgame_CrashedEmberianShip 11, CombatTestQuestionMark 12,
CombatTestFortress 13, EmberianHideout 14, DuchessIsland 15, UnderlandsCity 16,
ClockworkRuins 17, ClockworkCorrupt 18, EndlessExIsle 19, UnderlandsCradle 20,
Creature_Town 21, Creature_Intestines 22`

### 5.4 Content ID namespaces seen in assets

Mined from asset string data (`tools/scan_assets.py`):

- Quests: `Q_*` — e.g. `Q_Bardolph`, `Q_Innogen_Crates`, `Q_Innogen_LostPurse`,
  `Q_Guard_Smugglers`, `Q_Tutorial_TOGGLE`, `Q_VELA`, `Q_DIRT`, `Q_DYZL`;
  plus `QUEST_*` (scene/trigger names, not quests: `QUEST_CITY`, `QUEST_ANGEL`, …).
- Achievements: `ACHIEVEMENT_BANISHED`, `ACHIEVEMENT_MERGED`, `ACH*` short ids.
- Notes/UI: `NOTE_*` (~100), `FACTION_INFO_*`, `FACTION_NAME_*`, `DOOR_*`.
- Regions: `REGION_*`, `SUBREGION_*` (also used as scene names).

The authoritative quest list is the `QuestSystemDB` ScriptableObject (one
`List<QuestData>`), extracted by `tools/extract_databases.py` into
`work/data/QuestSystemDB.json`. Fact keys are the `InitialFacts` ScriptableObject plus
every `List<string>` in `TaskStage`/`SubQuestData`.

---

## 6. Co-op architecture

### 6.1 Shape

```
                ┌──────────── host game (BepInEx IL2CPP plugin) ────────────┐
                │  Harmony patches on fact/quest/door/enemy/spawner paths   │
                │  Authoritative simulation + world-state journal           │
                │  Transport: SteamNetworkingMessages via steam_api64        │
                └───────────────────────────┬──────────────────────────────┘
                                            │ UDP datagrams (Steam relay)
                ┌───────────────────────────┴──────────────────────────────┐
                │  peer game (same plugin)                                  │
                │  Applies world state, renders puppet, own local player    │
                └──────────────────────────────────────────────────────────┘
```

Because the game is IL2CPP and already links `steam_api64.dll`, the mod is a
**BepInEx 6 IL2CPP plugin** (C#) that talks to Steam's flat API directly — no Spacewar
app id, no external relay, no port forwarding. This is the same route documented for
Crime Scene Cleaner.

### 6.2 Authority model

| Domain | Authority | Replication |
|---|---|---|
| Story facts | host | host→peer, delta of changed keys; never peer→host |
| Quests/tasks | host | derive from facts; send stage transitions |
| World objects (doors, lifts, chests, triggers) | host | `DoorState`/`SaveData`/motion transitions as ordered events |
| Enemy spawner state | host | kill/spawn events + vitals for engaged enemies |
| Enemy AI | host | puppet transform + animation state at 10 Hz |
| Player movement | each client | own player local; peer puppet interpolated |
| Player inventory/stats | each client | local only |
| Dialogue / UI / camera | each client | local only |
| Day/night clock | host | tick + day-change events |
| Airship | host | transform + docking state |
| Achievements | each client | local only |

**Loop suppression is the #1 correctness requirement.** Applying a fact on the peer runs
the same `SetFact` path, which fires `TriggerByFactOrTask`, spawners and door logic. Use:

1. A thread-static `ApplyingRemote` flag checked by every outbound Harmony patch.
2. Facts are *idempotent*: only send when the value actually differs from last-known.
3. World-object events are keyed by `(uniqueIdentifier, eventId)` with at-most-once
   delivery, replayed to late joiners from a journal.

### 6.3 Transport

Reuse the `dollsMP` wire discipline: a small binary header, sequence numbers, a
reliability layer for events and unreliable for transforms.

- Transforms: unreliable, 20 Hz, quantised (position 1 cm, rotation 0.1°).
- Facts/events: reliable-ordered, at-most-once by id, journalled for late join.
- Handshake: mod version + game build hash + content-table hash.

The `dollsMP` Rust crates (`dollsmp-core` bitstream/quantize/reliability/delta/desync)
are directly reusable as the protocol *specification*; for a C# in-process plugin the
same logic is ~600 lines and avoids a second process.

---

## 7. Implementation plan

**Phase 0 — harness.** BepInEx 6 IL2CPP loads; a debug console; dump live singletons
(`DelusionSave.Instance.Facts.GetBoolFacts()` etc.). Verify the metadata v31 / BepInEx
version pairing first — a mismatch here costs days.

**Phase 1 — identity + session.** Object keys from `SaveableObject.GetUniqueIdentifier()`;
build a content table (scene, path, key) per region; hash-exchange with the host; refuse
mismatched builds. Two instances on one PC must coexist before any sync work.

**Phase 2 — facts and quests.** Hook `FactDB.SetFact(string,bool)` / `SetFact(string,int)`;
send diffs; apply with loop suppression; verify quest journal matches on both sides.
This alone makes the story co-op.

**Phase 3 — world objects.** `ISaveable` mirror for doors, lifts, chests, machines,
shop stock. Snapshot on join; ordered events afterwards.

**Phase 4 — enemies.** Spawner-state ownership, vitals, death, respawn; puppet AI.

**Phase 5 — players.** Remote puppet (build from the player prefab; hide the host's
first-person-only parts), vitals HUD, name tag.

**Phase 6 — bosses, cutscenes, transitions.** Host-driven `PlayableDirector` by ID,
teleport/reposition both players, `CharWorldState` and day/night.

**Phase 7 — QA.** Census bot over all 25 regions; two-client lockstep harness comparing
fact maps, door states and spawner states after scripted actions.

---

## 8. Unknowns and risks

Verified as **absent** or **unknown**, and each is a real risk:

1. **No `Boss` type.** Bosses are plain enemies plus scripted scenes — so "boss sync"
   is really "enemy sync + cutscene sync". Confirmed by type search.
2. **`ISaveable` payloads are `object`.** Many `CaptureState` implementations return
   primitive/serializable structs, but some return engine types that cannot cross the
   wire. Each one must be mapped individually; there is no generic marshal.
3. **Save files use `BinaryFormatter`.** Never deserialize peer data with it. Sync only
   typed diffs built in-process.
4. **`TriggerOnPlayerEnter` and friends are local per-player by design.** Every one needs
   an explicit conversion; missing one produces double-granted items.
5. **`EnemyController` has 90 methods and a NavMesh agent.** Puppeting it well is the
   largest single animation/replication effort.
6. **Region streaming is camera-driven** (`RegionDetector` uses the local player). Two
   players far apart means two different region sets; the mod must not force them equal.
7. **`DelusionDayNight` respawn coupling.** Enemy and pickup respawn tick on day changes;
   clock divergence silently desyncs spawners.
8. **Not yet verified live:** exact `FactDB.SetFact` event fan-out (needs a runtime hook),
   and which `ISaveable` implementations exist per scene (needs the census run).

---

## 9. Verification plan

- `tools/q.py` — every claim about a type in this document is re-checkable.
- `tools/extract_databases.py` — regenerates `work/data/*.json` content tables.
- Census bot: load each of the 25 regions, enumerate `ISaveable` components and their
  `uniqueIdentifier`s, write `work/data/scene_objects.json`. This is the ground truth for
  the object-identity table and is the first thing to run against a live game.
- Two-client harness: scripted actions (open door, take item, kill enemy, advance quest)
  then compare full fact maps on both clients.
