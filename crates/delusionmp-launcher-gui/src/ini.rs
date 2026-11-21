//! A line-based ini editor: changes one value and leaves everything else (comments, order,
//! the player's own lines) exactly as it was.

use std::fs;
use std::path::{Path, PathBuf};

pub struct Ini {
    pub path: PathBuf,
    lines: Vec<String>,
    /// a `;` after the value starts a comment (the mod's config); the launcher's own ini keeps the whole line as the value
    inline_comments: bool,
}

fn is_header(l: &str) -> Option<String> {
    let t = l.trim();
    if t.starts_with('[') && t.ends_with(']') {
        Some(t[1..t.len() - 1].trim().to_ascii_lowercase())
    } else {
        None
    }
}

impl Ini {
    pub fn load(path: &Path, inline_comments: bool) -> Ini {
        let text = fs::read_to_string(path).unwrap_or_default();
        Ini { path: path.to_path_buf(), lines: text.lines().map(str::to_string).collect(), inline_comments }
    }

    pub fn from_text(path: &Path, text: &str, inline_comments: bool) -> Ini {
        Ini { path: path.to_path_buf(), lines: text.lines().map(str::to_string).collect(), inline_comments }
    }

    pub fn save(&self) -> std::io::Result<()> {
        if let Some(d) = self.path.parent() {
            fs::create_dir_all(d)?;
        }
        let mut s = self.lines.join("\r\n");
        s.push_str("\r\n");
        fs::write(&self.path, s)
    }

    /// (index of the key's line, index one past the section's last line, whether the section exists)
    fn find(&self, sec: &str, key: &str) -> (Option<usize>, usize, bool) {
        let (sec, key) = (sec.to_ascii_lowercase(), key.to_ascii_lowercase());
        let mut cur = String::new();
        let mut found = None;
        let mut end = 0usize;
        let mut have = false;
        for (i, l) in self.lines.iter().enumerate() {
            if let Some(h) = is_header(l) {
                cur = h;
                if cur == sec {
                    have = true;
                    end = i + 1;
                }
                continue;
            }
            if cur != sec {
                continue;
            }
            let t = l.trim();
            if !t.is_empty() && !t.starts_with(';') && !t.starts_with('#') {
                end = i + 1;
                if let Some((k, _)) = t.split_once('=') {
                    if k.trim().eq_ignore_ascii_case(&key) && found.is_none() {
                        found = Some(i);
                    }
                }
            }
        }
        (found, end, have)
    }

    pub fn get(&self, sec: &str, key: &str) -> Option<String> {
        let (i, _, _) = self.find(sec, key);
        let l = &self.lines[i?];
        let (_, v) = l.split_once('=')?;
        let v = if self.inline_comments { v.split(';').next().unwrap_or("") } else { v };
        Some(v.trim().to_string())
    }

    pub fn set(&mut self, sec: &str, key: &str, val: &str) {
        let (i, end, have) = self.find(sec, key);
        match i {
            Some(i) => {
                let l = &self.lines[i];
                let eq = l.find('=').unwrap_or(0);
                let head = &l[..eq];
                let tail = if self.inline_comments { l[eq..].find(';').map(|p| &l[eq + p..]) } else { None };
                self.lines[i] = match tail {
                    Some(t) => {
                        let pad = 30usize.saturating_sub(key.len() + val.len() + 3);
                        format!("{}= {}{} {}", head, val, " ".repeat(pad), t)
                    }
                    None => format!("{}= {}", head, val),
                };
            }
            None => {
                let line = format!("{} = {}", key, val);
                if have {
                    self.lines.insert(end, line);
                } else {
                    if !self.lines.is_empty() {
                        self.lines.push(String::new());
                    }
                    self.lines.push(format!("[{}]", sec));
                    self.lines.push(line);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_comments_and_neighbours() {
        let t = "; top\r\n[Rules]\r\npvp = 0   ; hurt\r\nx = 1 ; y\r\n[Local]\r\nk = F5 ; key\r\n";
        let mut i = Ini::from_text(Path::new("x"), t, true);
        assert_eq!(i.get("Rules", "pvp").as_deref(), Some("0"));
        i.set("Rules", "pvp", "1");
        i.set("Local", "new_key", "7");
        i.set("Other", "a", "b");
        let out = i.lines.join("\n");
        assert!(out.contains("pvp = 1") && out.contains("; hurt"), "{out}");
        assert!(out.contains("x = 1 ; y") && out.contains("k = F5 ; key"));
        assert!(out.contains("new_key = 7") && out.contains("[Other]\na = b"));
        assert_eq!(i.get("Local", "new_key").as_deref(), Some("7"));
    }
}
