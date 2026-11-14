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
                        Emit($"LIFTSTATE {id} pos={l.liftPosition} moving={Priv.GetField<bool>(l, "isMoving")} d={d:F0} at={p.x:F1},{p.y:F1},{p.z:F1}");
                    }
                    break;
                }
                case "syncstats":
                {
                    Emit($"SYNC mirror sent={WorldMirror.SentCount} requests={WorldMirror.RequestCount} " +
                         $"applied={WorldMirror.AppliedCount} rejected={WorldMirror.RejectedCount} " +
                         $"volatile={WorldMirror.VolatileCount} facts-forwarded={FactSync.ForwardedCount} " +
                         $"quests: {QuestSync.Stats} enemies: {EnemySync.Stats}");
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
                var look = Priv.GetField<global::PlayerLook>(GameRefs.Manager, "playerLook");
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
