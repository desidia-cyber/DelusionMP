//! The few Windows calls the launcher needs, declared by hand (no extra crates).

#![allow(non_snake_case, clippy::upper_case_acronyms)]

use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::AsRawHandle;
use std::path::{Path, PathBuf};
use std::process::Child;

type HANDLE = *mut c_void;
type BOOL = i32;

#[repr(C)]
struct STARTUPINFOW {
    cb: u32,
    lpReserved: *mut u16,
    lpDesktop: *mut u16,
    lpTitle: *mut u16,
    dwX: u32,
    dwY: u32,
    dwXSize: u32,
    dwYSize: u32,
    dwXCountChars: u32,
    dwYCountChars: u32,
    dwFillAttribute: u32,
    dwFlags: u32,
    wShowWindow: u16,
    cbReserved2: u16,
    lpReserved2: *mut u8,
    hStdInput: HANDLE,
    hStdOutput: HANDLE,
    hStdError: HANDLE,
}

#[repr(C)]
struct PROCESS_INFORMATION {
    hProcess: HANDLE,
    hThread: HANDLE,
    dwProcessId: u32,
    dwThreadId: u32,
}

#[repr(C)]
#[derive(Default)]
struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
    PerProcessUserTimeLimit: i64,
    PerJobUserTimeLimit: i64,
    LimitFlags: u32,
    MinimumWorkingSetSize: usize,
    MaximumWorkingSetSize: usize,
    ActiveProcessLimit: u32,
    Affinity: usize,
    PriorityClass: u32,
    SchedulingClass: u32,
}

#[repr(C)]
#[derive(Default)]
struct IO_COUNTERS {
    a: [u64; 6],
}

#[repr(C)]
#[derive(Default)]
struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    BasicLimitInformation: JOBOBJECT_BASIC_LIMIT_INFORMATION,
    IoInfo: IO_COUNTERS,
    ProcessMemoryLimit: usize,
    JobMemoryLimit: usize,
    PeakProcessMemoryUsed: usize,
    PeakJobMemoryUsed: usize,
}

#[link(name = "kernel32")]
extern "system" {
    fn CreateProcessW(
        app: *const u16,
        cmd: *mut u16,
        pa: *mut c_void,
        ta: *mut c_void,
        inherit: BOOL,
        flags: u32,
        env: *mut c_void,
        dir: *const u16,
        si: *mut STARTUPINFOW,
        pi: *mut PROCESS_INFORMATION,
    ) -> BOOL;
    fn WaitForSingleObject(h: HANDLE, ms: u32) -> u32;
    fn GetExitCodeProcess(h: HANDLE, code: *mut u32) -> BOOL;
    fn CloseHandle(h: HANDLE) -> BOOL;
    fn CreateJobObjectW(a: *mut c_void, name: *const u16) -> HANDLE;
    fn SetInformationJobObject(job: HANDLE, class: i32, info: *mut c_void, len: u32) -> BOOL;
    fn AssignProcessToJobObject(job: HANDLE, proc_: HANDLE) -> BOOL;
}

#[link(name = "user32")]
extern "system" {
    fn GetSystemMetrics(i: i32) -> i32;
}

#[link(name = "advapi32")]
extern "system" {
    fn RegGetValueW(hkey: isize, sub: *const u16, value: *const u16, flags: u32, ty: *mut u32, data: *mut c_void, len: *mut u32) -> i32;
}

fn wide(s: &std::ffi::OsStr) -> Vec<u16> {
    s.encode_wide().chain(std::iter::once(0)).collect()
}

/// The helpers (relay, sidecar) end when this launcher ends, however it ends.
pub struct Job(HANDLE);

impl Job {
    pub fn new() -> Job {
        unsafe {
            let h = CreateJobObjectW(std::ptr::null_mut(), std::ptr::null());
            if !h.is_null() {
                let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(h, 9, &mut info as *mut _ as *mut c_void, std::mem::size_of_val(&info) as u32);
            }
            Job(h)
        }
    }
    pub fn add(&self, c: &Child) {
        if !self.0.is_null() {
            unsafe {
                AssignProcessToJobObject(self.0, c.as_raw_handle() as HANDLE);
            }
        }
    }
}

pub enum Show {
    Normal,
    NoActivate,
}

pub struct Proc {
    pub pid: u32,
    h: HANDLE,
}

impl Proc {
    /// Some(exit code) once the process has ended.
    pub fn wait(&self, ms: u32) -> Option<u32> {
        unsafe {
            if WaitForSingleObject(self.h, ms) != 0 {
                return None;
            }
            let mut c = 0u32;
            GetExitCodeProcess(self.h, &mut c);
            Some(c)
        }
    }
}

impl Drop for Proc {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.h);
        }
    }
}

/// The game is NOT in the job: closing the launcher must not kill a game that has not saved.
pub fn start(exe: &Path, args: &[String], dir: &Path, show: Show) -> Result<Proc, String> {
    let mut cmd: Vec<u16> = format!("\"{}\" {}", exe.display(), args.join(" ")).encode_utf16().chain(std::iter::once(0)).collect();
    let app = wide(exe.as_os_str());
    let wdir = wide(dir.as_os_str());
    unsafe {
        let mut si: STARTUPINFOW = std::mem::zeroed();
        si.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
        if let Show::NoActivate = show {
            si.dwFlags = 0x1; // STARTF_USESHOWWINDOW
            si.wShowWindow = 4; // SW_SHOWNOACTIVATE
        }
        let mut pi: PROCESS_INFORMATION = std::mem::zeroed();
        let ok = CreateProcessW(
            app.as_ptr(),
            cmd.as_mut_ptr(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            0,
            0,
            std::ptr::null_mut(),
            wdir.as_ptr(),
            &mut si,
            &mut pi,
        );
        if ok == 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        CloseHandle(pi.hThread);
        Ok(Proc { pid: pi.dwProcessId, h: pi.hProcess })
    }
}

/// Past the right edge of the whole virtual screen (doc 53).
pub fn offscreen_origin() -> (i32, i32) {
    unsafe { (GetSystemMetrics(76) + GetSystemMetrics(78) + 200, GetSystemMetrics(77)) }
}

/// Steam's library folders (the Steam folder itself first), from the registry and libraryfolders.vdf.
pub fn steam_libraries() -> Vec<PathBuf> {
    let mut out = Vec::new();
    let mut buf = vec![0u16; 1024];
    let mut len = (buf.len() * 2) as u32;
    let sub = wide(std::ffi::OsStr::new("Software\\Valve\\Steam"));
    let val = wide(std::ffi::OsStr::new("SteamPath"));
    // HKEY_CURRENT_USER, RRF_RT_REG_SZ
    let r = unsafe { RegGetValueW(0x8000_0001u32 as i32 as isize, sub.as_ptr(), val.as_ptr(), 0x2, std::ptr::null_mut(), buf.as_mut_ptr() as *mut c_void, &mut len) };
    if r != 0 {
        return out;
    }
    let n = (len as usize / 2).saturating_sub(1);
    let steam = PathBuf::from(String::from_utf16_lossy(&buf[..n]).replace('/', "\\"));
    out.push(steam.clone());
    if let Ok(vdf) = std::fs::read_to_string(steam.join("steamapps").join("libraryfolders.vdf")) {
        for line in vdf.lines() {
            let t = line.trim();
            if let Some(rest) = t.strip_prefix("\"path\"") {
                let p = rest.trim().trim_matches('"').replace("\\\\", "\\");
                if !p.is_empty() {
                    out.push(PathBuf::from(p));
                }
            }
        }
    }
    out
}
