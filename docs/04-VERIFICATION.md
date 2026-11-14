# DelusionMP — Verified Status (rig run, 2026-11-14)

What is recorded here was **observed in two running Dread Delusion processes** on the rig,
driven through the mod's control channel. Each claim below says how it was checked.

## Rig

| | |
|---|---|
| Game | `rig/instance1/game` (host), `rig/instance2/game` (client) |
| Loader | BepInEx 6 IL2CPP `6.0.0-be.793` |
| Isolation | one private Windows desktop per instance (`DelusionMPRig1/2`): never visible, never takes focus |
| Audio | muted (`-ddmp-mute` + `tools/quiet-watch.ps1`) |
| Saves | per-instance `persistentDataPath` (`Dread Delusion MP<n>`) |
| Logs | `BepInEx/ddmp_log.txt` (the mod's own log; BepInEx disk logging is off on the rig) |

```powershell
./tools/session.ps1              # stop, deploy, launch both, load save 1, resync, compare
./tools/rig.ps1 -Action stop
```

Commands are written to `rig/instance<N>/game/BepInEx/ddmp_cmd.txt`, results appear in
`ddmp_out.txt`. Proof screenshots are taken in-engine (`res 1280 720`, `hud off`,
`shot <file>`), because the windows live on desktops the owner never sees.

## Verified

| System | Direction | How it was checked | Result |
|---|---|---|---|
| Handshake | — | client log of a full session | 1 connect / 1 join (was 21 connects and 37 snapshot applies); client *restart* not yet tested |
| Story facts | host → client | `setfact COOP_T1_HOST true` | both `bools=55`, identical fingerprint |
| Story facts | client → host | `setfact COOP_T2_CLIENT true`, `setfact COOP_T2_INT 7` | host has both; fingerprints identical |
| Inventory facts | per player | 249 item/coin keys excluded | client coins stay 360 while host's go 360 → 363 |
| Quests | host → client | `taskstage charMentor 20` | client journal `charMentor=20` (replayed through `TaskStageTrigger`) |
| Quests | client → host | `taskstage pwyllShroom 10` | host journal gains `pwyllShroom=10`; quest facts match |
| Doors (`DoorsUltimate`) | both | locked→open, unlocked→open, open→closed | both peers end in the same `st8`; screenshots 07/08 |
| Item pickups | both | each player walks onto a different pickup | pickup inactive on both peers; screenshots 03/04 |
| Chests | host → client | host `PlayerUse()` on a chest | identical `ItemChest` save state on the client |
| Enemies, movement | host → client | 43 puppets, `enemies` on client | puppet within 0–0.7 m of host target, updated every ~0.17 s |
| Enemies, damage | client → host | client `hitenemy 20` | host HP 56 → 16 (same as client) |
| Enemies, kills | both | kill from each side | enemy dead on both; no double death |
| Player avatars | both | face-to-face at 4 m | screenshots 01/02 |
| Whole world | — | `statedump` diff of all 725 saveables | 3 differences, all benign (see below) |

The three remaining `statedump` differences are a continuously moving sky object
(`StarOrbit`, sampled a second apart) and two empty trophy stands (`Null_Item` on one side,
`null` on the other).

### Screenshots (`docs/screenshots/`)

| File | What it shows |
|---|---|
| `proof_01_host_sees_client.png` | host's view: the client's avatar 4 m away |
| `proof_02_client_sees_host.png` | client's view: the host's avatar |
| `proof_03_host_before_client_pickup.png` | host's view: knife on the ground, client avatar behind |
| `proof_04_host_after_client_pickup.png` | same view after the **client** collected the knife: it is gone |
| `proof_07_host_door_before.png` | host's view: closed door |
| `proof_08_host_door_after_client_opened.png` | same view after the **client** used the door: it is open |

## Defects found and fixed in this pass

1. **Generic world mirror was not syncing anything.** A saveable's state is a
   `Dictionary<string, object>`; the codec walked the dictionary's private internals
   (`_buckets`, `_count`) rather than its entries. That meant the old "725 objects match" hash
   only compared counts. The codec now walks dictionaries, lists, arrays, enums and nested game
   objects, and **rebuilds** the state object on the receiver. Rebuilding matters because an
   untouched object captures `null` there.
2. **Untouched objects never synced from the client.** Null captures were skipped before being
   recorded, so a client's first loot counted as "first sight" and was dropped.
3. **Reliability layer.** Acks ignored the ack bitfield, so every reliable packet was resent ~20×
   and applied each time (the snapshot was applied 37 times). There was no dedupe or ordering,
   and sequence comparison broke after 65 536 packets. Now: an exactly-once, in-order reliable
   channel, wrap-safe sequences, retransmit until acked, and session epochs per client process.
4. **Enemy replication never worked.** Batches of 24 enemies (~150-byte keys) exceeded the
   1200-byte datagram and were dropped; path keys were also unresolvable on the client.
5. **Client actions were discarded.** Client fact writes were suppressed, and doors, pickups,
   quests and hits were host-only. Every one now forwards to the host, which applies it and
   re-broadcasts.
6. **Inventory leaked between players.** Item counts are mirrored into the fact map by
   `ChangeItemFact`. Syncing them overwrote the partner's counts and then fought
   `ValidateItemFact`. Those keys are now player-owned.
7. **Doors only replayed `UnlockDoor`.** An unlocked-but-closed door never opened on the peer,
   and close/smash were not handled. Each transition now uses the door's own
   `OpenDoor`/`CloseDoor`/`SmashDoor`. Plain doors and lifts were only writing private fields
   (no animation, no movement); they now use `OpenDoor`/`StartClose` and replay
   `StartLiftMovement`.
8. **Late-join journal never applied.** The interop list was null, and
   `RestoreFromPersistentTaskData` faults natively during load. It is replaced by per-task
   `TaskStageTrigger` replays, deferred until the player is in the world.
9. **Partner avatar vanished during menus/loot windows**, because pose was only streamed while
   `RUNNING`.

## Known gaps

- **Enemy AI is host-driven and host-centred.** Enemies chase and fight the host's player; on
  the client they follow the host's positions but still attack the client locally. An
  encounter far from the host freezes on the client, because the host stops streaming
  enemies it deactivated. The fix is per-encounter ownership (whoever is nearest simulates).
- **Lifts:** movement replication (`StartLiftMovement` hook + `PositionInstant` settle) is
  implemented but **not exercised**. `TryRecallTop/Bot` is refused from a distance, so a ride
  test needs a player on the carriage.
- **Not exercised end to end:** levers/`MovingFactory`, smashables, shops, NPC dialogue choices,
  cutscenes, airship, region/scene transitions with both players in different regions,
  and player death/respawn while the partner is nearby (both deaths were observed and
  `totalPlayerDeaths` synced — that stat should become player-owned).
- **Fact conflicts are last-writer-wins.** Additive counters written at the same moment by both
  players can lose one increment. The 1 Hz fingerprint check repairs drift from the host
  within ~5 s.
- **Avatar is a mannequin** (body, visor, spinning marker), with no animation and no name text:
  `TextMesh` is stripped from this build, and adding `TextMeshPro` at runtime crashes natively.
- **Transport is UDP to a fixed peer address** (LAN/localhost). There is no Steam relay or NAT
  traversal, and no host-loss detection.
- `mod/typecheck/` (the stub compile harness) has not been updated for the new codec. The real
  build against the interop assemblies is the authority.
