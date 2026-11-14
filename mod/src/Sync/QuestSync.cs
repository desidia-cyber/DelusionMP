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
    /// CRITICAL: do NOT replay the task pipeline on the peer. TaskStageTrigger fans out
    /// into StartTask -> SetTaskFacts, CheckKeyItems, StoreItem, RemoveItem,
    /// CalculateItemsAndRewards, ChangeFactions. Re-running it would grant glimmers,
    /// coins and items to both players. The client instead applies the facts (they arrive
    /// through FactSync) and restores the journal from the host's PersistentTaskData,
    /// which is exactly { string id; int stage; }.
    /// </summary>
    public static class QuestSync
    {
        private const byte KindStage = 0;
        private const byte KindQuestStart = 1;
        private const byte KindQuestFinish = 2;
        private const byte KindJournalSnapshot = 3;

        public static void Init() { }

        internal static void OnTaskStageTrigger(string taskID, int stageID, bool hasPopup, bool result)
        {
            if (!result) return;                        // the stage did not advance
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;

            var w = new Writer();
            w.U8(KindStage).Str(taskID).I32(stageID).Bool(hasPopup);
            Session.Instance.SendReliable(PacketKind.QuestState, w.ToArray());
        }

        internal static void OnQuestStarted(string questID)
        {
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;
            var w = new Writer();
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
                    ApplyStage(taskID, stage, popup);
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

        /// <summary>
        /// Advances the local journal WITHOUT running reward code.
        ///
        /// The stage's own side effects are already arriving as facts. What we need here is
        /// only the journal entry, so the player's quest log matches. We therefore call the
        /// journal data layer directly and skip TaskStageTrigger.
        /// </summary>
        private static void ApplyStage(string taskID, int stage, bool popup)
        {
            var tasks = GameRefs.Tasks;
            if (tasks == null) return;
            LoopGuard.Apply(() =>
            {
                try
                {
                    var data = tasks.GetPersistentTaskData();
                    if (data == null) return;
                    var merged = new DelusionTasks.PersistentTaskDataList();
                    bool found = false;
                    // Preserve every other entry; override only this task's stage.
                    if (data.list != null)
                    {
                        foreach (var entry in data.list)
                        {
                            merged.list.Add(entry.id == taskID
                                ? new DelusionTasks.PersistentTaskData { id = taskID, stage = stage }
                                : entry);
                            if (entry.id == taskID) found = true;
                        }
                    }
                    if (!found)
                        merged.list.Add(new DelusionTasks.PersistentTaskData { id = taskID, stage = stage });

                    if (merged.list == null || merged.list.Count == 0) return;
                    tasks.RestoreFromPersistentTaskData(merged);
                    Log.Debug($"journal {taskID} -> stage {stage}");
                }
                catch (Exception e) { Log.Warn($"ApplyStage({taskID},{stage}) failed: {e.Message}"); }
            });
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
                    w.Str(e.id).I32(e.stage);
                    if (w.Length > Proto.MaxPayload - 64) break;
                }
            }
            return w.ToArray();
        }

        private static void ApplyJournal(byte[] payload, int offset)
        {
            var r = new Reader(payload);
            r.U8(); // kind
            var tasks = GameRefs.Tasks;
            if (tasks == null) return;
            int n = r.U16();
            if (n == 0) return;   // an empty journal is the main-menu state; restoring it
                                  // replaces the live list and makes the game NRE
            var entries = new List<DelusionTasks.PersistentTaskData>(n);
            for (int i = 0; i < n; i++)
                entries.Add(new DelusionTasks.PersistentTaskData { id = r.Str(), stage = r.I32() });

            LoopGuard.Apply(() =>
            {
                try
                {
                    var merged = new DelusionTasks.PersistentTaskDataList();
                    if (merged.list == null) { Log.Warn("journal: data list has no backing list"); return; }
                    foreach (var e in entries) merged.list.Add(e);
                    tasks.RestoreFromPersistentTaskData(merged);
                    Log.Info($"journal snapshot applied: {entries.Count} tasks");
                }
                catch (Exception ex) { Log.Warn($"RestoreFromPersistentTaskData failed: {ex.Message}"); }
            });
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
