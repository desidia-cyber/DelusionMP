using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DelusionMP.Game;

namespace DelusionMP.Core
{
    /// <summary>
    /// Co-op save backup & restore manager (in-process port of launcher's backups.rs).
    /// Operates ONLY on the isolated co-op save folder (Dread Delusion MP1); never writes
    /// to the player's single-player save folder.
    /// </summary>
    public static class SaveBackups
    {
        public sealed class BackupEntry
        {
            public string Stamp;
            public string Dir;
            public string Made;
            public string Kind;
            public string Reason;
            public int FileCount;
            public long Bytes;
            public bool Verified;
        }

        private const string ManifestName = "backup.txt";

        public static string BackupsRoot =>
            Path.Combine(BepInEx.Paths.BepInExRootPath, "save-backups");

        public static string SingleplayerSaveDir
        {
            get
            {
                Patch_PersistentDataPath.EnsureInitialized();
                return Patch_PersistentDataPath.OriginalPath;
            }
        }

        public static string CoopSaveDir =>
            Patch_PersistentDataPath.GetCoopPath();

        private static ulong Fnv64(byte[] data)
        {
            ulong h = 0xcbf29ce484222325UL;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= 0x100000001b3UL;
            }
            return h;
        }

        public static List<BackupEntry> List()
        {
            var result = new List<BackupEntry>();
            try
            {
                if (!Directory.Exists(BackupsRoot)) return result;
                foreach (var dir in Directory.GetDirectories(BackupsRoot))
                {
                    var stamp = Path.GetFileName(dir);
                    if (stamp.Length != 15 || stamp[8] != '-') continue;
                    var entry = new BackupEntry
                    {
                        Stamp = stamp,
                        Dir = dir,
                        Made = FormatStamp(stamp),
                        Kind = "backup",
                    };
                    var man = Path.Combine(dir, ManifestName);
                    if (File.Exists(man))
                    {
                        foreach (var raw in File.ReadAllLines(man))
                        {
                            var line = raw.Trim();
                            int eq = line.IndexOf('=');
                            if (eq <= 0 || line.StartsWith(";")) continue;
                            var k = line.Substring(0, eq).Trim();
                            var v = line.Substring(eq + 1).Trim();
                            if (k == "made") entry.Made = v;
                            else if (k == "kind") entry.Kind = v;
                            else if (k == "reason") entry.Reason = v;
                            else if (k == "files" && int.TryParse(v, out var fc)) entry.FileCount = fc;
                            else if (k == "bytes" && long.TryParse(v, out var b)) entry.Bytes = b;
                            else if (k == "verified") entry.Verified = v == "1";
                        }
                    }
                    result.Add(entry);
                }
                result.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));
            }
            catch (Exception e) { Log.Warn("backups list: " + e.Message); }
            return result;
        }

        public static string MakeBackup(string kind, string reason, out string error)
        {
            error = "";
            var srcDir = CoopSaveDir;
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir))
            {
                error = "No co-op save folder exists yet.";
                return null;
            }
            var files = CollectSaveFiles(srcDir);
            if (files.Count == 0)
            {
                error = "Co-op save folder is empty.";
                return null;
            }

            try
            {
                Directory.CreateDirectory(BackupsRoot);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string finalDir = Path.Combine(BackupsRoot, stamp);
                if (Directory.Exists(finalDir))
                    stamp = DateTime.Now.AddSeconds(1).ToString("yyyyMMdd-HHmmss");
                finalDir = Path.Combine(BackupsRoot, stamp);

                string partDir = finalDir + ".part";
                if (Directory.Exists(partDir)) Directory.Delete(partDir, true);
                Directory.CreateDirectory(partDir);

                var sb = new StringBuilder();
                long totalBytes = 0;
                foreach (var rel in files)
                {
                    var src = Path.Combine(srcDir, rel);
                    var dst = Path.Combine(partDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    var data = File.ReadAllBytes(src);
                    File.WriteAllBytes(dst, data);
                    totalBytes += data.Length;
                    sb.AppendLine($"file = {rel} | {data.Length} | {Fnv64(data):X16}");
                }

                var header = new StringBuilder();
                header.AppendLine("; DelusionMP co-op save backup");
                header.AppendLine("version = 1");
                header.AppendLine("stamp = " + stamp);
                header.AppendLine("made = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                header.AppendLine("kind = " + kind);
                header.AppendLine("reason = " + reason);
                header.AppendLine("files = " + files.Count);
                header.AppendLine("bytes = " + totalBytes);
                header.AppendLine("verified = 1");
                header.Append(sb);
                File.WriteAllText(Path.Combine(partDir, ManifestName), header.ToString(), Encoding.UTF8);

                Directory.Move(partDir, finalDir);
                PruneOldBackups(25);
                Log.Info($"backup created: {stamp} ({files.Count} files, {totalBytes} B)");
                return stamp;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        public static bool Restore(string stamp, out string message)
        {
            if (GameRefs.InWorld)
            {
                message = "Return to the Title Screen before restoring a backup.";
                return false;
            }

            var b = List().FirstOrDefault(x => x.Stamp == stamp);
            if (b == null)
            {
                message = "Backup not found: " + stamp;
                return false;
            }

            var target = CoopSaveDir;
            if (string.IsNullOrEmpty(target) || target.Equals(SingleplayerSaveDir, StringComparison.OrdinalIgnoreCase))
            {
                message = "Refused: cannot overwrite the single-player save folder.";
                return false;
            }

            try
            {
                // Verify files listed in manifest
                var entries = ReadManifestFiles(b.Dir, out string verifyErr);
                if (!string.IsNullOrEmpty(verifyErr))
                {
                    message = "Backup verification failed: " + verifyErr;
                    return false;
                }

                // Make a safety backup of current co-op saves first
                string undo = null;
                if (Directory.Exists(target) && CollectSaveFiles(target).Count > 0)
                    undo = MakeBackup("before-restore", "Before restoring " + stamp, out _);

                Directory.CreateDirectory(target);
                foreach (var old in CollectSaveFiles(target))
                {
                    try { File.Delete(Path.Combine(target, old)); } catch { }
                }

                foreach (var rel in entries)
                {
                    var src = Path.Combine(b.Dir, rel);
                    var dst = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }

                try { global::DelusionMenu.Instance?.RefreshSaveButtons(); } catch { }
                message = undo != null
                    ? $"Restored {b.Made}. Previous save backed up as {undo}."
                    : $"Restored {b.Made}.";
                Log.Info("backup restored: " + stamp);
                return true;
            }
            catch (Exception e)
            {
                message = "Restore failed: " + e.Message;
                return false;
            }
        }

        public static bool Delete(string stamp, out string error)
        {
            error = "";
            var b = List().FirstOrDefault(x => x.Stamp == stamp);
            if (b == null) { error = "Backup not found."; return false; }
            try
            {
                Directory.Delete(b.Dir, true);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Copies the player's normal single-player saves into the isolated co-op save folder.</summary>
        public static bool CopySingleplayerToCoop(out string message)
        {
            if (GameRefs.InWorld)
            {
                message = "Return to the Title Screen first.";
                return false;
            }
            var sp = SingleplayerSaveDir;
            var cp = CoopSaveDir;
            if (string.IsNullOrEmpty(sp) || !Directory.Exists(sp))
            {
                message = "No single-player save folder found.";
                return false;
            }
            if (string.IsNullOrEmpty(cp) || cp.Equals(sp, StringComparison.OrdinalIgnoreCase))
            {
                message = "Co-op save folder is not distinct from single-player.";
                return false;
            }
            var files = CollectSaveFiles(sp);
            if (files.Count == 0)
            {
                message = "No single-player save files found to copy.";
                return false;
            }
            try
            {
                if (Directory.Exists(cp) && CollectSaveFiles(cp).Count > 0)
                    MakeBackup("before-copy", "Before copying single-player save", out _);

                Directory.CreateDirectory(cp);
                foreach (var rel in files)
                {
                    var dst = Path.Combine(cp, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(Path.Combine(sp, rel), dst, true);
                }
                try { global::DelusionMenu.Instance?.RefreshSaveButtons(); } catch { }
                message = $"Copied {files.Count} single-player save file(s) to Co-op saves.";
                Log.Info(message);
                return true;
            }
            catch (Exception e)
            {
                message = "Copy failed: " + e.Message;
                return false;
            }
        }

        private static List<string> CollectSaveFiles(string dir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("Player.log", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Player-prev.log", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = Path.GetRelativePath(dir, f);
                if (rel.StartsWith("Crashes", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(rel);
            }
            return list;
        }

        private static List<string> ReadManifestFiles(string dir, out string err)
        {
            err = "";
            var files = new List<string>();
            var man = Path.Combine(dir, ManifestName);
            if (!File.Exists(man)) { err = "backup.txt missing"; return files; }
            foreach (var raw in File.ReadAllLines(man))
            {
                var line = raw.Trim();
                if (!line.StartsWith("file =")) continue;
                var parts = line.Substring(6).Split('|');
                if (parts.Length != 3) continue;
                var rel = parts[0].Trim();
                if (!long.TryParse(parts[1].Trim(), out long size)) continue;
                ulong fnv = Convert.ToUInt64(parts[2].Trim(), 16);
                var full = Path.Combine(dir, rel);
                if (!File.Exists(full)) { err = rel + " is missing"; return files; }
                var data = File.ReadAllBytes(full);
                if (data.Length != size || Fnv64(data) != fnv) { err = rel + " checksum mismatch"; return files; }
                files.Add(rel);
            }
            if (files.Count == 0) err = "no files listed";
            return files;
        }

        private static void PruneOldBackups(int keep)
        {
            try
            {
                var all = List();
                for (int i = keep; i < all.Count; i++)
                {
                    if (!File.Exists(Path.Combine(all[i].Dir, "pinned")))
                        Directory.Delete(all[i].Dir, true);
                }
            }
            catch { }
        }

        private static string FormatStamp(string s) =>
            s.Length == 15
                ? $"{s.Substring(0, 4)}-{s.Substring(4, 2)}-{s.Substring(6, 2)} {s.Substring(9, 2)}:{s.Substring(11, 2)}:{s.Substring(13, 2)}"
                : s;
    }
}
