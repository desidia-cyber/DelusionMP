//! DelusionMP-Coop.exe -- starts Dread Delusion with the co-op mod, on its own session.
//!
//! The normal install is never written to. The co-op game lives next to this exe in `game\`:
//! the large `Dread Delusion_Data` folder is a junction into the install (read only), the small
//! top-level files are copied, and BepInEx + DelusionMP are installed into that copy. The mod
//! redirects the game's save folder with `-ddmp-slot=1`, so the single-player save is untouched.
//!
//! Steam: the bundled Steam tunnel carries the game's UDP datagrams between the two machines.
//! Both players enter the same co-op password; the host opens a Steam lobby keyed by a hash of
//! it and the joiner finds that lobby by password (no Steam friendship needed). LAN uses the
//! game's UDP port directly.
//!
//! Everything else (the Steam API, the log bundle, save backups) is reused from the launcher.

use std::collections::BTreeMap;
use std::fs;
use std::io::{self, BufRead, Write};
use std::net::{SocketAddr, UdpSocket};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::os::windows::process::CommandExt;
use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};
use std::sync::Arc;
use std::time::{Duration, Instant};

mod backups;
mod logs;
mod monitors;
mod rules_table;
mod sha;
mod steam;
mod win;

const INI_NAME: &str = "DelusionMP-Coop.ini";
const GAME_DIR: &str = "windows_content";
const GAME_EXE: &str = "Dread Delusion.exe";
const SAVE_SLOT: &str = "1";

const DEFAULT_INI: &str = "\
; DelusionMP co-op launcher settings. Lines starting with ; are comments.
[Launcher]
; your name, shown to your partner over their screen
name = Player
; host = you start the session (your world is the session's world)
; join = you connect to your partner's session
mode = host
; lan = over the network by IP address
; steam = through Steam: no port to open; both players enter the same co-op password
network = lan
; network = steam: whose Steam identity to use. game = Dread Delusion (you own it on Steam).
; spacewar = Valve's free test app 480, for a copy not bought on Steam.
steam_identity = game
; Spacewar needs a 64-bit steam_api64.dll from Steam. Empty = found in the game or in Steam.
steam_dll =
; network = steam: the SAME password on both machines. The host opens a Steam lobby carrying
; only a hash of it; empty = Steam friends only. Use a long, unique one.
password =
; join: your partner's address (IP or host name). host: not used.
server = 127.0.0.1
; the UDP port of the session (the host must let it through the firewall / router on LAN)
port = 27050
; host: the address the session listens on. 0.0.0.0 = every network card.
bind = 0.0.0.0
; the Dread Delusion install (the folder containing windows_content). Empty = found in Steam.
game =
; window: fullscreen (the game's own setting) or e.g. 1600x900 for a window
window = fullscreen
; monitor: which screen a windowed game opens on (1 = the main screen)
monitor = 1
";

// ---------------------------------------------------------------- settings

fn steam_dll(game: &Path, s: &Settings, pkg: &Path) -> Vec<PathBuf> {
    let (dlls, forced) = steam::dll_candidates(game, &s.steam_dll, pkg, s.steam_identity == "spacewar");
    let sw = forced || s.steam_identity == "spacewar";
    steam::use_spacewar(sw);
    if sw {
        say("  Steam: starting as Spacewar (app 480); Steam shows you as playing it");
    }
    dlls
}

#[derive(Clone, Debug)]
struct Settings {
    name: String,
    mode: String,
    network: String,
    steam_identity: String,
    steam_dll: String,
    password: String,
    server: String,
    port: u16,
    bind: String,
    game: String,
    window: String,
    monitor: usize,
}

fn parse_ini(text: &str) -> BTreeMap<String, String> {
    let mut m = BTreeMap::new();
    for line in text.lines() {
        let l = line.trim();
        if l.is_empty() || l.starts_with(';') || l.starts_with('#') || l.starts_with('[') {
            continue;
        }
        if let Some((k, v)) = l.split_once('=') {
            m.insert(k.trim().to_ascii_lowercase(), v.trim().to_string());
        }
    }
    m
}

fn settings_from(m: &BTreeMap<String, String>) -> Settings {
    let g = |k: &str, d: &str| m.get(k).cloned().filter(|v| !v.is_empty()).unwrap_or_else(|| d.to_string());
    Settings {
        name: g("name", "Player"),
        mode: g("mode", "host").to_ascii_lowercase(),
        network: g("network", "lan").to_ascii_lowercase(),
        steam_identity: g("steam_identity", "game").to_ascii_lowercase(),
        steam_dll: m.get("steam_dll").cloned().unwrap_or_default(),
        password: m.get("password").cloned().unwrap_or_default(),
        server: g("server", "127.0.0.1"),
        port: g("port", "27050").parse().unwrap_or(27050),
        bind: g("bind", "0.0.0.0"),
        game: m.get("game").cloned().unwrap_or_default(),
        window: g("window", "fullscreen").to_ascii_lowercase(),
        monitor: g("monitor", "1").parse().unwrap_or(1),
    }
}

/// A name the partner's screen can carry: printable, at most 24 characters.
fn clean_name(s: &str) -> String {
    let t: String = s.chars().filter(|c| !c.is_control() && *c != '"').take(24).collect();
    let t = t.trim().to_string();
    if t.is_empty() { "Player".into() } else { t }
}

struct Opts {
    root: Option<PathBuf>,
    setup_only: bool,
    background: bool,
    share_logs: bool,
    steam_check: bool,
    selftest: Option<String>,
    backups: Option<String>,
    answer: Option<bool>,
    over: BTreeMap<String, String>,
}

fn parse_args() -> Result<Opts, String> {
    let a: Vec<String> = std::env::args().skip(1).collect();
    let mut o = Opts { root: None, setup_only: false, background: false, share_logs: false, steam_check: false, selftest: None, backups: None, answer: None, over: BTreeMap::new() };
    let mut i = 0;
    while i < a.len() {
        let val = |i: &mut usize| -> Result<String, String> {
            *i += 1;
            a.get(*i).cloned().ok_or(format!("{} needs a value", a[*i - 1]))
        };
        match a[i].as_str() {
            "--host" => { o.over.insert("mode".into(), "host".into()); }
            "--join" => {
                let v = val(&mut i)?;
                o.over.insert("mode".into(), "join".into());
                match v.rsplit_once(':') {
                    Some((h, p)) if p.parse::<u16>().is_ok() => {
                        o.over.insert("server".into(), h.into());
                        o.over.insert("port".into(), p.into());
                    }
                    _ => { o.over.insert("server".into(), v); }
                }
            }
            "--steam" => { o.over.insert("network".into(), "steam".into()); }
            "--lan" => { o.over.insert("network".into(), "lan".into()); }
            "--name" => { let v = val(&mut i)?; o.over.insert("name".into(), v); }
            "--port" => { let v = val(&mut i)?; o.over.insert("port".into(), v); }
            "--bind" => { let v = val(&mut i)?; o.over.insert("bind".into(), v); }
            "--game" => { let v = val(&mut i)?; o.over.insert("game".into(), v); }
            "--window" => { let v = val(&mut i)?; o.over.insert("window".into(), v); }
            "--root" => o.root = Some(PathBuf::from(val(&mut i)?)),
            "--setup" => o.setup_only = true,
            "--steam-check" => o.steam_check = true,
            "--steam-selftest" => o.selftest = Some(val(&mut i)?),
            "--logs" => o.share_logs = true,
            "--backups" => o.backups = Some(String::new()),
            "--backup-now" => o.backups = Some("+".into()),
            "--restore" => { let v = val(&mut i)?; o.backups = Some(v); }
            "--background" => o.background = true,
            "--yes" => o.answer = Some(true),
            "--no" => o.answer = Some(false),
            "--help" | "-h" | "/?" => return Err(String::new()),
            x => return Err(format!("unknown argument: {x}")),
        }
        i += 1;
    }
    Ok(o)
}

const USAGE: &str = "\
DelusionMP-Coop.exe -- Dread Delusion co-op, on its own save

  DelusionMP-Coop.exe                 start with the settings in DelusionMP-Coop.ini
  DelusionMP-Coop.exe --host          start a session (your partner joins you)
  DelusionMP-Coop.exe --join ADDR[:PORT]   join your partner's session
  --steam          play through Steam (with --join: NAME / SteamID64 / nothing = the friend in-game)
  --lan            play by IP address (the default)
  --name NAME      your name on your partner's screen
  --port N         the session's UDP port (default 27050)
  --bind ADDR      (host) where the session listens (default 0.0.0.0)
  --game DIR       the Dread Delusion install (folder containing windows_content)
  --window WxH     play in a window (or: fullscreen)
  --steam-check    start Steam as the game, show who you are and which friends play, then end
  --setup          prepare the co-op folder only, do not start
  --logs           pack the logs for a bug report into one zip here and show it
  --backups        list the co-op save backups (newest first)
  --backup-now     back up the co-op save now (the game must be closed)
  --restore STAMP  put backup STAMP (YYYYMMDD-HHMMSS) back
";

// ---------------------------------------------------------------- paths

fn lower(p: &Path) -> String {
    let s = p.to_string_lossy().replace('/', "\\").to_ascii_lowercase();
    s.trim_end_matches('\\').to_string()
}

fn is_under(p: &Path, base: &Path) -> bool {
    let (a, b) = (lower(p), lower(base));
    !b.is_empty() && (a == b || a.starts_with(&(b + "\\")))
}

/// The game's own save folder: the launcher never writes in there.
fn main_save_dir() -> Option<PathBuf> {
    std::env::var_os("USERPROFILE").map(|h| PathBuf::from(h).join("AppData").join("LocalLow"))
}

fn writable(p: &Path, game: &Path) -> Result<(), String> {
    if is_under(p, game) {
        return Err(format!("refusing to write inside the game install: {}", p.display()));
    }
    if let Some(m) = main_save_dir() {
        if is_under(p, &m) { return Err(format!("refusing to write in the save's folder: {}", p.display())); }
    }
    Ok(())
}

fn game_exe_of(install: &Path) -> PathBuf { install.join(GAME_DIR).join(GAME_EXE) }

/// The install to use: the one in the ini, else one next to this folder, else in Steam.
fn find_game(s: &Settings, root: &Path) -> Option<PathBuf> {
    let ok = |p: &Path| game_exe_of(p).is_file();
    if !s.game.is_empty() {
        let p = PathBuf::from(&s.game);
        return if ok(&p) { Some(p) } else { None };
    }
    let mut cands: Vec<PathBuf> = Vec::new();
    if let Some(parent) = root.parent() {
        cands.push(parent.join("Dread Delusion"));
        cands.push(root.parent().unwrap_or(root).to_path_buf());
    }
    for steam in win::steam_libraries() {
        cands.push(steam.join("steamapps").join("common").join("Dread Delusion"));
    }
    cands.into_iter().find(|p| ok(p) && !is_under(root, p))
}

// ---------------------------------------------------------------- small file helpers

fn copy_tree(from: &Path, to: &Path, skip: &[&str]) -> io::Result<usize> {
    fs::create_dir_all(to)?;
    let mut n = 0;
    for e in fs::read_dir(from)? {
        let e = e?;
        let name = e.file_name();
        if skip.iter().any(|s| name.to_string_lossy().eq_ignore_ascii_case(s)) { continue; }
        let (src, dst) = (e.path(), to.join(&name));
        if src.is_dir() { n += copy_tree(&src, &dst, skip)?; }
        else if !same_file_meta(&src, &dst) { fs::copy(&src, &dst)?; n += 1; }
    }
    Ok(n)
}

/// Same length and modification time (a copy keeps the source's time): already installed.
fn same_file_meta(a: &Path, b: &Path) -> bool {
    match (fs::metadata(a), fs::metadata(b)) {
        (Ok(x), Ok(y)) => x.len() == y.len() && x.modified().ok() == y.modified().ok(),
        _ => false,
    }
}

fn same_bytes(a: &Path, b: &Path) -> bool {
    match (fs::read(a), fs::read(b)) { (Ok(x), Ok(y)) => x == y, _ => false }
}

/// A directory junction `link` -> `target` (no administrator rights needed).
fn ensure_junction(link: &Path, target: &Path) -> Result<(), String> {
    if let Ok(cur) = fs::read_link(link) {
        if lower(&cur).trim_start_matches("\\\\?\\") == lower(target).trim_start_matches("\\\\?\\") { return Ok(()); }
        fs::remove_dir(link).map_err(|e| format!("cannot replace {}: {e}", link.display()))?;
    } else if link.is_dir() {
        return Err(format!("{} exists and is not a link; move it away", link.display()));
    }
    let st = Command::new("cmd").args(["/C", "mklink", "/J"]).arg(link).arg(target)
        .stdout(Stdio::null()).status().map_err(|e| e.to_string())?;
    if !st.success() || !link.is_dir() {
        return Err(format!("could not link {} -> {}", link.display(), target.display()));
    }
    Ok(())
}

fn ask_yes_no(q: &str) -> bool {
    print!("{q} [y/n] ");
    let _ = io::stdout().flush();
    let mut s = String::new();
    let _ = io::stdin().lock().read_line(&mut s);
    matches!(s.trim().to_ascii_lowercase().as_str(), "y" | "yes" | "j" | "1")
}

static LAUNCH_LOG: std::sync::Mutex<Option<PathBuf>> = std::sync::Mutex::new(None);

fn say(s: &str) {
    println!("{s}");
    if let Ok(g) = LAUNCH_LOG.lock() {
        if let Some(p) = g.as_ref() {
            if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(p) {
                let _ = write!(f, "{s}\r\n");
            }
        }
    }
}

fn open_launch_log(logs: &Path) {
    let _ = fs::create_dir_all(logs);
    let p = logs.join("launcher.log");
    if fs::metadata(&p).map(|m| m.len() > (4 << 20)).unwrap_or(false) {
        let _ = fs::rename(&p, logs.join("launcher.1.log"));
    }
    if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(&p) {
        let _ = write!(f, "\r\n==== DelusionMP-Coop {} started {} (UTC) ====\r\n", env!("CARGO_PKG_VERSION"), logs::stamp());
    }
    if let Ok(mut g) = LAUNCH_LOG.lock() { *g = Some(p); }
}

fn bundle_info(l: &Layout, s: &Settings, game: Option<&Path>, code: Option<u32>) -> String {
    let mut t = format!(
        "time (UTC): {}\r\nlauncher: DelusionMP-Coop {}\r\nco-op folder: {}\r\nmode: {}  port: {}  name: {}\r\n",
        logs::stamp(), env!("CARGO_PKG_VERSION"), l.root.display(), s.mode, s.port, s.name
    );
    match game {
        Some(g) => t += &format!("game install: {}\r\n", g.display()),
        None => t += "game install: not found\r\n",
    }
    let dll = l.coop.join("BepInEx").join("plugins").join("DelusionMP.dll");
    t += &format!("{}: {}\r\n", dll.display(), match fs::read(&dll) { Ok(b) => format!("{} bytes", b.len()), Err(_) => "missing".into() });
    if let Some(c) = code { t += &format!("the game ended with code {c:#x}\r\n"); }
    t
}

fn share_logs(l: &Layout, s: &Settings, game: Option<&Path>, code: Option<u32>, show: bool) -> Option<PathBuf> {
    let src = logs::Sources { root: l.root.clone(), logs: l.logs.clone(), win64: l.coop.clone(), user_dir: l.saves.clone(), suffix: "" };
    match logs::collect(&src, &bundle_info(l, s, game, code)) {
        Ok(p) => { if show { logs::reveal(&p); } Some(p) }
        Err(e) => { say(&format!("  the logs could not be packed: {e}")); None }
    }
}

// ---------------------------------------------------------------- the co-op folder

struct Layout {
    root: PathBuf,
    pkg: PathBuf,
    game: PathBuf,
    coop: PathBuf,
    cfg: PathBuf,
    saves: PathBuf,
    logs: PathBuf,
}

fn layout(root: &Path, pkg: &Path, game: &Path) -> Layout {
    let coop = root.join("game").join(GAME_DIR);
    Layout {
        root: root.to_path_buf(),
        pkg: pkg.to_path_buf(),
        game: game.to_path_buf(),
        cfg: coop.join("BepInEx").join("config").join("delusionmp.coop.cfg"),
        coop,
        saves: root.join("saves"),
        logs: root.join("logs"),
    }
}

fn prepare(l: &Layout) -> Result<(), String> {
    for p in [&l.root, &l.coop, &l.saves, &l.logs] {
        writable(p, &l.game)?;
    }
    fs::create_dir_all(&l.coop).map_err(|e| e.to_string())?;
    fs::create_dir_all(&l.saves).map_err(|e| e.to_string())?;
    fs::create_dir_all(&l.logs).map_err(|e| e.to_string())?;

    // The big data folder is a junction into the install (read only); the small files are copies.
    let src_data = l.game.join(GAME_DIR).join("Dread Delusion_Data");
    if src_data.is_dir() {
        ensure_junction(&l.coop.join("Dread Delusion_Data"), &src_data)?;
    }
    if let Ok(rd) = fs::read_dir(l.game.join(GAME_DIR)) {
        for e in rd.flatten() {
            let (src, dst) = (e.path(), l.coop.join(e.file_name()));
            if src.is_file() && !dst.exists() {
                fs::copy(&src, &dst).map_err(|e| format!("copy game file: {e}"))?;
            }
        }
    }

    // BepInEx + doorstop from this package.
    let payload = l.pkg.join("tools").join("bepinex-payload");
    if !payload.is_dir() {
        return Err(format!("BepInEx payload is missing: {}", payload.display()));
    }
    // The mod's config is the player's (the launcher's Rules/Player pages edit it): never
    // overwritten here; a default is written below only when there is none.
    copy_tree(&payload, &l.coop, &["delusionmp.coop.cfg", "DelusionMP.dll"]).map_err(|e| format!("install BepInEx: {e}"))?;
    // A Steam build started from a copy needs its app id beside it, or the Steam API asks
    // Steam to restart it from the original (unmodded) install. 1574240 = Dread Delusion.
    let appid = l.coop.join("steam_appid.txt");
    if !appid.is_file() {
        fs::write(&appid, "1574240").map_err(|e| format!("write steam_appid.txt: {e}"))?;
    }

    // The mod DLL, latest copy from this package.
    let src_dll = l.pkg.join("mod").join("DelusionMP.dll");
    if !src_dll.is_file() {
        return Err(format!("the mod is missing: {}", src_dll.display()));
    }
    let plugins = l.coop.join("BepInEx").join("plugins");
    fs::create_dir_all(&plugins).map_err(|e| e.to_string())?;
    let dst_dll = plugins.join("DelusionMP.dll");
    if !same_bytes(&src_dll, &dst_dll) {
        fs::copy(&src_dll, &dst_dll).map_err(|e| format!("install DelusionMP.dll: {e} (is the co-op game still running?)"))?;
        say("  installed DelusionMP.dll into the co-op game");
    }

    // The mod's config, before the first start, so it can be edited before the game ever runs.
    if !l.cfg.is_file() {
        fs::write(&l.cfg, rules_table::default_bepinex_cfg()).map_err(|e| e.to_string())?;
        say(&format!("  wrote {} (this mod's settings)", l.cfg.display()));
    }
    Ok(())
}

// ---------------------------------------------------------------- helpers/processes

fn spawn_logged(exe: &Path, args: &[String], log: &Path, job: &win::Job) -> Result<Child, String> {
    let out = fs::File::create(log).map_err(|e| e.to_string())?;
    let err = out.try_clone().map_err(|e| e.to_string())?;
    let child = Command::new(exe).args(args).stdout(out).stderr(err)
        .creation_flags(0x0800_0000).spawn()
        .map_err(|e| format!("start {}: {e}", exe.display()))?;
    job.add(&child);
    Ok(child)
}

/// A small UDP hub for the Steam host: the game sends here (port+1), the Steam tunnel's peer
/// sockets send here too. Each packet is forwarded to the other side. DelusionMP's own transport
/// is plain UDP, so the Steam tunnel needs this bridge on the host machine.
fn start_udp_hub(bind: SocketAddr, game_port: u16) -> Result<Arc<AtomicBool>, String> {
    let sock = UdpSocket::bind(bind).map_err(|e| format!("bind the UDP hub on {bind}: {e}"))?;
    sock.set_nonblocking(true).map_err(|e| e.to_string())?;
    let stop = Arc::new(AtomicBool::new(false));
    let stop_t = stop.clone();
    std::thread::spawn(move || {
        let mut game: Option<SocketAddr> = None;
        let mut peers: Vec<SocketAddr> = Vec::new();
        let mut buf = [0u8; 65536];
        while !stop_t.load(AtomicOrdering::Relaxed) {
            let mut got = false;
            while let Ok((n, from)) = sock.recv_from(&mut buf) {
                got = true;
                if from.port() == game_port {
                    game = Some(from);
                    for p in &peers { let _ = sock.send_to(&buf[..n], p); }
                } else {
                    if !peers.contains(&from) { peers.push(from); }
                    if let Some(g) = game { let _ = sock.send_to(&buf[..n], g); }
                }
            }
            if !got { std::thread::sleep(Duration::from_millis(1)); }
        }
    });
    Ok(stop)
}

fn backups_cli(root: &Path, what: &str, answer: Option<bool>) -> Result<i32, String> {
    let p = backups::paths(root);
    if what.is_empty() {
        let all = backups::list(&p);
        say(&format!("{} backup(s) in {}", all.len(), p.backups.display()));
        for b in &all { say(&format!("  {}  {}  {}", b.stamp, b.made, b.moment())); }
        return Ok(0);
    }
    if what == "+" {
        let sg = backups::save_folders(&p).into_iter().next().ok_or("no co-op save folder yet")?;
        let b = backups::make(&p, &sg, "manual", "made by hand in the launcher")?;
        say(&format!("Backup {} made and checked ({} file(s)).", b.stamp, b.entries.len()));
        return Ok(0);
    }
    let b = backups::list(&p).into_iter().find(|b| b.stamp == what).ok_or_else(|| format!("no backup {what}"))?;
    let target = backups::target_of(&p, &b)?;
    say(&format!("Restore backup {} ({}) into {}?", b.stamp, b.moment(), target.display()));
    let go = match answer { Some(a) => a, None => ask_yes_no("Restore it?") };
    if !go { return Ok(0); }
    backups::restore(&p, &b)?;
    say("Restored.");
    Ok(0)
}

// ---------------------------------------------------------------- the session

fn run() -> Result<i32, String> {
    let o = parse_args();
    let o = match o {
        Ok(o) => o,
        Err(e) => {
            if e.is_empty() { print!("{USAGE}"); return Ok(0); }
            return Err(e);
        }
    };
    let root = o.root.clone().unwrap_or_else(|| std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")));
    let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or(root.clone());
    let ini = root.join(INI_NAME);
    if !ini.is_file() {
        fs::write(&ini, DEFAULT_INI.as_bytes()).map_err(|e| format!("write {}: {e}", ini.display()))?;
        say(&format!("Wrote the settings file {} (name, host/join, network, password, game).", ini.display()));
    }
    let mut m = parse_ini(&fs::read_to_string(&ini).unwrap_or_default());
    m.extend(o.over.clone());
    let mut s = settings_from(&m);
    s.name = clean_name(&s.name);
    // The helpers, the BepInEx payload and the mod live beside this exe in a normal package;
    // fall back to the working folder when the exe is run from elsewhere (setup/tests).
    let pkg = if root.join("tools").join("bepinex-payload").is_dir() { root.clone() }
        else if exe_dir.join("tools").join("bepinex-payload").is_dir() { exe_dir.clone() }
        else if root.join("mod").join("DelusionMP.dll").is_file() { root.clone() }
        else { exe_dir.clone() };

    open_launch_log(&root.join("logs"));
    if let Some(what) = o.backups.clone() { return backups_cli(&root, &what, o.answer); }
    if o.share_logs {
        let game = find_game(&s, &root);
        let l = layout(&root, &pkg, game.as_deref().unwrap_or(Path::new("")));
        if let Some(p) = share_logs(&l, &s, game.as_deref(), None, true) {
            say(&format!("Logs packed: {}", p.display()));
        }
        return Ok(0);
    }
    if let Some(role) = o.selftest.clone() {
        let logs = root.join("logs");
        fs::create_dir_all(&logs).map_err(|e| e.to_string())?;
        let dlls = steam_dll(Path::new(""), &s, &pkg);
        let want = if s.server == "127.0.0.1" { String::new() } else { s.server.clone() };
        let local = steam::start(dlls, &logs, role == "host", format!("127.0.0.1:{}", s.port).parse().unwrap(), &want, &s.password)?;
        say(&format!("TUNNEL {}", local.map(|a| a.to_string()).unwrap_or_else(|| "host".into())));
        let mut line = String::new();
        let _ = io::stdin().lock().read_line(&mut line);
        steam::stop();
        return Ok(0);
    }

    say("DelusionMP co-op launcher");
    let game = find_game(&s, &root).ok_or_else(|| {
        format!("Dread Delusion was not found. Put its folder in {} as  game = G:\\SteamLibrary\\steamapps\\common\\Dread Delusion", ini.display())
    })?;
    if is_under(&root, &game) {
        return Err("put the co-op folder OUTSIDE the game's install folder".into());
    }
    let l = layout(&root, &pkg, &game);
    if o.steam_check {
        let dlls = steam_dll(&game, &s, &pkg);
        fs::create_dir_all(&l.logs).map_err(|e| e.to_string())?;
        steam::check(dlls, &l.logs)?;
        return Ok(0);
    }
    say(&format!("  game install (read only): {}", game.display()));
    prepare(&l)?;
    if o.setup_only {
        say("Co-op folder ready (--setup: not starting).");
        return Ok(0);
    }

    let job = win::Job::new();
    let mut children: Vec<Child> = Vec::new();
    let use_steam = s.network == "steam";
    let mut hub_stop: Option<Arc<AtomicBool>> = None;
    let (host_addr, game_port): (String, u16) = if use_steam && s.mode == "join" {
        let dlls = steam_dll(&game, &s, &pkg);
        let want = if s.server == "127.0.0.1" { String::new() } else { s.server.clone() };
        let local = steam::start(dlls, &l.logs, false, "127.0.0.1:0".parse().unwrap(), &want, &s.password)?
            .ok_or("the Steam tunnel gave no local address")?;
        say(&format!("  Steam: joining through local {}", local));
        ("127.0.0.1".into(), local.port())
    } else if use_steam {
        let dlls = steam_dll(&game, &s, &pkg);
        let hub_port = s.port + 1;
        hub_stop = Some(start_udp_hub(format!("127.0.0.1:{hub_port}").parse().unwrap(), s.port)?);
        say(if s.password.trim().is_empty() {
            "  hosting through Steam (friends; no port to open)"
        } else {
            "  hosting through Steam (same co-op password; no port to open)"
        });
        steam::start(dlls, &l.logs, true, format!("127.0.0.1:{hub_port}").parse().unwrap(), "", &s.password)?;
        ("127.0.0.1".into(), s.port)
    } else if s.mode == "join" {
        (s.server.clone(), s.port)
    } else {
        say(&format!("  hosting: session on UDP {}:{} (your partner joins your IP, port {})", s.bind, s.port, s.port));
        (s.bind.clone(), s.port)
    };

    let role = if s.mode == "join" { "client" } else { "host" };
    let mut args = vec![
        format!("-ddmp-role={role}"),
        format!("-ddmp-port={game_port}"),
        format!("-ddmp-host={host_addr}"),
        format!("-ddmp-name={}", s.name),
        format!("-ddmp-slot={SAVE_SLOT}"),
    ];
    if o.background {
        let (x, y) = win::offscreen_origin();
        args.extend(["-windowed".into(), "-ResX=960".into(), "-ResY=540".into(), format!("-WinX={x}"), format!("-WinY={y}")]);
    } else if let Some((w, h)) = s.window.split_once('x').and_then(|(w, h)| Some((w.trim().parse::<u32>().ok()?, h.trim().parse::<u32>().ok()?))) {
        let screens = monitors::list();
        let m = screens.get(s.monitor.max(1) - 1).unwrap_or(&screens[0]);
        let (x, y) = monitors::centred(m, w, h);
        args.extend(["-windowed".into(), format!("-ResX={w}"), format!("-ResY={h}"), format!("-WinX={x}"), format!("-WinY={y}")]);
    }
    say(&format!("  {} as \"{}\" -> {}:{}", if s.mode == "join" { "joining" } else { "hosting" }, s.name, host_addr, game_port));
    let exe = l.coop.join(GAME_EXE);
    say("  starting Dread Delusion (co-op)... keep the launcher window open while you play.");
    let started = Instant::now();
    let game_proc = win::start(&exe, &args, &l.coop, win::Show::Normal).map_err(|e| format!("start the game: {e}"))?;
    say(&format!("  game PID {}", game_proc.pid));

    let code = loop {
        if let Some(c) = game_proc.wait(1000) { break c; }
        for c in children.iter_mut() {
            if let Ok(Some(x)) = c.try_wait() { say(&format!("  a helper ended early ({x}); see {}", l.logs.display())); }
        }
        children.retain_mut(|c| !matches!(c.try_wait(), Ok(Some(_))));
    };
    for mut c in children { let _ = c.kill(); let _ = c.wait(); }
    if let Some(stop) = hub_stop { stop.store(true, AtomicOrdering::Relaxed); }
    steam::stop();
    say(&format!("  the game ended (code {code:#x}) after {} s", started.elapsed().as_secs()));
    if code != 0 {
        if let Some(p) = share_logs(&l, &s, Some(&game), Some(code), true) {
            say(&format!("  The game did not end normally. Logs packed for a bug report: {}", p.display()));
        }
    } else {
        say("  To report a problem: double-click DelusionMP-Share-Logs.cmd here and send the zip it makes.");
    }
    Ok(code as i32)
}

fn main() {
    let code = match run() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("\nERROR: {e}");
            if std::env::args().all(|a| a != "--background") {
                eprintln!("Press Enter to close.");
                let mut s = String::new();
                let _ = io::stdin().lock().read_line(&mut s);
            }
            1
        }
    };
    steam::stop();
    std::process::exit(code);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ini_and_names() {
        let m = parse_ini(DEFAULT_INI);
        let s = settings_from(&m);
        assert_eq!((s.mode.as_str(), s.port, s.bind.as_str(), s.network.as_str()), ("host", 27050, "0.0.0.0", "lan"));
        assert_eq!(clean_name("  \"Alice\"\u{7} "), "Alice");
        assert_eq!(clean_name(""), "Player");
        assert_eq!(clean_name(&"x".repeat(40)).len(), 24);
    }

    #[test]
    fn paths() {
        let root = Path::new(r"G:\Coop");
        let game = Path::new(r"G:\SteamLibrary\steamapps\common\Dread Delusion");
        assert_eq!(game_exe_of(game), game.join("windows_content").join("Dread Delusion.exe"));
        let l = layout(root, root, game);
        assert_eq!(l.coop, root.join("game").join("windows_content"));
        assert_eq!(l.cfg, l.coop.join("BepInEx").join("config").join("delusionmp.coop.cfg"));
    }
}
