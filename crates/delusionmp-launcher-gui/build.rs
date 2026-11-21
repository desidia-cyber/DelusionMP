//! Puts assets/icon.ico into DelusionMP-Launcher.exe (Explorer, taskbar) with the Windows SDK's rc.exe.
//! If rc.exe is not found the exe is built without the file icon (the window still gets its icon at run time).
use std::path::{Path, PathBuf};
use std::process::Command;

fn find_rc() -> Option<PathBuf> {
    let root = Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin");
    let mut vers: Vec<PathBuf> = std::fs::read_dir(root).ok()?.flatten().map(|e| e.path()).collect();
    vers.sort();
    vers.into_iter().rev().map(|v| v.join("x64").join("rc.exe")).find(|p| p.is_file())
}

fn main() {
    println!("cargo:rerun-if-changed=assets/icon.ico");
    println!("cargo:rerun-if-changed=assets/icon.rc");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let (Some(rc), Ok(out)) = (find_rc(), std::env::var("OUT_DIR")) else {
        println!("cargo:warning=rc.exe not found: DelusionMP-Launcher.exe gets no file icon");
        return;
    };
    let res = Path::new(&out).join("icon.res");
    let ok = Command::new(rc).args(["/nologo", "/fo"]).arg(&res).arg("assets/icon.rc").status().map(|s| s.success()).unwrap_or(false);
    if ok {
        println!("cargo:rustc-link-arg-bins={}", res.display());
    } else {
        println!("cargo:warning=rc.exe failed: DelusionMP-Launcher.exe gets no file icon");
    }
}
