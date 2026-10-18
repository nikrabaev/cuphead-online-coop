using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Copies Cuphead's save files to BepInEx/CupheadOnline/SaveBackups/&lt;date-time&gt;/ when an online session
    /// starts, keeping the newest few. Online play writes the host's slot exactly like local co-op; this is a cheap
    /// safety net in case anything goes wrong. A backup identical to the newest one is skipped.
    /// To restore: quit Cuphead and copy a backup's .sav files back into the save folder (logged on each backup).
    /// </summary>
    public static class SaveBackups
    {
        public static string BackupRoot
        {
            get { return Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "CupheadOnline"), "SaveBackups"); }
        }

        /// <summary>The folder the game itself saves to (OnlineInterfaceSteam.SavePath), or the same rule rebuilt.</summary>
        public static string SaveFolder
        {
            get
            {
                try
                {
                    object iface = OnlineManager.Instance != null ? OnlineManager.Instance.Interface : null;
                    if (iface != null)
                    {
                        System.Reflection.PropertyInfo p = AccessTools.Property(iface.GetType(), "SavePath");
                        string path = p != null ? p.GetValue(iface, null) as string : null;
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
                catch (Exception) { } // fall back below
                if (Application.platform == RuntimePlatform.OSXPlayer)
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Library/Application Support/unity.Studio MDHR.Cuphead/Cuphead/");
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cuphead");
            }
        }

        public static void BackUp()
        {
            if (!Plugin.BackupSaves.Value) return;
            try
            {
                string folder = SaveFolder;
                if (!Directory.Exists(folder)) return;
                string[] saves = Directory.GetFiles(folder, "*.sav");
                if (saves.Length == 0) return;
                Directory.CreateDirectory(BackupRoot);
                if (SameAsNewest(saves))
                {
                    Plugin.Log.LogInfo("Saves unchanged since the last backup");
                    return;
                }
                string target = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(target);
                foreach (string save in saves) File.Copy(save, Path.Combine(target, Path.GetFileName(save)), true);
                Prune();
                Plugin.Log.LogInfo("Backed up " + saves.Length + " save file(s) from " + folder + " to " + target);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not back up the saves: " + ex.Message);
            }
        }

        private static bool SameAsNewest(string[] saves)
        {
            List<string> backups = Backups();
            if (backups.Count == 0) return false;
            string newest = backups[backups.Count - 1];
            if (Directory.GetFiles(newest, "*.sav").Length != saves.Length) return false;
            foreach (string save in saves)
            {
                string copy = Path.Combine(newest, Path.GetFileName(save));
                if (!File.Exists(copy) || !SameBytes(File.ReadAllBytes(save), File.ReadAllBytes(copy))) return false;
            }
            return true;
        }

        /// <summary>Backup folders, oldest first (their names sort by time).</summary>
        private static List<string> Backups()
        {
            List<string> folders = new List<string>(Directory.GetDirectories(BackupRoot));
            folders.Sort(StringComparer.Ordinal);
            return folders;
        }

        private static void Prune()
        {
            List<string> folders = Backups();
            int keep = Mathf.Max(1, Plugin.BackupsToKeep.Value);
            for (int i = 0; i < folders.Count - keep; i++)
            {
                try { Directory.Delete(folders[i], true); }
                catch (Exception ex) { Plugin.Log.LogWarning("Could not remove old backup " + folders[i] + ": " + ex.Message); }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
