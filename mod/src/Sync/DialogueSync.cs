using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Shared conversations. When one player (the driver) talks to an NPC, the partner
    /// (the spectator) is pulled into the same conversation with the game's own dialogue
    /// controller: same panel, NPC name, voice lines, poses and story-art cards, SPEECH
    /// control lock and camera turn. The driver alone advances it; every node change, choice,
    /// charm roll and the end are streamed, so the spectator never evaluates branches or
    /// randomness itself and both see exactly the same lines.
    ///
    /// Node identity: xNode regenerates node GUIDs per process, so nodes are keyed by
    /// (tree asset name, index in tree.nodes), which is serialized and identical in both.
    ///
    /// Side effects stay with the driver. Node actions on the spectator are suppressed except
    /// presentation (poses, look-at, flashes, art cards); facts and quest stages reach the
    /// spectator through FactSync/QuestSync.
    ///
    /// Camera: a spectator near the NPC turns to it (native forced look). A spectator far
    /// from the driver watches from the driver's eyes (camera moved to the driver's avatar
    /// head, looking at the speaker) and gets their own view back when the talk ends.
    /// </summary>
    public static class DialogueSync
    {
        private const byte MsgStart = 0, MsgNode = 1, MsgText = 2, MsgChoice = 3, MsgCharm = 4, MsgEnd = 5;
        private const byte MsgGiftItem = 6, MsgGiftCoin = 7;
        // A node with several pages ("extended text": the ending's epilogue cards, long
        // speeches) turns pages without leaving the node or re-processing it: the game's
        // SkipToNextTreeNode only bumps m_extendedTextID and its coroutine swaps the text.
        // The spectator's own skip is blocked, so the driver sends each page.
        private const byte MsgPage = 8;
        private const float PovDistance = 12f;

        private static bool _spectating;
        private static float _spectateStarted;
        private static string _treeName = "";
        private static int _lastNodeIdx = int.MinValue;
        private static bool _rootPending;
        private static global::NPCBase _speakerNpc;
        private static int _charmNeeded;
        private static int _started, _nodes, _ended, _ignored;

        // driver side: the NPC whose StartTalking is on the stack
        private static global::NPCBase _talkingNpc;

        public static bool Spectating => _spectating;
        public static string Stats =>
            $"spectating={_spectating} tree={_treeName} node={_lastNodeIdx} started={_started} nodes={_nodes} ended={_ended} ignored={_ignored} pov={Pov.Active}";

        private static bool CanSend()
        {
            var s = Session.Instance;
            return s != null && s.Connected && !LoopGuard.ApplyingRemote && !_spectating;
        }

        private static global::DelusionDialogueController Ctrl
        {
            get
            {
                try { return global::DelusionDialogueNEW.Instance?.DialogueController; }
                catch { return null; }
            }
        }

        // Long lines with their choices can pass one datagram: fragmented when they do.
        private static void Send(Writer w) => Session.Instance.SendFragmented(PacketKind.Dialogue, w.ToArray());

        // ------------------------------------------------------------------ identity

        internal static int NodeIndex(global::DialogueTree tree, global::DialogueGraphNode node)
        {
            if (tree == null || node == null) return -1;
            var nodes = tree.nodes;
            if (nodes == null) return -1;
            IntPtr p = node.Pointer;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n != null && n.Pointer == p) return i;
            }
            return -1;
        }

        private static global::DialogueGraphNode NodeAt(global::DialogueTree tree, int idx)
        {
            if (tree == null || idx < 0) return null;
            var nodes = tree.nodes;
            if (nodes == null || idx >= nodes.Count) return null;
            return nodes[idx]?.TryCast<global::DialogueGraphNode>();
        }

        private static global::DialogueTree TreeByName(string name)
        {
            var all = Resources.FindObjectsOfTypeAll<global::DialogueTree>();
            if (all == null) return null;
            foreach (var t in all) if (t != null && t.name == name) return t;
            return null;
        }

        private static global::NPCBase NpcByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var all = Resources.FindObjectsOfTypeAll<global::NPCBase>();
            if (all == null) return null;
            var reg = ObjectRegistry.Instance;
            foreach (var n in all)
            {
                if (n == null || !n.gameObject.activeInHierarchy) continue;
                try { if (EnemySync.KeyFor(reg, n) == key) return n; } catch { }
            }
            return null;
        }

        // ------------------------------------------------------------------ driver hooks

        internal static void OnStartTalking(global::NPCBase npc) => _talkingNpc = npc;
        internal static void OnStartTalkingDone() => _talkingNpc = null;

        internal static void OnActivateTree(global::DialogueTree tree, string speakerOverride)
        {
            if (!CanSend() || tree == null) return;
            string npcKey = "";
            try { if (_talkingNpc != null) npcKey = EnemySync.KeyFor(ObjectRegistry.Instance, _talkingNpc); } catch { }
            var w = new Writer();
            w.U8(MsgStart).Str(tree.name ?? "").Str(speakerOverride ?? "").Str(npcKey);
            Send(w);
            _treeName = tree.name ?? "";
            Log.Info($"dialogue -> peer: start {_treeName} npc={npcKey}");
        }

        internal static void OnActivateNode(global::DelusionDialogueController c, global::DialogueGraphNode node)
        {
            if (!CanSend() || c == null) return;
            var w = new Writer();
            w.U8(MsgNode).I32(NodeIndex(c.tree, node));
            Send(w);
        }

        internal static void OnTextProcessed(global::DelusionDialogueController c)
        {
            if (!CanSend() || c == null) return;
            var w = new Writer();
            string text = "", name = "";
            var choices = new List<string>();
            try { text = c.GetCurrentNodeText() ?? ""; } catch { }
            try { name = c.GetNPCName() ?? ""; } catch { }
            try
            {
                var ch = c.GetCurrentChoicesText();
                if (ch != null) for (int i = 0; i < ch.Count; i++) choices.Add(ch[i] ?? "");
            }
            catch { }
            w.U8(MsgText).Str(Clip(text, 900)).Str(Clip(name, 60)).U8((byte)Math.Min(choices.Count, 8));
            for (int i = 0; i < choices.Count && i < 8; i++) w.Str(Clip(choices[i], 120));
            Send(w);
        }

        internal static void OnChoice(int visibleIndex)
        {
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgChoice).I32(visibleIndex);
            Send(w);
        }

        internal static void OnCharmInt(int needed) => _charmNeeded = needed;

        internal static void OnCharmNote(bool success, bool stackable, bool lucky)
        {
            if (!CanSend()) return;
            var c = Ctrl;
            if (c == null || c.currentNode == null) return;   // not a dialogue charm check
            var w = new Writer();
            w.U8(MsgCharm).Bool(success).Bool(stackable).Bool(lucky).I32(_charmNeeded);
            Send(w);
        }

        internal static void OnEnd()
        {
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgEnd);
            Send(w);
            Log.Info("dialogue -> peer: end");
        }

        // ------------------------------------------------------------------ shared gifts
        //
        // Items and coin an NPC hands over (quest items, rewards) go to BOTH players, so a
        // quest that later checks for the item can never soft-lock the partner who didn't
        // do the talking. Items the player hands to an NPC stay with that player.

        private static int _giftsSent, _giftsApplied;
        public static string GiftStats => $"giftsSent={_giftsSent} giftsApplied={_giftsApplied}";

        internal static void OnGiveItem(string itemID, int amount, int quality)
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || LoopGuard.ApplyingRemote || _spectating) return;
            if (string.IsNullOrEmpty(itemID) || amount <= 0) return;
            var w = new Writer();
            w.U8(MsgGiftItem).Str(itemID).I32(amount).I32(quality);
            s.SendReliable(PacketKind.Dialogue, w.ToArray());
            _giftsSent++;
            Log.Info($"gift -> partner: {itemID} x{amount} q{quality}");
        }

        internal static void OnChangePlayerCoin(string amt)
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || LoopGuard.ApplyingRemote || _spectating) return;
            if (!int.TryParse(amt, out int n) || n <= 0) return;   // only rewards are shared
            var w = new Writer();
            w.U8(MsgGiftCoin).I32(n);
            s.SendReliable(PacketKind.Dialogue, w.ToArray());
            _giftsSent++;
        }

        private static void ApplyGiftItem(string itemID, int amount, int quality)
        {
            var inv = GameRefs.Inventory;
            if (inv == null) return;
            LoopGuard.Apply(() =>
            {
                try
                {
                    var it = inv.CreateItem(itemID, (global::ItemRarity)quality, amount);
                    if (it != null && inv.StoreItem(it, global::ItemPopupMode.PopupIfNew, amount, true)) _giftsApplied++;
                    Log.Info($"gift <- partner: {itemID} x{amount}");
                }
                catch (Exception e) { Log.Warn($"gift {itemID} failed: {e.Message}"); }
            });
        }

        private static void ApplyGiftCoin(int amount)
        {
            LoopGuard.Apply(() =>
            {
                try { global::DelusionStats.Instance?.ChangeCoin(amount, true, true); _giftsApplied++; }
                catch (Exception e) { Log.Warn("coin gift failed: " + e.Message); }
            });
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        // ------------------------------------------------------------------ spectator

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            try
            {
                switch (kind)
                {
                    case MsgStart: ApplyStart(r.Str(), r.Str(), r.Str()); break;
                    case MsgNode: ApplyNode(r.I32()); break;
                    case MsgText: ApplyText(r); break;
                    case MsgChoice: ApplyChoice(r.I32()); break;
                    case MsgCharm: ApplyCharm(r.Bool(), r.Bool(), r.Bool(), r.I32()); break;
                    case MsgEnd: ApplyEnd(); break;
                    case MsgGiftItem: ApplyGiftItem(r.Str(), r.I32(), r.I32()); break;
                    case MsgGiftCoin: ApplyGiftCoin(r.I32()); break;
                    case MsgPage: ApplyPage(r.I32(), r.I32(), r.Str()); break;
                }
            }
            catch (Exception e) { Log.Warn($"dialogue apply {kind} failed: {e.Message}"); }
        }

        private static void ApplyStart(string treeName, string speakerOverride, string npcKey)
        {
            var c = Ctrl;
            var mgr = GameRefs.Manager;
            if (c == null || mgr == null) { _ignored++; return; }

            var state = mgr.CurrentGameState;
            if (!_spectating && c.currentNode != null)
            {
                // Already in their own conversation: both talk independently, effects sync.
                _ignored++;
                Log.Info($"dialogue: partner started {treeName}, but we are talking - not joining");
                return;
            }
            if (state == global::DelusionManager.GameState.CINEMATIC
                || state == global::DelusionManager.GameState.CINEMATIC_FULLSPEED
                || state == global::DelusionManager.GameState.SCENE_SWITCH
                || state == global::DelusionManager.GameState.PREGAME)
            {
                _ignored++;
                return;
            }
            // Close menus/books/rest panels so the conversation can take the screen.
            if (state != global::DelusionManager.GameState.RUNNING
                && state != global::DelusionManager.GameState.SPEECH)
            {
                try { global::DelusionGUI.Instance?.DeactivateGUI(); } catch { }
            }

            _spectating = true;
            _spectateStarted = Time.realtimeSinceStartup;
            _treeName = treeName;
            _speakerNpc = NpcByKey(npcKey);
            _started++;

            var tree = TreeByName(treeName);
            bool ok = false;
            if (_speakerNpc != null)
            {
                // The native path: panel, name, speaking anim, voice, forced look, zoom.
                ok = LoopGuard.Apply(() =>
                {
                    try { return _speakerNpc.StartTalking(); }
                    catch (Exception e) { Log.Warn("spectator StartTalking: " + e.Message); return false; }
                });
                // The NPC may have switched trees by fact; make sure we show the driver's.
                if (ok && tree != null && c.tree != null && c.tree.name != treeName)
                    LoopGuard.Apply(() => global::DelusionDialogueNEW.Instance.ActivateDialogueTree(tree, speakerOverride));
            }
            if (!ok)
            {
                if (tree == null) { Log.Warn("dialogue: unknown tree " + treeName); _spectating = false; _ignored++; return; }
                LoopGuard.Apply(() => global::DelusionDialogueNEW.Instance.ActivateDialogueTree(tree, speakerOverride));
            }
            _lastNodeIdx = NodeIndex(c.tree, c.currentNode);
            _rootPending = true;

            // Far from the driver: watch through their eyes.
            var player = GameRefs.Player;
            if (player != null && PlayerSync.TryPeerPose(out var peerPos, out _)
                && Vector3.Distance(player.transform.position, peerPos) > PovDistance)
                Pov.Begin(_speakerNpc);
            Log.Info($"dialogue <- peer: spectating {treeName} npc={(_speakerNpc != null ? _speakerNpc.name : "-")} pov={Pov.Active}");
        }

        private static void ApplyNode(int idx)
        {
            if (!_spectating) return;
            var c = Ctrl;
            if (c == null) return;
            // StartTalking already activated the root; skip the driver's echo of it.
            if (_rootPending && idx == _lastNodeIdx) { _rootPending = false; return; }
            _rootPending = false;
            _nodes++;
            _lastNodeIdx = idx;
            if (idx < 0) { ApplyEnd(); return; }
            var node = NodeAt(c.tree, idx);
            if (node == null) { Log.Warn($"dialogue: node {idx} not in {_treeName}"); return; }
            LoopGuard.Apply(() => c.ActivateTreeNode(node));
        }

        private static void ApplyText(Reader r)
        {
            string text = r.Str(), name = r.Str();
            int n = r.U8();
            var choices = new List<string>(n);
            for (int i = 0; i < n; i++) choices.Add(r.Str());
            if (!_spectating) return;
            var c = Ctrl;
            if (c == null) return;
            // Safety net: if fact-conditioned text or hidden choices differ locally, show
            // exactly what the driver sees.
            bool differs = false;
            try
            {
                if ((c.GetCurrentNodeText() ?? "") != text) differs = true;
                var mine = c.GetCurrentChoicesText();
                int mc = mine != null ? mine.Count : 0;
                if (mc != n) differs = true;
                else for (int i = 0; i < n; i++) if ((mine[i] ?? "") != choices[i]) { differs = true; break; }
            }
            catch { differs = true; }
            try
            {
                var panel = global::DelusionDialogueNEW.Instance?.DialogueControllerPanel;
                if (panel != null && !string.IsNullOrEmpty(name) && (c.GetNPCName() ?? "") != name)
                    panel.EnableNPCName(name, true);
            }
            catch { }
            if (!differs) return;
            try
            {
                c.m_curNodeText = text;
                c.m_curNodeProgressingText = text;
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var s in choices) list.Add(s);
                c.m_curNodeChoicesText = list;
                LoopGuard.Apply(() => c.OnDialogueChanged?.Invoke());
                c.SkipTreeTextProgression();
            }
            catch (Exception e) { Log.Debug("dialogue text override: " + e.Message); }
        }

        // ---- pages of one node

        // driver: the node and text last seen, to notice a page turn
        private static int _drvNode = int.MinValue;
        private static string _drvText;
        private static int _pagesSent, _pagesApplied;
        public static string PageStats => $"pagesSent={_pagesSent} pagesApplied={_pagesApplied}";

        /// <summary>Driver, per frame: a new page of the same node goes to the spectator.</summary>
        private static void DriverTick()
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || _spectating) { _drvNode = int.MinValue; return; }
            var c = Ctrl;
            if (c == null || c.currentNode == null) { _drvNode = int.MinValue; return; }
            string text; int page;
            try { text = c.GetCurrentNodeText() ?? ""; page = c.m_extendedTextID; }
            catch { return; }
            int node = NodeIndex(c.tree, c.currentNode);
            if (node != _drvNode) { _drvNode = node; _drvText = text; return; }   // a node change has its own messages
            if (text == _drvText) return;
            _drvText = text;
            // Any change inside one node is a page turn (pages count from 0, the node's own
            // text being the one before them).
            var w = new Writer();
            w.U8(MsgPage).I32(node).I32(page).Str(Clip(text, 900));
            Send(w);
            _pagesSent++;
        }

        private static void ApplyPage(int node, int page, string text)
        {
            if (!_spectating) return;
            var c = Ctrl;
            if (c == null || c.currentNode == null) return;
            if (NodeIndex(c.tree, c.currentNode) != node) { Log.Debug($"dialogue page {page} for node {node}: not on it"); return; }
            try
            {
                c.m_extendedTextID = page;
                c.m_curNodeText = text;
                c.m_curNodeProgressingText = text;
                LoopGuard.Apply(() => c.OnDialogueChanged?.Invoke());
                c.SkipTreeTextProgression();
                _pagesApplied++;
            }
            catch (Exception e) { Log.Debug("dialogue page: " + e.Message); }
        }

        private static void ApplyChoice(int visibleIndex)
        {
            if (!_spectating) return;
            Log.Info($"dialogue <- peer chose option {visibleIndex + 1}");
        }

        private static void ApplyCharm(bool success, bool stackable, bool lucky, int needed)
        {
            if (!_spectating) return;
            var note = global::DelusionNote.Instance;
            if (note == null) return;
            LoopGuard.Apply(() =>
            {
                try
                {
                    note.SetCharmInt(needed);
                    note.StartCharmNote(success, stackable, lucky);
                }
                catch (Exception e) { Log.Debug("charm note: " + e.Message); }
            });
        }

        private static void ApplyEnd()
        {
            if (!_spectating) return;
            var c = Ctrl;
            if (c != null) LoopGuard.Apply(() =>
            {
                try { c.EndDialogue(); }
                catch (Exception e) { Log.Warn("spectator EndDialogue: " + e.Message); }
            });
            StopSpectating();
            _ended++;
            Log.Info("dialogue <- peer: ended");
        }

        private static void StopSpectating()
        {
            _spectating = false;
            _speakerNpc = null;
            _rootPending = false;
            Pov.End();
        }

        /// <summary>Per frame: drop out if the session or the conversation vanished.</summary>
        public static void Tick()
        {
            DriverTick();
            if (!_spectating) return;
            var s = Session.Instance;
            bool lost = s == null || !s.Connected;
            if (!lost && Time.realtimeSinceStartup - _spectateStarted > 2f)
            {
                var c = Ctrl;
                var mgr = GameRefs.Manager;
                // Our conversation was closed by something else (a cutscene, death).
                if (c == null || c.currentNode == null
                    || (mgr != null && mgr.CurrentGameState != global::DelusionManager.GameState.SPEECH))
                    lost = true;
            }
            if (!lost)
            {
                // Watch through the driver's eyes whenever we are too far to see the speaker
                // (checked continuously: the driver's pose may arrive after the start).
                var player = GameRefs.Player;
                if (!Pov.Active && player != null && PlayerSync.TryPeerPose(out var peerPos, out _))
                {
                    float dPeer = Vector3.Distance(player.transform.position, peerPos);
                    float dNpc = _speakerNpc != null ? Vector3.Distance(player.transform.position, _speakerNpc.transform.position) : 999f;
                    if (dPeer > PovDistance && dNpc > PovDistance) Pov.Begin(_speakerNpc);
                }
                return;
            }
            var ctrl = Ctrl;
            if (ctrl != null && ctrl.currentNode != null)
                LoopGuard.Apply(() => { try { ctrl.EndDialogue(); } catch { } });
            StopSpectating();
        }

        public static void LateTick() => Pov.LateTick();

        // ------------------------------------------------------------------ spectator gates

        /// <summary>True when a spectator's own input/timer tries to move the conversation.</summary>
        internal static bool BlockLocal => _spectating && !LoopGuard.ApplyingRemote;

        private static readonly HashSet<string> _presentationCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "TriggerPose", "CancelPose", "LookAtOther", "LookAtSpeaker", "StartCinematic",
            "ContinueCinematic", "EndCinematic", "FlashRed", "FlashWhite", "ShowCoin", "HideCoin",
        };

        /// <summary>Spectator: which node actions may run locally.</summary>
        internal static bool AllowAction(global::DialogueNodeActionData a)
        {
            if (!_spectating || a == null) return true;
            int type = (int)a.Type;
            switch (type)
            {
                case 0: return true;    // None
                case 5: return true;    // RefreshHiddenChoices
                case 3: return a.Name != null && _presentationCalls.Contains(a.Name);   // CallOnSpeaker
                default: return false;  // SetFact (synced), CallOnPlayer, EndDialogue (driver ends)
            }
        }

        // ------------------------------------------------------------------ point of view

        /// <summary>Moves the spectator's camera to the driver's eyes for the conversation.</summary>
        private static class Pov
        {
            public static bool Active;
            private static Transform _cam;
            private static Vector3 _savedLocalPos;
            private static Quaternion _savedLocalRot;
            private static global::NPCBase _target;

            public static void Begin(global::NPCBase target)
            {
                var cam = GameRefs.PlayerCamera;
                if (cam == null) cam = Camera.main;
                if (cam == null) { Log.Warn("dialogue pov: no camera"); return; }
                Log.Info($"dialogue pov: watching from the partner's eyes (camera {cam.name})");
                _cam = cam.transform;
                _savedLocalPos = _cam.localPosition;
                _savedLocalRot = _cam.localRotation;
                _target = target;
                Active = true;
                PlayerSync.SetPuppetVisible(false);   // the camera sits inside it
            }

            public static void LateTick()
            {
                if (!Active || _cam == null) return;
                if (!PlayerSync.TryPuppetHead(out var head)) return;
                Vector3 look;
                Transform lt = null;
                try { if (_target != null) lt = _target.lookTarget; } catch { }
                if (lt != null) look = lt.position;
                else if (_target != null) look = _target.transform.position + Vector3.up * 1.6f;
                else
                {
                    PlayerSync.TryPeerPose(out _, out float yaw);
                    look = head + Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                }
                var dir = look - head;
                if (dir.sqrMagnitude < 0.0001f) return;
                _cam.SetPositionAndRotation(head, Quaternion.LookRotation(dir.normalized, Vector3.up));
            }

            public static void End()
            {
                if (!Active) return;
                Active = false;
                if (_cam != null)
                {
                    _cam.localPosition = _savedLocalPos;
                    _cam.localRotation = _savedLocalRot;
                }
                _cam = null;
                _target = null;
                PlayerSync.SetPuppetVisible(true);
            }
        }
    }

    // ---------------------------------------------------------------------- driver hooks

    [HarmonyPatch(typeof(global::NPCBase), nameof(global::NPCBase.StartTalking))]
    internal static class Patch_NPC_StartTalking
    {
        [HarmonyPrefix] static void Prefix(global::NPCBase __instance) => DialogueSync.OnStartTalking(__instance);
        [HarmonyFinalizer] static void Finalizer() => DialogueSync.OnStartTalkingDone();
    }

    [HarmonyPatch(typeof(global::DelusionDialogueNEW), nameof(global::DelusionDialogueNEW.ActivateDialogueTree))]
    internal static class Patch_Dialogue_ActivateTree
    {
        [HarmonyPrefix]
        static void Prefix(global::DialogueTree tree, string SpeakerTagOverride) => DialogueSync.OnActivateTree(tree, SpeakerTagOverride);
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), nameof(global::DelusionDialogueController.ActivateTreeNode))]
    internal static class Patch_Dialogue_ActivateNode
    {
        [HarmonyPrefix]
        static void Prefix(global::DelusionDialogueController __instance, global::DialogueGraphNode node)
            => DialogueSync.OnActivateNode(__instance, node);
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), "ProcessCurrentTreeTextData")]
    internal static class Patch_Dialogue_Text
    {
        [HarmonyPostfix]
        static void Postfix(global::DelusionDialogueController __instance) => DialogueSync.OnTextProcessed(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), nameof(global::DelusionDialogueController.TransitionToTreeChoice))]
    internal static class Patch_Dialogue_Choice
    {
        [HarmonyPrefix]
        static bool Prefix(int iChoice)
        {
            if (DialogueSync.BlockLocal) return false;
            DialogueSync.OnChoice(iChoice);
            return true;
        }
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), nameof(global::DelusionDialogueController.EndDialogue))]
    internal static class Patch_Dialogue_End
    {
        [HarmonyPrefix]
        static bool Prefix()
        {
            if (DialogueSync.BlockLocal) return false;
            DialogueSync.OnEnd();
            return true;
        }
    }

    [HarmonyPatch(typeof(global::DelusionNote), nameof(global::DelusionNote.SetCharmInt))]
    internal static class Patch_Note_SetCharmInt
    {
        [HarmonyPostfix] static void Postfix(int charmNeeded) => DialogueSync.OnCharmInt(charmNeeded);
    }

    [HarmonyPatch(typeof(global::DelusionNote), nameof(global::DelusionNote.StartCharmNote))]
    internal static class Patch_Note_StartCharmNote
    {
        [HarmonyPostfix]
        static void Postfix(bool isSuccess, bool isStackable, bool isLuckyWin) => DialogueSync.OnCharmNote(isSuccess, isStackable, isLuckyWin);
    }

    // ---------------------------------------------------------------------- spectator gates

    [HarmonyPatch(typeof(global::DelusionDialogueController), "SkipManually")]
    internal static class Patch_Dialogue_SkipManually
    {
        [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal;
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), "SkipToNextTreeNode")]
    internal static class Patch_Dialogue_SkipToNext
    {
        // The driver streams every node, including pass-through and branch results, so a
        // spectator never advances on its own (not even under a remote apply).
        [HarmonyPrefix] static bool Prefix() => !DialogueSync.Spectating;
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), nameof(global::DelusionDialogueController.TransitionOnInput))]
    internal static class Patch_Dialogue_TransitionOnInput
    {
        [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal;
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), nameof(global::DelusionDialogueController.GoBackInDialogue))]
    internal static class Patch_Dialogue_Back
    {
        [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal;
    }

    [HarmonyPatch(typeof(global::DelusionDialogueController), "DoNodeAction")]
    internal static class Patch_Dialogue_DoNodeAction
    {
        [HarmonyPrefix]
        static bool Prefix(global::DialogueNodeActionData iActionData, ref global::EDialogueActionType __result)
        {
            if (DialogueSync.AllowAction(iActionData)) return true;
            try { __result = iActionData.Type; } catch { __result = global::EDialogueActionType.None; }
            return false;
        }
    }

    [HarmonyPatch(typeof(global::DialoguePanelController), "OnChoice1Click")]
    internal static class Patch_Panel_Choice1 { [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal; }

    [HarmonyPatch(typeof(global::DialoguePanelController), "OnChoice2Click")]
    internal static class Patch_Panel_Choice2 { [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal; }

    [HarmonyPatch(typeof(global::DialoguePanelController), "OnChoice3Click")]
    internal static class Patch_Panel_Choice3 { [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal; }

    [HarmonyPatch(typeof(global::DialoguePanelController), "OnDlgBackClick")]
    internal static class Patch_Panel_Back { [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal; }

    [HarmonyPatch(typeof(global::DialoguePanelController), "OnDlgExitClick")]
    internal static class Patch_Panel_Exit { [HarmonyPrefix] static bool Prefix() => !DialogueSync.BlockLocal; }

    // NPC gifts. The typed overload carries the final values and always reports; the string
    // overload ("id,amount,quality") reports only if it did not go through the typed one.
    [HarmonyPatch(typeof(global::NPCBase), nameof(global::NPCBase.GiveItem), new[] { typeof(string), typeof(int), typeof(int) })]
    internal static class Patch_NPC_GiveItemTyped
    {
        [HarmonyPostfix]
        static void Postfix(string itemID, int amount, int quality)
        {
            Patch_NPC_GiveItemString.TypedRan = true;
            DialogueSync.OnGiveItem(itemID, amount, quality);
        }
    }

    [HarmonyPatch(typeof(global::NPCBase), nameof(global::NPCBase.GiveItem), new[] { typeof(string) })]
    internal static class Patch_NPC_GiveItemString
    {
        internal static bool TypedRan;
        [HarmonyPrefix] static void Prefix() => TypedRan = false;
        [HarmonyPostfix]
        static void Postfix(string s)
        {
            if (TypedRan || string.IsNullOrEmpty(s)) return;
            var parts = s.Split(',');
            int amount = 1, quality = 1;
            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out amount);
            if (parts.Length > 2) int.TryParse(parts[2].Trim(), out quality);
            DialogueSync.OnGiveItem(parts[0].Trim(), amount <= 0 ? 1 : amount, quality);
        }
    }

    [HarmonyPatch(typeof(global::NPCBase), nameof(global::NPCBase.ChangePlayerCoin))]
    internal static class Patch_NPC_ChangePlayerCoin
    {
        [HarmonyPostfix] static void Postfix(string amtString) => DialogueSync.OnChangePlayerCoin(amtString);
    }
}
