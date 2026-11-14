#!/usr/bin/env python3
"""Build the co-op sync manifests from the extracted asset tables.

Outputs (work/manifest/):
  scenes.json        level file -> scene name
  region_objects.json per region: sync-relevant objects with identity keys
  fact_keys.json     every fact-like string the world references, with referrer classes
  doors.json         door table with lock configuration
  lifts.json         lift/mover table
  enemies.json       spawner table
"""
import collections
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "work" / "data"
OUT = ROOT / "work" / "manifest"

SCENES = [
    "DelusionPreBoot", "DelusionBoot", "DelusionAudio", "DelusionMain", "DelusionMenu",
    "DelusionCredits", "DelusionCombatTest", "REGION_Starting", "REGION_Hallowshire",
    "REGION_MinesHallow", "REGION_Clockwork", "REGION_DungeonEmberian", "REGION_Endless",
    "REGION_MushroomIsle", "REGION_WikkanIsle", "REGION_MinesEndless",
    "REGION_DungeonEndless", "REGION_PlayerHouse", "REGION_ObservatoryIsle",
    "REGION_Underlands", "REGION_DesertIsland", "REGION_GodWarIsland",
    "REGION_CreatureIsland", "REGION_TinyIslands", "REGION_Cradle", "REGION_DemoIsle",
]
LEVEL_TO_SCENE = {f"level{i}": s for i, s in enumerate(SCENES)}

# classes that carry world state a peer must see
SYNC_CLASSES = [
    "SaveableObject", "QuestObject", "EnemySpawner", "EnemyController", "EnemyStats",
    "DoorsDelusion", "DoorsDelusionVert", "DoorsDelusionBridge", "DoorsUltimate",
    "DelusionLift", "DelusionLiftLever", "DelusionLever", "ItemPickup", "ItemChest",
    "PlantPickup", "NPCBase", "SimpleTaskTrigger", "TriggerOnPlayerEnter",
    "TriggerByFactOrTask", "TriggerByTask", "MovingFactory", "CompletableObject",
    "ShopSpeaker", "AirshipController", "EnemyCombatEncounter",
]

KEY_FIELDS = {
    "SaveableObject": ["uniqueIdentifier"],
    "QuestObject": ["taskString", "taskStage"],
    "EnemySpawner": ["optionalEnemyID", "setNormalFact", "setFactBool", "myLevel", "canRespawn"],
    "EnemyController": ["myFaction", "myCombatantType", "hostileType", "followType", "canOpenDoors"],
    "EnemyStats": ["maxHealth", "enemyDamage", "enemyDefense"],
    "DoorsDelusion": ["staysOpen"],
    "DoorsDelusionVert": ["staysOpen"],
    "DoorsUltimate": ["unlockableByPick", "lockDifficulty", "unlockableByItem", "itemNeededID",
                      "unlockableBySmash", "mightRequired", "unlockableByFact", "factID",
                      "unlockableByLore", "factToSet", "smashOnly"],
    "DelusionLift": ["liftDisabled", "recallLocked", "factToSetWhenUsed", "keyItemNeeded", "liftPosition"],
    "DelusionLever": ["leverState", "factToSet", "keyItemNeeded"],
    "ItemPickup": ["canRespawn", "maxDaysUntilRespawn"],
    "NPCBase": ["NPCTag", "questTitleString", "questStage", "nameRevealedOnFact", "speakerFaction"],
    "SimpleTaskTrigger": ["taskID", "taskStage", "hasPopup", "willDisableSelf"],
    "TriggerOnPlayerEnter": ["factToSet", "setFactToThis", "setsNewWorldState", "needsItemUsedID"],
    "TriggerByFactOrTask": ["typeOfTrigger", "worldState", "onlyCheckOnRegionSwap"],
    "TriggerByTask": ["taskToCheck", "activeIfTaskNotStarted", "stagesWhenActive"],
    "MovingFactory": ["moveTime", "rotateTime", "pauseTime"],
    "ShopSpeaker": ["sellCategories"],
    "EnemyCombatEncounter": ["factMustBeTrue", "intEqualOrHigher", "neverDisables"],
}

FACT_FIELD = re.compile(r"fact", re.I)
FACT_VALUE_OK = re.compile(r"^[A-Za-z][A-Za-z0-9_]{1,60}$")


def load(name):
    p = DATA / f"{name}.json"
    if not p.exists():
        return []
    return json.loads(p.read_text(encoding="utf-8"))


def scene_of(rec):
    return LEVEL_TO_SCENE.get(rec.get("__file", ""), rec.get("__file", ""))


def walk_strings(obj, path=""):
    if isinstance(obj, dict):
        for k, v in obj.items():
            yield from walk_strings(v, f"{path}.{k}" if path else k)
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            yield from walk_strings(v, f"{path}[{i}]")
    elif isinstance(obj, str):
        yield path, obj


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "scenes.json").write_text(json.dumps(LEVEL_TO_SCENE, indent=1))

    region_objects = collections.defaultdict(lambda: collections.defaultdict(int))
    fact_refs = collections.defaultdict(lambda: collections.defaultdict(int))
    doors, lifts, enemies, pickups = [], [], [], []

    for cls in SYNC_CLASSES:
        recs = load(cls)
        for r in recs:
            sc = scene_of(r)
            region_objects[sc][cls] += 1
            if cls == "SaveableObject":
                continue
            rec = {"scene": sc, "file": r.get("__file"), "path_id": r.get("__path_id")}
            for f in KEY_FIELDS.get(cls, []):
                if f in r:
                    rec[f] = r[f]
            if cls.startswith("Doors"):
                doors.append(rec)
            elif cls in ("DelusionLift", "DelusionLiftLever", "MovingFactory"):
                lifts.append(rec)
            elif cls == "EnemySpawner":
                enemies.append(rec)
            elif cls in ("ItemPickup", "ItemChest", "PlantPickup"):
                pickups.append(rec)
            # harvest fact-like strings anywhere in the record
            for p, val in walk_strings(r):
                leaf = p.split(".")[-1].split("[")[0]
                if FACT_FIELD.search(leaf) and FACT_VALUE_OK.match(val):
                    fact_refs[val][cls] += 1

    # task/quest fact vocabulary from the quest database
    for r in load("QuestSystemDB"):
        for p, val in walk_strings(r):
            leaf = p.split(".")[-1].split("[")[0]
            if FACT_FIELD.search(leaf) and FACT_VALUE_OK.match(val):
                fact_refs[val]["QuestSystemDB"] += 1
    for r in load("InitialFacts"):
        for p, val in walk_strings(r):
            if FACT_VALUE_OK.match(val) and p.split(".")[-1] in ("Key",):
                fact_refs[val]["InitialFacts"] += 1

    (OUT / "region_objects.json").write_text(json.dumps(
        {k: dict(sorted(v.items())) for k, v in sorted(region_objects.items())}, indent=1))
    (OUT / "fact_keys.json").write_text(json.dumps(
        {k: dict(v) for k, v in sorted(fact_refs.items())}, indent=1))
    (OUT / "doors.json").write_text(json.dumps(doors, indent=1))
    (OUT / "lifts.json").write_text(json.dumps(lifts, indent=1))
    (OUT / "enemies.json").write_text(json.dumps(enemies, indent=1))
    (OUT / "pickups.json").write_text(json.dumps(pickups, indent=1))

    print("scenes:", len(LEVEL_TO_SCENE))
    print("regions with sync objects:", len(region_objects))
    for sc in sorted(region_objects):
        tot = sum(region_objects[sc].values())
        print(f"  {sc:28s} {tot:6d}")
    print("fact-like keys:", len(fact_refs))
    print("doors:", len(doors), "lifts:", len(lifts), "spawners:", len(enemies), "pickups:", len(pickups))


if __name__ == "__main__":
    main()
