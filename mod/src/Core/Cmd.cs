using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DelusionMP.Game;
using DelusionMP.Sync;
using UnityEngine;

namespace DelusionMP.Core
{
    /// <summary>
    /// Rig control channel.
    ///
    /// The game cannot be driven through its UI in a background/muted rig run (windows are
    /// off-screen and never activated), and injected input is unreliable against Unity's raw
    /// input handling. Instead the mod watches a small text file inside its own BepInEx
    /// folder and executes one command per line, appending results to an output file. That
    /// is how the harness starts a save, pokes facts, asks for a world census and takes a
    /// state snapshot without touching the game window.
    ///
    ///   BepInEx/ddmp_cmd.txt   in   (truncated after being read)
    ///   BepInEx/ddmp_out.txt   out  (append-only)
    ///
    /// Commands:
    ///   status                     one-line session/census summary
    ///   facts                      dump every bool/int fact
    ///   setfact &lt;key&gt; &lt;bool|int&gt;   set a fact (host)
    ///   saveables                  count SaveableObjects and show identifier coverage
    ///   probe [n]                  dump CaptureState() runtime types (default 12)
    ///   start [hard]               start a new game in save slot 1
    ///   load                       load the current save slot
    ///   save                       write the current save
    ///   tp &lt;x&gt; &lt;y&gt; &lt;z&gt;              teleport the local player
    ///   mirror                     force a world mirror snapshot
    ///   quit                       close the session transport
    /// </summary>
    public static class Cmd
    {
        private static string _cmdPath;
        private static string _outPath;
        private static float _nextPoll;
        private static bool _ready;

        public static void Init()
        {
            try
            {
                var root = BepInEx.Paths.BepInExRootPath;
                _cmdPath = Path.Combine(root, "ddmp_cmd.txt");
                _outPath = Path.Combine(root, "ddmp_out.txt");
                _ready = true;
                Log.Info("cmd channel: " + _cmdPath);
            }
            catch (Exception e)
            {
                Log.Warn("cmd channel unavailable: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!_ready) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.5f;

            string[] lines;
            try
            {
                if (!File.Exists(_cmdPath)) return;
                lines = File.ReadAllLines(_cmdPath);
                if (lines.Length == 0) return;
                File.WriteAllText(_cmdPath, string.Empty);
            }
            catch { return; }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try { Execute(line); }
                catch (Exception e) { Emit($"ERR {line}: {e.GetType().Name}: {e.Message}"); }
            }
        }

        private static void Emit(string s)
        {
            Log.Info("[cmd] " + s);
            try { File.AppendAllText(_outPath, s + Environment.NewLine); } catch { }
        }

        private static void Execute(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "status":
                {
                    var s = Net.Session.Instance;
                    Emit($"STATUS role={s.Role} connected={s.Connected} inWorld={GameRefs.InWorld} " +
                         $"objects={ObjectRegistry.Instance.Count} scene={ObjectRegistry.Instance.CurrentScene} " +
                         $"puppet={Sync.PlayerSync.PuppetInfo} " +
                         $"uptime={Time.realtimeSinceStartup:F0}s");
                    break;
                }
                case "facts":
                {
                    var (b, i) = FactSync.Capture();
                    Emit($"FACTS bools={b.Count} ints={i.Count} fp={FactSync.Fingerprint()}");
                    var keys = new List<string>(b.Keys); keys.Sort(StringComparer.Ordinal);
                    foreach (var k in keys) Emit($"  B {k} = {b[k]}");
                    var ik = new List<string>(i.Keys); ik.Sort(StringComparer.Ordinal);
                    foreach (var k in ik) Emit($"  I {k} = {i[k]}");
                    break;
                }
                case "setfact":
                {
                    if (parts.Length < 3) { Emit("ERR setfact needs <key> <value>"); break; }
                    var facts = GameRefs.Facts;
                    if (facts == null) { Emit("ERR no FactDB"); break; }
                    string key = parts[1];
                    string val = parts[2];
                    if (val == "true" || val == "false") { facts.SetFact(key, val == "true"); Emit($"SET {key} = {val}"); }
                    else if (int.TryParse(val, out int n)) { facts.SetFact(key, n); Emit($"SET {key} = {n}"); }
                    else Emit("ERR value must be true/false/int");
                    break;
                }
                case "saveables":
                {
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    int total = all?.Length ?? 0, named = 0;
                    if (all != null)
                        foreach (var so in all)
                        {
                            if (so == null) continue;
                            try { if (!string.IsNullOrEmpty(so.GetUniqueIdentifier())) named++; } catch { }
                        }
                    Emit($"SAVEABLES total={total} named={named} registry={ObjectRegistry.Instance.Count}");
                    break;
                }
                case "probe":
                {
                    int n = parts.Length > 1 && int.TryParse(parts[1], out int x) ? x : 12;
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    if (all == null) { Emit("PROBE none"); break; }
                    int seen = 0;
                    foreach (var so in all)
                    {
                        if (so == null || seen >= n) continue;
                        string id = null;
                        try { id = so.GetUniqueIdentifier(); } catch { }
                        if (string.IsNullOrEmpty(id)) continue;
                        seen++;
                        string type = "-";
                        string fields = "";
                        try
                        {
                            var st = so.CaptureState();
                            type = StateCodec.TypeNameOf(st);
                            fields = StateCodec.DescribeFields(st);
                        }
                        catch (Exception e) { type = "ERR " + e.GetType().Name; }
                        Emit($"PROBE {id} :: {type} :: {fields}");
                    }
                    break;
                }
                case "start":
                {
                    var save = GameRefs.Save;
                    if (save == null) { Emit("ERR no DelusionSave"); break; }
                    // SaveSlotOne() is the title-screen "New Game" handler for slot 1; it
                    // performs the whole change-slot + start flow. Calling the two private
                    // halves directly (ChangeSaveSlot + StartNormalMode) leaves the menu
                    // state machine half-way and the player exits.
                    save.SaveSlotOne();
                    Emit("START SaveSlotOne requested");
                    break;
                }
                case "go":
                {
                    var save = GameRefs.Save;
                    if (save == null) { Emit("ERR no DelusionSave"); break; }
                    save.StartNormalMode();
                    Emit("GO StartNormalMode");
                    break;
                }
                case "menu":
                {
                    // The title screen runs through Unity UI buttons; invoking their onClick
                    // is exactly what a player click does, and unlike calling the save API
                    // directly it keeps the menu state machine consistent.
                    var buttons = Resources.FindObjectsOfTypeAll<global::UnityEngine.UI.Button>();
                    if (buttons == null) { Emit("MENU none"); break; }
                    Emit($"MENU total={buttons.Length}");
                    foreach (var b in buttons)
                    {
                        if (b == null) continue;
                        bool active = false;
                        try { active = b.gameObject.activeInHierarchy; } catch { }
                        var label = "";
                        try
                        {
                            var txt = b.GetComponentInChildren<global::TMPro.TextMeshProUGUI>(false);
                            if (txt != null) label = txt.text;
                        }
                        catch { }
                        Emit($"BTN {b.gameObject.name} active={active} interactable={b.interactable} label='{label}'");
                    }
                    break;
                }
                case "click":
                {
                    if (parts.Length < 2) { Emit("ERR click needs a name substring"); break; }
                    string want = string.Join(" ", parts, 1, parts.Length - 1);
                    var buttons = Resources.FindObjectsOfTypeAll<global::UnityEngine.UI.Button>();
                    bool hit = false;
                    // Prefer an active match, then fall back to any match: the title menu
                    // lives on a canvas whose state does not always read as "active" through
                    // the interop wrapper even while it is rendering.
                    for (int pass = 0; pass < 2 && !hit; pass++)
                    {
                        if (buttons == null) break;
                        foreach (var b in buttons)
                        {
                            if (b == null) continue;
                            bool active = false;
                            try { active = b.gameObject.activeInHierarchy; } catch { }
                            if (pass == 0 && !active) continue;
                            try
                            {
                                if (b.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            }
                            catch { continue; }
                            try
                            {
                                b.onClick.Invoke();
                                Emit($"CLICK {b.gameObject.name} active={active}");
                                hit = true;
                                break;
                            }
                            catch (Exception e) { Emit("ERR click invoke: " + e.Message); }
                        }
                    }
                    if (!hit) Emit("ERR no button matching: " + want);
                    break;
                }
                case "demo1":
                case "demo2":
                {
                    var save = GameRefs.Save;
                    if (save == null) { Emit("ERR no DelusionSave"); break; }
                    if (cmd == "demo1") save.DemoLevelOne(); else save.DemoLevelTwo();
                    Emit("DEMO " + cmd);
                    break;
                }
                case "selftest":
                {
                    // Round-trip every named saveable's state through the wire codec and back
                    // into the game's own RestoreState. This is the correctness proof for the
                    // mirror that does not need a second player.
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    if (all == null) { Emit("SELFTEST no saveables"); break; }
                    int tested = 0, encoded = 0, decoded = 0, restored = 0, failed = 0;
                    var firstErr = "";
                    foreach (var so in all)
                    {
                        if (so == null || tested >= 250) continue;
                        string id = null;
                        try { id = so.GetUniqueIdentifier(); } catch { }
                        if (string.IsNullOrEmpty(id)) continue;
                        tested++;
                        try
                        {
                            var state = so.CaptureState();
                            if (state == null) continue;
                            var bytes = StateCodec.Encode(state);
                            encoded++;
                            // Patch the same object with its own encoded values: a clean
                            // round-trip check for the field codec.
                            if (!StateCodec.ApplyFields(state, bytes)) continue;
                            decoded++;
                            LoopGuard.Apply(() => so.RestoreState((Il2CppSystem.Object)state));
                            restored++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            if (firstErr.Length == 0) firstErr = id + ": " + e.GetType().Name + ": " + e.Message;
                        }
                    }
                    Emit($"SELFTEST tested={tested} encoded={encoded} decoded={decoded} restored={restored} failed={failed} first={firstErr}");
                    break;
                }
                case "load":
                {
                    GameRefs.Save?.Load();
                    Emit("LOAD requested");
                    break;
                }
                case "save":
                {
                    GameRefs.Save?.Save();
                    Emit("SAVE requested");
                    break;
                }
                case "tp":
                {
                    if (parts.Length < 4) { Emit("ERR tp needs x y z"); break; }
                    var p = GameRefs.Player;
                    if (p == null) { Emit("ERR no player"); break; }
                    var pos = new Vector3(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
                    p.transform.position = pos;
                    Emit($"TP {pos}");
                    break;
                }
                case "doors":
                {
                    var doors = Resources.FindObjectsOfTypeAll<global::DoorsUltimate>();
                    if (doors == null) { Emit("DOORS none"); break; }
                    int shown = 0;
                    foreach (var d in doors)
                    {
                        if (d == null || shown >= 25) continue;
                        try
                        {
                            if (!d.gameObject.activeInHierarchy) continue;
                            var so = d.GetComponent<global::SaveableObject>();
                            string id = so != null ? so.GetUniqueIdentifier() : "(no saveable)";
                            Emit($"DOOR {id} st8={d.GetSt8()}");
                            shown++;
                        }
                        catch { }
                    }
                    Emit($"DOORS shown={shown} total={doors.Length}");
                    break;
                }
                case "opendoor":
                {
                    if (parts.Length < 2) { Emit("ERR opendoor needs an identifier substring"); break; }
                    string want = string.Join(" ", parts, 1, parts.Length - 1);
                    var doors = Resources.FindObjectsOfTypeAll<global::DoorsUltimate>();
                    bool hit = false;
                    if (doors != null)
                        foreach (var d in doors)
                        {
                            if (d == null) continue;
                            try
                            {
                                if (!d.gameObject.activeInHierarchy) continue;
                                var so = d.GetComponent<global::SaveableObject>();
                                var id = so != null ? so.GetUniqueIdentifier() : "";
                                if (string.IsNullOrEmpty(id) ||
                                    id.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                d.UnlockDoor(true, false);
                                Emit($"OPENDOOR {id} -> {d.GetSt8()}");
                                hit = true;
                                break;
                            }
                            catch (Exception e) { Emit("ERR opendoor: " + e.Message); }
                        }
                    if (!hit) Emit("ERR no door matching: " + want);
                    break;
                }
                case "newgame":
                {
                    // What the title screen actually does: pick the slot, then tell the
                    // manager a new game started (it loads the starting scene and spawns the
                    // avatar). Calling StartNormalMode from here leaves the menu state
                    // machine half-way.
                    var save = GameRefs.Save;
                    var mgr = GameRefs.Manager;
                    if (save == null || mgr == null) { Emit("ERR missing DelusionSave/Manager"); break; }
                    save.SaveSlotOne();
                    mgr.NewGameStarted();
                    Emit("NEWGAME requested");
                    break;
                }
                case "skipintro":
                {
                    var cin = GameRefs.Cinematics;
                    if (cin == null) { Emit("ERR no cinematics"); break; }
                    cin.TrySkipCinematic();
                    Emit("SKIPINTRO TrySkipCinematic");
                    break;
                }
                case "endintro":
                {
                    var cin = GameRefs.Cinematics;
                    if (cin == null) { Emit("ERR no cinematics"); break; }
                    cin.EndIntroVideo();
                    Emit("ENDINTRO EndIntroVideo");
                    break;
                }
                case "copysave":
                {
                    // Copy the owner's real save into this instance's isolated profile so a
                    // rig session starts from an actual game state. The game process (not the
                    // harness) does the I/O, so the sandbox's "workspace only" rule does not
                    // block it.
                    try
                    {
                        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        var srcDir = Path.Combine(home, "AppData", "LocalLow", "Lovely Hellplace", "Dread Delusion");
                        var dstDir = Application.persistentDataPath;
                        Directory.CreateDirectory(dstDir);
                        int n = 0;
                        foreach (var f in Directory.GetFiles(srcDir))
                        {
                            if (f.EndsWith("Player.log", StringComparison.OrdinalIgnoreCase)) continue;
                            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true);
                            n++;
                        }
                        Emit($"COPYSAVE {n} files -> {dstDir}");
                    }
                    catch (Exception e) { Emit("ERR copysave: " + e.Message); }
                    break;
                }
                case "loadsave":
                {
                    var save = GameRefs.Save;
                    if (save == null) { Emit("ERR no DelusionSave"); break; }
                    save.saveSlot = global::SaveSlot.Save1;
                    save.Load();
                    Emit("LOADSAVE requested slot=Save1");
                    break;
                }
                case "keys":
                {
                    int n = parts.Length > 1 && int.TryParse(parts[1], out int k) ? k : 8;
                    foreach (var key in ObjectRegistry.Instance.SampleKeys(n)) Emit("KEY " + key);
                    Emit($"KEYS registry={ObjectRegistry.Instance.Count}");
                    break;
                }
                case "mirrordebug":
                {
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    Emit($"MDBG saveables={all?.Length ?? 0} known={WorldMirror.KnownObjects} " +
                         $"scene='{ObjectRegistry.Instance.CurrentScene}' role={Net.Session.Instance.Role} " +
                         $"connected={Net.Session.Instance.Connected} applied={WorldMirror.AppliedCount} " +
                         $"sent={WorldMirror.SentCount} rejected={WorldMirror.RejectedCount} " +
                         $"lastReject='{WorldMirror.LastReject}' lastApply='{WorldMirror.LastApply}'");
                    WorldMirror.ForceSnapshot();
                    for (int i = 0; i < 4; i++) WorldMirror.TickHost();
                    Emit($"MDBG after-force known={WorldMirror.KnownObjects} sent={WorldMirror.SentCount}");
                    break;
                }
                case "statehash":
                {
                    var (h, n) = WorldMirror.StateHash();
                    Emit($"STATEHASH {h:X16} count={n}");
                    break;
                }
                case "resync":
                {
                    Net.Session.Instance.Resync();
                    Emit("RESYNC requested");
                    break;
                }
                case "mirror":
                {
                    WorldMirror.ForceSnapshot();
                    Emit("MIRROR requested");
                    break;
                }
                case "quit":
                    Net.Session.Instance.Close();
                    Emit("QUIT");
                    break;
                default:
                    Emit("ERR unknown command: " + cmd);
                    break;
            }
        }
    }
}
