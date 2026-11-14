#!/usr/bin/env python3
"""Extract identifier-like strings from Unity serialized assets.

Unity writes MonoBehaviour string fields as raw UTF-8, so game content IDs
(quests, facts, items, regions) are readable without a full asset parser.
"""
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

RUN = re.compile(rb"[\x20-\x7e]{3,96}")

CATEGORIES = [
    ("quest", re.compile(r"^(Q_[A-Za-z0-9_]+|QUEST_[A-Za-z0-9_]+)$")),
    ("fact", re.compile(r"^(FACT|F)[_A-Za-z0-9]{2,}$")),
    ("item", re.compile(r"^(ITM|ITEM)_[A-Za-z0-9_]+$")),
    ("door", re.compile(r"^(DOOR)_[A-Za-z0-9_]+$")),
    ("region", re.compile(r"^(REGION|SUBREGION)_[A-Za-z0-9_]+$")),
    ("task", re.compile(r"^TASK[_A-Za-z0-9]*$")),
    ("note", re.compile(r"^NOTE_[A-Z0-9_]+$")),
    ("achievement", re.compile(r"^(ACH|ACHIEVEMENT)[_A-Za-z0-9]*$")),
]


def scan(base: Path, outdir: Path):
    files = sorted(base.glob("*.assets")) + sorted(base.glob("*.resource"))
    for pat in ("level*", "sharedassets*"):
        files += sorted(base.glob(pat))
    files = [f for f in files if f.is_file()]
    seen = defaultdict(lambda: defaultdict(Counter))
    per_file = {}
    for f in files:
        try:
            data = f.read_bytes()
        except OSError:
            continue
        name = f.name
        counts = Counter()
        for m in RUN.finditer(data):
            s = m.group().decode("ascii", "ignore").strip()
            if not s or len(s) < 3:
                continue
            for cat, pat in CATEGORIES:
                if pat.match(s):
                    seen[cat][s][name] += 1
                    counts[s] += 1
        per_file[name] = len(counts)
    result = {
        cat: {
            tok: {"files": dict(where), "count": sum(where.values())}
            for tok, where in sorted(tokens.items())
        }
        for cat, tokens in seen.items()
    }
    outdir.mkdir(parents=True, exist_ok=True)
    (outdir / "asset_ids.json").write_text(json.dumps(result, indent=1), encoding="utf-8")
    for cat, tokens in result.items():
        print(f"== {cat}: {len(tokens)}")
        for tok in list(tokens)[:40]:
            print(f"    {tok}")
    return result


if __name__ == "__main__":
    base = Path(sys.argv[1])
    out = Path(sys.argv[2])
    scan(base, out)
