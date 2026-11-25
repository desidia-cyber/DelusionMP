using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DelusionMP.UI
{
    /// <summary>
    /// Integrates DelusionMP organically into Dread Delusion's native uGUI main menu and
    /// pause menu by cloning the game's own stone frames, buttons, label plates, toggles,
    /// sliders, and pixel-serif fonts directly from DelusionMenu.titleScreen and
    /// DelusionMenu.optionsScreen.
    /// </summary>
    public static class NativeCoopMenu
    {
        public static bool IsOpen => _coopPanel != null && _coopPanel.activeSelf;

        private static DelusionMenu _boundMenu;
        private static GameObject _titleCoopBtn;
        private static GameObject _pauseCoopBtn;
        private static Text _titleStatusText;
        private static GameObject _coopPanel;
        private static bool _openedFromPause;

        // Extracted native templates (inactive clones of optionsScreen widgets)
        private static GameObject _tplRoot;
        private static GameObject _tplButton;
        private static GameObject _tplLabelPlate;
        private static GameObject _tplToggle;
        private static GameObject _tplSlider;
        private static GameObject _tplWidePlate;
        private static Font _nativeFont;
        private static Color _nativeTextColor = new Color(0.95f, 0.92f, 0.86f, 1f);
        private static Color _goldAccent = new Color(0.88f, 0.72f, 0.36f, 1f);
        private static Color _mutedColor = new Color(0.68f, 0.64f, 0.58f, 1f);
        private static Color _activeTabTint = new Color(1.25f, 1.12f, 0.85f, 1f);
        private static Color _normalTabTint = new Color(0.82f, 0.82f, 0.86f, 1f);

        // Tabs & live references
        private static int _activeTab;
        private static readonly Button[] _tabButtons = new Button[4];
        private static readonly GameObject[] _tabPages = new GameObject[4];
        private static Text _panelTitleText;
        private static Text _statusBannerText;
        private static Text _footerNoteText;
        private static GameObject _loadSaveBtn;
        private static GameObject _hostBtnObj;
        private static GameObject _joinBtnObj;
        private static GameObject _disconnectBtnObj;

        // Session tab dynamic fields
        private static GameObject _steamGroup;
        private static GameObject _steamPwRow;
        private static GameObject _steamPartnerRow;
        private static GameObject _lanGroup;
        private static Text _lobbyCodeText;
        private static Text _friendsScanText;
        private static GameObject _friendsListRoot;
        private static Text _netToggleLabel;
        private static Text _steamIdLabel;
        private static Text _steamModeLabel;

        // Rules tab sliders/toggles for reset refresh
        private static Toggle _togExclusiveShop;
        private static Toggle _togDeathDebuffs;
        private static Toggle _togRespawnPartner;
        private static Slider _sldMaxStacks;
        private static Text _valMaxStacks;
        private static Slider _sldPctStack;
        private static Text _valPctStack;
        private static Slider _sldRespawnDelay;
        private static Text _valRespawnDelay;

        // Player & Voice tab controls
        private static Text _lblAvatarModel;
        private static Text _lblAvatarOutfit;
        private static Text _lblTeleportKey;
        private static Toggle _togChatEnabled;
        private static Text _lblChatKey;
        private static Toggle _togVoiceChat;
        private static Text _lblVoiceMode;
        private static Text _lblVoiceKey;
        private static Slider _sldVoiceRange;
        private static Text _valVoiceRange;
        private static Slider _sldVoiceVol;
        private static Text _valVoiceVol;
        private static Slider _sldVoiceMic;
        private static Text _valVoiceMic;
        private static Slider _sldVoiceAway;
        private static Text _valVoiceAway;

        // Backups tab container
        private static GameObject _backupsListRoot;
        private static string _confirmRestoreStamp = "";

        private static float _nextStatusRefresh;
        private static float _nextInjectRetry;

        public static void Tick()
        {
            var menu = global::DelusionMenu.Instance;
            if (menu == null)
            {
                _boundMenu = null;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if ((_boundMenu != menu || _coopPanel == null) && now >= _nextInjectRetry)
            {
                _nextInjectRetry = now + 2f;
                TryInject(menu);
            }

            if (now >= _nextStatusRefresh)
            {
                _nextStatusRefresh = now + 0.35f;
                RefreshLiveStatus();
            }
        }

        private static void TryInject(DelusionMenu menu)
        {
            try
            {
                if (menu.titleScreen == null || menu.optionsScreen == null) return;

                _boundMenu = menu;
                ExtractTemplates(menu.optionsScreen);
                InjectTitleButton(menu);
                InjectPauseButton(menu);
                BuildCoopPanel(menu);
                Log.Info("native co-op menu injected into DelusionMenu");
            }
            catch (Exception e)
            {
                Log.Error("native co-op menu injection failed: " + e);
            }
        }

        // =====================================================================
        // Template extraction from native optionsScreen
        // =====================================================================

        private static void ExtractTemplates(GameObject optionsScreen)
        {
            if (_tplRoot != null) UnityEngine.Object.Destroy(_tplRoot);
            _tplRoot = new GameObject("DelusionMP_Templates");
            _tplRoot.transform.SetParent(optionsScreen.transform.parent, false);
            _tplRoot.SetActive(false);

            // Grab the game's native pixel-serif font from any Text inside optionsScreen
            foreach (var t in optionsScreen.GetComponentsInChildren<Text>(true))
            {
                if (t != null && t.font != null)
                {
                    _nativeFont = t.font;
                    _nativeTextColor = t.color;
                    break;
                }
            }

            // Button template: prefer "Return" or "Reset All" button
            Button bestBtn = null;
            Button wideBtn = null;
            foreach (var b in optionsScreen.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                var n = b.gameObject.name;
                if (n.IndexOf("Return", StringComparison.OrdinalIgnoreCase) >= 0) bestBtn = b;
                else if (bestBtn == null && n.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0) bestBtn = b;
                else if (wideBtn == null && b.GetComponent<RectTransform>() is RectTransform brt && brt.rect.width > 160f)
                    wideBtn = b;
            }
            if (bestBtn == null)
            {
                var allBtns = optionsScreen.GetComponentsInChildren<Button>(true);
                if (allBtns != null && allBtns.Length > 0) bestBtn = allBtns[0];
            }

            if (bestBtn != null) _tplButton = CloneTemplate(bestBtn.gameObject, "TplButton");
            if (wideBtn != null) _tplWidePlate = CloneTemplate(wideBtn.gameObject, "TplWidePlate");
            else if (bestBtn != null) _tplWidePlate = CloneTemplate(bestBtn.gameObject, "TplWidePlate");

            // Toggle template
            var tog = optionsScreen.GetComponentInChildren<Toggle>(true);
            if (tog != null) _tplToggle = CloneTemplate(tog.gameObject, "TplToggle");

            // Slider template
            var sld = optionsScreen.GetComponentInChildren<Slider>(true);
            if (sld != null) _tplSlider = CloneTemplate(sld.gameObject, "TplSlider");

            // Label plate template: an Image with a child Text that is NOT a Button/Toggle/Slider
            foreach (var img in optionsScreen.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.gameObject == optionsScreen) continue;
                if (img.GetComponent<Selectable>() != null) continue;
                var rt = img.GetComponent<RectTransform>();
                if (rt == null || rt.rect.width < 80f || rt.rect.width > 360f || rt.rect.height < 20f || rt.rect.height > 70f)
                    continue;
                var txt = img.GetComponentInChildren<Text>(true);
                if (txt != null)
                {
                    _tplLabelPlate = CloneTemplate(img.gameObject, "TplLabelPlate");
                    break;
                }
            }
            if (_tplLabelPlate == null && _tplButton != null)
                _tplLabelPlate = CloneTemplate(_tplButton, "TplLabelPlate");
        }

        private static GameObject CloneTemplate(GameObject src, string name)
        {
            var c = UnityEngine.Object.Instantiate(src, _tplRoot.transform, false);
            c.name = name;
            c.SetActive(false);
            StripLocalisers(c);
            foreach (var bs in c.GetComponentsInChildren<global::ButtonSelector>(true))
                UnityEngine.Object.Destroy(bs);
            foreach (var sc in c.GetComponentsInChildren<global::SettingCycleOptions>(true))
                UnityEngine.Object.Destroy(sc);
            return c;
        }

        // =====================================================================
        // Inject "Co-op" into Title Screen & Pause Screen
        // =====================================================================

        private static void InjectTitleButton(DelusionMenu menu)
        {
            var title = menu.titleScreen;
            if (title == null) return;
            if (_titleCoopBtn != null && _titleCoopBtn.transform != null && _titleCoopBtn.transform.IsChildOf(title.transform))
                return;

            var existing = title.transform.Find("Coop_Btn");
            if (existing != null) { _titleCoopBtn = existing.gameObject; return; }

            Button startBtn = null;
            Button optionsBtn = null;
            foreach (var b in title.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                if (b.gameObject.name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0) startBtn = b;
                else if (b.gameObject.name.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0) optionsBtn = b;
            }
            if (startBtn == null) return;

            var parent = startBtn.transform.parent;
            var btnGo = UnityEngine.Object.Instantiate(startBtn.gameObject, parent, false);
            btnGo.name = "Coop_Btn";
            StripLocalisers(btnGo);
            foreach (var bs in btnGo.GetComponentsInChildren<global::ButtonSelector>(true))
                UnityEngine.Object.Destroy(bs);

            SetLabel(btnGo, "Co-op");
            var btn = btnGo.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener((UnityAction)(() => Open(fromPause: false)));

            // Place right below Start Game
            int startIdx = startBtn.transform.GetSiblingIndex();
            btnGo.transform.SetSiblingIndex(startIdx + 1);

            // If parent does NOT have a LayoutGroup, shift sibling buttons down nicely
            var lg = parent.GetComponent<VerticalLayoutGroup>();
            if (lg == null && optionsBtn != null && optionsBtn.transform.parent == parent)
            {
                var startRt = startBtn.GetComponent<RectTransform>();
                var optRt = optionsBtn.GetComponent<RectTransform>();
                float step = startRt.anchoredPosition.y - optRt.anchoredPosition.y;
                if (step < 20f) step = 62f;

                // Compact spacing slightly so all 5 buttons sit comfortably inside the stone frame
                float compactStep = step * 0.84f;
                var siblings = new List<RectTransform>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var ch = parent.GetChild(i);
                    if (ch.GetComponent<Button>() != null && ch.gameObject.activeSelf)
                        siblings.Add(ch.GetComponent<RectTransform>());
                }
                siblings.Sort((a, b) => b.anchoredPosition.y.CompareTo(a.anchoredPosition.y));
                // Put Coop_Btn directly below Start_Btn
                var ordered = new List<RectTransform> { startRt, btnGo.GetComponent<RectTransform>() };
                foreach (var s in siblings)
                {
                    if (s != startRt && s != btnGo.GetComponent<RectTransform>())
                        ordered.Add(s);
                }
                float topY = startRt.anchoredPosition.y + (step - compactStep) * 0.6f;
                for (int i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i].anchoredPosition;
                    ordered[i].anchoredPosition = new Vector2(p.x, topY - i * compactStep);
                    ordered[i].sizeDelta = new Vector2(ordered[i].sizeDelta.x, Math.Min(ordered[i].sizeDelta.y, compactStep - 8f));
                }
            }
            else if (lg != null)
            {
                lg.spacing = Math.Max(4f, lg.spacing * 0.72f);
            }

            _titleCoopBtn = btnGo;

            // Subtle native-font status indicator at top-left below "Version 1.3.7"
            var statusGo = new GameObject("DelusionMP_TitleStatus");
            statusGo.transform.SetParent(title.transform, false);
            var srt = statusGo.AddComponent<RectTransform>();
            srt.anchorMin = new Vector2(0f, 1f);
            srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = new Vector2(22f, -46f);
            srt.sizeDelta = new Vector2(640f, 36f);
            _titleStatusText = statusGo.AddComponent<Text>();
            _titleStatusText.font = _nativeFont;
            _titleStatusText.fontSize = 18;
            _titleStatusText.color = _goldAccent;
            _titleStatusText.alignment = TextAnchor.UpperLeft;
            _titleStatusText.raycastTarget = false;
            var sh = statusGo.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
            sh.effectDistance = new Vector2(1.5f, -1.5f);
        }

        private static void InjectPauseButton(DelusionMenu menu)
        {
            var pause = menu.pauseScreen;
            if (pause == null) return;
            if (_pauseCoopBtn != null && _pauseCoopBtn.transform != null && _pauseCoopBtn.transform.IsChildOf(pause.transform))
                return;

            var existing = pause.transform.Find("Coop_Pause_Btn");
            if (existing != null) { _pauseCoopBtn = existing.gameObject; return; }

            Button refBtn = null;
            foreach (var b in pause.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                if (b.gameObject.name.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0) { refBtn = b; break; }
            }
            if (refBtn == null)
            {
                var all = pause.GetComponentsInChildren<Button>(true);
                if (all != null && all.Length > 0) refBtn = all[0];
            }
            if (refBtn == null) return;

            var parent = refBtn.transform.parent;
            var btnGo = UnityEngine.Object.Instantiate(refBtn.gameObject, parent, false);
            btnGo.name = "Coop_Pause_Btn";
            StripLocalisers(btnGo);
            foreach (var bs in btnGo.GetComponentsInChildren<global::ButtonSelector>(true))
                UnityEngine.Object.Destroy(bs);

            SetLabel(btnGo, "Co-op");
            var btn = btnGo.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener((UnityAction)(() => Open(fromPause: true)));
            btnGo.transform.SetSiblingIndex(refBtn.transform.GetSiblingIndex());

            var lg = parent.GetComponent<VerticalLayoutGroup>();
            if (lg == null)
            {
                // Place neatly above/below options if manually laid out
                var buttons = new List<RectTransform>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var ch = parent.GetChild(i);
                    if (ch.GetComponent<Button>() != null && ch.gameObject.activeSelf)
                        buttons.Add(ch.GetComponent<RectTransform>());
                }
                if (buttons.Count >= 2)
                {
                    buttons.Sort((a, b) => b.anchoredPosition.y.CompareTo(a.anchoredPosition.y));
                    float top = buttons[0].anchoredPosition.y;
                    float bot = buttons[buttons.Count - 1].anchoredPosition.y;
                    float span = Math.Max(180f, top - bot);
                    float step = span / Math.Max(1, buttons.Count - 1);
                    for (int i = 0; i < buttons.Count; i++)
                        buttons[i].anchoredPosition = new Vector2(buttons[i].anchoredPosition.x, top - i * step);
                }
            }

            _pauseCoopBtn = btnGo;
        }

        // =====================================================================
        // Build the native-styled Co-op panel (cloned from optionsScreen)
        // =====================================================================

        private static void BuildCoopPanel(DelusionMenu menu)
        {
            if (_coopPanel != null) UnityEngine.Object.Destroy(_coopPanel);

            var panel = UnityEngine.Object.Instantiate(menu.optionsScreen, menu.optionsScreen.transform.parent, false);
            panel.name = "DelusionMP_CoopPanel";
            panel.SetActive(false);

            // Strip any game-specific scripts from the clone so DelusionSettings doesn't touch it
            StripLocalisers(panel);
            foreach (var bs in panel.GetComponentsInChildren<global::ButtonSelector>(true))
                UnityEngine.Object.Destroy(bs);
            foreach (var sc in panel.GetComponentsInChildren<global::SettingCycleOptions>(true))
                UnityEngine.Object.Destroy(sc);

            // Identify the top title ribbon ("Options" text) so we preserve it and rename it to "Co-op".
            Transform titleBanner = null;
            foreach (var t in panel.GetComponentsInChildren<Text>(true))
            {
                if (t != null && t.text != null && t.text.Trim().Equals("Options", StringComparison.OrdinalIgnoreCase))
                {
                    _panelTitleText = t;
                    titleBanner = t.transform.parent;
                    break;
                }
            }

            // Clear all Options controls while keeping the stone frame AND the torn top title ribbon
            Transform contentHost = FindBackdropContainer(panel.transform, titleBanner);
            var toDestroy = new List<GameObject>();
            for (int i = 0; i < contentHost.childCount; i++)
            {
                var ch = contentHost.GetChild(i);
                if (titleBanner != null && (ch == titleBanner || IsAncestorOf(ch, titleBanner)))
                    continue;
                if (ch.name.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ch.name.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                bool hasControls = ch.GetComponentInChildren<Selectable>(true) != null ||
                                   ch.GetComponentInChildren<Text>(true) != null;
                if (hasControls)
                    toDestroy.Add(ch.gameObject);
            }
            foreach (var go in toDestroy)
                UnityEngine.Object.DestroyImmediate(go);

            if (_panelTitleText != null)
            {
                _panelTitleText.text = "Co-op";
                _panelTitleText.transform.parent.SetAsLastSibling();
            }

            // Create our clean layout container inside the stone frame (leaving room for the top title ribbon)
            var body = new GameObject("CoopBody");
            body.transform.SetParent(contentHost, false);
            var bodyRt = body.AddComponent<RectTransform>();
            bodyRt.anchorMin = new Vector2(0.05f, 0.05f);
            bodyRt.anchorMax = new Vector2(0.95f, 0.87f);
            bodyRt.offsetMin = Vector2.zero;
            bodyRt.offsetMax = Vector2.zero;

            // 1. Top Tab Bar (4 native buttons)
            var tabBar = MakeRect("TabBar", body.transform, new Vector2(0f, 0.89f), new Vector2(1f, 1f));
            var tabNames = new[] { "Session", "Rules", "Player & Voice", "Backups & Logs" };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                float x0 = i * 0.25f + 0.006f;
                float x1 = (i + 1) * 0.25f - 0.006f;
                var tb = CreateNativeButton(tabBar, "Tab_" + idx, tabNames[i], new Vector2(x0, 0.04f), new Vector2(x1, 0.96f), 21, () => SelectTab(idx));
                _tabButtons[i] = tb.GetComponent<Button>();
            }

            // 2. Live Status Strip right under tabs
            var statusRow = MakeRect("StatusBar", body.transform, new Vector2(0.006f, 0.80f), new Vector2(0.994f, 0.875f));
            var statusBg = statusRow.gameObject.AddComponent<Image>();
            statusBg.color = new Color(0.04f, 0.05f, 0.07f, 0.72f);
            _statusBannerText = CreateText(statusRow, "StatusText", "Offline (Singleplayer)", 20, _goldAccent, TextAnchor.MiddleCenter);
            Stretch(_statusBannerText.rectTransform, 8f, 2f);

            // 3. Pages area
            var pagesHost = MakeRect("Pages", body.transform, new Vector2(0f, 0.13f), new Vector2(1f, 0.785f));
            _tabPages[0] = BuildSessionPage(pagesHost);
            _tabPages[1] = BuildRulesPage(pagesHost);
            _tabPages[2] = BuildPlayerVoicePage(pagesHost);
            _tabPages[3] = BuildBackupsLogsPage(pagesHost);

            // 4. Bottom Footer Bar (feedback text + Load Save shortcut + Return)
            var footer = MakeRect("Footer", body.transform, new Vector2(0f, 0f), new Vector2(1f, 0.115f));
            _footerNoteText = CreateText(footer, "FooterNote", "", 18, _mutedColor, TextAnchor.MiddleLeft);
            _footerNoteText.rectTransform.anchorMin = new Vector2(0.01f, 0f);
            _footerNoteText.rectTransform.anchorMax = new Vector2(0.54f, 1f);
            _footerNoteText.rectTransform.offsetMin = Vector2.zero;
            _footerNoteText.rectTransform.offsetMax = Vector2.zero;

            _loadSaveBtn = CreateNativeButton(footer, "LoadSave_Btn", "Load Save", new Vector2(0.56f, 0.06f), new Vector2(0.77f, 0.94f), 22, OnClickLoadSave);
            CreateNativeButton(footer, "Return_Btn", "Return", new Vector2(0.79f, 0.06f), new Vector2(0.994f, 0.94f), 22, Close);

            _coopPanel = panel;
            SelectTab(0);
        }

        // =====================================================================
        // Page 0: Session (Host / Join / Disconnect, Steam & LAN, Name)
        // =====================================================================

        private static GameObject BuildSessionPage(RectTransform parent)
        {
            var page = MakeRect("Page_Session", parent, Vector2.zero, Vector2.one);

            // Left column: Connection settings (0.01 .. 0.62)
            var left = MakeRect("LeftCol", page, new Vector2(0.008f, 0.02f), new Vector2(0.63f, 0.98f));

            // Row 0: Player Name
            AddInputRow(left, 0, 7, "Player Name", CoopConfig.PlayerName.Value, val =>
            {
                CoopConfig.PlayerName.Value = CoopConfig.CleanName(val);
                CoopConfig.ApplyAll(true);
            });

            // Row 1: Network (Steam / LAN)
            _netToggleLabel = AddCycleRow(left, 1, 7, "Network",
                CoopConfig.Network.Value.Equals("lan", StringComparison.OrdinalIgnoreCase) ? "LAN (Direct IP)" : "Steam (No Port Forward)",
                () =>
                {
                    bool toLan = !CoopConfig.Network.Value.Equals("lan", StringComparison.OrdinalIgnoreCase);
                    CoopConfig.Network.Value = toLan ? "lan" : "steam";
                    CoopConfig.Save();
                    RefreshSessionFields();
                });

            // Row 2..5: Steam-specific group
            _steamGroup = MakeRect("SteamGroup", left, Vector2.zero, Vector2.one).gameObject;
            var steamRt = _steamGroup.GetComponent<RectTransform>();

            _steamModeLabel = AddCycleRow(steamRt, 2, 7, "Steam Mode",
                CoopConfig.SteamUsePassword.Value ? "Co-op Password" : "Steam Friends",
                () =>
                {
                    CoopConfig.SteamUsePassword.Value = !CoopConfig.SteamUsePassword.Value;
                    CoopConfig.Save();
                    RefreshSessionFields();
                });

            // Row 3a: Password input
            _steamPwRow = MakeRow(steamRt, 3, 7, "SteamPwRow").gameObject;
            AddInputIntoRow(_steamPwRow.GetComponent<RectTransform>(), "Co-op Password", CoopConfig.SteamPassword.Value, val =>
            {
                CoopConfig.SteamPassword.Value = val ?? "";
                CoopConfig.Save();
                UpdateLobbyCodePreview();
            });

            // Row 3b: Steam Partner input (Friends mode)
            _steamPartnerRow = MakeRow(steamRt, 3, 7, "SteamPartnerRow").gameObject;
            AddInputIntoRow(_steamPartnerRow.GetComponent<RectTransform>(), "Friend / ID64", CoopConfig.SteamPartner.Value, val =>
            {
                CoopConfig.SteamPartner.Value = val ?? "";
                CoopConfig.Save();
            });

            // Row 4: Steam Identity
            _steamIdLabel = AddCycleRow(steamRt, 4, 7, "Steam App",
                CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase) ? "Spacewar (480)" : "Dread Delusion (1574240)",
                () =>
                {
                    bool toSw = !CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
                    CoopConfig.SteamIdentity.Value = toSw ? "spacewar" : "game";
                    CoopConfig.Save();
                    RefreshSessionFields();
                });

            // Row 5 (Steam): Lobby code preview / Scan Friends button
            var codeRow = MakeRow(steamRt, 5, 7, "SteamCodeRow");
            _lobbyCodeText = CreateText(codeRow, "CodeText", "", 17, _goldAccent, TextAnchor.MiddleLeft);
            _lobbyCodeText.rectTransform.anchorMin = new Vector2(0f, 0f);
            _lobbyCodeText.rectTransform.anchorMax = new Vector2(0.64f, 1f);
            _lobbyCodeText.rectTransform.offsetMin = Vector2.zero;
            _lobbyCodeText.rectTransform.offsetMax = Vector2.zero;
            CreateNativeButton(codeRow, "ScanFriends_Btn", "Scan Friends", new Vector2(0.66f, 0.06f), new Vector2(1f, 0.94f), 17, OnScanFriends);

            // Row 2..4: LAN-specific group
            _lanGroup = MakeRect("LanGroup", left, Vector2.zero, Vector2.one).gameObject;
            var lanRt = _lanGroup.GetComponent<RectTransform>();
            AddInputRow(lanRt, 2, 7, "Host IP (Join)", CoopConfig.LanServer.Value, val =>
            {
                CoopConfig.LanServer.Value = string.IsNullOrWhiteSpace(val) ? "127.0.0.1" : val.Trim();
                CoopConfig.Save();
            });
            AddInputRow(lanRt, 3, 7, "UDP Port", CoopConfig.LanPort.Value.ToString(), val =>
            {
                if (int.TryParse(val, out int p) && p >= 1024 && p <= 65534)
                {
                    CoopConfig.LanPort.Value = p;
                    CoopConfig.Save();
                }
            });
            AddInputRow(lanRt, 4, 7, "Bind IP (Host)", CoopConfig.LanBind.Value, val =>
            {
                CoopConfig.LanBind.Value = string.IsNullOrWhiteSpace(val) ? "0.0.0.0" : val.Trim();
                CoopConfig.Save();
            });

            // Row 6: Separate Co-op Saves toggle
            AddToggleRow(left, 6, 7, "Separate Co-op Saves", CoopConfig.SeparateCoopSaves.Value, on =>
            {
                CoopConfig.SeparateCoopSaves.Value = on;
                CoopConfig.Save();
                if (CoopManager.IsActive) CoopManager.SetCoopSaveMode(on);
                SetNote(on ? "Co-op saves stored in Dread Delusion MP1 (single-player untouched)."
                           : "Using your main single-player save folder for co-op.");
            });

            // Right column: Big Host / Join / Disconnect actions + Friends list (0.65 .. 0.99)
            var right = MakeRect("RightCol", page, new Vector2(0.655f, 0.02f), new Vector2(0.992f, 0.98f));

            _hostBtnObj = CreateNativeButton(right, "HostSession_Btn", "Host Session", new Vector2(0f, 0.80f), new Vector2(1f, 0.98f), 22, OnClickHost);
            _joinBtnObj = CreateNativeButton(right, "JoinSession_Btn", "Join Session", new Vector2(0f, 0.59f), new Vector2(1f, 0.77f), 22, OnClickJoin);
            _disconnectBtnObj = CreateNativeButton(right, "Disconnect_Btn", "Stop Session", new Vector2(0f, 0.38f), new Vector2(1f, 0.56f), 20, OnClickDisconnect);

            var friendsBox = MakeRect("FriendsBox", right, new Vector2(0f, 0f), new Vector2(1f, 0.35f));
            var fbBg = friendsBox.gameObject.AddComponent<Image>();
            fbBg.color = new Color(0.04f, 0.04f, 0.06f, 0.55f);
            _friendsScanText = CreateText(friendsBox, "FriendsInfo",
                "Host starts the world.\nJoiner loads any save to\nsync into the host's world.",
                15, _mutedColor, TextAnchor.UpperLeft);
            Stretch(_friendsScanText.rectTransform, 8f, 6f);
            _friendsListRoot = MakeRect("FriendsList", friendsBox, new Vector2(0.03f, 0.04f), new Vector2(0.97f, 0.72f)).gameObject;

            RefreshSessionFields();
            return page.gameObject;
        }

        // =====================================================================
        // Page 1: Rules (Host-Authoritative)
        // =====================================================================

        private static GameObject BuildRulesPage(RectTransform parent)
        {
            var page = MakeRect("Page_Rules", parent, Vector2.zero, Vector2.one);
            var left = MakeRect("RulesLeft", page, new Vector2(0.008f, 0.14f), new Vector2(0.49f, 0.96f));
            var right = MakeRect("RulesRight", page, new Vector2(0.51f, 0.14f), new Vector2(0.992f, 0.96f));

            (_sldMaxStacks, _valMaxStacks) = AddSliderRow(left, 0, 4, "Max Death Stacks", 0, 20, CoopConfig.MaxStacks.Value, true, v =>
            {
                CoopConfig.MaxStacks.Value = Mathf.RoundToInt(v);
                CoopConfig.ApplyAll(true);
            });
            (_sldPctStack, _valPctStack) = AddSliderRow(left, 1, 4, "Loss / Stack (%)", 0, 50, CoopConfig.PercentPerStack.Value, true, v =>
            {
                CoopConfig.PercentPerStack.Value = Mathf.Round(v);
                CoopConfig.ApplyAll(true);
            });
            (_sldRespawnDelay, _valRespawnDelay) = AddSliderRow(left, 2, 4, "Respawn Delay (s)", 0, 30, CoopConfig.RespawnDelaySeconds.Value, true, v =>
            {
                CoopConfig.RespawnDelaySeconds.Value = Mathf.Round(v);
                CoopConfig.ApplyAll(true);
            });

            _togDeathDebuffs = AddToggleRow(right, 0, 4, "Death Debuffs", CoopConfig.DeathDebuffs.Value, v =>
            {
                CoopConfig.DeathDebuffs.Value = v;
                CoopConfig.ApplyAll(true);
            });
            _togRespawnPartner = AddToggleRow(right, 1, 4, "Respawn at Partner", CoopConfig.RespawnAtPartner.Value, v =>
            {
                CoopConfig.RespawnAtPartner.Value = v;
                CoopConfig.ApplyAll(true);
            });
            _togExclusiveShop = AddToggleRow(right, 2, 4, "Exclusive Shop Items", CoopConfig.ExclusiveUniqueShopItems.Value, v =>
            {
                CoopConfig.ExclusiveUniqueShopItems.Value = v;
                CoopConfig.ApplyAll(true);
            });

            var info = CreateText(page, "RulesNote", "In a session, the host's rules are sent to both players automatically.", 16, _mutedColor, TextAnchor.MiddleLeft);
            info.rectTransform.anchorMin = new Vector2(0.01f, 0.01f);
            info.rectTransform.anchorMax = new Vector2(0.70f, 0.12f);
            info.rectTransform.offsetMin = Vector2.zero;
            info.rectTransform.offsetMax = Vector2.zero;

            CreateNativeButton(page, "ResetRules_Btn", "Reset Rules", new Vector2(0.74f, 0.01f), new Vector2(0.992f, 0.12f), 18, () =>
            {
                CoopConfig.ResetRulesToDefault();
                RefreshRulesControls();
                SetNote("Session rules reset to defaults.");
            });

            return page.gameObject;
        }

        // =====================================================================
        // Page 2: Player, Avatar & Voice
        // =====================================================================

        private static GameObject BuildPlayerVoicePage(RectTransform parent)
        {
            var page = MakeRect("Page_Player", parent, Vector2.zero, Vector2.one);
            var left = MakeRect("PlayerLeft", page, new Vector2(0.008f, 0.02f), new Vector2(0.49f, 0.98f));
            var right = MakeRect("PlayerRight", page, new Vector2(0.51f, 0.02f), new Vector2(0.992f, 0.98f));

            // Left column (6 rows): Avatar & Keys
            _lblAvatarModel = AddCycleRow(left, 0, 6, "Avatar Model", ModelLabelOf(CoopConfig.AvatarModel.Value), () =>
            {
                int i = (Array.FindIndex(CoopConfig.ModelValues, x => x.Equals(CoopConfig.AvatarModel.Value, StringComparison.OrdinalIgnoreCase)) + 1) % CoopConfig.ModelValues.Length;
                CoopConfig.AvatarModel.Value = CoopConfig.ModelValues[i];
                CoopConfig.ApplyAll(true);
                _lblAvatarModel.text = CoopConfig.ModelLabels[i];
            });

            _lblAvatarOutfit = AddCycleRow(left, 1, 6, "Avatar Outfit", OutfitLabelOf(CoopConfig.AvatarOutfit.Value), () =>
            {
                int i = (Array.FindIndex(CoopConfig.OutfitValues, x => x.Equals(CoopConfig.AvatarOutfit.Value, StringComparison.OrdinalIgnoreCase)) + 1) % CoopConfig.OutfitValues.Length;
                CoopConfig.AvatarOutfit.Value = CoopConfig.OutfitValues[i];
                CoopConfig.ApplyAll(true);
                _lblAvatarOutfit.text = CoopConfig.OutfitLabels[i];
            });

            _lblTeleportKey = AddCycleRow(left, 2, 6, "Teleport Key", CoopConfig.TeleportKey.Value, () =>
            {
                CoopConfig.TeleportKey.Value = NextKey(CoopConfig.TeleportKey.Value);
                CoopConfig.ApplyAll(true);
                _lblTeleportKey.text = CoopConfig.TeleportKey.Value;
            });

            _togChatEnabled = AddToggleRow(left, 3, 6, "Text Chat", CoopConfig.ChatEnabled.Value, v =>
            {
                CoopConfig.ChatEnabled.Value = v;
                CoopConfig.ApplyAll(true);
            });

            _lblChatKey = AddCycleRow(left, 4, 6, "Chat Key", CoopConfig.ChatKey.Value, () =>
            {
                CoopConfig.ChatKey.Value = NextKey(CoopConfig.ChatKey.Value);
                CoopConfig.ApplyAll(true);
                _lblChatKey.text = CoopConfig.ChatKey.Value;
            });

            _lblVoiceKey = AddCycleRow(left, 5, 6, "Push-to-Talk Key", CoopConfig.VoiceKey.Value, () =>
            {
                CoopConfig.VoiceKey.Value = NextKey(CoopConfig.VoiceKey.Value);
                CoopConfig.ApplyAll(true);
                _lblVoiceKey.text = CoopConfig.VoiceKey.Value;
            });

            // Right column (6 rows): Voice settings
            _togVoiceChat = AddToggleRow(right, 0, 6, "Proximity Voice", CoopConfig.VoiceChat.Value, v =>
            {
                CoopConfig.VoiceChat.Value = v;
                CoopConfig.ApplyAll(true);
            });

            _lblVoiceMode = AddCycleRow(right, 1, 6, "Voice Mode", CoopConfig.VoiceMode.Value == 1 ? "Open Mic" : "Push-to-Talk", () =>
            {
                CoopConfig.VoiceMode.Value = CoopConfig.VoiceMode.Value == 1 ? 0 : 1;
                CoopConfig.ApplyAll(true);
                _lblVoiceMode.text = CoopConfig.VoiceMode.Value == 1 ? "Open Mic" : "Push-to-Talk";
            });

            (_sldVoiceRange, _valVoiceRange) = AddSliderRow(right, 2, 6, "Voice Range (m)", 0, 200, CoopConfig.VoiceRangeM.Value, true, v =>
            {
                CoopConfig.VoiceRangeM.Value = Mathf.RoundToInt(v);
                CoopConfig.ApplyAll(true);
            });

            (_sldVoiceVol, _valVoiceVol) = AddSliderRow(right, 3, 6, "Partner Vol (%)", 0, 200, CoopConfig.VoiceVolumePct.Value, true, v =>
            {
                CoopConfig.VoiceVolumePct.Value = Mathf.RoundToInt(v);
                CoopConfig.ApplyAll(true);
            });

            (_sldVoiceMic, _valVoiceMic) = AddSliderRow(right, 4, 6, "Mic Gain (%)", 0, 300, CoopConfig.VoiceMicPct.Value, true, v =>
            {
                CoopConfig.VoiceMicPct.Value = Mathf.RoundToInt(v);
                CoopConfig.ApplyAll(true);
            });

            (_sldVoiceAway, _valVoiceAway) = AddSliderRow(right, 5, 6, "Away Vol (%)", 0, 100, CoopConfig.VoiceAwayPct.Value, true, v =>
            {
                CoopConfig.VoiceAwayPct.Value = Mathf.RoundToInt(v);
                CoopConfig.ApplyAll(true);
            });

            return page.gameObject;
        }

        // =====================================================================
        // Page 3: Backups & Logs
        // =====================================================================

        private static GameObject BuildBackupsLogsPage(RectTransform parent)
        {
            var page = MakeRect("Page_Backups", parent, Vector2.zero, Vector2.one);

            // Top action buttons
            CreateNativeButton(page, "MakeBackup_Btn", "Backup Co-op Save", new Vector2(0.008f, 0.84f), new Vector2(0.32f, 0.98f), 18, () =>
            {
                var stamp = SaveBackups.MakeBackup("manual", "Created from in-game Co-op menu", out string err);
                SetNote(stamp != null ? "Backup created: " + stamp : "Backup failed: " + err);
                RefreshBackupsList();
            });

            CreateNativeButton(page, "CopySpSave_Btn", "Copy SP Save -> Co-op", new Vector2(0.34f, 0.84f), new Vector2(0.66f, 0.98f), 18, () =>
            {
                SaveBackups.CopySingleplayerToCoop(out string msg);
                SetNote(msg);
                RefreshBackupsList();
            });

            CreateNativeButton(page, "ExportLogs_Btn", "Export Logs (.zip)", new Vector2(0.68f, 0.84f), new Vector2(0.992f, 0.98f), 18, () =>
            {
                LogBundler.ExportAndReveal(out string msg);
                SetNote(msg);
            });

            // Backups list container
            var listFrame = MakeRect("BackupsFrame", page, new Vector2(0.008f, 0.02f), new Vector2(0.992f, 0.80f));
            var bg = listFrame.gameObject.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.04f, 0.06f, 0.60f);
            _backupsListRoot = listFrame.gameObject;

            return page.gameObject;
        }

        // =====================================================================
        // Open / Close / Tabs / Actions
        // =====================================================================

        public static void Open(bool fromPause)
        {
            var menu = global::DelusionMenu.Instance;
            if (menu == null || _coopPanel == null) return;

            _openedFromPause = fromPause;
            _confirmRestoreStamp = "";
            menu.DisableAllPanels();
            _coopPanel.SetActive(true);
            SelectTab(_activeTab);
            RefreshLiveStatus();
            RefreshBackupsList();
        }

        public static void HideOnly()
        {
            if (_coopPanel != null && _coopPanel.activeSelf)
                _coopPanel.SetActive(false);
        }

        public static void Close()
        {
            if (_coopPanel == null) return;
            _coopPanel.SetActive(false);

            var menu = global::DelusionMenu.Instance;
            if (menu == null) return;

            if (_openedFromPause && GameRefs.InWorld)
            {
                if (menu.pauseScreen != null) menu.pauseScreen.SetActive(true);
                menu.activeMenuPanel = global::DelusionMenu.ActiveMenuPanel.root;
            }
            else
            {
                if (menu.titleScreen != null) menu.titleScreen.SetActive(true);
                menu.activeMenuPanel = global::DelusionMenu.ActiveMenuPanel.root;
            }
        }

        public static void SelectTab(int index)
        {
            _activeTab = Math.Clamp(index, 0, 3);
            var titles = new[] { "Co-op", "Co-op — Rules", "Co-op — Player & Voice", "Co-op — Backups" };
            if (_panelTitleText != null) _panelTitleText.text = titles[_activeTab];

            for (int i = 0; i < 4; i++)
            {
                if (_tabPages[i] != null) _tabPages[i].SetActive(i == _activeTab);
                if (_tabButtons[i] != null)
                {
                    var img = _tabButtons[i].GetComponent<Image>();
                    if (img != null) img.color = i == _activeTab ? _activeTabTint : _normalTabTint;
                }
            }

            if (_activeTab == 3) RefreshBackupsList();
        }

        private static void OnClickHost()
        {
            if (CoopManager.StartHost())
            {
                SetNote(GameRefs.InWorld
                    ? "Hosting started! Your partner can join now."
                    : "Hosting started! Click 'Load Save' to enter the world.");
            }
            else
            {
                SetNote("Host failed: " + CoopManager.LastError);
            }
            RefreshLiveStatus();
        }

        private static void OnClickJoin()
        {
            if (CoopManager.StartJoin())
            {
                SetNote(GameRefs.InWorld
                    ? "Joining host..."
                    : "Connecting... Click 'Load Save' (any save) to enter the host's world.");
            }
            else
            {
                SetNote("Join failed: " + CoopManager.LastError);
            }
            RefreshLiveStatus();
        }

        private static void OnClickDisconnect()
        {
            CoopManager.StopSession();
            SetNote("Co-op session stopped (singleplayer mode).");
            RefreshLiveStatus();
        }

        private static void OnClickLoadSave()
        {
            if (GameRefs.InWorld) { Close(); return; }
            var menu = global::DelusionMenu.Instance;
            if (menu == null) return;
            _coopPanel.SetActive(false);
            menu.ActivateLoadScreen();
        }

        private static void OnScanFriends()
        {
            bool sw = CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
            var friends = SteamTransport.QueryFriendsInGame(sw, out string err);
            ClearChildren(_friendsListRoot.transform);
            if (!string.IsNullOrEmpty(err))
            {
                _friendsScanText.text = "Steam: " + err;
                return;
            }
            if (friends.Count == 0)
            {
                _friendsScanText.text = "No Steam friends currently in Dread Delusion.";
                return;
            }
            _friendsScanText.text = $"Friends in game ({friends.Count}) — click to select:";
            int count = Math.Min(friends.Count, 3);
            var rt = _friendsListRoot.GetComponent<RectTransform>();
            for (int i = 0; i < count; i++)
            {
                var f = friends[i];
                float y1 = 1f - i * 0.33f;
                float y0 = y1 - 0.30f;
                CreateNativeButton(rt, "Friend_" + i, f.name, new Vector2(0f, y0), new Vector2(1f, y1), 15, () =>
                {
                    CoopConfig.SteamUsePassword.Value = false;
                    CoopConfig.SteamPartner.Value = f.id.ToString();
                    CoopConfig.Save();
                    RefreshSessionFields();
                    SetNote($"Selected Steam friend: {f.name}");
                });
            }
        }

        private static void RefreshSessionFields()
        {
            bool isLan = CoopConfig.Network.Value.Equals("lan", StringComparison.OrdinalIgnoreCase);
            if (_netToggleLabel != null)
                _netToggleLabel.text = isLan ? "LAN (Direct IP)" : "Steam (No Port Forward)";
            if (_steamGroup != null) _steamGroup.SetActive(!isLan);
            if (_lanGroup != null) _lanGroup.SetActive(isLan);

            bool usePw = CoopConfig.SteamUsePassword.Value;
            if (_steamModeLabel != null) _steamModeLabel.text = usePw ? "Co-op Password" : "Steam Friends";
            if (_steamPwRow != null) _steamPwRow.SetActive(!isLan && usePw);
            if (_steamPartnerRow != null) _steamPartnerRow.SetActive(!isLan && !usePw);

            if (_steamIdLabel != null)
                _steamIdLabel.text = CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase)
                    ? "Spacewar (480)" : "Dread Delusion (1574240)";

            UpdateLobbyCodePreview();
        }

        private static void UpdateLobbyCodePreview()
        {
            if (_lobbyCodeText == null) return;
            if (!CoopConfig.SteamUsePassword.Value)
            {
                _lobbyCodeText.text = "Friends mode: auto-joins friend in game";
                return;
            }
            var pw = CoopConfig.SteamPassword.Value;
            if (string.IsNullOrWhiteSpace(pw))
                _lobbyCodeText.text = "Enter same password on both PCs";
            else
                _lobbyCodeText.text = "Lobby Code: " + SteamTransport.ShortCode(pw);
        }

        private static void RefreshLiveStatus()
        {
            string summary = CoopManager.StatusSummary;
            bool active = CoopManager.IsActive;

            if (_statusBannerText != null)
                _statusBannerText.text = summary;

            if (_titleStatusText != null)
                _titleStatusText.text = active ? "Co-op: " + summary : "";

            if (_disconnectBtnObj != null)
                _disconnectBtnObj.SetActive(active);

            if (_loadSaveBtn != null)
                _loadSaveBtn.SetActive(!GameRefs.InWorld && active);
        }

        private static void RefreshRulesControls()
        {
            if (_togExclusiveShop != null) _togExclusiveShop.SetIsOnWithoutNotify(CoopConfig.ExclusiveUniqueShopItems.Value);
            if (_togDeathDebuffs != null) _togDeathDebuffs.SetIsOnWithoutNotify(CoopConfig.DeathDebuffs.Value);
            if (_togRespawnPartner != null) _togRespawnPartner.SetIsOnWithoutNotify(CoopConfig.RespawnAtPartner.Value);
            if (_sldMaxStacks != null) { _sldMaxStacks.SetValueWithoutNotify(CoopConfig.MaxStacks.Value); _valMaxStacks.text = CoopConfig.MaxStacks.Value.ToString(); }
            if (_sldPctStack != null) { _sldPctStack.SetValueWithoutNotify(CoopConfig.PercentPerStack.Value); _valPctStack.text = Mathf.RoundToInt(CoopConfig.PercentPerStack.Value).ToString(); }
            if (_sldRespawnDelay != null) { _sldRespawnDelay.SetValueWithoutNotify(CoopConfig.RespawnDelaySeconds.Value); _valRespawnDelay.text = Mathf.RoundToInt(CoopConfig.RespawnDelaySeconds.Value).ToString(); }
        }

        private static void RefreshBackupsList()
        {
            if (_backupsListRoot == null) return;
            ClearChildren(_backupsListRoot.transform);
            var rt = _backupsListRoot.GetComponent<RectTransform>();
            var list = SaveBackups.List();
            if (list.Count == 0)
            {
                var empty = CreateText(rt, "Empty", "No co-op save backups yet.\nBackups are created automatically when you Host or Join.", 17, _mutedColor, TextAnchor.MiddleCenter);
                Stretch(empty.rectTransform, 12f, 12f);
                return;
            }

            int show = Math.Min(list.Count, 6);
            for (int i = 0; i < show; i++)
            {
                var b = list[i];
                var row = MakeRow(rt, i, 6, "BkRow_" + i);
                var label = CreateText(row, "Info", $"{b.Made}   [{b.Kind}]   ({b.FileCount} files)", 16, _nativeTextColor, TextAnchor.MiddleLeft);
                label.rectTransform.anchorMin = new Vector2(0.02f, 0f);
                label.rectTransform.anchorMax = new Vector2(0.66f, 1f);
                label.rectTransform.offsetMin = Vector2.zero;
                label.rectTransform.offsetMax = Vector2.zero;

                bool confirming = _confirmRestoreStamp == b.Stamp;
                CreateNativeButton(row, "Restore_" + i, confirming ? "Confirm!" : "Restore", new Vector2(0.68f, 0.08f), new Vector2(0.84f, 0.92f), 15, () =>
                {
                    if (_confirmRestoreStamp != b.Stamp)
                    {
                        _confirmRestoreStamp = b.Stamp;
                        SetNote($"Click 'Confirm!' to restore {b.Made} (current co-op save will be backed up first).");
                        RefreshBackupsList();
                        return;
                    }
                    _confirmRestoreStamp = "";
                    SaveBackups.Restore(b.Stamp, out string msg);
                    SetNote(msg);
                    RefreshBackupsList();
                });

                CreateNativeButton(row, "Delete_" + i, "Delete", new Vector2(0.86f, 0.08f), new Vector2(0.985f, 0.92f), 15, () =>
                {
                    if (SaveBackups.Delete(b.Stamp, out string err))
                        SetNote("Deleted backup " + b.Stamp);
                    else
                        SetNote("Delete failed: " + err);
                    RefreshBackupsList();
                });
            }
        }

        private static void SetNote(string text)
        {
            if (_footerNoteText != null) _footerNoteText.text = text ?? "";
        }

        // =====================================================================
        // Native widget builders (cloning Dread Delusion's exact UI prefabs)
        // =====================================================================

        private static RectTransform MakeRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static RectTransform MakeRow(RectTransform parent, int row, int totalRows, string name)
        {
            float h = 1f / totalRows;
            float y1 = 1f - row * h - 0.01f;
            float y0 = 1f - (row + 1) * h + 0.01f;
            return MakeRect(name, parent, new Vector2(0f, y0), new Vector2(1f, y1));
        }

        private static GameObject CreateNativeButton(RectTransform parent, string name, string label, Vector2 aMin, Vector2 aMax, int fontSize, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var tplImg = _tplButton != null ? _tplButton.GetComponent<Image>() : null;
            if (tplImg != null)
            {
                img.sprite = tplImg.sprite;
                img.type = tplImg.type;
                img.color = tplImg.color;
                img.pixelsPerUnitMultiplier = tplImg.pixelsPerUnitMultiplier;
            }
            else
            {
                img.color = new Color(0.12f, 0.14f, 0.18f, 0.95f);
            }

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var tplBtn = _tplButton != null ? _tplButton.GetComponent<Button>() : null;
            if (tplBtn != null)
            {
                btn.transition = tplBtn.transition;
                btn.colors = tplBtn.colors;
                btn.spriteState = tplBtn.spriteState;
            }
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));

            var txt = CreateText(rt, "Label", label, fontSize, _nativeTextColor, TextAnchor.MiddleCenter);
            txt.raycastTarget = false;
            Stretch(txt.rectTransform, 6f, 2f);
            return go;
        }

        private static void CreateLabelPlate(RectTransform parent, string text, Vector2 aMin, Vector2 aMax)
        {
            var go = new GameObject("Lbl_" + text);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var tplImg = (_tplLabelPlate != null ? _tplLabelPlate.GetComponent<Image>() : null)
                      ?? (_tplButton != null ? _tplButton.GetComponent<Image>() : null);
            if (tplImg != null)
            {
                img.sprite = tplImg.sprite;
                img.type = tplImg.type;
                img.color = tplImg.color;
                img.pixelsPerUnitMultiplier = tplImg.pixelsPerUnitMultiplier;
            }
            else
            {
                img.color = new Color(0.08f, 0.10f, 0.13f, 0.88f);
            }
            img.raycastTarget = false;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var txt = CreateText(rt, "Text", text, 20, _nativeTextColor, TextAnchor.MiddleCenter);
            txt.raycastTarget = false;
            Stretch(txt.rectTransform, 6f, 2f);
        }

        private static Text AddCycleRow(RectTransform parent, int row, int total, string label, string value, Action onClick)
        {
            var r = MakeRow(parent, row, total, "Cycle_" + label);
            CreateLabelPlate(r, label, new Vector2(0f, 0.06f), new Vector2(0.46f, 0.94f));
            var btn = CreateNativeButton(r, "Val_" + label, value, new Vector2(0.48f, 0.06f), new Vector2(1f, 0.94f), 20, onClick);
            return btn.GetComponentInChildren<Text>(true);
        }

        private static void AddInputRow(RectTransform parent, int row, int total, string label, string initial, Action<string> onChanged)
        {
            var r = MakeRow(parent, row, total, "InputRow_" + label);
            AddInputIntoRow(r, label, initial, onChanged);
        }

        private static void AddInputIntoRow(RectTransform row, string label, string initial, Action<string> onChanged)
        {
            CreateLabelPlate(row, label, new Vector2(0f, 0.06f), new Vector2(0.46f, 0.94f));

            // Build a fresh GameObject with the native wide plate's Image style so InputField
            // never conflicts with a deferred-destroyed Button/Selectable on the same GameObject.
            var box = new GameObject("Input_" + label);
            box.transform.SetParent(row, false);
            var img = box.AddComponent<Image>();
            var tplImg = _tplWidePlate != null ? _tplWidePlate.GetComponent<Image>() : null;
            if (tplImg != null)
            {
                img.sprite = tplImg.sprite;
                img.type = tplImg.type;
                img.color = tplImg.color;
                img.pixelsPerUnitMultiplier = tplImg.pixelsPerUnitMultiplier;
            }
            else
            {
                img.color = new Color(0.06f, 0.08f, 0.11f, 0.95f);
            }
            var rt = box.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.48f, 0.06f);
            rt.anchorMax = new Vector2(1f, 0.94f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var textComp = CreateText(rt, "Text", initial ?? "", 20, _nativeTextColor, TextAnchor.MiddleLeft);
            textComp.supportRichText = false;
            Stretch(textComp.rectTransform, 10f, 2f);

            var input = box.AddComponent<InputField>();
            input.targetGraphic = img;
            input.textComponent = textComp;
            input.text = initial ?? "";
            input.caretColor = _goldAccent;
            input.selectionColor = new Color(0.55f, 0.38f, 0.18f, 0.55f);
            input.onEndEdit.AddListener((UnityAction<string>)(v => onChanged?.Invoke(v)));
            input.onValueChanged.AddListener((UnityAction<string>)(v => onChanged?.Invoke(v)));
        }

        private static Toggle AddToggleRow(RectTransform parent, int row, int total, string label, bool initial, Action<bool> onChanged)
        {
            var r = MakeRow(parent, row, total, "ToggleRow_" + label);
            CreateLabelPlate(r, label, new Vector2(0f, 0.06f), new Vector2(0.78f, 0.94f));

            GameObject go;
            if (_tplToggle != null)
            {
                go = UnityEngine.Object.Instantiate(_tplToggle, r, false);
                go.SetActive(true);
            }
            else
            {
                go = new GameObject("Toggle_" + label);
                go.transform.SetParent(r, false);
                go.AddComponent<Toggle>();
            }
            go.name = "Toggle_" + label;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.83f, 0.12f);
            rt.anchorMax = new Vector2(0.97f, 0.88f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var tog = go.GetComponent<Toggle>();
            tog.onValueChanged = new Toggle.ToggleEvent();
            tog.SetIsOnWithoutNotify(initial);
            tog.onValueChanged.AddListener((UnityAction<bool>)(v => onChanged?.Invoke(v)));
            return tog;
        }

        private static (Slider, Text) AddSliderRow(RectTransform parent, int row, int total, string label, float min, float max, float initial, bool wholeNumbers, Action<float> onChanged)
        {
            var r = MakeRow(parent, row, total, "SliderRow_" + label);
            CreateLabelPlate(r, label, new Vector2(0f, 0.06f), new Vector2(0.44f, 0.94f));

            GameObject go;
            if (_tplSlider != null)
            {
                go = UnityEngine.Object.Instantiate(_tplSlider, r, false);
                go.SetActive(true);
            }
            else
            {
                go = new GameObject("Slider_" + label);
                go.transform.SetParent(r, false);
                go.AddComponent<Slider>();
            }
            go.name = "Slider_" + label;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.46f, 0.22f);
            rt.anchorMax = new Vector2(0.85f, 0.78f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var valTxt = CreateText(r, "Val", Mathf.RoundToInt(initial).ToString(), 18, _nativeTextColor, TextAnchor.MiddleCenter);
            valTxt.rectTransform.anchorMin = new Vector2(0.86f, 0f);
            valTxt.rectTransform.anchorMax = new Vector2(1f, 1f);
            valTxt.rectTransform.offsetMin = Vector2.zero;
            valTxt.rectTransform.offsetMax = Vector2.zero;

            var sld = go.GetComponent<Slider>();
            sld.onValueChanged = new Slider.SliderEvent();
            sld.minValue = min;
            sld.maxValue = max;
            sld.wholeNumbers = wholeNumbers;
            sld.SetValueWithoutNotify(initial);
            sld.onValueChanged.AddListener((UnityAction<float>)(v =>
            {
                valTxt.text = Mathf.RoundToInt(v).ToString();
                onChanged?.Invoke(v);
            }));
            return (sld, valTxt);
        }

        private static Text CreateText(RectTransform parent, string name, string content, int fontSize, Color color, TextAnchor align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var t = go.AddComponent<Text>();
            if (_nativeFont != null) t.font = _nativeFont;
            t.text = content;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private static void Stretch(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        private static void SetLabel(GameObject go, string text, int fontSize = 0)
        {
            if (go == null) return;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                if (t == null) continue;
                t.text = text;
                if (fontSize > 0) t.fontSize = fontSize;
            }
        }

        private static void StripLocalisers(GameObject go)
        {
            if (go == null) return;
            foreach (var l in go.GetComponentsInChildren<global::LocalisedMenuText>(true))
                UnityEngine.Object.Destroy(l);
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        private static Transform FindBackdropContainer(Transform root, Transform titleBanner)
        {
            // Often optionsScreen has a single child "OptionsRoot" or "Panel" that holds the stone frame + controls
            if (root.childCount == 1)
                return root.GetChild(0);
            if (titleBanner != null && titleBanner.parent != null && titleBanner.parent != root)
                return titleBanner.parent;
            return root;
        }

        private static bool IsAncestorOf(Transform candidate, Transform child)
        {
            var cur = child;
            while (cur != null)
            {
                if (cur == candidate) return true;
                cur = cur.parent;
            }
            return false;
        }

        private static string ModelLabelOf(string val)
        {
            int i = Array.FindIndex(CoopConfig.ModelValues, x => x.Equals(val, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? CoopConfig.ModelLabels[i] : val;
        }

        private static string OutfitLabelOf(string val)
        {
            int i = Array.FindIndex(CoopConfig.OutfitValues, x => x.Equals(val, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? CoopConfig.OutfitLabels[i] : val;
        }

        private static string NextKey(string cur)
        {
            int i = Array.FindIndex(CoopConfig.KeyChoices, x => x.Equals(cur, StringComparison.OrdinalIgnoreCase));
            return CoopConfig.KeyChoices[(i + 1) % CoopConfig.KeyChoices.Length];
        }
    }

    /// <summary>Esc / Cancel closes the Co-op panel cleanly and returns to the previous menu.</summary>
    [HarmonyPatch(typeof(global::DelusionMenu), nameof(global::DelusionMenu.CancelMenuInput))]
    internal static class Patch_DelusionMenu_Cancel
    {
        private static bool Prefix()
        {
            if (!NativeCoopMenu.IsOpen) return true;
            // If typing inside an InputField, unfocus it first instead of closing the menu
            try
            {
                var sel = EventSystem.current?.currentSelectedGameObject;
                if (sel != null && sel.GetComponent<InputField>() != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                    return false;
                }
            }
            catch { }
            NativeCoopMenu.Close();
            return false;
        }
    }

    /// <summary>Whenever the game hides all menu panels, hide the Co-op panel too.</summary>
    [HarmonyPatch(typeof(global::DelusionMenu), nameof(global::DelusionMenu.DisableAllPanels))]
    internal static class Patch_DelusionMenu_DisableAll
    {
        private static void Postfix()
        {
            NativeCoopMenu.HideOnly();
        }
    }
}
