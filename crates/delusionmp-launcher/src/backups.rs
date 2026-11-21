//! Doc 107: the co-op save backups, for DelusionMP-Coop.exe (`--backups`, `--restore`) and
//! DelusionMP-Launcher.exe (the Backups page).
//!
//! The mod makes them (mod/native/src/dmp_settings_io.h): `<co-op folder>\game\DollsNest\Binaries\Win64\
//! DollsMP\save-backups\<YYYYMMDD-HHMMSS>\`, each with a `backup.txt` that says when, why, which level,
//! the pair id shared with the partner's backup of the same moment, and every file's size and FNV-1a 64.
//! Folders from before doc 107 have no backup.txt: they are listed as "older backup" (no details).
//!
//! A restore:
//!   * only into `<co-op folder>\saves\Saved_<x>\SaveGames` (never the main save in %LOCALAPPDATA%);
//!   * only while no game from this co-op folder is running;
//!   * only from a backup whose files still match backup.txt;
//!   * after a "before-restore" backup of the save as it is (so a restore can itself be undone);
//!   * into a new folder first, checked, then swapped in by renames.
#![allow(dead_code)]

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

pub const MANIFEST: &str = "backup.txt";
pub const PIN: &str = "pinned";
const OWN_DIR: &str = "_dollsmp"; // the mod's own-world files (doc 68), beside the exe

#[derive(Clone, Debug, Default)]
pub struct Entry {
    pub rel: String,
    pub size: u64,
    pub fnv: u64,
}

#[derive(Clone, Debug, Default)]
pub struct Backup {
    pub dir: PathBuf,
    pub stamp: String,
    pub made: String,
    pub made_unix: u64,
    pub kind: String,
    pub reason: String,
    pub role: String,
    pub pair: String,
    pub partner: String,
    pub level: String,
    pub cycle: i32,
    pub slot: String,
    pub source: String,
    pub files: u32,
    pub bytes: u64,
    pub verified: bool,
    /// the partner made its half of this moment (host: the peer said so; peer: the host made it first)
    pub confirmed: bool,
    pub pinned: bool,
    /// made before doc 107: no backup.txt
    pub legacy: bool,
    pub entries: Vec<Entry>,
}

impl Backup {
    /// A short label for the moment.
    pub fn moment(&self) -> &'static str {
        match self.kind.as_str() {
            "game-start" => "Game started",
            "session-start" => "Session started",
            "level" => "New level",
            "fight" => "Boss / trial ended",
            "save" => "Game saved",
            "session-end" => "Partner left",
            "manual" => "Made by hand",
            "before-restore" => "Before a restore",
            _ if self.legacy => "Older backup",
            _ => "Backup",
        }
    }
    /// The level as the game names its maps, without the "_P".
    pub fn level_short(&self) -> String {
        self.level.trim_end_matches("_P").to_string()
    }
    /// "153012-7F3A": the part of the pair id two players read to each other.
    pub fn pair_short(&self) -> String {
        if self.pair.len() == 20 { self.pair[9..].to_string() } else { self.pair.clone() }
    }
}

pub struct Paths {
    pub root: PathBuf,
    pub win64: PathBuf,
    pub backups: PathBuf,
    pub saves: PathBuf,
}

pub fn paths(root: &Path) -> Paths {
    let win64 = root.join("game").join("windows_content");
    Paths { root: root.to_path_buf(), backups: win64.join("BepInEx").join("save-backups"), win64, saves: root.join("saves") }
}

pub fn fnv64(data: &[u8]) -> u64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in data {
        h ^= *b as u64;
        h = h.wrapping_mul(0x100_0000_01b3);
    }
    h
}

pub fn is_stamp(s: &str) -> bool {
    let b = s.as_bytes();
    b.len() == 15 && b[8] == b'-' && b.iter().enumerate().all(|(i, c)| i == 8 || c.is_ascii_digit())
}

fn stamp_text(s: &str) -> String {
    if !is_stamp(s) {
        return s.to_string();
    }
    format!("{}-{}-{} {}:{}:{}", &s[0..4], &s[4..6], &s[6..8], &s[9..11], &s[11..13], &s[13..15])
}

pub fn parse_manifest(text: &str, b: &mut Backup) {
    b.cycle = -1;
    for line in text.lines() {
        let l = line.trim();
        if l.is_empty() || l.starts_with(';') || l.starts_with('#') {
            continue;
        }
        let Some((k, v)) = l.split_once('=') else { continue };
        let (k, v) = (k.trim(), v.trim());
        match k {
            "made" => b.made = v.to_string(),
            "made_unix" => b.made_unix = v.parse().unwrap_or(0),
            "kind" => b.kind = v.to_string(),
            "reason" => b.reason = v.to_string(),
            "role" => b.role = v.to_string(),
            "pair" => b.pair = v.to_string(),
            "partner" => b.partner = v.to_string(),
            "level" => b.level = v.to_string(),
            "cycle" => b.cycle = v.parse().unwrap_or(-1),
            "slot" => b.slot = v.to_string(),
            "source" => b.source = v.to_string(),
            "files" => b.files = v.parse().unwrap_or(0),
            "bytes" => b.bytes = v.parse().unwrap_or(0),
            "verified" => b.verified = v == "1",
            "partner_confirmed" => b.confirmed = v == "1",
            "file" => {
                let parts: Vec<&str> = v.rsplitn(3, '|').collect();
                if parts.len() == 3 {
                    if let (Ok(size), Ok(fnv)) = (parts[1].trim().parse::<u64>(), u64::from_str_radix(parts[0].trim(), 16)) {
                        b.entries.push(Entry { rel: parts[2].trim().to_string(), size, fnv });
                    }
                }
            }
            _ => {}
        }
    }
}

fn walk(base: &Path, rel: &Path, out: &mut Vec<(PathBuf, u64)>, depth: u32) {
    if depth > 4 {
        return;
    }
    let Ok(rd) = fs::read_dir(base.join(rel)) else { return };
    for e in rd.flatten() {
        let r = rel.join(e.file_name());
        let Ok(m) = e.metadata() else { continue };
        if m.is_dir() {
            walk(base, &r, out, depth + 1);
        } else {
            out.push((r, m.len()));
        }
    }
}

/// Every backup in the folder, newest first.
pub fn list(p: &Paths) -> Vec<Backup> {
    let mut v = Vec::new();
    let Ok(rd) = fs::read_dir(&p.backups) else { return v };
    for e in rd.flatten() {
        let name = e.file_name().to_string_lossy().to_string();
        if !is_stamp(&name) || !e.path().is_dir() {
            continue;
        }
        let dir = e.path();
        let mut b = Backup { dir: dir.clone(), stamp: name.clone(), cycle: -1, ..Default::default() };
        match fs::read_to_string(dir.join(MANIFEST)) {
            Ok(t) => parse_manifest(&t, &mut b),
            Err(_) => {
                b.legacy = true;
                b.made = stamp_text(&name);
                let mut files = Vec::new();
                walk(&dir, Path::new(""), &mut files, 0);
                b.files = files.len() as u32;
                b.bytes = files.iter().map(|f| f.1).sum();
                if let Some(slot) = files.iter().filter_map(|f| f.0.components().next().filter(|_| f.0.components().count() > 1)).next() {
                    b.slot = slot.as_os_str().to_string_lossy().to_string();
                }
            }
        }
        if b.made.is_empty() {
            b.made = stamp_text(&name);
        }
        b.pinned = dir.join(PIN).exists();
        v.push(b);
    }
    v.sort_by(|a, b| b.stamp.cmp(&a.stamp));
    v
}

/// The files still match backup.txt (an older backup: its files are readable).
pub fn verify(b: &Backup) -> Result<(), String> {
    if b.legacy {
        let mut files = Vec::new();
        walk(&b.dir, Path::new(""), &mut files, 0);
        if files.is_empty() {
            return Err("the backup folder is empty".into());
        }
        for (r, _) in files {
            fs::read(b.dir.join(&r)).map_err(|e| format!("{} cannot be read: {e}", r.display()))?;
        }
        return Ok(());
    }
    if !b.verified || b.entries.is_empty() {
        return Err("backup.txt does not list the files (the backup was not finished)".into());
    }
    for e in &b.entries {
        let data = fs::read(b.dir.join(&e.rel)).map_err(|err| format!("{} is missing or unreadable: {err}", e.rel))?;
        if data.len() as u64 != e.size || fnv64(&data) != e.fnv {
            return Err(format!("{} has changed since the backup was made", e.rel));
        }
    }
    Ok(())
}

fn lower(p: &Path) -> String {
    p.to_string_lossy().replace('/', "\\").trim_end_matches('\\').to_lowercase()
}

/// The main save. Never written by anything here.
fn main_save() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA").map(|l| PathBuf::from(l).join("LocalLow").join("Dread Delusion"))
}

/// Is `t` exactly `<root>\saves\Saved_<x>\SaveGames`, and nowhere near the main save?
pub fn safe_target(p: &Paths, t: &Path) -> Result<(), String> {
    let lt = lower(t);
    if let Some(m) = main_save() {
        let lm = lower(&m);
        if lt == lm || lt.starts_with(&(lm.clone() + "\\")) || lm.starts_with(&(lt.clone() + "\\")) {
            return Err(format!("refused: {} is the main (single-player) save", t.display()));
        }
    }
    let ls = lower(&p.saves);
    let Some(rest) = lt.strip_prefix(&(ls + "\\")) else {
        return Err(format!("refused: {} is not in this co-op folder's saves", t.display()));
    };
    let parts: Vec<&str> = rest.split('\\').collect();
    if parts.len() != 2 || !parts[0].starts_with("saved_") || parts[1] != "savegames" || parts.iter().any(|s| *s == ".." || s.is_empty()) {
        return Err(format!("refused: {} is not a co-op SaveGames folder", t.display()));
    }
    Ok(())
}

/// The co-op save folders that exist (`saves\Saved_*\SaveGames`), the most recently written first.
pub fn save_folders(p: &Paths) -> Vec<PathBuf> {
    let mut v: Vec<(std::time::SystemTime, PathBuf)> = Vec::new();
    if let Ok(rd) = fs::read_dir(&p.saves) {
        for e in rd.flatten() {
            let sg = e.path().join("SaveGames");
            if e.file_name().to_string_lossy().to_lowercase().starts_with("saved_") && sg.is_dir() {
                let mut files = Vec::new();
                walk(&sg, Path::new(""), &mut files, 0);
                let newest = files.iter().filter_map(|f| fs::metadata(sg.join(&f.0)).and_then(|m| m.modified()).ok()).max().unwrap_or(std::time::UNIX_EPOCH);
                v.push((newest, sg));
            }
        }
    }
    v.sort_by(|a, b| b.0.cmp(&a.0));
    v.into_iter().map(|x| x.1).collect()
}

/// Where this backup goes back to.
pub fn target_of(p: &Paths, b: &Backup) -> Result<PathBuf, String> {
    if !b.source.is_empty() {
        let t = PathBuf::from(&b.source);
        safe_target(p, &t)?;
        return Ok(t);
    }
    let f = save_folders(p);
    match f.len() {
        1 => Ok(f[0].clone()),
        0 => Err("no co-op save folder (saves\\Saved_*\\SaveGames) exists to restore into".into()),
        _ => Err("this older backup does not say which game version's save it is, and there are several (saves\\Saved_*): restore it by hand".into()),
    }
}

// ---- is a game from this co-op folder running? ----

type Handle = *mut std::ffi::c_void;

#[repr(C)]
struct ProcessEntry32W {
    size: u32,
    usage: u32,
    pid: u32,
    heap: usize,
    module: u32,
    threads: u32,
    parent: u32,
    pri: i32,
    flags: u32,
    exe: [u16; 260],
}
#[link(name = "kernel32")]
extern "system" {
    fn CreateToolhelp32Snapshot(flags: u32, pid: u32) -> Handle;
    fn Process32FirstW(h: Handle, e: *mut ProcessEntry32W) -> i32;
    fn Process32NextW(h: Handle, e: *mut ProcessEntry32W) -> i32;
    fn OpenProcess(access: u32, inherit: i32, pid: u32) -> Handle;
    fn QueryFullProcessImageNameW(h: Handle, flags: u32, name: *mut u16, len: *mut u32) -> i32;
    fn CloseHandle(h: Handle) -> i32;
}

/// The full paths of the running Dread Delusion games (any folder).
pub fn running_games() -> Vec<String> {
    let mut out = Vec::new();
    unsafe {
        let snap = CreateToolhelp32Snapshot(2, 0);
        if snap as isize == -1 || snap.is_null() {
            return out;
        }
        let mut e: ProcessEntry32W = std::mem::zeroed();
        e.size = std::mem::size_of::<ProcessEntry32W>() as u32;
        let mut ok = Process32FirstW(snap, &mut e);
        while ok != 0 {
            let n = e.exe.iter().position(|c| *c == 0).unwrap_or(260);
            let name = String::from_utf16_lossy(&e.exe[..n]).to_lowercase();
            if name.starts_with("dollsnest") {
                let h = OpenProcess(0x1000, 0, e.pid);
                if !h.is_null() {
                    let mut buf = [0u16; 1024];
                    let mut len = buf.len() as u32;
                    if QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len) != 0 {
                        out.push(String::from_utf16_lossy(&buf[..len as usize]));
                    } else {
                        out.push(name.clone());
                    }
                    CloseHandle(h);
                } else {
                    out.push(name.clone());
                }
            }
            ok = Process32NextW(snap, &mut e);
        }
        CloseHandle(snap);
    }
    out
}

/// A game started from this co-op folder is running (an unknown path counts as running: the safe answer).
pub fn game_running(p: &Paths) -> bool {
    let base = lower(&p.root.join("game")) + "\\";
    running_games().iter().any(|g| {
        let lg = g.replace('/', "\\").to_lowercase();
        !lg.contains('\\') || lg.starts_with(&base)
    })
}

// ---- making a backup here (by hand, or before a restore) ----

fn now_stamp() -> (String, String, u64) {
    #[repr(C)]
    #[derive(Default)]
    struct SystemTime { y: u16, m: u16, dow: u16, d: u16, h: u16, mi: u16, s: u16, ms: u16 }
    #[link(name = "kernel32")]
    extern "system" {
        fn GetLocalTime(t: *mut SystemTime);
    }
    let mut t = SystemTime::default();
    unsafe { GetLocalTime(&mut t) };
    let unix = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    (
        format!("{:04}{:02}{:02}-{:02}{:02}{:02}", t.y, t.m, t.d, t.h, t.mi, t.s),
        format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}", t.y, t.m, t.d, t.h, t.mi, t.s),
        unix,
    )
}

fn write_checked(path: &Path, data: &[u8]) -> Result<(), String> {
    if let Some(d) = path.parent() {
        fs::create_dir_all(d).map_err(|e| format!("{}: {e}", d.display()))?;
    }
    fs::write(path, data).map_err(|e| format!("{}: {e}", path.display()))?;
    let back = fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
    if back.len() != data.len() || fnv64(&back) != fnv64(data) {
        return Err(format!("{} did not read back the same", path.display()));
    }
    Ok(())
}

fn own_world_files(p: &Paths) -> Vec<PathBuf> {
    let mut v = Vec::new();
    if let Ok(rd) = fs::read_dir(&p.win64) {
        for e in rd.flatten() {
            let n = e.file_name().to_string_lossy().to_string();
            if n.starts_with("DollsMP-own-world-") && n.ends_with(".bin") && e.path().is_file() {
                v.push(e.path());
            }
        }
    }
    v
}

/// Copy `save_games` into a new, checked backup (kind "manual" or "before-restore").
pub fn make(p: &Paths, save_games: &Path, kind: &str, reason: &str) -> Result<Backup, String> {
    safe_target(p, save_games)?;
    let mut files = Vec::new();
    walk(save_games, Path::new(""), &mut files, 0);
    if files.is_empty() {
        return Err(format!("{} is empty: nothing to back up", save_games.display()));
    }
    fs::create_dir_all(&p.backups).map_err(|e| e.to_string())?;
    let (mut stamp, mut made, mut unix) = now_stamp();
    while p.backups.join(&stamp).exists() {
        std::thread::sleep(std::time::Duration::from_millis(300));
        (stamp, made, unix) = now_stamp();
    }
    let part = p.backups.join(format!("{stamp}.part"));
    let _ = fs::remove_dir_all(&part);
    let result = (|| -> Result<Backup, String> {
        let mut lines = String::new();
        let mut bytes = 0u64;
        let mut newest: Option<(std::time::SystemTime, String)> = None;
        for (rel, _) in &files {
            let src = save_games.join(rel);
            let data = fs::read(&src).map_err(|e| format!("{}: {e}", src.display()))?;
            write_checked(&part.join(rel), &data)?;
            bytes += data.len() as u64;
            lines += &format!("file = {} | {} | {:016X}\r\n", rel.to_string_lossy().replace('/', "\\"), data.len(), fnv64(&data));
            if rel.components().count() > 1 {
                if let Ok(t) = fs::metadata(&src).and_then(|m| m.modified()) {
                    if newest.as_ref().map_or(true, |n| t > n.0) {
                        newest = Some((t, rel.components().next().unwrap().as_os_str().to_string_lossy().to_string()));
                    }
                }
            }
        }
        for f in own_world_files(p) {
            let data = fs::read(&f).map_err(|e| format!("{}: {e}", f.display()))?;
            let rel = format!("{OWN_DIR}\\{}", f.file_name().unwrap().to_string_lossy());
            write_checked(&part.join(&rel), &data)?;
            lines += &format!("file = {} | {} | {:016X}\r\n", rel, data.len(), fnv64(&data));
        }
        let head = format!(
            "; DollsMP save backup (doc 107). DelusionMP-Launcher.exe lists and restores these: Backups page.\r\n\
             version = 1\r\nstamp = {stamp}\r\nmade = {made}\r\nmade_unix = {unix}\r\nkind = {kind}\r\nreason = {reason}\r\n\
             role = launcher\r\npair = \r\npartner = \r\nlevel = \r\ncycle = -1\r\nslot = {}\r\nsource = {}\r\nfiles = {}\r\nbytes = {bytes}\r\nverified = 1\r\n",
            newest.map(|n| n.1).unwrap_or_default(),
            save_games.display(),
            files.len()
        );
        write_checked(&part.join(MANIFEST), (head + &lines).as_bytes())?;
        let fin = p.backups.join(&stamp);
        fs::rename(&part, &fin).map_err(|e| format!("could not finish the backup folder: {e}"))?;
        let mut b = Backup { dir: fin.clone(), stamp: stamp.clone(), ..Default::default() };
        parse_manifest(&fs::read_to_string(fin.join(MANIFEST)).unwrap_or_default(), &mut b);
        verify(&b)?;
        Ok(b)
    })();
    if result.is_err() {
        let _ = fs::remove_dir_all(&part);
    }
    result
}

/// Put a backup back. Returns what was done, in words.
pub fn restore(p: &Paths, b: &Backup) -> Result<String, String> {
    let target = target_of(p, b)?;
    if game_running(p) {
        return Err("a co-op game is running: close it first (a running game would write its own save over the restored one)".into());
    }
    verify(b).map_err(|e| format!("the backup did not pass its check: {e}"))?;
    let parent = target.parent().ok_or("no parent folder")?.to_path_buf();
    fs::create_dir_all(&parent).map_err(|e| e.to_string())?;

    // 1. the save as it is now, so this can be undone
    let mut undo = String::new();
    if target.is_dir() {
        let mut cur = Vec::new();
        walk(&target, Path::new(""), &mut cur, 0);
        if !cur.is_empty() {
            let pre = make(p, &target, "before-restore", &format!("the save as it was before backup {} was restored", b.stamp))?;
            undo = pre.stamp;
        }
    }

    // 2. the backup into a new folder beside SaveGames, checked
    let fresh = parent.join("SaveGames.restore-new");
    let old = parent.join("SaveGames.restore-old");
    let _ = fs::remove_dir_all(&fresh);
    let _ = fs::remove_dir_all(&old);
    let mut own: Vec<(String, Vec<u8>)> = Vec::new();
    let mut files = Vec::new();
    if b.legacy {
        walk(&b.dir, Path::new(""), &mut files, 0);
        files.retain(|(r, _)| r.to_string_lossy() != MANIFEST && r.to_string_lossy() != PIN);
    } else {
        files = b.entries.iter().map(|e| (PathBuf::from(&e.rel), e.size)).collect();
    }
    for (rel, _) in &files {
        let rs = rel.to_string_lossy().replace('/', "\\");
        let data = fs::read(b.dir.join(rel)).map_err(|e| format!("{rs}: {e}"))?;
        if let Some(name) = rs.strip_prefix(&format!("{OWN_DIR}\\")) {
            own.push((name.to_string(), data));
            continue;
        }
        if let Err(e) = write_checked(&fresh.join(rel), &data) {
            let _ = fs::remove_dir_all(&fresh);
            return Err(e);
        }
    }

    // 3. swap: SaveGames -> .restore-old, .restore-new -> SaveGames, then the old one goes
    if target.exists() {
        fs::rename(&target, &old).map_err(|e| {
            let _ = fs::remove_dir_all(&fresh);
            format!("the current save folder could not be moved aside (is something holding it open?): {e}")
        })?;
    }
    if let Err(e) = fs::rename(&fresh, &target) {
        let _ = fs::rename(&old, &target); // put it back as it was
        return Err(format!("the restored save could not be put in place (nothing was changed): {e}"));
    }
    let _ = fs::remove_dir_all(&old);

    // 4. the mod's own-world files (doc 68): as they were at the backup (none then = none now)
    if !b.legacy {
        for f in own_world_files(p) {
            let _ = fs::remove_file(f);
        }
        for (name, data) in &own {
            if name.starts_with("DollsMP-own-world-") && !name.contains('\\') && !name.contains('/') {
                write_checked(&p.win64.join(name), data)?;
            }
        }
    }
    let note = format!(
        "restored = {}\r\npair = {}\r\nat = {}\r\nbefore_restore_backup = {}\r\n",
        b.stamp,
        b.pair,
        now_stamp().1,
        undo
    );
    let _ = fs::write(p.backups.join("last-restore.txt"), note);
    let mut msg = format!("Restored the backup of {} ({}) into {}.", b.made, b.moment(), target.display());
    if !undo.is_empty() {
        msg += &format!(" The save as it was is kept as backup {undo} (\"Before a restore\"): restore that one to undo this.");
    }
    if !b.pair.is_empty() {
        msg += &format!(" Your partner must restore pair {} too.", b.pair_short());
    }
    Ok(msg)
}

pub fn set_pinned(b: &Backup, on: bool) -> io::Result<()> {
    let f = b.dir.join(PIN);
    if on {
        fs::write(f, b"kept: the mod never deletes this backup\r\n")
    } else if f.exists() {
        fs::remove_file(f)
    } else {
        Ok(())
    }
}

/// Delete one backup (only a folder named like one, inside the backups folder).
pub fn delete(p: &Paths, b: &Backup) -> Result<(), String> {
    if !is_stamp(&b.stamp) || b.dir != p.backups.join(&b.stamp) {
        return Err("not a backup folder".into());
    }
    fs::remove_dir_all(&b.dir).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("dmp-bk-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn fnv_matches_the_mod() {
        assert_eq!(fnv64(b"a"), 0xAF63_DC4C_8601_EC8C);
    }

    #[test]
    fn manifest_parses() {
        let t = "; c\r\nversion = 1\r\nkind = level\r\npair = 20261112-153012-7F3A\r\nlevel = M02_P\r\ncycle = 1\r\nverified = 1\r\npartner_confirmed = 1\r\nfile = S1\\Persistent.sav | 5 | 00000000000000FF\r\n";
        let mut b = Backup::default();
        parse_manifest(t, &mut b);
        assert_eq!(b.kind, "level");
        assert_eq!(b.pair_short(), "153012-7F3A");
        assert_eq!(b.level_short(), "M02");
        assert!(b.verified && b.confirmed);
        assert_eq!(b.entries.len(), 1);
        assert_eq!(b.entries[0].rel, "S1\\Persistent.sav");
        assert_eq!(b.entries[0].fnv, 0xFF);
    }

    #[test]
    fn targets_are_guarded() {
        let root = tmp("guard");
        let p = paths(&root);
        assert!(safe_target(&p, &root.join("saves").join("Saved_coop13").join("SaveGames")).is_ok());
        assert!(safe_target(&p, &root.join("saves").join("Saved_coop13")).is_err());
        assert!(safe_target(&p, &root.join("saves").join("x").join("SaveGames")).is_err());
        assert!(safe_target(&p, &root.join("other").join("Saved_coop").join("SaveGames")).is_err());
        if let Some(m) = main_save() {
            assert!(safe_target(&p, &m.join("Saved").join("SaveGames")).is_err());
        }
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn make_restore_round_trip() {
        let root = tmp("rt");
        let p = paths(&root);
        let sg = root.join("saves").join("Saved_coop13").join("SaveGames");
        fs::create_dir_all(sg.join("SLOT")).unwrap();
        fs::create_dir_all(&p.win64).unwrap();
        fs::write(sg.join("SLOT").join("Persistent.sav"), b"one").unwrap();
        fs::write(sg.join("LocalProfile.sav"), b"profile").unwrap();
        fs::write(p.win64.join("DollsMP-own-world-00000001.bin"), b"own1").unwrap();
        let b1 = make(&p, &sg, "manual", "test").unwrap();
        assert!(verify(&b1).is_ok());
        assert_eq!(b1.entries.len(), 3);
        // the game moves on: a new slot file, another own-world file
        fs::write(sg.join("SLOT").join("Persistent.sav"), b"two-later").unwrap();
        fs::write(sg.join("SLOT").join("Extra.sav"), b"x").unwrap();
        fs::write(p.win64.join("DollsMP-own-world-00000002.bin"), b"own2").unwrap();
        std::thread::sleep(std::time::Duration::from_millis(1100)); // a new stamp for the before-restore backup
        let msg = restore(&p, &b1).unwrap();
        assert!(msg.contains("Before a restore"), "{msg}");
        assert_eq!(fs::read(sg.join("SLOT").join("Persistent.sav")).unwrap(), b"one");
        assert!(!sg.join("SLOT").join("Extra.sav").exists(), "a file made after the backup is gone");
        assert!(p.win64.join("DollsMP-own-world-00000001.bin").exists());
        assert!(!p.win64.join("DollsMP-own-world-00000002.bin").exists());
        let all = list(&p);
        assert_eq!(all.len(), 2);
        let pre = all.iter().find(|b| b.kind == "before-restore").unwrap();
        assert!(pre.entries.iter().any(|e| e.rel.ends_with("Extra.sav")));
        // a damaged backup is refused
        fs::write(b1.dir.join("SLOT").join("Persistent.sav"), b"tampered").unwrap();
        assert!(restore(&p, &b1).is_err());
        // pin, delete
        set_pinned(pre, true).unwrap();
        assert!(list(&p).iter().any(|b| b.pinned));
        delete(&p, &b1).unwrap();
        assert_eq!(list(&p).len(), 1);
        let _ = fs::remove_dir_all(&root);
    }
}
