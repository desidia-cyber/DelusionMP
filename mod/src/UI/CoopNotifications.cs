using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using DelusionMP.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace DelusionMP.UI
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>
    /// Native-styled in-game pop-up notifications (top-center uGUI overlay) for co-op
    /// connection status, errors, and bug markers. Reuses Dread Delusion's own stone-and-gold
    /// 9-sliced UI plate sprite and pixel-serif font so pop-ups blend seamlessly with the
    /// game's existing menus and HUD across all scenes.
    /// </summary>
    public static class CoopNotifications
    {
        private sealed class Toast
        {
            public string Text;
            public ToastKind Kind;
            public float CreatedAt;
            public float Duration;
            public GameObject Root;
            public CanvasGroup Group;
            public RectTransform Rect;
            public Image PlateImg;
            public Image AccentImg;
            public Text Label;
            public bool StyledWithNative;
        }

        private const int MaxVisible = 3;
        private const float FadeIn = 0.22f;
        private const float FadeOut = 0.65f;
        private const float TopMargin = 26f;
        private const float ToastGap = 8f;
        private const float MinWidth = 280f;
        private const float MaxWidth = 740f;

        private static readonly List<Toast> Active = new List<Toast>();
        private static readonly Queue<(string text, ToastKind kind, float duration)> Pending =
            new Queue<(string, ToastKind, float)>();
        private static readonly object Gate = new object();

        // uGUI root
        private static GameObject _canvasObj;
        private static RectTransform _containerRt;

        // Status transition tracking
        private static bool _initialized;
        private static SessionRole _lastRole;
        private static bool _lastConnected;
        private static string _lastSteamError = "";
        private static string _lastSteamStatus = "";
        private static bool _lastInWorld;

        // Native Dread Delusion palette
        private static readonly Color ParchmentText = new Color(0.95f, 0.92f, 0.86f, 1f);
        private static readonly Color FallbackStoneBg = new Color(0.08f, 0.10f, 0.13f, 0.94f);
        private static readonly Color GoldTrim = new Color(0.76f, 0.64f, 0.34f, 1f);

        public static void Show(string message, ToastKind kind = ToastKind.Info, float duration = 5f)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            lock (Gate)
            {
                Pending.Enqueue((message.Trim(), kind, Math.Max(2f, duration)));
            }
        }

        /// <summary>Polls session/transport transitions and updates native uGUI toast cards on the main thread.</summary>
        public static void Tick()
        {
            PollStatusTransitions();
            DrainPending();
            UpdateToasts();
        }

        private static void PollStatusTransitions()
        {
            var s = Session.Instance;
            if (s == null) return;

            var role = s.Role;
            bool connected = s.Connected;
            var steam = CoopManager.ActiveSteam;
            string steamErr = steam?.LastError ?? "";
            string steamStatus = steam?.Status ?? "";
            bool inWorld = GameRefs.InWorld;

            if (!_initialized)
            {
                _initialized = true;
                _lastRole = role;
                _lastConnected = connected;
                _lastSteamError = steamErr;
                _lastSteamStatus = steamStatus;
                _lastInWorld = inWorld;
                return;
            }

            if (!string.IsNullOrEmpty(steamErr) && steamErr != _lastSteamError)
            {
                Show("Co-op Error: " + steamErr, ToastKind.Error, 7.5f);
            }

            if (role != _lastRole)
            {
                if (role == SessionRole.None && _lastRole != SessionRole.None)
                {
                    Show("Co-op: Session stopped (Singleplayer)", ToastKind.Info, 4f);
                }
                else if (role == SessionRole.Host || role == SessionRole.Client)
                {
                    string summary = CoopManager.StatusSummary;
                    if (!summary.StartsWith("Steam error:", StringComparison.OrdinalIgnoreCase))
                        Show("Co-op: " + summary, ToastKind.Info, 5f);
                }
            }

            if (connected != _lastConnected)
            {
                if (connected)
                {
                    string who = PartnerDisplayName();
                    string msg = role == SessionRole.Host
                        ? $"Co-op Connected — {who} joined!"
                        : $"Co-op Connected to {who}!";
                    Show(msg, ToastKind.Success, 5.5f);
                }
                else if (role != SessionRole.None)
                {
                    Show("Co-op: Partner disconnected — waiting to reconnect...", ToastKind.Warning, 6f);
                }
            }

            if (steam != null && string.IsNullOrEmpty(steamErr) && steamStatus != _lastSteamStatus)
            {
                if (steamStatus.IndexOf("code ", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    steamStatus.IndexOf("waiting for partner", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Show("Co-op: " + steamStatus, ToastKind.Info, 5.5f);
                }
                else if (steamStatus.IndexOf("ended the session", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         steamStatus.IndexOf("reconnecting", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Show("Co-op: " + steamStatus, ToastKind.Warning, 5.5f);
                }
            }

            if (inWorld && !_lastInWorld && role != SessionRole.None)
            {
                if (!string.IsNullOrEmpty(steamErr))
                    Show("Co-op Error: " + steamErr, ToastKind.Error, 7f);
                else if (connected)
                    Show($"Co-op OK — Connected to {PartnerDisplayName()}", ToastKind.Success, 5f);
                else
                    Show("Co-op: " + CoopManager.StatusSummary, ToastKind.Info, 5f);
            }

            _lastRole = role;
            _lastConnected = connected;
            _lastSteamError = steamErr;
            _lastSteamStatus = steamStatus;
            _lastInWorld = inWorld;
        }

        private static string PartnerDisplayName()
        {
            string name = PlayerSync.PartnerName;
            if (string.IsNullOrWhiteSpace(name) || name == "Partner")
            {
                var sn = CoopManager.ActiveSteam?.PartnerName;
                if (!string.IsNullOrWhiteSpace(sn)) name = sn;
            }
            return string.IsNullOrWhiteSpace(name) ? "partner" : name;
        }

        private static void DrainPending()
        {
            List<(string text, ToastKind kind, float duration)> batch = null;
            lock (Gate)
            {
                while (Pending.Count > 0)
                    (batch ??= new List<(string, ToastKind, float)>()).Add(Pending.Dequeue());
            }
            if (batch == null) return;

            if (!EnsureCanvas()) return;
            float now = Time.realtimeSinceStartup;

            foreach (var item in batch)
            {
                bool dup = false;
                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    if (Active[i].Text == item.text && now - Active[i].CreatedAt < 2f)
                    {
                        Active[i].CreatedAt = now;
                        Active[i].Duration = Math.Max(Active[i].Duration, item.duration);
                        dup = true;
                        break;
                    }
                }
                if (dup) continue;

                var toast = CreateToastVisual(item.text, item.kind, now, item.duration);
                if (toast != null)
                {
                    Active.Add(toast);
                    while (Active.Count > MaxVisible)
                    {
                        DestroyToast(Active[0]);
                        Active.RemoveAt(0);
                    }
                }
            }
            LayoutToasts();
        }

        private static void UpdateToasts()
        {
            if (Active.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            bool changed = false;
            var nativeFont = ResolveFont();
            var nativePlate = NativeCoopMenu.NativePlateImage;

            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var t = Active[i];
                if (t.Root == null)
                {
                    Active.RemoveAt(i);
                    changed = true;
                    continue;
                }

                float age = now - t.CreatedAt;
                float rem = t.Duration - age;
                if (rem <= 0f)
                {
                    DestroyToast(t);
                    Active.RemoveAt(i);
                    changed = true;
                    continue;
                }

                // Upgrade early-boot toasts to native stone sprite/font once DelusionMenu is injected
                if (!t.StyledWithNative && nativePlate != null && nativeFont != null)
                {
                    ApplyNativeStyle(t, nativePlate, nativeFont);
                    changed = true;
                }

                float alpha = 1f;
                if (age < FadeIn) alpha = Mathf.Clamp01(age / FadeIn);
                else if (rem < FadeOut) alpha = Mathf.Clamp01(rem / FadeOut);
                if (t.Group != null) t.Group.alpha = alpha;
            }

            if (changed) LayoutToasts();
        }

        private static bool EnsureCanvas()
        {
            if (_canvasObj != null && _containerRt != null) return true;
            try
            {
                _canvasObj = new GameObject("DelusionMP_ToastCanvas");
                _canvasObj.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_canvasObj);

                var canvas = _canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 500;

                var scaler = _canvasObj.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                var container = new GameObject("ToastStack");
                container.transform.SetParent(_canvasObj.transform, false);
                _containerRt = container.AddComponent<RectTransform>();
                _containerRt.anchorMin = new Vector2(0.5f, 1f);
                _containerRt.anchorMax = new Vector2(0.5f, 1f);
                _containerRt.pivot = new Vector2(0.5f, 1f);
                _containerRt.anchoredPosition = new Vector2(0f, -TopMargin);
                _containerRt.sizeDelta = new Vector2(MaxWidth, 300f);
                return true;
            }
            catch (Exception e)
            {
                Log.Debug("toast canvas create failed: " + e.Message);
                return false;
            }
        }

        private static Font ResolveFont()
        {
            if (NativeCoopMenu.NativeFont != null) return NativeCoopMenu.NativeFont;
            try
            {
                var noteFont = global::DelusionNote.Instance?.displayText?.font;
                if (noteFont != null) return noteFont;
            }
            catch { }
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (t != null && t.font != null && t.font.name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) < 0)
                        return t.font;
                }
            }
            catch { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { return null; }
        }

        private static Toast CreateToastVisual(string text, ToastKind kind, float now, float duration)
        {
            try
            {
                var go = new GameObject("CoopToast");
                go.transform.SetParent(_containerRt, false);

                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.localScale = Vector3.one;

                var group = go.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;

                // Outer stone frame (cloned from Dread Delusion's native UI plate)
                var plateImg = go.AddComponent<Image>();
                plateImg.raycastTarget = false;

                // Inner subtle status accent strip inside the stone frame's left edge
                var accentGo = new GameObject("StatusAccent");
                accentGo.transform.SetParent(go.transform, false);
                var accentRt = accentGo.AddComponent<RectTransform>();
                accentRt.anchorMin = new Vector2(0f, 0f);
                accentRt.anchorMax = new Vector2(0f, 1f);
                accentRt.pivot = new Vector2(0f, 0.5f);
                accentRt.offsetMin = new Vector2(6f, 6f);
                accentRt.offsetMax = new Vector2(11f, -6f);
                var accentImg = accentGo.AddComponent<Image>();
                accentImg.color = AccentOf(kind);
                accentImg.raycastTarget = false;

                // Native pixel-serif text with drop shadow
                var txtGo = new GameObject("Label");
                txtGo.transform.SetParent(go.transform, false);
                var txtRt = txtGo.AddComponent<RectTransform>();
                txtRt.anchorMin = Vector2.zero;
                txtRt.anchorMax = Vector2.one;
                txtRt.offsetMin = new Vector2(22f, 10f);
                txtRt.offsetMax = new Vector2(-18f, -10f);

                var label = txtGo.AddComponent<Text>();
                label.text = text;
                label.fontSize = 24;
                label.color = TextColorOf(kind);
                label.alignment = TextAnchor.MiddleCenter;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Overflow;
                label.raycastTarget = false;

                var shadow = txtGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.92f);
                shadow.effectDistance = new Vector2(1.5f, -1.5f);

                var toast = new Toast
                {
                    Text = text,
                    Kind = kind,
                    CreatedAt = now,
                    Duration = duration,
                    Root = go,
                    Group = group,
                    Rect = rt,
                    PlateImg = plateImg,
                    AccentImg = accentImg,
                    Label = label,
                };

                var nativePlate = NativeCoopMenu.NativePlateImage;
                var font = ResolveFont();
                if (nativePlate != null && font != null)
                {
                    ApplyNativeStyle(toast, nativePlate, font);
                }
                else
                {
                    // Fallback before DelusionMenu is injected: stone-slate + antique gold outline
                    plateImg.color = FallbackStoneBg;
                    var outline = go.AddComponent<Outline>();
                    outline.effectColor = GoldTrim;
                    outline.effectDistance = new Vector2(2f, -2f);
                    if (font != null) label.font = font;
                }

                SizeToast(toast);
                return toast;
            }
            catch (Exception e)
            {
                Log.Debug("toast visual failed: " + e.Message);
                return null;
            }
        }

        private static void ApplyNativeStyle(Toast t, Image nativePlate, Font nativeFont)
        {
            t.PlateImg.sprite = nativePlate.sprite;
            t.PlateImg.type = nativePlate.type;
            t.PlateImg.pixelsPerUnitMultiplier = nativePlate.pixelsPerUnitMultiplier;
            // Subtle warm-dark stone tint matching Dread Delusion's menu buttons/plates
            t.PlateImg.color = PlateTintOf(t.Kind, nativePlate.color);

            var existingOutline = t.Root.GetComponent<Outline>();
            if (existingOutline != null) UnityEngine.Object.Destroy(existingOutline);

            t.Label.font = nativeFont;
            t.StyledWithNative = true;
            SizeToast(t);
        }

        private static void SizeToast(Toast t)
        {
            if (t.Label == null || t.Rect == null) return;
            // Measure preferred width first, clamped to [MinWidth, MaxWidth]
            float prefW = Mathf.Clamp(t.Label.preferredWidth + 44f, MinWidth, MaxWidth);
            t.Rect.sizeDelta = new Vector2(prefW, 56f);

            // Force layout pass so wrapped preferredHeight accounts for final width
            Canvas.ForceUpdateCanvases();
            float textW = Math.Max(120f, prefW - 40f);
            var settings = t.Label.GetGenerationSettings(new Vector2(textW, 0f));
            float prefH = t.Label.cachedTextGeneratorForLayout.GetPreferredHeight(t.Text, settings) / Math.Max(1f, t.Label.pixelsPerUnit);
            float boxH = Mathf.Max(52f, prefH + 24f);
            t.Rect.sizeDelta = new Vector2(prefW, boxH);
        }

        private static void LayoutToasts()
        {
            float y = 0f;
            for (int i = 0; i < Active.Count; i++)
            {
                var rt = Active[i].Rect;
                if (rt == null) continue;
                rt.anchoredPosition = new Vector2(0f, -y);
                y += rt.sizeDelta.y + ToastGap;
            }
        }

        private static void DestroyToast(Toast t)
        {
            try { if (t?.Root != null) UnityEngine.Object.Destroy(t.Root); } catch { }
        }

        private static Color AccentOf(ToastKind kind) => kind switch
        {
            ToastKind.Success => new Color(0.43f, 0.79f, 0.53f, 0.95f), // antique verdigris green
            ToastKind.Warning => new Color(0.90f, 0.63f, 0.24f, 0.95f), // warm ochre amber
            ToastKind.Error => new Color(0.84f, 0.30f, 0.28f, 0.95f),   // muted crimson
            _ => new Color(0.88f, 0.72f, 0.36f, 0.95f),                 // Dread Delusion gold
        };

        private static Color TextColorOf(ToastKind kind) => kind switch
        {
            ToastKind.Success => new Color(0.84f, 0.96f, 0.86f, 1f),
            ToastKind.Warning => new Color(0.98f, 0.90f, 0.72f, 1f),
            ToastKind.Error => new Color(0.98f, 0.82f, 0.80f, 1f),
            _ => ParchmentText,
        };

        private static Color PlateTintOf(ToastKind kind, Color baseColor)
        {
            // Keep the native stone texture rich and authentic with only a very subtle hue hint
            return kind switch
            {
                ToastKind.Success => Color.Lerp(baseColor, new Color(0.75f, 0.95f, 0.82f, baseColor.a), 0.14f),
                ToastKind.Warning => Color.Lerp(baseColor, new Color(0.98f, 0.86f, 0.65f, baseColor.a), 0.14f),
                ToastKind.Error => Color.Lerp(baseColor, new Color(0.98f, 0.72f, 0.70f, baseColor.a), 0.18f),
                _ => baseColor,
            };
        }
    }
}
