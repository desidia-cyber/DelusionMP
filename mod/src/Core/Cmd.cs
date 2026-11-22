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

        // "after <seconds> <command>": run later (frame-accurate captures of short actions).
        private static readonly List<(float at, string line)> _delayed = new List<(float, string)>();

        private static void Run(string line)
        {
            // Several commands on one line, separated by ';'.
            foreach (var part in line.Split(';'))
            {
                var cmd = part.Trim();
                if (cmd.Length == 0) continue;
                if (cmd.StartsWith("after ", StringComparison.Ordinal))
                {
                    var bits = cmd.Split(new[] { ' ' }, 3);
                    if (bits.Length == 3 && float.TryParse(bits[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    {
                        _delayed.Add((Time.realtimeSinceStartup + sec, bits[2]));
                        continue;
                    }
                }
                try { Execute(cmd); }
                catch (Exception e) { Emit($"ERR {cmd}: {e.GetType().Name}: {e.Message}"); }
            }
        }

        // ---- scanscenes: load each region briefly, list where interesting objects are, unload.
        private static readonly Queue<int> _scanQueue = new Queue<int>();
        private static AsyncOperation _scanOp;
        private static int _scanIdx = -1;
        private static bool _scanUnload;

        private static void ScanTick()
        {
            if (_scanOp != null)
            {
                if (!_scanOp.isDone) return;
                _scanOp = null;
                if (_scanUnload) { _scanUnload = false; return; }
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(_scanIdx);
                ScanReport(sc);
                _scanUnload = true;
                try { _scanOp = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(_scanIdx); } catch { _scanOp = null; _scanUnload = false; }
                return;
            }
            if (_scanQueue.Count == 0) return;
            _scanIdx = _scanQueue.Dequeue();
            var existing = UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(_scanIdx);
            if (existing.IsValid() && existing.isLoaded) { ScanReport(existing); return; }
            try { _scanOp = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(_scanIdx, UnityEngine.SceneManagement.LoadSceneMode.Additive); }
            catch (Exception e) { Emit($"SCAN {_scanIdx} load failed: {e.Message}"); }
        }

        private static void ScanReport(UnityEngine.SceneManagement.Scene sc)
        {
            int shops = 0, traps = 0, comps = 0, facs = 0;
            foreach (var root in sc.GetRootGameObjects())
            {
                foreach (var x in root.GetComponentsInChildren<global::ShopSpeaker>(true)) { shops++; Emit($"SCAN {_scanIdx} {sc.name} SHOP {x.name} at {x.transform.position}"); }
                foreach (var x in root.GetComponentsInChildren<global::Trap>(true)) { if (traps++ < 3) Emit($"SCAN {_scanIdx} {sc.name} TRAP {x.name} at {x.transform.position}"); }
                foreach (var x in root.GetComponentsInChildren<global::CompletableObject>(true)) { comps++; Emit($"SCAN {_scanIdx} {sc.name} COMPLETABLE {x.name} at {x.transform.position}"); }
                foreach (var x in root.GetComponentsInChildren<global::MovingFactory>(true)) { facs++; Emit($"SCAN {_scanIdx} {sc.name} FACTORY {x.name} at {x.transform.position}"); }
                foreach (var x in root.GetComponentsInChildren<global::EnemyStats>(true))
                {
                    int mh = 0; try { mh = x.maxHealth; } catch { }
                    if (mh >= 250) { var p = x.transform.position; Emit($"SCAN {_scanIdx} {sc.name} BOSS {x.gameObject.name}|{mh}|{p.x:F1}|{p.y:F1}|{p.z:F1}|{ObjectRegistry.HierarchyPath(x.transform)}"); }
                }
                foreach (var x in root.GetComponentsInChildren<global::CutsceneTrigger>(true))
                { var p = x.transform.position; Emit($"SCAN {_scanIdx} {sc.name} CUTSCENE {x.name}|{p.x:F1}|{p.y:F1}|{p.z:F1}"); }
                foreach (var x in root.GetComponentsInChildren<global::NPCBase>(true))
                {
                    if (x.dialogueTree == null) continue;
                    var p = x.transform.position; Emit($"SCAN {_scanIdx} {sc.name} NPC {x.dialogueTree.name}|{p.x:F1}|{p.y:F1}|{p.z:F1}");
                }
            }
            Emit($"SCAN {_scanIdx} {sc.name} shops={shops} traps={traps} completable={comps} factories={facs}");
        }

        private static float _holdUntil = -1f;
        private static Vector3 _holdPos;

        private static bool _god;

        private static void HoldTick(float now)
        {
            if (_god)
            {
                try { var cs = GameRefs.Stats; if (cs != null) cs.currentHealth = cs.maxHealth.GetValue(); } catch { }
            }
            if (_holdUntil < 0f) return;
            var p = GameRefs.Player;
            if (p == null) return;
            if (now < _holdUntil) { p.transform.position = _holdPos; return; }
            _holdUntil = -1f;
            var pm = p.GetComponent<global::PlayerMove>();
            var cc = p.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;
            if (pm != null) pm.enabled = true;
        }

        public static void Tick()
        {
            if (!_ready) return;
            float now = Time.realtimeSinceStartup;
            HoldTick(now);
            ScanTick();
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                if (now < _delayed[i].at) continue;
                var line = _delayed[i].line;
                _delayed.RemoveAt(i);
                Run(line);
            }
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
                Run(line);
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
                         $"gs={(GameRefs.Manager != null ? GameRefs.Manager.CurrentGameState.ToString() : "-")} " +
                         $"active={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} " +
                         $"uptime={Time.realtimeSinceStartup:F0}s");
                    break;
                }
                case "facts":
                {
                    var (sb, si) = FactSync.CaptureShared();
                    var (b, i) = parts.Length > 1 && parts[1] == "all" ? FactSync.Capture() : (sb, si);
                    Emit($"FACTS shared bools={sb.Count} ints={si.Count} fp={FactSync.Fingerprint()} " +
                         $"(player-owned keys={FactSync.PlayerOwnedCount})");
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
                case "resolve":
                {
                    foreach (var m in ObjectRegistry.Instance.Matching(parts[1])) Emit("RESOLVE " + m);
                    foreach (var so in Resources.FindObjectsOfTypeAll<global::SaveableObject>())
                    {
                        if (so == null) continue;
                        string id = null; try { id = so.GetUniqueIdentifier(); } catch { }
                        if (id != null && id.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0)
                            Emit($"  INSTANCE {so.gameObject.name} iid={so.GetInstanceID()} scene={so.gameObject.scene.name} valid={so.gameObject.scene.IsValid()} active={so.gameObject.activeInHierarchy}");
                    }
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
                            // Rebuild from the wire form, re-encode, compare, then restore.
                            var built = StateCodec.Build(bytes);
                            if (built == null) continue;
                            var again = StateCodec.Encode(built);
                            if (!System.Linq.Enumerable.SequenceEqual(bytes, again))
                            {
                                failed++;
                                if (firstErr.Length == 0) firstErr = id + ": round-trip differs " +
                                    StateCodec.Describe(bytes, 300) + " VS " + StateCodec.Describe(again, 300);
                                continue;
                            }
                            decoded++;
                            LoopGuard.Apply(() => so.RestoreState(built));
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
                case "res":
                {
                    // The rig renders tiny (320x180) to stay cheap; bump it for proof shots.
                    int w = parts.Length > 2 ? int.Parse(parts[1]) : 1280;
                    int h = parts.Length > 2 ? int.Parse(parts[2]) : 720;
                    Screen.SetResolution(w, h, FullScreenMode.Windowed);
                    Emit($"RES {w}x{h}");
                    break;
                }
                case "shot":
                {
                    string name = parts.Length > 1 ? parts[1] : "ddmp_shot.png";
                    string path = Path.IsPathRooted(name) ? name : Path.Combine(BepInEx.Paths.BepInExRootPath, name);
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    ScreenCapture.CaptureScreenshot(path);
                    Emit($"SHOT {path} {Screen.width}x{Screen.height}");
                    break;
                }
                case "hud":
                {
                    // Hide/show every screen-space canvas so a proof shot shows the world.
                    bool on = parts.Length < 2 || parts[1] != "off";
                    var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
                    int n = 0;
                    if (canvases != null)
                        foreach (var c in canvases)
                        {
                            if (c == null || c.renderMode == RenderMode.WorldSpace) continue;
                            c.enabled = on; n++;
                        }
                    Emit($"HUD {(on ? "on" : "off")} canvases={n}");
                    break;
                }
                case "pos":
                {
                    var p = GameRefs.Player;
                    if (p == null) { Emit("POS no player"); break; }
                    var t = p.transform;
                    PlayerSync.TryPeerPose(out var pp, out var py);
                    Emit($"POS me={t.position.x:F1},{t.position.y:F1},{t.position.z:F1} yaw={t.eulerAngles.y:F0} " +
                         $"peer={pp.x:F1},{pp.y:F1},{pp.z:F1} yaw={py:F0} dist={Vector3.Distance(t.position, pp):F1}");
                    break;
                }
                case "tppeer":
                {
                    // Stand <dist> metres in front of the partner (along their facing).
                    float dist = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 4f;
                    if (!PlayerSync.TryPeerPose(out var pp, out var py)) { Emit("ERR no peer pose yet"); break; }
                    var fwd = Quaternion.Euler(0, py, 0) * Vector3.forward;
                    Teleport(pp + fwd * dist + Vector3.up * 0.1f);
                    FacePeer();
                    break;
                }
                case "facepeer":
                    FacePeer();
                    break;
                case "lookat":
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    LookAt(new Vector3(float.Parse(parts[1], ci), float.Parse(parts[2], ci), float.Parse(parts[3], ci)));
                    break;
                }
                case "hier":
                {
                    var root = parts.Length > 1 && parts[1] == "puppet"
                        ? GameObject.Find("DelusionMP_RemotePuppet") : GameRefs.Player;
                    if (root == null) { Emit("HIER none"); break; }
                    DumpHier(root.transform, 0);
                    break;
                }
                case "enemies":
                {
                    int n = parts.Length > 1 ? int.Parse(parts[1]) : 6;
                    foreach (var (key, c, dist) in EnemySync.Nearest(n))
                    {
                        int hp = -1;
                        try { hp = c.myStats != null ? c.myStats.currentHealth : -1; } catch { }
                        Emit($"ENEMY {key} name={c.gameObject.name} hp={hp} dist={dist:F1} dead={EnemySync.IsDead(c)} " +
                             $"pos={c.transform.position.x:F1},{c.transform.position.y:F1},{c.transform.position.z:F1}" + EnemySync.PuppetInfo(c));
                    }
                    Emit("ENEMYSTATS " + EnemySync.Stats);
                    break;
                }
                case "hitenemy":
                {
                    // Hit the nearest live enemy exactly as a player weapon does.
                    int dmg = parts.Length > 1 ? int.Parse(parts[1]) : 10;
                    string want = parts.Length > 2 ? parts[2] : null;
                    foreach (var (key, c, dist) in EnemySync.Nearest(40))
                    {
                        if (EnemySync.IsDead(c)) continue;
                        if (want != null && key.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var trig = c.GetComponentInChildren<global::EnemyDamageTrigger>(true);
                        if (trig == null) continue;
                        int before = c.myStats != null ? c.myStats.currentHealth : -1;
                        trig.ApplyDamage(dmg, 0f, 0, 0, 0, 1f, false, true, null, 0);
                        int after = c.myStats != null ? c.myStats.currentHealth : -1;
                        Emit($"HIT {key} {before}->{after} dist={dist:F1}");
                        break;
                    }
                    break;
                }
                case "tpenemy":
                {
                    // Stand next to an enemy (by key substring) so both players can watch it.
                    string want = parts.Length > 1 ? parts[1] : "";
                    float off = parts.Length > 2 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 5f;
                    foreach (var (key, c, dist) in EnemySync.Nearest(200))
                    {
                        if (key.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ep = c.transform.position;
                        Teleport(ep + new Vector3(off, 0.5f, 0));
                        Emit($"TPENEMY {key}");
                        break;
                    }
                    break;
                }
                case "avatar":
                {
                    Emit("AVATAR " + RemoteAvatar.Info);
                    if (parts.Length > 1 && parts[1] == "parts") Emit("  PARTS " + string.Join(",", RemoteAvatar.PartNames));
                    break;
                }
                case "avplay":
                {
                    Emit("AVPLAY " + RemoteAvatar.DebugPlay(parts.Length > 1 ? parts[1] : ""));
                    break;
                }
                case "avmodels":
                {
                    foreach (var m in RemoteAvatar.ListModels()) Emit("  MODEL " + m);
                    Emit("AVMODELS " + RemoteAvatar.PreloadInfo);
                    break;
                }
                case "avsource":
                {
                    // avsource <model GameObject name> [outfit,parts|-]  : rebuild the partner's body
                    RemoteAvatar.SourceName = parts.Length > 1 ? parts[1] : "fbx_UnionWomen";
                    if (parts.Length > 2) RemoteAvatar.Outfit = parts[2] == "-" ? new string[0] : parts[2].Split(',');
                    PlayerSync.DestroyPuppet();
                    Emit("AVSOURCE " + RemoteAvatar.SourceName);
                    break;
                }
                case "outfit":
                {
                    if (parts.Length < 2) { Emit("ERR outfit a,b,c"); break; }
                    RemoteAvatar.SetOutfit(parts[1].Split(','));
                    Emit("OUTFIT " + parts[1]);
                    break;
                }
                case "heldrot":
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    RemoteAvatar.HeldEuler = new Vector3(float.Parse(parts[1], ci), float.Parse(parts[2], ci), float.Parse(parts[3], ci));
                    if (parts.Length > 6) RemoteAvatar.HeldOffset = new Vector3(float.Parse(parts[4], ci), float.Parse(parts[5], ci), float.Parse(parts[6], ci));
                    RemoteAvatar.RetuneHeld();
                    Emit($"HELDROT {RemoteAvatar.HeldEuler} {RemoteAvatar.HeldOffset}");
                    break;
                }
                case "avtest":
                {
                    // avtest off | avtest <weaponType> <rarity> <weaponState> <attack> <spellState> <spellType>
                    if (parts.Length < 2 || parts[1] == "off") { RemoteAvatar.Override = null; Emit("AVTEST off"); break; }
                    var o = new AvatarState
                    {
                        WeaponType = byte.Parse(parts[1]), Rarity = byte.Parse(parts[2]), WeaponState = byte.Parse(parts[3]),
                        Attack = byte.Parse(parts[4]), SpellState = byte.Parse(parts[5]), SpellType = byte.Parse(parts[6]),
                    };
                    RemoteAvatar.Override = o;
                    Emit("AVTEST " + string.Join(" ", parts, 1, parts.Length - 1));
                    break;
                }
                case "cuts":
                {
                    // World cutscene triggers in memory: name, played flag, distance.
                    var me = GameRefs.Player;
                    foreach (var ct in Resources.FindObjectsOfTypeAll<global::CutsceneTrigger>())
                    {
                        if (ct == null || !ct.gameObject.scene.IsValid()) continue;
                        float d = me != null ? Vector3.Distance(me.transform.position, ct.transform.position) : -1f;
                        Emit($"CUT {ct.name} scene={(ct.sceneToPlay != null ? ct.sceneToPlay.name : "-")} played={ct.hasBeenPlayed} active={ct.gameObject.activeInHierarchy} d={d:F0}");
                    }
                    Emit("CUTS " + CutsceneSync.Stats);
                    break;
                }
                case "cut":
                {
                    string want = parts.Length > 1 ? parts[1] : "";
                    foreach (var ct in Resources.FindObjectsOfTypeAll<global::CutsceneTrigger>())
                    {
                        if (ct == null || !ct.gameObject.scene.IsValid()) continue;
                        if (ct.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0
                            && (ct.sceneToPlay == null || ct.sceneToPlay.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        ct.TriggerCutscene();
                        Emit($"CUT played {ct.name}");
                        break;
                    }
                    break;
                }
                case "skipcut":
                {
                    global::DelusionCinematics.Instance?.TrySkipCinematic();
                    Emit("SKIPCUT");
                    break;
                }
                case "coopdeath":
                {
                    if (parts.Length > 1) CoopDeath.SetStacks(int.Parse(parts[1]), "command");
                    var cs = GameRefs.Stats;
                    string hp = cs != null ? $" hp={cs.currentHealth:F0}/{cs.maxHealth.GetValue()} mana={cs.maxMana.GetValue()} stam={cs.maxStamina.GetValue()}" : "";
                    Emit("COOPDEATH " + CoopDeath.Stats + hp);
                    break;
                }
                case "playcine":
                {
                    // playcine fortressScene|cutterLiftoff|angelAwaken|velaKillPlayer
                    var m = CutsceneSync.Manager;
                    UnityEngine.Playables.PlayableDirector d = null;
                    if (m != null)
                        switch (parts.Length > 1 ? parts[1] : "")
                        {
                            case "fortressScene": d = m.fortressScene; break;
                            case "cutterLiftoff": d = m.cutterLiftoff; break;
                            case "angelAwaken": d = m.angelAwaken; break;
                            case "velaKillPlayer": d = m.velaKillPlayer; break;
                        }
                    if (d == null) { Emit("ERR no such cinematic"); break; }
                    global::DelusionCinematics.Instance.WorldCutscene(d, true, false);
                    Emit($"PLAYCINE {parts[1]} state={GameRefs.Manager?.CurrentGameState} {CutsceneSync.Stats}");
                    break;
                }
                case "bedrest":
                {
                    // The last step of sleeping in a bed (what the rest fade ends with).
                    var dn = GameRefs.DayNight;
                    if (dn == null) { Emit("ERR no day/night"); break; }
                    dn.restingInBed = true;
                    dn.hourAfterScreenFade = parts.Length > 1 ? int.Parse(parts[1]) : 9;
                    dn.ScreenFadeEnded();
                    Emit("BEDREST " + CoopDeath.Stats);
                    break;
                }
                case "underfoot":
                {
                    Emit("UNDERFOOT " + PlayerSync.UnderfootInfo());
                    break;
                }
                case "loadscene":
                {
                    int idx = int.Parse(parts[1]);
                    UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(idx, UnityEngine.SceneManagement.LoadSceneMode.Additive);
                    Emit("LOADSCENE " + idx);
                    break;
                }
                case "factory":
                {
                    foreach (var f in Resources.FindObjectsOfTypeAll<global::MovingFactory>())
                    {
                        if (f == null || !f.gameObject.scene.IsValid()) continue;
                        var mt = f.moveObjectTransform;
                        var mp = mt != null ? mt.position : f.transform.position;
                        Emit($"FACTORY active={f.gameObject.activeInHierarchy} state={f.currentMoveState} pos={mp.x:F1},{mp.y:F1},{mp.z:F1} rotY={(mt != null ? mt.eulerAngles.y : 0f):F0}{FactorySync.DebugFor(f)}");
                    }
                    Emit("FACTORY stream " + FactorySync.Stats);
                    break;
                }
                case "complete":
                {
                    foreach (var co in Resources.FindObjectsOfTypeAll<global::CompletableObject>())
                    {
                        if (co == null || !co.gameObject.scene.IsValid()) continue;
                        if (parts.Length > 1 && parts[1] == "check") { Emit($"COMPLETABLE {co.name} active={co.gameObject.activeInHierarchy}"); continue; }
                        co.AllComponentsComplete();
                        Emit($"COMPLETE {co.name}");
                    }
                    break;
                }
                case "charmtest":
                {
                    // Inside a conversation: show a charm-roll result as the game does.
                    var note = global::DelusionNote.Instance;
                    note.SetCharmInt(5);
                    note.StartCharmNote(parts.Length > 1 && parts[1] == "fail" ? false : true, false, false);
                    Emit("CHARMTEST " + DialogueSync.Stats);
                    break;
                }
                case "shop":
                {
                    // shop [n]: nearest shops and their stock
                    var me = GameRefs.Player;
                    foreach (var sh in Resources.FindObjectsOfTypeAll<global::ShopSpeaker>())
                    {
                        if (sh == null || !sh.gameObject.activeInHierarchy || me == null) continue;
                        float d = Vector3.Distance(sh.transform.position, me.transform.position);
                        var inv = sh.shopInventory;
                        var names = new List<string>();
                        if (inv != null) for (int k = 0; k < inv.Count && k < 12; k++) { var it = inv[k]; if (it != null) names.Add($"{k}:{it.itemInstanceID}x{it.amount}"); }
                        Emit($"SHOP {sh.name} d={d:F0} pos={sh.transform.position} items={string.Join(" ", names)}");
                    }
                    break;
                }
                case "sell":
                {
                    // sell <shop name substring> <item id>: sell through the game's own path
                    global::ShopSpeaker shop = null;
                    foreach (var sh in Resources.FindObjectsOfTypeAll<global::ShopSpeaker>())
                        if (sh != null && sh.gameObject.activeInHierarchy && sh.name.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0) { shop = sh; break; }
                    if (shop == null) { Emit("ERR no shop"); break; }
                    var pinv = GameRefs.Inventory;
                    var item = pinv?.GetItemFromInvAnyRarity(parts[2]);
                    if (item == null) { Emit("ERR not in inventory: " + parts[2]); break; }
                    int before = shop.shopInventory?.Count ?? -1;
                    shop.Activate();
                    pinv.SwitchToSell();
                    pinv.StartSellItem(item);
                    pinv.FinishSellItem(false);
                    Emit($"SELL {parts[2]} to {shop.name} stock {before}->{shop.shopInventory?.Count ?? -1} {WorldEventsSync.Stats}");
                    break;
                }
                case "buy":
                {
                    // buy <shop name substring> <index>: buy through the game's own purchase path
                    global::ShopSpeaker shop = null;
                    foreach (var sh in Resources.FindObjectsOfTypeAll<global::ShopSpeaker>())
                        if (sh != null && sh.gameObject.activeInHierarchy && sh.name.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0) { shop = sh; break; }
                    if (shop == null) { Emit("ERR no shop"); break; }
                    var item = shop.shopInventory[int.Parse(parts[2])];
                    var pinv = GameRefs.Inventory;
                    shop.Activate();
                    pinv.StartBuyItem(item);
                    pinv.FinishBuyItem(false);
                    Emit($"BUY {item.itemInstanceID} from {shop.name} stockNow={shop.shopInventory.Count} {WorldEventsSync.Stats}");
                    break;
                }
                case "traps":
                {
                    var me = GameRefs.Player;
                    var list = new List<(float, global::Trap)>();
                    foreach (var t in Resources.FindObjectsOfTypeAll<global::Trap>())
                        if (t != null && t.gameObject.activeInHierarchy && me != null)
                            list.Add((Vector3.Distance(t.transform.position, me.transform.position), t));
                    list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                    for (int k = 0; k < list.Count && k < 3; k++)
                        Emit($"TRAPSTATE {list[k].Item2.transform.parent?.name}/{list[k].Item2.name} locked={list[k].Item2.lockStats?.isLocked} tag={list[k].Item2.tag} d={list[k].Item1:F1}");
                    break;
                }
                case "statues":
                {
                    foreach (var co in Resources.FindObjectsOfTypeAll<global::CompletableObject>())
                    {
                        if (co == null || !co.gameObject.scene.IsValid()) continue;
                        string anim = "";
                        try { var a2 = co.animator; if (a2 != null) { var st = a2.GetCurrentAnimatorStateInfo(0); anim = $" animState={st.shortNameHash} t={st.normalizedTime:F2}"; } } catch { }
                        Emit($"STATUE {co.name} active={co.gameObject.activeInHierarchy}{anim}");
                    }
                    break;
                }
                case "voice":
                    Emit("VOICE " + VoiceSync.Stats);
                    break;
                case "voicetalk":
                {
                    float sec = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vs) ? vs : 3f;
                    VoiceSync.TestTalk(sec);
                    Emit($"VOICETALK {sec}s");
                    break;
                }
                case "chat":
                    VoiceSync.SendChat(string.Join(" ", parts, 1, parts.Length - 1));
                    Emit("CHAT sent");
                    break;
                case "chatopen":
                    VoiceSync.OpenChat();
                    Emit("CHAT open=" + VoiceSync.ChatOpen);
                    break;
                case "chatclose":
                    VoiceSync.CloseChat();
                    Emit("CHAT open=" + VoiceSync.ChatOpen);
                    break;
                case "chatlog":
                    lock (VoiceSync.ChatLines) foreach (var cl in VoiceSync.ChatLines) Emit("CHATLINE " + cl);
                    Emit($"CHAT open={VoiceSync.ChatOpen} text='{VoiceSync.ChatText}'");
                    break;
                case "rules":
                    Emit("RULES " + RulesSync.Describe());
                    break;
                case "inputmaps":
                {
                    var di = global::DelusionInput.Instance;
                    if (di == null) { Emit("ERR no DelusionInput"); break; }
                    void Dump(string tag, UnityEngine.InputSystem.InputActionAsset a)
                    {
                        if (a == null) { Emit($"MAPS {tag} null"); return; }
                        var ms = a.actionMaps;
                        for (int i = 0; i < ms.Count; i++) Emit($"MAPS {tag} {ms[i].name} enabled={ms[i].enabled}");
                    }
                    Dump("controls", di.controls);
                    try { Dump("delusionControls", di.delusionControls?.asset); } catch (Exception e) { Emit("MAPS delusionControls err " + e.Message); }
                    try { Dump("playerInput", di.playerInput?.actions); } catch (Exception e) { Emit("MAPS playerInput err " + e.Message); }
                    Emit($"MOVE {di.moveInput}");
                    try { var kb = UnityEngine.InputSystem.Keyboard.current; Emit($"KEYBOARD {(kb != null ? kb.name : "null")} T={kb?[UnityEngine.InputSystem.Key.T].isPressed} W={kb?[UnityEngine.InputSystem.Key.W].isPressed}"); } catch (Exception e) { Emit("KEYBOARD err " + e.Message); }
                    try { Emit($"LEGACY W={Input.GetKey(KeyCode.W)}"); } catch (Exception e) { Emit("LEGACY err " + e.GetType().Name); }
                    break;
                }
                case "perf":
                {
                    Emit("MIRROR " + WorldMirror.Slowest);
                    foreach (var pl in Perf.Report(parts.Length > 1 && parts[1] == "reset").Split((char)10)) Emit(pl);
                    break;
                }
                case "taskdb":
                {
                    // Every quest the game defines, with its stage ids (in definition order).
                    var tf = global::DelusionTextFiles.Instance;
                    var db = tf != null ? tf.taskTextData : null;
                    if (db == null) { Emit("ERR no task data"); break; }
                    int n = 0;
                    foreach (var kv in db)
                    {
                        var t = kv.Value;
                        if (t == null || t.stages == null) continue;
                        var ids = new List<string>();
                        string done = "";
                        foreach (var st in t.stages) { if (st == null) continue; ids.Add(st.stageID.ToString()); if (st.completesQuest) done += st.stageID + ","; }
                        Emit($"TASKDEF {kv.Key} stages={string.Join(",", ids)} completes={done.TrimEnd(',')}");
                        n++;
                    }
                    Emit($"TASKDB {n}");
                    break;
                }
                case "spawnpoints":
                {
                    foreach (var sp in Resources.FindObjectsOfTypeAll<global::DeathRespawnPoint>())
                    {
                        if (sp == null || !sp.gameObject.scene.IsValid()) continue;
                        var t = sp.spawnPointTransform != null ? sp.spawnPointTransform : sp.transform;
                        var p = t.position;
                        Emit($"SPAWN {sp.spawnPointName ?? sp.name}|{p.x:F1}|{p.y:F1}|{p.z:F1}|{sp.gameObject.scene.name}");
                    }
                    Emit("SPAWNPOINTS done");
                    break;
                }
                case "bosses":
                {
                    // bosses [minMaxHp]: every scene enemy (loaded, active or not) with a big health pool.
                    int min = parts.Length > 1 ? int.Parse(parts[1]) : 300;
                    var reg = ObjectRegistry.Instance;
                    foreach (var e in Resources.FindObjectsOfTypeAll<global::EnemyController>())
                    {
                        if (e == null || !e.gameObject.scene.IsValid()) continue;
                        int mh = 0, ch = 0; try { mh = e.myStats.maxHealth; ch = e.myStats.currentHealth; } catch { }
                        if (mh < min) continue;
                        var p = e.transform.position;
                        string spi = "";
                        global::EnemySpawner bsp = null;
                        for (var t = e.transform; t != null && bsp == null; t = t.parent) bsp = t.GetComponent<global::EnemySpawner>();
                        if (bsp != null)
                        {
                            try
                            {
                                var qo = bsp.GetComponent<global::QuestObject>();
                                spi = $" spawner={bsp.gameObject.activeInHierarchy}/act={bsp.spawnerActive}/init={bsp.hasBeenInitialized} respawn={bsp.canRespawn} days={bsp.maxDaysUntilRespawn} killData={bsp.savesKillData} fact='{bsp.setNormalFact}' id='{bsp.optionalEnemyID}'" +
                                      (qo != null ? $" quest='{qo.taskString}'@{qo.taskStage}" : "");
                            }
                            catch (Exception ex) { spi = " spawner=? " + ex.Message; }
                        }
                        Emit($"BOSS {e.gameObject.name} hp={ch}/{mh} active={e.gameObject.activeInHierarchy} dead={EnemySync.IsDead(e)} puppet={EnemySync.IsPuppet(e)} at {p.x:F1},{p.y:F1},{p.z:F1}{spi} key={EnemySync.KeyFor(reg, e)}");
                    }
                    Emit("BOSSES done");
                    break;
                }
                case "ending":
                {
                    // ending [trigger]: the Cradle's EndingsScene (the ending is a conversation).
                    bool fire = parts.Length > 1 && parts[1] == "trigger";
                    int n = 0;
                    foreach (var es in Resources.FindObjectsOfTypeAll<global::EndingsScene>())
                    {
                        if (es == null || !es.gameObject.scene.IsValid()) continue;
                        n++;
                        string conv = "-";
                        try { conv = es.conversationToTrigger != null ? es.conversationToTrigger.gameObject.name : "-"; } catch { }
                        Emit($"ENDING {ObjectRegistry.HierarchyPath(es.transform)} active={es.gameObject.activeInHierarchy} triggered={es.hasBeenTriggered} conversation={conv}");
                        if (fire)
                        {
                            try { es.TriggerEndings(); Emit("ENDING triggered"); }
                            catch (Exception e) { Emit("ERR ending: " + e.Message); }
                        }
                    }
                    if (n == 0) Emit("ENDING none loaded");
                    break;
                }
                case "buttons":
                {
                    // buttons: active UI buttons (names for "click").
                    var names = new List<string>();
                    foreach (var b in Resources.FindObjectsOfTypeAll<global::UnityEngine.UI.Button>())
                    {
                        try { if (b != null && b.gameObject.activeInHierarchy && b.interactable) names.Add(b.gameObject.name); } catch { }
                    }
                    Emit($"BUTTONS {names.Count}: {string.Join(" | ", names)}");
                    break;
                }
                case "mirrortop":
                {
                    foreach (var l in WorldMirror.TopSent(parts.Length > 1 ? int.Parse(parts[1]) : 8)) Emit("MTOP " + l);
                    Emit("MTOP done");
                    break;
                }
                case "god":
                {
                    // god on|off: rig only - the local player's health is refilled every frame.
                    _god = parts.Length < 2 || parts[1] != "off";
                    Emit($"GOD {_god}");
                    break;
                }
                case "comps":
                {
                    // comps <name substring> [max]: scene objects by name with activeSelf and components.
                    string want = parts.Length > 1 ? parts[1] : "";
                    int max = parts.Length > 2 ? int.Parse(parts[2]) : 8;
                    int seen = 0;
                    foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    {
                        if (seen >= max) break;
                        if (t == null || !t.gameObject.scene.IsValid()) continue;
                        if (t.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        seen++;
                        var cs = new List<string>();
                        foreach (var c in t.GetComponents<Component>())
                            if (c != null) cs.Add(c.GetIl2CppType().Name);
                        Emit($"COMP {ObjectRegistry.HierarchyPath(t)} self={t.gameObject.activeSelf} inHier={t.gameObject.activeInHierarchy} children={t.childCount} [{string.Join(",", cs)}]");
                    }
                    Emit("COMPS done");
                    break;
                }
                case "fot":
                {
                    // fot <name substring>: TriggerByFactOrTask gates (what turns story objects on).
                    string want = parts.Length > 1 ? parts[1] : "";
                    foreach (var t in Resources.FindObjectsOfTypeAll<global::TriggerByFactOrTask>())
                    {
                        if (t == null || !t.gameObject.scene.IsValid()) continue;
                        if (t.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string cond = "";
                        try
                        {
                            var f = t.factCnd; var k = t.taskCnd;
                            cond = $"type={t.typeOfTrigger} fact={(f != null ? $"{f.factType}:'{f.factToCheck}' true={f.mustBeTrue} >{f.activeIfGreaterInt}" : "-")} " +
                                   $"task={(k != null ? $"'{k.taskToCheck}' on>={k.activeOnAndAfterStage} before={k.activeBeforeStage} notStarted={k.activeIfTaskNotStarted} default={k.activeByDefault} stages=[{(k.stagesWhenActive != null ? string.Join(",", k.stagesWhenActive) : "")}] off=[{(k.stagesWhenInactive != null ? string.Join(",", k.stagesWhenInactive) : "")}]" : "-")} " +
                                   $"world={t.worldState} regionSwapOnly={t.onlyCheckOnRegionSwap}";
                        }
                        catch (Exception ex) { cond = "? " + ex.Message; }
                        Emit($"FOT {ObjectRegistry.HierarchyPath(t.transform)} isActive={t.isActive} {cond}");
                    }
                    Emit("FOTS done");
                    break;
                }
                case "encounters":
                {
                    // encounters [radius]: combat encounter zones near the player, their fact gate
                    // and the enemies they spawn.
                    float rad = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 150f;
                    var me = GameRefs.Player != null ? GameRefs.Player.transform.position : Vector3.zero;
                    foreach (var enc in Resources.FindObjectsOfTypeAll<global::EnemyCombatEncounter>())
                    {
                        if (enc == null || !enc.gameObject.scene.IsValid()) continue;
                        var col = enc.GetComponent<Collider>();
                        var ctr = col != null ? col.bounds.center : enc.transform.position;
                        float d = Vector3.Distance(ctr, me);
                        if (d > rad) continue;
                        var names = new List<string>();
                        try
                        {
                            var arr = enc.enemiesToSpawn;
                            if (arr != null)
                                foreach (var sp in arr)
                                    if (sp != null) names.Add(sp.gameObject.name + (sp.enemyObject != null && sp.enemyObject.activeInHierarchy ? "(on)" : "(off)"));
                        }
                        catch { }
                        bool inside = col != null && col.bounds.Contains(me);
                        Emit($"ENC {ObjectRegistry.HierarchyPath(enc.transform)} active={enc.gameObject.activeInHierarchy} at {ctr.x:F0},{ctr.y:F0},{ctr.z:F0} dist={d:F1} inside={inside} " +
                             $"size={(col != null ? col.bounds.size.ToString("F0") : "-")} fact='{enc.factMustBeTrue}'>={enc.intEqualOrHigher} neverDisables={enc.neverDisables} spawns=[{string.Join(",", names)}]");
                    }
                    Emit("ENCS done");
                    break;
                }
                case "spawnboss":
                {
                    // spawnboss <name substring>: bring a boss in as its encounter would - activate
                    // its spawner/hierarchy (respawning it if it was killed). Prints its position.
                    string want = parts.Length > 1 ? parts[1] : "";
                    global::EnemyController best = null; int bestHp = -1;
                    foreach (var e in Resources.FindObjectsOfTypeAll<global::EnemyController>())
                    {
                        if (e == null || !e.gameObject.scene.IsValid()) continue;
                        if (e.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        int mh = 0; try { mh = e.myStats != null ? e.myStats.maxHealth : 0; } catch { }
                        if (mh > bestHp) { bestHp = mh; best = e; }
                    }
                    if (best == null) { Emit("ERR no enemy " + want); break; }
                    global::EnemySpawner sp = null;
                    for (var t = best.transform; t != null && sp == null; t = t.parent) sp = t.GetComponent<global::EnemySpawner>();
                    for (var t = best.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    try { if (sp != null) sp.ActivateSpawner(true); } catch (Exception e) { Emit("spawner: " + e.Message); }
                    int hp = 0; try { hp = best.myStats.currentHealth; } catch { }
                    if (hp <= 0 && sp != null) { try { sp.RespawnEnemy(true, null); } catch { } }
                    var p = best.transform.position;
                    Emit($"SPAWNBOSS {best.gameObject.name} hp={(best.myStats != null ? best.myStats.currentHealth : -1)}/{bestHp} active={best.gameObject.activeInHierarchy} at {p.x:F1}|{p.y:F1}|{p.z:F1}");
                    break;
                }
                case "scenes":
                {
                    var names = new List<string>();
                    for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    {
                        var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        names.Add($"{sc.buildIndex}:{sc.name}{(sc.isLoaded ? "" : "(loading)")}");
                    }
                    Emit("SCENES " + string.Join(" ", names));
                    var rm = GameRefs.Regions;
                    if (rm != null && rm.regions != null)
                    {
                        var rs = new List<string>();
                        foreach (var r in rm.regions)
                            if (r != null) rs.Add($"{r.scene}:{r.state}{(r.needsLoading ? "+L" : "")}{(r.needsUnloading ? "-U" : "")}");
                        Emit("REGIONS allowLoad=" + rm.allowLoading + " " + string.Join(" ", rs));
                    }
                    break;
                }
                case "tphold":
                {
                    // tphold x y z seconds: teleport and hold the player still (no falling)
                    // while the destination region streams in.
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var dest = new Vector3(float.Parse(parts[1], ci), float.Parse(parts[2], ci), float.Parse(parts[3], ci));
                    float secs = parts.Length > 4 ? float.Parse(parts[4], ci) : 10f;
                    Teleport(dest);
                    var pm = GameRefs.Player?.GetComponent<global::PlayerMove>();
                    var cc = GameRefs.Player?.GetComponent<CharacterController>();
                    if (pm != null) pm.enabled = false;
                    if (cc != null) cc.enabled = false;
                    _holdUntil = Time.realtimeSinceStartup + secs;
                    _holdPos = dest;
                    Emit($"TPHOLD {dest} for {secs}s");
                    break;
                }
                case "scanscenes":
                {
                    int a = int.Parse(parts[1]), b = int.Parse(parts[2]);
                    for (int i = a; i <= b; i++) _scanQueue.Enqueue(i);
                    Emit($"SCANSCENES {a}..{b} queued");
                    break;
                }
                case "die":
                {
                    bool ok = GameRefs.PlayerActions?.StartPlayerDeath() ?? false;
                    Emit("DIE " + ok);
                    break;
                }
                case "npcgive":
                {
                    // npcgive <item> [amount] : the nearest dialogue NPC gives the player an item.
                    var me = GameRefs.Player;
                    global::NPCBase best = null; float bd = float.MaxValue;
                    foreach (var npc in Resources.FindObjectsOfTypeAll<global::NPCBase>())
                    {
                        if (npc == null || !npc.gameObject.activeInHierarchy || me == null) continue;
                        float d = Vector3.Distance(npc.transform.position, me.transform.position);
                        if (d < bd) { bd = d; best = npc; }
                    }
                    if (best == null) { Emit("ERR no npc"); break; }
                    int amt = parts.Length > 2 ? int.Parse(parts[2]) : 1;
                    best.GiveItem(parts[1], amt, 1);
                    Emit($"NPCGIVE {best.name} {parts[1]} x{amt} {DialogueSync.GiftStats}");
                    break;
                }
                case "inv":
                {
                    var inv = GameRefs.Inventory;
                    var it = inv?.GetItemFromInvAnyRarity(parts[1]);
                    Emit($"INV {parts[1]} has={(it != null)} {DialogueSync.GiftStats}");
                    break;
                }
                case "ship":
                {
                    var ship = AirshipSync.Ship;
                    if (ship == null) { Emit("SHIP none " + AirshipSync.Stats); break; }
                    var sp = ship.transform.position;
                    bool anch = false; try { anch = ship.anchor != null && ship.anchor.isAnchored; } catch { }
                    var me = GameRefs.Player;
                    string rel = me != null ? $" me-local={ship.transform.InverseTransformPoint(me.transform.position)} parented={me.transform.IsChildOf(ship.transform)}" : "";
                    Emit($"SHIP active={ship.gameObject.activeInHierarchy} state={ship.state} pos={sp.x:F1},{sp.y:F1},{sp.z:F1} yaw={ship.transform.eulerAngles.y:F0} dock={ship.data?.dockId} anchored={anch}{rel} {AirshipSync.Stats}");
                    break;
                }
                case "showship":
                {
                    var ship = AirshipSync.Ship;
                    if (ship == null) { Emit("ERR no ship"); break; }
                    for (var t = ship.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    Emit("SHOWSHIP active=" + ship.gameObject.activeInHierarchy);
                    break;
                }
                case "giveitem":
                {
                    var inv = GameRefs.Inventory;
                    if (inv == null || parts.Length < 2) { Emit("ERR giveitem <id>"); break; }
                    var it = inv.CreateItem(parts[1], global::ItemRarity.Common, 1);
                    bool ok = it != null && inv.StoreItem(it, global::ItemPopupMode.PopupIfNew, 1, false);
                    Emit($"GIVEITEM {parts[1]} ok={ok}");
                    break;
                }
                case "boardship":
                {
                    // Stand on the deck (next to the wheel).
                    var ship = AirshipSync.Ship;
                    var wheel = ship != null ? ship.GetComponentInChildren<global::AirshipWheel>(true) : null;
                    if (wheel == null) { Emit("ERR no wheel"); break; }
                    float off = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1.5f;
                    Teleport(wheel.transform.position + ship.transform.right * off + Vector3.up * 0.6f);
                    Emit("BOARDSHIP " + wheel.transform.position);
                    break;
                }
                case "pilot":
                {
                    var ship = AirshipSync.Ship;
                    var wheel = ship != null ? ship.GetComponentInChildren<global::AirshipWheel>(true) : null;
                    if (wheel == null) { Emit("ERR no wheel"); break; }
                    wheel.PlayerUse();
                    Emit($"PILOT requested state={ship.state} {AirshipSync.Stats}");
                    break;
                }
                case "shipmove":
                {
                    // Test flight without input: shove the flying ship (pilot side only).
                    var ship = AirshipSync.Ship;
                    if (ship == null || parts.Length < 4) { Emit("ERR shipmove dx dy dz [yaw]"); break; }
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var d = new Vector3(float.Parse(parts[1], ci), float.Parse(parts[2], ci), float.Parse(parts[3], ci));
                    float yaw = parts.Length > 4 ? float.Parse(parts[4], ci) : 0f;
                    var body = ship.body;
                    if (body != null) { body.position = body.position + d; body.rotation = body.rotation * Quaternion.Euler(0, yaw, 0); }
                    ship.transform.position += d;
                    if (yaw != 0) ship.transform.rotation = ship.transform.rotation * Quaternion.Euler(0, yaw, 0);
                    Emit("SHIPMOVE " + ship.transform.position);
                    break;
                }
                case "land":
                {
                    var ship = AirshipSync.Ship;
                    if (ship == null) { Emit("ERR no ship"); break; }
                    ship.StopUsing(true);
                    Emit($"LAND state={ship.state}");
                    break;
                }
                case "npcs":
                {
                    // Nearest dialogue NPCs: key, tree, distance.
                    int n = parts.Length > 1 ? int.Parse(parts[1]) : 8;
                    string want = parts.Length > 2 ? parts[2] : null;
                    var me = GameRefs.Player;
                    var all = Resources.FindObjectsOfTypeAll<global::NPCBase>();
                    var list = new List<(float d, global::NPCBase npc)>();
                    if (me != null && all != null)
                        foreach (var npc in all)
                        {
                            if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                            if (npc.dialogueTree == null) continue;
                            if (want != null && npc.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0
                                && npc.dialogueTree.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            list.Add((Vector3.Distance(npc.transform.position, me.transform.position), npc));
                        }
                    list.Sort((x, y) => x.d.CompareTo(y.d));
                    for (int k = 0; k < list.Count && k < n; k++)
                    {
                        var (d, npc) = list[k];
                        var pp = npc.transform.position;
                        Emit($"NPC {npc.name} tree={npc.dialogueTree.name} tag={npc.NPCTag} d={d:F1} pos={pp.x:F1},{pp.y:F1},{pp.z:F1}");
                    }
                    break;
                }
                case "talk":
                {
                    // talk <name-or-tree substring>: walk up to the nearest match and start talking.
                    string want = parts.Length > 1 ? parts[1] : "";
                    var me = GameRefs.Player;
                    var all = Resources.FindObjectsOfTypeAll<global::NPCBase>();
                    global::NPCBase best = null; float bd = float.MaxValue;
                    if (me != null && all != null)
                        foreach (var npc in all)
                        {
                            if (npc == null || !npc.gameObject.activeInHierarchy || npc.dialogueTree == null) continue;
                            if (npc.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0
                                && npc.dialogueTree.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            float d = Vector3.Distance(npc.transform.position, me.transform.position);
                            if (d < bd) { bd = d; best = npc; }
                        }
                    if (best == null) { Emit("ERR no such npc"); break; }
                    if (bd > 3f)
                    {
                        var np = best.transform.position;
                        var fwd = best.transform.forward; fwd.y = 0;
                        Teleport(np + (fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward) * 2f + Vector3.up * 0.3f);
                    }
                    bool ok = best.StartTalking();
                    Emit($"TALK {best.name} tree={best.dialogueTree.name} started={ok}");
                    break;
                }
                case "dlg":
                {
                    var c = global::DelusionDialogueNEW.Instance?.DialogueController;
                    Emit($"DLG {DialogueSync.Stats} state={GameRefs.Manager?.CurrentGameState}");
                    if (c != null && c.currentNode != null)
                    {
                        Emit($"  NODE {DialogueSync.NodeIndex(c.tree, c.currentNode)} name={c.GetNPCName()} text={c.GetCurrentNodeText()}");
                        var ch = c.GetCurrentChoicesText();
                        if (ch != null) for (int k = 0; k < ch.Count; k++) Emit($"  CHOICE {k}: {ch[k]}");
                    }
                    Emit($"  CUTSCENES {CutsceneSync.Stats}");
                    break;
                }
                case "choose":
                {
                    var c = global::DelusionDialogueNEW.Instance?.DialogueController;
                    if (c == null || c.currentNode == null) { Emit("ERR not talking"); break; }
                    int k = int.Parse(parts[1]);
                    c.TransitionToTreeChoice(k);
                    Emit($"CHOSE {k}");
                    break;
                }
                case "next":
                {
                    // Same as pressing Confirm: finish the typing, or advance.
                    var c = global::DelusionDialogueNEW.Instance?.DialogueController;
                    if (c == null || c.currentNode == null) { Emit("ERR not talking"); break; }
                    c.SkipManually();
                    Emit("NEXT");
                    break;
                }
                case "enddlg":
                {
                    global::DelusionDialogueNEW.Instance?.DialogueController?.EndDialogue();
                    Emit("ENDDLG");
                    break;
                }
                case "worldevents":
                case "worldstate":
                case "rest":
                case "clock":
                case "travel":
                case "trignear":
                case "trapnear":
                case "smashnear":
                case "respawnnear":
                case "liftup":
                case "liftdown":
                {
                    // World events (WorldEventsSync): worldstate [n], rest <hour>, clock,
                    // travel underlands|cradle|return, trignear/trapnear/smashnear/respawnnear
                    // [name filter], liftup|liftdown [id substring], worldevents (stats).
                    foreach (var l in WorldEventsSync.DebugCmd(cmd, parts)) Emit(l);
                    break;
                }
                case "tasks":
                {
                    var list = GameRefs.Tasks?.GetPersistentTaskData()?.list;
                    int n = list?.Count ?? 0;
                    Emit($"TASKS n={n} {QuestSync.Stats}");
                    if (list != null)
                        foreach (var e in list) Emit($"  TASK {e.id} stage={e.stage}");
                    break;
                }
                case "taskstage":
                {
                    if (parts.Length < 3) { Emit("ERR taskstage <id> <stage>"); break; }
                    bool ok = GameRefs.Tasks?.TaskStageTrigger(parts[1], int.Parse(parts[2]), false) ?? false;
                    Emit($"TASKSTAGE {parts[1]} {parts[2]} advanced={ok}");
                    break;
                }
                case "nullcaps":
                {
                    // Which saveable component types capture no state here (the generic
                    // mirror cannot drive those on this peer).
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    var nulls = new Dictionary<string, int>();
                    var ok = new Dictionary<string, int>();
                    if (all != null)
                        foreach (var so in all)
                        {
                            if (so == null) continue;
                            string id = null;
                            try { id = so.GetUniqueIdentifier(); } catch { }
                            if (string.IsNullOrEmpty(id)) continue;
                            string kind = "?";
                            try
                            {
                                foreach (var c in so.GetComponents<MonoBehaviour>())
                                {
                                    if (c == null) continue;
                                    var n = c.GetIl2CppType().Name;
                                    if (n == "SaveableObject") continue;
                                    kind = n; break;
                                }
                            }
                            catch { }
                            object st = null;
                            try { st = so.CaptureState(); } catch { }
                            var d = st == null ? nulls : ok;
                            d[kind] = d.TryGetValue(kind, out var v) ? v + 1 : 1;
                        }
                    foreach (var kv in nulls) Emit($"NULLCAP {kv.Key} null={kv.Value} ok={(ok.TryGetValue(kv.Key, out var o) ? o : 0)}");
                    int okTotal = 0; foreach (var v in ok.Values) okTotal += v;
                    Emit($"NULLCAPS types={nulls.Count} okTotal={okTotal}");
                    break;
                }
                case "statedump":
                {
                    // Every named saveable's encoded state, one line each, for diffing peers.
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    var lines = new List<string>();
                    if (all != null)
                        foreach (var so in all)
                        {
                            if (so == null) continue;
                            string id = null;
                            try { id = so.GetUniqueIdentifier(); } catch { }
                            if (string.IsNullOrEmpty(id)) continue;
                            string d;
                            try { d = StateCodec.Describe(StateCodec.Encode(so.CaptureState()), 1200); }
                            catch (Exception e) { d = "ERR " + e.Message; }
                            lines.Add($"{id}	{so.gameObject.name}	{d}");
                        }
                    lines.Sort(StringComparer.Ordinal);
                    var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_states.txt");
                    File.WriteAllLines(path, lines);
                    Emit($"STATEDUMP {lines.Count} -> {path}");
                    break;
                }
                case "dumpstate":
                {
                    if (parts.Length < 2) { Emit("ERR dumpstate <id substring>"); break; }
                    var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
                    int shown = 0;
                    if (all != null)
                        foreach (var so in all)
                        {
                            if (so == null || shown >= 3) continue;
                            string id = null;
                            try { id = so.GetUniqueIdentifier(); } catch { }
                            if (string.IsNullOrEmpty(id) || id.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                            object st = null;
                            try { st = so.CaptureState(); } catch { }
                            Emit($"STATE {id} ({so.gameObject.name}) = {StateCodec.Describe(StateCodec.Encode(st), 1500)}");
                            shown++;
                        }
                    if (shown == 0) Emit("STATE none");
                    break;
                }
                case "pickups":
                case "pickup":
                case "chests":
                case "chest":
                {
                    // List/loot the nearest world items through the game's own code paths.
                    bool chests = cmd.StartsWith("chest");
                    bool act = cmd == "pickup" || cmd == "chest";
                    string want = parts.Length > 1 ? parts[1] : null;
                    var me = GameRefs.Player;
                    if (me == null) { Emit("ERR no player"); break; }
                    var rows = new List<(float d, MonoBehaviour c, string id)>();
                    var src = chests
                        ? (IEnumerable<MonoBehaviour>)ToList<global::ItemChest>(Resources.FindObjectsOfTypeAll<global::ItemChest>())
                        : ToList<global::ItemPickup>(Resources.FindObjectsOfTypeAll<global::ItemPickup>());
                    foreach (var c in src)
                    {
                        if (c == null || !c.gameObject.activeInHierarchy) continue;
                        var so = c.GetComponentInParent<global::SaveableObject>();
                        string id = null;
                        try { id = so?.GetUniqueIdentifier(); } catch { }
                        if (string.IsNullOrEmpty(id)) continue;
                        if (want != null && id.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        rows.Add((Vector3.Distance(c.transform.position, me.transform.position), c, id));
                    }
                    rows.Sort((a, b) => a.d.CompareTo(b.d));
                    int shown = 0;
                    foreach (var (d, c, id) in rows)
                    {
                        if (shown++ >= (act ? 1 : 6)) break;
                        var so = c.GetComponentInParent<global::SaveableObject>();
                        object st = null; try { st = so.CaptureState(); } catch { }
                        if (act)
                        {
                            if (chests) c.Cast<global::ItemChest>().PlayerUse();
                            else c.Cast<global::ItemPickup>().StoreItem();
                            Emit($"LOOT {id} ({c.gameObject.name}) d={d:F1}");
                        }
                        else
                        {
                            Emit($"ITEM {id} ({c.gameObject.name}) d={d:F1} " +
                                 $"pos={c.transform.position.x:F1},{c.transform.position.y:F1},{c.transform.position.z:F1} " +
                                 $"state={StateCodec.Describe(StateCodec.Encode(st), 300)}");
                        }
                    }
                    if (shown == 0) Emit("ITEM none");
                    break;
                }
                case "lifts":
                case "lift":
                {
                    // lifts            : nearest lifts with state
                    // lift <id> top|bot: recall it the way a player at that end would
                    var me = GameRefs.Player;
                    var all = Resources.FindObjectsOfTypeAll<global::DelusionLift>();
                    var rows = new List<(float d, global::DelusionLift l, string id)>();
                    if (all != null && me != null)
                        foreach (var l in all)
                        {
                            if (l == null || !l.gameObject.activeInHierarchy) continue;
                            string id = null;
                            try { id = l.GetComponent<global::SaveableObject>()?.GetUniqueIdentifier(); } catch { }
                            if (string.IsNullOrEmpty(id)) continue;
                            if (cmd == "lift" && parts.Length > 1 && id.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                            rows.Add((Vector3.Distance(l.transform.position, me.transform.position), l, id));
                        }
                    rows.Sort((a, b) => a.d.CompareTo(b.d));
                    if (cmd == "lift")
                    {
                        if (rows.Count == 0) { Emit("ERR no lift"); break; }
                        var l = rows[0].l;
                        bool ok = parts.Length > 2 && parts[2] == "bot" ? l.TryRecallBot() : l.TryRecallTop();
                        Emit($"LIFT {rows[0].id} recall ok={ok} pos={l.liftPosition}");
                        break;
                    }
                    for (int i = 0; i < rows.Count && i < 6; i++)
                    {
                        var (d, l, id) = rows[i];
                        var p = l.transform.position;
                        string car = "-";
                        try
                        {
                            var cp = l.liftCarriage != null ? l.liftCarriage.transform.position : Vector3.zero;
                            var tp = l.topPos != null ? l.topPos.position : Vector3.zero;
                            var bp = l.botPos != null ? l.botPos.position : Vector3.zero;
                            car = $"{cp.x:F1},{cp.y:F1},{cp.z:F1} top={tp.y:F0} bot={bp.y:F0}";
                        }
                        catch { }
                        Emit($"LIFTSTATE {id} pos={l.liftPosition} moving={l.isMoving} d={d:F0} at={p.x:F1},{p.y:F1},{p.z:F1} carriage={car}");
                    }
                    break;
                }
                case "syncstats":
                {
                    Emit($"SYNC mirror sent={WorldMirror.SentCount} requests={WorldMirror.RequestCount} " +
                         $"applied={WorldMirror.AppliedCount} rejected={WorldMirror.RejectedCount} " +
                         $"volatile={WorldMirror.VolatileCount} facts-forwarded={FactSync.ForwardedCount} " +
                         $"{WorldMirror.PendingInfo} quests: {QuestSync.Stats} enemies: {EnemySync.Stats} dialogue: {DialogueSync.Stats} cutscenes: {CutsceneSync.Stats} airship: {AirshipSync.Stats}");
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
                            var dp = d.transform.position;
                            Emit($"DOOR {id} st8={d.GetSt8()} pos={dp.x:F1},{dp.y:F1},{dp.z:F1} " +
                                 $"d={(GameRefs.Player != null ? Vector3.Distance(dp, GameRefs.Player.transform.position) : -1):F0}");
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
                                // The player's interact: unlock-and-open a locked door,
                                // toggle an unlocked one.
                                if (d.GetSt8() == global::DoorState.locked) d.UnlockDoor(true, false);
                                else d.UseDoor();
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

        private static List<MonoBehaviour> ToList<T>(IEnumerable<T> arr) where T : MonoBehaviour
        {
            var l = new List<MonoBehaviour>();
            if (arr != null) foreach (var a in arr) l.Add(a);
            return l;
        }

        private static void Teleport(Vector3 pos)
        {
            var p = GameRefs.Player;
            if (p == null) { Emit("ERR no player"); return; }
            var cc = p.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;   // a live controller snaps the transform back
            p.transform.position = pos;
            if (cc != null) cc.enabled = true;
            Emit($"TP {pos.x:F1},{pos.y:F1},{pos.z:F1}");
        }

        private static void FacePeer()
        {
            if (!PlayerSync.TryPuppetHead(out var head)) { Emit("ERR no puppet"); return; }
            LookAt(head);
        }

        private static void LookAt(Vector3 head)
        {
            var p = GameRefs.Player;
            if (p == null) { Emit("ERR no player"); return; }
            var cam = GameRefs.PlayerCamera;
            var eye = cam != null ? cam.transform.position : p.transform.position + Vector3.up * 1.6f;
            var d = head - eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            p.transform.rotation = Quaternion.Euler(0, yaw, 0);
            try
            {
                var look = GameRefs.Manager?.playerLook;
                look?.LookSetValues(yaw, pitch);
            }
            catch (Exception e) { Emit("WARN LookSetValues: " + e.Message); }
            Emit($"FACE yaw={yaw:F0} pitch={pitch:F0} dist={d.magnitude:F1}");
        }

        private static void DumpHier(Transform t, int depth)
        {
            if (depth > 6) return;
            var comps = t.GetComponents<Component>();
            var names = new List<string>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var n = c.GetIl2CppType().Name;
                var r = c.TryCast<Renderer>();
                if (r != null) n += r.enabled ? "+" : "-";
                names.Add(n);
            }
            Emit($"HIER {new string(' ', depth * 2)}{t.name} active={t.gameObject.activeSelf} layer={t.gameObject.layer} [{string.Join(",", names)}]");
            for (int i = 0; i < t.childCount; i++) DumpHier(t.GetChild(i), depth + 1);
        }
    }
}
