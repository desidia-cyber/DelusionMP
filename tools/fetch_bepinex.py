#!/usr/bin/env python3
"""Fetch the newest BepInEx 6 IL2CPP (bleeding edge) x64 build.

The official GitHub releases stop at `v6.0.0-pre.2` (2022), which predates
Unity 2022.3 / metadata v31. The maintained builds live on the BepInEx build
server, whose HTML lists the artifacts. This scrapes that page, picks the
newest `BepInEx-Unity.IL2CPP-win-x64-*.zip`, downloads it once into
`work/bepinex/` and unzips it into a target directory.

Usage:
    python tools/fetch_bepinex.py --target "rig/instance1/windows_content"
    python tools/fetch_bepinex.py --download-only
"""
import argparse
import os
import re
import shutil
import sys
import urllib.parse
import urllib.request
import zipfile

BASE = "https://builds.bepinex.dev"
LIST_URL = BASE + "/projects/bepinex_be"
UA = {"User-Agent": "delusionMP-bootstrap/1.0"}
CACHE = os.path.join(os.path.dirname(__file__), "..", "work", "bepinex")


def fetch(url: str) -> bytes:
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def newest_artifact_url() -> str:
    html = fetch(LIST_URL).decode("utf-8", "replace")
    # hrefs look like /projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip
    hrefs = re.findall(
        r'href="(/projects/bepinex_be/\d+/BepInEx-Unity\.IL2CPP-win-x64-[^"]+\.zip)"', html
    )
    if not hrefs:
        sys.exit("no IL2CPP win-x64 artifact found on the build server")

    def build_no(h):
        m = re.search(r"-be\.(\d+)", urllib.parse.unquote(h))
        return int(m.group(1)) if m else 0

    href = sorted(set(hrefs), key=build_no)[-1]
    return BASE + href


def download(url: str) -> str:
    os.makedirs(CACHE, exist_ok=True)
    name = os.path.basename(urllib.parse.unquote(url))
    path = os.path.join(CACHE, name)
    if os.path.exists(path) and os.path.getsize(path) > 10_000_000:
        print(f"[cached] {path}")
        return path
    print(f"[get] {url}")
    data = fetch(url)
    with open(path, "wb") as f:
        f.write(data)
    print(f"[ok] {len(data)} bytes -> {path}")
    return path


def install(zip_path: str, target: str) -> None:
    os.makedirs(target, exist_ok=True)
    with zipfile.ZipFile(zip_path) as z:
        names = z.namelist()
        # The archives are rooted at the game folder (winhttp.dll, doorstop_config.ini, BepInEx/...)
        z.extractall(target)
    print(f"[install] {len(names)} entries -> {target}")


UNITY_LIBS_URL = "https://unity.bepinex.dev/libraries/{version}.zip"


def install_unity_libs(version: str, target: str) -> None:
    """BepInEx needs the Unity base libraries to generate IL2CPP interop.

    Its own downloader uses CoreCLR's schannel stack, which fails on some rigs
    (SEC_E_NO_CREDENTIALS). Pre-seeding BepInEx/unity-libs from Python's OpenSSL
    stack sidesteps that entirely.
    """
    dest = os.path.join(target, "BepInEx", "unity-libs")
    # BepInEx reads the .zip straight out of this folder (see UnityBaseLibrariesSource
    # in BepInEx.cfg: "If a .zip file with the same filename ... already exists in
    # unity-libs, it will be used instead of downloading"). It must NOT be extracted.
    wanted = os.path.join(dest, f"{version}.zip")
    if os.path.isfile(wanted):
        print(f"[cached] {wanted}")
        return
    url = UNITY_LIBS_URL.format(version=version)
    print(f"[get] {url}")
    data = fetch(url)
    os.makedirs(dest, exist_ok=True)
    with open(wanted, "wb") as f:
        f.write(data)
    print(f"[install] {len(data)} bytes -> {wanted}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", help="game folder to unpack into")
    ap.add_argument("--download-only", action="store_true")
    ap.add_argument("--url", help="explicit artifact URL")
    ap.add_argument("--unity-version", help="also pre-seed BepInEx/unity-libs for this Unity version")
    a = ap.parse_args()

    if a.unity_version:
        if not a.target:
            sys.exit("--target is required with --unity-version")
        install_unity_libs(a.unity_version, a.target)
        return

    url = a.url or newest_artifact_url()
    path = download(url)
    if not a.download_only:
        if not a.target:
            sys.exit("--target is required unless --download-only")
        # Clear a previous BepInEx so an upgrade cannot mix loader versions.
        for sub in ("BepInEx", "doorstop_config.ini", "winhttp.dll", ".doorstop_version"):
            p = os.path.join(a.target, sub)
            if os.path.isdir(p):
                shutil.rmtree(p)
            elif os.path.exists(p):
                os.remove(p)
        install(path, a.target)


if __name__ == "__main__":
    main()
