// ============================================================================
//  File Batch Renamer - custom drawn controls
//  Controls.cs
//
//  Everything here paints itself: no system chrome, no default WinForms look.
//  All metrics are logical pixels and go through Theme.S().
//  Target : .NET Framework 4.x, C# 5 syntax
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileBatRenamer
{
    /// <summary>单行文本框的灰色占位提示（EM_SETCUEBANNER）。</summary>
    internal static class NativeCue
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void Set(TextBox box, string text)
        {
            try
            {
                if (box == null || !box.IsHandleCreated || string.IsNullOrEmpty(text)) return;
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            }
            catch { }
        }
    }

    /// <summary>读取窗口样式，用来判断原生 ListView 是否真的显示了横向滚动条。</summary>
    internal static class NativeState
    {
        private const int GWL_STYLE = -16;
        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        private const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        /// <summary>给原生 ListView 打开双缓冲，否则逐行 hover 重绘会整块闪。</summary>
        private const int LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55;

        public static bool HasListViewDoubleBuffer(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return false;
                IntPtr r = SendMessage(c.Handle, LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero);
                return ((long)r & LVS_EX_DOUBLEBUFFER) != 0;
            }
            catch { return false; }
        }

        public static void EnableListViewDoubleBuffer(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                SendMessage(c.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER);
            }
            catch { }
        }
        private const int WS_HSCROLL = 0x00100000;
        private const int SB_HORZ = 0;

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO
        {
            public uint cbSize;
            public uint fMask;
            public int nMin;
            public int nMax;
            public uint nPage;
            public int nPos;
            public int nTrackPos;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO si);

        public static bool HasHScroll(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return false;
                return (GetWindowLong(c.Handle, GWL_STYLE) & WS_HSCROLL) != 0;
            }
            catch { return false; }
        }

        /// <summary>横向可滚动范围（0 表示内容没有超出可视区）。</summary>
        public static int HScrollRange(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return 0;
                SCROLLINFO si = new SCROLLINFO();
                si.cbSize = (uint)Marshal.SizeOf(typeof(SCROLLINFO));
                si.fMask = 0x1 | 0x2 | 0x4; // RANGE | PAGE | POS
                if (!GetScrollInfo(c.Handle, SB_HORZ, ref si)) return -1;
                return si.nMax - (int)si.nPage + 1;
            }
            catch { return -1; }
        }
    }

    internal static class DrawUtil
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 1)
            {
                p.AddRectangle(r);
                return p;
            }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ------------------------------------------------------------------------
    //  图标：统一 16 单位网格、同一线宽、同一圆头，避免混用字符图标
    // ------------------------------------------------------------------------
    internal enum IconKind
    {
        None, Plus, Folder, Minus, Trash, Search, Up, Down, Top, Bottom,
        Swap, Sort, Undo, Export, ChevronDown, Check, Close, File, Funnel,
        ArrowRight, Scissors, Hash, Calendar, Grip, Layers, Case
    }

    internal static class Icons
    {
        public static void Draw(Graphics g, IconKind kind, Rectangle box, Color color)
        {
            if (kind == IconKind.None || box.Width <= 2) return;
            GraphicsState st = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float sc = box.Width / 16f;
                g.TranslateTransform(box.X, box.Y);
                g.ScaleTransform(sc, sc);
                using (Pen p = new Pen(color, 1.45f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    Pen(g, p, kind);
                }
            }
            finally { g.Restore(st); }
        }

        private static void L(Graphics g, Pen p, float x1, float y1, float x2, float y2)
        {
            g.DrawLine(p, x1, y1, x2, y2);
        }

        private static void Pen(Graphics g, Pen p, IconKind kind)
        {
            switch (kind)
            {
                case IconKind.Plus:
                    L(g, p, 4.6f, 8f, 11.4f, 8f);
                    L(g, p, 8f, 4.6f, 8f, 11.4f);
                    break;
                case IconKind.Minus:
                    L(g, p, 4.6f, 8f, 11.4f, 8f);
                    break;
                case IconKind.Folder:
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(2.4f, 12.4f), new PointF(2.4f, 4.4f), new PointF(6.2f, 4.4f),
                            new PointF(7.7f, 6.1f), new PointF(13.6f, 6.1f), new PointF(13.6f, 12.4f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                    }
                    break;
                case IconKind.Trash:
                    L(g, p, 3.4f, 4.6f, 12.6f, 4.6f);
                    L(g, p, 6.2f, 4.6f, 6.2f, 3.1f);
                    L(g, p, 6.2f, 3.1f, 9.8f, 3.1f);
                    L(g, p, 9.8f, 3.1f, 9.8f, 4.6f);
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(4.9f, 4.6f), new PointF(5.6f, 13.1f),
                            new PointF(10.4f, 13.1f), new PointF(11.1f, 4.6f) });
                        g.DrawPath(p, path);
                        path.Dispose();
                    }
                    L(g, p, 7f, 7.2f, 7f, 10.8f);
                    L(g, p, 9f, 7.2f, 9f, 10.8f);
                    break;
                case IconKind.Search:
                    g.DrawEllipse(p, 2.8f, 2.8f, 8.2f, 8.2f);
                    L(g, p, 10.4f, 10.4f, 13.4f, 13.4f);
                    break;
                case IconKind.Up:
                    L(g, p, 8f, 12.8f, 8f, 4.2f);
                    L(g, p, 4.8f, 7.4f, 8f, 4.2f);
                    L(g, p, 11.2f, 7.4f, 8f, 4.2f);
                    break;
                case IconKind.Down:
                    L(g, p, 8f, 3.2f, 8f, 11.8f);
                    L(g, p, 4.8f, 8.6f, 8f, 11.8f);
                    L(g, p, 11.2f, 8.6f, 8f, 11.8f);
                    break;
                case IconKind.Top:
                    L(g, p, 4.2f, 3.2f, 11.8f, 3.2f);
                    L(g, p, 8f, 13f, 8f, 6f);
                    L(g, p, 5.1f, 8.9f, 8f, 6f);
                    L(g, p, 10.9f, 8.9f, 8f, 6f);
                    break;
                case IconKind.Bottom:
                    L(g, p, 4.2f, 12.8f, 11.8f, 12.8f);
                    L(g, p, 8f, 3f, 8f, 10f);
                    L(g, p, 5.1f, 7.1f, 8f, 10f);
                    L(g, p, 10.9f, 7.1f, 8f, 10f);
                    break;
                case IconKind.Swap:
                    L(g, p, 3.4f, 6f, 12.6f, 6f);
                    L(g, p, 10f, 3.4f, 12.6f, 6f);
                    L(g, p, 10f, 8.6f, 12.6f, 6f);
                    L(g, p, 3.4f, 10f, 12.6f, 10f);
                    L(g, p, 6f, 7.4f, 3.4f, 10f);
                    L(g, p, 6f, 12.6f, 3.4f, 10f);
                    break;
                case IconKind.Sort:
                    L(g, p, 3f, 4.6f, 13f, 4.6f);
                    L(g, p, 5f, 8f, 11f, 8f);
                    L(g, p, 6.8f, 11.4f, 9.2f, 11.4f);
                    break;
                case IconKind.Funnel:
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(2.8f, 3.8f), new PointF(13.2f, 3.8f), new PointF(9.2f, 8.6f),
                            new PointF(9.2f, 12.6f), new PointF(6.8f, 11.2f), new PointF(6.8f, 8.6f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                    }
                    break;
                case IconKind.Undo:
                    {
                        g.DrawArc(p, 3.6f, 4.4f, 8.8f, 8.8f, 200f, 300f);
                        L(g, p, 3.2f, 5.2f, 3.2f, 9.2f);
                        L(g, p, 3.2f, 9.2f, 7.2f, 9.2f);
                    }
                    break;
                case IconKind.Export:
                    L(g, p, 3.4f, 9.6f, 3.4f, 13.2f);
                    L(g, p, 3.4f, 13.2f, 12.6f, 13.2f);
                    L(g, p, 12.6f, 13.2f, 12.6f, 9.6f);
                    L(g, p, 8f, 11f, 8f, 3.4f);
                    L(g, p, 4.9f, 6.5f, 8f, 3.4f);
                    L(g, p, 11.1f, 6.5f, 8f, 3.4f);
                    break;
                case IconKind.ChevronDown:
                    L(g, p, 4.8f, 6.4f, 8f, 9.6f);
                    L(g, p, 11.2f, 6.4f, 8f, 9.6f);
                    break;
                case IconKind.Check:
                    L(g, p, 3.6f, 8.6f, 6.7f, 11.5f);
                    L(g, p, 6.7f, 11.5f, 12.4f, 4.7f);
                    break;
                case IconKind.Close:
                    L(g, p, 4.4f, 4.4f, 11.6f, 11.6f);
                    L(g, p, 11.6f, 4.4f, 4.4f, 11.6f);
                    break;
                case IconKind.File:
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(4f, 2.6f), new PointF(9.4f, 2.6f), new PointF(12f, 5.2f),
                            new PointF(12f, 13.4f), new PointF(4f, 13.4f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        L(g, p, 9.4f, 2.6f, 9.4f, 5.2f);
                        L(g, p, 9.4f, 5.2f, 12f, 5.2f);
                        path.Dispose();
                    }
                    break;
                case IconKind.ArrowRight:
                    L(g, p, 2.8f, 8f, 12.4f, 8f);
                    L(g, p, 9.2f, 4.8f, 12.4f, 8f);
                    L(g, p, 9.2f, 11.2f, 12.4f, 8f);
                    break;
                case IconKind.Scissors:
                    g.DrawEllipse(p, 2.6f, 2.4f, 4.2f, 4.2f);
                    g.DrawEllipse(p, 2.6f, 9.4f, 4.2f, 4.2f);
                    L(g, p, 6.2f, 6.3f, 13.2f, 12.6f);
                    L(g, p, 6.2f, 9.7f, 13.2f, 3.4f);
                    break;
                case IconKind.Hash:
                    L(g, p, 6.2f, 2.8f, 5.4f, 13.2f);
                    L(g, p, 10.6f, 2.8f, 9.8f, 13.2f);
                    L(g, p, 3.4f, 6.2f, 12.8f, 6.2f);
                    L(g, p, 3.2f, 9.8f, 12.6f, 9.8f);
                    break;
                case IconKind.Case:
                    L(g, p, 3.4f, 12.2f, 6.9f, 4.2f);
                    L(g, p, 6.9f, 4.2f, 10.4f, 12.2f);
                    L(g, p, 5.0f, 9.2f, 8.9f, 9.2f);
                    break;
                case IconKind.Calendar:
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(2.6f, 4.2f), new PointF(13.4f, 4.2f),
                            new PointF(13.4f, 13.2f), new PointF(2.6f, 13.2f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        L(g, p, 2.6f, 7.2f, 13.4f, 7.2f);
                        L(g, p, 5.6f, 2.6f, 5.6f, 4.6f);
                        L(g, p, 10.4f, 2.6f, 10.4f, 4.6f);
                    }
                    break;
                case IconKind.Grip:
                    for (int i = 0; i < 3; i++)
                    {
                        g.DrawEllipse(p, 5.2f, 3.2f + i * 4.2f, 1.1f, 1.1f);
                        g.DrawEllipse(p, 9.7f, 3.2f + i * 4.2f, 1.1f, 1.1f);
                    }
                    break;
                case IconKind.Layers:
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddLines(new PointF[] {
                            new PointF(8f, 2.6f), new PointF(13.4f, 5.6f),
                            new PointF(8f, 8.6f), new PointF(2.6f, 5.6f) });
                        path.CloseFigure();
                        g.DrawPath(p, path);
                        path.Dispose();
                        L(g, p, 2.6f, 8.6f, 8f, 11.6f);
                        L(g, p, 8f, 11.6f, 13.4f, 8.6f);
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------------------
    //  卡片：白底 + 极浅描边，标题与字段之间不再画分割线
    // ------------------------------------------------------------------------
    internal class Card : Panel
    {
        public string Title = "";
        public string Hint = "";
        /// <summary>平铺模式：不再画成一张浮起的白卡，而是检查器里的一段，靠发丝线分隔。</summary>
        public bool Flat = false;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public int ContentTop { get { return Theme.S(44); } }

        public int Pad { get { return Theme.S(16); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Flat)
            {
                // 检查器栏：不要白卡，只要标题、右上的说明和一条段间发丝线
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
                int fx = Theme.S(16);
                if (!string.IsNullOrEmpty(Title))
                {
                    TextRenderer.DrawText(g, Title, Theme.FontLabel,
                        new Rectangle(fx, Theme.S(7), Width - Theme.S(32), Theme.S(20)),
                        Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                if (!string.IsNullOrEmpty(Hint))
                {
                    Size t = TextRenderer.MeasureText(g, Hint, Theme.FontMeta, new Size(1000, 20),
                        TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Hint, Theme.FontMeta,
                        new Rectangle(Width - t.Width - Theme.S(16), Theme.S(7), t.Width, Theme.S(20)),
                        Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                using (Pen p = new Pen(Theme.Border))
                    g.DrawLine(p, fx, Height - 1, Width, Height - 1);
                return;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = DrawUtil.Round(r, Theme.S(8)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                using (Pen pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }

            int x = Theme.S(16);
            if (!string.IsNullOrEmpty(Title))
            {
                TextRenderer.DrawText(g, Title, Theme.FontSection,
                    new Rectangle(x, Theme.S(8), Width - Theme.S(32), Theme.S(26)),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding);
            }
            if (!string.IsNullOrEmpty(Hint))
            {
                Size t = TextRenderer.MeasureText(g, Hint, Theme.FontMeta, new Size(1000, 20),
                    TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Hint, Theme.FontMeta,
                    new Rectangle(Width - t.Width - Theme.S(16), Theme.S(8), t.Width, Theme.S(26)),
                    Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  按钮：实心/描边/无边框三档，图标与文字同一套线宽
    // ------------------------------------------------------------------------
    internal enum ButtonKind { Primary, Segment, Secondary, Ghost, Danger }

    internal class FlatButton : Button
    {
        public ButtonKind Kind = ButtonKind.Secondary;
        public IconKind Glyph = IconKind.None;
        private bool hover;
        private bool pressed;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            TabStop = false;
            Font = Theme.FontBody;
            Height = Theme.S(34);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            Color bg, fg, border;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    bg = !Enabled ? Color.FromArgb(206, 207, 203)
                        : (pressed || hover ? Theme.AccentHover : Theme.Accent);
                    fg = !Enabled ? Color.White : Theme.AccentText;
                    border = bg;
                    break;
                case ButtonKind.Segment:
                    // 分段选择里被选中的那一格：浅色填充 + 主色文字，不抢主按钮的实心蓝
                    bg = !Enabled ? Color.FromArgb(247, 246, 242) : Theme.AccentSoft;
                    fg = !Enabled ? Theme.Faint : Theme.Accent;
                    border = !Enabled ? Theme.Border : Theme.AccentLine;
                    break;
                case ButtonKind.Danger:
                    bg = !Enabled ? Color.FromArgb(224, 196, 192)
                        : (pressed || hover ? Color.FromArgb(134, 52, 44) : Theme.Danger);
                    fg = Color.White;
                    border = bg;
                    break;
                case ButtonKind.Ghost:
                    bg = hover ? (pressed ? Color.FromArgb(238, 235, 227) : Theme.RowHover) : Theme.Surface;
                    fg = !Enabled ? Theme.Faint : (hover ? Theme.Text : Theme.Muted);
                    border = bg;
                    break;
                default:
                    bg = !Enabled ? Color.FromArgb(250, 249, 246)
                        : (pressed ? Color.FromArgb(244, 241, 234) : (hover ? Color.FromArgb(253, 252, 249) : Theme.Surface));
                    fg = !Enabled ? Theme.Faint : Theme.Text;
                    border = !Enabled ? Theme.Border : (hover ? Theme.BorderStrong : Theme.Border);
                    break;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = DrawUtil.Round(r, Theme.S(6)))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, path);
                if (Kind == ButtonKind.Secondary || Kind == ButtonKind.Ghost || Kind == ButtonKind.Segment)
                {
                    using (Pen pen = new Pen(border)) g.DrawPath(pen, path);
                }
            }

            int iw = Theme.S(16);
            int gap = Theme.S(7);
            bool hasText = !string.IsNullOrEmpty(Text);
            Size ts = hasText ? TextRenderer.MeasureText(g, Text, Font, new Size(4000, Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine) : Size.Empty;
            int total = (Glyph != IconKind.None ? iw : 0) + (Glyph != IconKind.None && hasText ? gap : 0) + ts.Width;
            int x = (Width - total) / 2;
            if (Glyph != IconKind.None)
            {
                Icons.Draw(g, Glyph, new Rectangle(x, (Height - iw) / 2, iw, iw), fg);
                x += iw + gap;
            }
            if (hasText)
            {
                TextRenderer.DrawText(g, Text, Font,
                    new Rectangle(x, 0, ts.Width + Theme.S(2), Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.SingleLine);
            }
        }
    }

    // 只有图标的小方块按钮（调序用）
    internal class IconButton : Control
    {
        public IconKind Glyph = IconKind.None;
        public string Tip = "";
        private bool hover;
        private bool pressed;
        private readonly ToolTip tip = new ToolTip();

        public IconButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(28), Theme.S(28));
            Cursor = Cursors.Hand;
            TabStop = false;
            BackColor = Theme.Surface;
        }

        public void SetTip(string text)
        {
            Tip = text;
            tip.SetToolTip(this, text);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);

            if (hover && Enabled)
            {
                Color bg = pressed ? Color.FromArgb(236, 233, 225) : Theme.RowHover;
                using (GraphicsPath p = DrawUtil.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(6)))
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
            }
            int iw = Theme.S(15);
            Color c = !Enabled ? Theme.Faint : (hover ? Theme.Text : Theme.Muted);
            Icons.Draw(g, Glyph, new Rectangle((Width - iw) / 2, (Height - iw) / 2, iw, iw), c);
        }
    }

    // ------------------------------------------------------------------------
    //  输入框：自绘边框 + 内嵌无边框 TextBox（系统边框在高 DPI 下很脏）
    // ------------------------------------------------------------------------
    internal class Field : Control
    {
        public readonly TextBox Box;
        public IconKind Glyph = IconKind.None;
        private bool focus;
        private bool hover;
        private string placeholder = "";

        public Field()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(32);
            Cursor = Cursors.IBeam;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.FontBody;
            Box.BackColor = Theme.Surface;
            Box.ForeColor = Theme.Text;
            Box.TextChanged += delegate { Invalidate(); OnTextChanged(EventArgs.Empty); };
            Box.GotFocus += delegate { focus = true; Invalidate(); };
            Box.LostFocus += delegate { focus = false; Invalidate(); };
            Box.HandleCreated += delegate { NativeCue.Set(Box, placeholder); };
            Controls.Add(Box);
        }

        public string Placeholder
        {
            get { return placeholder; }
            set
            {
                placeholder = value ?? "";
                if (Box.IsHandleCreated) NativeCue.Set(Box, placeholder);
            }
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value ?? ""; }
        }

        public override Color BackColor
        {
            get { return base.BackColor; }
            set
            {
                base.BackColor = value;
                if (Box != null) { Box.BackColor = value; }
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.Enabled = Enabled;
            Invalidate();
        }

        private int LeftPad
        {
            get { return Theme.S(Glyph == IconKind.None ? 11 : 32); }
        }

        private void LayoutBox()
        {
            if (Box == null) return;
            int h = Box.PreferredHeight;
            Box.SetBounds(LeftPad, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - LeftPad - Theme.S(10)), h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Box.Focus(); base.OnGotFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            Color fill = Enabled ? Theme.Surface : Theme.Bg;
            Color border = !Enabled ? Theme.Border
                : (focus ? Theme.Accent : (hover ? Theme.BorderStrong : Theme.Border));

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = DrawUtil.Round(r, Theme.S(6)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }

            if (Glyph != IconKind.None)
            {
                int iw = Theme.S(14);
                Icons.Draw(g, Glyph, new Rectangle(Theme.S(10), (Height - iw) / 2, iw, iw),
                    focus ? Theme.Accent : Theme.Faint);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  下拉选择：FlatStyle 在高 DPI 下会错位，这里整只自绘 + 自绘弹层
    // ------------------------------------------------------------------------
    internal class Select : Control
    {
        public readonly List<string> Items = new List<string>();
        private int sel = -1;
        private bool hover;
        private bool quiet;
        private SelectDrop open;

        public event EventHandler SelectedIndexChanged;

        public Select()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(32);
            Cursor = Cursors.Hand;
            Font = Theme.FontBody;
            TabStop = false;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= Items.Count) v = Items.Count - 1;
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (!quiet && SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>刷新选项文字（筛选器里的计数会变），不触发选择事件。</summary>
        public void SetItems(string[] items, int index)
        {
            quiet = true;
            Items.Clear();
            if (items != null) Items.AddRange(items);
            sel = index >= 0 && index < Items.Count ? index : (Items.Count > 0 ? 0 : -1);
            quiet = false;
            Invalidate();
        }

        public string SelectedItem
        {
            get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; }
        }

        public void AddRange(string[] items)
        {
            if (items != null) Items.AddRange(items);
            if (sel < 0 && Items.Count > 0) SelectedIndex = 0;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowDrop();
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private void ShowDrop()
        {
            if (!Enabled || Items.Count == 0) return;
            if (open != null)
            {
                open.Close();
                open = null;
                return;
            }
            SelectDrop d = new SelectDrop(Items, sel, Width, Font);
            int itemH = Theme.S(30);
            int h = Math.Min(Items.Count * itemH + Theme.S(10), Theme.S(320));
            Point p = PointToScreen(new Point(0, Height + Theme.S(3)));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (p.Y + h > wa.Bottom) p.Y = PointToScreen(new Point(0, -h - Theme.S(3))).Y;
            d.Bounds = new Rectangle(p.X, p.Y, Width, h);
            d.Picked += delegate(object s, int idx)
            {
                SelectedIndex = idx;
                open = null;
            };
            d.FormClosed += delegate { open = null; Invalidate(); };
            open = d;
            // 必须挂在顶层窗体上：挂到控件自身会让弹层变成子窗口，
            // 被同一张卡片里的其它控件盖住、且背景发灰看不清。
            Form owner = FindForm();
            if (owner != null) d.Show(owner); else d.Show();
            d.BringToFront();
            d.TakeFocus();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color border = !Enabled ? Theme.Border
                : (open != null ? Theme.Accent : (hover ? Theme.BorderStrong : Theme.Border));
            Color fill = Enabled ? Theme.Surface : Theme.Bg;
            using (GraphicsPath p = DrawUtil.Round(r, Theme.S(6)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }

            string text = SelectedItem;
            if (string.IsNullOrEmpty(text)) text = Items.Count > 0 ? Items[0] : "";
            Color fg = !Enabled ? Theme.Faint : Theme.Text;
            int right = Width - Theme.S(28);
            TextRenderer.DrawText(g, text, Font, new Rectangle(Theme.S(11), 0, right - Theme.S(12), Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            int iw = Theme.S(12);
            Icons.Draw(g, IconKind.ChevronDown,
                new Rectangle(Width - Theme.S(10) - iw, (Height - iw) / 2, iw, iw),
                !Enabled ? Theme.Faint : Theme.Muted);
        }
    }

    internal class SelectDrop : Form
    {
        public event EventHandler<int> Picked;
        private readonly ListBox lb;

        public SelectDrop(List<string> items, int sel, int width, Font font)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            Width = width;
            Padding = new Padding(1);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            lb = new ListBox();
            lb.BorderStyle = BorderStyle.None;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = Theme.S(30);
            lb.Font = font;
            lb.BackColor = Theme.Surface;
            lb.ForeColor = Theme.Text;
            lb.IntegralHeight = false;
            lb.Dock = DockStyle.Fill;
            lb.TabStop = false;
            for (int i = 0; i < items.Count; i++) lb.Items.Add(items[i]);
            if (sel >= 0 && sel < lb.Items.Count) lb.SelectedIndex = sel;
            lb.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color bg = hot ? Theme.AccentSoft : Theme.Surface;
                using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
                Rectangle tr = new Rectangle(e.Bounds.X + Theme.S(12), e.Bounds.Y,
                    e.Bounds.Width - Theme.S(20), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, lb.Items[e.Index].ToString(), font, tr,
                    hot ? Theme.Accent : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            };
            lb.Click += delegate { Pick(); };
            lb.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Pick(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { Close(); }
            };
            lb.MouseMove += delegate(object s, MouseEventArgs e)
            {
                int idx = lb.IndexFromPoint(e.Location);
                if (idx >= 0 && idx != lb.SelectedIndex) { lb.SelectedIndex = idx; }
            };
            Controls.Add(lb);
        }

        public void TakeFocus()
        {
            lb.Focus();
        }

        private void Pick()
        {
            int idx = lb.SelectedIndex;
            if (idx >= 0 && Picked != null) Picked(this, idx);
            Close();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        // 系统投影：弹层浮在卡片上时不至于糊成一片
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.BorderStrong))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ------------------------------------------------------------------------
    //  复选框：16px 圆角方块，选中填墨绿
    // ------------------------------------------------------------------------
    internal class CheckBoxFlat : Control
    {
        private bool chk;
        private bool hover;

        public event EventHandler CheckedChanged;

        public CheckBoxFlat()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(24);
            Cursor = Cursors.Hand;
            TabStop = false;
            Font = Theme.FontBody;
        }

        public bool Checked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled) Checked = !Checked;
        }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            int box = Theme.S(15);
            Rectangle r = new Rectangle(0, (Height - box) / 2, box, box);
            using (GraphicsPath p = DrawUtil.Round(r, Theme.S(3)))
            {
                Color fill = !Enabled ? Color.FromArgb(247, 245, 239) : (chk ? Theme.Accent : Theme.Surface);
                Color border = !Enabled ? Theme.Border : (chk ? Theme.Accent : (hover ? Theme.BorderStrong : Theme.BorderStrong));
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }
            if (chk)
            {
                int iw = Theme.S(10);
                Icons.Draw(g, IconKind.Check,
                    new Rectangle(r.X + (box - iw) / 2, r.Y + (box - iw) / 2, iw, iw),
                    Theme.AccentText);
            }

            int tx = box + Theme.S(8);
            Color fg = !Enabled ? Theme.Faint : (hover ? Theme.Text : Theme.Muted);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Math.Max(10, Width - tx), Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------------
    //  空状态：一叠待整理的文件，比大图标 + 箭头更贴近这个工具
    // ------------------------------------------------------------------------
    internal class EmptyState : Panel
    {
        public string Title = "还没有文件";
        public string Sub = "把文件拖到这里，或从左侧添加";
        public string Step = "支持整个文件夹递归导入，拖拽行可以调整顺序";
        public readonly FlatButton Action;

        /// <summary>筛选无结果时把按钮收起来，避免「选择文件」和当前语境打架。</summary>
        public bool ActionVisible
        {
            get { return Action.Visible; }
            set { Action.Visible = value; }
        }

        public EmptyState()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Dock = DockStyle.Fill;

            Action = new FlatButton();
            Action.Kind = ButtonKind.Secondary;
            Action.Glyph = IconKind.Plus;
            Action.Text = "选择文件";
            Action.Font = Theme.FontBody;
            Action.Size = new Size(Theme.S(112), Theme.S(30));
            Action.BackColor = Theme.Surface;
            Controls.Add(Action);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PlaceAction();
        }

        private void PlaceAction()
        {
            if (Action == null) return;
            int cy = Height / 2 - Theme.S(64);
            Action.Bounds = new Rectangle((Width - Action.Width) / 2, cy + Theme.S(150), Action.Width, Action.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            int cx = Width / 2;
            int cy = Height / 2 - Theme.S(64);

            // 三张错位叠放的纸：像一叠还没誊清的文件
            int cw = Theme.S(170), ch = Theme.S(104);
            for (int i = 2; i >= 0; i--)
            {
                int off = i * Theme.S(10);
                Rectangle r = new Rectangle(cx - cw / 2 + off, cy - ch / 2 - off, cw, ch);
                using (GraphicsPath p = DrawUtil.Round(r, Theme.S(7)))
                {
                    Color fill = i == 0 ? Theme.Surface : (i == 1 ? Color.FromArgb(249, 247, 242) : Color.FromArgb(243, 240, 233));
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                    using (Pen pen = new Pen(i == 0 ? Theme.BorderStrong : Theme.Border)) g.DrawPath(pen, p);
                }
            }

            // 最上面那张：左侧索引条 + 「旧名 → 新名」三行台账
            Rectangle front = new Rectangle(cx - cw / 2, cy - ch / 2, cw, ch);
            using (GraphicsPath p = DrawUtil.Round(front, Theme.S(7)))
            using (SolidBrush b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, p);
            using (GraphicsPath p2 = DrawUtil.Round(new Rectangle(front.X, front.Y, Theme.S(4), front.Height), Theme.S(2)))
            using (SolidBrush b2 = new SolidBrush(Theme.Accent)) g.FillPath(b2, p2);

            using (Pen line = new Pen(Theme.BorderStrong, Theme.S(1)))
            using (Pen line2 = new Pen(Theme.Accent, Theme.S(1)))
            {
                for (int i = 0; i < 3; i++)
                {
                    int y = front.Y + Theme.S(24) + i * Theme.S(21);
                    g.DrawLine(line, front.X + Theme.S(18), y, front.X + Theme.S(58), y);
                    int iw = Theme.S(12);
                    Icons.Draw(g, IconKind.ArrowRight, new Rectangle(front.X + Theme.S(64), y - iw / 2, iw, iw), Theme.Faint);
                    g.DrawLine(i < 2 ? line2 : line, front.X + Theme.S(82), y, front.X + Theme.S(146), y);
                }
            }

            int w = Math.Min(Theme.S(440), Width - Theme.S(60));
            int ty = front.Bottom + Theme.S(30);
            TextRenderer.DrawText(g, Title, Theme.FontTitle,
                new Rectangle(cx - w / 2, ty, w, Theme.S(26)),
                Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Sub, Theme.FontBody,
                new Rectangle(cx - w / 2, ty + Theme.S(32), w, Theme.S(22)),
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Step, Theme.FontMeta,
                new Rectangle(cx - w / 2, ty + Theme.S(120), w, Theme.S(20)),
                Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
    }

    // 细长提示条（如“已跳过 2 个隐藏文件”）
    internal class NoteBar : Panel
    {
        public string Text2 = "";
        public Color Tone = Theme.Accent;

        public NoteBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(30);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            if (string.IsNullOrEmpty(Text2)) return;
            Rectangle r = new Rectangle(0, Theme.S(2), Width - 1, Height - Theme.S(4));
            using (GraphicsPath p = DrawUtil.Round(r, Theme.S(6)))
            using (SolidBrush b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, p);
            TextRenderer.DrawText(g, Text2, Theme.FontMeta,
                new Rectangle(Theme.S(10), r.Y, Width - Theme.S(20), r.Height),
                Tone, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
    /// <summary>两段/三段切换：一条浅色轨道，选中项是白纸片。</summary>
    internal class Segmented : Control
    {
        public string[] Items = new string[0];
        private int sel;
        private int hover = -1;

        public event EventHandler SelectedIndexChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(30);
            Cursor = Cursors.Hand;
            Font = Theme.FontBody;
            TabStop = false;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (Items.Length > 0 && v >= Items.Length) v = Items.Length - 1;
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        private int Hit(MouseEventArgs e)
        {
            if (Items.Length == 0) return -1;
            int seg = (Width - Theme.S(4)) / Items.Length;
            if (seg <= 0) return -1;
            int i = (e.X - Theme.S(2)) / seg;
            return i < 0 ? 0 : (i >= Items.Length ? Items.Length - 1 : i);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = Hit(e);
            if (i != hover) { hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = Hit(e);
            if (i >= 0 && i != sel) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = DrawUtil.Round(track, Theme.S(7)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(241, 238, 231))) g.FillPath(b, p);

            if (Items.Length == 0) return;
            int inner = Width - Theme.S(4);
            int seg = inner / Items.Length;
            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle r = new Rectangle(Theme.S(2) + i * seg, Theme.S(2), seg - (i == Items.Length - 1 ? 0 : 1), Height - Theme.S(5));
                bool on = i == sel;
                if (on)
                {
                    using (GraphicsPath p = DrawUtil.Round(r, Theme.S(5)))
                    {
                        using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                        using (Pen pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
                    }
                }
                else if (i == hover)
                {
                    using (GraphicsPath p = DrawUtil.Round(r, Theme.S(5)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(247, 245, 239))) g.FillPath(b, p);
                }
                TextRenderer.DrawText(g, Items[i], on ? Theme.FontBold : Theme.FontBody, r,
                    on ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>小圆点 + 文字，用在底栏的统计里。</summary>
    internal class DotLabel : Control
    {
        public Color Tone = Theme.Faint;
        private string caption = "";
        public string Caption
        {
            get { return caption; }
            set { if (value == caption) return; caption = value ?? ""; Invalidate(); }
        }
        public bool Emphasis;

        public DotLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(20);
            Font = Theme.FontMeta;
        }

        public int PreferredWidth
        {
            get
            {
                Size t = TextRenderer.MeasureText(Caption, Emphasis ? Theme.FontBold : Theme.FontMeta,
                    new Size(2000, 20), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return Theme.S(12) + t.Width + Theme.S(10);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int d = Theme.S(5);
            using (SolidBrush b = new SolidBrush(Tone))
                g.FillEllipse(b, new Rectangle(Theme.S(1), (Height - d) / 2, d, d));
            TextRenderer.DrawText(g, Caption, Emphasis ? Theme.FontBold : Theme.FontMeta,
                new Rectangle(Theme.S(12), 0, Width - Theme.S(12), Height),
                Emphasis ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>处理步骤的一行：序号、标题、摘要、删除按钮。</summary>
    internal class StepRow : Control
    {
        public int Index;
        public string Title = "";
        public string Summary = "";
        public bool Active;

        private bool hover;
        private bool hoverClose;

        public event EventHandler Activate;
        public event EventHandler DeletePressed;

        public StepRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Width = Theme.S(368);
            Height = Theme.S(44);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hc = e.X >= Width - Theme.S(34);
            bool h = !hc;
            if (hc != hoverClose) { hoverClose = hc; Invalidate(); }
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            hoverClose = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.X >= Width - Theme.S(34))
            {
                if (DeletePressed != null) DeletePressed(this, EventArgs.Empty);
            }
            else if (Activate != null) Activate(this, EventArgs.Empty);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Active ? Theme.AccentSoft : (hover ? Theme.RowHover : Theme.Surface));

            if (Active)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(0, 0, Theme.S(2), Height));
            }

            string n = (Index + 1).ToString("00");
            TextRenderer.DrawText(g, n, Theme.FontNum,
                new Rectangle(Theme.S(10), 0, Theme.S(20), Height), Active ? Theme.Accent : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int tx = Theme.S(34);
            int tw = Width - tx - Theme.S(34);
            TextRenderer.DrawText(g, Title, Theme.FontBodyBold,
                new Rectangle(tx, Theme.S(6), tw, Theme.S(18)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Summary, Theme.FontMeta,
                new Rectangle(tx, Theme.S(25), tw, Theme.S(16)), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (hover || Active)
            {
                Icons.Draw(g, IconKind.Close,
                    new Rectangle(Width - Theme.S(26), (Height - Theme.S(13)) / 2, Theme.S(13), Theme.S(13)),
                    hoverClose ? Theme.Danger : Theme.Faint);
            }

            using (Pen p = new Pen(Active ? Theme.AccentLine : Theme.RowGrid))
                g.DrawLine(p, Theme.S(10), Height - 1, Width - Theme.S(8), Height - 1);
        }
    }
    /// <summary>原生 ListView 的句柄重建后扩展样式会丢，所以在 OnHandleCreated 里每次都补上。</summary>
    internal class FlatListView : ListView
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeState.EnableListViewDoubleBuffer(this);
        }
    }
}
