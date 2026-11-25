using System;
using BepInEx.Configuration;
using DelusionMP.Game;
using DelusionMP.Net;
using DelusionMP.Sync;

namespace DelusionMP.Core
{
    /// <summary>
    /// All DelusionMP settings, backed by BepInEx/config/delusionmp.coop.cfg.
    /// Replaces both DelusionMP-Coop.ini and the launcher's rules editor so everything
    /// lives in one config file editable from the in-game native Co-op menu.
    /// </summary>
    public static class CoopConfig
    {
        public static readonly string[] ModelValues =
        {
            "fbx_UnionWomen",
            "fbx_DarkStarWomen",
            "fbx_PwyllWomen",
        };

        public static readonly string[] ModelLabels =
        {
            "Union Woman",
            "Dark Star Woman",
            "Pwyll Woman",
        };

        public static readonly string[] OutfitValues =
        {
            "thief",
            "civilianA",
            "civilianB",
            "civilianC",
            "soldier",
            "inquisitor",
            "cultist",
        };

        public static readonly string[] OutfitLabels =
        {
            "Thief",
            "Civilian A",
            "Civilian B",
            "Civilian C",
            "Soldier",
            "Inquisitor",
            "Cultist",
        };

        public static readonly string[] KeyChoices =
        {
            "V", "B", "T",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "LeftCtrl", "RightCtrl", "Tab", "none",
        };

        private static ConfigFile _cfg;

        // [Connection]
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<string> Network;          // "steam" or "lan"
        public static ConfigEntry<string> SteamIdentity;    // "game" or "spacewar"
        public static ConfigEntry<bool> SteamUsePassword;   // true = password lobby, false = Steam friends
        public static ConfigEntry<string> SteamPassword;
        public static ConfigEntry<string> SteamPartner;     // friend name or SteamID64 (blank = auto)
        public static ConfigEntry<string> LanServer;
        public static ConfigEntry<int> LanPort;
        public static ConfigEntry<string> LanBind;
        public static ConfigEntry<bool> SeparateCoopSaves;

        // [Avatar]
        public static ConfigEntry<string> AvatarModel;
        public static ConfigEntry<string> AvatarOutfit;

        // [Player]
        public static ConfigEntry<string> TeleportKey;

        // [Rules] (host-authoritative)
        public static ConfigEntry<bool> ExclusiveUniqueShopItems;
        public static ConfigEntry<bool> DeathDebuffs;
        public static ConfigEntry<int> MaxStacks;
        public static ConfigEntry<float> PercentPerStack;
        public static ConfigEntry<float> RespawnDelaySeconds;
        public static ConfigEntry<bool> RespawnAtPartner;

        // [Voice]
        public static ConfigEntry<bool> ChatEnabled;
        public static ConfigEntry<string> ChatKey;
        public static ConfigEntry<bool> VoiceChat;
        public static ConfigEntry<int> VoiceMode;           // 0 = push-to-talk, 1 = open mic
        public static ConfigEntry<string> VoiceKey;
        public static ConfigEntry<int> VoiceRangeM;
        public static ConfigEntry<int> VoiceVolumePct;
        public static ConfigEntry<int> VoiceMicPct;
        public static ConfigEntry<int> VoiceAwayPct;

        public static void Bind(ConfigFile cfg)
        {
            _cfg = cfg;

            // [Connection]
            PlayerName = cfg.Bind("Connection", "PlayerName", "Player",
                "Your name, shown to your partner.");
            Network = cfg.Bind("Connection", "Network", "steam",
                "Transport: steam (no port forwarding) or lan (direct UDP).");
            SteamIdentity = cfg.Bind("Connection", "SteamIdentity", "game",
                "Steam app identity: game (Dread Delusion, 1574240) or spacewar (480).");
            SteamUsePassword = cfg.Bind("Connection", "SteamUsePassword", true,
                "Steam discovery: true = match by co-op password lobby; false = Steam friends only.");
            SteamPassword = cfg.Bind("Connection", "SteamPassword", "",
                "Shared co-op password for Steam lobby discovery (only its hash is published).");
            SteamPartner = cfg.Bind("Connection", "SteamPartner", "",
                "Steam friend name or SteamID64 to join in Friends mode (empty = auto-detect friend in game).");
            LanServer = cfg.Bind("Connection", "LanServer", "127.0.0.1",
                "Host IP address or hostname when joining over LAN.");
            LanPort = cfg.Bind("Connection", "LanPort", 27050,
                "UDP port used for LAN hosting and joining.");
            LanBind = cfg.Bind("Connection", "LanBind", "0.0.0.0",
                "Local bind address when hosting over LAN.");
            SeparateCoopSaves = cfg.Bind("Connection", "SeparateCoopSaves", true,
                "Keep co-op saves in a separate folder (Dread Delusion MP1) so single-player saves stay untouched.");

            // [Avatar]
            AvatarModel = cfg.Bind("Avatar", "Model", "fbx_UnionWomen",
                "Character model your partner sees you as (fbx_UnionWomen, fbx_DarkStarWomen, fbx_PwyllWomen).");
            AvatarOutfit = cfg.Bind("Avatar", "Outfit", "thief",
                "Outfit preset for fbx_UnionWomen (thief, civilianA/B/C, soldier, inquisitor, cultist) or custom part list.");

            // [Player]
            TeleportKey = cfg.Bind("Player", "TeleportToPartnerKey", "F8",
                "Key that puts you next to your partner (none to disable).");

            // [Rules]
            ExclusiveUniqueShopItems = cfg.Bind("Rules", "ExclusiveUniqueShopItems", false,
                "Only one player can buy a one-of-a-kind shop item (host decides).");
            DeathDebuffs = cfg.Bind("Rules", "DeathDebuffs", true,
                "Dying while your partner is alive adds a stack that lowers max health/mana/stamina until you rest.");
            MaxStacks = cfg.Bind("Rules", "MaxStacks", 5,
                "Most death stacks a player can carry.");
            PercentPerStack = cfg.Bind("Rules", "PercentPerStack", 10f,
                "Max health/mana/stamina lost per stack, in %.");
            RespawnDelaySeconds = cfg.Bind("Rules", "RespawnDelaySeconds", 8f,
                "Seconds a player stays down when the partner is alive.");
            RespawnAtPartner = cfg.Bind("Rules", "RespawnAtPartner", true,
                "Get up next to the partner instead of at the nearest respawn point.");

            // [Voice]
            ChatEnabled = cfg.Bind("Voice", "ChatEnabled", true,
                "Chat key opens a text line; partner lines are still shown when off.");
            ChatKey = cfg.Bind("Voice", "ChatKey", "T",
                "Key that opens the chat line.");
            VoiceChat = cfg.Bind("Voice", "VoiceChat", true,
                "Proximity voice chat: hear your partner and talk.");
            VoiceMode = cfg.Bind("Voice", "VoiceMode", 0,
                "0 = push-to-talk, 1 = open mic (noise-gated).");
            VoiceKey = cfg.Bind("Voice", "VoiceKey", "V",
                "Push-to-talk key.");
            VoiceRangeM = cfg.Bind("Voice", "VoiceRangeM", 80,
                "Partner's voice fades to nothing at this many metres; 0 = no proximity.");
            VoiceVolumePct = cfg.Bind("Voice", "VoiceVolumePct", 100,
                "How loud your partner's voice is, in %.");
            VoiceMicPct = cfg.Bind("Voice", "VoiceMicPct", 100,
                "How loud your microphone is sent, in %.");
            VoiceAwayPct = cfg.Bind("Voice", "VoiceAwayPct", 40,
                "Partner's volume while they are in another level or menu, in %.");

            ApplyAll(broadcastIfConnected: false);
        }

        public static string CleanName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Player";
            var chars = new char[Math.Min(s.Length, 24)];
            int n = 0;
            foreach (char c in s)
            {
                if (n >= 24) break;
                if (char.IsControl(c) || c == '"') continue;
                chars[n++] = c;
            }
            var t = new string(chars, 0, n).Trim();
            return t.Length == 0 ? "Player" : t;
        }

        /// <summary>Push all config values into the live mod subsystems and save to disk.</summary>
        public static void ApplyAll(bool broadcastIfConnected = true)
        {
            if (_cfg == null) return;

            Bootstrap.PlayerName = CleanName(PlayerName.Value);

            RemoteAvatar.SetLocalLook(AvatarModel.Value, AvatarOutfit.Value);
            PartnerTeleport.Key = TeleportKey.Value;

            WorldEventsSync.ExclusiveUniqueBuys = ExclusiveUniqueShopItems.Value;
            CoopDeath.Enabled = DeathDebuffs.Value;
            CoopDeath.MaxStacks = Math.Clamp(MaxStacks.Value, 0, 20);
            CoopDeath.PctPerStack = Math.Clamp(PercentPerStack.Value, 0f, 100f);
            CoopDeath.RespawnDelay = Math.Clamp(RespawnDelaySeconds.Value, 0f, 60f);
            CoopDeath.RespawnAtPartner = RespawnAtPartner.Value;

            bool voiceWasOn = VoiceConfig.VoiceChat;
            VoiceConfig.ChatEnabled = ChatEnabled.Value;
            VoiceConfig.ChatKey = ChatKey.Value;
            VoiceConfig.VoiceChat = VoiceChat.Value;
            VoiceConfig.VoiceMode = Math.Clamp(VoiceMode.Value, 0, 1);
            VoiceConfig.VoiceKey = VoiceKey.Value;
            VoiceConfig.VoiceRangeM = Math.Clamp(VoiceRangeM.Value, 0, 2000);
            VoiceConfig.VoiceVolumePct = Math.Clamp(VoiceVolumePct.Value, 0, 200);
            VoiceConfig.VoiceMicPct = Math.Clamp(VoiceMicPct.Value, 0, 300);
            VoiceConfig.VoiceAwayPct = Math.Clamp(VoiceAwayPct.Value, 0, 100);

            if (broadcastIfConnected)
            {
                try { _cfg.Save(); } catch (Exception e) { Log.Warn("config save: " + e.Message); }

                if (voiceWasOn != VoiceConfig.VoiceChat)
                {
                    try
                    {
                        if (VoiceConfig.VoiceChat) VoiceSync.Init();
                        else VoiceSync.Shutdown();
                    }
                    catch (Exception e) { Log.Warn("voice reinit: " + e.Message); }
                }

                var s = Session.Instance;
                if (s != null && s.Connected)
                {
                    if (s.Role == SessionRole.Host)
                        RulesSync.Send(s);
                    try { WorldEventsSync.SendJoinState(); } catch { }
                }
            }
        }

        public static void Save()
        {
            try { _cfg?.Save(); } catch (Exception e) { Log.Warn("config save: " + e.Message); }
        }

        public static void ResetRulesToDefault()
        {
            ExclusiveUniqueShopItems.Value = false;
            DeathDebuffs.Value = true;
            MaxStacks.Value = 5;
            PercentPerStack.Value = 10f;
            RespawnDelaySeconds.Value = 8f;
            RespawnAtPartner.Value = true;
            ApplyAll(true);
        }

        public static void ResetPlayerVoiceToDefault()
        {
            AvatarModel.Value = "fbx_UnionWomen";
            AvatarOutfit.Value = "thief";
            TeleportKey.Value = "F8";
            ChatEnabled.Value = true;
            ChatKey.Value = "T";
            VoiceChat.Value = true;
            VoiceMode.Value = 0;
            VoiceKey.Value = "V";
            VoiceRangeM.Value = 80;
            VoiceVolumePct.Value = 100;
            VoiceMicPct.Value = 100;
            VoiceAwayPct.Value = 40;
            ApplyAll(true);
        }
    }
}
