using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Quests and the task journal.
    ///
    /// The journal is driven through one entry point:
    ///   DelusionTasks.TaskStageTrigger(string taskID, int stageID, bool hasPopup) @ 0x415E90
    /// It returns bool - true when the stage actually advanced - which makes it a clean
    /// postfix hook.
    ///
    /// Either player can advance a quest. A stage advanced on one side is replayed on the
    /// other through the game's own TaskStageTrigger, so each player's journal, popups and
    /// rewards follow (inventories are per player, so each gets the reward once - the
    /// replay runs inside LoopGuard and is never forwarded again). If the replay refuses
    /// (the peer's preconditions differ), the journal entry is restored directly from
    /// { id, stage } so the log still matches.
    ///
    /// Client-originated stages go to the host first as a request; the host runs the
    /// trigger, which makes it canonical and re-broadcasts it.
    /// </summary>
    public static class QuestSync
    {
        private const byte KindStage = 0;
        private const byte KindQuestStart = 1;
        private const byte KindQuestFinish = 2;
        private const byte KindJournalSnapshot = 3;
        private const byte KindClientStage = 4;
        private const byte KindClientQuestStart = 5;

        private static int _replayed, _restored, _forwarded;
        public static string Stats => $"replayed={_replayed} restored={_restored} forwarded={_forwarded}";

        private static bool ClientConnected()
        {
            var s = Session.Instance;
            return s != null && s.Role == SessionRole.Client && s.Connected;
        }

        public static void Init() { }

        /// <summary>The interop constructor does not run the field initialiser, so the
        /// backing list is null until we give it one.</summary>
        private static DelusionTasks.PersistentTaskDataList NewTaskList()
        {
            var l = new DelusionTasks.PersistentTaskDataList();
            if (l.list == null) l.list = new Il2CppSystem.Collections.Generic.List<DelusionTasks.PersistentTaskData>();
            return l;
        }

        internal static void OnTaskStageTrigger(string taskID, int stageID, bool hasPopup, bool result)
        {
            if (!result) return;                        // the stage did not advance
            if (LoopGuard.ApplyingRemote) return;
            var w = new Writer();
            if (ClientConnected())
            {
                w.U8(KindClientStage).Str(taskID).I32(stageID).Bool(hasPopup);
                Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
                _forwarded++;
                return;
            }
            if (Session.Instance?.Role != SessionRole.Host) return;
            w.U8(KindStage).Str(taskID).I32(stageID).Bool(hasPopup);
            Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
        }

        internal static void OnQuestStarted(string questID)
        {
            if (LoopGuard.ApplyingRemote) return;
            var w = new Writer();
            if (ClientConnected())
            {
                w.U8(KindClientQuestStart).Str(questID);
                Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
                _forwarded++;
                return;
            }
            if (Session.Instance?.Role != SessionRole.Host) return;
            w.U8(KindQuestStart).Str(questID);
            Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
        }

        internal static void OnQuestFinished(string questID)
        {
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;
            var w = new Writer();
            w.U8(KindQuestFinish).Str(questID);
            Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            switch (kind)
            {
                case KindStage:
                {
                    string taskID = r.Str();
                    int stage = r.I32();
                    bool popup = r.Bool();
                    ReplayStage(taskID, stage, popup);
                    break;
                }
                case KindClientStage:
                {
                    // Host: the client advanced a quest. Run the real trigger here (outside
                    // LoopGuard) so the host's journal/facts/rewards follow and the postfix
                    // re-broadcasts it as canonical.
                    string taskID = r.Str();
                    int stage = r.I32();
                    bool popup = r.Bool();
                    if (Session.Instance?.Role != SessionRole.Host) break;
                    bool ok = false;
                    try { ok = GameRefs.Tasks?.TaskStageTrigger(taskID, stage, popup) ?? false; }
                    catch (Exception e) { Log.Warn($"client stage {taskID}/{stage} failed: {e.Message}"); }
                    if (!ok) ApplyStage(taskID, stage, popup);
                    Log.Info($"client advanced {taskID} -> {stage} (trigger={ok})");
                    break;
                }
                case KindClientQuestStart:
                {
                    string id = r.Str();
                    if (Session.Instance?.Role != SessionRole.Host) break;
                    try { GameRefs.Quest?.ActivateQuest(id); }
                    catch (Exception e) { Log.Warn($"client ActivateQuest({id}) failed: {e.Message}"); }
                    break;
                }
                case KindQuestStart:
                {
                    string id = r.Str();
                    LoopGuard.Apply(() =>
                    {
                        try { GameRefs.Quest?.ActivateQuest(id); }
                        catch (Exception e) { Log.Warn($"ActivateQuest({id}) failed: {e.Message}"); }
                    });
                    break;
                }
                case KindQuestFinish:
                {
                    string id = r.Str();
                    LoopGuard.Apply(() =>
                    {
                        try { GameRefs.Quest?.NotifyQuestFinished(id); }
                        catch (Exception e) { Log.Warn($"NotifyQuestFinished({id}) failed: {e.Message}"); }
                    });
                    break;
                }
                case KindJournalSnapshot:
                    ApplyJournal(payload, 1);
                    break;
                default:
                    Log.Warn($"Unknown QuestState kind {kind}");
                    break;
            }
        }

        /// <summary>Peer advanced a stage: replay it through the game, fall back to the
        /// journal data layer if the trigger refuses.</summary>
        private static void ReplayStage(string taskID, int stage, bool popup)
        {
            var tasks = GameRefs.Tasks;
            if (tasks == null) return;
            bool ok = LoopGuard.Apply(() =>
            {
                try { return tasks.TaskStageTrigger(taskID, stage, popup); }
                catch (Exception e) { Log.Warn($"replay {taskID}/{stage} failed: {e.Message}"); return false; }
            });
            if (ok) { _replayed++; Log.Info($"quest {taskID} -> {stage} (replayed)"); return; }
            ApplyStage(taskID, stage, popup);
            _restored++;
        }

        /// <summary>
        /// Brings one journal entry to the peer's stage without the reward pipeline being
        /// re-entered from the network: replay TaskStageTrigger (inside LoopGuard) for the
        /// target stage. RestoreFromPersistentTaskData is deliberately not used - replacing
        /// the live task list faults natively while a save is loading.
        /// </summary>
        private static void ApplyStage(string taskID, int stage, bool popup)
        {
            if (!GameRefs.InWorld) { _pendingJournal[taskID] = stage; return; }
            var tasks = GameRefs.Tasks;
            if (tasks == null) return;
            int local = LocalStage(tasks, taskID);
            if (local == stage) return;
            bool ok = LoopGuard.Apply(() =>
            {
                try { return tasks.TaskStageTrigger(taskID, stage, false); }
                catch (Exception e) { Log.Warn($"journal {taskID}->{stage} failed: {e.Message}"); return false; }
            });
            if (!ok && local < 0)
            {
                // Not in this journal and the stage cannot start a task: add the entry the
                // way a save load does.
                try
                {
                    tasks.RestoreTask(new DelusionTasks.PersistentTaskData { id = taskID, stage = stage });
                    tasks.RefreshTasksFromDataCore();
                    LoopGuard.Apply(() => tasks.OnTaskUpdated?.Invoke());
                    ok = LocalStage(tasks, taskID) == stage;
                    Log.Info($"journal {taskID}: added at {stage} ({(ok ? "ok" : "failed")})");
                    return;
                }
                catch (Exception e) { Log.Warn($"journal add {taskID}@{stage}: {e.Message}"); }
            }
            Log.Info($"journal {taskID}: {local} -> {stage} ({(ok ? "ok" : "refused")})");
        }

        private static readonly Dictionary<string, int> _pendingJournal = new Dictionary<string, int>();

        /// <summary>Apply journal entries that arrived while the world was loading.</summary>
        private static Dictionary<string, int> _journalTruth;

        private static void TrimToHost()
        {
            var truth = _journalTruth;
            _journalTruth = null;
            var tasks = GameRefs.Tasks;
            // The live journal (GetPersistentTaskData returns a copy).
            var list = tasks?.playerTasks?.taskDataList;
            if (list == null) return;
            int removed = 0, set = 0, added = 0;
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t == null) continue;
                present.Add(t.taskID);
                if (!truth.TryGetValue(t.taskID, out int want)) { list.RemoveAt(i); removed++; continue; }
                int cur = t.activeStage != null ? t.activeStage.stageID : -1;
                if (cur == want) continue;
                try
                {
                    var st = tasks.GetStage(t, want);
                    if (st != null) { t.activeStage = st; t.isComplete = st.completesQuest; set++; }
                }
                catch (Exception e) { Log.Debug($"journal set {t.taskID}: {e.Message}"); }
            }
            // Quests the host has and this side never started: the trigger refuses a stage
            // that cannot start a task, so the entry is rebuilt the way a save load does.
            foreach (var kv in truth)
            {
                if (present.Contains(kv.Key)) continue;
                try
                {
                    var item = new DelusionTasks.PersistentTaskData { id = kv.Key, stage = kv.Value };
                    tasks.RestoreTask(item);
                    added++;
                }
                catch (Exception e) { Log.Warn($"journal add {kv.Key}@{kv.Value}: {e.Message}"); }
            }
            if (removed + set + added == 0) return;
            try { tasks.RefreshTasksFromDataCore(); } catch { }
            try { LoopGuard.Apply(() => tasks.OnTaskUpdated?.Invoke()); } catch { }
            Log.Info($"journal: matched to the host ({removed} entr(ies) removed, {set} stage(s) set, {added} added)");
        }

        public static void Tick()
        {
            if (_journalTruth != null && _pendingJournal.Count == 0 && GameRefs.InWorld) TrimToHost();
            if (_pendingJournal.Count == 0 || !GameRefs.InWorld) return;
            var copy = new List<KeyValuePair<string, int>>(_pendingJournal);
            _pendingJournal.Clear();
            foreach (var kv in copy) ApplyStage(kv.Key, kv.Value, false);
        }

        private static int LocalStage(global::DelusionTasks tasks, string taskID)
        {
            try
            {
                var list = tasks.GetPersistentTaskData()?.list;
                if (list != null) foreach (var e in list) if (e.id == taskID) return e.stage;
            }
            catch { }
            return -1;
        }

        /// <summary>Full journal state, used at handshake and to repair drift.</summary>
        public static byte[] CaptureJournal()
        {
            var w = new Writer();
            w.U8(KindJournalSnapshot);
            var tasks = GameRefs.Tasks;
            var list = tasks?.GetPersistentTaskData()?.list;
            int n = list?.Count ?? 0;
            w.U16((ushort)Math.Min(n, ushort.MaxValue));
            if (list != null)
            {
                foreach (var e in list)
                {
                    w.Str(e.id).I32(e.stage);   // sent fragmented: no size cap needed
                }
            }
            return w.ToArray();
        }

        private static void ApplyJournal(byte[] payload, int offset)
        {
            var r = new Reader(payload);
            r.U8(); // kind
            int n = r.U16();
            var truth = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(); int stage = r.I32();
                truth[id] = stage;
                ApplyStage(id, stage, false);
            }
            // After the replays: entries only this side has, and stages the trigger could
            // not move (backwards), are brought to the host's journal.
            if (Session.Instance?.Role == SessionRole.Client) _journalTruth = truth;
            Log.Info($"journal snapshot: {n} tasks ({_pendingJournal.Count} deferred until in world)");
        }
    }

    [HarmonyPatch(typeof(global::DelusionTasks), nameof(global::DelusionTasks.TaskStageTrigger))]
    internal static class Patch_DelusionTasks_TaskStageTrigger
    {
        [HarmonyPostfix]
        static void Postfix(string newTaskID, int newStageID, bool hasPopup, bool __result)
            => QuestSync.OnTaskStageTrigger(newTaskID, newStageID, hasPopup, __result);
    }

    [HarmonyPatch(typeof(global::DelusionQuest), nameof(global::DelusionQuest.ActivateQuest))]
    internal static class Patch_DelusionQuest_ActivateQuest
    {
        [HarmonyPostfix]
        static void Postfix(string ID) => QuestSync.OnQuestStarted(ID);
    }
}
