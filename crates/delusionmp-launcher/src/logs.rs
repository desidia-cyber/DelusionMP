//! One step to send logs. `DelusionMP-Coop.exe --logs` (or `DelusionMP-Share-Logs.cmd`, or
//! on its own after a game that did not end cleanly) puts everything needed to diagnose a bug in
//! one zip beside the launcher, and shows it in Explorer:
//!
//!   info.txt            launcher version, time, game install + build verdict, the mod's dll ids,
//!                       Windows version, the the co-op folder in use
//!   launcher/           logs\*.log (launcher, relay, sidecar)
//!   mod/                BepInEx logs and DelusionMP's own log/config files
//!   game/               the game's own logs and its newest two crash reports (minidumps)
//!   DelusionMP-Coop.ini
//!
//! Large logs keep their END (where the bug is). Text is cleaned before it is packed: IPv4
//! addresses become <ip> (127.x and 0.0.0.0 stay) and the Windows user / computer name become
//! <user> / <pc>, so a bundle can be posted where others read it. Saves are never included.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

pub struct Sources {
    pub root: PathBuf,
    pub logs: PathBuf,
    pub win64: PathBuf,
    pub user_dir: PathBuf,
    pub suffix: &'static str, // doc 72: the co-op save's folder suffix (coop / coop13)
}

const TEXT_MAX: u64 = 24 << 20; // a text log keeps its last 24 MB
const FILE_MAX: u64 = 96 << 20; // a crash file bigger than this is left out (listed in info.txt)

/// "20261024-071530" (UTC), for file names.
pub fn stamp() -> String {
    let t = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    let (days, s) = ((t / 86400) as i64, t % 86400);
    // civil from days (Howard Hinnant)
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + if m <= 2 { 1 } else { 0 };
    format!("{y:04}{m:02}{d:02}-{:02}{:02}{:02}", s / 3600, s / 60 % 60, s % 60)
}

/// IPv4 addresses -> <ip> (loopback and 0.0.0.0 kept: they say how the session was set up).
pub fn redact_ips(text: &str) -> String {
    let b = text.as_bytes();
    let mut out = String::with_capacity(text.len());
    let mut i = 0;
    let mut copied = 0;
    while i < b.len() {
        let starts = b[i].is_ascii_digit() && (i == 0 || !(b[i - 1].is_ascii_digit() || b[i - 1] == b'.'));
        if starts {
            let mut j = i;
            let mut parts = [0u32; 4];
            let mut ok = true;
            for (k, part) in parts.iter_mut().enumerate() {
                let s = j;
                while j < b.len() && b[j].is_ascii_digit() && j - s < 4 {
                    *part = *part * 10 + (b[j] - b'0') as u32;
                    j += 1;
                }
                if j == s || j - s > 3 || *part > 255 {
                    ok = false;
                    break;
                }
                if k < 3 {
                    if j < b.len() && b[j] == b'.' {
                        j += 1;
                    } else {
                        ok = false;
                        break;
                    }
                }
            }
            let ends = j >= b.len() || !(b[j].is_ascii_digit() || (b[j] == b'.' && j + 1 < b.len() && b[j + 1].is_ascii_digit()));
            if ok && ends {
                if parts[0] != 127 && parts != [0, 0, 0, 0] {
                    out.push_str(&text[copied..i]);
                    out.push_str("<ip>");
                    copied = j;
                }
                i = j;
                continue;
            }
        }
        i += 1;
    }
    out.push_str(&text[copied..]);
    out
}

fn replace_ci(text: &str, what: &str, with: &str) -> String {
    if what.len() < 3 {
        return text.to_string();
    }
    let lt = text.to_ascii_lowercase();
    let lw = what.to_ascii_lowercase();
    let mut out = String::with_capacity(text.len());
    let mut at = 0;
    while let Some(p) = lt[at..].find(&lw) {
        out.push_str(&text[at..at + p]);
        out.push_str(with);
        at += p + lw.len();
    }
    out.push_str(&text[at..]);
    out
}

pub fn clean_text(text: &str) -> String {
    let mut t = redact_ips(text);
    for (var, with) in [("USERNAME", "<user>"), ("COMPUTERNAME", "<pc>")] {
        if let Ok(v) = std::env::var(var) {
            if v.is_ascii() {
                t = replace_ci(&t, &v, with);
            }
        }
    }
    t
}

fn is_text(p: &Path) -> bool {
    let e = p.extension().and_then(|e| e.to_str()).unwrap_or("").to_ascii_lowercase();
    matches!(e.as_str(), "log" | "txt" | "ini" | "xml" | "json" | "runtime-xml" | "csv")
}

/// Copy one file into the bundle (text: its last TEXT_MAX bytes, cleaned). Returns a note line.
fn put(src: &Path, dst_dir: &Path, notes: &mut Vec<String>) {
    let Ok(meta) = fs::metadata(src) else { return };
    if !meta.is_file() {
        return;
    }
    let name = src.file_name().map(|n| n.to_owned()).unwrap_or_default();
    let dst = dst_dir.join(&name);
    let _ = fs::create_dir_all(dst_dir);
    if is_text(src) {
        let Ok(mut data) = fs::read(src) else { return };
        let len = data.len() as u64;
        if len > TEXT_MAX {
            data.drain(..(len - TEXT_MAX) as usize);
            notes.push(format!("{}: last {} MB of {} MB kept", src.display(), TEXT_MAX >> 20, len >> 20));
        }
        let _ = fs::write(&dst, clean_text(&String::from_utf8_lossy(&data)));
    } else if meta.len() > FILE_MAX {
        notes.push(format!("{}: {} MB, left out (too big)", src.display(), meta.len() >> 20));
    } else {
        let _ = fs::copy(src, &dst);
    }
}

fn files_in(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = fs::read_dir(dir)
        .map(|r| r.flatten().map(|e| e.path()).filter(|p| p.is_file()).collect())
        .unwrap_or_default();
    v.sort();
    v
}

fn newest_dirs(dir: &Path, n: usize) -> Vec<PathBuf> {
    let mut v: Vec<(SystemTime, PathBuf)> = fs::read_dir(dir)
        .map(|r| {
            r.flatten()
                .filter(|e| e.path().is_dir())
                .map(|e| (e.metadata().and_then(|m| m.modified()).unwrap_or(UNIX_EPOCH), e.path()))
                .collect()
        })
        .unwrap_or_default();
    v.sort();
    v.into_iter().rev().take(n).map(|x| x.1).collect()
}

/// Build the bundle. `info` is the launcher's own summary (versions, verdicts). Returns the zip
/// (or, when no zip tool works, the folder).
pub fn collect(src: &Sources, info: &str) -> Result<PathBuf, String> {
    let name = format!("DelusionMP-logs-{}", stamp());
    let stage = src.logs.join(&name);
    let _ = fs::remove_dir_all(&stage);
    fs::create_dir_all(&stage).map_err(|e| format!("create {}: {e}", stage.display()))?;
    let mut notes: Vec<String> = Vec::new();

    for f in files_in(&src.logs) {
        put(&f, &stage.join("launcher"), &mut notes);
    }
    for f in files_in(&src.win64) {
        let n = f.file_name().and_then(|n| n.to_str()).unwrap_or("").to_ascii_lowercase();
        if (n.starts_with("dollsmp") && (n.ends_with(".log") || n.ends_with(".ini") || n.ends_with(".txt")))
            || n.ends_with(".dmp")
        {
            put(&f, &stage.join("mod"), &mut notes);
        }
    }
    for f in ["dmp_status.txt", "dmp_role.txt"] {
        put(&src.win64.join("BepInEx").join("logs").join(f), &stage.join("mod"), &mut notes);
    }
    put(&src.root.join(crate::INI_NAME), &stage, &mut notes);
    let saved = src.user_dir.join(format!("Saved_{}", src.suffix));
    for f in files_in(&saved.join("Logs")) {
        put(&f, &stage.join("game").join("Logs"), &mut notes);
    }
    for d in newest_dirs(&saved.join("Crashes"), 2) {
        let dn = d.file_name().map(|n| n.to_owned()).unwrap_or_default();
        for f in files_in(&d) {
            put(&f, &stage.join("game").join("Crashes").join(&dn), &mut notes);
        }
    }
    let switches: Vec<String> = files_in(&src.win64)
        .iter()
        .filter_map(|p| p.file_name().and_then(|n| n.to_str()).map(str::to_string))
        .filter(|n| {
            let l = n.to_ascii_lowercase();
            l.starts_with("dollsmp-") && (l.ends_with(".on") || l.ends_with(".off"))
        })
        .collect();
    let ver = Command::new("cmd").args(["/c", "ver"]).output().map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string()).unwrap_or_default();
    let text = format!(
        "DelusionMP log bundle\r\n{info}\r\nWindows: {ver}\r\nswitch files beside the game: {}\r\n{}\r\n",
        if switches.is_empty() { "(none)".to_string() } else { switches.join(", ") },
        notes.iter().map(|n| format!("note: {n}")).collect::<Vec<_>>().join("\r\n"),
    );
    let _ = fs::write(stage.join("info.txt"), clean_text(&text));

    let zip = src.root.join(format!("{name}.zip"));
    let _ = fs::remove_file(&zip);
    // tar.exe ships with Windows 10 (1803+) and writes a zip for a .zip name; PowerShell otherwise
    let tar_ok = Command::new("tar")
        .args(["-a", "-c", "-f"])
        .arg(&zip)
        .arg("-C")
        .arg(&stage)
        .arg(".")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
        && zip.is_file();
    let ok = tar_ok
        || (Command::new("powershell")
            .args(["-NoProfile", "-NonInteractive", "-Command"])
            .arg(format!(
                "Compress-Archive -Path '{}\\*' -DestinationPath '{}' -Force",
                stage.display().to_string().replace('\'', "''"),
                zip.display().to_string().replace('\'', "''")
            ))
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
            && zip.is_file());
    if ok {
        let _ = fs::remove_dir_all(&stage);
        Ok(zip)
    } else {
        Ok(stage)
    }
}

/// Show the bundle in Explorer (selected), for the player to drag into a message.
pub fn reveal(p: &Path) {
    let _ = Command::new("explorer").arg(format!("/select,{}", p.display())).spawn();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ips_are_hidden_but_not_versions_or_loopback() {
        assert_eq!(redact_ips("server 192.168.1.20:27050 ok"), "server <ip>:27050 ok");
        assert_eq!(redact_ips("bind 0.0.0.0:27050 relay 127.0.0.1:27050"), "bind 0.0.0.0:27050 relay 127.0.0.1:27050");
        assert_eq!(redact_ips("UE 4.27.2 build 1.2.3.4.5"), "UE 4.27.2 build 1.2.3.4.5");
        assert_eq!(redact_ips("a 999.1.1.1 b 10.0.0.1"), "a 999.1.1.1 b <ip>");
        assert_eq!(redact_ips("10.0.0.1"), "<ip>");
    }

    #[test]
    fn names_are_hidden_case_insensitively() {
        assert_eq!(replace_ci(r"C:\Users\Alvibo\x ALVIBO", "alvibo", "<user>"), r"C:\Users\<user>\x <user>");
        assert_eq!(replace_ci("ab", "ab", "x"), "ab"); // too short to replace safely
    }

    #[test]
    fn stamp_is_a_file_name() {
        let s = stamp();
        assert_eq!(s.len(), 15);
        assert!(s.chars().all(|c| c.is_ascii_digit() || c == '-'));
    }
}
