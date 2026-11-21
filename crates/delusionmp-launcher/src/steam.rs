//! doc 73: the session over Steam (`network = steam`), beside the LAN path.
//!
//! Nothing in the relay, the sidecar or the mod changes: this is a tunnel for their UDP
//! datagrams. Each datagram travels as one unreliable Steam networking message
//! (ISteamNetworkingMessages, channel 0), so it gets Steam's NAT punching and relays, and
//! nobody has to open a port.
//!
//!   join:  sidecar --UDP--> 127.0.0.1:<local>  [tunnel] --Steam--> host's tunnel
//!   host:  host's tunnel --UDP--> relay on 127.0.0.1:<port> (one socket per partner)
//!
//! The Steam API is a `steam_api64.dll` started in this process as Dread Delusion (app 1574240) or,
//! for a copy not bought on Steam, as Spacewar (app 480). Which DLL: `steam_dll =`, the game's own,
//! or the pinned Steamworks.NET build beside the launcher (`bin\steam_api64.dll`, checked against
//! its SHA-256). Older DLLs lack the newest interface versions, so several are tried.
//!
//! Finding each other: with a co-op password both sides derive the same lobby key from it; the host
//! opens a public Steam lobby carrying the key, the joiner searches for it (Seamless Co-op's idea).
//! Without a password: Steam friends only (a friend playing the game, or one named).
//!
//! A Steam session sends no "disconnected": channel 1 carries 1 s keep-alives and a goodbye, and a
//! partner silent for 20 s is dropped (the host) or reported (the joiner).

#![allow(non_snake_case)]

use std::collections::HashMap;
use std::ffi::{c_char, c_void, CStr, CString};
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::net::{SocketAddr, UdpSocket};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{mpsc, OnceLock};
use std::time::{Duration, Instant};

pub const APP_ID: u32 = 1574240;
/// Valve's free test app. A player whose copy of the game is not from Steam (so Steam would refuse to
/// start the API as Dread Delusion) borrows its id to get a Steam identity and the Steam relay.
pub const SPACEWAR: u32 = 480;
const FRIEND_IMMEDIATE: i32 = 0x04;
const SEND_UNRELIABLE_NONAGLE_RESTART: i32 = 1 | 32;
const MSG_MAX: usize = 64;
/// The host sends this to friends in Dread Delusion: sending to a user opens the Steam session with
/// them even if the session-request callback is never dispatched. Never forwarded to a sidecar.
const HELLO: &[u8] = b"DMPSTEAM-HELLO";
const KEEPALIVE: &[u8] = b"KA";
const GOODBYE: &[u8] = b"BYE";
const CH_DATA: i32 = 0;
const CH_CTRL: i32 = 1;
const LOBBY_PUBLIC: i32 = 2;
const LOBBY_MAX: i32 = 8;
const ENTER_SUCCESS: i32 = 1;

/// The pinned Steamworks.NET-Standalone 2025.164.1 `steam_api64.dll` (Windows-x64), the one the launcher ships.
pub const PINNED_SHA256: &str = "eb17909a76668cf9ae0b92a618a34a50f6c73d3a6787cb4dd8ce36a8b10bfb75";

type Iface = *mut c_void;
type U64 = u64;

static IDENTITY: AtomicU32 = AtomicU32::new(APP_ID);
static STOP: AtomicBool = AtomicBool::new(false);
static DONE: AtomicBool = AtomicBool::new(false);
static STARTED: AtomicBool = AtomicBool::new(false);

/// Which Steam app this process starts the Steam API as: Dread Delusion (default) or Spacewar.
pub fn use_spacewar(on: bool) {
    IDENTITY.store(if on { SPACEWAR } else { APP_ID }, Ordering::Relaxed);
}

fn identity() -> u32 {
    IDENTITY.load(Ordering::Relaxed)
}

/// Ends the tunnel: the partner is told goodbye, Steam is shut down.
pub fn stop() {
    if !STARTED.load(Ordering::Relaxed) {
        return;
    }
    STOP.store(true, Ordering::Relaxed);
    let t = Instant::now();
    while !DONE.load(Ordering::Relaxed) && t.elapsed() < Duration::from_millis(800) {
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// How long a partner may be silent before it counts as gone (`DMP_STEAM_TIMEOUT_MS` is for tests).
fn silence_limit() -> Duration {
    Duration::from_millis(std::env::var("DMP_STEAM_TIMEOUT_MS").ok().and_then(|v| v.parse().ok()).unwrap_or(20_000))
}

#[repr(C)]
struct Identity {
    kind: i32,
    size: i32,
    data: [u8; 128],
}
const IDENTITY_STEAMID: i32 = 16;

impl Identity {
    fn steam_id(&self) -> Option<u64> {
        if self.kind == IDENTITY_STEAMID {
            Some(u64::from_le_bytes(self.data[..8].try_into().ok()?))
        } else {
            None
        }
    }
}

/// The part of SteamNetworkingMessage_t (SDK 1.51) read here.
#[repr(C)]
struct Message {
    data: *const u8,
    size: i32,
    conn: u32,
    peer: Identity,
}

#[repr(C)]
#[derive(Default)]
struct FriendGameInfo {
    game_id: u64,
    ip: u32,
    port: u16,
    query_port: u16,
    lobby: u64,
}

#[repr(C)]
struct CallbackMsg {
    user: i32,
    id: i32,
    param: *mut u8,
    size: i32,
}

#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn FreeLibrary(m: *mut c_void) -> i32;
    fn GetProcAddress(m: *mut c_void, name: *const c_char) -> *mut c_void;
}
#[link(name = "winmm")]
extern "system" {
    fn timeBeginPeriod(p: u32) -> u32;
}

struct Api {
    pipe: i32,
    shutdown: extern "C" fn(),
    run_frame: extern "C" fn(i32),
    next_cb: extern "C" fn(i32, *mut CallbackMsg) -> bool,
    free_cb: extern "C" fn(i32),
    call_result: extern "C" fn(i32, U64, *mut c_void, i32, i32, *mut bool) -> bool,
    user: Iface,
    friends: Iface,
    msgs: Iface,
    mm: Iface,
    get_steam_id: extern "C" fn(Iface) -> U64,
    persona: extern "C" fn(Iface) -> *const c_char,
    friend_count: extern "C" fn(Iface, i32) -> i32,
    friend_by_index: extern "C" fn(Iface, i32, i32) -> U64,
    friend_name: extern "C" fn(Iface, U64) -> *const c_char,
    friend_game: extern "C" fn(Iface, U64, *mut FriendGameInfo) -> bool,
    has_friend: extern "C" fn(Iface, U64, i32) -> bool,
    send: extern "C" fn(Iface, *const Identity, *const c_void, u32, i32, i32) -> i32,
    receive: extern "C" fn(Iface, i32, *mut *mut Message, i32) -> i32,
    accept: extern "C" fn(Iface, *const Identity) -> bool,
    release: extern "C" fn(*mut Message),
    create_lobby: extern "C" fn(Iface, i32, i32) -> U64,
    set_lobby_data: extern "C" fn(Iface, U64, *const c_char, *const c_char) -> bool,
    request_lobbies: extern "C" fn(Iface) -> U64,
    filter_string: extern "C" fn(Iface, *const c_char, *const c_char, i32),
    filter_distance: extern "C" fn(Iface, i32),
    filter_count: extern "C" fn(Iface, i32),
    lobby_by_index: extern "C" fn(Iface, i32) -> U64,
    lobby_owner: extern "C" fn(Iface, U64) -> U64,
    join_lobby: extern "C" fn(Iface, U64) -> U64,
    lobby_member_count: extern "C" fn(Iface, U64) -> i32,
    lobby_member: extern "C" fn(Iface, U64, i32) -> U64,
    leave_lobby: extern "C" fn(Iface, U64),
}
unsafe impl Send for Api {}
unsafe impl Sync for Api {}

static LOG: OnceLock<std::sync::Mutex<Option<File>>> = OnceLock::new();

fn log(s: &str) {
    if let Some(m) = LOG.get() {
        if let Ok(mut g) = m.lock() {
            if let Some(f) = g.as_mut() {
                let _ = writeln!(f, "{} {s}", crate::logs::stamp());
            }
        }
    }
}

fn ident(id: u64) -> Identity {
    let mut i = Identity { kind: IDENTITY_STEAMID, size: 8, data: [0; 128] };
    i.data[..8].copy_from_slice(&id.to_le_bytes());
    i
}

fn cstr(p: *const c_char) -> String {
    if p.is_null() {
        String::new()
    } else {
        unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned()
    }
}

// ---------------------------------------------------------------- which DLL

/// The install's steam_api64.dll (Engine\Binaries\ThirdParty\Steamworks\Steamv*\Win64).
pub fn find_dll(game: &Path) -> Option<PathBuf> {
    let base = game.join("Engine").join("Binaries").join("ThirdParty").join("Steamworks");
    let mut dirs: Vec<PathBuf> = fs::read_dir(&base).ok()?.flatten().map(|e| e.path()).collect();
    dirs.sort();
    dirs.into_iter().rev().map(|d| d.join("Win64").join("steam_api64.dll")).find(|p| p.is_file())
}

/// Installed by Steam on this machine: steamapps\common\<game> with Steam's manifest beside `common`.
pub fn steam_install(game: &Path) -> bool {
    game.parent().and_then(Path::parent).is_some_and(|apps| apps.join(format!("appmanifest_{APP_ID}.acf")).is_file())
}

/// A steam_api64.dll that is not Steam's (a crack's emulator) cannot reach Steam.
fn not_steams(dll: &Path) -> bool {
    let Ok(d) = fs::read(dll) else { return true };
    let low: Vec<u8> = d.iter().map(|b| b.to_ascii_lowercase()).collect();
    [b"tenoke".as_slice(), b"goldberg", b"codex", b"steam_emu"].iter().any(|n| low.windows(n.len()).any(|w| w == *n))
}

/// The DLLs to try, best first, and whether the game has no Steam files of its own (a copy not
/// bought on Steam: then Steam can only start as Spacewar).
///   1. `steam_dll =` (the player's own choice), 2. the game's own, 3. the pinned one beside the launcher.
/// As Spacewar the pinned one goes before the game's: a copy not from Steam may carry an emulator the
/// byte scan does not know, which "works" but talks only to itself (lobbies nobody else can find).
pub fn dll_candidates(game: &Path, own: &str, pkg: &Path, spacewar: bool) -> (Vec<PathBuf>, bool) {
    let mut v = Vec::new();
    if !own.trim().is_empty() {
        let p = PathBuf::from(own.trim());
        if p.is_file() {
            v.push(p);
        } else {
            crate::say(&format!("  Steam: steam_dll = {own} does not exist; ignored"));
        }
    }
    let game_dll = find_dll(game).filter(|d| !not_steams(d));
    let pinned_first = spacewar || game_dll.is_none();
    if let (false, Some(d)) = (pinned_first, &game_dll) {
        v.push(d.clone());
    }
    let pinned = pkg.join("bin").join("steam_api64.dll");
    if pinned.is_file() {
        match fs::read(&pinned) {
            Ok(b) if crate::sha::hex(&crate::sha::sha256(&b)) == PINNED_SHA256 => v.push(pinned),
            _ => crate::say(&format!("  Steam: {} is not the pinned build (SHA-256 differs); not used", pinned.display())),
        }
    }
    if let (true, Some(d)) = (pinned_first, &game_dll) {
        v.push(d.clone());
    }
    (v, game_dll.is_none())
}

// ---------------------------------------------------------------- start Steam

fn init_error(code: i32, msg: &str) -> String {
    let who = if identity() == SPACEWAR { "Spacewar".to_string() } else { "Dread Delusion".to_string() };
    let detail = if msg.is_empty() { String::new() } else { format!(" ({msg})") };
    match code {
        2 => "Steam is not running (or not logged in): start the Steam client, log in, then try again".to_string(),
        3 => "This steam_api64.dll does not match your Steam client (a version mismatch): update the Steam client, or use another DLL (steam_dll = ...)".to_string(),
        _ if identity() == SPACEWAR => format!("Steam would not start as {who}{detail}: is the Steam client running and logged in?"),
        _ => format!("Steam would not start as {who}{detail}: is the Steam client running and logged in, and is the game in your library? A copy not bought on Steam: set steam_identity = spacewar"),
    }
}

enum LoadErr {
    /// this DLL lacks something: the next one may do
    Dll(String),
    /// Steam itself would not start: no other DLL will fix that
    Steam(String),
}

fn load(dll: &Path) -> Result<Api, LoadErr> {
    let w: Vec<u16> = dll.as_os_str().encode_wide().chain(Some(0)).collect();
    let m = unsafe { LoadLibraryW(w.as_ptr()) };
    if m.is_null() {
        return Err(LoadErr::Dll(format!("could not load {}", dll.display())));
    }
    let r = load_from(m, dll);
    if matches!(r, Err(LoadErr::Dll(_))) {
        unsafe { FreeLibrary(m) };
    }
    r
}

fn load_from(m: *mut c_void, dll: &Path) -> Result<Api, LoadErr> {
    let find = |n: &str| -> *mut c_void {
        let c = CString::new(n).unwrap();
        unsafe { GetProcAddress(m, c.as_ptr()) }
    };
    let sym = |n: &str| -> Result<*mut c_void, LoadErr> {
        let p = find(n);
        if p.is_null() { Err(LoadErr::Dll(format!("{} has no {n} (too old?)", dll.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_default()))) } else { Ok(p) }
    };
    macro_rules! f {
        ($n:expr) => {
            unsafe { std::mem::transmute(sym($n)?) }
        };
    }
    // an interface is exported once per version the DLL knows: take the newest it has
    let iface = |names: &[&str]| -> Result<extern "C" fn() -> Iface, LoadErr> {
        for n in names {
            let p = find(n);
            if !p.is_null() {
                return Ok(unsafe { std::mem::transmute::<*mut c_void, extern "C" fn() -> Iface>(p) });
            }
        }
        Err(LoadErr::Dll(format!("{} has none of {}", dll.display(), names.join(", "))))
    };
    // symbols first: a DLL that lacks one is skipped before Steam is started
    let user_g = iface(&["SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020"])?;
    let friends_g = iface(&["SteamAPI_SteamFriends_v018", "SteamAPI_SteamFriends_v017", "SteamAPI_SteamFriends_v016"])?;
    let msgs_g = iface(&["SteamAPI_SteamNetworkingMessages_SteamAPI_v002"])?;
    let mm_g = iface(&["SteamAPI_SteamMatchmaking_v009"])?;
    let utils_g = iface(&["SteamAPI_SteamNetworkingUtils_SteamAPI_v004", "SteamAPI_SteamNetworkingUtils_SteamAPI_v003"]).ok();
    let get_pipe: extern "C" fn() -> i32 = f!("SteamAPI_GetHSteamPipe");
    let manual_init: extern "C" fn() = f!("SteamAPI_ManualDispatch_Init");
    let mut api = Api {
        pipe: 0,
        shutdown: f!("SteamAPI_Shutdown"),
        run_frame: f!("SteamAPI_ManualDispatch_RunFrame"),
        next_cb: f!("SteamAPI_ManualDispatch_GetNextCallback"),
        free_cb: f!("SteamAPI_ManualDispatch_FreeLastCallback"),
        call_result: f!("SteamAPI_ManualDispatch_GetAPICallResult"),
        user: std::ptr::null_mut(),
        friends: std::ptr::null_mut(),
        msgs: std::ptr::null_mut(),
        mm: std::ptr::null_mut(),
        get_steam_id: f!("SteamAPI_ISteamUser_GetSteamID"),
        persona: f!("SteamAPI_ISteamFriends_GetPersonaName"),
        friend_count: f!("SteamAPI_ISteamFriends_GetFriendCount"),
        friend_by_index: f!("SteamAPI_ISteamFriends_GetFriendByIndex"),
        friend_name: f!("SteamAPI_ISteamFriends_GetFriendPersonaName"),
        friend_game: f!("SteamAPI_ISteamFriends_GetFriendGamePlayed"),
        has_friend: f!("SteamAPI_ISteamFriends_HasFriend"),
        send: f!("SteamAPI_ISteamNetworkingMessages_SendMessageToUser"),
        receive: f!("SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel"),
        accept: f!("SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser"),
        release: f!("SteamAPI_SteamNetworkingMessage_t_Release"),
        create_lobby: f!("SteamAPI_ISteamMatchmaking_CreateLobby"),
        set_lobby_data: f!("SteamAPI_ISteamMatchmaking_SetLobbyData"),
        request_lobbies: f!("SteamAPI_ISteamMatchmaking_RequestLobbyList"),
        filter_string: f!("SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter"),
        filter_distance: f!("SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter"),
        filter_count: f!("SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter"),
        lobby_by_index: f!("SteamAPI_ISteamMatchmaking_GetLobbyByIndex"),
        lobby_owner: f!("SteamAPI_ISteamMatchmaking_GetLobbyOwner"),
        join_lobby: f!("SteamAPI_ISteamMatchmaking_JoinLobby"),
        lobby_member_count: f!("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers"),
        lobby_member: f!("SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex"),
        leave_lobby: f!("SteamAPI_ISteamMatchmaking_LeaveLobby"),
    };

    // start Steam: InitFlat says why it failed; older DLLs have only SteamAPI_Init
    std::env::set_var("SteamAppId", identity().to_string());
    std::env::set_var("SteamGameId", identity().to_string());
    let flat = find("SteamAPI_InitFlat");
    if !flat.is_null() {
        let init_flat: extern "C" fn(*mut c_char) -> i32 = unsafe { std::mem::transmute(flat) };
        let mut err = [0 as c_char; 1024];
        let r = init_flat(err.as_mut_ptr());
        if r != 0 {
            return Err(LoadErr::Steam(init_error(r, &cstr(err.as_ptr()))));
        }
    } else {
        let init: extern "C" fn() -> bool = f!("SteamAPI_Init");
        if !init() {
            return Err(LoadErr::Steam(init_error(1, "")));
        }
    }
    manual_init();
    api.pipe = get_pipe();
    api.user = user_g();
    api.friends = friends_g();
    api.msgs = msgs_g();
    api.mm = mm_g();
    if api.user.is_null() || api.friends.is_null() || api.msgs.is_null() || api.mm.is_null() {
        return Err(LoadErr::Steam("Steam started but its networking interfaces are missing".into()));
    }
    if let Some(u) = utils_g {
        // the relay network starts by itself on first use; asking early saves the first seconds
        let utils = u();
        let init_relay = find("SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
        if !utils.is_null() && !init_relay.is_null() {
            let relay_init: extern "C" fn(Iface) = unsafe { std::mem::transmute(init_relay) };
            relay_init(utils);
        }
        tune_send_queue(utils, &find);
    }
    log(&format!("Steam started from {} as app {}", dll.display(), identity()));
    crate::say(&format!("  Steam: using {}", dll.display()));
    Ok(api)
}

/// doc 93: Steam's send queue must not hold seconds of game traffic.
///
/// Everything (pose records, lanes, voice) shares one unreliable Steam session. Unreliable
/// messages that exceed the send rate are QUEUED, not dropped, and the default queue is 512 KB:
/// in the 4 Nov boss fight the host's effects lane overfilled it and every pose sample of the
/// host waited ~150-250 ms behind it, so the partner saw the doll freeze and jump. A small queue
/// makes Steam refuse a message instead (a stale pose or effect frame is worth nothing; the next
/// one follows within ~30 ms), and a higher rate floor stops Steam's own estimate from starting
/// low on a link that can carry far more.
const CFG_SEND_BUFFER_SIZE: i32 = 9; // k_ESteamNetworkingConfig_SendBufferSize (bytes)
const CFG_SEND_RATE_MIN: i32 = 10; // k_ESteamNetworkingConfig_SendRateMin (bytes/s)
const SEND_BUFFER_BYTES: i32 = 64 * 1024;
const SEND_RATE_MIN_BPS: i32 = 256 * 1024;

fn tune_send_queue(utils: Iface, find: &dyn Fn(&str) -> *mut c_void) {
    if utils.is_null() {
        return;
    }
    let p = find("SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32");
    if p.is_null() {
        log("send queue: this steam_api64.dll cannot set networking config; Steam's defaults stay");
        return;
    }
    let set: extern "C" fn(Iface, i32, i32) -> bool = unsafe { std::mem::transmute(p) };
    let buf = set(utils, CFG_SEND_BUFFER_SIZE, SEND_BUFFER_BYTES);
    let rate = set(utils, CFG_SEND_RATE_MIN, SEND_RATE_MIN_BPS);
    log(&format!(
        "send queue (doc 93): buffer {} B {}, rate floor {} B/s {}",
        SEND_BUFFER_BYTES,
        if buf { "set" } else { "REFUSED" },
        SEND_RATE_MIN_BPS,
        if rate { "set" } else { "REFUSED" }
    ));
}

/// The first DLL that starts. `Err` carries what to tell the player.
fn load_any(dlls: &[PathBuf]) -> Result<Api, String> {
    if dlls.is_empty() {
        return Err("network = steam: no steam_api64.dll to start Steam from: the game has none (a copy not bought on Steam) and the pinned one is missing (bin\\steam_api64.dll). Put a steam_api64.dll path in  steam_dll =  of DelusionMP-Coop.ini".into());
    }
    let mut why = Vec::new();
    for d in dlls {
        match load(d) {
            Ok(a) => return Ok(a),
            Err(LoadErr::Steam(e)) => return Err(e),
            Err(LoadErr::Dll(e)) => {
                log(&format!("skipped: {e}"));
                why.push(e);
            }
        }
    }
    Err(format!("no steam_api64.dll could be used: {}", why.join("; ")))
}

// ---------------------------------------------------------------- callbacks

#[derive(Default)]
struct Ev {
    created: Option<(i32, u64)>,
    matches: Option<u32>,
    entered: Option<(u64, i32)>,
    /// Steam users who sent a first message and wait to be accepted
    requests: Vec<u64>,
    failed: Vec<u64>,
}

fn rd<T: Copy>(p: &[u8], at: usize) -> T {
    unsafe { std::ptr::read_unaligned(p.as_ptr().add(at) as *const T) }
}

/// Steam's events, by hand (manual dispatch): lobby call results, session requests and failures.
fn pump(api: &Api, ev: &mut Ev) {
    (api.run_frame)(api.pipe);
    let mut m = CallbackMsg { user: 0, id: 0, param: std::ptr::null_mut(), size: 0 };
    while (api.next_cb)(api.pipe, &mut m) {
        let param: &[u8] = if m.param.is_null() { &[] } else { unsafe { std::slice::from_raw_parts(m.param, m.size.max(0) as usize) } };
        match m.id {
            703 if param.len() >= 16 => {
                // SteamAPICallCompleted_t { call: u64, callback: i32, size: u32 }
                let call: u64 = rd(param, 0);
                let cb: i32 = rd(param, 8);
                let mut buf = [0u8; 256];
                let mut failed = false;
                if (api.call_result)(api.pipe, call, buf.as_mut_ptr() as *mut c_void, buf.len() as i32, cb, &mut failed) && !failed {
                    match cb {
                        513 => ev.created = Some((rd::<i32>(&buf, 0), rd::<u64>(&buf, 8))),
                        510 => ev.matches = Some(rd::<u32>(&buf, 0)),
                        504 => ev.entered = Some((rd::<u64>(&buf, 0), rd::<i32>(&buf, 16))),
                        _ => {}
                    }
                } else if failed && matches!(cb, 504 | 510 | 513) {
                    log(&format!("call result {cb} failed"));
                    match cb {
                        513 => ev.created = Some((-1, 0)),
                        510 => ev.matches = Some(0),
                        504 => ev.entered = Some((0, -1)),
                        _ => {}
                    }
                }
            }
            1251 if param.len() >= 16 => {
                // SteamNetworkingMessagesSessionRequest_t { identity }
                if param.len() >= 8 && rd::<i32>(param, 0) == IDENTITY_STEAMID {
                    ev.requests.push(rd::<u64>(param, 8));
                }
            }
            1252 if param.len() >= 16 => {
                if rd::<i32>(param, 0) == IDENTITY_STEAMID {
                    ev.failed.push(rd::<u64>(param, 8));
                }
            }
            _ => {}
        }
        (api.free_cb)(api.pipe);
    }
}

/// Waits (pumping) for something to arrive in `ev`; false after `limit`.
fn wait_for(api: &Api, ev: &mut Ev, limit: Duration, done: impl Fn(&Ev) -> bool) -> bool {
    let t = Instant::now();
    while t.elapsed() < limit && !STOP.load(Ordering::Relaxed) {
        pump(api, ev);
        if done(ev) {
            return true;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    false
}

// ---------------------------------------------------------------- finding the partner

/// Both machines derive the same lobby key from the co-op password; the password itself is never sent.
pub fn lobby_key(password: &str) -> String {
    let mut h = crate::sha::sha256(format!("dollsmp-coop-v1|{}", password.trim()).as_bytes());
    for _ in 0..20_000 {
        h = crate::sha::sha256(&h);
    }
    crate::sha::hex(&h)[..32].to_string()
}

fn lobby_members(api: &Api, lobby: u64) -> Vec<u64> {
    (0..(api.lobby_member_count)(api.mm, lobby).max(0)).map(|i| (api.lobby_member)(api.mm, lobby, i)).collect()
}

/// Host: a public lobby that only carries the password's key.
fn open_lobby(api: &Api, ev: &mut Ev, key: &str) -> Result<u64, String> {
    ev.created = None;
    (api.create_lobby)(api.mm, LOBBY_PUBLIC, LOBBY_MAX);
    if !wait_for(api, ev, Duration::from_secs(30), |e| e.created.is_some()) {
        return Err("Steam did not create the session lobby".into());
    }
    let (res, lobby) = ev.created.unwrap_or((-1, 0));
    if res != 1 || lobby == 0 {
        return Err(format!("Steam could not create the session lobby (result {res})"));
    }
    for (k, v) in [("dmp", "1"), ("k", key)] {
        let (kc, vc) = (CString::new(k).unwrap(), CString::new(v).unwrap());
        if !(api.set_lobby_data)(api.mm, lobby, kc.as_ptr(), vc.as_ptr()) {
            return Err(format!("Steam refused the session lobby's data ({k})"));
        }
    }
    log(&format!("lobby {lobby} open, key {}", &key[..8]));
    crate::say(&format!("  Steam: session lobby open (code {}: your partner's launcher must show the same code)", &key[..8]));
    Ok(lobby)
}

/// Joiner: searches Steam for the lobby with this key, joins it (membership is what lets the host
/// accept us), and returns the host's SteamID.
fn find_by_password(api: &Api, ev: &mut Ev, key: &str, me: u64, once: bool) -> Result<(u64, u64), String> {
    let started = Instant::now();
    let mut said = false;
    let mut seen = None;
    let (kc, vc) = (CString::new("k").unwrap(), CString::new(key).unwrap());
    loop {
        if STOP.load(Ordering::Relaxed) {
            return Err("stopped".into());
        }
        ev.matches = None;
        (api.filter_string)(api.mm, kc.as_ptr(), vc.as_ptr(), 0);
        (api.filter_distance)(api.mm, 3);
        (api.filter_count)(api.mm, 20);
        (api.request_lobbies)(api.mm);
        let answered = wait_for(api, ev, Duration::from_secs(20), |e| e.matches.is_some());
        let found = if answered { ev.matches } else { None };
        if found != seen {
            log(&format!("lobby search, key {}: {found:?} found (None = Steam did not answer)", &key[..8]));
            seen = found;
        }
        if answered {
            for i in 0..ev.matches.unwrap_or(0) as i32 {
                let lobby = (api.lobby_by_index)(api.mm, i);
                if lobby == 0 {
                    continue;
                }
                // Steam tells a lobby's owner only to its members: join first, then ask
                ev.entered = None;
                (api.join_lobby)(api.mm, lobby);
                if !wait_for(api, ev, Duration::from_secs(20), |e| e.entered.is_some()) || ev.entered.map(|e| e.1) != Some(ENTER_SUCCESS) {
                    log(&format!("lobby {lobby}: could not join ({:?})", ev.entered));
                    continue;
                }
                let owner = (api.lobby_owner)(api.mm, lobby);
                if owner != 0 && owner != me {
                    log(&format!("lobby {lobby}: joined, host {owner}"));
                    return Ok((owner, lobby));
                }
                log(&format!("lobby {lobby}: owner {owner}, not a host to join"));
                (api.leave_lobby)(api.mm, lobby);
            }
        }
        if once {
            return Err("not found".into());
        }
        if !said {
            crate::say(&format!("  Steam: waiting for the host (code {}: the host's launcher must show the same code)...", &key[..8]));
            said = true;
        }
        if started.elapsed() > Duration::from_secs(240) {
            return Err("no host with this co-op password was found (the host starts first, and both need the same password)".into());
        }
        std::thread::sleep(Duration::from_secs(2));
    }
}

fn friends_in_game(api: &Api) -> Vec<(u64, String)> {
    let mut out = Vec::new();
    let n = (api.friend_count)(api.friends, FRIEND_IMMEDIATE);
    for i in 0..n.max(0) {
        let id = (api.friend_by_index)(api.friends, i, FRIEND_IMMEDIATE);
        let mut g = FriendGameInfo::default();
        // Dread Delusion, or Spacewar (a friend whose copy is not from Steam)
        if (api.friend_game)(api.friends, id, &mut g) && matches!((g.game_id & 0xFF_FFFF) as u32, APP_ID | SPACEWAR) {
            out.push((id, cstr((api.friend_name)(api.friends, id))));
        }
    }
    out
}

/// Who to join without a password: a SteamID64, a friend's Steam name, or (empty / "steam") the one
/// friend playing Dread Delusion right now.
fn resolve_host(api: &Api, want: &str) -> Result<Option<u64>, String> {
    let want = want.trim();
    if want.len() >= 15 && want.chars().all(|c| c.is_ascii_digit()) {
        return want.parse().map(Some).map_err(|_| "bad SteamID64".into());
    }
    let by_name = !(want.is_empty() || want.eq_ignore_ascii_case("steam") || want == "127.0.0.1");
    if by_name {
        let n = (api.friend_count)(api.friends, FRIEND_IMMEDIATE);
        for i in 0..n.max(0) {
            let id = (api.friend_by_index)(api.friends, i, FRIEND_IMMEDIATE);
            if cstr((api.friend_name)(api.friends, id)).eq_ignore_ascii_case(want) {
                return Ok(Some(id));
            }
        }
        return Err(format!("no Steam friend is called \"{want}\""));
    }
    let playing = friends_in_game(api);
    match playing.len() {
        0 => Ok(None),
        1 => Ok(Some(playing[0].0)),
        _ => Err(format!(
            "several friends are in Dread Delusion ({}): put your partner's Steam name in Partner (or use a co-op password)",
            playing.iter().map(|p| p.1.as_str()).collect::<Vec<_>>().join(", ")
        )),
    }
}

// ---------------------------------------------------------------- the tunnel

/// Start the tunnel. Host: forwards partners to the relay at `relay`; returns None.
/// Join: returns the local UDP address the sidecar must use as its server.
/// `password` (non-empty): partners find each other through a Steam lobby; otherwise Steam friends only.
pub fn start(dlls: Vec<PathBuf>, logs: &Path, host: bool, relay: SocketAddr, want: &str, password: &str) -> Result<Option<SocketAddr>, String> {
    let _ = LOG.set(std::sync::Mutex::new(OpenOptions::new().create(true).append(true).open(logs.join("steam.log")).ok()));
    STOP.store(false, Ordering::Relaxed);
    DONE.store(false, Ordering::Relaxed);
    STARTED.store(true, Ordering::Relaxed);
    let (tx, rx) = mpsc::channel::<Result<Option<SocketAddr>, String>>();
    let want = want.to_string();
    let key = if password.trim().is_empty() { None } else { Some(lobby_key(password)) };
    std::thread::spawn(move || {
        unsafe { timeBeginPeriod(1) };
        let api = match load_any(&dlls) {
            Ok(a) => a,
            Err(e) => {
                let _ = tx.send(Err(e));
                DONE.store(true, Ordering::Relaxed);
                return;
            }
        };
        let me = (api.get_steam_id)(api.user);
        let me_name = cstr((api.persona)(api.friends));
        log(&format!("Steam up: {me_name} ({me}), host={host}, password={}", key.is_some()));
        crate::say(&format!("  Steam: you are \"{me_name}\" (SteamID {me})"));
        let mut ev = Ev::default();
        if host {
            match key.as_deref().map(|k| open_lobby(&api, &mut ev, k)).transpose() {
                Ok(lobby) => {
                    let _ = tx.send(Ok(None));
                    host_loop(&api, &mut ev, relay, lobby);
                }
                Err(e) => {
                    let _ = tx.send(Err(e));
                }
            }
        } else {
            join_loop(&api, &mut ev, &want, key.as_deref(), me, tx);
        }
        (api.shutdown)();
        DONE.store(true, Ordering::Relaxed);
    });
    rx.recv_timeout(Duration::from_secs(300)).map_err(|_| "Steam did not answer".to_string())?
}

struct Peer {
    sock: UdpSocket,
    who: Identity,
    last: Instant,
}

fn send(api: &Api, to: &Identity, data: &[u8], ch: i32) {
    (api.send)(api.msgs, to, data.as_ptr() as *const c_void, data.len() as u32, SEND_UNRELIABLE_NONAGLE_RESTART, ch);
}

fn host_loop(api: &Api, ev: &mut Ev, relay: SocketAddr, lobby: Option<u64>) {
    let mut peers: HashMap<u64, Peer> = HashMap::new();
    let mut buf = [0u8; 65536];
    let mut msgs: [*mut Message; MSG_MAX] = [std::ptr::null_mut(); MSG_MAX];
    let (mut up, mut down) = (0u64, 0u64);
    let mut report = Instant::now() + Duration::from_secs(30);
    let mut hello = Instant::now();
    let mut beat = Instant::now();
    let limit = silence_limit();
    // with a password: whoever is in the lobby; without: Steam friends
    let allowed = |id: u64| match lobby {
        Some(l) => lobby_members(api, l).contains(&id),
        None => (api.has_friend)(api.friends, id, FRIEND_IMMEDIATE),
    };
    let name = |id: u64| cstr((api.friend_name)(api.friends, id));
    // a first message can beat the joiner's lobby entry: ask again for a minute before giving up
    let mut waiting: HashMap<u64, Instant> = HashMap::new();
    while !STOP.load(Ordering::Relaxed) {
        pump(api, ev);
        for id in ev.requests.drain(..).collect::<Vec<_>>() {
            waiting.entry(id).or_insert_with(Instant::now);
        }
        waiting.retain(|&id, since| {
            if allowed(id) {
                let ok = (api.accept)(api.msgs, &ident(id));
                log(&format!("session request from {} ({id}): accepted={ok}", name(id)));
                false
            } else if since.elapsed() > Duration::from_secs(60) {
                log(&format!("session request from {id}: refused ({})", if lobby.is_some() { "not in the lobby" } else { "not a Steam friend" }));
                false
            } else {
                true
            }
        });
        for id in ev.failed.drain(..).collect::<Vec<_>>() {
            if peers.remove(&id).is_some() {
                log(&format!("partner {id}: Steam session failed"));
                crate::say(&format!("  Steam: lost the connection to {}", name(id)));
            }
        }
        let now = Instant::now();
        if now >= beat {
            beat = now + Duration::from_secs(1);
            for p in peers.values() {
                send(api, &p.who, KEEPALIVE, CH_CTRL);
            }
            let gone: Vec<u64> = peers.iter().filter(|(_, p)| now.duration_since(p.last) > limit).map(|(id, _)| *id).collect();
            for id in gone {
                peers.remove(&id);
                log(&format!("partner {id}: silent for {} s, dropped", limit.as_secs()));
                crate::say(&format!("  Steam: {} stopped answering; waiting for them to come back", name(id)));
            }
        }
        if lobby.is_none() && now >= hello {
            hello = now + Duration::from_secs(3);
            for (id, _) in friends_in_game(api) {
                if !peers.contains_key(&id) {
                    send(api, &ident(id), HELLO, CH_DATA);
                }
            }
        }
        let mut got = 0;
        for ch in [CH_DATA, CH_CTRL] {
            let n = (api.receive)(api.msgs, ch, msgs.as_mut_ptr(), MSG_MAX as i32);
            got += n.max(0);
            for m in msgs.iter().take(n.max(0) as usize) {
                let msg = unsafe { &**m };
                let data = unsafe { std::slice::from_raw_parts(msg.data, msg.size.max(0) as usize) };
                if let Some(id) = msg.peer.steam_id() {
                    if ch == CH_CTRL && data == GOODBYE {
                        if peers.remove(&id).is_some() {
                            log(&format!("partner {id} left"));
                            crate::say(&format!("  Steam: {} left the session", name(id)));
                        }
                    } else {
                        if !peers.contains_key(&id) && allowed(id) {
                            if let Ok(s) = UdpSocket::bind("127.0.0.1:0") {
                                let _ = s.set_nonblocking(true);
                                if s.connect(relay).is_ok() {
                                    log(&format!("partner {} ({id}) connected", name(id)));
                                    crate::say(&format!("  Steam: {} connected", name(id)));
                                    peers.insert(id, Peer { sock: s, who: ident(id), last: Instant::now() });
                                }
                            }
                        }
                        if let Some(p) = peers.get_mut(&id) {
                            p.last = Instant::now();
                            if ch == CH_DATA {
                                let _ = p.sock.send(data);
                                up += 1;
                            }
                        }
                    }
                }
                (api.release)(*m);
            }
        }
        for p in peers.values() {
            while let Ok(k) = p.sock.recv(&mut buf) {
                send(api, &p.who, &buf[..k], CH_DATA);
                down += 1;
            }
        }
        if Instant::now() >= report {
            report = Instant::now() + Duration::from_secs(30);
            log(&format!("partners {} | from Steam {up} | to Steam {down}", peers.len()));
        }
        if got <= 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
    }
    for p in peers.values() {
        send(api, &p.who, GOODBYE, CH_CTRL);
    }
    if let Some(l) = lobby {
        (api.leave_lobby)(api.mm, l);
    }
    std::thread::sleep(Duration::from_millis(100));
}

fn join_loop(api: &Api, ev: &mut Ev, want: &str, key: Option<&str>, me: u64, tx: mpsc::Sender<Result<Option<SocketAddr>, String>>) {
    // 1. who is the host
    let (host, lobby) = match key {
        Some(k) => match find_by_password(api, ev, k, me, false) {
            Ok((h, l)) => (h, Some(l)),
            Err(e) => {
                let _ = tx.send(Err(e));
                return;
            }
        },
        None => {
            let started = Instant::now();
            let mut said = false;
            let h = loop {
                pump(api, ev);
                match resolve_host(api, want) {
                    Ok(Some(id)) => break id,
                    Ok(None) if started.elapsed() < Duration::from_secs(240) && !STOP.load(Ordering::Relaxed) => {
                        if !said {
                            crate::say("  Steam: waiting for a friend to host Dread Delusion co-op...");
                            said = true;
                        }
                        std::thread::sleep(Duration::from_secs(2));
                    }
                    Ok(None) => {
                        let _ = tx.send(Err("no Steam friend is playing Dread Delusion (the host starts first)".into()));
                        return;
                    }
                    Err(e) => {
                        let _ = tx.send(Err(e));
                        return;
                    }
                }
            };
            (h, None)
        }
    };
    let host_name = cstr((api.friend_name)(api.friends, host));
    let host_name = if host_name.is_empty() || host_name == "[unknown]" { format!("{host}") } else { host_name };
    let sock = match UdpSocket::bind("127.0.0.1:0") {
        Ok(s) => s,
        Err(e) => {
            let _ = tx.send(Err(format!("local socket: {e}")));
            return;
        }
    };
    let _ = sock.set_nonblocking(true);
    let local = sock.local_addr().ok();
    log(&format!("joining {host_name} ({host}) via {local:?}"));
    crate::say(&format!("  Steam: joining {host_name}"));
    let _ = tx.send(Ok(local));
    // 2. the tunnel
    let mut host = host;
    let mut lobby = lobby;
    let mut who = ident(host);
    let mut next_search = Instant::now();
    let mut side: Option<SocketAddr> = None;
    let mut buf = [0u8; 65536];
    let mut msgs: [*mut Message; MSG_MAX] = [std::ptr::null_mut(); MSG_MAX];
    let (mut up, mut down) = (0u64, 0u64);
    let mut report = Instant::now() + Duration::from_secs(30);
    let mut beat = Instant::now();
    let mut last_host = Instant::now();
    let mut lost = false;
    let limit = silence_limit();
    while !STOP.load(Ordering::Relaxed) {
        pump(api, ev);
        for id in ev.requests.drain(..).collect::<Vec<_>>() {
            if id == host {
                (api.accept)(api.msgs, &ident(id));
            }
        }
        if ev.failed.drain(..).any(|id| id == host) {
            log("the Steam session to the host failed");
        }
        let now = Instant::now();
        if now >= beat {
            beat = now + Duration::from_secs(1);
            send(api, &who, KEEPALIVE, CH_CTRL);
            // a restarted host opens a new lobby: look for it again (same password), and follow it
            if lost && now >= next_search {
                next_search = now + Duration::from_secs(5);
                if let Some(k) = key {
                    if let Ok((h, l)) = find_by_password(api, ev, k, me, true) {
                        if lobby != Some(l) {
                            if let Some(old) = lobby {
                                (api.leave_lobby)(api.mm, old);
                            }
                            log(&format!("found the host again: {h}, lobby {l}"));
                            host = h;
                            lobby = Some(l);
                            who = ident(h);
                            send(api, &who, KEEPALIVE, CH_CTRL);
                        }
                    }
                }
            }
            if !lost && now.duration_since(last_host) > limit {
                lost = true;
                log(&format!("the host has been silent for {} s", limit.as_secs()));
                crate::say(&format!("  Steam: {host_name} stopped answering (the game keeps trying; this resumes when they are back)"));
            }
        }
        while let Ok((k, from)) = sock.recv_from(&mut buf) {
            side = Some(from);
            send(api, &who, &buf[..k], CH_DATA);
            up += 1;
        }
        let mut got = 0;
        for ch in [CH_DATA, CH_CTRL] {
            let n = (api.receive)(api.msgs, ch, msgs.as_mut_ptr(), MSG_MAX as i32);
            got += n.max(0);
            for m in msgs.iter().take(n.max(0) as usize) {
                let msg = unsafe { &**m };
                let data = unsafe { std::slice::from_raw_parts(msg.data, msg.size.max(0) as usize) };
                if msg.peer.steam_id() == Some(host) {
                    if ch == CH_CTRL && data == GOODBYE {
                        lost = true;
                        last_host = Instant::now() - limit - Duration::from_secs(1);
                        log("the host said goodbye");
                        crate::say(&format!("  Steam: {host_name} ended the session"));
                    } else {
                        last_host = Instant::now();
                        if lost {
                            lost = false;
                            log("the host is back");
                            crate::say(&format!("  Steam: {host_name} is back"));
                        }
                        if ch == CH_DATA && data != HELLO {
                            if let Some(to) = side {
                                let _ = sock.send_to(data, to);
                                down += 1;
                            }
                        }
                    }
                }
                (api.release)(*m);
            }
        }
        if Instant::now() >= report {
            report = Instant::now() + Duration::from_secs(30);
            log(&format!("to Steam {up} | from Steam {down}"));
        }
        if got <= 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
    }
    send(api, &who, GOODBYE, CH_CTRL);
    if let Some(l) = lobby {
        (api.leave_lobby)(api.mm, l);
    }
    std::thread::sleep(Duration::from_millis(100));
}

/// `--steam-check`: Steam starts as the game, says who you are and which friends are in the game.
pub fn check(dlls: Vec<PathBuf>, logs: &Path) -> Result<(), String> {
    let _ = LOG.set(std::sync::Mutex::new(OpenOptions::new().create(true).append(true).open(logs.join("steam.log")).ok()));
    let api = load_any(&dlls)?;
    let mut ev = Ev::default();
    pump(&api, &mut ev);
    crate::say(&format!("  Steam: you are \"{}\" (SteamID {})", cstr((api.persona)(api.friends)), (api.get_steam_id)(api.user)));
    // one line per friend, read by the launcher window: `  friend in Dread Delusion now: NAME (ID)`
    // what a friend is playing reaches a freshly started Steam API a moment later: give it a few seconds
    let t = Instant::now();
    let mut playing = friends_in_game(&api);
    while playing.is_empty() && t.elapsed() < Duration::from_secs(5) {
        std::thread::sleep(Duration::from_millis(100));
        pump(&api, &mut ev);
        playing = friends_in_game(&api);
    }
    for (id, name) in &playing {
        crate::say(&format!("  friend in Dread Delusion now: {name} ({id})"));
    }
    if playing.is_empty() {
        crate::say("  no Steam friend is in Dread Delusion right now");
    }
    (api.shutdown)();
    Ok(())
}
