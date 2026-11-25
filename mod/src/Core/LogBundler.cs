using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DelusionMP.Game;
using DelusionMP.Net;

namespace DelusionMP.Core
{
    /// <summary>
    /// Collects DelusionMP and game logs into a privacy-cleaned zip bundle and reveals it
    /// in Windows Explorer. Replaces DelusionMP-Share-Logs.cmd and launcher's logs.rs.
    /// </summary>
    public static class LogBundler
    {
        private static readonly Regex Ipv4Regex = new Regex(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);

        public static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = Ipv4Regex.Replace(text, m =>
            {
                if (int.TryParse(m.Groups[1].Value, out int a) &&
                    int.TryParse(m.Groups[2].Value, out int b) &&
                    int.TryParse(m.Groups[3].Value, out int c) &&
                    int.TryParse(m.Groups[4].Value, out int d) &&
                    a <= 255 && b <= 255 && c <= 255 && d <= 255)
                {
                    if (a == 127 || (a == 0 && b == 0 && c == 0 && d == 0)) return m.Value;
                    return "<ip>";
                }
                return m.Value;
            });

            t = ReplaceCi(t, Environment.UserName, "<user>");
            t = ReplaceCi(t, Environment.MachineName, "<pc>");
            return t;
        }

        private static string ReplaceCi(string text, string what, string with)
        {
            if (string.IsNullOrEmpty(what) || what.Length < 3) return text;
            return Regex.Replace(text, Regex.Escape(what), with, RegexOptions.IgnoreCase);
        }

        public static string ExportAndReveal(out string message)
        {
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string gameDir = Path.GetFullPath(Path.Combine(BepInEx.Paths.BepInExRootPath, ".."));
                string zipPath = Path.Combine(gameDir, $"DelusionMP-logs-{stamp}.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);

                using (var fs = new FileStream(zipPath, FileMode.Create))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var info = new StringBuilder();
                    info.AppendLine("DelusionMP log bundle");
                    info.AppendLine("time (UTC): " + stamp);
                    info.AppendLine("mod version: " + PluginInfo.Version);
                    info.AppendLine("GameAssembly SHA-256: " + BuildInfo.GameAssemblyHash);
                    info.AppendLine("role: " + Session.Instance.Role + "  connected: " + Session.Instance.Connected);
                    info.AppendLine("OS: " + Environment.OSVersion);
                    AddText(zip, "info.txt", CleanText(info.ToString()));

                    string bep = BepInEx.Paths.BepInExRootPath;
                    AddLogFile(zip, Path.Combine(bep, "LogOutput.log"), "mod/LogOutput.log");
                    AddLogFile(zip, Path.Combine(bep, "ddmp_log.txt"), "mod/ddmp_log.txt");
                    AddLogFile(zip, Path.Combine(bep, "ErrorLog.log"), "mod/ErrorLog.log");
                    AddLogFile(zip, Path.Combine(bep, "config", "delusionmp.coop.cfg"), "mod/delusionmp.coop.cfg");

                    var spDir = Patch_PersistentDataPath.OriginalPath;
                    if (!string.IsNullOrEmpty(spDir))
                    {
                        AddLogFile(zip, Path.Combine(spDir, "Player.log"), "game/Player.log");
                        AddLogFile(zip, Path.Combine(spDir, "Player-prev.log"), "game/Player-prev.log");
                    }
                }

                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
                }
                catch { }

                message = "Logs exported: " + Path.GetFileName(zipPath);
                Log.Info("exported log bundle: " + zipPath);
                return zipPath;
            }
            catch (Exception e)
            {
                message = "Log export failed: " + e.Message;
                Log.Warn(message);
                return null;
            }
        }

        private static void AddLogFile(ZipArchive zip, string srcPath, string entryName)
        {
            if (!File.Exists(srcPath)) return;
            try
            {
                // Open with FileShare.ReadWrite so active log files can be read safely.
                using var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                const long maxBytes = 8 * 1024 * 1024;
                if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
                using var sr = new StreamReader(fs, Encoding.UTF8, true);
                AddText(zip, entryName, CleanText(sr.ReadToEnd()));
            }
            catch { }
        }

        private static void AddText(ZipArchive zip, string entryName, string content)
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var sw = new StreamWriter(e.Open(), Encoding.UTF8);
            sw.Write(content);
        }
    }
}
