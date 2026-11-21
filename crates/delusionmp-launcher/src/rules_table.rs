// Rules/settings for DelusionMP co-op launcher GUI.
// Reflects BepInEx config sections [Avatar] and [Rules] in delusionmp.coop.cfg.
#![allow(dead_code)]

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Kind { Int, Bool, Key, Text, Choice }

#[derive(Clone, Copy, Debug)]
pub struct ChoiceOpt { pub value: &'static str, pub label: &'static str }

#[derive(Clone, Copy, Debug)]
pub struct Rule {
    pub id: u8,                // compatibility
    pub local: bool,           // false => host-applies/session rules; true => local player prefs
    pub kind: Kind,
    pub name: &'static str,    // BepInEx key
    pub def: i32,              // default for numeric/bool/key/choice index
    pub lo: i32,
    pub hi: i32,
    pub comment: &'static str,
    pub section: &'static str, // BepInEx section
    pub def_s: &'static str,   // default string value
    pub scale10: bool,         // if true, int represents value*10
    pub choices: &'static [ChoiceOpt],
}

pub const MODEL_CHOICES: &[ChoiceOpt] = &[
    ChoiceOpt { value: "fbx_UnionWomen", label: "Union Woman (dressable)" },
    ChoiceOpt { value: "fbx_DarkStarWomen", label: "Dark Star Woman" },
    ChoiceOpt { value: "fbx_PwyllWomen", label: "Pwyll Woman" },
];

pub const OUTFIT_CHOICES: &[ChoiceOpt] = &[
    ChoiceOpt { value: "thief", label: "Thief (preset)" },
    ChoiceOpt { value: "civilianA", label: "Civilian A (preset)" },
    ChoiceOpt { value: "civilianB", label: "Civilian B (preset)" },
    ChoiceOpt { value: "civilianC", label: "Civilian C (preset)" },
    ChoiceOpt { value: "soldier", label: "Soldier (preset)" },
    ChoiceOpt { value: "inquisitor", label: "Inquisitor (preset)" },
    ChoiceOpt { value: "cultist", label: "Cultist (preset)" },
];

pub const KEY_CHOICES: &[ChoiceOpt] = &[
    ChoiceOpt { value: "V", label: "V" },
    ChoiceOpt { value: "B", label: "B" },
    ChoiceOpt { value: "T", label: "T" },
    ChoiceOpt { value: "F1", label: "F1" },
    ChoiceOpt { value: "F2", label: "F2" },
    ChoiceOpt { value: "F3", label: "F3" },
    ChoiceOpt { value: "F4", label: "F4" },
    ChoiceOpt { value: "F5", label: "F5" },
    ChoiceOpt { value: "F6", label: "F6" },
    ChoiceOpt { value: "F7", label: "F7" },
    ChoiceOpt { value: "F8", label: "F8" },
    ChoiceOpt { value: "F9", label: "F9" },
    ChoiceOpt { value: "F10", label: "F10" },
    ChoiceOpt { value: "F11", label: "F11" },
    ChoiceOpt { value: "F12", label: "F12" },
    ChoiceOpt { value: "LeftCtrl", label: "Left Ctrl" },
    ChoiceOpt { value: "RightCtrl", label: "Right Ctrl" },
    ChoiceOpt { value: "Tab", label: "Tab" },
    ChoiceOpt { value: "none", label: "none" },
];

pub const RULES: &[Rule] = &[
    // Avatar (local)
    Rule { id: 1, local: true, kind: Kind::Choice, name: "Model", def: 0, lo: 0, hi: 2, comment: "Character model your partner sees you as. Union Woman supports outfits.", section: "Avatar", def_s: "fbx_UnionWomen", scale10: false, choices: MODEL_CHOICES },
    Rule { id: 2, local: true, kind: Kind::Choice, name: "Outfit", def: 0, lo: 0, hi: 6, comment: "Outfit preset for Union Woman (or set a custom comma-separated list in the config file).", section: "Avatar", def_s: "thief", scale10: false, choices: OUTFIT_CHOICES },
    // Rules (host-applies)
    Rule { id: 10, local: false, kind: Kind::Bool, name: "ExclusiveUniqueShopItems", def: 0, lo: 0, hi: 1, comment: "Only one player can buy a one-of-a-kind shop item. Off = each player may buy their own copy.", section: "Rules", def_s: "false", scale10: false, choices: &[] },
    Rule { id: 11, local: false, kind: Kind::Bool, name: "DeathDebuffs", def: 1, lo: 0, hi: 1, comment: "Dying while your partner is alive adds a stack that lowers max health/mana/stamina until you rest in a bed.", section: "Rules", def_s: "true", scale10: false, choices: &[] },
    Rule { id: 12, local: false, kind: Kind::Int, name: "MaxStacks", def: 5, lo: 0, hi: 20, comment: "Most death stacks a player can carry.", section: "Rules", def_s: "5", scale10: false, choices: &[] },
    Rule { id: 13, local: false, kind: Kind::Int, name: "PercentPerStack", def: 100, lo: 0, hi: 1000, comment: "Max health/mana/stamina lost per stack, in %.", section: "Rules", def_s: "10", scale10: true, choices: &[] },
    Rule { id: 14, local: false, kind: Kind::Int, name: "RespawnDelaySeconds", def: 80, lo: 0, hi: 600, comment: "Seconds a player stays down when the partner is alive.", section: "Rules", def_s: "8", scale10: true, choices: &[] },
    Rule { id: 15, local: false, kind: Kind::Bool, name: "RespawnAtPartner", def: 1, lo: 0, hi: 1, comment: "Get up next to the partner instead of at the nearest respawn point (when the partner is alive).", section: "Rules", def_s: "true", scale10: false, choices: &[] },
    // Chat and voice: the same local settings the DollsMP launcher offers. Written into the mod's
    // BepInEx config [Voice] section (and mirrored in the launcher's own ini) so they take effect.
    Rule { id: 110, local: true, kind: Kind::Bool, name: "ChatEnabled", def: 1, lo: 0, hi: 1, comment: "chat_key opens a line of text chat; partner lines are still shown when off", section: "Voice", def_s: "true", scale10: false, choices: &[] },
    Rule { id: 111, local: true, kind: Kind::Choice, name: "ChatKey", def: 2, lo: 0, hi: 18, comment: "key that opens the chat line; Enter sends, Esc cancels", section: "Voice", def_s: "T", scale10: false, choices: KEY_CHOICES },
    Rule { id: 112, local: true, kind: Kind::Bool, name: "VoiceChat", def: 1, lo: 0, hi: 1, comment: "proximity voice chat: you hear your partner and can talk; off = no microphone, no sound", section: "Voice", def_s: "true", scale10: false, choices: &[] },
    Rule { id: 113, local: true, kind: Kind::Int, name: "VoiceMode", def: 0, lo: 0, hi: 1, comment: "how your microphone is used: hold the key to talk, or always on", section: "Voice", def_s: "0", scale10: false, choices: &[] },
    Rule { id: 114, local: true, kind: Kind::Choice, name: "VoiceKey", def: 0, lo: 0, hi: 18, comment: "push-to-talk key (only in hold-the-key mode)", section: "Voice", def_s: "V", scale10: false, choices: KEY_CHOICES },
    Rule { id: 115, local: true, kind: Kind::Int, name: "VoiceRangeM", def: 80, lo: 0, hi: 2000, comment: "your partner's voice fades to nothing at this many metres; 0 = no proximity", section: "Voice", def_s: "80", scale10: false, choices: &[] },
    Rule { id: 116, local: true, kind: Kind::Int, name: "VoiceVolumePct", def: 100, lo: 0, hi: 200, comment: "how loud your partner's voice is, in %", section: "Voice", def_s: "100", scale10: false, choices: &[] },
    Rule { id: 117, local: true, kind: Kind::Int, name: "VoiceMicPct", def: 100, lo: 0, hi: 300, comment: "how loud your microphone is sent, in %", section: "Voice", def_s: "100", scale10: false, choices: &[] },
    Rule { id: 118, local: true, kind: Kind::Int, name: "VoiceAwayPct", def: 40, lo: 0, hi: 100, comment: "your partner's volume while they are in another level, a menu or loading", section: "Voice", def_s: "40", scale10: false, choices: &[] },
];

pub fn key_name(vk: i32) -> String { if vk == 0 { "none".into() } else { vk.to_string() } }

pub fn format_float10(v: i32) -> String {
    let sign = if v < 0 { "-" } else { "" };
    let a = v.abs();
    if a % 10 == 0 { format!("{sign}{}", a / 10) } else { format!("{sign}{}.{}", a / 10, a % 10) }
}

pub fn parse_float10(s: &str) -> Option<i32> {
    let t = s.trim().replace(',', ".");
    if let Ok(f) = t.parse::<f64>() { Some((f * 10.0).round() as i32) } else { s.trim().parse::<i32>().ok().map(|i| i*10) }
}

pub fn value_text(r: &Rule, v: i32) -> String {
    match r.kind {
        Kind::Bool => if v != 0 { "true".into() } else { "false".into() },
        Kind::Int if r.scale10 => format_float10(v),
        Kind::Key => key_name(v),
        Kind::Choice => r.choices.get(v.clamp(0, (r.choices.len().saturating_sub(1)) as i32) as usize).map(|c| c.value.to_string()).unwrap_or_else(|| r.def_s.to_string()),
        Kind::Text => r.def_s.to_string(),
        Kind::Int => v.to_string(),
    }
}

pub fn choice_index_for(r: &Rule, val: &str) -> i32 {
    if let Some((i,_)) = r.choices.iter().enumerate().find(|(_,c)| c.value.eq_ignore_ascii_case(val.trim())) { i as i32 } else { r.def }
}

pub fn default_bepinex_cfg() -> String {
    let mut s = String::new();
    s.push_str("## Settings file was created by plugin DelusionMP v0.1.0\r\n");
    s.push_str("## Plugin GUID: delusionmp.coop\r\n\r\n");
    let mut sec = "";
    for r in RULES {
        if r.section != sec {
            sec = r.section;
            s.push_str(&format!("[{}]\r\n\r\n", sec));
        }
        s.push_str(&format!("## {}\r\n", r.comment));
        let ty = match r.kind { Kind::Bool => "Boolean", Kind::Int if r.scale10 => "Single", Kind::Int => "Int32", _ => "String" };
        s.push_str(&format!("# Setting type: {}\r\n", ty));
        let dv = value_text(r, r.def);
        s.push_str(&format!("# Default value: {}\r\n", dv));
        s.push_str(&format!("{} = {}\r\n\r\n", r.name, dv));
    }
    s
}

/// The [Voice] section written into DelusionMP-Coop.ini (the launcher's own file).
pub fn default_voice_ini() -> String {
    let mut s = String::from("\r\n[Voice]\r\n");
    for r in RULES.iter().filter(|r| r.section == "Voice") {
        s.push_str(&format!("{} = {}\r\n", r.name, value_text(r, r.def)));
    }
    s
}

pub fn default_ini() -> String { default_bepinex_cfg() }
