//! DelusionMP-Launcher.exe: the settings window for the co-op launcher.
//!
//! It edits two files in place (comments and the player's own lines are kept) and starts
//! DelusionMP-Coop.exe, which does the real work:
//!   <folder>\DelusionMP-Coop.ini                                    name, host/join, LAN/Steam, password, game, window, Voice
//!   <folder>\game\windows_content\BepInEx\config\delusionmp.coop.cfg   this mod's [Avatar] and [Rules]
//! The rules table mirrors the mod's own BepInEx settings, so the two cannot drift.
#![windows_subsystem = "windows"]

#[path = "../../delusionmp-launcher/src/rules_table.rs"]
mod rules_table;
#[path = "../../delusionmp-launcher/src/monitors.rs"]
mod monitors;
#[path = "../../delusionmp-launcher/src/backups.rs"]
mod backups;
mod ini;

use eframe::egui::{self, Align, Color32, FontData, FontDefinitions, FontFamily, Layout, RichText, Rounding, Stroke, Vec2};
use ini::Ini;
use rules_table::{Kind, Rule, RULES};
use std::io::{BufRead, BufReader};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const BG: Color32 = Color32::from_rgb(0x15, 0x0e, 0x12);
const PANEL: Color32 = Color32::from_rgb(0x0f, 0x0a, 0x0d);
const LINE: Color32 = Color32::from_rgb(0x2e, 0x1c, 0x22);
const FIELD: Color32 = Color32::from_rgb(0x0b, 0x07, 0x09);
const TEXT: Color32 = Color32::from_rgb(0xf2, 0xe6, 0xe4);
const MUTED: Color32 = Color32::from_rgb(0xa8, 0x8e, 0x91);
const ACCENT: Color32 = Color32::from_rgb(0xd8, 0x38, 0x5c);
const OK: Color32 = Color32::from_rgb(0xd9, 0xa4, 0x41);
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(PartialEq, Clone, Copy)]
enum Page { Connection, Rules, Player, Game, Backups, Log }

#[derive(Default)]
struct Run {
    lines: Vec<String>,
    running: bool,
    exit: Option<i32>,
    pid: Option<u32>,
}

struct App {
    root: PathBuf,
    launcher: Ini,
    rules: Ini,
    page: Page,
    // launcher settings
    name: String,
    mode: String,
    network: String,
    server: String,
    /// Steam app to start as: game (Dread Delusion, bought on Steam) or spacewar (a copy from elsewhere)
    identity: String,
    /// co-op password: partners find each other through a Steam lobby that carries only its hash
    password: String,
    /// find the partner by co-op password (true) or by Steam friend (false)
    find_pw: bool,
    /// join over Steam: a friend's name or SteamID64 (empty = the friend playing Dread Delusion); kept apart from `server`, the LAN address
    partner: String,
    port: String,
    bind: String,
    game: String,
    window: String,
    monitor: usize,
    screens: Vec<monitors::Monitor>,
    copy_save: String,
    // rule values, in RULES order
    vals: Vec<i32>,
    dirty: Option<Instant>,
    run: Arc<Mutex<Run>>,
    ask_copy: bool,
    note: String,
    friends: Arc<Mutex<Friends>>,
    art: Option<Art>,
    custom_win: bool,
    scroll_pending: f32,
    bk: Bk,
}

/// doc 107: the Backups page.
#[derive(Default)]
struct Bk {
    list: Vec<backups::Backup>,
    read_at: Option<Instant>,
    sel: Option<String>,
    confirm: Option<String>,
    delete: Option<String>,
    msg: String,
    msg_bad: bool,
    find: String,
    game_running: bool,
}


/// Artwork from Dread Delusion, cropped and darkened into assets/ (page banners and the sidebar).
struct Art {
    banners: Vec<egui::TextureHandle>,
    doll: egui::TextureHandle,
}

fn texture(ctx: &egui::Context, name: &str, png: &[u8]) -> Option<egui::TextureHandle> {
    let im = image::load_from_memory_with_format(png, image::ImageFormat::Png).ok()?.to_rgba8();
    let size = [im.width() as usize, im.height() as usize];
    Some(ctx.load_texture(name, egui::ColorImage::from_rgba_unmultiplied(size, im.as_raw()), egui::TextureOptions::LINEAR))
}

fn load_art(ctx: &egui::Context) -> Option<Art> {
    Some(Art {
        banners: vec![
            texture(ctx, "b0", include_bytes!("../assets/banner_connection.png"))?,
            texture(ctx, "b1", include_bytes!("../assets/banner_rules.png"))?,
            texture(ctx, "b2", include_bytes!("../assets/banner_player.png"))?,
            texture(ctx, "b3", include_bytes!("../assets/banner_game.png"))?,
            texture(ctx, "b4", include_bytes!("../assets/banner_log.png"))?,
        ],
        doll: texture(ctx, "doll", include_bytes!("../assets/side_doll.png"))?,
    })
}

/// A rectangle filled with four corner colours (top-left, top-right, bottom-right, bottom-left).
fn fade(p: &egui::Painter, r: egui::Rect, c: [Color32; 4]) {
    let mut m = egui::epaint::Mesh::default();
    for (pos, col) in [r.left_top(), r.right_top(), r.right_bottom(), r.left_bottom()].into_iter().zip(c) {
        m.colored_vertex(pos, col);
    }
    m.add_triangle(0, 1, 2);
    m.add_triangle(0, 2, 3);
    p.add(egui::Shape::mesh(m));
}

/// An image scaled to cover `r` (cropped, not stretched), anchored at the bottom or top.
fn cover(p: &egui::Painter, t: &egui::TextureHandle, r: egui::Rect, from_bottom: bool) {
    let ts = t.size_vec2();
    let k = (r.width() / ts.x).max(r.height() / ts.y);
    let (vw, vh) = (r.width() / k / ts.x, r.height() / k / ts.y);
    let u0 = (1.0 - vw) / 2.0;
    let v0 = if from_bottom { 1.0 - vh } else { 0.0 };
    p.image(t.id(), r, egui::Rect::from_min_max(egui::pos2(u0, v0), egui::pos2(u0 + vw, v0 + vh)), Color32::WHITE);
}

/// The Steam friends who are in Dread Delusion, asked of `DelusionMP-Coop.exe --steam-check`.
#[derive(Default)]
struct Friends {
    scanning: bool,
    scanned: bool,
    /// (Steam name, SteamID64)
    list: Vec<(String, String)>,
    msg: String,
}

fn exe_dir() -> PathBuf {
    std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or_else(|| PathBuf::from("."))
}

fn parse_key(s: &str) -> Option<i32> {
    let t = s.trim();
    (0..=0xFE).find(|&vk| rules_table::key_name(vk).eq_ignore_ascii_case(t) && !rules_table::key_name(vk).chars().all(|c| c.is_ascii_digit() && t.len() > 1))
}

fn key_choices(r: &Rule) -> Vec<i32> {
    let mut v = Vec::new();
    if r.lo == 0 {
        v.push(0);
        v.extend([0x09, 0x2D, 0x2E, 0x23, 0x21, 0x22, 0x14]);
    }
    v.extend(0x70..=0x7B);
    if r.lo == 0 {
        v.extend(0x41..=0x5A);
        v.extend(0x30..=0x39);
    }
    v.retain(|k| (r.lo..=r.hi).contains(k) || (r.lo == 0 && *k == 0));
    v
}

fn pretty(name: &str) -> String {
    // BepInEx keys are PascalCase (DeathDebuffs); the old mod used snake_case. Both read well.
    let mut split = Vec::new();
    let mut cur = String::new();
    for c in name.chars() {
        if c == '_' {
            if !cur.is_empty() { split.push(std::mem::take(&mut cur)); }
        } else if c.is_uppercase() && !cur.is_empty() {
            split.push(std::mem::take(&mut cur));
            cur.push(c);
        } else {
            cur.push(c);
        }
    }
    if !cur.is_empty() { split.push(cur); }
    let words: Vec<String> = split.into_iter().map(|w| match w.to_ascii_lowercase().as_str() {
        "pvp" => "PvP".to_string(),
        "pct" => "%".to_string(),
        "s" => "(s)".to_string(),
        "m" => "(m)".to_string(),
        "npcs" => "NPCs".to_string(),
        "hud" => "HUD".to_string(),
        "mb" => "(MB)".to_string(),
        _ => w.to_ascii_lowercase(),
    }).collect();
    let mut s = words.join(" ");
    if let Some(c) = s.get(0..1) {
        s = c.to_uppercase() + &s[1..];
    }
    s
}

fn group_of(name: &str) -> &'static str {
    match name {
        "DeathDebuffs" | "MaxStacks" | "PercentPerStack" | "RespawnDelaySeconds" | "RespawnAtPartner" => "Death and respawn",
        "ExclusiveUniqueShopItems" => "Shared world",
        _ => "Session",
    }
}

const GROUP_ORDER: &[&str] = &["Death and respawn", "Shared world", "Session"];

impl App {
    fn new() -> App {
        let root = exe_dir();
        let launcher_path = root.join("DelusionMP-Coop.ini");
        if !launcher_path.is_file() {
            // the same text the launcher would write; saved on the first change
            let _ = std::fs::write(&launcher_path, DEFAULT_LAUNCHER_INI.replace('\n', "\r\n"));
        }
        let launcher = Ini::load(&launcher_path, false);
        let rules_path = root.join("game").join("windows_content").join("BepInEx").join("config").join("delusionmp.coop.cfg");
        let rules = if rules_path.is_file() {
            Ini::load(&rules_path, false)
        } else {
            let r = Ini::from_text(&rules_path, &rules_table::default_bepinex_cfg(), false);
            let _ = r.save(); // so it exists before the game has ever run
            r
        };
        let g = |k: &str, d: &str| launcher.get("Launcher", k).filter(|v| !v.is_empty()).unwrap_or_else(|| d.to_string());
        let vals = RULES
            .iter()
            .map(|r| {
                let text = if r.section == "Voice" {
                    rules.get("Voice", r.name).or_else(|| launcher.get("Voice", r.name))
                } else {
                    rules.get(r.section, r.name)
                };
                text.and_then(|v| match r.kind {
                    Kind::Bool => Some(if v.trim().eq_ignore_ascii_case("true") || v.trim() == "1" { 1 } else { 0 }),
                    Kind::Int if r.scale10 => rules_table::parse_float10(&v),
                    Kind::Choice => Some(rules_table::choice_index_for(r, &v)),
                    _ => v.trim().parse::<i32>().ok(),
                })
                .map(|v| v.clamp(r.lo, r.hi))
                .unwrap_or(r.def)
            })
            .collect();
        let network = g("network", "lan").to_ascii_lowercase();
        // the launcher's one `server =` line is the LAN address or, over Steam, the partner
        let raw_server = g("server", "127.0.0.1");
        let (server, partner) = if network == "steam" {
            ("127.0.0.1".to_string(), if raw_server == "127.0.0.1" || raw_server.eq_ignore_ascii_case("steam") { String::new() } else { raw_server })
        } else {
            (raw_server, String::new())
        };
        App {
            name: g("name", "Player"),
            mode: g("mode", "host").to_ascii_lowercase(),
            network,
            server,
            identity: g("steam_identity", "game").to_ascii_lowercase(),
            find_pw: !launcher.get("Launcher", "password").unwrap_or_default().trim().is_empty(),
            password: launcher.get("Launcher", "password").unwrap_or_default(),
            partner,
            port: g("port", "27050"),
            bind: g("bind", "0.0.0.0"),
            game: launcher.get("Launcher", "game").unwrap_or_default(),
            window: g("window", "fullscreen"),
            monitor: g("monitor", "1").parse().unwrap_or(1),
            screens: monitors::list(),
            copy_save: g("copy_main_save", "ask").to_ascii_lowercase(),
            root,
            launcher,
            rules,
            page: Page::Connection,
            vals,
            dirty: None,
            run: Arc::new(Mutex::new(Run::default())),
            ask_copy: false,
            note: String::new(),
            friends: Arc::new(Mutex::new(Friends::default())),
            art: None,
            custom_win: false,
            scroll_pending: 0.0,
            bk: Bk::default(),
        }
    }

    /// Two hotkeys on the same key cannot both work: the first conflict, as a sentence.
    fn key_conflict(&self) -> Option<String> {
        let keys: Vec<usize> = (0..RULES.len()).filter(|&i| RULES[i].kind == Kind::Key && self.vals[i] != 0).collect();
        for (n, &a) in keys.iter().enumerate() {
            if let Some(&b) = keys[n + 1..].iter().find(|&&b| self.vals[b] == self.vals[a]) {
                return Some(format!("{} and {} are both on {} (Player page)", pretty(RULES[a].name), pretty(RULES[b].name), rules_table::key_name(self.vals[a])));
            }
        }
        None
    }

    fn scan_friends(&mut self) {
        let exe = self.root.join("DelusionMP-Coop.exe");
        {
            let mut f = self.friends.lock().unwrap();
            if f.scanning {
                return;
            }
            f.scanning = true;
            f.msg.clear();
        }
        let (root, friends) = (self.root.clone(), self.friends.clone());
        std::thread::spawn(move || {
            let out = Command::new(&exe).arg("--steam-check").current_dir(&root).creation_flags(CREATE_NO_WINDOW).stdin(Stdio::null()).output();
            let mut f = friends.lock().unwrap();
            f.scanning = false;
            f.scanned = true;
            f.list.clear();
            match out {
                Err(e) => f.msg = format!("could not start DelusionMP-Coop.exe: {e}"),
                Ok(o) => {
                    let text = format!("{}{}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr));
                    for l in text.lines() {
                        if let Some(rest) = l.trim().strip_prefix("friend in Dread Delusion now:") {
                            if let Some((name, id)) = rest.trim().trim_end_matches(')').rsplit_once(" (") {
                                f.list.push((name.trim().to_string(), id.trim().to_string()));
                            }
                        }
                    }
                    if f.list.is_empty() {
                        // Steam's own log lines (minidump, SteamInternal...) are noise; our lines start with two spaces
                        let ours = |l: &&str| l.starts_with("  ") && !l.trim().is_empty() && !l.trim_start().starts_with("Steam: you are");
                        f.msg = text.lines().filter(ours).last().map(|l| l.trim().to_string()).unwrap_or_else(|| "Steam did not answer: is the Steam client running?".to_string());
                    }
                }
            }
        });
    }

    fn touch(&mut self) {
        self.dirty = Some(Instant::now());
    }

    fn save(&mut self) {
        self.dirty = None;
        let server = if self.network == "steam" { if self.find_pw { String::new() } else { self.partner.trim().to_string() } } else { self.server.clone() };
        for (k, v) in [
            ("name", self.name.clone()),
            ("mode", self.mode.clone()),
            ("network", self.network.clone()),
            ("steam_identity", self.identity.clone()),
            ("password", if self.find_pw { self.password.trim().to_string() } else { String::new() }),
            ("server", server),
            ("port", self.port.clone()),
            ("bind", self.bind.clone()),
            ("game", self.game.clone()),
            ("copy_main_save", self.copy_save.clone()),
            ("window", self.window.clone()),
            ("monitor", self.monitor.to_string()),
        ] {
            self.launcher.set("Launcher", k, &v);
        }
        for (r, v) in RULES.iter().zip(&self.vals) {
            let text = rules_table::value_text(r, *v);
            if r.section == "Voice" {
                // The mod reads its BepInEx config on start: write there so the setting works.
                self.rules.set("Voice", r.name, &text);
                self.launcher.set("Voice", r.name, &text);
            } else {
                self.rules.set(r.section, r.name, &text);
            }
        }
        let a = self.launcher.save();
        let b = self.rules.save();
        self.note = match (a, b) {
            (Ok(_), Ok(_)) => "Saved".into(),
            (Err(e), _) | (_, Err(e)) => format!("Could not save: {e}"),
        };
    }

    fn coop_save_exists(&self) -> bool {
        std::fs::read_dir(self.root.join("saves")).map(|d| d.flatten().any(|e| e.path().join("SaveGames").is_dir())).unwrap_or(false)
    }

    fn start(&mut self, args: Vec<String>) {
        self.save();
        let exe = self.root.join("DelusionMP-Coop.exe");
        if !exe.is_file() {
            self.run.lock().unwrap().lines.push(format!("DelusionMP-Coop.exe is not beside this window: {}", exe.display()));
            self.page = Page::Log;
            return;
        }
        {
            let mut r = self.run.lock().unwrap();
            r.lines.clear();
            r.running = true;
            r.exit = None;
        }
        self.page = Page::Log;
        let child = Command::new(&exe).args(&args).current_dir(&self.root).creation_flags(CREATE_NO_WINDOW).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn();
        let run = self.run.clone();
        match child {
            Err(e) => {
                let mut r = run.lock().unwrap();
                r.lines.push(format!("could not start {}: {e}", exe.display()));
                r.running = false;
            }
            Ok(mut c) => {
                run.lock().unwrap().pid = Some(c.id());
                for pipe in [c.stdout.take().map(|p| Box::new(p) as Box<dyn std::io::Read + Send>), c.stderr.take().map(|p| Box::new(p) as Box<dyn std::io::Read + Send>)].into_iter().flatten() {
                    let run = run.clone();
                    std::thread::spawn(move || {
                        for l in BufReader::new(pipe).split(b'\n').flatten() {
                            let s = String::from_utf8_lossy(&l).trim_end_matches('\r').to_string();
                            run.lock().unwrap().lines.push(s);
                        }
                    });
                }
                std::thread::spawn(move || {
                    let code = c.wait().ok().and_then(|s| s.code());
                    std::thread::sleep(Duration::from_millis(300));
                    let mut r = run.lock().unwrap();
                    r.running = false;
                    r.pid = None;
                    r.exit = code;
                });
            }
        }
    }

    fn launch(&mut self) {
        if self.copy_save == "ask" && !self.coop_save_exists() {
            self.ask_copy = true;
            return;
        }
        let flag = match self.copy_save.as_str() {
            "yes" => Some("--yes"),
            "no" => Some("--no"),
            _ => Some("--no"),
        };
        self.start(flag.map(|f| vec![f.to_string()]).unwrap_or_default());
    }
}

const DEFAULT_LAUNCHER_INI: &str = "\
; DelusionMP co-op launcher settings. Lines starting with ; are comments. This window edits them.
[Launcher]
name = Player
mode = host
network = lan
steam_identity = game
password =
server = 127.0.0.1
port = 27050
bind = 0.0.0.0
game =
copy_main_save = ask
window = fullscreen
[Voice]
ChatEnabled = true
ChatKey = T
VoiceChat = true
VoiceMode = 0
VoiceKey = V
VoiceRangeM = 80
VoiceVolumePct = 100
VoiceMicPct = 100
VoiceAwayPct = 40
";

// ---------------------------------------------------------------- small widgets

fn toggle(ui: &mut egui::Ui, on: &mut bool) -> egui::Response {
    let (rect, mut resp) = ui.allocate_exact_size(Vec2::new(34.0, 18.0), egui::Sense::click());
    if resp.clicked() {
        *on = !*on;
        resp.mark_changed();
    }
    let t = ui.ctx().animate_bool(resp.id, *on);
    let bg = Color32::from_rgb(0x3a, 0x25, 0x2c).lerp_to_gamma(ACCENT, t);
    ui.painter().rect_filled(rect, Rounding::same(9.0), bg);
    let x = egui::lerp((rect.left() + 9.0)..=(rect.right() - 9.0), t);
    let knob = Color32::from_rgb(0xb0, 0x96, 0x99).lerp_to_gamma(Color32::WHITE, t);
    ui.painter().circle_filled(egui::pos2(x, rect.center().y), 6.5, knob);
    resp.on_hover_cursor(egui::CursorIcon::PointingHand)
}

/// A segmented control: returns true when the choice changed.
fn seg(ui: &mut egui::Ui, cur: &mut String, opts: &[(&str, &str)]) -> bool {
    let mut changed = false;
    ui.spacing_mut().item_spacing.x = 2.0;
    for (val, label) in opts {
        let on = cur == val;
        let b = egui::Button::new(RichText::new(*label).color(if on { Color32::WHITE } else { Color32::from_rgb(0xb0, 0x96, 0x99) }))
            .fill(if on { Color32::from_rgb(0x2a, 0x1b, 0x21) } else { FIELD })
            .stroke(Stroke::new(1.0, Color32::from_rgb(0x3a, 0x25, 0x2c)))
            .min_size(Vec2::new(60.0, 26.0));
        if ui.add(b).clicked() && !on {
            *cur = val.to_string();
            changed = true;
        }
    }
    changed
}

fn row(ui: &mut egui::Ui, label: &str, hint: &str, right: impl FnOnce(&mut egui::Ui)) {
    // Leave a few px at the right: the scroll area clips at its edge, which shaved the buttons' outline.
    let total = ui.available_width() - 6.0;
    ui.scope(|ui| {
        ui.set_max_width(total);
        ui.horizontal(|ui| {
        ui.allocate_ui_with_layout(Vec2::new((total - 260.0).max(180.0), 10.0), Layout::top_down(Align::LEFT), |ui| {
            ui.label(RichText::new(label).color(TEXT));
            if !hint.is_empty() {
                ui.label(RichText::new(hint).size(11.0).color(MUTED));
            }
        });
        ui.with_layout(Layout::right_to_left(Align::Center), right);
        });
    });
    ui.add_space(2.0);
    let r = ui.available_rect_before_wrap();
    ui.painter().hline(r.x_range(), ui.cursor().top(), Stroke::new(1.0, Color32::from_rgb(0x2a, 0x1b, 0x21)));
    ui.add_space(6.0);
}

fn heading(ui: &mut egui::Ui, t: &str) {
    ui.add_space(8.0);
    ui.label(RichText::new(t.to_uppercase()).size(10.5).color(MUTED));
    ui.add_space(2.0);
}

// ---------------------------------------------------------------- pages

impl App {
    fn connection(&mut self, ui: &mut egui::Ui) {
        let mut ch = false;
        let steam = self.network == "steam";
        row(ui, "Mode", "host: you start the session and your world is the session's. join: you connect to your partner.", |ui| {
            ch |= seg(ui, &mut self.mode, &[("join", "Join"), ("host", "Host")]);
        });
        row(ui, "Network", "Steam: through Steam, nothing to open on your router. LAN: by IP address.", |ui| {
            ch |= seg(ui, &mut self.network, &[("steam", "Steam"), ("lan", "LAN")]);
        });
        if self.network == "steam" {
            row(ui, "Steam account", "Dread Delusion: you own the game on Steam. Spacewar: your copy is from elsewhere (Valve's free test app 480). With a co-op password both of you must pick the same. Steam then shows you playing Spacewar; set Game details to Private in your Steam privacy settings to hide that.", |ui| {
                ch |= seg(ui, &mut self.identity, &[("spacewar", "Spacewar"), ("game", "Dread Delusion")]);
            });
        }
        row(ui, "Your name", "Shown to your partner over their screen.", |ui| {
            ch |= ui.add(egui::TextEdit::singleline(&mut self.name).desired_width(170.0).margin(Vec2::new(8.0, 5.0))).changed();
        });
        if steam {
            // two ways to find each other: one choice, and only that choice's settings below it
            heading(ui, "How do you find your partner?");
            ui.label(RichText::new("Pick one of the two.").size(11.0).color(MUTED));
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                let mut by_pw = if self.find_pw { "password" } else { "friend" }.to_string();
                if seg(ui, &mut by_pw, &[("password", "Co-op password"), ("friend", "Steam friend")]) {
                    self.find_pw = by_pw == "password";
                    ch = true;
                }
            });
            ui.add_space(6.0);
            let dim = Color32::from_rgb(0x6b, 0x52, 0x58);
            egui::Frame::none().fill(Color32::from_rgb(0x1c, 0x12, 0x16)).stroke(Stroke::new(1.0, LINE)).rounding(Rounding::same(6.0)).inner_margin(egui::Margin::same(14.0)).show(ui, |ui| {
                ui.set_width(ui.available_width());
                if self.find_pw {
                    ui.label(RichText::new("Type the same password on both machines and Steam pairs you up, even if you are not Steam friends.").size(12.0).color(TEXT));
                    ui.add_space(6.0);
                    ch |= ui.add(egui::TextEdit::singleline(&mut self.password).desired_width(260.0).hint_text(RichText::new("password").color(dim)).margin(Vec2::new(8.0, 5.0))).changed();
                    ui.add_space(4.0);
                    ui.label(RichText::new("Use a long, unique one. Steam only ever sees a hash of it.").size(11.0).color(MUTED));
                } else if self.mode == "host" {
                    ui.label(RichText::new("Nothing to set. Your Steam friends can join you once you start.").size(12.0).color(TEXT));
                } else {
                    ui.label(RichText::new("Join a Steam friend who is hosting. Leave the name empty to join the one friend playing Dread Delusion right now.").size(12.0).color(TEXT));
                    ui.add_space(6.0);
                    let (scanning, scanned, list, msg) = {
                        let f = self.friends.lock().unwrap();
                        (f.scanning, f.scanned, f.list.clone(), f.msg.clone())
                    };
                    let busy = self.run.lock().unwrap().running;
                    ui.horizontal(|ui| {
                        ch |= ui.add(egui::TextEdit::singleline(&mut self.partner).desired_width(260.0).hint_text(RichText::new("friend's Steam name or SteamID64").color(dim)).margin(Vec2::new(8.0, 5.0))).changed();
                        if scanning {
                            ui.add(egui::Spinner::new().size(14.0));
                            ui.ctx().request_repaint_after(Duration::from_millis(200));
                        } else if ui.add_enabled(!busy, egui::Button::new("Find friends")).on_hover_text("Asks Steam who is playing Dread Delusion right now (the Steam client must be running)").clicked() {
                            self.scan_friends();
                        }
                    });
                    if scanned && !scanning {
                        ui.add_space(4.0);
                        if list.is_empty() {
                            ui.label(RichText::new(msg.trim_start_matches("  ")).size(11.0).color(MUTED));
                        }
                        ui.horizontal_wrapped(|ui| {
                            for (name, id) in list.iter().rev() {
                                let on = self.partner == *name || self.partner == *id;
                                if ui.selectable_label(on, name).on_hover_text(format!("SteamID64 {id}")).clicked() {
                                    // a name can be shared by two friends; the id never is
                                    let same = list.iter().filter(|(n, _)| n == name).count() > 1;
                                    self.partner = if same { id.clone() } else { name.clone() };
                                    ch = true;
                                }
                            }
                        });
                    }
                }
            });
        } else if self.mode == "join" {
            row(ui, "Partner's address", "The host's IP address or host name.", |ui| {
                ch |= ui.add(egui::TextEdit::singleline(&mut self.server).desired_width(170.0).margin(Vec2::new(8.0, 5.0))).changed();
            });
        }
        // over Steam there is no port and no listen address: Steam carries the session
        if !steam {
            heading(ui, "Advanced");
            row(ui, "Port", "UDP port of the session. The host must let it through the firewall or router.", |ui| {
                ch |= ui.add(egui::TextEdit::singleline(&mut self.port).desired_width(80.0).margin(Vec2::new(8.0, 5.0))).changed();
            });
        }
        if self.mode == "host" && !steam {
            row(ui, "Listen address", "0.0.0.0 = every network card (a friend on another PC). 127.0.0.1 = this PC only.", |ui| {
                ch |= ui.add(egui::TextEdit::singleline(&mut self.bind).desired_width(170.0).margin(Vec2::new(8.0, 5.0))).changed();
            });
        }
        if ch {
            self.touch();
        }
    }

    fn rule_row(&mut self, ui: &mut egui::Ui, i: usize) {
        let r = &RULES[i];
        let changed_from_default = self.vals[i] != r.def;
        let mut ch = false;
        let label = if changed_from_default { format!("{}  \u{2022}", pretty(r.name)) } else { pretty(r.name) };
        let v = &mut self.vals[i];
        row(ui, &label, r.comment, |ui| {
            match r.kind {
                Kind::Bool => {
                    let mut b = *v != 0;
                    if toggle(ui, &mut b).changed() {
                        *v = b as i32;
                        ch = true;
                    }
                }
                Kind::Int if r.name == "VoiceMode" => {
                    let names = ["Hold the key to talk", "Always on"];
                    egui::ComboBox::from_id_salt(r.name).width(170.0).selected_text(names[(*v != 0) as usize]).show_ui(ui, |ui| {
                        for (k, n) in names.iter().enumerate() {
                            ch |= ui.selectable_value(v, k as i32, *n).changed();
                        }
                    });
                }
                Kind::Choice => {
                    let idx = (*v).clamp(0, r.choices.len().saturating_sub(1) as i32) as usize;
                    let cur = r.choices.get(idx).map(|c| c.label).unwrap_or("");
                    egui::ComboBox::from_id_salt(r.name).width(170.0).selected_text(cur).show_ui(ui, |ui| {
                        for (k, c) in r.choices.iter().enumerate() {
                            ch |= ui.selectable_value(v, k as i32, c.label).changed();
                        }
                    });
                }
                Kind::Int => {
                    ui.spacing_mut().slider_width = 150.0;
                    let unit = if r.name.ends_with("Pct") || r.name == "PercentPerStack" { " %" }
                        else if r.name.ends_with("Seconds") { " s" }
                        else if r.name.ends_with("M") { " m" }
                        else { "" };
                    if r.scale10 {
                        let unit = unit.to_string();
                        ch |= ui.add(egui::Slider::new(v, r.lo..=r.hi).custom_formatter(move |n, _| format!("{}{unit}", rules_table::format_float10(n as i32)))).changed();
                    } else {
                        ch |= ui.add(egui::Slider::new(v, r.lo..=r.hi).suffix(unit)).changed();
                    }
                }
                Kind::Key | Kind::Text => {}
            }
            if changed_from_default && ui.add(egui::Button::new(RichText::new("reset").size(11.0).color(MUTED)).frame(false)).on_hover_text(format!("default: {}", rules_table::value_text(r, r.def))).clicked() {
                *v = r.def;
                ch = true;
            }
        });
        if ch {
            self.touch();
        }
    }

    fn rules_page(&mut self, ui: &mut egui::Ui, local: bool) {
        if !local {
            ui.label(RichText::new("In a session the host's rules apply to both players. Your own [Rules] are used only when you play alone. Changes are written to the mod config and take effect the next time you start the game.").size(11.5).color(MUTED));
            ui.add_space(4.0);
            for g in GROUP_ORDER {
                let idx: Vec<usize> = (0..RULES.len()).filter(|&i| RULES[i].section == "Rules" && group_of(RULES[i].name) == *g).collect();
                if idx.is_empty() {
                    continue;
                }
                let changed = idx.iter().filter(|&&i| self.vals[i] != RULES[i].def).count();
                let title = if changed > 0 { format!("{g}   \u{2022} {changed} changed") } else { g.to_string() };
                egui::CollapsingHeader::new(RichText::new(title).size(13.5).color(TEXT))
                    .id_salt(*g)
                    .default_open(true)
                    .show(ui, |ui| {
                        for i in idx {
                            self.rule_row(ui, i);
                        }
                    });
                ui.add_space(2.0);
            }
            ui.add_space(6.0);
            if ui.button("Reset all rules to default").clicked() {
                for (i, r) in RULES.iter().enumerate() {
                    if r.section == "Rules" {
                        self.vals[i] = r.def;
                    }
                }
                self.touch();
            }
        } else {
            ui.label(RichText::new("These are yours alone: they are never sent to your partner.").size(11.5).color(MUTED));
            ui.add_space(8.0);
            ui.label(RichText::new("Partner look").size(13.5).color(TEXT));
            for i in (0..RULES.len()).filter(|&i| RULES[i].section == "Avatar") {
                self.rule_row(ui, i);
            }
            ui.add_space(8.0);
            ui.label(RichText::new("Chat and voice").size(13.5).color(TEXT));
            for i in (0..RULES.len()).filter(|&i| RULES[i].section == "Voice") {
                self.rule_row(ui, i);
            }
        }
    }

    fn game_page(&mut self, ui: &mut egui::Ui) {
        let mut ch = false;
        row(ui, "Dread Delusion folder", "Empty = found next to this folder or in your Steam library. Your install is only read, never changed.", |ui| {
            if ui.button("Browse...").clicked() {
                if let Some(p) = pick_folder("Choose the Dread Delusion folder (the one with windows_content)") {
                    self.game = p;
                    ch = true;
                }
            }
            ch |= ui.add(egui::TextEdit::singleline(&mut self.game).desired_width(230.0).hint_text("automatic").margin(Vec2::new(8.0, 5.0))).changed();
        });
        if self.monitor == 0 || self.monitor > self.screens.len() {
            self.monitor = 1;
        }
        let mon = self.screens[self.monitor - 1];
        let (sw, sh) = (mon.w, mon.h);
        let hint = format!("Fullscreen is the game's own setting. A size opens it in a window. {} {sw}x{sh}; only sizes that fit it are listed.", if self.screens.len() > 1 { "That screen is" } else { "Your screen is" });
        row(ui, "Window", &hint, |ui| {
            let cur = self.window.trim().to_ascii_lowercase();
            let mut fit: Vec<(u32, u32)> = WINDOW_SIZES.iter().copied().filter(|(w, h)| monitors::fits(&mon, *w, *h)).collect();
            fit.sort_by_key(|(w, h)| (w * h, *w));
            let mut sizes: Vec<String> = fit.iter().map(|(w, h)| format!("{w}x{h}")).collect();
            if cur != "fullscreen" && !sizes.contains(&cur) && !self.custom_win {
                sizes.push(cur.clone());
            }
            let shown = if self.custom_win { "Custom".to_string() } else if cur == "fullscreen" { "Fullscreen".to_string() } else { cur.clone() };
            egui::ComboBox::from_id_salt("winsize").selected_text(shown).width(150.0).show_ui(ui, |ui| {
                if ui.selectable_label(cur == "fullscreen" && !self.custom_win, "Fullscreen").clicked() {
                    self.window = "fullscreen".into();
                    self.custom_win = false;
                    ch = true;
                }
                for sz in &sizes {
                    if ui.selectable_label(&cur == sz && !self.custom_win, sz.as_str()).clicked() {
                        self.window = sz.clone();
                        self.custom_win = false;
                        ch = true;
                    }
                }
                if ui.selectable_label(self.custom_win, "Custom...").clicked() {
                    self.custom_win = true;
                    if cur == "fullscreen" {
                        self.window = format!("{}x{}", mon.ww * 5 / 6 / 2 * 2, mon.wh * 5 / 6 / 2 * 2);
                        ch = true;
                    }
                }
            });
            if self.custom_win {
                ch |= ui.add(egui::TextEdit::singleline(&mut self.window).desired_width(90.0).hint_text("1600x900").margin(Vec2::new(8.0, 5.0))).changed();
            }
        });
        if self.screens.len() > 1 {
            row(ui, "Screen", "Which screen a windowed game opens on (fullscreen is up to the game).", |ui| {
                let label = |i: usize, m: &monitors::Monitor| format!("{}  {}x{}{}", i + 1, m.w, m.h, if m.primary { "  main" } else { "" });
                egui::ComboBox::from_id_salt("screen").selected_text(label(self.monitor - 1, &self.screens[self.monitor - 1])).width(200.0).show_ui(ui, |ui| {
                    for (i, m) in self.screens.clone().iter().enumerate() {
                        if ui.selectable_label(self.monitor == i + 1, label(i, m)).clicked() {
                            self.monitor = i + 1;
                            ch = true;
                        }
                    }
                });
            });
        }
        row(ui, "First start", "Start the co-op save from a copy of your single-player save? Your single-player save itself is only read.", |ui| {
            ch |= seg(ui, &mut self.copy_save, &[("no", "No"), ("yes", "Yes"), ("ask", "Ask")]);
        });
        if ch {
            self.touch();
        }
    }

    fn log_page(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            if ui.button("Open co-op folder").clicked() {
                let _ = Command::new("explorer").arg(&self.root).spawn();
            }
            let running = self.run.lock().unwrap().running;
            if ui.add_enabled(!running, egui::Button::new("Pack logs for a bug report")).clicked() {
                self.start(vec!["--logs".into()]);
            }
        });
        ui.add_space(6.0);
        egui::Frame::none().fill(FIELD).stroke(Stroke::new(1.0, LINE)).rounding(Rounding::same(6.0)).inner_margin(egui::Margin::same(10.0)).show(ui, |ui| {
            egui::ScrollArea::vertical().stick_to_bottom(true).auto_shrink([false, false]).show(ui, |ui| {
                let r = self.run.lock().unwrap();
                if r.lines.is_empty() {
                    ui.label(RichText::new("Nothing yet. Press Launch.").color(MUTED));
                }
                for l in &r.lines {
                    ui.label(RichText::new(l).monospace().size(12.0).color(TEXT));
                }
                if let Some(c) = r.exit {
                    ui.label(RichText::new(format!("ended (exit code {c})")).monospace().size(12.0).color(MUTED));
                }
            });
        });
    }
}

// ---------------------------------------------------------------- doc 107: the Backups page

fn ago(unix: u64) -> String {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    if unix == 0 || unix > now + 60 {
        return String::new();
    }
    let s = now - unix;
    match s {
        0..=89 => "just now".into(),
        90..=5399 => format!("{} min ago", (s + 30) / 60),
        5400..=129_599 => format!("{} h ago", (s + 1800) / 3600),
        _ => format!("{} days ago", (s + 43_200) / 86_400),
    }
}

fn size_text(b: u64) -> String {
    if b >= 1 << 20 { format!("{:.1} MB", b as f64 / 1048576.0) } else { format!("{} KB", (b + 1023) / 1024) }
}

const WARN: Color32 = Color32::from_rgb(0xff, 0x73, 0x57);
const GOLD: Color32 = Color32::from_rgb(0xe0, 0xb4, 0x5a);

impl App {
    fn bk_paths(&self) -> backups::Paths {
        backups::paths(&self.root)
    }

    fn bk_refresh(&mut self, force: bool) {
        if !force && self.bk.read_at.map_or(false, |t| t.elapsed() < Duration::from_secs(3)) {
            return;
        }
        let p = self.bk_paths();
        self.bk.list = backups::list(&p);
        self.bk.game_running = backups::game_running(&p);
        self.bk.read_at = Some(Instant::now());
        if let Some(s) = &self.bk.sel {
            if !self.bk.list.iter().any(|b| &b.stamp == s) {
                self.bk.sel = None;
            }
        }
    }

    fn bk_say(&mut self, ok: Result<String, String>) {
        match ok {
            Ok(m) => { self.bk.msg = m; self.bk.msg_bad = false; }
            Err(e) => { self.bk.msg = e; self.bk.msg_bad = true; }
        }
    }

    /// Restoring needs the game closed AND this window's launcher stopped (it would start the game again).
    fn bk_blocked(&self) -> Option<&'static str> {
        if self.run.lock().unwrap().running {
            Some("The launcher is running. Press Stop and close the game first.")
        } else if self.bk.game_running {
            Some("A co-op game is running. Close it first: it would write its own save over the restored one.")
        } else {
            None
        }
    }

    fn backups_page(&mut self, ui: &mut egui::Ui) {
        self.bk_refresh(false);
        ui.ctx().request_repaint_after(Duration::from_secs(3));
        ui.label(RichText::new(
            "The mod backs up your co-op save when the game starts, when a session starts, after the game saves in a new level or after a boss fight or trial, \
             and after an ordinary save every few minutes. Each backup is checked: every file is read back and compared.",
        ).size(11.5).color(MUTED));
        ui.add_space(2.0);
        ui.label(RichText::new(
            "In a session the host picks the moment and both PCs back up together: the two backups share a PAIR CODE (like 153012-7F3A). \
             To go back to the same point after a desync, both of you restore the backup with the same pair code.",
        ).size(11.5).color(TEXT));
        ui.add_space(4.0);
        ui.label(RichText::new("DelusionMP does not create in-game backups. Use 'Back up now' before changing anything important.").size(11.5).color(MUTED));

        let p = self.bk_paths();
        let total: u64 = self.bk.list.iter().map(|b| b.bytes).sum();
        let blocked = self.bk_blocked();
        ui.add_space(4.0);
        ui.horizontal(|ui| {
            let can_make = blocked.is_none() && !backups::save_folders(&p).is_empty();
            if ui.add_enabled(can_make, egui::Button::new("Back up now")).on_hover_text("Copies your co-op save as it is now (the game must be closed; while it runs the mod backs up by itself)").on_disabled_hover_text("Close the game first, or there is no co-op save yet").clicked() {
                let r = backups::save_folders(&p).into_iter().next().ok_or_else(|| "no co-op save yet".to_string()).and_then(|sg| backups::make(&p, &sg, "manual", "made by hand in the launcher"));
                self.bk_say(r.map(|b| format!("Backup {} made and checked ({} file(s)).", b.made, b.entries.len())));
                self.bk_refresh(true);
            }
            if ui.button("Open folder").clicked() {
                let _ = std::fs::create_dir_all(&p.backups);
                let _ = Command::new("explorer").arg(&p.backups).spawn();
            }
            ui.add_space(8.0);
            ui.label(RichText::new("Find pair").size(12.0).color(MUTED));
            ui.add(egui::TextEdit::singleline(&mut self.bk.find).desired_width(110.0).hint_text(RichText::new("153012-7F3A").color(Color32::from_rgb(0x6b, 0x52, 0x58))).margin(Vec2::new(6.0, 4.0)))
                .on_hover_text("Type or paste the pair code your partner reads to you: the matching backup is shown in gold");
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.label(RichText::new(format!("{} backups, {}", self.bk.list.len(), size_text(total))).size(11.5).color(MUTED));
            });
        });
        if let Some(b) = blocked {
            ui.label(RichText::new(b).size(11.5).color(GOLD));
        }
        if !self.bk.msg.is_empty() {
            ui.label(RichText::new(&self.bk.msg).size(12.0).color(if self.bk.msg_bad { WARN } else { OK }));
        }
        ui.add_space(6.0);

        if self.bk.list.is_empty() {
            ui.label(RichText::new(format!("No backups yet. They are made in {} once you play.", p.backups.display())).color(MUTED));
            return;
        }
        let find = self.bk.find.trim().to_uppercase();
        let mut clicked: Option<String> = None;
        egui::Frame::none().fill(FIELD).stroke(Stroke::new(1.0, LINE)).rounding(Rounding::same(6.0)).inner_margin(egui::Margin::same(8.0)).show(ui, |ui| {
            ui.set_width(ui.available_width());
            egui::ScrollArea::vertical().id_salt("bklist").max_height(300.0).auto_shrink([false, true]).show(ui, |ui| {
                egui::Grid::new("bkgrid").num_columns(6).spacing(Vec2::new(14.0, 5.0)).striped(true).show(ui, |ui| {
                    for h in ["When", "Moment", "Level", "You were", "Pair code", "Check"] {
                        ui.label(RichText::new(h.to_uppercase()).size(10.0).color(MUTED));
                    }
                    ui.end_row();
                    for b in &self.bk.list {
                        let sel = self.bk.sel.as_deref() == Some(b.stamp.as_str());
                        let hit = !find.is_empty() && !b.pair.is_empty() && b.pair.to_uppercase().contains(&find);
                        let col = if hit { GOLD } else if b.kind == "before-restore" { MUTED } else { TEXT };
                        let when = if b.made.len() >= 16 { b.made[5..16].replace('-', "/") } else { b.made.clone() };
                        let r = ui.selectable_label(sel, RichText::new(format!("{}{}", if b.pinned { "\u{2022} " } else { "" }, when)).color(col).size(13.0));
                        let a = ago(b.made_unix);
                        let r = if a.is_empty() { r } else { r.on_hover_text(a) };
                        if r.clicked() {
                            clicked = Some(b.stamp.clone());
                        }
                        ui.label(RichText::new(b.moment()).color(col).size(13.0));
                        ui.label(RichText::new(if b.level.is_empty() { "-".into() } else { b.level_short() }).color(col).size(13.0));
                        ui.label(RichText::new(match b.role.as_str() { "host" => "host", "peer" => "guest", "solo" => "alone", "launcher" => "-", _ => "-" }).color(col).size(13.0));
                        ui.label(RichText::new(if b.pair.is_empty() { "-".into() } else { b.pair_short() }).monospace().color(col).size(12.5));
                        let (st, sc) = if b.legacy {
                            ("older", MUTED)
                        } else if !b.verified {
                            ("unfinished", WARN)
                        } else if !b.pair.is_empty() && b.confirmed {
                            ("both PCs", OK)
                        } else if !b.pair.is_empty() {
                            ("this PC", GOLD)
                        } else {
                            ("checked", OK)
                        };
                        ui.label(RichText::new(st).size(12.0).color(sc)).on_hover_text(match st {
                            "both PCs" => "Checked, and your partner's PC made its backup of the same moment",
                            "this PC" => "Checked here; your partner's half was not confirmed (they may have left, or run an older mod)",
                            "older" => "Made before backups were checked: no file list to compare",
                            "unfinished" => "This backup was not finished: do not restore it",
                            _ => "Every file was read back and compared when it was made",
                        });
                        ui.end_row();
                    }
                });
            });
        });
        if let Some(c) = clicked {
            self.bk.sel = if self.bk.sel.as_deref() == Some(c.as_str()) { None } else { Some(c) };
        }

        // the chosen backup
        let Some(b) = self.bk.sel.as_ref().and_then(|s| self.bk.list.iter().find(|b| &b.stamp == s)).cloned() else {
            ui.add_space(4.0);
            ui.label(RichText::new("Click a backup to see it, restore it or keep it.").size(11.5).color(MUTED));
            return;
        };
        ui.add_space(8.0);
        egui::Frame::none().fill(Color32::from_rgb(0x1c, 0x12, 0x16)).stroke(Stroke::new(1.0, LINE)).rounding(Rounding::same(6.0)).inner_margin(egui::Margin::same(12.0)).show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(RichText::new(format!("{}  \u{2014}  {}", b.made, b.moment())).size(15.0).color(Color32::WHITE));
            let mut facts: Vec<(&str, String)> = Vec::new();
            if !b.reason.is_empty() { facts.push(("Why", b.reason.clone())); }
            if !b.level.is_empty() { facts.push(("Level", b.level.clone())); }
            if b.cycle > 0 { facts.push(("New Game+", format!("cycle {}", b.cycle))); }
            if !b.role.is_empty() && b.role != "launcher" { facts.push(("You were", match b.role.as_str() { "peer" => "the guest".into(), "solo" => "playing alone".into(), r => format!("the {r}") })); }
            if !b.partner.is_empty() { facts.push(("Partner", b.partner.clone())); }
            if !b.pair.is_empty() {
                facts.push(("Pair code", format!("{}{}", b.pair_short(), if b.confirmed { "   (your partner's PC has its backup of this moment)" } else { "   (your partner's half was not confirmed)" })));
            }
            facts.push(("Files", format!("{} save file(s), {}{}", b.files, size_text(b.bytes), if b.slot.is_empty() { String::new() } else { format!(", slot {}", b.slot) })));
            facts.push(("Folder", b.dir.display().to_string()));
            egui::Grid::new("bkfacts").num_columns(2).spacing(Vec2::new(12.0, 3.0)).show(ui, |ui| {
                for (k, v) in facts {
                    ui.label(RichText::new(k).size(12.0).color(MUTED));
                    ui.label(RichText::new(v).size(12.0).color(TEXT));
                    ui.end_row();
                }
            });
            ui.add_space(8.0);
            ui.horizontal(|ui| {
                let blocked = self.bk_blocked();
                let ok = !b.legacy || backups::target_of(&self.bk_paths(), &b).is_ok();
                let rb = egui::Button::new(RichText::new("Restore this backup").color(Color32::WHITE)).fill(ACCENT);
                if ui.add_enabled(blocked.is_none() && ok && (b.verified || b.legacy), rb).on_disabled_hover_text(blocked.unwrap_or("This backup cannot be restored from here (see its check)")).clicked() {
                    self.bk.confirm = Some(b.stamp.clone());
                }
                if ui.button(if b.pinned { "Unpin" } else { "Pin (never delete)" }).on_hover_text("A pinned backup is never deleted by the mod's clean-up (save_backups_keep)").clicked() {
                    let r = backups::set_pinned(&b, !b.pinned).map(|_| if b.pinned { "Unpinned.".to_string() } else { "Pinned: the mod will keep this backup.".to_string() }).map_err(|e| e.to_string());
                    self.bk_say(r);
                    self.bk_refresh(true);
                }
                if !b.pair.is_empty() && ui.button("Copy pair code").on_hover_text("To send to your partner").clicked() {
                    ui.ctx().copy_text(b.pair_short());
                    self.bk_say(Ok(format!("Pair code {} copied.", b.pair_short())));
                }
                if ui.button("Open").clicked() {
                    let _ = Command::new("explorer").arg(&b.dir).spawn();
                }
                if ui.add(egui::Button::new(RichText::new("Delete").color(WARN))).clicked() {
                    self.bk.delete = Some(b.stamp.clone());
                }
            });
        });
    }

    /// The two questions the Backups page asks (restore, delete), as windows over everything.
    fn backup_dialogs(&mut self, ctx: &egui::Context) {
        if let Some(stamp) = self.bk.confirm.clone() {
            let Some(b) = self.bk.list.iter().find(|b| b.stamp == stamp).cloned() else { self.bk.confirm = None; return };
            let target = backups::target_of(&self.bk_paths(), &b);
            egui::Window::new("Restore a backup").collapsible(false).resizable(false).anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0]).show(ctx, |ui| {
                ui.set_max_width(460.0);
                ui.label(RichText::new(format!("Put the save back as it was on {} ({}{})?", b.made, b.moment(), if b.level.is_empty() { String::new() } else { format!(", {}", b.level_short()) })).color(TEXT));
                ui.add_space(4.0);
                ui.label(RichText::new("Your co-op save as it is now is backed up first (\"Before a restore\"), so you can undo this.").size(11.5).color(MUTED));
                if !b.pair.is_empty() {
                    ui.add_space(4.0);
                    ui.label(RichText::new(format!("Pair code {}: your partner must restore the backup with this same code on their PC, or your worlds will not match.", b.pair_short())).size(12.0).color(GOLD));
                }
                if let Err(e) = &target {
                    ui.label(RichText::new(e).size(12.0).color(WARN));
                }
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    if ui.add_enabled(target.is_ok() && self.bk_blocked().is_none(), egui::Button::new(RichText::new("Restore").color(Color32::WHITE)).fill(ACCENT)).clicked() {
                        let r = backups::restore(&self.bk_paths(), &b);
                        self.bk.confirm = None;
                        self.bk_say(r);
                        self.bk_refresh(true);
                    }
                    if ui.button("Cancel").clicked() {
                        self.bk.confirm = None;
                    }
                });
            });
        }
        if let Some(stamp) = self.bk.delete.clone() {
            let Some(b) = self.bk.list.iter().find(|b| b.stamp == stamp).cloned() else { self.bk.delete = None; return };
            egui::Window::new("Delete a backup").collapsible(false).resizable(false).anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0]).show(ctx, |ui| {
                ui.label(format!("Delete the backup of {} ({})? This cannot be undone.", b.made, b.moment()));
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    if ui.add(egui::Button::new(RichText::new("Delete").color(Color32::WHITE)).fill(Color32::from_rgb(0x8c, 0x2a, 0x38))).clicked() {
                        let r = backups::delete(&self.bk_paths(), &b).map(|_| "Deleted.".to_string());
                        self.bk.delete = None;
                        self.bk.sel = None;
                        self.bk_say(r);
                        self.bk_refresh(true);
                    }
                    if ui.button("Cancel").clicked() {
                        self.bk.delete = None;
                    }
                });
            });
        }
    }
}

const WINDOW_SIZES: [(u32, u32); 11] = [(1024, 576), (1152, 648), (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (1280, 800), (1680, 1050), (1920, 1200)];

#[link(name = "user32")]
extern "system" {
    fn GetActiveWindow() -> isize;
}
#[link(name = "shell32")]
extern "system" {
    fn SHBrowseForFolderW(bi: *mut BrowseInfo) -> *mut core::ffi::c_void;
    fn SHGetPathFromIDListW(pidl: *mut core::ffi::c_void, path: *mut u16) -> i32;
}
#[link(name = "ole32")]
extern "system" {
    fn CoTaskMemFree(p: *mut core::ffi::c_void);
}
#[repr(C)]
struct BrowseInfo {
    owner: isize,
    root: *mut core::ffi::c_void,
    display: *mut u16,
    title: *const u16,
    flags: u32,
    func: *mut core::ffi::c_void,
    lparam: isize,
    image: i32,
}

/// The standard Windows "choose a folder" dialog; None if cancelled.
fn pick_folder(title: &str) -> Option<String> {
    let t: Vec<u16> = title.encode_utf16().chain(Some(0)).collect();
    let mut name = [0u16; 260];
    let mut bi = BrowseInfo { owner: unsafe { GetActiveWindow() }, root: core::ptr::null_mut(), display: name.as_mut_ptr(), title: t.as_ptr(), flags: 0x1 | 0x40 | 0x10, func: core::ptr::null_mut(), lparam: 0, image: 0 };
    unsafe {
        let pidl = SHBrowseForFolderW(&mut bi);
        if pidl.is_null() {
            return None;
        }
        let mut path = [0u16; 520];
        let ok = SHGetPathFromIDListW(pidl, path.as_mut_ptr());
        CoTaskMemFree(pidl);
        if ok == 0 {
            return None;
        }
        let n = path.iter().position(|c| *c == 0).unwrap_or(path.len());
        Some(String::from_utf16_lossy(&path[..n]))
    }
}

/// Capitals with letter-spacing in a heading font.
fn tracked(text: &str, size: f32, col: Color32, spacing: f32, family: &str) -> egui::text::LayoutJob {
    let mut j = egui::text::LayoutJob::default();
    j.append(&text.to_uppercase(), 0.0, egui::TextFormat { font_id: egui::FontId::new(size, FontFamily::Name(family.into())), color: col, extra_letter_spacing: spacing, ..Default::default() });
    j
}

fn setup_style(ctx: &egui::Context) {
    // Dread Delusion's own look: Barlow for text, Barlow Semi Condensed (tracked capitals) for headings, a stencil for the wordmark.
    let mut fonts = FontDefinitions::default();
    if let Ok(b) = std::fs::read(r"C:\Windows\Fonts\segoeui.ttf") {
        fonts.font_data.insert("segoe".into(), FontData::from_owned(b));
        fonts.families.get_mut(&FontFamily::Proportional).unwrap().push("segoe".into());
    }
    for (key, bytes) in [
        ("barlow", &include_bytes!("../assets/fonts/Barlow-Medium.ttf")[..]),
        ("head", &include_bytes!("../assets/fonts/BarlowSemiCondensed-SemiBold.ttf")[..]),
        ("logo", &include_bytes!("../assets/fonts/SairaStencilOne-Regular.ttf")[..]),
    ] {
        fonts.font_data.insert(key.into(), FontData::from_static(bytes));
    }
    fonts.families.get_mut(&FontFamily::Proportional).unwrap().insert(0, "barlow".into());
    for key in ["head", "logo"] {
        fonts.families.insert(FontFamily::Name(key.into()), vec![key.into(), "barlow".into(), "segoe".into()]);
    }
    ctx.set_fonts(fonts);
    let mut v = egui::Visuals::dark();
    v.panel_fill = BG;
    v.window_fill = BG;
    v.extreme_bg_color = FIELD;
    v.faint_bg_color = PANEL;
    v.override_text_color = Some(TEXT);
    v.selection.bg_fill = ACCENT;
    v.selection.stroke = Stroke::new(1.0, Color32::WHITE);
    v.hyperlink_color = ACCENT;
    let edge = Color32::from_rgb(0x3a, 0x25, 0x2c);
    for w in [&mut v.widgets.inactive, &mut v.widgets.hovered, &mut v.widgets.active, &mut v.widgets.open] {
        w.rounding = Rounding::same(6.0);
        w.bg_stroke = Stroke::new(1.0, edge);
    }
    v.widgets.noninteractive.rounding = Rounding::same(6.0);
    v.widgets.inactive.weak_bg_fill = Color32::from_rgb(0x20, 0x15, 0x19);
    v.widgets.inactive.bg_fill = Color32::from_rgb(0x2a, 0x1b, 0x21);
    v.widgets.hovered.weak_bg_fill = Color32::from_rgb(0x2a, 0x1b, 0x21);
    v.widgets.hovered.bg_fill = Color32::from_rgb(0x35, 0x22, 0x29);
    v.widgets.active.weak_bg_fill = ACCENT;
    v.widgets.active.bg_fill = ACCENT;
    ctx.set_visuals(v);
    ctx.style_mut(|s| {
        s.spacing.item_spacing = Vec2::new(8.0, 6.0);
        s.spacing.button_padding = Vec2::new(12.0, 5.0);
        s.text_styles.insert(egui::TextStyle::Body, egui::FontId::new(14.5, FontFamily::Proportional));
        // a bar that has its own thin lane beside the content instead of floating over the buttons
        s.spacing.scroll = egui::style::ScrollStyle { bar_width: 6.0, bar_inner_margin: 8.0, bar_outer_margin: 0.0, floating: false, ..egui::style::ScrollStyle::solid() };
        s.text_styles.insert(egui::TextStyle::Button, egui::FontId::new(14.5, FontFamily::Proportional));
    });
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _f: &mut eframe::Frame) {
        if self.art.is_none() {
            self.art = load_art(ctx);
            // a wheel notch moves ~3 rows instead of egui's 40 px
            ctx.options_mut(|o| o.line_scroll_speed = 96.0);
        }
        // egui eases the wheel over ~100 ms; use a much shorter ease (~25 ms constant) so the content follows the wheel at once
        // but still glides to a stop. The pending distance is spent here and handed to the scroll areas as this frame's delta.
        {
            let (raw, dt) = ctx.input(|i| (i.raw_scroll_delta.y, i.stable_dt.min(0.05)));
            self.scroll_pending += raw;
            let step = if self.scroll_pending.abs() < 0.5 { self.scroll_pending } else { self.scroll_pending * (1.0 - (-dt / 0.025).exp()) };
            self.scroll_pending -= step;
            ctx.input_mut(|i| i.smooth_scroll_delta.y = step);
            if self.scroll_pending != 0.0 {
                ctx.request_repaint();
            }
        }
        if let Some(t) = self.dirty {
            if t.elapsed() > Duration::from_millis(500) && !ctx.input(|i| i.pointer.any_down()) {
                self.save();
            }
            ctx.request_repaint_after(Duration::from_millis(250));
        }
        let (running, last) = {
            let r = self.run.lock().unwrap();
            (r.running, r.lines.last().cloned().unwrap_or_default())
        };
        if running {
            ctx.request_repaint_after(Duration::from_millis(300));
        }

        egui::SidePanel::left("side").exact_width(168.0).resizable(false).frame(egui::Frame::none().fill(PANEL).inner_margin(egui::Margin::symmetric(10.0, 14.0))).show(ctx, |ui| {
            if let Some(a) = &self.art {
                let full = ui.max_rect().expand2(egui::vec2(10.0, 14.0));
                let h = (full.width() * 1.45).min(full.height() * 0.6);
                let r = egui::Rect::from_min_max(egui::pos2(full.min.x, full.max.y - h), full.max);
                let p = ui.painter();
                cover(p, &a.doll, r, false);
                let mut top = r;
                top.max.y = r.min.y + h * 0.4;
                fade(p, top, [PANEL, PANEL, Color32::TRANSPARENT, Color32::TRANSPARENT]);
            }
            ui.add_space(4.0);
            ui.label(tracked("Dread Delusion", 10.5, ACCENT, 2.6, "head"));
            let mut j = egui::text::LayoutJob::default();
            let f = |col: Color32| egui::TextFormat { font_id: egui::FontId::new(25.0, FontFamily::Name("logo".into())), color: col, ..Default::default() };
            j.append("Delusion", 0.0, f(Color32::WHITE));
            j.append("MP", 0.0, f(ACCENT));
            ui.label(j);
            ui.label(tracked("Co-op launcher", 10.5, MUTED, 1.8, "head"));
            ui.add_space(10.0);
            let y = ui.cursor().min.y;
            let x = ui.max_rect().x_range();
            ui.painter().hline(x, y, Stroke::new(1.0, Color32::from_rgba_unmultiplied(0xd8, 0x38, 0x5c, 70)));
            ui.add_space(12.0);
            for (p, l) in [(Page::Connection, "Connection"), (Page::Rules, "Rules"), (Page::Player, "Player"), (Page::Game, "Game & saves"), (Page::Backups, "Backups"), (Page::Log, "Log")] {
                let on = self.page == p;
                let b = egui::Button::new(tracked(l, 13.5, if on { Color32::WHITE } else { Color32::from_rgb(0xc0, 0xaa, 0xac) }, 1.4, "head"))
                    .fill(if on { Color32::from_rgb(0x2a, 0x1b, 0x21) } else { Color32::TRANSPARENT })
                    .stroke(Stroke::NONE)
                    .min_size(Vec2::new(ui.available_width(), 30.0));
                if ui.add(b).clicked() {
                    self.page = p;
                }
            }
        });

        egui::TopBottomPanel::bottom("foot").exact_height(52.0).frame(egui::Frame::none().fill(PANEL).stroke(Stroke::new(1.0, LINE)).inner_margin(egui::Margin::symmetric(16.0, 10.0))).show(ctx, |ui| {
            ui.horizontal_centered(|ui| {
                let conflict = self.key_conflict();
                let (txt, col) = if let Some(c) = &conflict {
                    (c.clone(), Color32::from_rgb(0xff, 0x73, 0x57))
                } else if running {
                    (if last.is_empty() { "Starting...".to_string() } else { last.chars().take(90).collect() }, OK)
                } else if !self.note.is_empty() {
                    (self.note.clone(), MUTED)
                } else {
                    ("Ready".to_string(), MUTED)
                };
                // A diamond frame motif: hollow when idle, filled and pulsing while running, red on a conflict.
                let (r, _) = ui.allocate_exact_size(Vec2::splat(14.0), egui::Sense::hover());
                let c = if running || conflict.is_some() { col } else { MUTED };
                let dia = |k: f32| {
                    let m = r.center();
                    vec![m + Vec2::new(0.0, -k), m + Vec2::new(k, 0.0), m + Vec2::new(0.0, k), m + Vec2::new(-k, 0.0)]
                };
                ui.painter().add(egui::Shape::closed_line(dia(6.0), Stroke::new(1.2, c)));
                if running || conflict.is_some() {
                    let pulse = if running { 0.55 + 0.45 * ((ui.input(|i| i.time) * 3.0).sin() as f32 * 0.5 + 0.5) } else { 1.0 };
                    ui.painter().add(egui::Shape::convex_polygon(dia(3.2), c.gamma_multiply(pulse), Stroke::NONE));
                }
                ui.add(egui::Label::new(RichText::new(txt).size(12.0).color(if conflict.is_some() { col } else { MUTED })).truncate());
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    let b = egui::Button::new(tracked(if running { "Stop" } else { "Launch" }, 15.0, Color32::WHITE, 2.0, "head")).fill(if running { Color32::from_rgb(0x8c, 0x2a, 0x38) } else { ACCENT }).min_size(Vec2::new(120.0, 32.0));
                    if running {
                        if ui.add(b).on_hover_text("Ends the launcher (and the connection). A game that is already open stays open.").clicked() {
                            let pid = self.run.lock().unwrap().pid;
                            if let Some(p) = pid {
                                let _ = Command::new("taskkill").args(["/PID", &p.to_string(), "/F"]).creation_flags(CREATE_NO_WINDOW).output();
                            }
                        }
                    } else if ui.add_enabled(conflict.is_none(), b).clicked() {
                        self.launch();
                    }
                });
            });
        });

        egui::CentralPanel::default().frame(egui::Frame::none().fill(BG).inner_margin(egui::Margin::symmetric(24.0, 16.0))).show(ctx, |ui| {
            let title = match self.page {
                Page::Connection => "Connection",
                Page::Rules => "Rules",
                Page::Player => "Player",
                Page::Game => "Game & saves",
                Page::Backups => "Backups",
                Page::Log => "Log",
            };
            let sub = match self.page {
                Page::Connection => "Who you play with, and how you reach them",
                Page::Rules => "What changes in co-op",
                Page::Player => "Your own preferences, only on this machine",
                Page::Game => "Game folder, saves and window",
                Page::Backups => "Your co-op save at the moments that matter, and the way back",
                Page::Log => "What the launcher is doing right now",
            };
            let bh = 128.0;
            let full = ui.max_rect().expand2(egui::vec2(24.0, 16.0));
            if let Some(a) = &self.art {
                let r = egui::Rect::from_min_size(full.min, egui::vec2(full.width(), bh));
                let p = ui.painter();
                cover(p, &a.banners[match self.page { Page::Backups => 3, Page::Log => 4, pg => pg as usize }], r, false);
                let none = Color32::TRANSPARENT;
                let dark = Color32::from_rgba_unmultiplied(0x15, 0x0e, 0x12, 170);
                fade(p, r, [dark, none, none, dark]);
                fade(p, r, [none, none, BG, BG]);
            }
            ui.add_space(bh - 16.0 - 54.0);
            ui.label(tracked(title, 28.0, Color32::WHITE, 2.4, "head"));
            ui.label(RichText::new(sub).size(12.0).color(Color32::from_rgb(0xc0, 0xaa, 0xac)));
            ui.add_space(16.0);
            if self.page == Page::Log {
                self.log_page(ui);
            } else {
                // The bar's lane is always reserved and each page keeps its own scroll state: the text width no longer
                // depends on whether last frame's content overflowed, so it is wrapped once, correctly, on the first frame.
                // The lane stays, but the bar is only drawn once the page actually overflows (known from the previous frame).
                let out = egui::ScrollArea::vertical().id_salt(self.page as usize).auto_shrink([false, false]).scroll_bar_visibility(egui::scroll_area::ScrollBarVisibility::AlwaysVisible).show(ui, |ui| match self.page {
                    Page::Connection => self.connection(ui),
                    Page::Rules => self.rules_page(ui, false),
                    Page::Player => self.rules_page(ui, true),
                    Page::Game => self.game_page(ui),
                    Page::Backups => self.backups_page(ui),
                    Page::Log => {}
                });
                if out.content_size.y <= out.inner_rect.height() + 0.5 {
                    let mut lane = out.inner_rect;
                    lane.min.x = lane.max.x;
                    lane.max.x = ui.max_rect().max.x + 24.0;
                    ui.painter().rect_filled(lane, 0.0, BG);
                }
            }
        });

        self.backup_dialogs(ctx);
        if self.ask_copy {
            egui::Window::new("First start").collapsible(false).resizable(false).anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0]).show(ctx, |ui| {
                ui.label("Start the co-op save from a copy of your single-player save?");
                ui.label(RichText::new("Your single-player save itself is only read, never changed.").size(11.5).color(MUTED));
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    if ui.button("Yes, copy it").clicked() {
                        self.ask_copy = false;
                        self.start(vec!["--yes".into()]);
                    }
                    if ui.button("No, new game").clicked() {
                        self.ask_copy = false;
                        self.start(vec!["--no".into()]);
                    }
                    if ui.button("Cancel").clicked() {
                        self.ask_copy = false;
                    }
                });
            });
        }
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        if self.dirty.is_some() {
            self.save();
        }
    }
}

fn main() -> eframe::Result<()> {
    let opts = eframe::NativeOptions {
        centered: true,
        viewport: {
            let mut v = egui::ViewportBuilder::default().with_inner_size([860.0, 600.0]).with_min_inner_size([720.0, 480.0]).with_title("DelusionMP Co-op");
            if let Ok(im) = image::load_from_memory_with_format(include_bytes!("../assets/icon.png"), image::ImageFormat::Png) {
                let im = im.to_rgba8();
                v = v.with_icon(egui::IconData { width: im.width(), height: im.height(), rgba: im.into_raw() });
            }
            v
        },
        ..Default::default()
    };
    eframe::run_native(
        "DelusionMP Co-op",
        opts,
        Box::new(|cc| {
            setup_style(&cc.egui_ctx);
            let mut app = App::new();
            // `--page backups` (a shortcut straight to the backups)
            let a: Vec<String> = std::env::args().collect();
            if let Some(i) = a.iter().position(|x| x == "--page") {
                if a.get(i + 1).map(|s| s.eq_ignore_ascii_case("backups")).unwrap_or(false) {
                    app.page = Page::Backups;
                }
            }
            Ok(Box::new(app))
        }),
    )
}
