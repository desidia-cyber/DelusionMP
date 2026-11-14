# DelusionMP  Etwo-player co-op for Dread Delusion

Reverse engineering and co-op implementation for **Dread Delusion** (Lovely Hellplace /
DreadXP, Unity 2022.3.60f1, IL2CPP), built on the methodology and tooling in
[cranebeaker/coop-anything](https://github.com/cranebeaker/coop-anything) and reusing the
protocol design from the DollsMP co-op mod.

Status: **reverse engineering complete; the co-op core is running and verified live.**
Fact sync, world-state consistency, door replication and remote player puppets have been
observed in a real two-instance session; enemy encounters, quest-journal advancement and
cutscenes are wired but not yet exercised. See `docs/04-VERIFICATION.md` for exactly what
was proven and how to reproduce it, and `docs/03-IMPLEMENTATION-PLAN.md` for the remaining
work.

---

## What is here

| Path | What |
|---|---|
| `docs/01-REVERSE-ENGINEERING.md` | Every sync surface with types, field offsets and method RVAs, read out of the game's own metadata |
| `docs/02-SYNC-DESIGN.md` | Authority model, wire protocol, per-system replication rules |
| `docs/03-IMPLEMENTATION-PLAN.md` | Phased build plan with exit criteria |
| `docs/04-VERIFICATION.md` | What was proven in a live two-instance session, and the rig procedure |
| `mod/src/` | BepInEx 6 IL2CPP plugin: protocol, loop guard, event journal, fact/quest/world/enemy/player sync |
| `tools/rig.ps1` | Two-instance invisible rig: private desktops, muted, isolated saves |
| `tools/session.ps1` | Brings up a full two-player session and reports state |
| `mod/typecheck/` | Compile harness (stubs) so the source typechecks without the game |
| `tools/` | Dump parser, type query CLI, asset and database extractors, manifest builder |
| `work/dump/` | Il2CppDumper output + `index.json` (10 787 types, 81 441 methods) |
| `work/data/` | Content extracted from assets (quest DB, initial facts, ~75 000 MonoBehaviours) |
| `work/manifest/` | Per-region sync-object manifests, fact-key vocabulary, doors/lifts/enemies |
| `reference/coop-anything/` | The upstream playbook this project follows (git clone) |
| `third_party/Il2CppDumper/` | Source of the dumper used (MIT) |

---

## The findings that decide the design

**The game has no netcode**, but it already has a generic keyed state system: `DelusionSave`
captures a `Dictionary<string, object>` from every `ISaveable` component in the scene, and
`SaveableObject.uniqueIdentifier` gives each one a stable name (4 754 non-empty, 4 493
distinct across 4 804 components). That identity table is what co-op keys on.

**Story state is one small map.** `FactDB` holds `Dictionary<string,bool>` and
`Dictionary<string,int>`; almost everything  Edoors, spawners, triggers, quest stages,
cutscenes  Ekeys off it. Syncing facts correctly is most of "story co-op".

**Quests have one entry point and one serialisable state.** `DelusionTasks.TaskStageTrigger`
(`0x415E90`) advances a task and returns whether it did; `GetPersistentTaskData` /
`RestoreFromPersistentTaskData` move `{id, stage}` pairs. The trick is to replicate stages
*without* re-running the reward pipeline, which grants glimmers, coins and items.

**Moving platforms are state machines, not transforms.** `DelusionLift.liftPosition`,
`MovingFactory.currentMoveState`, `DoorsUltimate.st8` (`DoorState`). Replicate transitions
and let each client animate locally; that removes platform rubber-banding.

**There is no `Boss` type.** Bosses are ordinary `EnemyController`s with a high
`CombatantLevel` inside scripted scenes, so enemy sync covers them and only cutscene
triggers need separate handling.

**Move private state with `Traverse`, not direct access.** Several fields the mod needs are
private or have private setters (`DoorsUltimate.st8`, `DelusionLift.isMoving`,
`EnemyController.agent`, `DelusionDayNight._starTimeSeconds`). The code uses Harmony
`Traverse` for those and direct access for public fields.

---

## Reproducing the analysis

```powershell
# 1. dump the game (needs the game installed)
work\il2cppdumper\Il2CppDumper.exe work\GameAssembly.dll work\metadata\global-metadata.dat work\dump

# 2. index it
python tools\parse_dump.py work\dump\dump.cs work\dump\index.json

# 3. query
python tools\q.py types                        # every game type
python tools\q.py members DelusionSave         # fields + methods + RVAs
python tools\q.py find "fact"                  # search field/method names

# 4. extract game content from the shipped assets
python tools\extract_databases.py              # -> work/data/*.json
python tools\build_manifest.py                 # -> work/manifest/*.json

# 5. typecheck the mod source (no game needed)
dotnet build mod\typecheck\FullTypecheck.csproj
```

`tools/extract_databases.py` needs `UnityPy` and `TypeTreeGeneratorAPI`. With no type trees
in the shipping build, it generates them from the IL2CPP image; the generator wants the
bare assembly name (`Assembly-CSharp`, not `.dll`), which is patched in the script.

---

## Numbers from the build

| | |
|---|---|
| Game types (Assembly-CSharp) | 962 |
| Total indexed types / methods | 10 787 / 81 441 |
| MonoBehaviours scanned | 75 000 across 478 classes |
| Build scenes / master regions | 26 / 25 |
| `SaveableObject` (with identity) | 4 804 (4 754) |
| Doors (`DoorsDelusion` + `DoorsUltimate` + vert + bridge) | 947 |
| Lifts / levers / movers | 245 (+ 141 levers) |
| Enemy spawners | 918 |
| Item pickups / chests / plants | 2 285 |
| `QuestObject` components | 5 951 |
| `DialogueGraphNode` | 8 176 |

---

## Not yet verified

Be honest about these before shipping anything:

1. **BepInEx 6 IL2CPP against metadata v31**  Ethe whole plan depends on it loading.
   Phase 0 exists solely to prove it.
2. **`FactDB.SetFact` event fan-out at runtime**  Ethe hook assumes a postfix on the two
   `SetFact` overloads catches every write. A live probe should confirm no writer bypasses it.
3. **Per-`ISaveable` payload mapping**  E`CaptureState()` returns `object`; each component
   still needs its typed wire variant written (the design lists the common ones).
4. **The 50 `SaveableObject`s without identifiers**  Eneed the runtime census to see whether
   hierarchy paths are stable between two copies.
5. **Real `CharacterStats` ownership**  E`DelusionStats.characterStats` is assumed canonical;
   only a running game can confirm there is not a second instance per save slot.

---

## Credits

- Methodology and QA tooling: [coop-anything](https://github.com/cranebeaker/coop-anything) (MIT).
- Protocol framing and reliability design: the DollsMP co-op mod (`crates/dollsmp-core`).
- IL2CPP dump: [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) (MIT).
- Asset reading: [UnityPy](https://github.com/K0lb3/UnityPy) + TypeTreeGeneratorAPI.

No game files or assets are committed here; `work/` holds analysis output only and is
ignored by git.
