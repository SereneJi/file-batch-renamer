// ============================================================================
//  File Batch Renamer - visual language
//  Theme.cs
//
//  Direction: a proofing desk for file names. Warm paper ground, white sheets,
//  one ink-blue accent used only where it means something (primary action,
//  selection, the part of a name that actually changed). Hierarchy comes from
//  type weight, hairlines and whitespace - no shadows, no gradients.
//  Target : .NET Framework 4.x, C# 5 syntax
// ============================================================================
using System;
using System.Drawing;

namespace FileBatRenamer
{
    internal static class Theme
    {
        // 启动时由 Program 按当前显示器 DPI 设置（96 DPI = 1.0，200% = 2.0）
        public static float Scale = 1f;

        public static int S(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        public static float SF(float v)
        {
            return v * Scale;
        }

        public static void Init()
        {
            float s = 1f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { s = g.DpiX / 96f; }
            }
            catch { }
            Scale = s > 0f ? s : 1f;
        }

        // ---- 颜色：暖纸灰中性阶 + 单一墨蓝 --------------------------------------
        public static readonly Color Bg = Color.FromArgb(243, 241, 236);           // 窗口底（暖纸灰）
        public static readonly Color Surface = Color.White;                        // 纸面 / 列表
        public static readonly Color Sunken = Color.FromArgb(251, 249, 245);       // 输入框底
        public static readonly Color RowGrid = Color.FromArgb(242, 239, 232);
        public static readonly Color Border = Color.FromArgb(230, 226, 215);       // 发丝线
        public static readonly Color BorderStrong = Color.FromArgb(213, 207, 193);
        public static readonly Color BorderHover = Color.FromArgb(184, 176, 160);
        public static readonly Color Text = Color.FromArgb(26, 25, 22);            // 墨色
        public static readonly Color Muted = Color.FromArgb(108, 102, 91);
        public static readonly Color Faint = Color.FromArgb(155, 147, 133);

        public static readonly Color Accent = Color.FromArgb(32, 74, 120);         // 墨蓝（唯一强调色）
        public static readonly Color AccentHover = Color.FromArgb(24, 58, 95);
        public static readonly Color AccentSoft = Color.FromArgb(234, 241, 248);
        public static readonly Color AccentLine = Color.FromArgb(196, 216, 234);
        public static readonly Color AccentText = Color.FromArgb(250, 250, 248);

        public static readonly Color Ok = Color.FromArgb(46, 107, 70);             // 将改名
        public static readonly Color OkSoft = Color.FromArgb(231, 243, 235);
        public static readonly Color Warn = Color.FromArgb(138, 90, 18);           // 冲突
        public static readonly Color WarnSoft = Color.FromArgb(250, 242, 226);
        public static readonly Color Danger = Color.FromArgb(162, 58, 46);         // 错误
        public static readonly Color DangerSoft = Color.FromArgb(250, 235, 232);

        public static readonly Color RowAlt = Color.FromArgb(252, 251, 248);
        public static readonly Color RowHover = Color.FromArgb(248, 246, 240);
        public static readonly Color Selection = Color.FromArgb(239, 244, 250);
        public static readonly Color NewName = Color.FromArgb(26, 25, 22);

        // ---- 字体（点为物理单位，DPI 由 GDI+ 自动换算）-------------------------
        private const string Ui = "Segoe UI";

        public static readonly Font FontTitle = new Font(Ui, 13.5f, FontStyle.Bold);
        public static readonly Font FontSection = new Font(Ui, 9.5f, FontStyle.Bold);
        public static readonly Font FontHead = new Font(Ui, 9.5f, FontStyle.Bold);
        public static readonly Font FontBody = new Font(Ui, 9.5f);
        public static readonly Font FontBold = new Font(Ui, 9.5f, FontStyle.Bold);
        public static readonly Font FontBodyBold = new Font(Ui, 9.5f, FontStyle.Bold);
        public static readonly Font FontSmall = new Font(Ui, 8.5f);
        public static readonly Font FontMeta = new Font(Ui, 8.5f);
        public static readonly Font FontLabel = new Font(Ui, 8.25f, FontStyle.Bold);
        public static readonly Font FontMicro = new Font(Ui, 7.75f);
        public static readonly Font FontMono = new Font("Consolas", 9f);
        public static readonly Font FontNum = new Font("Consolas", 9f);
        public static readonly Font FontIcon = new Font("Segoe UI Symbol", 11f, FontStyle.Bold);
    }
}
