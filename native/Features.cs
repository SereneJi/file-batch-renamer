// ============================================================================
//  File Batch Renamer - auxiliary features (presets, undo history, csv, settings)
//  Features.cs
//  Target : .NET Framework 4.x, C# 5 syntax
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FileBatRenamer
{
    public class UndoInfo
    {
        public string Path = "";
        public DateTime Created = DateTime.MinValue;
        public int Count = 0;
        public string Summary = "";

        public string TimeText
        {
            get { return Created == DateTime.MinValue ? "" : Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        }
    }

    public class AppSettings
    {
        public int Width = 1280;
        public int Height = 840;
        public bool Maximized = false;
        public RuleSet Rules = new RuleSet();
        public bool ShowAdvanced = false;
    }

    public static class Features
    {
        private static string dataDir;

        /// <summary>首选 %LOCALAPPDATA%\FileBatRenamer；不可写时回退 %TEMP%，再回退 exe 同目录。</summary>
        public static string DataDir()
        {
            if (dataDir != null) return dataDir;
            string[] candidates = new string[]
            {
                Core.DefaultLogDir(),
                Path.Combine(Path.GetTempPath(), "FileBatRenamer"),
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? ".", "FileBatRenamer.data")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string c = candidates[i];
                try
                {
                    Directory.CreateDirectory(c);
                    string probe = Path.Combine(c, ".write-probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    dataDir = c;
                    return c;
                }
                catch { }
            }
            dataDir = Path.Combine(Path.GetTempPath(), "FileBatRenamer");
            return dataDir;
        }

        public static string PresetDir()
        {
            return Path.Combine(DataDir(), "presets");
        }

        private static string SettingsFile()
        {
            return Path.Combine(DataDir(), "settings.json");
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "preset";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = true;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            return sb.ToString().Trim().TrimEnd('.');
        }

        private static void AtomicWrite(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---------------------------------------------------------- 预设方案
        public static List<string> ListPresets()
        {
            List<string> list = new List<string>();
            try
            {
                string dir = PresetDir();
                if (!Directory.Exists(dir)) return list;
                string[] files = Directory.GetFiles(dir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                    list.Add(Path.GetFileNameWithoutExtension(files[i]));
            }
            catch { }
            return list;
        }

        public static bool SavePreset(string name, RuleSet rules)
        {
            if (rules == null) return false;
            name = SafeFileName(name);
            if (name.Length == 0) return false;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                AtomicWrite(Path.Combine(PresetDir(), name + ".json"), js.Serialize(rules));
                return true;
            }
            catch { return false; }
        }

        public static RuleSet LoadPreset(string name)
        {
            try
            {
                string p = Path.Combine(PresetDir(), SafeFileName(name) + ".json");
                if (!File.Exists(p)) return null;
                JavaScriptSerializer js = new JavaScriptSerializer();
                return js.Deserialize<RuleSet>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static void DeletePreset(string name)
        {
            try
            {
                string p = Path.Combine(PresetDir(), SafeFileName(name) + ".json");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        // ---------------------------------------------------------- 撤销历史
        public static List<UndoInfo> GetUndoHistory(string logDir)
        {
            List<UndoInfo> list = new List<UndoInfo>();
            List<string> logs = Core.GetUndoLogs(logDir);
            JavaScriptSerializer js = new JavaScriptSerializer();
            for (int i = 0; i < logs.Count; i++)
            {
                UndoInfo info = new UndoInfo();
                info.Path = logs[i];
                try
                {
                    UndoLog data = js.Deserialize<UndoLog>(File.ReadAllText(logs[i], Encoding.UTF8));
                    if (data != null)
                    {
                        info.Count = data.items == null ? 0 : data.items.Count;
                        DateTime t;
                        if (!string.IsNullOrEmpty(data.createdAt)
                            && DateTime.TryParse(data.createdAt, CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out t))
                            info.Created = t;
                        if (data.items != null && data.items.Count > 0 && data.items[0] != null)
                        {
                            string dir = Path.GetDirectoryName(data.items[0].To ?? "");
                            if (!string.IsNullOrEmpty(dir)) info.Summary = dir;
                        }
                    }
                }
                catch { }
                if (info.Created == DateTime.MinValue)
                {
                    try { info.Created = File.GetLastWriteTime(logs[i]); } catch { }
                }
                list.Add(info);
            }
            return list;
        }

        // ---------------------------------------------------------- 导出清单
        public static bool ExportCsv(string path, List<PreviewItem> items, out string error)
        {
            error = "";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("\uFEFF序号,原文件名,新文件名,所在文件夹,大小(字节),状态,说明");
                for (int i = 0; i < items.Count; i++)
                {
                    PreviewItem p = items[i];
                    if (p == null) continue;
                    string status;
                    switch (p.Status)
                    {
                        case PreviewStatus.Ok: status = "将改名"; break;
                        case PreviewStatus.NoChange: status = "未变化"; break;
                        case PreviewStatus.Conflict: status = "冲突"; break;
                        default: status = "错误"; break;
                    }
                    sb.Append(p.Order.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(Csv(p.Item == null ? "" : p.Item.Name)).Append(',');
                    sb.Append(Csv(p.NewName)).Append(',');
                    sb.Append(Csv(p.Item == null ? "" : p.Item.Dir)).Append(',');
                    sb.Append(p.Item == null ? "0" : p.Item.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(Csv(status)).Append(',');
                    sb.Append(Csv(p.Message)).AppendLine();
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Csv(string v)
        {
            if (v == null) return "";
            if (v.IndexOf('"') >= 0 || v.IndexOf(',') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        // ---------------------------------------------------------- 设置持久化
        public static AppSettings LoadSettings()
        {
            try
            {
                string p = SettingsFile();
                if (!File.Exists(p)) return new AppSettings();
                JavaScriptSerializer js = new JavaScriptSerializer();
                AppSettings s = js.Deserialize<AppSettings>(File.ReadAllText(p, Encoding.UTF8));
                if (s == null) return new AppSettings();
                if (s.Rules == null) s.Rules = new RuleSet();
                if (s.Rules.Replaces == null) s.Rules.Replaces = new List<ReplaceRule>();
                // 旧版本或测试可能留下「全名重写 + 空模板」，会生成空文件名，这里兜底回保留原名
                if (s.Rules.RenameAll && string.IsNullOrEmpty(s.Rules.Template)) s.Rules.RenameAll = false;
                if (s.Width < 1120) s.Width = 1280;
                if (s.Height < 760) s.Height = 840;
                return s;
            }
            catch { return new AppSettings(); }
        }

        public static void SaveSettings(AppSettings s)
        {
            if (s == null) return;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                AtomicWrite(SettingsFile(), js.Serialize(s));
            }
            catch { }
        }
    }
}

