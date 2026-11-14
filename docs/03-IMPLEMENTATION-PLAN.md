# Dread Delusion — Implementation Plan

Phased plan, ordered so that each phase produces something playable and testable, and so
that the risky unknowns are hit early rather than after weeks of work.

---

## Phase 0 — Load a plugin at all (highest risk first)

**Goal:** BepInEx 6 IL2CPP loads into Dread Delusion and a Harmony patch fires.

Why first: the build is Unity 2022.3.60f1 with metadata **v31**. BepInEx 6 IL2CPP support
for v31 is version-sensitive; a mismatch here costs days and invalidates everything else.

Steps:
1. Back up the game folder (copy `windows_content` to a scratch directory).
2. Install BepInEx 6 bleeding-edge IL2CPP (x64) into the game root.
3. Launch once so the chainloader generates `BepInEx/interop/*.dll`.
4. Build `mod/DelusionMP.csproj` against those interop assemblies.
5. Confirm in `BepInEx/LogOutput.log`: plugin loaded, patch count > 0, `GameAssembly
   SHA-256` logged.

**Exit criteria:** the log shows `DelusionMP 0.1.0 loading` and no IL2CPP resolution
errors. If interop generation fails, stop and fix that — nothing downstream can work.

---

## Phase 1 — Census and identity

**Goal:** a live object table and the content hash.

1. Add a census command (F9 already dumps facts; extend it) that, per loaded region,
   walks `SaveableObject` and writes `{scene, uniqueIdentifier, hierarchyPath, components}`
   to disk.
2. Compare against `work/manifest/region_objects.json` (build-time expectation) to find
   objects that only exist at runtime, and identifiers that are duplicated.
3. Confirm the object-table hash is identical between two game copies.

**Exit criteria:** identical hashes on two instances in the same region.

This is where any mismatch between the shipped assets and runtime behaviour shows up, so do
it before writing sync code.

---

## Phase 2 — Two instances on one PC

**Goal:** two copies of the game running simultaneously, connected over loopback.

Needed because every later test is a two-client test.

1. Make the game run twice: Unity games usually just work if the launcher is bypassed;
   if not, use the `gameslot` pattern from coop-anything (a per-instance copy or a
   junction plus a separate save directory).
2. `UdpTransport` on two ports, host/join by command line or config.
3. Handshake green: same `GameAssemblyHash`, same `TableHash`, same mod version.

**Exit criteria:** both logs show `connected to host` and a snapshot transfer, in a loaded
save, with no crash for 5 minutes.

---

## Phase 3 — Story facts

**Goal:** the host's story flags appear on the client, and quests advance in step.

1. Enable `FactSync` (already scaffolded): host hooks `FactDB.SetFact(bool/int)`,
   client applies under `LoopGuard`.
2. Verify against a scripted run: take a quest, complete an objective, open a door that
   needs a fact, and diff the full `GetBoolFacts()`/`GetIntFacts()` maps on both clients.
3. Add the fingerprint check to the ping (already scaffolded) and confirm drift triggers a
   snapshot rather than a stuck quest.

**Exit criteria:** fact maps byte-identical after a 10-minute scripted session.

This phase alone is "story co-op" and is the single biggest win per unit of work.

---

## Phase 4 — Quest journal

**Goal:** the journal (task list and stages) matches without duplicating rewards.

1. `TaskStageTrigger` postfix → replicate `(taskID, stage)`.
2. Client applies through `RestoreFromPersistentTaskData`, never through
   `TaskStageTrigger` (reward duplication).
3. Verify glimmer/coin/item counts do **not** double.

**Exit criteria:** journal identical; no duplicate rewards on the client.

---

## Phase 5 — World objects

**Goal:** doors, lifts, chests, pickups and shop stock agree.

1. `WorldObjectSync` poll (100 ms) on the host; ordered `ObjectState` events.
2. Add the remaining `ISaveable` types as typed payloads: `ItemChest`, `ShopSpeaker`,
   `CompletableObject`, `PlantPickup`, `SimpleTaskTrigger`.
3. Replay missed events from the journal to a late joiner.

**Exit criteria:** open/smash/unlock every door type; ride every lift; take a pickup; both
clients agree with no double-grant.

---

## Phase 6 — Enemies and bosses

**Goal:** one enemy, one death, one loot drop.

1. Host runs AI; client puppets (`NavMeshAgent` disabled, transform driven).
2. `EnemyStats.currentHealth`, `combatMode`, `hostileType` replicated at 10 Hz.
3. `EnemySpawner` kill events at-most-once; respawn follows the shared clock.
4. Bosses need no extra code — only the cutscene triggers around them.

**Exit criteria:** a full enemy encounter, including a boss arena, killed once on both
clients with one loot drop.

---

## Phase 7 — Puppets and combat

**Goal:** you can see and fight alongside the other player.

1. Puppet built from the player hierarchy, simulation/damage components stripped.
2. 20 Hz transform + move-state, ~100 ms interpolation, freeze on silence.
3. Projectiles cosmetic; damage resolved by the target owner.
4. Name tag and vitals bar on the puppet.

**Exit criteria:** two players in one region, visible to each other, melee and ranged
hitting enemies correctly from both sides.

---

## Phase 8 — Story machinery

**Goal:** cutscenes, teleports, region changes and the clock agree.

1. `Cutscene` messages for `CinematicsManager` directors, played locally by id.
2. Teleports: host `RepositionPlayer` mirrored to the client's `SpawnPointManager` point.
3. `CharWorldState` changes host-owned.
4. Clock and rest (`DelusionDayNight`), so spawner respawn ticks stay aligned.
5. Airship: host-simulated transform + docking.

**Exit criteria:** play a chapter boundary (scene change + cutscene + teleport) with both
players landing where they should.

---

## Phase 9 — QA harness

**Goal:** automated proof instead of "it felt fine".

Adapt the coop-anything tools:

| Tool | Use here |
|---|---|
| `tools/gameslot`, `gamepool` | run two copies on one PC safely |
| `tools/chaos` | loss/reorder between the two clients |
| `tools/flight` | record both sides, find the first divergence |
| `tools/ownership` | enemy/spawner ownership with epochs |
| `tools/bots`, `tools/scenarios` | scripted actions + state diff per step |
| `tools/inspect` | live side-by-side view of both copies |

Concrete suites to build, mirroring the Crime Scene Cleaner set:

- `facts` — scripted fact changes agree and do not echo.
- `doors` — every door type's transitions.
- `lifts` — mid-ride join snaps correctly.
- `enemies` — single death, single loot.
- `journal` — stages agree, rewards do not double.
- `region` — independent streaming, puppet hidden across regions.
- `clock` — rest advances both, respawn ticks align.

---

## Sequencing rationale

Facts and the journal (phases 3–4) deliver the story co-op that players actually ask for,
and they are cheap. Enemies and puppets (6–7) are the most expensive per unit of player
value because of `EnemyController`'s 90 methods and the animation work. Bosses, cutscenes
and the airship (8) depend on the earlier phases being stable, so they come last.

---

## Open decisions

1. **Steam transport vs UDP.** Steam gives zero-config friend joining (the game already
   links `steam_api64.dll`) but cannot be automated in CI. Decision: build `UdpTransport`
   first for the harness, add the Steam transport for release.
2. **One shared save vs two careers.** This plan keeps two careers and shares the world.
   The alternative (host's save mirrored) breaks per-player inventory and stats and is
   rejected.
3. **Who owns enemies in combat.** Host-owned everywhere is simplest and is what the plan
   assumes. Regional handoff (the Gothic 3 / coop-anything ownership pattern) can be added
   if host CPU becomes a problem, but it is a substantial complexity increase.
