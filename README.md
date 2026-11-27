# DelusionMP — two-player co-op for Dread Delusion

Reverse engineering and co-op implementation for **Dread Delusion** (Lovely Hellplace /
DreadXP, Unity 2022.3.60f1, IL2CPP), built on the methodology and tooling in
[cranebeaker/coop-anything](https://github.com/cranebeaker/coop-anything) and reusing the
protocol design from the DollsMP co-op mod.

Status: **playable two-player co-op, verified on a two-instance rig** (story, quests, world
objects, enemies and bosses, summons and charm, deaths, airship, lifts, dialogue, cutscenes,
both endings (banish and merge) on all three karma routes, either player driving, and the credits, text chat and proximity voice). It has not yet been played start to finish by two
people. `docs/04-VERIFICATION.md` lists what was proven and how; the known limitations are at
its end.

## Playing

1. Build the package once: `./tools/package.ps1` writes `dist/DelusionMP-Coop.zip`
   (**zero `.exe` files** — BepInEx 6 IL2CPP + `DelusionMP.dll` only). Both players use the
   **same** package; a different mod version is refused with an in-game note.
2. Each player unzips it directly into `<Dread Delusion>\windows_content\` and launches
   Dread Delusion normally (from Steam or directly).
3. Click **Co-op** in the native Title Screen menu: configure your name, Steam (password lobby
   or Steam friends) or LAN, session rules, avatar, voice chat, and co-op save backups, then
   click **Host Session** or **Join Session**. Co-op saves are isolated in `Dread Delusion MP1`
   by default so your single-player saves remain untouched.
4. The host loads or starts a game; the joiner loads any save and is brought to the host's
   world, story and journal on joining.

In game: **T** opens text chat (Enter sends, Esc cancels); voice is push-to-talk on **V** or
open-mic, configurable live in **Co-op -> Player & Voice** (accessible from both the Title
Screen and Pause Menu). **F8** puts you next to your partner. Chests and loose pickups are each
player's own: both of you loot each one. Session rules (death penalties, respawn at the partner,
exclusive shop items) are the host's and are sent to the joiner.

---

## What is here

| Path | What |
|---|---|
| `docs/01-REVERSE-ENGINEERING.md` | Every sync surface with types, field offsets and method RVAs, read out of the game's own metadata |
| `docs/02-SYNC-DESIGN.md` | Authority model, wire protocol, per-system replication rules |
| `docs/03-IMPLEMENTATION-PLAN.md` | Phased build plan with exit criteria |
| `docs/04-VERIFICATION.md` | What was proven in a live two-instance session, and the rig procedure |
| `mod/src/` | BepInEx 6 IL2CPP plugin: protocol, loop guard, event journal, fact/quest/world/enemy/player sync |
| `crates/delusionmp-launcher*` | The launcher (window and command-line): co-op game copy, LAN/Steam, settings |
| `tools/package.ps1` | Builds the shareable package `dist/DelusionMP-Coop.zip` |
| `tools/rig.ps1` | Two-instance invisible rig: private desktops, muted, isolated saves (`-Visible` for manual checks) |
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
`Dictionary<string,int>`; almost everything —doors, spawners, triggers, quest stages,
cutscenes —keys off it. Syncing facts correctly is most of "story co-op".

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

- A real playthrough by two people from start to finish (late-game story beats and bosses were
  reached on the rig through quest stages and travel commands; the ending was run through the
  Angel with mixed region outcomes, not every combination of them).

---

## Credits

- Methodology and QA tooling: [coop-anything](https://github.com/cranebeaker/coop-anything) (MIT).
- Protocol framing and reliability design: the DollsMP co-op mod (`crates/dollsmp-core`).
- IL2CPP dump: [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) (MIT).
- Asset reading: [UnityPy](https://github.com/K0lb3/UnityPy) + TypeTreeGeneratorAPI.

No game files or assets are committed here; `work/` holds analysis output only and is
ignored by git.
