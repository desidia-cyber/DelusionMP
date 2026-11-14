# DelusionMP — Verified Status (rig run)

This file records **what was actually observed in a running game**, not what the design
intends. Everything below was produced by two real Dread Delusion processes on one PC.

## Rig

| | |
|---|---|
| Game | `rig/instance1/game`, `rig/instance2/game` (copies of `windows_content`) |
| Loader | BepInEx 6 IL2CPP `6.0.0-be.793` + Unity 2022.3.60 base libraries |
| Isolation | one private Windows desktop per instance (`DelusionMPRig1/2`) |
| Visibility | windows never appear on the owner's desktop and cannot take focus |
| Audio | `tools/quiet-watch.ps1` mutes each process's Core Audio sessions |
| Detachment | launched through `explorer.exe` + `launch-game.ps1`; direct children of the harness are killed when a tool call ends |
| Saves | `DelusionSave` `persistentDataPath` is postfixed to `Dread Delusion MP<n>` per instance |

Reproduce:

```powershell
./tools/rig.ps1 -Action setup -Count 2      # copy the game + install BepInEx
./tools/session.ps1                         # stop, build, deploy, launch, load save 1, resync
./tools/rig.ps1 -Action stop                # stop everything
```

`tools/session.ps1` drives the title screen through the game's own UI buttons
(`onClick`), because injected input cannot reach a window on a non-active desktop.

## Verified

1. **BepInEx 6 IL2CPP loads on metadata v31.** Interop generation produced 97 assemblies;
   the chainloader completes with the plugin loaded and 6 Harmony patches applied.
2. **Two instances coexist invisibly and silently**, each on its own desktop, each muted.
3. **Handshake works**: protocol version, mod version and `GameAssembly.dll` SHA-256 are
   exchanged and checked; host and client both report `connected=True`.
4. **Facts are host-authoritative and converge.**
   - Both peers load the same save: `bools=53 ints=19`, identical fingerprint.
   - Host `setfact COOP_FINAL true` → client follows to `bools=54 ints=19`, fingerprint
     identical (`9AC38F510CFFE47D`).
   - Connected clients suppress their own `FactDB.SetFact` writes, so local scene triggers
     cannot diverge the story.
5. **World state is consistent.** `statehash` (FNV-1a over every named saveable's encoded
   state, sorted by identifier) is identical on both peers: `count=725`,
   `8D73E3FC5FA57154`.
6. **Door state replicates.** Host `opendoor 20e90114…` → host `st8=open`, client
   `st8=open` within one poll. The client applies through the game's own
   `DoorsUltimate.UnlockDoor`, so its animator/navmesh/save record stay consistent.
7. **Player puppets exist and are positioned.** Both sides report a live puppet at
   `291,89,-681` — each renders the other player.

## Known gaps (not yet verified)

- **Enemy replication** (`EnemySync`) compiles and runs but has not been exercised against
  a live encounter; the spawner/kill path needs a real fight.
- **Quest journal apply** (`RestoreFromPersistentTaskData`) is wired but not yet observed
  advancing on the client.
- **Generic saveable mirror** applies ~3 300 states per session, but many components
  return `null` from `CaptureState()` on the client before their own `Awake/Start` builds
  the state object. Doors and lifts are covered by the typed fast-path instead; other
  component families (chests, shops, quest objects) need the same treatment or a way to
  force client-side capture initialisation.
- **Cutscenes / teleports / airship** are designed but not implemented end to end.

## Reproduce a check

```powershell
# host opens a door, client should follow
./tools/session.ps1
# then, with the session up:
#   write "opendoor <identifier>" to rig/instance1/game/BepInEx/ddmp_cmd.txt
#   write "doors"                to both instances' ddmp_cmd.txt
```

Control-channel commands live in `mod/src/Core/Cmd.cs`: `status`, `facts`, `setfact`,
`doors`, `opendoor`, `statehash`, `mirrordebug`, `keys`, `probe`, `menu`, `click`,
`demo1/2`, `newgame`, `copysave`, `loadsave`, `resync`, `mirror`, `tp`, `quit`.
