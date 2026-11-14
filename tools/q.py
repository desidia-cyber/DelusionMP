#!/usr/bin/env python3
"""Query the parsed IL2CPP type index.

Usage:
  python tools/q.py types                 # game types grouped by namespace
  python tools/q.py grep <regex>          # type names matching regex
  python tools/q.py type <Name>           # full record for a type
  python tools/q.py members <Name>        # fields + methods of a type
  python tools/q.py find <regex>          # search field/method names across game types
  python tools/q.py kinds                 # type kind histogram
"""
import json
import re
import sys
from pathlib import Path

INDEX = Path(__file__).resolve().parent.parent / "work" / "dump" / "index.json"
GAME_LO, GAME_HI = 2790, 3752


def load():
    return json.loads(INDEX.read_text(encoding="utf-8"))


def game_types(types):
    return [t for t in types if GAME_LO <= t["typedef_index"] < GAME_HI]


def full_name(t):
    return f"{t['ns']}.{t['name']}" if t["ns"] else t["name"]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    cmd = sys.argv[1]
    types = load()
    gt = game_types(types)

    if cmd == "types":
        nss = {}
        for t in gt:
            nss.setdefault(t["ns"], []).append(t)
        for ns in sorted(nss):
            label = ns if ns else "<global>"
            print(f"== {label} ({len(nss[ns])})")
            for t in sorted(nss[ns], key=lambda x: x["name"]):
                print(f"    {t['kind']:9s} {t['name']}")
        return 0

    if cmd == "kinds":
        h = {}
        for t in gt:
            h[t["kind"]] = h.get(t["kind"], 0) + 1
        for k, v in sorted(h.items(), key=lambda x: -x[1]):
            print(f"{v:6d} {k}")
        return 0

    if cmd == "grep":
        pat = re.compile(sys.argv[2], re.I)
        for t in sorted(gt, key=full_name):
            if pat.search(full_name(t)):
                print(f"{full_name(t)}  [{t['kind']}] tdi={t['typedef_index']} parent={t['parent']}")
        return 0

    if cmd == "find":
        pat = re.compile(sys.argv[2], re.I)
        for t in gt:
            for m in t["methods"]:
                if pat.search(m["name"]):
                    print(f"M {full_name(t)}.{m['name']}  rva=0x{m['rva']:X} sig={m['sig'][:120]}")
            for f in t["fields"]:
                if pat.search(f["name"]):
                    print(f"F {full_name(t)}.{f['name']} : {f['type']} off={f['offset']}")
        return 0

    if cmd in ("type", "members"):
        want = sys.argv[2]
        hits = [t for t in types if t["name"] == want or full_name(t) == want]
        if not hits:
            pat = re.compile(want, re.I)
            hits = [t for t in types if pat.search(t["name"])]
        for t in hits:
            print(f"### {full_name(t)}  [{t['kind']}] tdi={t['typedef_index']}")
            print(f"    parent={t['parent']} interfaces={t['interfaces']}")
            if t["consts"]:
                print("    consts:", t["consts"])
            if t["fields"]:
                print("    -- fields --")
                for f in t["fields"]:
                    off = f"0x{f['offset']:X}" if f["offset"] is not None else "-"
                    print(f"      {off:>8s} {f['type']} {f['name']}")
            if cmd == "members" and t["methods"]:
                print("    -- methods --")
                for m in t["methods"]:
                    print(f"      0x{m['rva']:08X} {m['sig']}")
            elif t["methods"]:
                print(f"    -- {len(t['methods'])} methods --")
            print()
        return 0

    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
