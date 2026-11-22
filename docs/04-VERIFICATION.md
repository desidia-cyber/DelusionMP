# DelusionMP — Verified Status (rig runs, 2026-11-14 … 22)

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
| Handshake | — | client log of a full session | 1 connect / 1 join (was 21 connects and 37 snapshot applies) |
| Story facts | host → client | `setfact COOP_T1_HOST true` | both `bools=55`, identical fingerprint |
| Story facts | client → host | `setfact COOP_T2_CLIENT true`, `setfact COOP_T2_INT 7` | host has both; fingerprints identical |
| Inventory facts | per player | 249 item/coin keys excluded | client coins stay 360 while host's go 360 → 363 |
| Quests | host → client | `taskstage charMentor 20` | client journal `charMentor=20` (replayed through `TaskStageTrigger`) |
| Quests | client → host | `taskstage pwyllShroom 10` | host journal gains `pwyllShroom=10`; quest facts match |
| Doors (`DoorsUltimate`) | both | locked→open, unlocked→open, open→closed | both peers end in the same `st8`; screenshots 07/08 |
| Item pickups | both | each player walks onto a different pickup | pickup inactive on both peers; screenshots 03/04. **Superseded in round 5:** loose pickups are now each player's own |
| Chests | host → client | host `PlayerUse()` on a chest | identical `ItemChest` save state on the client. **Superseded in round 5:** chests are now each player's own |
| Enemies, movement | host → client | 43 puppets, `enemies` on client | puppet within 0–0.7 m of host target, updated every ~0.17 s |
| Enemies, damage | client → host | client `hitenemy 20` | host HP 56 → 16 (same as client) |
| Enemies, kills | both | kill from each side | enemy dead on both; no double death |
| Player avatars | both | face-to-face at 4 m | screenshots 01/02 |
| Whole world | — | `statedump` diff of all 725 saveables | 1 difference: the moving sky object (run 2; run 1 had 3) |
| Enemy ownership | both | client next to goblins 110 m from the host | client simulates them (`puppet=False`), host shows puppets within 0.1–0.3 m |
| Enemy hits/kills on the partner's enemies | host → client | host hits a client-owned goblin | hit forwarded and applied once (HP 36 on both); kill replicated once |
| Shared dialogue | both | host `talk gatebridge`, `next`, `choose 1`, `enddlg` | client shows the same node, text and choices at every step; client's own `next`/`choose` ignored; client back to `RUNNING` at the end; screenshots 16/17 |
| Dialogue point of view | host → client | client 34 m away from the talk | client camera moves to the host's eyes facing the NPC, restored afterwards; screenshot 20 |
| Airship, pilot lock | both | both try the wheel | the second request is refused |
| Airship, flight + passenger | both directions | host flies 55 m / +25 m with the client on deck, then the reverse | passenger parented and carried (ship-local offset unchanged); both see the same ship pose; screenshots 18/19 |
| Airship, landing | both | `land` | anchor drop replicated, lock released, authority back to the host |
| Partner body | both | face to face, forced weapon/spell states, walking | female body (game model) idles, walks, holds the steel/fine sword, thrusts, power-attacks, casts with the spellbook; screenshots 10–15 |
| Story phase (`CharWorldState`) | both | `worldstate 4` on host, `5` on client, `3` on host | 4 and 5 replicate; 3 is refused (never goes backwards) |
| Rest / day | both | `rest 9` on client, `rest 3` on host | both peers advance the day and time of day; the client's rest is not rewound |
| Lifts | both | `liftdown`/`liftup` on 3 lifts from either side | partner's lift runs the same transition and settles at the same height |
| Smashables | host → client | `smashnear` | applied on the client without re-rolling loot |
| Story counter merge | — | code path (`ChangeOrCreateIntFact` deltas) | simultaneous increments are added, not overwritten |
| Combat animations from the start | both | fresh session, `avatar` | `a_UnionWomen` captured ~1 s after entering the world (region 9 loaded and unloaded in the background) |
| Moving-platform glue | host → client | host rides a lift 110 m → 430 m | partner's body stays on the client's own carriage: horizontal ±0.05 m, vertical within ~1 m (was 11–17 m off) |
| Weapons in hand | — | dagger/greatsword/clockwork/void/emberian/bow | grip at the hand, blade along the model's own sword; bows in the left hand; screenshot 22 |
| Partner looks | both | `avmodels`, `avsource`, `outfit` | 3 female models + 7 outfits; chosen per player in the config (`[Avatar] Model/Outfit`), sent to the partner |
| NPC gifts to both | host → client | NPC gives `deedAirship` | client receives it too (sent 1, applied 1) |
| Co-op death, one down | host | `die` with the client alive | down 8 s, gets up next to the partner, 1 stack (max HP/mana/stamina 101 → 91); enemies stay dead |
| Co-op death, party wipe | both | both `die` within the window | wipe detected on both, game's own respawn at own points, "respawn on death" spawners run on the host, stacks cleared |
| Death stacks cleared by rest | client | `bedrest` with 2 stacks | stacks 0, max stats back to 101 |
| Cutscene | host → client | `playcine cutterLiftoff`, client `skipcut` | played on the client (field-keyed director); the client's skip ended it on the host |
| Floor triggers | host → client | host death respawn trigger | replayed on the client without moving its respawn point |
| Story travel | host → client | `travel underlands` | both players arrive at the same spot (1568, -3333, -3905) |
| Shops | both | both buy the single passport at the same moment | each owns a copy (allowed by default, `ExclusiveUniqueShopItems=false`); both shops end with identical stock |
| Traps | host → client | both at a spike trap, host disarms | client's trap goes locked → unlocked and stops being usable (`RayTrigPick` → `Untagged`) |
| Moving factory | host → client | both walk to it (natural region streaming) | client follows the host's 5 Hz pose stream; positions match within 2–4 m (query spacing) |
| Network latency | — | host teleports in a busy region | client sees it within 0.7 s (was > 6 s: the receive loop read only 64 datagrams per frame) |
| Completable statue | host → client | `complete` with its region loaded on both | event applied on the client; the statue object is inactive at this story stage, so no visible change |
| Changes far from the host | client → host | host at Pwyll, client loots a potion in Hallowshire (not loaded on the host), host travels there | host keeps the change (save's loaded-state table + pending list) and applies it when the object loads: potion gone on arrival |
| Clock (time of day) | both | `rest` | real clock values now flow (were read as 0 through reflection that cannot see IL2CPP fields) |
| Performance | both | `perf` over 30 s, same spot | mod cost per second of play ≈ 450–590 ms → 230–290 ms; client factory 14.7 → 0.04 ms/frame, enemies 5–9 → ~1.5–2 ms/frame, no full-world capture in a single frame |
| Far-apart changes across restarts | client → host | client loots in a region the host never loads; both games closed without saving; host relaunched and walks there | the change is in a per-save pending file next to the host's save and is applied on arrival (potion gone) |
| Host-authoritative join | host → client | host killed; client alone opens a door, sets a fact, starts a quest, raises the story phase 3 → 5; host relaunched | client notices the loss (12 s), rejoins, and adopts the host's world: door back to unlocked, fact removed (identical fingerprint), quest removed from the journal, phase back to 3 |
| Charm roll in dialogue | host → client | charm note during a shared talk | the same "Charm challenge succeeded. Needed 6." toast on both; screenshot 21 |

### Round 4 (2026-11-22): bosses, ending, interruptions, launcher

| System | Direction | How it was checked | Result |
|---|---|---|---|
| Boss: Endless Knight (dungeon elite, 600 hp) | both | killed in the save; host rests a day so it respawns; alternating 50-damage hits from each side | respawned on both; HP identical after every hit (560 → -40); death on both; facts fingerprint equal. The old "540 vs 80" was the test hitting two different knights |
| Boss: Angel Spawn (1300 hp) | host → client | dead in the save; host respawns it | client copy respawns too; HP identical through its own armour and healing; death on both |
| Boss: Sculptor | both | `endM_doneLaw` set on the host only | client's Sculptor turns hostile from the fact; HP identical; death on both; facts equal |
| Boss: Vela | both | `cradle` stage 78 on the host only | quest replicates, hostile Vela on both (client simulates); HP identical; her death advances `cradle` to 82 on both. The old "invulnerable" was the passive Vela |
| Orlaith (Underlands story stages) | — | stage 8 scene | active on both; she fights the mechs and dies on both |
| Spiders | both | one spider by exact key | HP identical, kill replicated. The old "HP bounce" was hits landing on different spiders |
| Enemy hand-over mid-fight | both | damaged mech; each player leaves/re-enters the encounter zone in turn | HP kept through every hand-over (was refilled to max: the zone's spawner toggle was mirrored, and a returning enemy was rebuilt at full health) |
| Encounter zones | both | host inside, client outside, then client enters | enemies on both, client copies follow the host's; nothing respawns |
| Joiner's region scenes | client | join, `scenes` | region scene loaded (was empty after the join's region reset until the next border: no local enemies, zones or region objects) |
| Journal on join | host → client | host has a quest the client never started | entry added at the host's stage (`underlands` -1 → 80; the trigger refused it before) |
| Client chest / smashable | client → host | client opens a chest and takes the loot; client smashes a crate | host's chest is empty; host's crate state matches (chests: superseded in round 5, see below) |
| Client sells to a shop | client → host | `sell` | stock pushed; sold items are not part of the shop's saved state, so only the seller's copy lists them |
| Client-driven dialogue | client → host | client talks to the gatebridge guard, picks a choice | host follows every node and choice; both back to RUNNING |
| Client restart mid-session | both | client killed; host sets a fact, smashes, advances a quest; client relaunched | rejoins, facts fingerprint equal, quest stage taken over |
| Host reloads its save mid-session | host → client | `loadsave` with a fact set after the save | both stay connected; the fact the host lost is reset on the client (host's story wins); worlds re-adopted |
| Death on the airship | client | client dies on deck while the host flies | respawns on the deck and is carried on (was left behind: the respawn cleared the parent) |
| Death on a lift | client | client dies while both ride a 320 m lift | respawns beside the host on the carriage, arrives at the top with the host |
| Ending | host → client | `travel cradle`, `ending trigger`, host steps the ending | every node identical (25 steps); both go to the credits (the client stayed in the world before) and both reach the title menu still connected |
| Duplicate save ids | host → client | `mirrortop` | three doors sharing one id no longer flip the client's door between locked and unlocked twice a second |
| Voice (final build) | host → client | test signal through `voicetalk` | 200/202 frames played, mean error 69/32768 (mu-law); 2 dropped in the post-join load hitch |
| Package | — | `tools/package.ps1` | `dist/DelusionMP-Coop.zip` with both launchers, the mod and BepInEx |
| Aggro | both | host 3 m from a mech, client 18 m away hits it three times; then the host hits harder | control and the AI's target move to the client (score 30 vs 12), the mech fights the client (client 91 → 1 hp, dies); it turns back to the host when the client is down and when the host out-damages it |
| Enemies hurting the partner | both | as above | the player the enemy fights takes the damage on their own machine |
| Menus during a fight | both | host opens the pause menu (time 0), later the client opens the inventory (time 0.2) | the other player's fight continues at full speed: the enemies move to the playing partner at once and come back by score (they froze or snapped back before) |
| Quest hand-over stage | client → host | client holds `merchantLoot`, host does not; client reaches `thievesTwo` 30, which takes it | both journals at 30, host inventory unchanged (the host's game refused the stage before and stayed at 20) |
| World cutscene from its real trigger | host → client | `CutterTransportScript.CradleJourneyBegin` (the cutter journey) | the liftoff cutscene plays on the client, then both travel to the same spot in the Cradle. The shipped game has no walk-in cutscene trigger objects; its four world cutscenes are started by scripts like this one |
| Version mismatch | — | code review | protocol, mod version (now 0.2.0) and game build are compared on connect; a mismatch is shown as an in-game note |

### Round 5 (2026-11-22): summons, charm, every ending route, loot per player

| System | Direction | How it was checked | Result |
|---|---|---|---|
| Loot per player | both | design change, code path | chests and loose pickups are each player's own: never mirrored, never adopted on join; each player loots every one once, so a quest item one player finds is still there for the other. Breakable crates stay shared, and the partner's copy rolls its own loot |
| F8: to the partner | both | rig `partnertp` (earlier session, commit 69d2495) | stands behind the partner on their floor (probed within 1.5 m of the partner's feet); far jumps wait for the region to stream in |
| Chat typing, real keyboard | both | played by hand (owner) | characters, Enter, Esc and Backspace work, Cyrillic included |
| Summon keys | both | both players summon a skeleton | before: both summons had the same key (`summon_Skeleton(Clone)`), so each machine's summon was driven to the partner's, and the client logged "unknown enemy". Now summons never enter the enemy stream |
| Summon on the partner's screen | both directions | host skeleton, client caster at once | each machine keeps its own summon and shows the partner's as a ghost (same model, its own animator; AI, spawner, self-drain, HUD, facts and colliders stripped); health follows the caster's within 0.2 s; ghosts go when the summon dies (~15 s); screenshots 23/24 |
| Summon damage | host → client | the host's summon hits a goblin the client simulates | hit forwarded and applied once (HP 56 → 26 on both); it counts as the host's threat, so the goblin moves to the host's machine, where the summon fights it |
| Summon tallies | — | `summonSkeletonDeaths`, `TotalSummonKills` … | player-owned facts (they were being pushed into the shared story facts) |
| Charm, partner's enemy | client → host | client charm hit (power 300) on a goblin the host simulates | one roll, on the client; forwarded hits carry no charm power (the host rolled again and filled its own charm slot before); the host's copy is charmed and passive; the goblin is pinned to the client's machine and fights a moth for the client |
| Charm end | client → host | wait out the charm | ended on both in the same second (`RecoverCharmed` on the charmer is sent); the host's copy is hostile again |
| Ending, three region fact sets (shortcut) | host → client | rig `ending trigger` (starts the ending conversation directly) with every "harsh", no, or every "merciful" region outcome | 33/33/35 steps, every node **and page** identical, different region branches; both reach the credits. This shortcut skips the karma computation (all `Karma*` facts stayed false), so it tested the region epilogues only |
| Multi-page dialogue | host → client | the ending's epilogue cards | before: the partner stayed on a node's first page while the driver turned pages (the game swaps pages inside one node without re-processing it). The driver now sends each page |
| Long strings | both | ending pages over 256 bytes | every string on the wire was cut at 256 bytes, sometimes inside a UTF-8 character; now 1000 bytes, cut on a character boundary, and dialogue messages are fragmented when they exceed one datagram |

The karma score (`EndingsScene.CalculateKarma`, decompiled) starts at 10 and moves with
`PledgedToInquisition` −5 / `PledgedToLiberation` +5, `godHuntUnion`/`godHuntCult` ∓3,
`EndlessHope`/`EndlessDestroy` ∓3, `cwk_AidedAphra`/`cwk_AidedReza` ∓3,
`LluddSacrificed`/`LluddSaved` ∓3, `KilledCaerGodlet`/`SparedCaerGodlet` ∓2 and ∓1 for the End
Mines crystal, Vierme, the Academy god, the Flesh and the castle duke; below 1 is
`KarmaOrder`, below 20 `KarmaSelf`, else `KarmaFreedom`. The region epilogues branch on the
outcome facts, and the karma bucket picks the Oneiric Isles passage (node 72/74/75).

**The game's own path to the ending.** `EndingsScene.OnEnable` hooks `CheckEndingsReady` to the
dialogue controller's "conversation finished" event. The final choice is in the Angel's
conversation (`Angel` tree, next to `ENDINGS_CUTSCENE`): *banish it to the Void* or *merge with
it*. That conversation sets `EndingSequenceStarted`; when it ends, `CheckEndingsReady` computes
karma and starts the ending conversation (banish and merge are different conversations, nodes 1–65
and 68–114). Run on the rig with mixed region outcomes:

| Decision | Who talks to the Angel | Region facts (karma) | Result |
|---|---|---|---|
| merge | host | Inquisition, odd regions harsh / even merciful (4, SELF) | 47 steps identical (Angel + ending); credits → title menu on both |
| merge | host | regions 1–5 harsh, 6–10 unset (−4, ORDER) | 47 steps identical; Isles node 72; credits → menu on both |
| merge | host | Liberation, 1–5 merciful, 6–10 harsh (24, FREEDOM) | identical up to the end of the ending; Isles node 74 |
| merge | **client** | Liberation, odd merciful / even harsh (16, SELF) | 46 steps identical; the host spectates the Angel and the ending; credits → menu on both |
| banish | host | Liberation, odd merciful / even harsh (16, SELF) | 43 steps identical; credits → menu on both |
| banish | host | Liberation, 1–5 merciful, 6–10 harsh (24, FREEDOM) | 46 steps identical; credits → menu on both |
| banish | **client** | regions 1–5 harsh (−4, ORDER) | 41 steps identical; credits → menu on both |

In every run the spectator's `CheckEndingsReady` did not start a second ending: it joined the
driver's. The karma bucket and `ending_BanishedAngel`/`ending_MergedAngel` agree on both machines.

**Fixed: the partner's save after the ending.** The driver's game saves right before the credits
(the finished game: `mainQuest` 150, `cradle` 90, karma, the decision), and loading the credits
re-reads each machine's save. The spectator followed into the credits without saving, so its save
stayed before the ending and its facts fell back to that old save (one run showed
`ending_MergedAngel` from an earlier game after a banish). The spectator now saves first; 40 s
into the credits both machines hold the same post-ending facts.

**Seen, not caused by co-op:** the moment the ending conversation closes, the game restarts the
Angel's conversation behind the credits (also with the partner disconnected). Left alone, the
credits finish and return to the title menu; pressing through that stray conversation during the
credits can leave the game stuck in the credits (seen only when the rig pressed keys there).

Not verifiable on the rig: Steam tunnel mode (two Steam accounts on two machines).

The three remaining `statedump` differences are a continuously moving sky object
(`StarOrbit`, sampled a second apart) and two empty trophy stands (`Null_Item` on one side,
`null` on the other).

### Screenshots (`docs/screenshots/`)

| File | What it shows |
|---|---|
| `proof_10_avatar_idle.png` … `proof_15_avatar_walking.png` | the partner's body: idle, sword drawn, thrust, power attack, casting, walking |
| `proof_16_dialogue_spectator.png` / `proof_17_dialogue_driver.png` | the same line on both screens |
| `proof_18_airship_passenger_view.png` / `proof_19_airship_pilot_view.png` | the host flying with the client on deck |
| `proof_20_dialogue_partner_pov.png` | far-away partner watching the conversation from the speaker's eyes |
| `proof_21_dialogue_charm_both.png` | the charm-roll toast on both screens |
| `proof_22_avatar_special_weapons.png` | daggers, greatswords, special-quality sword and bow in the partner's hands |
| `proof_23_summon_ghost_client.png` | client's view: the host's summoned skeleton (a ghost) beside the host's avatar |
| `proof_24_summon_own_host.png` | host's view: its own summoned skeleton |
| `avatar_model_options.png` | the female models and outfits a player can choose from |
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

- **Changes in a region the host has not loaded** are kept in the save's loaded-state table and
  in a pending file per save (`DelusionMP_pending_<save>_<slot>.bin` beside the game's saves),
  applied the first time the object loads in any later session. If that save is reloaded from
  an older point, the partner's pending changes still apply to it.
- **Join is host-authoritative:** whatever the client did while not connected to the host
  (story facts, quests, story phase, shared world objects) is replaced by the host's version on
  connect. Player-owned data (inventory, coins, stats, position) is kept.
- **Duplicate identifiers in the shipped data** (e.g. four doors sharing one id) make the
  `statedump` comparison show differences between instances that are not real desyncs.
- **Starting a session:** through the launcher (`DelusionMP-Launcher.exe`): LAN/VPN over UDP
  (the host learns the joiner's address from its first packet), or Steam (lobby found by a
  shared password or a Steam friend, traffic through Steam; no port forwarding). Steam mode is
  not verified on the rig. A lost peer is detected after 12 s and the client rejoins
  automatically when the host is back.
- **Never played end to end by two people:** late-game regions, bosses and the ending were
  exercised on the rig by commands (quest stages, facts, travel), not by playing through them.
  The ending was run through the game's own path (the Angel) with both decisions, all three
  karma buckets and either player driving, using mixed region outcomes; not every combination
  of the ten region outcomes.
- **A player who dies during a shared conversation** drops out of it (the driver carries on); they
  rejoin the next one.
- **Summons:** the partner sees a ghost of each summon that follows the caster's stream; it
  plays the game's idle/walk blend from a navigation agent that is off, so it glides in its
  idle pose while moving. A summon fights only on its caster's machine; its hits on the
  partner's enemies are forwarded and pull them to the caster.
- **Charm:** the partner's copy of a charmed enemy counts down on its own and may read a
  different number; the end of the charm is sent by the charmer. While charmed, the enemy is
  simulated on the charmer's machine.
- **Loot is per player:** chests and loose pickups are looted once by each player (by design;
  quest items cannot be taken from the partner). Breakable crates are shared.
- **Moving factory:** if one player reaches it long before the other, the factory jumps to the
  host's copy when the host arrives (each copy runs on its own until then).
- **Shop selling:** items sold to a merchant are not saved by the game, so only the seller's copy
  of the shop lists them for buyback.
- **Aggro is by threat, one target at a time:** an enemy fights the player with the most threat
  (damage dealt, closeness); it switches when the other player clearly out-threatens it, at most
  every 2 s, never attacking both in the same swing.
- **A player in a menu** keeps the vanilla pause/slow-motion on their own screen, but their
  fights move to the partner; the day/night clock follows the host, so it stops while the host
  is paused.
- **Partner body:** the game has no jump/fall clip for humans, so a jumping partner holds a
  mid-stride pose. DarkStar/Pwyll women only load with their regions; until then the partner is
  shown as the main female model and switches when the chosen one appears.
- **Dialogue:** if the partner is already in their own conversation, they are not pulled in
  (both talk independently; effects still sync). Items and coin rewards an NPC hands over now go
  to both players; items a player hands to an NPC are taken only from that player.
- **Airship:** `AirshipController.Use()` saves the game (vanilla behaviour) on the pilot's machine.
  A player without `deedAirship` cannot take the helm (vanilla rule, inventory is per player).
- **Fact conflicts:** additive counters now merge; two absolute writes to the same key at the
  same moment are still last-writer-wins (repaired by the 1 Hz fingerprint check).
- `mod/typecheck/` (the stub compile harness) is stale; the real build against the interop
  assemblies is the authority.
