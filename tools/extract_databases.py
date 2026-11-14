#!/usr/bin/env python3
"""Extract Dread Delusion content databases from the shipped assets.

Two passes:
  1. cheap header scan: class name per MonoBehaviour (no typetree generation)
  2. full parse: only for the database classes we actually need

Writes work/data/<Class>.json plus work/data/index.json
"""
import collections
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "work" / "pylibs"))

import UnityPy  # noqa: E402
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator  # noqa: E402
from UnityPy.helpers.TypeTreeNode import TypeTreeNode as TN  # noqa: E402

GAME = Path(r"G:\SteamLibrary\steamapps\common\Dread Delusion\windows_content")
DATA = GAME / "Dread Delusion_Data"
OUT = ROOT / "work" / "data"

WANTED = {
    "QuestSystemDB", "InitialFacts", "ItemDatabaseData", "DialogueSystemDB",
    "BuffsDatabase", "TextDatabase", "QuestBaseData", "StoneData",
    # world-object identity + per-scene sync surface
    "SaveableObject", "QuestObject", "EnemySpawner", "EnemyController", "EnemyStats",
    "DoorsDelusion", "DoorsDelusionVert", "DoorsDelusionBridge", "DoorsUltimate",
    "DelusionLift", "DelusionLiftLever", "DelusionLever", "ItemPickup", "ItemChest",
    "PlantPickup", "NPCBase", "SimpleTaskTrigger", "TriggerOnPlayerEnter",
    "TriggerByFactOrTask", "TriggerByTask", "MovingFactory", "CompletableObject",
    "ShopSpeaker", "AirshipController", "EnemyCombatEncounter", "RespawnTimer",
}


def make_gen():
    g = TypeTreeGenerator("2022.3.60f1")
    g.load_il2cpp((GAME / "GameAssembly.dll").read_bytes(),
                  (DATA / "il2cpp_data" / "Metadata" / "global-metadata.dat").read_bytes())

    def get_nodes_up(assembly, fullname):
        key = (fullname,)
        if key in g.cache:
            return g.cache[key]
        try:
            base = g.get_nodes(assembly, fullname)
        except Exception:
            g.cache[key] = None
            return None
        if not base:
            g.cache[key] = None
            return None
        node = TN.from_list([TN(b.m_Level, b.m_Type, b.m_Name, 0, 0, m_MetaFlag=b.m_MetaFlag) for b in base])
        g.cache[key] = node
        return node

    g.get_nodes_up = get_nodes_up
    return g


def files():
    out = []
    for pat in ("*.assets", "sharedassets*", "resources*", "level*"):
        out += sorted(DATA.glob(pat))
    seen, uniq = set(), []
    for f in out:
        if f.is_file() and not f.name.endswith((".resS", ".resource")) and f.name not in seen:
            seen.add(f.name)
            uniq.append(f)
    return uniq


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    g = make_gen()
    counts = collections.Counter()
    per_file = {}
    wanted_hits = collections.defaultdict(list)
    t0 = time.time()
    for f in files():
        try:
            env = UnityPy.load(str(f))
            env.typetree_generator = g
        except Exception as e:
            print("skip", f.name, e, flush=True)
            continue
        local = collections.Counter()
        for o in env.objects:
            if o.type.name != "MonoBehaviour":
                continue
            try:
                mb = o.parse_monobehaviour_head()
                script = mb.m_Script.deref_parse_as_object()
                cls = script.m_ClassName or "?"
                asm = script.m_AssemblyName or ""
            except Exception:
                continue
            counts[cls] += 1
            local[cls] += 1
            if cls in WANTED and asm.startswith("Assembly-CSharp"):
                try:
                    d = o.read_typetree()
                    if isinstance(d, dict):
                        d["__file"] = f.name
                        d["__path_id"] = o.path_id
                        wanted_hits[cls].append(d)
                except Exception:
                    pass
        per_file[f.name] = dict(local)
        print(f"{f.name}: {sum(local.values())} mono ({time.time()-t0:.0f}s)", flush=True)

    (OUT / "class_counts.json").write_text(json.dumps(dict(counts), indent=1, sort_keys=True))
    (OUT / "per_file.json").write_text(json.dumps(per_file, indent=1, sort_keys=True))
    for cls, objs in wanted_hits.items():
        (OUT / f"{cls}.json").write_text(json.dumps(objs, indent=1, default=str))
        print(f"saved {cls}: {len(objs)}", flush=True)
    print("DONE total mono:", sum(counts.values()), "classes:", len(counts), flush=True)


if __name__ == "__main__":
    main()
