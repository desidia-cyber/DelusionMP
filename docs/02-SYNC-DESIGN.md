# Dread Delusion — Co-op Sync Design

This is the design that follows from `01-REVERSE-ENGINEERING.md`. It states, per system,
who owns the state, what crosses the wire, and which game method is hooked.

---

## 0. Principles

1. **Host-authoritative world, client-authoritative player.**
   The world (facts, quests, objects, enemies, clock) has exactly one writer: the host.
   A player's own body, inventory and camera are purely local.

2. **Never replicate animation; replicate the transition.**
   Every animated interactable in this game already has an explicit state
   (`DoorState`, `LiftPosition`, `SaveData`, `saveData`). Send the state, let each client
   run its own animation.

3. **Loop suppression is mandatory.**
   Applying remote state runs the same code path that produced it, which re-fires
   triggers, spawners and doors. Every entry point must be guarded.

4. **Facts are the spine.**
   `FactDB` is a small string→(bool|int) map and almost every system keys off it. Get facts
   right and quests, doors, spawns and cutscenes mostly follow.

5. **Separate careers from the world.**
   Each player keeps their own save slot, stats and inventory. Only the world is shared.

---

## 1. Object identity

Co-op needs a stable name for every world object that both clients agree on, because
`GetInstanceID()` and `path_id` differ per process.

The game already solved this: `SaveableObject.uniqueIdentifier` (`[SerializeField]`,
`GetUniqueIdentifier()` @ `0x377B90`). From the asset scan:

| | |
|---|---|
| `SaveableObject` components | **4 804** |
| with a non-empty `uniqueIdentifier` | **4 754** |
| distinct identifiers | **4 493** |

Identity key = `(sceneName, uniqueIdentifier)`. For the ~50 without an identifier, fall
back to `(scene, hierarchyPath, siblingIndex)`.

`DelusionSave.GatherComponentIdentifiers()` @ `0x404CE0` already builds this lookup, and
`SaveableObject.RestoreIndividualState(string uniqueID)` @ `0x355980` can restore one
object by key — useful for late-join repair.

**Boot protocol:** both clients compute a hash of `(scene → sorted uniqueIdentifiers)` and
exchange it in the handshake. A mismatch means different game builds or a mod conflict;
refuse to start rather than desync silently.

---

## 2. Wire protocol

Reuse the `dollsMP` framing (`crates/dollsmp-core/src/protocol.rs`) with a new magic.

```
+--------+--------+--------+---------+--------+-----------+---------+----------+
| magic  | ver    | kind   | flags   | seq    | ack       | ticks   | len      |
| 'DDMP' | u16    | u8     | u8      | u32    | u32       | u32     | u16      |
+--------+--------+--------+---------+--------+-----------+---------+----------+
 18-byte header, then a bit-packed body
```

Flags from dollsMP: `COMPRESSED 0x01`, `DELTA 0x02`, `RELIABLE 0x04`,
`RETRANSMIT 0x08`, `FRAG_LAST 0x10`.

Packet kinds (dollsMP numbering extended for this game):

| Kind | Name | Direction | Reliability |
|---|---|---|---|
| 1 | `Hello` | C→H | reliable |
| 2 | `Welcome` | H→C | reliable |
| 3/4 | `Ping`/`Pong` | both | unreliable |
| 10 | `PlayerState` | both | unreliable, 20 Hz |
| 11 | `PlayerLoadout` | both | reliable |
| 12 | `CombatEvent` | both | reliable |
| 13 | `Projectile` | both | unreliable |
| 15 | `EnemyState` | H→C | unreliable, 10 Hz |
| 16 | `WorldEvent` | H→C | reliable, at-most-once |
| 17 | `Chat` | both | reliable |
| 40 | `FactDelta` | H→C | reliable |
| 41 | `QuestState` | H→C | reliable |
| 42 | `ObjectState` | H→C | reliable |
| 43 | `ClockState` | H→C | reliable |
| 44 | `Cutscene` | H→C | reliable |
| 45 | `Snapshot` | H→C | reliable, fragmented |
| 20 | `Delta` | H→C | unreliable against a baseline |
| 22 | `DesyncReport` | C→H | reliable |
| 23 | `ResyncRequest` | H→C | reliable |
| 30 | `Bye` | both | reliable |

`WorldEvent`, `FactDelta`, `ObjectState`, `QuestState` and `Cutscene` all share the same
delivery contract: **at-most-once by event id, journalled, replayed to late joiners.**

---

## 3. Per-system design

### 3.1 Facts — `FactDB`

**Hook (host):**
```
FactDB.SetFact(string, bool)   @ 0x3A4440
FactDB.SetFact(string, int)    @ 0x3A46C0
FactDB.ChangeOrCreateIntFact(string, int) @ 0x3A3580
```
**Hook (client):** the same three, guarded by `ApplyingRemote`.

**Outbound (host):** after the original runs, diff against the last-sent value and queue
`FactDelta{ entries: [(key, kind, value)] }`. Idempotence check prevents echo storms when
a trigger re-sets an already-set fact.

**Inbound (client):** set `ApplyingRemote = true`, call `SetFact`/`SetFact(int)`, clear.
The guard makes the client's own hook drop the change instead of forwarding it (there is
no client→host fact path at all).

**Late join / resync:** `GetBoolFacts()` @ `0x365F00` + `GetIntFacts()` @ `0x352390`
give the complete map; send as one `Snapshot` fragment set.

**Why not `GetFactsSaveData`/`LoadFacts`?** `LoadFacts` replaces the dictionary wholesale
and would suppress per-key events; the peer's triggers would never fire. Use per-key
`SetFact`.

**Scale:** ~120 fact keys are referenced directly by doors/triggers/NPCs in the shipped
assets (`work/manifest/fact_keys.json`); the runtime set is larger because task stages add
their own. A full snapshot is a few KB.

### 3.2 Quests and tasks — `DelusionQuest` / `DelusionTasks`

**Hook (host):**
```
DelusionTasks.TaskStageTrigger(string taskID, int stageID, bool hasPopup) @ 0x415E90
DelusionQuest.ActivateQuest(string ID)  @ 0x489820
DelusionQuest.NotifyQuestFinished(string ID) @ 0x48A0D0
```
`TaskStageTrigger` returns `bool`, so it is a clean postfix hook: replicate only when it
returns `true` (the stage actually advanced).

**Do not replicate the reward side.** `TaskStageTrigger` → `StartTask`/`UpdateTask` →
`SetTaskFacts`, `CheckKeyItems`, `StoreItem`, `RemoveItem`, `CalculateItemsAndRewards`,
`ChangeFactions`, `CheckIfTaskIsAchievement`. If the peer re-runs that, both players get
glimmers, coins and items twice.

**Client application:** apply the *facts* the stage sets (they arrive via `FactDelta`) and
set the journal entry from the authoritative state:
```
DelusionTasks.GetPersistentTaskData()          @ 0x414130  -> List<{id, stage}>
DelusionTasks.RestoreFromPersistentTaskData(x) @ 0x4152D0
```
`PersistentTaskData` is exactly `{ string id; int stage; }` — a perfect wire type. Use it
for both late join and drift repair.

**Quest completion** (`QuestBase.CheckCompletion` @ 0x493990) is derived from facts on
both sides, so it needs no dedicated message once facts agree.

### 3.3 World objects — `ISaveable`

**Model:** every `ISaveable` in the scene is a replicable object.

```
interface ISaveable { object CaptureState(); void RestoreState(object state); }
SaveableObject.CaptureState()  @ 0x42D620
SaveableObject.RestoreState()  @ 0x42DD80
```

**Do not ship `object`.** `CaptureState()` returns arbitrary engine types. Instead define a
typed wire variant per component class, populated from the component's own fields:

| Component | Wire payload |
|---|---|
| `DoorsDelusion` | `{ DoorState st8; bool staysOpen; }` |
| `DoorsUltimate` | `{ DoorState st8; bool loreComplete; bool factIsSet; bool smashOnly; }` |
| `DelusionLift` | `{ LiftPosition liftPosition; bool isMoving; float timeLeft; Vector3 currentMovePos; Vector3 moveDestination; bool factIsSet; }` |
| `ItemPickup` | `{ bool taken; RespawnTimer respawn; }` |
| `ItemChest` | `{ bool opened; }` |
| `ShopSpeaker` | `{ List<ItemInstance> shopInventory; int lastDayRestocked; }` |
| `CompletableObject` | `{ bool isComplete; }` |
| `EnemySpawner` | `{ bool spawnerActive; RespawnTimer respawn; bool killed; }` |

**Change detection:** poll the small set of state fields each frame on the host
(cheap — a few thousand objects) or hook the setters. Polling is simpler and robust to the
many private field writes; a 100 ms scan of ~4 500 objects is negligible.

**Ordering:** `ObjectState{ objectKey, payload }` with a monotonically increasing event id.
The client applies in id order; anything older than the last applied id is dropped.

**Loot is per player (as built).** `ItemChest` and loose `ItemPickup` objects are excluded from
the mirror and from the join adoption: each player loots each one once, from their own save, so a
quest item one player picks up is still there for the other. Breakable crates
(`SmashableSpawner`) stay shared; the partner's copy of a smash rolls its own loot.

### 3.4 Doors

`DoorsUltimate.st8` (`DoorState`: `unlocked 0, locked 1, open 2, smashed 3, midClose 4,
midOpen 5, midPick 6`) is the whole state machine, and `saveData` (`SaveData`) persists it.
Replicate `st8` only, at the `unlocked→open`, `locked→unlocked` and `*→smashed` edges.

A door may also set a fact (`factToSet` 0x70); that arrives through `FactDelta`, so do not
duplicate it in the door payload.

**Enemy interaction:** `EnemyController.canOpenDoors` (0x60) and `traversingDoor` (0x1B9)
mean a host-side enemy may open a door. Because doors are host-authoritative too, this
works without extra messages.

### 3.5 Lifts, platforms and moving machinery

Types: `DelusionLift` (62 in assets), `DelusionLiftLever` (182), `DelusionLever` (141),
`MovingFactory` (1), `VelaFortressLift` (1), `MovingSkiff`, `Gatebridge`.

**Design: start/stop events, not transform streams.**

```
LiftEvent{ objectKey, LiftPosition target, bool started, float duration }
```

Each client runs its own animation from the shared state. Two extra rules:

- A client that joins mid-ride receives `currentMovePos`/`moveDestination`/`timeLeft` once
  and snaps — never interpolates toward it.
- A player standing on a platform is carried by *their own* client's physics. Do not
  replicate passenger transforms; the platform motion is identical on both sides, so the
  local character controller resolves it naturally.

This is the standard fix for "platform rubber-banding" in Unity co-op mods.

### 3.6 Enemies

**Ownership is per spawner.** `EnemySpawner` (918 in assets) already implements
`ISaveable` and owns kill state (`savesKillData`, `StoreKillData` @ 0x45C6B0,
`EnemyKilled` @ 0x45B330, `RespawnEnemy` @ 0x45BD80, `RespawnTimer`).

```
EnemyState{ spawnerKey, alive, currentHealth, maxHealth, combatMode, hostileType,
            position, yaw, animState, animSpeed }
EnemyEvent{ spawnerKey, kind: Killed|Spawned|Provoked|Respawned }
```

- Host runs all `EnemyController` AI (`0x44D270 Provoke`, `0x44CEF0 OnDeath`).
- Clients make their local `EnemyController` a puppet: disable its `NavMeshAgent`, drive
  the transform and `EnemyAnimator` from `EnemyState`, and block damage application.
- `currentHealth` is on `EnemyStats` at **0x24** (a backing field for `currentHealth`).

Because bosses are ordinary `EnemyController`s (see §4.5 of the RE doc), boss fights need
no separate code path — only the scripted cutscene triggers around them.

**Summons (as built, `SummonSync`).** `DelusionPlayerSpell.SpellSummon` instantiates a prefab
(`summon_Skeleton(Clone)` …: an `EnemySpawner` with an `EnemyController`) at the scene root; it
has no save identity and the same hierarchy key on both machines, so summons never enter the
enemy stream. The caster streams `SummonState{ id, prefab, pos, yaw, hp }` at 5 Hz; the partner
shows a ghost built from the same prefab with the AI, spawner, `SummonSelfDamage`,
`SummonMonitor`, `SkeletonFacts`, `QuestObject` and colliders removed and its `EnemyAnimator`
woken. A summon's hit on an enemy the partner simulates is forwarded like the caster's own hit
(and counts as the caster's threat), so the enemy moves to the caster's machine.

**Charm (as built).** `EnemyStats.ApplyCharm` rolls a die; `GetCharmed` sets `isCharmed` (a
countdown) and puts the enemy in the local player's single charm slot
(`DelusionPlayerSpell.CharmEnemy`). The roll happens only on the hitter's machine: forwarded hits
carry charm power 0, and `CombatEvent{Charm, key, timer, buff}` / `{Uncharm, key}` move the
state. The host pins a charmed enemy to the charmer's machine until the charm ends.

### 3.7 Player puppets

Each client sends, at 20 Hz:
```
PlayerState{ seq, pos(quantised 1cm), yaw(0.1°), pitch(0.1°), moveState, isGrounded,
             isCrouching, isSprinting, isDodging, health, mana, stamina, worldState }
```
Peer renders a puppet built from the player prefab:
- clone the host's `DelusionManager.player` hierarchy minus camera/audio listener,
- disable its `PlayerMove`, `PlayerLook`, `CharacterController`, `DelusionPlayerActions`
  and any damage-receiving component,
- drive its `Animator` from `moveState` + speed,
- interpolation buffer ~100 ms, extrapolate up to 250 ms with a freeze beyond that.

`DelusionPlayerActions.moveState`/`lookState`/`playerState` and `PlayerMove.isGrounded`
give the animation inputs; do not attempt to replicate the character controller.

### 3.8 Combat

Melee and projectiles are the jitter-prone part. Keep it simple:
- Damage is computed by whoever owns the target (host for enemies, each client for
  themselves), never by the attacker.
- Send `CombatEvent{ kind: Attack|Cast|Block|Dodge, weaponId, dir }` for animation only.
- Projectiles (`Arrow`, `MagicMissile`, pools `ArrowPool`/`MissilePool`/`ThrowablePool`)
  are cosmetic beyond spawn: send `Projectile{ id, spawnPos, dir, speed }` and simulate
  locally; damage resolves on the target owner.

### 3.9 Dialogue and dice rolls

*As built, conversations are shared (`DialogueSync`): the driver streams node changes, choices,
charm rolls and the end; the spectator follows with the game's own controller. A node with
several pages turns them inside the node (`SkipToNextTreeNode` bumps `m_extendedTextID`), so
the driver also sends each page. The original plan below kept dialogue local.*

Local only. The `DialogueTree` / `DelusionDialogueController` / `DelusionDiceRoll` UI stays
per-client. Only effects cross:
- `EDialogueActionType.SetFact` (1) → already covered by `FactDelta`,
- `CallOnPlayer` (2) / `CallOnSpeaker` (3) → run locally,
- quest stage changes on `NPCBase` (`questStage` 0xA0, `questStageAlt` 0xA4,
  `questStage3` 0xA8) → `TaskStageTrigger` hook covers it.

### 3.10 Scene, region and transition

- Region streaming is **per-client**, driven by `RegionDetector` around the local player.
  Never force it.
- Send `{ masterRegion, subRegion }` in `PlayerState`; when two players are in different
  master regions, hide the remote puppet and skip its updates.
- Story-mandated moves must move both: `DelusionManager.RepositionPlayer` @ 0x3E2AE0,
  `EPTransition*`, `EPTeleportable`, `TransitAcademyPortal`, `TransitCrystalPoint`.
  Host sends `Cutscene{ kind: Teleport, scene, spawnPoint }`; the client calls
  `RepositionPlayer` with the corresponding `SpawnPointManager` transform.
- `DelusionSceneControl` `currentSceneName` (0x28) is sent so the client can verify it is
  in the same region before applying region-scoped events.

### 3.11 Clock and rest — `DelusionDayNight`

Host owns `_starTimeSeconds` (0x20), `typeOfDay` (0x5C) and `CharacterStats.daysPlayed`
(0x28). Broadcast `ClockState` on every `DayPassed` (0x38) and `OnRest` (0x48), plus a
low-rate tick.

This is not cosmetic: `EnemySpawner.PlayerRest`/`DayPassed` and `ItemPickup.PlayerRest`
respawn world content on day changes. Diverged clocks silently desync spawners.

### 3.12 Airship — `AirshipController`

Shared vehicle, host-simulated. Replicate `modelRoot` transform + velocity + `anchor`
docking state. The passenger's camera stays local (`AirshipFirstPersonViewpoint`).
`AirshipUpgradeSystem` / `AirshipPartPivot` upgrades are world state → `ObjectState`.

### 3.13 Cutscenes and cinematics

`CinematicsManager` holds `PlayableDirector`s (`fortressScene` 0x30, `cutterLiftoff` 0x38,
`angelAwaken` 0x40, `velaKillPlayer` 0x48). Send `Cutscene{ directorId, play|stop }`; each
client plays its own local `PlayableDirector` at the same time. Never let a client start
these independently.

`DelusionManager.EnableWorldCinematics(bool,bool,bool)` @ 0x3DF420 should be set on both
sides while a shared cutscene plays so region loading is blocked consistently.

---

## 4. Loop suppression

The single most important mechanism. Three layers:

```csharp
static int _applyDepth;
public static bool ApplyingRemote => _applyDepth > 0;

// every outbound hook:
if (ApplyingRemote) return;   // do not forward
// inbound application:
_applyDepth++; try { Apply(); } finally { _applyDepth--; }
```

Additional guards:

- **Value idempotence** — a fact is only sent when its value differs from the last value
  sent or observed. Many triggers re-set facts to the same value every frame.
- **Event ids** — every `WorldEvent` carries `(objectKey, monotonicId)`; the client drops
  duplicates.
- **Reward suppression** — on the client, `DelusionTasks.StartTask`/`UpdateTask` and
  `CalculateItemsAndRewards` are postfixed-out when applying remote stages.
- **Spawner double-kill** — `EnemySpawner.EnemyKilled` is only executed on the host;
  clients take the resulting state.

---

## 5. Failure handling

| Symptom | Detection | Repair |
|---|---|---|
| Fact map drift | periodic fact-map hash in `Ping` | host sends full `Snapshot` |
| Object drift | per-object last-event id in ping payload | `ResyncRequest` for that key |
| Missing enemy | spawner alive-state mismatch | client re-runs `RespawnEnemy` |
| Wrong scene | `currentSceneName` mismatch | client told to `RepositionPlayer` |
| Build mismatch | content-table hash at handshake | refuse connection, clear error |

Never paper over a mismatch silently: a co-op desync in this game looks exactly like a
game bug (quest stuck, door won't open), which is the most expensive kind to diagnose.

---

## 6. What is deliberately *not* synced

- Dialogue UI, dice-roll animation, book/note screens, char-creation panels.
- Camera, FOV, forced-look, screenshot mode.
- Each player's inventory, stats, skills, faction reputation and achievements.
- Per-player cutscene subtitles and audio.
- `DistantObject`, `RotationConstant`, `TextureScroll`, `DisableSnow`,
  `AtmosphereAudio*` — pure local cosmetics (thousands of them; syncing would be waste).

That last row matters: the census shows 5 953 `DistantObject`, 6 293 `TextureScroll`,
5 650 `RotationConstant` and 1 736 `DisableSnow` components. None of them carry gameplay
state.
