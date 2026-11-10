// ============================================================================
//  File Batch Renamer - core logic (pure .NET, no PowerShell)
//  Core.cs : data model, rename rules, preview engine, two-phase rename engine,
//            undo log I/O and the headless self-test.
//  Target : .NET Framework 4.x, C# 5 syntax (builds with the in-box csc.exe)
//
//  2026-10-19 v2 修复记录（源于独立对抗性验证）：
//   F1  预览只把"本次真的会改名"的文件当作会让出名字的源文件（可移动集合迭代求解）。
//       修复前：目标名撞上批内"不改名"的文件会被误判为安全，执行必然失败，
//       且回滚失败会把文件留成 __rntmp_*.txt。现在这种冲突会在预览阶段就标红拦下。
//   F1b 冲突判定不再随列表顺序漂移（同上，NoChange 项不再占用 used 集合）。
//   F2  目标路径超过 260 字符在预览阶段直接判 Error（.NET Framework 未启用长路径，
//       执行必然抛 PathTooLongException）。
//   回滚 回滚失败不再只报错：先还原原名，若原名已被占用则改存为 <名>(n)<扩展名>，
//       并把结果写进撤销日志，绝不把正常文件留在 __rntmp_* 下。
//   F3a 撤销日志改为"临时文件 + 改名"原子写入；损坏的日志隔离为 .bad 后继续处理更新的记录。
//   F3b 撤销时文件缺失/与记录不符的条目不再被静默吞掉：如实上报，并把未还原的条目
//       回写日志保留，信息不丢。
//   F3c 撤销记录写入文件大小，撤销前做身份比对，避免把用户新建的同名无关文件改成原名。
//   F9  对 null 入参做保护。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FileBatRenamer
{
    // ---------------------------------------------------------------- 数据模型
    public class FileItem
    {
        public string FullName = "";
        public string Dir = "";
        public string Name = "";
        public string Base = "";
        public string Ext = "";
        public long Size;
        public DateTime LastWrite;

        public FileItem() { }

        public FileItem(string path) { SetPath(path); }

        public void SetPath(string path)
        {
            FileInfo fi = new FileInfo(path);
            FullName = fi.FullName;
            Dir = fi.DirectoryName;
            Name = fi.Name;
            Ext = fi.Extension;
            Base = Name.Substring(0, Name.Length - Ext.Length);
            Size = fi.Length;
            LastWrite = fi.LastWriteTime;
        }
    }

    public class RuleSet
    {
        public string Prefix = "";
        public string Suffix = "";
        public string NewExt = "";
        public bool KeepExt = true;
        public string Find = "";
        public string Replace = "";
        public bool UseRegex = false;
        public bool MatchCase = false;
        public bool Number = false;
        public int Start = 1;
        public int Step = 1;
        public int Digits = 2;
        public string NumPos = "prefix";     // prefix | suffix
        public string NumSep = "_";
        public string CaseMode = "keep";     // keep | lower | upper | title
        public int TrimLeft = 0;
        public int MaxLength = 0;
        public bool ResolveDuplicates = false;

        // ---- v3 新增 ----
        public string Template = "";                 // 非空时优先使用模板（{name}{ext}{n}{nn}{date}{parent}）
        public List<ReplaceRule> Replaces = new List<ReplaceRule>();   // 多条查找替换，按顺序应用
        public bool GroupByFolder = false;           // 编号按所在文件夹各自从 Start 开始

        // ---- v3.3 新增：按位置增删（位置从 1 开始，0 = 不启用）----
        public int InsertPos = 0;                    // 在第 N 个字符前插入；N 大于长度时追加到末尾
        public string InsertText = "";               // 要插入的文字
        public int DeletePos = 0;                    // 从第 N 个字符开始删除
        public int DeleteCount = 0;                  // 删除的字符数

        // ---- v4 新增：步骤链 ----
        // Ops 非空时走步骤链，上面的旧字段全部忽略（旧方案载入时会用 OpEngine.FromLegacy 迁移）
        public bool RenameAll = false;               // true = 起始名字来自模板，完全丢弃原名
        public List<Op> Ops = new List<Op>();
    }

    // 一条查找替换规则
    public class ReplaceRule
    {
        public string Find = "";
        public string Replace = "";
        public bool UseRegex = false;
        public bool MatchCase = false;
    }

    public enum PreviewStatus { Ok, NoChange, Conflict, Error }

    public class PreviewItem
    {
        public int Order;
        public FileItem Item;
        public string NewName = "";
        public string Target = "";
        public string Body = "";             // 内部用：不含扩展名的主体
        public string FinalExt = "";         // 内部用：最终扩展名
        public string Message = "";
        public PreviewStatus Status = PreviewStatus.Ok;
    }

    public class Mapping
    {
        public string From = "";
        public string To = "";
        public Mapping() { }
        public Mapping(string from, string to) { From = from; To = to; }
    }

    public class RenameResult
    {
        public int Ok;
        public int Failed;
        public List<string> Errors = new List<string>();
        public List<string> Notes = new List<string>();     // 非致命但需要告诉用户的说明
        public string LogPath = "";
        public List<Mapping> Done = new List<Mapping>();
        public string Message = "";
    }

    // 撤销日志 JSON 结构（与旧版 PowerShell 版兼容：字段名一致）
    public class UndoLog
    {
        public int version { get; set; }
        public string kind { get; set; }
        public string createdAt { get; set; }
        public List<UndoEntry> items { get; set; }
        public UndoLog()
        {
            version = 2;
            kind = "rename";
            createdAt = "";
            items = new List<UndoEntry>();
        }
    }

    public class UndoEntry
    {
        public string From { get; set; }
        public string To { get; set; }
        public long Size { get; set; }        // 改名后的文件大小，用于撤销前身份比对（-1 = 未知）
        public UndoEntry() { From = ""; To = ""; Size = -1; }
        public UndoEntry(string from, string to) { From = from; To = to; Size = -1; }
    }

    // ---------------------------------------------------------------- 核心逻辑
    public static class Core
    {
        public static RuleSet DefaultRules()
        {
            return new RuleSet();
        }

        public static string DefaultLogDir()
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(b)) b = Path.GetTempPath();
            return Path.Combine(b, "FileBatRenamer");
        }

        public static string TestLogDir()
        {
            string custom = Environment.GetEnvironmentVariable("FBR_TESTLOG");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Path.GetTempPath(), "FileBatRenamer");
        }

        // 自然序排序键：2.txt 排在 10.txt 前面
        public static string NaturalKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s, @"\d+", delegate(Match m) { return m.Value.PadLeft(30, '0'); });
        }

        public static string SafeName(string name)
        {
            if (name == null) return "";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++) { if (invalid[j] == c) { bad = true; break; } }
                sb.Append(bad ? '_' : c);
            }
            return sb.ToString();
        }

        public static string FormatSize(long b)
        {
            if (b < 1024L) return b.ToString(CultureInfo.InvariantCulture) + " B";
            if (b < 1024L * 1024L) return string.Format(CultureInfo.InvariantCulture, "{0:N1} KB", b / 1024.0);
            if (b < 1024L * 1024L * 1024L) return string.Format(CultureInfo.InvariantCulture, "{0:N1} MB", b / 1048576.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:N2} GB", b / 1073741824.0);
        }

        public static List<FileItem> GetPathItems(string path, bool recurse)
        {
            int skipped;
            return GetPathItems(path, recurse, out skipped);
        }

        // 读取文件（目录则枚举其中文件；可递归；跳过隐藏/系统文件与重解析点）
        public static List<FileItem> GetPathItems(string path, bool recurse, out int skippedHidden)
        {
            List<FileItem> list = new List<FileItem>();
            skippedHidden = 0;
            if (string.IsNullOrEmpty(path)) return list;
            if (Directory.Exists(path)) Walk(path, recurse, list, 0, ref skippedHidden);
            else if (File.Exists(path))
            {
                try { list.Add(new FileItem(path)); }
                catch { }
            }
            return list;
        }

        private static void Walk(string dir, bool recurse, List<FileItem> outList, int depth, ref int skippedHidden)
        {
            if (depth > 64) return;
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = new string[0]; }
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    FileAttributes at = File.GetAttributes(files[i]);
                    if ((at & FileAttributes.Hidden) != 0 || (at & FileAttributes.System) != 0)
                    {
                        skippedHidden++;
                        continue;
                    }
                    outList.Add(new FileItem(files[i]));
                }
                catch { }
            }
            if (!recurse) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { dirs = new string[0]; }
            for (int i = 0; i < dirs.Length; i++)
            {
                try
                {
                    FileAttributes at = File.GetAttributes(dirs[i]);
                    if ((at & FileAttributes.ReparsePoint) != 0) continue;  // 不跟随符号链接/联接，避免环路
                }
                catch { }
                Walk(dirs[i], true, outList, depth + 1, ref skippedHidden);
            }
        }

        // 依据规则和“物品在列表中的序号”生成新主名（不含扩展名）
        public static string NewBody(FileItem it, RuleSet r, int orderIndex)
        {
            return NewBody(it, r, orderIndex, orderIndex);
        }

        // numberIndex：用于编号的序号（按文件夹分组时是组内序号）
        public static string NewBody(FileItem it, RuleSet r, int orderIndex, int numberIndex)
        {
            if (it == null) return "";
            if (r == null) r = new RuleSet();

            bool hasNewExt = r.NewExt != null && r.NewExt.Trim().Length > 0;
            bool useExt = r.KeepExt || hasNewExt;
            string body = useExt ? it.Base : it.Name;

            // ---- v4：步骤链模式 ----
            // 起始名字 = 原名（或模板），然后按用户排定的顺序一步步改写。
            if (r.Ops != null && r.Ops.Count > 0)
            {
                string cur = r.RenameAll ? ExpandTemplate(r.Template, it, r, numberIndex) : body;
                OpContext ctx = new OpContext();
                ctx.Item = it;
                ctx.Rules = r;
                ctx.NumberIndex = numberIndex;
                for (int k = 0; k < r.Ops.Count; k++)
                    cur = OpEngine.Apply(r.Ops[k], cur, ctx);
                cur = SafeName(cur);
                return cur.TrimEnd(' ', '.');
            }

            bool templateMode = !string.IsNullOrEmpty(r.Template);

            if (templateMode)
            {
                // 模板模式：模板决定整段主体，编号由 {n}/{nn} 占位，不再自动追加编号
                body = ExpandTemplate(r.Template, it, r, numberIndex);
            }
            else
            {
                // 1) 查找替换（多条按顺序；列表为空时回落到单条 Find/Replace）
                body = ApplyReplaces(body, r);

                // 2) 按位置删除一段，再按位置插入文字（先删后插，位置都相对当前主体）
                if (r.DeletePos > 0 && r.DeleteCount > 0)
                    body = RemoveRange(body, r.DeletePos, r.DeleteCount);
                if (r.InsertPos > 0 && !string.IsNullOrEmpty(r.InsertText))
                    body = InsertAtPos(body, r.InsertPos, r.InsertText);

                // 3) 删除开头 N 个字符
                if (r.TrimLeft > 0)
                    body = body.Length > r.TrimLeft ? body.Substring(r.TrimLeft) : "";
            }

            // 4) 最大长度
            if (r.MaxLength > 0 && body.Length > r.MaxLength)
                body = body.Substring(0, r.MaxLength);

            // 5) 大小写
            switch (r.CaseMode)
            {
                case "lower": body = body.ToLowerInvariant(); break;
                case "upper": body = body.ToUpperInvariant(); break;
                case "title":
                    body = Regex.Replace(body, @"[^\s_\-\.]+", delegate(Match m)
                    {
                        string v = m.Value;
                        if (v.Length == 0) return v;
                        return v.Substring(0, 1).ToUpperInvariant() + v.Substring(1).ToLowerInvariant();
                    });
                    break;
            }

            // 6) 编号（严格按列表当前顺序；分组时用组内序号）
            if (r.Number && !templateMode)
            {
                int n = r.Start + numberIndex * r.Step;
                string numText = n.ToString(CultureInfo.InvariantCulture).PadLeft(r.Digits, '0');
                body = r.NumPos == "suffix" ? body + r.NumSep + numText : numText + r.NumSep + body;
            }

            // 7) 前缀 / 后缀
            body = r.Prefix + body + r.Suffix;

            body = SafeName(body);
            body = body.TrimEnd(' ', '.');
            return body;
        }

        // 是否按文件夹分组编号（旧字段 或 步骤链里的编号步骤要求分组）
        public static bool NeedsGrouping(RuleSet r)
        {
            if (r == null) return false;
            if (r.GroupByFolder) return true;
            if (r.Ops == null) return false;
            for (int i = 0; i < r.Ops.Count; i++)
            {
                Op o = r.Ops[i];
                if (o != null && o.Kind == "number" && o.Enabled && o.GroupByFolder) return true;
            }
            return false;
        }

        // 从第 pos 个字符（1 起）开始删掉 count 个字符；越界自动收敛
        private static string RemoveRange(string s, int pos, int count)
        {
            if (s == null || s.Length == 0 || count <= 0) return s;
            int start = pos - 1;
            if (start < 0) start = 0;
            if (start > s.Length) start = s.Length;
            int n = Math.Min(count, s.Length - start);
            if (n <= 0) return s;
            return s.Substring(0, start) + s.Substring(start + n);
        }

        // 在第 pos 个字符（1 起）前插入文字；pos 超过长度时追加到末尾
        private static string InsertAtPos(string s, int pos, string text)
        {
            if (string.IsNullOrEmpty(text)) return s;
            if (s == null) s = "";
            int at = pos - 1;
            if (at < 0) at = 0;
            if (at > s.Length) at = s.Length;
            return s.Substring(0, at) + text + s.Substring(at);
        }

        private static string ApplyReplaces(string body, RuleSet r)
        {
            List<ReplaceRule> rules = r.Replaces;
            if (rules != null && rules.Count > 0)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    ReplaceRule rr = rules[i];
                    if (rr == null || string.IsNullOrEmpty(rr.Find)) continue;
                    body = ApplyOne(body, rr.Find, rr.Replace, rr.UseRegex, rr.MatchCase);
                }
                return body;
            }
            if (!string.IsNullOrEmpty(r.Find))
                body = ApplyOne(body, r.Find, r.Replace, r.UseRegex, r.MatchCase);
            return body;
        }

        private static string ApplyOne(string body, string find, string repl, bool useRegex, bool matchCase)
        {
            RegexOptions opt = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            string rep = repl == null ? "" : repl;
            if (useRegex) return Regex.Replace(body, find, rep, opt);
            return Regex.Replace(body, Regex.Escape(find), rep.Replace("$", "$$"), opt);
        }

        // 模板占位符：{name} 原名主体 / {ext} 扩展名（不含点）/ {n} 编号 / {nn} 补零编号
        //             {date} 当前日期 yyyyMMdd / {parent} 所在文件夹名
        public static string ExpandTemplate(string t, FileItem it, RuleSet r, int numberIndex)
        {
            if (string.IsNullOrEmpty(t)) return "";
            if (r == null) r = new RuleSet();
            int n = r.Start + numberIndex * r.Step;
            string num = n.ToString(CultureInfo.InvariantCulture);
            string ext = it == null ? "" : (it.Ext == null ? "" : it.Ext.TrimStart('.'));
            string parent = "";
            try
            {
                if (it != null && !string.IsNullOrEmpty(it.Dir))
                    parent = Path.GetFileName(it.Dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch { }

            string s = t;
            s = s.Replace("{name}", it == null ? "" : it.Base);
            s = s.Replace("{ext}", ext);
            s = s.Replace("{parent}", parent);
            s = s.Replace("{date}", DateTime.Now.ToString("yyyyMMdd"));
            s = s.Replace("{nn}", num.PadLeft(Math.Max(1, r.Digits), '0'));
            s = s.Replace("{n}", num);
            return s;
        }

        // 预览：Status = Ok | NoChange | Conflict | Error
        //
        // 关键点：只有"本次真的会被改名"的文件才会让出自己的名字。
        // NoChange / Error / 被拦下的 Conflict 项都原地不动，谁都不许占用它们的名字。
        // 由于"谁被拦下"又取决于"谁让位"，这里用可移动集合的迭代求解（只会单调收缩，必然收敛）。
        public static List<PreviewItem> BuildPreview(IList<FileItem> items, RuleSet r)
        {
            List<PreviewItem> list = new List<PreviewItem>();
            if (items == null) return list;
            if (r == null) r = new RuleSet();
            int n = items.Count;

            // ---- 第 1 遍：原始目标名 + 区分"完全不会改名"的项
            Dictionary<string, int> groupSeq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                FileItem it = items[i];
                PreviewItem p = new PreviewItem();
                p.Order = i + 1;
                p.Item = it;
                list.Add(p);

                if (it == null)
                {
                    p.Status = PreviewStatus.Error;
                    p.Message = "条目为空";
                    continue;
                }
                try
                {
                    if (!File.Exists(it.FullName))
                    {
                        p.Status = PreviewStatus.Error;
                        p.Message = "文件不存在";
                        continue;
                    }

                    // 分组编号：每个目录各自从 Start 开始
                    int numIdx = i;
                    if (NeedsGrouping(r))
                    {
                        string gkey = it.Dir == null ? "" : it.Dir;
                        int gcount;
                        groupSeq.TryGetValue(gkey, out gcount);
                        numIdx = gcount;
                        groupSeq[gkey] = gcount + 1;
                    }

                    string body = NewBody(it, r, i, numIdx);
                    bool hasNewExt = r.NewExt != null && r.NewExt.Trim().Length > 0;
                    if (body.Length == 0 && !hasNewExt)
                    {
                        p.Status = PreviewStatus.Error;
                        p.Message = "新文件名为空";
                        continue;
                    }

                    string finalExt = "";
                    if (hasNewExt) finalExt = SafeName("." + r.NewExt.Trim().TrimStart('.'));
                    else if (r.KeepExt) finalExt = it.Ext;

                    string newName = body + finalExt;
                    p.Body = body;
                    p.FinalExt = finalExt;
                    p.NewName = newName;
                    string target = Path.Combine(it.Dir, newName);
                    p.Target = target;

                    if (string.CompareOrdinal(target, it.FullName) == 0)
                    {
                        p.Status = PreviewStatus.NoChange;
                        p.Message = "名称未变化";
                        continue;
                    }
                    if (newName.Length > 255)
                    {
                        p.Status = PreviewStatus.Error;
                        p.Message = "文件名超过 255 字符";
                        continue;
                    }
                    if (target.Length > 259)
                    {
                        p.Status = PreviewStatus.Error;
                        p.Message = "路径超过 260 字符，Windows 会拒绝改名";
                        continue;
                    }
                    // 其余为 Ok 候选，参与下面对移动集合的迭代求解
                }
                catch (Exception ex)
                {
                    p.Status = PreviewStatus.Error;
                    p.Message = ex.Message;
                }
            }

            // ---- 可移动集合迭代求解
            bool[] movable = new bool[n];
            for (int i = 0; i < n; i++) movable[i] = (list[i].Status == PreviewStatus.Ok);

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int iter = 0; iter <= n + 1; iter++)
            {
                HashSet<string> vacating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                    if (movable[i] && items[i] != null && items[i].FullName.Length > 0) vacating.Add(items[i].FullName);

                used.Clear();
                bool changed = false;

                for (int i = 0; i < n; i++)
                {
                    PreviewItem p = list[i];
                    if (p.Status != PreviewStatus.Ok) continue;

                    string target = p.Target;
                    string key = target.ToLowerInvariant();
                    bool dupInList = used.Contains(key);
                    bool occupied = File.Exists(target) && !vacating.Contains(target);

                    if ((dupInList || occupied) && r.ResolveDuplicates)
                    {
                        int k = 1;
                        string cand, candTarget;
                        do
                        {
                            cand = p.Body + "(" + k.ToString(CultureInfo.InvariantCulture) + ")" + p.FinalExt;
                            candTarget = Path.Combine(p.Item.Dir, cand);
                            k++;
                        }
                        while (used.Contains(candTarget.ToLowerInvariant())
                               || (File.Exists(candTarget) && !vacating.Contains(candTarget)));
                        p.NewName = cand;
                        p.Target = candTarget;
                        p.Message = "自动追加序号避免重名";
                        used.Add(candTarget.ToLowerInvariant());
                    }
                    else if (dupInList)
                    {
                        p.Status = PreviewStatus.Conflict;
                        p.Message = "与列表中其他文件重名";
                        used.Add(key);
                        if (movable[i]) { movable[i] = false; changed = true; }
                    }
                    else if (occupied)
                    {
                        p.Status = PreviewStatus.Conflict;
                        p.Message = "目标文件已存在，且该文件本次不会改名（不会让出名字）";
                        used.Add(key);
                        if (movable[i]) { movable[i] = false; changed = true; }
                    }
                    else
                    {
                        used.Add(key);
                    }
                }
                if (!changed) break;
            }

            return list;
        }

        // 两阶段批量重命名：先把源文件改成同目录临时名，再改成目标名
        // —— A→B 且 B→A 的循环换名、交叉覆盖都不会丢文件
        private class TempMove
        {
            public string From = "";
            public string Temp = "";
            public string To = "";
            public bool Done;
        }

        public static RenameResult BatchRename(IList<Mapping> mappings, string logDir, string kind)
        {
            RenameResult res = new RenameResult();
            if (mappings == null) return res;
            List<TempMove> moved = new List<TempMove>();

            // 第一阶段：源文件 → 同目录临时名（解开一切交叉/循环依赖）
            for (int i = 0; i < mappings.Count; i++)
            {
                Mapping m = mappings[i];
                if (m == null || string.IsNullOrEmpty(m.From)) continue;
                if (!File.Exists(m.From))
                {
                    res.Failed++;
                    res.Errors.Add("源文件不存在，已跳过：" + m.From);
                    continue;
                }
                string dir = Path.GetDirectoryName(m.From);
                string ext = Path.GetExtension(m.From);
                TempMove mv = new TempMove();
                mv.From = m.From;
                mv.To = m.To;
                mv.Temp = Path.Combine(dir, "__rntmp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
                try
                {
                    File.Move(m.From, mv.Temp);
                    moved.Add(mv);
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Errors.Add("准备失败：" + m.From + " —— " + ex.Message);
                }
            }

            // 第二阶段：临时名 → 目标名；失败则抢救（绝不把文件留在 __rntmp_* 下）
            for (int i = 0; i < moved.Count; i++)
            {
                TempMove mv = moved[i];
                try
                {
                    File.Move(mv.Temp, mv.To);
                    mv.Done = true;
                    res.Ok++;
                    res.Done.Add(new Mapping(mv.From, mv.To));
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Errors.Add("重命名失败：" + mv.From + " → " + mv.To + " —— " + ex.Message);
                    string recovered = TryRecover(mv, res);
                    if (recovered != null) res.Done.Add(new Mapping(mv.From, recovered));
                }
            }

            if (!string.IsNullOrEmpty(logDir) && !string.IsNullOrEmpty(kind) && res.Done.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(logDir);
                    string name = "undo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json";
                    res.LogPath = Path.Combine(logDir, name);
                    List<UndoEntry> entries = new List<UndoEntry>();
                    for (int i = 0; i < res.Done.Count; i++)
                    {
                        UndoEntry e = new UndoEntry(res.Done[i].From, res.Done[i].To);
                        try { e.Size = new FileInfo(res.Done[i].To).Length; }
                        catch { e.Size = -1; }
                        entries.Add(e);
                    }
                    WriteUndoLog(res.LogPath, kind, entries);
                }
                catch (Exception ex)
                {
                    res.Errors.Add("撤销日志写入失败：" + ex.Message);
                }
            }
            return res;
        }

        // 抢救：先还原原名；原名被占用则改存为 <名>(n)<扩展名>，保证文件始终有可见名字
        private static string TryRecover(TempMove mv, RenameResult res)
        {
            try
            {
                File.Move(mv.Temp, mv.From);
                res.Notes.Add("已回滚为原名：" + Path.GetFileName(mv.From));
                return mv.From;
            }
            catch { }

            try
            {
                // 用"原名 + 序号"而不是目标名：用户一眼能认出这是哪个文件
                string dir = Path.GetDirectoryName(mv.From);
                string baseName = Path.GetFileNameWithoutExtension(mv.From);
                string ext = Path.GetExtension(mv.From);
                for (int k = 1; k <= 999; k++)
                {
                    string cand = Path.Combine(dir, baseName + "(" + k.ToString(CultureInfo.InvariantCulture) + ")" + ext);
                    if (File.Exists(cand)) continue;
                    File.Move(mv.Temp, cand);
                    res.Notes.Add("原名已被占用，文件已改存为 " + Path.GetFileName(cand) + "（内容完整，可手工改名或撤销）");
                    return cand;
                }
            }
            catch { }

            res.Errors.Add("抢救失败，临时文件仍为：" + mv.Temp + "（内容未丢，请手工改名）");
            return null;
        }

        // 原子写入撤销日志（先写临时文件再改名），避免半截 JSON 阻塞后续撤销
        private static void WriteUndoLog(string path, string kind, List<UndoEntry> entries)
        {
            UndoLog log = new UndoLog();
            log.kind = kind;
            log.createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            log.items = entries;
            JavaScriptSerializer js = new JavaScriptSerializer();
            string json = js.Serialize(log);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { }
            }
            File.Move(tmp, path);
        }

        public static List<string> GetUndoLogs(string logDir)
        {
            List<string> logs = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return logs;
                string[] files = Directory.GetFiles(logDir, "undo_*.json");
                Array.Sort(files, StringComparer.Ordinal);
                Array.Reverse(files);           // 文件名含时间戳 => 名称倒序即时间倒序
                logs.AddRange(files);
            }
            catch { }
            return logs;
        }

        private static void QuarantineBadLog(string path)
        {
            try { File.Move(path, path + ".bad"); }
            catch { }
        }

        private static bool EntryIn(List<UndoEntry> list, UndoEntry e)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].To, e.To, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(list[i].From, e.From, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 撤销最近一次（撤销成功后消费掉该条记录，可连续多级回退）
        public static RenameResult Undo(string logDir)
        {
            RenameResult res = new RenameResult();
            List<string> logs = GetUndoLogs(logDir);
            if (logs.Count == 0)
            {
                res.Message = "没有可撤销的记录";
                return res;
            }

            // 损坏的日志隔离为 .bad，不再永久阻塞后续撤销
            JavaScriptSerializer js = new JavaScriptSerializer();
            UndoLog data = null;
            string logFile = null;
            int bad = 0;
            for (int i = 0; i < logs.Count && data == null; i++)
            {
                try
                {
                    data = js.Deserialize<UndoLog>(File.ReadAllText(logs[i], Encoding.UTF8));
                    if (data == null || data.items == null) { data = null; QuarantineBadLog(logs[i]); bad++; continue; }
                    logFile = logs[i];
                }
                catch
                {
                    QuarantineBadLog(logs[i]);
                    bad++;
                }
            }
            if (data == null)
            {
                res.Message = "撤销记录无法解析（已隔离 " + bad + " 条为 .bad）";
                return res;
            }
            return UndoOne(logFile, data);
        }

        // 撤销指定的某一条记录（撤销历史窗口用）
        public static RenameResult UndoAt(string logFile)
        {
            RenameResult res = new RenameResult();
            if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile))
            {
                res.Message = "撤销记录不存在";
                return res;
            }
            JavaScriptSerializer js = new JavaScriptSerializer();
            UndoLog data = null;
            try { data = js.Deserialize<UndoLog>(File.ReadAllText(logFile, Encoding.UTF8)); }
            catch { }
            if (data == null || data.items == null)
            {
                QuarantineBadLog(logFile);
                res.Message = "撤销记录已损坏（已隔离为 .bad）";
                return res;
            }
            return UndoOne(logFile, data);
        }

        private static RenameResult UndoOne(string logFile, UndoLog data)
        {
            RenameResult res = new RenameResult();
            List<UndoEntry> pending = new List<UndoEntry>();
            List<Mapping> map = new List<Mapping>();
            int missing = 0, mismatch = 0;
            for (int i = 0; i < data.items.Count; i++)
            {
                UndoEntry e = data.items[i];
                if (e == null || string.IsNullOrEmpty(e.To)) continue;
                if (!File.Exists(e.To)) { pending.Add(e); missing++; continue; }
                if (e.Size >= 0)
                {
                    long len = -1;
                    try { len = new FileInfo(e.To).Length; }
                    catch { }
                    if (len != e.Size) { pending.Add(e); mismatch++; continue; }   // 身份不符，别动它
                }
                map.Add(new Mapping(e.To, e.From));
            }

            if (map.Count == 0)
            {
                res.Message = "有 " + (missing + mismatch) + " 个文件不存在或与记录不符，本次未做任何改动（撤销记录已保留，可恢复文件后重试）";
                res.Failed = missing + mismatch;
                return res;
            }

            RenameResult run = BatchRename(map, null, null);
            res = run;

            // 未还原的条目（预先跳过的 + 执行失败的）回写日志保留，信息不丢
            List<UndoEntry> remain = new List<UndoEntry>(pending);
            for (int i = 0; i < data.items.Count; i++)
            {
                UndoEntry e = data.items[i];
                if (e == null || string.IsNullOrEmpty(e.To)) continue;
                bool done = false;
                for (int j = 0; j < run.Done.Count; j++)
                    if (string.Equals(run.Done[j].From, e.To, StringComparison.OrdinalIgnoreCase)) { done = true; break; }
                if (!done && !EntryIn(remain, e)) remain.Add(e);
            }

            try
            {
                if (remain.Count == 0) File.Delete(logFile);
                else WriteUndoLog(logFile, string.IsNullOrEmpty(data.kind) ? "rename" : data.kind, remain);
            }
            catch { }

            res.Message = "已回退 " + res.Ok.ToString(CultureInfo.InvariantCulture) + " 个文件"
                          + (remain.Count > 0 ? ("；另有 " + remain.Count + " 个未能还原（撤销记录已保留，可稍后重试）") : "");
            return res;
        }
    }

    // ---------------------------------------------------------------- 无头自检
    public static class SelfTest
    {
        public static void Run(Reporter rep)
        {
            string root = Path.Combine(Path.GetTempPath(), "rn_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string logDir = Path.Combine(root, "_logs");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(Path.Combine(root, "file" + i + ".txt"), "x" + i, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "sub", "a.log"), "a", new UTF8Encoding(false));

            rep.Line("自检目录：" + root);
            try
            {
                rep.Head("1) 文件读取");
                List<FileItem> items = Core.GetPathItems(root, false);
                rep.Check("仅顶层读取 5 个文件", items.Count == 5, "实际 " + items.Count);
                List<FileItem> all = Core.GetPathItems(root, true);
                rep.Check("递归读取 6 个文件", all.Count == 6, "实际 " + all.Count);
                rep.Check("自然序排序键（file10 大于 file2）",
                    string.CompareOrdinal(Core.NaturalKey("file10.txt"), Core.NaturalKey("file2.txt")) > 0, "");

                rep.Head("2) 顺序调整 + 编号");
                List<FileItem> ordered = new List<FileItem>();
                items.Sort(delegate(FileItem x, FileItem y)
                {
                    return string.CompareOrdinal(Core.NaturalKey(x.Name), Core.NaturalKey(y.Name));
                });
                ordered.AddRange(items);
                ordered.Reverse();                       // 模拟用户把顺序整体反转
                string orderNames = "";
                for (int i = 0; i < ordered.Count; i++) { if (i > 0) orderNames += ","; orderNames += ordered[i].Name; }
                rep.Check("列表顺序已反转", orderNames == "file5.txt,file4.txt,file3.txt,file2.txt,file1.txt", orderNames);

                RuleSet r = Core.DefaultRules();
                r.Number = true; r.Start = 1; r.Step = 1; r.Digits = 2; r.Prefix = "IMG_"; r.NumSep = "-";
                List<PreviewItem> prev = Core.BuildPreview(ordered, r);
                string firstNames = "";
                for (int i = 0; i < prev.Count; i++) { if (i > 0) firstNames += ","; firstNames += prev[i].NewName; }
                rep.Check("编号跟随列表顺序",
                    prev[0].NewName == "IMG_01-file5.txt" && prev[4].NewName == "IMG_05-file1.txt", firstNames);

                rep.Head("3) 各类规则");
                RuleSet r2 = Core.DefaultRules(); r2.Find = "file"; r2.Replace = "图片"; r2.NewExt = "jpg";
                rep.Check("查找替换 + 改扩展名",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r2)[0].NewName == "图片5.jpg",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r2)[0].NewName);

                RuleSet r3 = Core.DefaultRules(); r3.UseRegex = true; r3.Find = @"^file(\d+)$"; r3.Replace = "F$1";
                rep.Check("正则捕获组替换",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r3)[0].NewName == "F5.txt",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r3)[0].NewName);

                RuleSet r4 = Core.DefaultRules(); r4.CaseMode = "upper"; r4.Suffix = "_终";
                rep.Check("大小写 + 后缀",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r4)[0].NewName == "FILE5_终.txt",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r4)[0].NewName);

                RuleSet r5 = Core.DefaultRules(); r5.TrimLeft = 4; r5.Prefix = "N";
                rep.Check("删除开头 4 字符",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r5)[0].NewName == "N5.txt",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r5)[0].NewName);

                rep.Head("3b) 按位置增删");
                RuleSet rIns = Core.DefaultRules();
                rIns.InsertPos = 1; rIns.InsertText = "新";
                string nIns1 = Core.BuildPreview(new FileItem[] { ordered[0] }, rIns)[0].NewName;
                rep.Check("在第 1 个字符前插入", nIns1 == "新file5.txt", nIns1);

                RuleSet rIns2 = Core.DefaultRules();
                rIns2.InsertPos = 6; rIns2.InsertText = "_尾";
                string nIns2 = Core.BuildPreview(new FileItem[] { ordered[0] }, rIns2)[0].NewName;
                rep.Check("插入位置超出长度时追加到末尾", nIns2 == "file5_尾.txt", nIns2);

                RuleSet rDel = Core.DefaultRules();
                rDel.DeletePos = 1; rDel.DeleteCount = 4;
                string nDel1 = Core.BuildPreview(new FileItem[] { ordered[0] }, rDel)[0].NewName;
                rep.Check("从第 1 个字符起删除 4 个", nDel1 == "5.txt", nDel1);

                RuleSet rDel2 = Core.DefaultRules();
                rDel2.DeletePos = 3; rDel2.DeleteCount = 99;
                string nDel2 = Core.BuildPreview(new FileItem[] { ordered[0] }, rDel2)[0].NewName;
                rep.Check("删除长度超出末尾时自动收敛", nDel2 == "fi.txt", nDel2);

                RuleSet rMix = Core.DefaultRules();
                rMix.DeletePos = 2; rMix.DeleteCount = 2; rMix.InsertPos = 2; rMix.InsertText = "XY";
                string nMix = Core.BuildPreview(new FileItem[] { ordered[0] }, rMix)[0].NewName;
                rep.Check("先删后插（位置相对删除后的主体）", nMix == "fXYe5.txt", nMix);

                RuleSet rDel0 = Core.DefaultRules();
                rDel0.DeletePos = 0; rDel0.DeleteCount = 3;
                string nDel0 = Core.BuildPreview(new FileItem[] { ordered[0] }, rDel0)[0].NewName;
                rep.Check("位置为 0 时不启用", nDel0 == "file5.txt", nDel0);

                rep.Head("3c) 步骤链（v4 新模型）");

                // 完全重命名：起始名字直接用模板，原名彻底丢弃
                RuleSet rAll = Core.DefaultRules();
                rAll.RenameAll = true;
                rAll.Template = "照片_{nn}";
                rAll.Digits = 3;
                string nAll = Core.BuildPreview(new FileItem[] { ordered[0] }, rAll)[0].NewName;
                rep.Check("完全重命名：模板丢弃原名", nAll == "照片_001.txt", nAll);

                // 步骤顺序真实生效：先插入 A，再把 A 换成 B
                RuleSet rChain = Core.DefaultRules();
                Op opIns2 = OpEngine.Create("insert");
                opIns2.Mode = "start";
                opIns2.Text = "A";
                rChain.Ops.Add(opIns2);
                Op opRep2 = OpEngine.Create("replace");
                opRep2.Find = "A";
                opRep2.Replace = "B";
                rChain.Ops.Add(opRep2);
                string nChain = Core.BuildPreview(new FileItem[] { ordered[0] }, rChain)[0].NewName;
                rep.Check("步骤按顺序执行（先插入再替换）", nChain == "Bfile5.txt", nChain);

                // 编号是独立一步：起始 5、两位、连字符
                RuleSet rNum2 = Core.DefaultRules();
                Op opNum2 = OpEngine.Create("number");
                opNum2.Start = 5; opNum2.Digits = 2; opNum2.Sep = "-"; opNum2.Mode = "suffix";
                rNum2.Ops.Add(opNum2);
                List<PreviewItem> pNum2 = Core.BuildPreview(ordered, rNum2);
                rep.Check("编号步骤跟随列表顺序",
                    pNum2[0].NewName == "file5-05.txt" && pNum2[1].NewName == "file4-06.txt",
                    pNum2[0].NewName + "," + pNum2[1].NewName);

                // 删除指定文字 / 按位置插入 / 截断
                RuleSet rDelText = Core.DefaultRules();
                Op opDelText = OpEngine.Create("remove");
                opDelText.Mode = "text";
                opDelText.Text = "file";
                rDelText.Ops.Add(opDelText);
                string nDelText = Core.BuildPreview(new FileItem[] { ordered[0] }, rDelText)[0].NewName;
                rep.Check("删除指定文字（全部出现处）", nDelText == "5.txt", nDelText);

                RuleSet rInsPos = Core.DefaultRules();
                Op opInsPos = OpEngine.Create("insert");
                opInsPos.Mode = "pos";
                opInsPos.Pos = 3;
                opInsPos.Text = "X";
                rInsPos.Ops.Add(opInsPos);
                string nInsPos = Core.BuildPreview(new FileItem[] { ordered[0] }, rInsPos)[0].NewName;
                rep.Check("按位置插入文字", nInsPos == "fiXle5.txt", nInsPos);

                RuleSet rTrim = Core.DefaultRules();
                Op opTrim = OpEngine.Create("trim");
                opTrim.Mode = "keep-start";
                opTrim.Count = 4;
                rTrim.Ops.Add(opTrim);
                string nTrim = Core.BuildPreview(new FileItem[] { ordered[0] }, rTrim)[0].NewName;
                rep.Check("截断只保留前 N 个字符", nTrim == "file.txt", nTrim);

                // 旧规则迁移成步骤链后，结果必须与旧流水线完全一致
                RuleSet rLegacy = Core.DefaultRules();
                rLegacy.Prefix = "P_";
                rLegacy.Suffix = "_K";
                rLegacy.Number = true;
                rLegacy.Start = 7; rLegacy.Digits = 3; rLegacy.NumSep = "-";
                rLegacy.CaseMode = "upper";
                rLegacy.TrimLeft = 0;
                rLegacy.Replaces.Add(new ReplaceRule { Find = "file", Replace = "doc" });
                string legacyName = Core.BuildPreview(ordered, rLegacy)[0].NewName;

                RuleSet rMigrated = Core.DefaultRules();
                rMigrated.Ops = OpEngine.FromLegacy(rLegacy);
                string migratedName = Core.BuildPreview(ordered, rMigrated)[0].NewName;
                rep.Check("旧规则迁移为步骤链后结果一致",
                    legacyName == migratedName && legacyName == "P_007-DOC5_K.txt",
                    legacyName + " / " + migratedName);

                rep.Head("4) 冲突检测");
                RuleSet r6 = Core.DefaultRules(); r6.UseRegex = true; r6.Find = "^.*$"; r6.Replace = "same";
                List<PreviewItem> p6 = Core.BuildPreview(ordered, r6);
                int conf = 0;
                string detail6 = "";
                for (int i = 0; i < p6.Count; i++)
                {
                    if (p6[i].Status == PreviewStatus.Conflict) conf++;
                    if (i > 0) detail6 += " ";
                    detail6 += p6[i].NewName + ":" + p6[i].Status;
                }
                rep.Check("同目录重名被识别为冲突", conf == 4, detail6);

                RuleSet r6b = Core.DefaultRules(); r6b.UseRegex = true; r6b.Find = "^.*$"; r6b.Replace = "same"; r6b.ResolveDuplicates = true;
                List<PreviewItem> p6b = Core.BuildPreview(ordered, r6b);
                HashSet<string> uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string names6b = "";
                for (int i = 0; i < p6b.Count; i++) { uniq.Add(p6b[i].NewName); if (i > 0) names6b += ","; names6b += p6b[i].NewName; }
                rep.Check("自动加序号去重后 5 个名字唯一", uniq.Count == 5, names6b);

                rep.Head("5) 执行重命名与撤销");
                List<Mapping> map = new List<Mapping>();
                for (int i = 0; i < prev.Count; i++) map.Add(new Mapping(prev[i].Item.FullName, prev[i].Target));
                RenameResult res = Core.BatchRename(map, logDir, "rename");
                rep.Check("重命名成功 5 个", res.Ok == 5 && res.Failed == 0,
                    "Ok=" + res.Ok + " Failed=" + res.Failed + " " + string.Join("; ", res.Errors.ToArray()));
                rep.Check("新文件名全部落盘",
                    File.Exists(Path.Combine(root, "IMG_03-file3.txt")) && File.Exists(Path.Combine(root, "IMG_05-file1.txt")), "文件缺失");
                rep.Check("撤销日志已生成", Directory.GetFiles(logDir, "undo_*.json").Length == 1, "");

                RenameResult undo = Core.Undo(logDir);
                rep.Check("撤销成功 5 个", undo.Ok == 5, "Ok=" + undo.Ok + " " + string.Join("; ", undo.Errors.ToArray()));
                rep.Check("原文件名已恢复",
                    File.Exists(Path.Combine(root, "file3.txt")) && !File.Exists(Path.Combine(root, "IMG_03-file3.txt")), "");
                rep.Check("撤销后记录被消费", Core.GetUndoLogs(logDir).Count == 0, "");

                rep.Head("6) 循环换名（A→B、B→A）");
                string pa = Path.Combine(root, "AAA.txt");
                string pb = Path.Combine(root, "BBB.txt");
                File.WriteAllText(pa, "a", new UTF8Encoding(false));
                File.WriteAllText(pb, "b", new UTF8Encoding(false));
                List<Mapping> swap = new List<Mapping>();
                swap.Add(new Mapping(pa, pb));
                swap.Add(new Mapping(pb, pa));
                RenameResult res2 = Core.BatchRename(swap, logDir, "rename");
                rep.Check("交换式重命名成功",
                    res2.Ok == 2 && File.ReadAllText(pa).Trim() == "b",
                    "Ok=" + res2.Ok + " " + string.Join("; ", res2.Errors.ToArray()));
                RenameResult undo2 = Core.Undo(logDir);
                rep.Check("交换后可撤销", undo2.Ok == 2 && File.ReadAllText(pa).Trim() == "a", "Ok=" + undo2.Ok);

                rep.Head("7) 边界处理与兼容性");
                RuleSet r7 = Core.DefaultRules(); r7.Prefix = "a/b:c*?";
                rep.Check("非法字符被替换为下划线",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r7)[0].NewName == "a_b_c__file5.txt",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r7)[0].NewName);

                RuleSet r8 = Core.DefaultRules(); r8.KeepExt = false; r8.UseRegex = true; r8.Find = "^file"; r8.Replace = "doc";
                rep.Check("不保留扩展名时扩展名参与处理",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r8)[0].NewName == "doc5.txt",
                    Core.BuildPreview(new FileItem[] { ordered[0] }, r8)[0].NewName);

                UndoLog lg = new UndoLog();
                lg.kind = "rename";
                lg.items.Add(new UndoEntry(@"C:\a\b.txt", @"C:\a\c.txt"));
                JavaScriptSerializer js = new JavaScriptSerializer();
                string json = js.Serialize(lg);
                UndoLog back = js.Deserialize<UndoLog>(json);
                rep.Check("撤销日志 JSON 往返一致",
                    back.items.Count == 1 && back.items[0].From == @"C:\a\b.txt" && back.version == 2, json);
                string legacy = "{\"version\":1,\"kind\":\"rename\",\"createdAt\":\"2026-10-19 10:00:00\",\"items\":[{\"From\":\"C:\\\\x\\\\1.txt\",\"To\":\"C:\\\\x\\\\2.txt\"}]}";
                UndoLog backLegacy = js.Deserialize<UndoLog>(legacy);
                rep.Check("可读取旧版 PowerShell 撤销日志",
                    backLegacy.items.Count == 1 && backLegacy.items[0].To == @"C:\x\2.txt", legacy);

                rep.Check("null 入参不崩溃",
                    Core.BuildPreview(null, null).Count == 0 && Core.BatchRename(null, null, null).Ok == 0
                    && Core.BatchRename(new Mapping[] { null }, null, null).Ok == 0, "");

                // ================= 回归：独立验证发现的缺陷 =================
                rep.Head("8) 回归 F1/F1b：目标名撞上批内“不改名”的文件");
                string reg1 = Path.Combine(root, "reg1");
                Directory.CreateDirectory(reg1);
                File.WriteAllText(Path.Combine(reg1, "ab.txt"), "F", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(reg1, "cab.txt"), "G", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(reg1, "ccab.txt"), "H", new UTF8Encoding(false));

                List<FileItem> ord = new List<FileItem>();
                ord.Add(new FileItem(Path.Combine(reg1, "ccab.txt")));
                ord.Add(new FileItem(Path.Combine(reg1, "cab.txt")));
                ord.Add(new FileItem(Path.Combine(reg1, "ab.txt")));      // 未变化项排在最后

                RuleSet rf = Core.DefaultRules();
                rf.UseRegex = true; rf.Find = "^c"; rf.Replace = "";
                List<PreviewItem> pf = Core.BuildPreview(ord, rf);
                rep.Check("阻止模式：两个会撞车的项被标为冲突（不再误判为 Ok）",
                    pf[0].Status == PreviewStatus.Conflict && pf[1].Status == PreviewStatus.Conflict
                    && pf[2].Status == PreviewStatus.NoChange,
                    "status=" + pf[0].Status + "," + pf[1].Status + "," + pf[2].Status);

                rf.ResolveDuplicates = true;
                List<PreviewItem> pf2 = Core.BuildPreview(ord, rf);
                bool noClash = true;
                for (int i = 0; i < pf2.Count; i++)
                    if (pf2[i].Status == PreviewStatus.Ok && string.Equals(pf2[i].NewName, "ab.txt", StringComparison.OrdinalIgnoreCase)) noClash = false;
                rep.Check("自动避让模式：没有任何 Ok 项占用 ab.txt", noClash,
                    "targets=" + pf2[0].NewName + "," + pf2[1].NewName);

                List<Mapping> mf = new List<Mapping>();
                for (int i = 0; i < pf2.Count; i++)
                    if (pf2[i].Status == PreviewStatus.Ok) mf.Add(new Mapping(pf2[i].Item.FullName, pf2[i].Target));
                RenameResult rfRes = Core.BatchRename(mf, logDir, "rename");
                bool residue = false;
                string[] reg1Files = Directory.GetFiles(reg1);
                for (int i = 0; i < reg1Files.Length; i++)
                    if (Path.GetFileName(reg1Files[i]).StartsWith("__rntmp_")) residue = true;
                rep.Check("执行后没有 __rntmp_ 残留", !residue, string.Join(",", reg1Files));
                rep.Check("不改名的 ab.txt 内容未被覆盖",
                    File.ReadAllText(Path.Combine(reg1, "ab.txt")).Trim() == "F",
                    File.ReadAllText(Path.Combine(reg1, "ab.txt")));
                rep.Check("两个待改名文件都成功且内容正确",
                    rfRes.Ok == 2 && File.ReadAllText(Path.Combine(reg1, "cab.txt")).Trim() == "H",
                    "Ok=" + rfRes.Ok + " " + string.Join("; ", rfRes.Errors.ToArray()));
                RenameResult rfUndo = Core.Undo(logDir);
                rep.Check("该场景可完整撤销",
                    rfUndo.Ok == 2 && File.ReadAllText(Path.Combine(reg1, "ccab.txt")).Trim() == "H"
                    && File.ReadAllText(Path.Combine(reg1, "cab.txt")).Trim() == "G",
                    "Ok=" + rfUndo.Ok + " " + rfUndo.Message);

                rep.Head("9) 回归：回滚失败必须抢救出可见名字（不许留 __rntmp_）");
                string reg2 = Path.Combine(root, "reg2");
                string log2 = Path.Combine(root, "_logs2");
                Directory.CreateDirectory(reg2);
                File.WriteAllText(Path.Combine(reg2, "ab.txt"), "F", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(reg2, "cab.txt"), "G", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(reg2, "ccab.txt"), "H", new UTF8Encoding(false));
                // 故意绕过预览，直接给出"目标被不让位的文件占住"的映射，模拟锁定/外部占用
                List<Mapping> bad = new List<Mapping>();
                bad.Add(new Mapping(Path.Combine(reg2, "ccab.txt"), Path.Combine(reg2, "cab.txt")));
                bad.Add(new Mapping(Path.Combine(reg2, "cab.txt"), Path.Combine(reg2, "ab.txt")));
                RenameResult badRes = Core.BatchRename(bad, log2, "rename");
                bool residue2 = false;
                string[] reg2Files = Directory.GetFiles(reg2);
                for (int i = 0; i < reg2Files.Length; i++)
                    if (Path.GetFileName(reg2Files[i]).StartsWith("__rntmp_")) residue2 = true;
                rep.Check("回滚失败时没有留下 __rntmp_ 临时文件", !residue2, string.Join(",", reg2Files));
                rep.Check("失败的那个文件被抢救成可见名字 cab(1).txt 且内容完整",
                    File.Exists(Path.Combine(reg2, "cab(1).txt")) && File.ReadAllText(Path.Combine(reg2, "cab(1).txt")).Trim() == "G",
                    string.Join(",", reg2Files));
                rep.Check("不给位的 ab.txt 未被覆盖", File.ReadAllText(Path.Combine(reg2, "ab.txt")).Trim() == "F", "");
                rep.Check("抢救结果被写进撤销日志（可撤销）", badRes.Done.Count == 2,
                    "Done=" + badRes.Done.Count + " Notes=" + string.Join("; ", badRes.Notes.ToArray()));
                RenameResult badUndo = Core.Undo(log2);
                rep.Check("该场景可完整撤销回原名",
                    File.ReadAllText(Path.Combine(reg2, "ccab.txt")).Trim() == "H"
                    && File.ReadAllText(Path.Combine(reg2, "cab.txt")).Trim() == "G"
                    && !File.Exists(Path.Combine(reg2, "cab(1).txt")),
                    "Ok=" + badUndo.Ok + " " + badUndo.Message);

                rep.Head("10) 回归 F3b/F3a：撤销的失败上报与日志保留");
                string reg3 = Path.Combine(root, "reg3");
                string log3 = Path.Combine(root, "_logs3");
                Directory.CreateDirectory(reg3);
                for (int i = 1; i <= 3; i++) File.WriteAllText(Path.Combine(reg3, "n" + i + ".txt"), "N" + i, new UTF8Encoding(false));
                List<Mapping> m3 = new List<Mapping>();
                for (int i = 1; i <= 3; i++)
                    m3.Add(new Mapping(Path.Combine(reg3, "n" + i + ".txt"), Path.Combine(reg3, "m" + i + ".txt")));
                Core.BatchRename(m3, log3, "rename");
                File.Delete(Path.Combine(reg3, "m2.txt"));         // 手工删掉一个目标
                RenameResult u3 = Core.Undo(log3);
                rep.Check("缺失项如实上报（不谎报全部成功）",
                    u3.Ok == 2 && u3.Message.IndexOf("未能还原") >= 0, "Ok=" + u3.Ok + " Msg=" + u3.Message);
                rep.Check("未还原的条目保留在撤销日志里（信息不丢）",
                    Core.GetUndoLogs(log3).Count == 1, "logs=" + Core.GetUndoLogs(log3).Count);

                // 损坏日志不再永久阻塞
                string log4 = Path.Combine(root, "_logs4");
                Directory.CreateDirectory(log4);
                File.WriteAllText(Path.Combine(log4, "undo_20260101_000000_000.json"), "{ 这不是合法 JSON", new UTF8Encoding(false));
                RenameResult u4 = Core.Undo(log4);
                rep.Check("损坏日志被隔离且不崩溃",
                    Directory.GetFiles(log4, "*.bad").Length == 1, "bad=" + Directory.GetFiles(log4, "*.bad").Length);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
