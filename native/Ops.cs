using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FileBatRenamer
{
    // 一步处理：字段全部是可序列化的简单值，Kind 决定这一步做什么。
    // 设计原则：一步只做一件事，步骤可以增删、上下移动、临时停用；
    // 名字按步骤顺序依次被改写，所以“先编号还是先替换”这种自由是真实的。
    public class Op
    {
        public string Kind = "replace";
        public bool Enabled = true;

        // replace
        public string Find = "";
        public string Replace = "";
        public bool UseRegex;
        public bool MatchCase;

        // remove / insert / trim / number / date 的位置
        public string Mode = "";
        public int Pos;          // 1 起
        public int Count;        // 删除个数 / 保留长度
        public string Text = ""; // 要插入或删除的文字

        // number
        public int Start = 1;
        public int Step = 1;
        public int Digits = 3;
        public string Sep = "_";
        public bool GroupByFolder;

        // date
        public string Format = "yyyyMMdd";

        // replace：一个步骤里可以放多条替换规则，按顺序逐条应用
        public List<ReplaceRule> Pairs = new List<ReplaceRule>();

        public Op Clone()
        {
            Op o = (Op)MemberwiseClone();
            if (Pairs != null)
            {
                o.Pairs = new List<ReplaceRule>();
                for (int i = 0; i < Pairs.Count; i++)
                {
                    ReplaceRule rr = Pairs[i];
                    ReplaceRule cp = new ReplaceRule();
                    if (rr != null) { cp.Find = rr.Find; cp.Replace = rr.Replace; cp.UseRegex = rr.UseRegex; cp.MatchCase = rr.MatchCase; }
                    o.Pairs.Add(cp);
                }
            }
            return o;
        }
    }

    public class OpContext
    {
        public FileItem Item;
        public RuleSet Rules;
        public int NumberIndex;
        public DateTime Now = DateTime.Now;
    }

    // 步骤引擎：纯函数式（输入名字 → 输出名字），便于自检
    public static class OpEngine
    {
        public static readonly string[] Kinds = new string[]
        {
            "replace", "remove", "insert", "case", "trim", "number", "date"
        };

        public static string KindName(string kind)
        {
            switch (kind)
            {
                case "remove": return "删除文字";
                case "insert": return "插入文字";
                case "case": return "大小写";
                case "trim": return "截断长度";
                case "number": return "编号";
                case "date": return "日期";
                default: return "替换文字";
            }
        }

        public static string KindIcon(string kind)
        {
            switch (kind)
            {
                case "remove": return "－";
                case "insert": return "＋";
                case "case": return "Aa";
                case "trim": return "…";
                case "number": return "#";
                case "date": return "日";
                default: return "⇄";
            }
        }

        // 新建一步，默认值都是“最常用又不会误伤”的那种
        public static Op Create(string kind)
        {
            Op o = new Op();
            o.Kind = kind;
            switch (kind)
            {
                case "remove": o.Mode = "pos"; o.Pos = 1; o.Count = 1; break;
                case "insert": o.Mode = "start"; o.Pos = 1; o.Text = ""; break;
                case "case": o.Mode = "lower"; break;
                case "trim": o.Mode = "keep-start"; o.Count = 20; break;
                case "number": o.Mode = "suffix"; o.Start = 1; o.Step = 1; o.Digits = 3; o.Sep = "_"; break;
                case "date": o.Mode = "suffix"; o.Format = "yyyyMMdd"; o.Sep = "_"; break;
                default: o.Kind = "replace"; break;
            }
            return o;
        }

        public static string Apply(Op op, string body, OpContext ctx)
        {
            if (op == null || body == null) return body == null ? "" : body;
            if (!op.Enabled) return body;
            if (ctx == null) ctx = new OpContext();

            switch (op.Kind)
            {
                case "replace": return DoReplace(op, body);
                case "remove": return DoRemove(op, body);
                case "insert": return DoInsert(op, body);
                case "case": return DoCase(op, body);
                case "trim": return DoTrim(op, body);
                case "number": return DoNumber(op, body, ctx);
                case "date": return DoDate(op, body, ctx);
                default: return body;
            }
        }

        private static RegexOptions Opt(bool matchCase)
        {
            return matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        }

        private static string DoReplace(Op op, string body)
        {
            if (op.Pairs != null && op.Pairs.Count > 0)
            {
                string cur = body;
                for (int i = 0; i < op.Pairs.Count; i++)
                {
                    ReplaceRule rr = op.Pairs[i];
                    if (rr == null || string.IsNullOrEmpty(rr.Find)) continue;
                    cur = ReplaceOne(cur, rr.Find, rr.Replace, rr.UseRegex, rr.MatchCase);
                }
                return cur;
            }
            if (string.IsNullOrEmpty(op.Find)) return body;
            return ReplaceOne(body, op.Find, op.Replace, op.UseRegex, op.MatchCase);
        }

        private static string ReplaceOne(string body, string find, string replace, bool useRegex, bool matchCase)
        {
            string rep = replace == null ? "" : replace;
            if (useRegex)
            {
                try { return Regex.Replace(body, find, rep, Opt(matchCase)); }
                catch (ArgumentException) { return body; }
            }
            return Regex.Replace(body, Regex.Escape(find), rep.Replace("$", "$$"), Opt(matchCase));
        }

        private static string DoRemove(Op op, string body)
        {
            switch (op.Mode)
            {
                case "start":
                    if (op.Count <= 0) return body;
                    return body.Length > op.Count ? body.Substring(op.Count) : "";

                case "end":
                    if (op.Count <= 0) return body;
                    return body.Length > op.Count ? body.Substring(0, body.Length - op.Count) : "";

                case "text":
                    if (string.IsNullOrEmpty(op.Text)) return body;
                    return Regex.Replace(body, Regex.Escape(op.Text), "", Opt(op.MatchCase));

                default:
                    {
                        if (op.Count <= 0) return body;
                        int start = op.Pos - 1;
                        if (start < 0) start = 0;
                        if (start > body.Length) start = body.Length;
                        int n = Math.Min(op.Count, body.Length - start);
                        if (n <= 0) return body;
                        return body.Substring(0, start) + body.Substring(start + n);
                    }
            }
        }

        private static string DoInsert(Op op, string body)
        {
            string t = op.Text == null ? "" : op.Text;
            if (t.Length == 0) return body;
            switch (op.Mode)
            {
                case "end": return body + t;
                case "pos":
                    {
                        int at = op.Pos - 1;
                        if (at < 0) at = 0;
                        if (at > body.Length) at = body.Length;
                        return body.Substring(0, at) + t + body.Substring(at);
                    }
                default: return t + body;
            }
        }

        private static string DoCase(Op op, string body)
        {
            switch (op.Mode)
            {
                case "upper": return body.ToUpperInvariant();
                case "lower": return body.ToLowerInvariant();
                case "title":
                    return Regex.Replace(body, @"[^\s_\-\.]+", delegate(Match m)
                    {
                        string v = m.Value;
                        if (v.Length == 0) return v;
                        return v.Substring(0, 1).ToUpperInvariant() + v.Substring(1).ToLowerInvariant();
                    });
                case "sentence":
                    {
                        string s = body.ToLowerInvariant();
                        if (s.Length == 0) return s;
                        return s.Substring(0, 1).ToUpperInvariant() + s.Substring(1);
                    }
                default: return body;
            }
        }

        private static string DoTrim(Op op, string body)
        {
            if (op.Count <= 0 || body.Length <= op.Count) return body;
            if (op.Mode == "keep-end") return body.Substring(body.Length - op.Count);
            return body.Substring(0, op.Count);
        }

        private static string DoNumber(Op op, string body, OpContext ctx)
        {
            int idx = ctx.NumberIndex < 0 ? 0 : ctx.NumberIndex;
            int n = op.Start + idx * op.Step;
            string num = n.ToString(CultureInfo.InvariantCulture);
            int d = op.Digits;
            if (d > num.Length) num = num.PadLeft(d, '0');
            string sep = op.Sep == null ? "" : op.Sep;
            return op.Mode == "prefix" ? num + sep + body : body + sep + num;
        }

        private static string DoDate(Op op, string body, OpContext ctx)
        {
            string fmt = string.IsNullOrEmpty(op.Format) ? "yyyyMMdd" : op.Format;
            string text;
            try { text = ctx.Now.ToString(fmt, CultureInfo.InvariantCulture); }
            catch (FormatException) { text = ctx.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture); }
            string sep = op.Sep == null ? "" : op.Sep;
            return op.Mode == "prefix" ? text + sep + body : body + sep + text;
        }

        // 步骤列表里显示的一行说明
        private static string OnePair(ReplaceRule rr)
        {
            if (rr == null) return "（空规则）";
            string f = rr.Find == null ? "" : rr.Find;
            string r = rr.Replace == null ? "" : rr.Replace;
            if (f.Length > 16) f = f.Substring(0, 16) + "…";
            if (r.Length > 16) r = r.Substring(0, 16) + "…";
            return "「" + f + "」→「" + r + "」" + (rr.UseRegex ? "（正则）" : "");
        }

        public static string Describe(Op op)
        {
            if (op == null) return "";
            switch (op.Kind)
            {
                case "replace":
                    {
                        if (op.Pairs != null && op.Pairs.Count > 0)
                        {
                            if (op.Pairs.Count == 1) return OnePair(op.Pairs[0]);
                            string first = OnePair(op.Pairs[0]);
                            return op.Pairs.Count + " 条替换 · " + first;
                        }
                        if (string.IsNullOrEmpty(op.Find)) return "还没填写要查找的文字";
                        string f = op.Find.Length > 18 ? op.Find.Substring(0, 18) + "…" : op.Find;
                        string r = op.Replace == null ? "" : op.Replace;
                        string rr = r.Length > 18 ? r.Substring(0, 18) + "…" : r;
                        return "「" + f + "」→「" + rr + "」" + (op.UseRegex ? "（正则）" : "");
                    }

                case "remove":
                    if (op.Mode == "start") return "删除开头 " + op.Count + " 个字符";
                    if (op.Mode == "end") return "删除结尾 " + op.Count + " 个字符";
                    if (op.Mode == "text") return string.IsNullOrEmpty(op.Text) ? "还没填写要删除的文字" : "删除所有「" + op.Text + "」";
                    return "删除第 " + op.Pos + " 位起的 " + op.Count + " 个字符";

                case "insert":
                    if (string.IsNullOrEmpty(op.Text)) return "还没填写要插入的文字";
                    if (op.Mode == "start") return "在最前面加上「" + op.Text + "」";
                    if (op.Mode == "end") return "在最后加上「" + op.Text + "」";
                    return "在第 " + op.Pos + " 位前插入「" + op.Text + "」";

                case "case":
                    if (op.Mode == "upper") return "全部转为大写";
                    if (op.Mode == "title") return "每个词首字母大写";
                    if (op.Mode == "sentence") return "首字母大写、其余小写";
                    return "全部转为小写";

                case "trim":
                    return op.Mode == "keep-end" ? "只保留最后 " + op.Count + " 个字符" : "只保留前 " + op.Count + " 个字符";

                case "number":
                    return "编号 " + new string('0', Math.Max(0, op.Digits - 1)) + op.Start.ToString(CultureInfo.InvariantCulture)
                        + " 起，加在" + (op.Mode == "prefix" ? "前面" : "后面")
                        + (op.GroupByFolder ? "，按文件夹各自计数" : "");

                case "date":
                    return "插入日期（" + op.Format + "）加在" + (op.Mode == "prefix" ? "前面" : "后面");

                default: return op.Kind;
            }
        }

        // 旧版规则（v3.x 的单页表单）→ 步骤链。用于兼容旧设置与旧方案文件。
        /// <summary>旧版（v3 及以前）的规则字段里是否有内容，需要迁移成步骤。</summary>
        public static bool HasLegacy(RuleSet r)
        {
            if (r == null) return false;
            if (!string.IsNullOrEmpty(r.Prefix)) return true;
            if (!string.IsNullOrEmpty(r.Suffix)) return true;
            if (r.Number) return true;
            if (r.TrimLeft > 0) return true;
            if (r.MaxLength > 0) return true;
            if (!string.IsNullOrEmpty(r.CaseMode) && r.CaseMode != "keep") return true;
            if (r.InsertPos > 0) return true;
            if (r.DeletePos > 0) return true;
            if (r.Replaces != null && r.Replaces.Count > 0) return true;
            return false;
        }

        public static List<Op> FromLegacy(RuleSet r)
        {
            List<Op> ops = new List<Op>();
            if (r == null) return ops;

            List<ReplaceRule> reps = r.Replaces;
            bool anyRep = false;
            Op repOp = Create("replace");
            if (reps != null)
            {
                for (int i = 0; i < reps.Count; i++)
                {
                    ReplaceRule rr = reps[i];
                    if (rr == null || string.IsNullOrEmpty(rr.Find)) continue;
                    repOp.Pairs.Add(rr);
                    anyRep = true;
                }
            }
            if (anyRep) ops.Add(repOp);
            if (!anyRep && !string.IsNullOrEmpty(r.Find))
            {
                Op o = Create("replace");
                o.Find = r.Find;
                o.Replace = r.Replace;
                o.UseRegex = r.UseRegex;
                o.MatchCase = r.MatchCase;
                ops.Add(o);
            }

            if (r.DeletePos > 0 && r.DeleteCount > 0)
            {
                Op o = Create("remove");
                o.Mode = "pos";
                o.Pos = r.DeletePos;
                o.Count = r.DeleteCount;
                ops.Add(o);
            }
            if (r.InsertPos > 0 && !string.IsNullOrEmpty(r.InsertText))
            {
                Op o = Create("insert");
                o.Mode = "pos";
                o.Pos = r.InsertPos;
                o.Text = r.InsertText;
                ops.Add(o);
            }
            if (r.TrimLeft > 0)
            {
                Op o = Create("remove");
                o.Mode = "start";
                o.Count = r.TrimLeft;
                ops.Add(o);
            }
            if (r.MaxLength > 0)
            {
                Op o = Create("trim");
                o.Mode = "keep-start";
                o.Count = r.MaxLength;
                ops.Add(o);
            }
            if (r.CaseMode == "lower" || r.CaseMode == "upper" || r.CaseMode == "title")
            {
                Op o = Create("case");
                o.Mode = r.CaseMode;
                ops.Add(o);
            }
            if (r.Number && string.IsNullOrEmpty(r.Template))
            {
                Op o = Create("number");
                o.Mode = r.NumPos == "prefix" ? "prefix" : "suffix";
                o.Start = r.Start;
                o.Step = r.Step;
                o.Digits = r.Digits;
                o.Sep = r.NumSep;
                o.GroupByFolder = r.GroupByFolder;
                ops.Add(o);
            }
            if (!string.IsNullOrEmpty(r.Prefix))
            {
                Op o = Create("insert");
                o.Mode = "start";
                o.Text = r.Prefix;
                ops.Add(o);
            }
            if (!string.IsNullOrEmpty(r.Suffix))
            {
                Op o = Create("insert");
                o.Mode = "end";
                o.Text = r.Suffix;
                ops.Add(o);
            }
            return ops;
        }
    }
}
