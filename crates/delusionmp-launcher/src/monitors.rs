//! The screens of this PC (shared by the launcher and its window via #[path]): where a windowed game goes, and which sizes fit.

use core::ffi::c_void;

#[derive(Clone, Copy, Debug)]
pub struct Monitor {
    /// the whole screen, in virtual-desktop pixels
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
    /// the part not covered by the taskbar
    pub wx: i32,
    pub wy: i32,
    pub ww: u32,
    pub wh: u32,
    pub primary: bool,
}

#[repr(C)]
#[derive(Default, Clone, Copy)]
struct Rect {
    l: i32,
    t: i32,
    r: i32,
    b: i32,
}
#[repr(C)]
struct Info {
    size: u32,
    mon: Rect,
    work: Rect,
    flags: u32,
}

#[link(name = "user32")]
extern "system" {
    fn SetProcessDpiAwarenessContext(c: *mut c_void) -> i32;
    fn EnumDisplayMonitors(dc: *mut c_void, clip: *const Rect, cb: extern "system" fn(*mut c_void, *mut c_void, *mut Rect, isize) -> i32, data: isize) -> i32;
    fn GetMonitorInfoW(m: *mut c_void, info: *mut Info) -> i32;
}

extern "system" fn each(m: *mut c_void, _dc: *mut c_void, _r: *mut Rect, data: isize) -> i32 {
    let out = unsafe { &mut *(data as *mut Vec<Monitor>) };
    let mut i = Info { size: core::mem::size_of::<Info>() as u32, mon: Rect::default(), work: Rect::default(), flags: 0 };
    if unsafe { GetMonitorInfoW(m, &mut i) } != 0 {
        out.push(Monitor {
            x: i.mon.l,
            y: i.mon.t,
            w: (i.mon.r - i.mon.l).max(0) as u32,
            h: (i.mon.b - i.mon.t).max(0) as u32,
            wx: i.work.l,
            wy: i.work.t,
            ww: (i.work.r - i.work.l).max(0) as u32,
            wh: (i.work.b - i.work.t).max(0) as u32,
            primary: i.flags & 1 != 0,
        });
    }
    1
}

/// All screens, the main one first, then left to right. Never empty.
pub fn list() -> Vec<Monitor> {
    // true pixels instead of scaled ones (harmless if the process already chose)
    unsafe {
        SetProcessDpiAwarenessContext(-4isize as *mut c_void);
    }
    let mut v: Vec<Monitor> = Vec::new();
    unsafe {
        EnumDisplayMonitors(core::ptr::null_mut(), core::ptr::null(), each, &mut v as *mut Vec<Monitor> as isize);
    }
    if v.is_empty() {
        v.push(Monitor { x: 0, y: 0, w: 1920, h: 1080, wx: 0, wy: 0, ww: 1920, wh: 1040, primary: true });
    }
    v.sort_by_key(|m| (!m.primary, m.x, m.y));
    v
}

/// The window size (title bar and borders included) that still fits a screen's usable area.
pub fn fits(m: &Monitor, w: u32, h: u32) -> bool {
    w <= m.ww && h + 40 <= m.wh
}

/// Where the top-left of a w x h window goes to sit centred on the screen.
pub fn centred(m: &Monitor, w: u32, h: u32) -> (i32, i32) {
    (m.wx + (m.ww as i32 - w as i32).max(0) / 2, m.wy + (m.wh as i32 - h as i32 - 40).max(0) / 2)
}
