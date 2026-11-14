#!/usr/bin/env python3
"""Index an Il2CppDumper dump.cs into JSON for querying.

Produces work/dump/index.json: one record per type with namespace, name,
kind, parent, interfaces, fields (with offsets) and methods (with RVA/Slot).
"""
import json
import re
import sys
from pathlib import Path

RE_NS = re.compile(r"^// Namespace: (.*)$")
RE_TYPE = re.compile(
    r"^(?:\[[^\]]*\]\s*)*"          # leading attributes
    r"(?:(?:public|private|protected|internal|static|sealed|abstract|readonly|partial|unsafe|ref)\s+)*"
    r"(class|struct|enum|interface|delegate)\s+(\S+?)\s*(?::\s*(.*?))?\s*//\s*TypeDefIndex:\s*(\d+)\s*$"
)
RE_METHOD = re.compile(r"^\s*//\s*RVA:\s*0x([0-9A-Fa-f]+)\s+Offset:\s*0x([0-9A-Fa-f]+)\s+VA:\s*0x([0-9A-Fa-f]+)(.*)$")
RE_SIG = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|virtual|abstract|override|sealed|new|extern|unsafe|readonly|async|partial|\s)+[\w<>,\[\]\.\*\?&\s]+?\s([\w\.<>\[\]]+)\s*\((.*)\)\s*(?:\{\s*\})?\s*$")
RE_FIELD = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|readonly|const|volatile|fixed|\s)+([\w<>,\[\]\.\*\?&]+)\s+([\w<>\.\[\]]+)\s*;\s*//\s*(0x[0-9A-Fa-f]+|-)?")
RE_CONST = re.compile(r"^\s*public const \S+ (\w+) = (.*);$")


def parse(path: Path):
    types = []
    cur = None
    pending_rva = None
    section = None
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.rstrip("\n")
            m = RE_NS.match(line)
            if m:
                cur = None
                section = None
                ns_of_next = m.group(1).strip()
                pending_ns = ns_of_next
                continue
            m = RE_TYPE.match(line)
            if m:
                kind, name, parents, tdi = m.groups()
                iface = []
                parent = None
                if parents:
                    parts = [p.strip() for p in parents.split(",") if p.strip()]
                    for p in parts:
                        iface.append(p)
                    parent = parts[0] if parts else None
                cur = {
                    "ns": pending_ns if "pending_ns" in dir() else "",
                    "name": name,
                    "kind": kind,
                    "parent": parent,
                    "interfaces": iface,
                    "typedef_index": int(tdi),
                    "fields": [],
                    "methods": [],
                    "consts": {},
                }
                types.append(cur)
                section = None
                pending_rva = None
                continue
            if cur is None:
                continue
            s = line.strip()
            if s == "// Fields":
                section = "fields"
                continue
            if s == "// Methods":
                section = "methods"
                continue
            m = RE_METHOD.match(line)
            if m:
                pending_rva = {
                    "rva": int(m.group(1), 16),
                    "offset": int(m.group(2), 16),
                    "va": int(m.group(3), 16),
                    "extra": m.group(4).strip(),
                }
                continue
            if section == "methods" and pending_rva and "(" in line:
                sig = s
                name_m = re.search(r"([\w\.<>\[\]]+)\s*\((.*)\)\s*(?:\{|\{\s*\}|\s*$)", sig)
                if name_m:
                    slot = None
                    sm = re.search(r"Slot:\s*(\d+)", pending_rva["extra"])
                    if sm:
                        slot = int(sm.group(1))
                    cur["methods"].append(
                        {
                            "name": name_m.group(1),
                            "sig": sig,
                            "rva": pending_rva["rva"],
                            "offset": pending_rva["offset"],
                            "va": pending_rva["va"],
                            "slot": slot,
                            "is_static": " static " in sig,
                            "is_virtual": " virtual " in sig,
                            "is_abstract": " abstract " in sig,
                        }
                    )
                pending_rva = None
                continue
            if section == "fields":
                m = RE_CONST.match(line)
                if m:
                    cur["consts"][m.group(1)] = m.group(2)
                    continue
                m = RE_FIELD.match(line)
                if m:
                    ftype, fname, off = m.groups()
                    cur["fields"].append(
                        {
                            "type": ftype,
                            "name": fname,
                            "offset": int(off, 16) if off and off != "-" else None,
                        }
                    )
    return types


def main():
    src = Path(sys.argv[1] if len(sys.argv) > 1 else "work/dump/dump.cs")
    out = Path(sys.argv[2] if len(sys.argv) > 2 else "work/dump/index.json")
    types = parse(src)
    for t in types:
        if not t["ns"]:
            t["ns"] = ""
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(types, separators=(",", ":")), encoding="utf-8")
    print(f"types={len(types)} -> {out}")
    acs = [t for t in types if 2790 <= t["typedef_index"] < 3752]
    print(f"Assembly-CSharp types={len(acs)}")
    print(f"total methods={sum(len(t['methods']) for t in types)}")


if __name__ == "__main__":
    main()
