// ============================================================================
//  File Batch Renamer - main window (v3 redesigned UI)
//  MainForm.cs
//  Layout:  header / toolbar / [list | rule cards] / action bar
//  Target : .NET Framework 4.x, C# 5 syntax, DPI aware (manual scaling)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FileBatRenamer
{
    internal class MainForm : Form
    {
        // ------------------------------------------------------------------ 模型
        private readonly List<FileItem> items = new List<FileItem>();
        private RuleSet rules = new RuleSet();
        private List<PreviewItem> preview = new List<PreviewItem>();
        private readonly List<PreviewItem> visible = new List<PreviewItem>();   // 列表当前显示的行
        private AppSettings settings = new AppSettings();
        private readonly bool uiTest;
        private FileItem dragging;
        private string logDirOverride;
        private bool ready;
        private string filterText = "";
        private int filterStatus = 0;          // 0 全部 / 1 将改名 / 2 冲突 / 3 未变化 / 4 错误
        public int SmokeFail;

        // ------------------------------------------------------------------ 控件
        private Panel header, leftRail, rightRail, center, centerTop, centerFoot, listFrame, listHost;
        private ListView list, srcList, presetList;
        private EmptyState empty;
        private Panel rightHost;

        private Label lblTitle, lblMsg, lblVersion, lblCount, lblCountSub, lblSrcHead, lblPresetHead, lblPresetEmpty;
        private DotLabel lblCntOk, lblCntConflict, lblCntSame, lblCntError;

        private FlatButton btnAddFiles, btnAddFolder, btnRun, btnExport, btnUndo;
        private IconButton btnRemove, btnClear, btnSort;
        private Field txtSearch;
        private Select cboFilter;

        // 规则控件（v5：命名方式 + 步骤链 + 输出）
        private Field txtExt, txtTemplate;
        private CheckBoxFlat chkKeepExt;
        private Select cboDup;
        private Label lblExtLabel, lblDup;
        private Label lblTemplate, lblTemplateHint, lblStepCount;
        private Label lblStepEmpty, lblStepEmptyHint, lblEditor;
        private Segmented segMode;
        private FlatButton btnSavePreset;
        private IconButton btnDelPreset;
        private IconButton btnOpUp, btnOpDown, btnOpDel;
        private FlowLayoutPanel stepHost;
        private Panel opPalette, opEditor, editorHost, stepScroll;
        private Panel cardBase, cardSteps, cardOutput;

        private readonly List<Op> ops = new List<Op>();
        private int selOp = -1;
        private bool renamingAll;
        private string filterDir = "";
        private bool suppressRail;
        private Timer refreshTimer;

        // ================================================================== 构造
        public MainForm(string[] paths, bool uiTest)
        {
            this.uiTest = uiTest;
            Theme.Init();
            settings = Features.LoadSettings();
            rules = settings.Rules ?? new RuleSet();
            if (rules.Replaces == null) rules.Replaces = new List<ReplaceRule>();

            Text = "文件批量重命名工具";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Theme.S(1120), Theme.S(760));
            Size = ClampToScreen(new Size(Theme.S(settings.Width), Theme.S(settings.Height)));
            if (settings.Maximized) WindowState = FormWindowState.Maximized;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            // 输入时合并连续刷新，避免每敲一个字就整表重建造成的闪烁
            refreshTimer = new Timer();
            refreshTimer.Interval = 120;
            refreshTimer.Tick += delegate { refreshTimer.Stop(); if (ready) RefreshPreview(); };
            ApplyRulesToUi();
            ready = true;
            RefreshPreview();
            UpdateEmptyState();

            if (paths != null && paths.Length > 0) AddPaths(paths);
            Shown += OnFormShown;
            FormClosing += OnFormClosing;
        }

        // 复合窗口：整窗先画到离屏缓冲再一次性提交，消除拖动/缩放时的子控件闪白
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return cp;
            }
        }

        /// <summary>开发用：套一组示例规则，方便截图与演示（-Demo 启动参数）。</summary>
        internal void ApplyDemo()
        {
            RuleSet d = new RuleSet();
            Op r1 = OpEngine.Create("replace"); r1.Find = "IMG"; r1.Replace = "照片"; d.Ops.Add(r1);
            Op r2 = OpEngine.Create("replace"); r2.Find = "SCREEN"; r2.Replace = "截图"; r2.MatchCase = true; d.Ops.Add(r2);
            Op pre = OpEngine.Create("insert"); pre.Mode = "start"; pre.Text = "旅行_"; d.Ops.Add(pre);
            if (Environment.GetEnvironmentVariable("FBR_DEMO_ALL") == "1") { d.RenameAll = true; d.Template = "照片_{nn}"; d.Ops.Clear(); }
            rules = d;
            ApplyRulesToUi();
            RefreshPreview();
            if (Environment.GetEnvironmentVariable("FBR_DEMO_MOVE") == "1" && items.Count > 3)
                MoveItemTo(items[0], items[2], true);
            string sel = Environment.GetEnvironmentVariable("FBR_DEMO_SEL");
            int k;
            if (sel != null && int.TryParse(sel, out k) && k >= 0 && k < ops.Count)
            {
                selOp = k;
                RebuildSteps();
            }
            if (rightHost != null) rightHost.AutoScrollPosition = new Point(0, 0);
        }

        // 窗口尺寸：settings 里存「逻辑像素」，这里换算成物理像素并夹进屏幕工作区，
        // 避免高 DPI 或换到小屏幕后窗口跑到屏幕外面。
        private static Size ClampToScreen(Size desired)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (desired.Width > wa.Width) desired.Width = wa.Width;
            if (desired.Height > wa.Height) desired.Height = wa.Height;
            if (desired.Width < 800) desired.Width = 800;
            if (desired.Height < 560) desired.Height = 560;
            return desired;
        }

        private void OnFormShown(object sender, EventArgs e)
        {
            LayoutAll();
            UpdateEmptyState();
            RebuildSources();
            ReloadPresetList();
            if (uiTest) { RunSmokeTest(); return; }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (refreshTimer != null) refreshTimer.Stop();
            if (uiTest) return;
            CollectRules();
            Rectangle rb = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Width = Math.Max(1120, (int)Math.Round(rb.Width / Theme.Scale));
            settings.Height = Math.Max(760, (int)Math.Round(rb.Height / Theme.Scale));
            settings.Maximized = WindowState == FormWindowState.Maximized;
            settings.Rules = rules;
            Features.SaveSettings(settings);
        }

        // ================================================================== 界面
        private void BuildUi()
        {
            SuspendLayout();

            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.S(52);
            header.BackColor = Theme.Surface;
            BuildHeader(header);

            leftRail = new Panel();
            leftRail.Dock = DockStyle.Left;
            leftRail.Width = Theme.S(232);
            leftRail.BackColor = Theme.Surface;
            leftRail.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, leftRail.Width - 1, 0, leftRail.Width - 1, leftRail.Height);
            };
            BuildLeftRail(leftRail);

            rightRail = new Panel();
            rightRail.Dock = DockStyle.Right;
            rightRail.Width = Theme.S(400);
            rightRail.BackColor = Theme.Surface;
            rightRail.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, 0, rightRail.Height);
            };
            BuildRightRail(rightRail);

            center = new Panel();
            center.Dock = DockStyle.Fill;
            center.BackColor = Theme.Surface;
            BuildCenter(center);

            // 停靠顺序：先加 Fill，再加四周（后加的先占边）
            Controls.Add(center);
            Controls.Add(leftRail);
            Controls.Add(rightRail);
            Controls.Add(header);

            HookFileDrop(this);
            HookFileDrop(header);
            HookFileDrop(leftRail);
            HookFileDrop(rightRail);
            HookFileDrop(center);

            ResumeLayout(false);
            PerformLayout();
            LayoutLeftRail();
            LayoutCenterTop();
            AdjustColumns();
        }

        // 统一接管「把文件拖进窗口」：容器本身有 AllowDrop 时，子控件没有也能往上冒泡。
        private void HookFileDrop(Control c)
        {
            if (c == null) return;
            c.AllowDrop = true;
            c.DragEnter += delegate(object s, DragEventArgs e)
            {
                e.Effect = (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += delegate(object s, DragEventArgs e)
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) AddPaths(files);
            };
        }

        private void BuildHeader(Panel host)
        {
            lblTitle = new Label();
            lblTitle.Text = "文件批量重命名";
            lblTitle.Font = Theme.FontTitle;
            lblTitle.ForeColor = Theme.Text;
            lblTitle.AutoSize = false;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.BackColor = Color.Transparent;
            host.Controls.Add(lblTitle);

            lblVersion = new Label();
            lblVersion.Text = "v5.1";
            lblVersion.Font = Theme.FontNum;
            lblVersion.ForeColor = Theme.Faint;
            lblVersion.AutoSize = false;
            lblVersion.TextAlign = ContentAlignment.MiddleLeft;
            lblVersion.BackColor = Color.Transparent;
            host.Controls.Add(lblVersion);

            btnUndo = new FlatButton();
            btnUndo.Text = "撤销历史";
            btnUndo.Kind = ButtonKind.Ghost;
            btnUndo.Glyph = IconKind.Undo;
            btnUndo.Click += delegate { ShowUndoHistory(); };
            host.Controls.Add(btnUndo);

            btnRun = MakeButton(host, "执行重命名", ButtonKind.Primary, IconKind.None, 158, delegate { ExecuteRename(true); });
            btnRun.Font = Theme.FontBold;
            btnRun.Height = Theme.S(34);

            host.Resize += delegate { LayoutHeader(); };
            host.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle tile = new Rectangle(Theme.S(16), (host.Height - Theme.S(26)) / 2, Theme.S(26), Theme.S(26));
                using (GraphicsPath path = DrawUtil.Round(tile, Theme.S(6)))
                using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillPath(b, path);
                int iw = Theme.S(15);
                Icons.Draw(g, IconKind.Swap,
                    new Rectangle(tile.X + (tile.Width - iw) / 2, tile.Y + (tile.Height - iw) / 2, iw, iw),
                    Color.FromArgb(248, 247, 243));
                using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, host.Height - 1, host.Width, host.Height - 1);
            };
        }

        private void LayoutHeader()
        {
            if (header == null || lblTitle == null) return;
            int h = header.Height;
            int titleX = Theme.S(52);
            int titleW = TextRenderer.MeasureText(lblTitle.Text, lblTitle.Font).Width + Theme.S(4);
            lblTitle.Bounds = new Rectangle(titleX, 0, titleW, h);
            lblVersion.Bounds = new Rectangle(titleX + titleW + Theme.S(8), 0, Theme.S(48), h);

            int rw = Theme.S(158);
            btnRun.Bounds = new Rectangle(header.Width - Theme.S(16) - rw, (h - Theme.S(34)) / 2, rw, Theme.S(34));
            btnUndo.Bounds = new Rectangle(btnRun.Left - Theme.S(4) - Theme.S(96), (h - Theme.S(30)) / 2, Theme.S(96), Theme.S(30));
        }

        private Label RailHead(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontLabel;
            l.ForeColor = Theme.Muted;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Color.Transparent;
            return l;
        }

        private ListView RailList()
        {
            ListView lv = new FlatListView();
            lv.View = View.Details;
            lv.HeaderStyle = ColumnHeaderStyle.None;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.BorderStyle = BorderStyle.None;
            lv.OwnerDraw = true;
            lv.BackColor = Theme.Surface;
            lv.ForeColor = Theme.Text;
            lv.Font = Theme.FontBody;
            lv.ShowItemToolTips = true;
            lv.SmallImageList = new ImageList();
            lv.SmallImageList.ImageSize = new Size(1, Theme.S(30));
            lv.Columns.Add("", Theme.S(140), HorizontalAlignment.Left);
            lv.Columns.Add("", Theme.S(40), HorizontalAlignment.Right);
            lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = false; };
            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e)
            {
                bool sel = e.Item.Selected;
                Color bg = sel ? Theme.Selection : (e.ItemIndex == hoverRailRow && ReferenceEquals(s, lv) ? Theme.RowHover : Theme.Surface);
                using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(b, new Rectangle(e.Bounds.X, e.Bounds.Y + Theme.S(6), Theme.S(2), e.Bounds.Height - Theme.S(12)));
                }
            };
            lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                bool first = e.ColumnIndex == 0;
                if (first)
                {
                    IconKind kind = e.ItemIndex == 0 ? IconKind.Layers : IconKind.Folder;
                    Icons.Draw(e.Graphics, kind,
                        new Rectangle(e.Bounds.X + Theme.S(2), e.Bounds.Y + (e.Bounds.Height - Theme.S(13)) / 2, Theme.S(13), Theme.S(13)),
                        e.Item.Selected ? Theme.Accent : Theme.Faint);
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.Item.Selected ? Theme.FontBodyBold : Theme.FontBody,
                        new Rectangle(e.Bounds.X + Theme.S(24), e.Bounds.Y, e.Bounds.Width - Theme.S(28), e.Bounds.Height),
                        e.Item.Selected ? Theme.Text : Theme.Muted, f);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Theme.FontNum,
                        new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - Theme.S(10), e.Bounds.Height),
                        e.Item.Selected ? Theme.Muted : Theme.Faint, f | TextFormatFlags.Right);
                }
            };
            lv.MouseMove += delegate(object s, MouseEventArgs e)
            {
                ListViewItem hit = lv.GetItemAt(e.X, e.Y);
                int idx = hit == null ? -1 : hit.Index;
                if (idx == hoverRailRow) return;
                hoverRailRow = idx;
                lv.Invalidate();
            };
            lv.MouseLeave += delegate { hoverRailRow = -1; lv.Invalidate(); };
            return lv;
        }

        private int hoverRailRow = -1;

        private void BuildLeftRail(Panel host)
        {
            lblSrcHead = RailHead("来源");
            host.Controls.Add(lblSrcHead);

            btnRemove = MakeIcon(host, IconKind.Minus, "移除选中的文件", delegate { RemoveSelected(); });
            btnClear = MakeIcon(host, IconKind.Trash, "清空文件列表", delegate { ClearAll(); });
            btnRemove.Size = btnClear.Size = new Size(Theme.S(24), Theme.S(24));

            btnAddFiles = MakeButton(host, "添加文件", ButtonKind.Secondary, IconKind.Plus, 100, delegate { AddFilesDialog(); });
            btnAddFolder = MakeButton(host, "添加文件夹", ButtonKind.Secondary, IconKind.Folder, 100, delegate { AddFolderDialog(false); });
            btnAddFiles.Height = Theme.S(30);
            btnAddFolder.Height = Theme.S(30);

            srcList = RailList();
            host.Controls.Add(srcList);

            lblPresetHead = RailHead("方案");
            host.Controls.Add(lblPresetHead);

            presetList = RailList();
            presetList.Columns.RemoveAt(1);   // 方案列表只要一列
            host.Controls.Add(presetList);

            lblPresetEmpty = MkHint(host, "还没有保存的方案", 16, 100, 200);
            lblPresetEmpty.Text = "还没有保存的方案\n保存规则与输出设置，下次一键套用";
            lblPresetEmpty.Height = Theme.S(42);

            btnSavePreset = MakeButton(host, "保存当前方案", ButtonKind.Ghost, IconKind.Plus, 96, delegate { SavePresetDialog(); });
            btnSavePreset.Height = Theme.S(30);
            btnDelPreset = MakeIcon(host, IconKind.Trash, "删除选中的方案", delegate { DeletePresetDialog(); });
            btnDelPreset.Size = new Size(Theme.S(24), Theme.S(24));

            srcList.SelectedIndexChanged += delegate { OnSourceSelected(); };
            presetList.SelectedIndexChanged += delegate { OnPresetSelected(); };
            presetList.SizeChanged += delegate { FitPresetColumn(); };
            srcList.SizeChanged += delegate { LayoutLeftRail(); };
            host.Resize += delegate { LayoutLeftRail(); };
        }

        private void FitPresetColumn()
        {
            if (presetList == null || presetList.Columns.Count == 0) return;
            int w = presetList.ClientSize.Width;
            if (w <= Theme.S(60)) return;
            presetList.Columns[0].Width = Math.Max(Theme.S(60), w - SystemInformation.VerticalScrollBarWidth - Theme.S(8));
        }

        private void LayoutLeftRail()
        {
            if (leftRail == null || srcList == null) return;
            int w = leftRail.Width, h = leftRail.Height;
            int pad = Theme.S(14), cw = w - pad * 2;
            if (cw < Theme.S(40)) return;

            lblSrcHead.Bounds = new Rectangle(pad, Theme.S(14), cw - Theme.S(56), Theme.S(18));
            btnRemove.Bounds = new Rectangle(pad + cw - Theme.S(50), Theme.S(11), Theme.S(24), Theme.S(24));
            btnClear.Bounds = new Rectangle(pad + cw - Theme.S(24), Theme.S(11), Theme.S(24), Theme.S(24));

            btnAddFiles.Bounds = new Rectangle(pad, Theme.S(40), cw, Theme.S(30));
            btnAddFolder.Bounds = new Rectangle(pad, Theme.S(76), cw, Theme.S(30));

            int top = Theme.S(120);
            int contentH = srcList.Items.Count * Theme.S(30) + Theme.S(8);
            int maxSrc = Math.Max(Theme.S(90), h - top - Theme.S(170));
            int srcH = Math.Min(Math.Max(Theme.S(36), contentH), maxSrc);
            srcList.Bounds = new Rectangle(pad, top, cw, srcH);
            int lw = srcList.ClientSize.Width;
            if (lw > Theme.S(60))
            {
                srcList.Columns[0].Width = lw - Theme.S(44);
                srcList.Columns[1].Width = Theme.S(40);
            }

            int y2 = top + srcH + Theme.S(24);
            lblPresetHead.Bounds = new Rectangle(pad, y2, cw, Theme.S(18));
            int listTop = y2 + Theme.S(26);
            int saveY = h - Theme.S(14) - Theme.S(30);
            int listH = Math.Max(Theme.S(36), saveY - Theme.S(10) - listTop);
            presetList.Bounds = new Rectangle(pad, listTop, cw, listH);
            int pw = presetList.ClientSize.Width;
            if (pw > Theme.S(60)) presetList.Columns[0].Width = pw - SystemInformation.VerticalScrollBarWidth - Theme.S(8);
            if (lblPresetEmpty != null) lblPresetEmpty.Bounds = new Rectangle(pad + Theme.S(2), listTop + Theme.S(2), cw, Theme.S(44));

            btnSavePreset.Bounds = new Rectangle(pad, saveY, cw - Theme.S(34), Theme.S(30));
            btnDelPreset.Bounds = new Rectangle(pad + cw - Theme.S(26), saveY + Theme.S(3), Theme.S(24), Theme.S(24));
        }
        private void OnSourceSelected()
        {
            if (suppressRail || srcList == null || srcList.SelectedItems.Count == 0) return;
            filterDir = (srcList.SelectedItems[0].Tag as string) ?? "";
            RebuildList();
        }

        private void OnPresetSelected()
        {
            if (suppressRail || presetList == null || presetList.SelectedItems.Count == 0) return;
            string name = presetList.SelectedItems[0].Text;
            if (string.IsNullOrEmpty(name)) return;
            RuleSet loaded = Features.LoadPreset(name);
            if (loaded == null) return;
            rules = loaded;
            if (rules.Replaces == null) rules.Replaces = new List<ReplaceRule>();
            ApplyRulesToUi();
            RefreshPreview();
            SetStatus("已加载方案：" + name);
        }

        private void BuildCenter(Panel host)
        {
            listFrame = new Panel();
            listFrame.Dock = DockStyle.Fill;
            listFrame.BackColor = Theme.Surface;
            host.Controls.Add(listFrame);

            listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.BackColor = Theme.Surface;
            listHost.Padding = new Padding(Theme.S(18), 0, Theme.S(18), 0);
            listFrame.Controls.Add(listHost);
            BuildList(listHost);

            centerFoot = new Panel();
            centerFoot.Dock = DockStyle.Bottom;
            centerFoot.Height = Theme.S(40);
            centerFoot.BackColor = Theme.Surface;
            centerFoot.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.RowGrid)) e.Graphics.DrawLine(p, 0, 0, centerFoot.Width, 0);
            };
            listFrame.Controls.Add(centerFoot);

            centerTop = new Panel();
            centerTop.Dock = DockStyle.Top;
            centerTop.Height = Theme.S(54);
            centerTop.BackColor = Theme.Surface;
            centerTop.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.RowGrid)) e.Graphics.DrawLine(p, Theme.S(18), centerTop.Height - 1, centerTop.Width - Theme.S(18), centerTop.Height - 1);
            };
            listFrame.Controls.Add(centerTop);

            lblCount = new Label();
            lblCount.Text = "全部文件";
            lblCount.Font = Theme.FontSection;
            lblCount.ForeColor = Theme.Text;
            lblCount.AutoSize = false;
            lblCount.TextAlign = ContentAlignment.MiddleLeft;
            lblCount.BackColor = Color.Transparent;
            centerTop.Controls.Add(lblCount);

            lblCountSub = new Label();
            lblCountSub.Text = "0 个文件";
            lblCountSub.Font = Theme.FontMeta;
            lblCountSub.ForeColor = Theme.Muted;
            lblCountSub.AutoSize = false;
            lblCountSub.TextAlign = ContentAlignment.MiddleLeft;
            lblCountSub.BackColor = Color.Transparent;
            centerTop.Controls.Add(lblCountSub);

            txtSearch = new Field();
            txtSearch.Placeholder = "搜索文件名";
            txtSearch.Font = Theme.FontBody;
            txtSearch.Glyph = IconKind.Search;
            txtSearch.TextChanged += delegate { filterText = txtSearch.Text.Trim(); RebuildList(); };
            centerTop.Controls.Add(txtSearch);

            cboFilter = new Select();
            cboFilter.Font = Theme.FontBody;
            cboFilter.AddRange(new string[] { "全部状态", "将改名", "冲突", "未变化", "错误" });
            cboFilter.SelectedIndexChanged += delegate { filterStatus = cboFilter.SelectedIndex; RebuildList(); };
            centerTop.Controls.Add(cboFilter);

            btnSort = MakeIcon(centerTop, IconKind.Sort, "排序与顺序", delegate { ShowSortMenu(btnSort); });
            btnSort.Size = new Size(Theme.S(28), Theme.S(28));
            centerTop.Resize += delegate { LayoutCenterTop(); };

            txtSearch.Resize += delegate { txtSearch.Invalidate(); };

            BuildCenterFooter(centerFoot);
        }

        private void BuildCenterFooter(Panel host)
        {
            lblCntOk = MakeDot(host, "将改名", Theme.Accent);
            lblCntConflict = MakeDot(host, "冲突", Theme.Warn);
            lblCntSame = MakeDot(host, "未变化", Theme.Faint);
            lblCntError = MakeDot(host, "错误", Theme.Danger);

            lblMsg = new Label();
            lblMsg.Text = "";
            lblMsg.Font = Theme.FontMeta;
            lblMsg.ForeColor = Theme.Faint;
            lblMsg.AutoSize = false;
            lblMsg.AutoEllipsis = true;
            lblMsg.TextAlign = ContentAlignment.MiddleLeft;
            lblMsg.BackColor = Color.Transparent;
            host.Controls.Add(lblMsg);

            btnExport = MakeButton(host, "导出", ButtonKind.Ghost, IconKind.Export, 72, delegate { ExportCsvDialog(); });
            btnExport.Height = Theme.S(26);
            host.Resize += delegate { LayoutCenterFoot(); };
        }

        private DotLabel MakeDot(Control host, string caption, Color tone)
        {
            DotLabel d = new DotLabel();
            d.Caption = caption;
            d.Tone = tone;
            d.Text = "0";
            d.Font = Theme.FontMeta;
            host.Controls.Add(d);
            return d;
        }

        private void LayoutCenterFoot()
        {
            if (centerFoot == null || lblMsg == null) return;
            int h = centerFoot.Height;
            int pad = Theme.S(18);
            int x = pad;
            DotLabel[] dots = new DotLabel[] { lblCntOk, lblCntConflict, lblCntSame, lblCntError };
            foreach (DotLabel d in dots)
            {
                int w = d.PreferredWidth;
                d.Bounds = new Rectangle(x, (h - Theme.S(20)) / 2, w, Theme.S(20));
                x += w + Theme.S(16);
            }
            int bw = Theme.S(72), gap = Theme.S(16);
            btnExport.Bounds = new Rectangle(centerFoot.Width - pad - bw, (h - Theme.S(26)) / 2, bw, Theme.S(26));
            int msgX = x + Theme.S(8);
            int msgW = Math.Max(0, btnExport.Left - gap - msgX);
            lblMsg.Bounds = new Rectangle(msgX, 0, msgW, h);
        }

        private void LayoutCenterTop()
        {
            if (centerTop == null || txtSearch == null) return;
            int w = centerTop.Width, h = centerTop.Height;
            int ch = Theme.S(30);
            int y = (h - ch) / 2;
            int pad = Theme.S(18);

            lblCount.Bounds = new Rectangle(pad, Theme.S(9), Theme.S(130), Theme.S(20));
            lblCountSub.Bounds = new Rectangle(pad, Theme.S(29), Theme.S(200), Theme.S(16));

            int right = w - pad;
            btnSort.Bounds = new Rectangle(right - Theme.S(28), y + Theme.S(1), Theme.S(28), Theme.S(28));
            right = btnSort.Left - Theme.S(8);
            cboFilter.Bounds = new Rectangle(right - Theme.S(112), y, Theme.S(112), ch);
            right = cboFilter.Left - Theme.S(8);
            int sw = Math.Max(Theme.S(110), Math.Min(Theme.S(210), right - pad - Theme.S(8)));
            txtSearch.Bounds = new Rectangle(right - sw, y, sw, ch);
        }

        private void BuildRightRail(Panel host)
        {
            rightHost = new Panel();
            rightHost.Dock = DockStyle.Fill;
            rightHost.BackColor = Theme.Surface;
            host.Controls.Add(rightHost);

            // 先放 Fill 段，再放上下两段：Dock 的填充段必须最早加入，否则它会盖住边条
            cardSteps = new Panel();
            cardSteps.Dock = DockStyle.Fill;
            cardSteps.BackColor = Theme.Surface;
            rightHost.Controls.Add(cardSteps);

            cardOutput = new Panel();
            cardOutput.Dock = DockStyle.Bottom;
            cardOutput.Height = Theme.S(124);
            cardOutput.BackColor = Theme.Surface;
            cardOutput.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.S(16), 0, cardOutput.Width - Theme.S(16), 0);
            };
            rightHost.Controls.Add(cardOutput);

            cardBase = new Panel();
            cardBase.Dock = DockStyle.Top;
            cardBase.Height = Theme.S(82);
            cardBase.BackColor = Theme.Surface;
            cardBase.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.S(16), cardBase.Height - 1, cardBase.Width - Theme.S(16), cardBase.Height - 1);
            };
            rightHost.Controls.Add(cardBase);

            BuildBaseSection(cardBase);
            BuildStepsSection(cardSteps);
            BuildOutputSection(cardOutput);
            cardSteps.Resize += delegate { LayoutStepsSection(); };
            rightHost.Resize += delegate { LayoutBaseSection(); LayoutStepsSection(); LayoutOutputSection(); };
        }

        private void BuildList(Control host)
        {
            list = new FlatListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.OwnerDraw = true;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Surface;
            list.ForeColor = Theme.Text;
            list.Font = Theme.FontBody;
            list.AllowDrop = true;
            list.ShowItemToolTips = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.SmallImageList = new ImageList();
            list.SmallImageList.ImageSize = new Size(1, Theme.S(42));
            list.Columns.Add("#", Theme.S(40), HorizontalAlignment.Right);
            list.Columns.Add("原文件名", Theme.S(260), HorizontalAlignment.Left);
            list.Columns.Add("新文件名", Theme.S(290), HorizontalAlignment.Left);
            list.Columns.Add("状态", Theme.S(88), HorizontalAlignment.Left);

            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.Resize += delegate { AdjustColumns(); };
            list.KeyDown += ListKeyDown;
            list.ItemDrag += ListItemDrag;
            list.DragOver += ListDragOver;
            list.DragLeave += delegate { list.InsertionMark.Index = -1; };
            list.DragDrop += ListDragDrop;
            list.DrawColumnHeader += ListDrawColumnHeader;
            list.DrawItem += ListDrawItem;
            list.DrawSubItem += ListDrawSubItem;
            list.MouseMove += ListMouseMove;
            list.MouseLeave += delegate { SetHoverRow(-1); };

            empty = new EmptyState();
            empty.Title = "把文件拖到这里";
            empty.Sub = "或者从左侧「添加文件 / 添加文件夹」开始";
            empty.Step = "支持整个文件夹递归导入 · 拖拽行可以调整顺序";
            empty.Action.Click += delegate { AddFilesDialog(); };
            empty.Visible = true;

            host.Controls.Add(list);
            host.Controls.Add(empty);
        }

        private FlatButton MakeButton(Control host, string text, ButtonKind kind, IconKind glyph, int width, EventHandler click)
        {
            FlatButton b = new FlatButton();
            b.AutoSize = false;          // Button 默认 AutoSize=true，会无视调用方给的宽度
            b.Text = text;
            b.Kind = kind;
            b.Glyph = glyph;
            b.Font = Theme.FontBody;
            b.Width = Theme.S(width);
            b.Height = Theme.S(34);
            if (click != null) b.Click += click;
            host.Controls.Add(b);
            return b;
        }

        private IconButton MakeIcon(Control host, IconKind glyph, string tip, EventHandler click)
        {
            IconButton b = new IconButton();
            b.Glyph = glyph;
            b.SetTip(tip);
            if (click != null) b.Click += click;
            host.Controls.Add(b);
            return b;
        }
        private int hoverRow = -1;

        private void SetHoverRow(int idx)
        {
            if (dragging != null) return;   // 拖拽中鼠标划过会不停重画行，看起来像闪
            if (hoverRow == idx) return;
            int old = hoverRow;
            hoverRow = idx;
            if (list == null || !list.IsHandleCreated) return;
            // 只重画变化的两行：整块 Invalidate 在 200% DPI 下会明显闪一下。
            InvalidateRow(old);
            InvalidateRow(idx);
        }

        private void InvalidateRow(int idx)
        {
            if (list == null || !list.IsHandleCreated) return;
            if (idx < 0 || idx >= list.Items.Count) return;
            try { list.Invalidate(list.GetItemRect(idx)); } catch { }
        }

        private void ListMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                ListViewItem it = list.GetItemAt(Theme.S(40), e.Y);
                SetHoverRow(it == null ? -1 : it.Index);
            }
            catch { }
        }

        private void ListDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Sunken)) g.FillRectangle(b, e.Bounds);
            TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            f |= e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            Rectangle r = new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y, e.Bounds.Width - Theme.S(24), e.Bounds.Height);
            TextRenderer.DrawText(g, e.Header.Text, Theme.FontMeta, r, Theme.Muted, f);
            using (Pen p = new Pen(Theme.RowGrid))
                g.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            // 原文件名 → 新文件名：表头尾部一个很小的箭头，提示两列的关系
            if (e.ColumnIndex == 1)
            {
                int iw = Theme.S(11);
                Icons.Draw(g, IconKind.ArrowRight,
                    new Rectangle(e.Bounds.Right - Theme.S(18), e.Bounds.Y + (e.Bounds.Height - iw) / 2, iw, iw), Theme.Faint);
            }
        }

        private void ListDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            Graphics g = e.Graphics;
            bool sel = e.Item.Selected;   // e.State 在 OwnerDraw 下会谎报 Selected，用 Item.Selected 才准
            Color bg = sel ? Theme.Selection : (e.ItemIndex == hoverRow ? Theme.RowHover : Theme.Surface);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
            if (sel)
            {
                using (SolidBrush b2 = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b2, new Rectangle(e.Bounds.X, e.Bounds.Y, Theme.S(2), e.Bounds.Height));
            }
            using (Pen p = new Pen(Theme.RowGrid))
                g.DrawLine(p, e.Bounds.X + Theme.S(10), e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        // 新名字里真正变掉的那一段：公共前缀/后缀不标，只给中段换墨蓝字。
        private static void SplitChange(string oldName, string newName, out string prefix, out string changed, out string suffix)
        {
            prefix = changed = suffix = "";
            if (string.IsNullOrEmpty(newName) || oldName == newName) return;
            int max = Math.Min(oldName.Length, newName.Length);
            int p = 0;
            while (p < max && oldName[p] == newName[p]) p++;
            int s = 0;
            while (s < max - p && oldName[oldName.Length - 1 - s] == newName[newName.Length - 1 - s]) s++;
            // 只有「换掉中段、前后还认得出」时才标；整名重写不标，免得满屏蓝线
            if (p + s <= 0 || p + s >= newName.Length) return;
            prefix = newName.Substring(0, p);
            changed = newName.Substring(p, newName.Length - p - s);
            suffix = newName.Substring(newName.Length - s);
            // 下划线不该被当成「改动」本身：把中段末尾的分隔符还回后缀
            int cut = changed.Length;
            while (cut > 1 && (changed[cut - 1] == '_' || changed[cut - 1] == '-' || changed[cut - 1] == ' ')) cut--;
            if (cut < changed.Length)
            {
                suffix = changed.Substring(cut) + suffix;
                changed = changed.Substring(0, cut);
            }
        }

        private void DrawNewName(Graphics g, Rectangle r, PreviewItem pi)
        {
            string oldName = pi.Item.Name;
            string newName = pi.NewName;
            if (string.IsNullOrEmpty(newName)) return;
            Color textColor;
            switch (pi.Status)
            {
                case PreviewStatus.Conflict: textColor = Theme.Danger; break;
                case PreviewStatus.NoChange: textColor = Theme.Faint; break;
                case PreviewStatus.Error: textColor = Theme.Warn; break;
                default: textColor = Theme.Text; break;
            }
            TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            string prefix, changed, suffix;
            SplitChange(oldName ?? "", newName, out prefix, out changed, out suffix);
            if (changed.Length == 0)
            {
                TextRenderer.DrawText(g, newName, Theme.FontBody, r, textColor, f | TextFormatFlags.EndEllipsis);
                return;
            }
            int x = r.X;
            Region clip = g.Clip;
            g.SetClip(r);
            TextFormatFlags sf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                 | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            // 量宽度必须和绘制用同一组 flags：MeasureText 默认会算上左右各 3px 内边距，
            // 逐段量就会把后缀一段段推右，看起来像名字里多了空格。
            Size sz;
            if (prefix.Length > 0)
            {
                sz = TextRenderer.MeasureText(g, prefix, Theme.FontBody, new Size(int.MaxValue, int.MaxValue), sf);
                TextRenderer.DrawText(g, prefix, Theme.FontBody, new Rectangle(x, r.Y, sz.Width + Theme.S(2), r.Height), textColor, sf);
                x += sz.Width;
            }
            sz = TextRenderer.MeasureText(g, changed, Theme.FontBody, new Size(int.MaxValue, int.MaxValue), sf);
            int cw = sz.Width;
            if (x + cw > r.Right) cw = Math.Max(0, r.Right - x);
            // 只换色不画线：文件名里全是下划线，压在下划线上的横线会互相打架，也容易看成链接
            TextRenderer.DrawText(g, changed, Theme.FontBody, new Rectangle(x, r.Y, cw + Theme.S(2), r.Height), Theme.Accent, sf);
            x += cw;
            if (suffix.Length > 0 && x < r.Right)
                TextRenderer.DrawText(g, suffix, Theme.FontBody, new Rectangle(x, r.Y, r.Right - x, r.Height), textColor,
                    sf | TextFormatFlags.EndEllipsis);
            g.Clip = clip;
        }

        private Label MkHint(Control host, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontMeta;
            l.ForeColor = Theme.Faint;
            l.BackColor = Color.Transparent;
            l.Bounds = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(34));
            host.Controls.Add(l);
            return l;
        }

        // ---------------------------------------------------------- 命名方式
        private bool suppressSeg;
        private int editorNatural;

        private void BuildBaseSection(Panel c)
        {
            Label title = Lbl(c, "命名方式", 16, 12, 200);
            title.Font = Theme.FontLabel;
            title.Height = Theme.S(18);

            segMode = new Segmented();
            segMode.Items = new string[] { "保留原文件名", "完全重命名" };
            segMode.SelectedIndexChanged += delegate
            {
                if (suppressSeg) return;
                SetRenameAll(segMode.SelectedIndex == 1);
            };
            c.Controls.Add(segMode);

            lblTemplate = Lbl(c, "模板", 16, 78, 80);
            lblTemplate.Font = Theme.FontLabel;
            lblTemplate.Height = Theme.S(16);

            lblTemplateHint = MkHint(c, "{name}  {n}  {nn}  {date}  {parent}", 96, 78, 288);
            lblTemplateHint.Height = Theme.S(16);

            txtTemplate = Input(c, 16, 96, 368);
            txtTemplate.Placeholder = "{name}_{nn}";
            txtTemplate.Font = Theme.FontMono;
            txtTemplate.Box.Font = Theme.FontMono;

            HookAll(c);
            LayoutBaseSection();
        }

        private void LayoutBaseSection()
        {
            if (cardBase == null || segMode == null) return;
            int w = cardBase.Width - Theme.S(32);
            segMode.Bounds = new Rectangle(Theme.S(16), Theme.S(36), w, Theme.S(30));
            lblTemplate.Bounds = new Rectangle(Theme.S(16), Theme.S(78), Theme.S(80), Theme.S(16));
            lblTemplateHint.Bounds = new Rectangle(Theme.S(96), Theme.S(78), w - Theme.S(80), Theme.S(16));
            txtTemplate.Bounds = new Rectangle(Theme.S(16), Theme.S(98), w, Theme.S(30));
            lblTemplate.Visible = lblTemplateHint.Visible = txtTemplate.Visible = renamingAll;
            cardBase.Height = Theme.S(renamingAll ? 144 : 82);
            UpdateModeButtons();
        }

        private void SetRenameAll(bool all)
        {
            renamingAll = all;
            UpdateModeButtons();
            LayoutBaseSection();
            MarkDirty();
        }

        private void UpdateModeButtons()
        {
            if (segMode == null) return;
            suppressSeg = true;
            segMode.SelectedIndex = renamingAll ? 1 : 0;
            suppressSeg = false;
        }

        // ---------------------------------------------------------- 规则
        private static IconKind OpGlyph(string kind)
        {
            switch (kind)
            {
                case "remove": return IconKind.Minus;
                case "insert": return IconKind.Plus;
                case "trim": return IconKind.Scissors;
                case "number": return IconKind.Hash;
                case "date": return IconKind.Calendar;
                case "case": return IconKind.Case;
                default: return IconKind.Swap;
            }
        }

        private void BuildStepsSection(Panel c)
        {
            Label title = Lbl(c, "规则", 16, 12, 80);
            title.Font = Theme.FontLabel;
            title.Height = Theme.S(18);

            lblStepCount = new Label();
            lblStepCount.Text = "0 条";
            lblStepCount.Font = Theme.FontMicro;
            lblStepCount.ForeColor = Theme.Faint;
            lblStepCount.AutoSize = false;
            lblStepCount.TextAlign = ContentAlignment.MiddleRight;
            lblStepCount.BackColor = Color.Transparent;
            c.Controls.Add(lblStepCount);

            // 规则类型全部摆在明面上：点一下就加一条，不用翻菜单找功能
            opPalette = new Panel();
            opPalette.BackColor = Theme.Surface;
            c.Controls.Add(opPalette);
            for (int i = 0; i < OpEngine.Kinds.Length; i++)
            {
                string kind = OpEngine.Kinds[i];
                int ci = i;
                FlatButton chip = MakeButton(opPalette, OpEngine.KindName(kind), ButtonKind.Secondary, OpGlyph(kind), 86,
                    delegate { AddOp(kind); });
                chip.Font = Theme.FontSmall;
                chip.Height = Theme.S(28);
                chip.Tag = ci;
                chip.Click += delegate { };
            }

            stepScroll = new Panel();
            stepScroll.BackColor = Theme.Surface;
            stepScroll.AutoScroll = true;
            c.Controls.Add(stepScroll);

            lblStepEmpty = new Label();
            lblStepEmpty.Text = "还没有规则";
            lblStepEmpty.Font = Theme.FontBodyBold;
            lblStepEmpty.ForeColor = Theme.Muted;
            lblStepEmpty.TextAlign = ContentAlignment.MiddleLeft;
            lblStepEmpty.BackColor = Theme.Surface;
            stepScroll.Controls.Add(lblStepEmpty);

            lblStepEmptyHint = new Label();
            lblStepEmptyHint.Text = "从上面挑一个动作，规则会从上到下依次生效";
            lblStepEmptyHint.Font = Theme.FontMeta;
            lblStepEmptyHint.ForeColor = Theme.Faint;
            lblStepEmptyHint.TextAlign = ContentAlignment.MiddleLeft;
            lblStepEmptyHint.BackColor = Theme.Surface;
            stepScroll.Controls.Add(lblStepEmptyHint);

            stepHost = new FlowLayoutPanel();
            stepHost.FlowDirection = FlowDirection.TopDown;
            stepHost.WrapContents = false;
            stepHost.AutoSize = true;
            stepHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stepHost.BackColor = Theme.Surface;
            stepHost.Location = new Point(0, 0);
            stepScroll.Controls.Add(stepHost);

            editorHost = new Panel();
            editorHost.BackColor = Theme.Surface;
            editorHost.Height = 0;
            editorHost.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Theme.S(16), 0, editorHost.Width - Theme.S(16), 0);
            };
            c.Controls.Add(editorHost);

            lblEditor = Lbl(editorHost, "", 16, 8, 260);
            lblEditor.Font = Theme.FontBodyBold;
            lblEditor.ForeColor = Theme.Text;
            lblEditor.Height = Theme.S(24);

            btnOpUp = MakeIcon(editorHost, IconKind.Up, "上移这一步", delegate { MoveOp(-1); });
            btnOpDown = MakeIcon(editorHost, IconKind.Down, "下移这一步", delegate { MoveOp(1); });
            btnOpDel = MakeIcon(editorHost, IconKind.Trash, "删除这一步", delegate { DeleteOp(); });
            btnOpUp.Size = btnOpDown.Size = btnOpDel.Size = new Size(Theme.S(24), Theme.S(24));

            opEditor = new Panel();
            opEditor.BackColor = Theme.Surface;
            editorHost.Controls.Add(opEditor);

            HookAll(c);
            LayoutStepsSection();
        }

        private void LayoutStepsSection()
        {
            if (cardSteps == null || stepScroll == null || opPalette == null) return;
            int w = cardSteps.Width - Theme.S(32);
            if (w < Theme.S(100)) return;
            int sw = SystemInformation.VerticalScrollBarWidth;

            lblStepCount.Bounds = new Rectangle(Theme.S(64), Theme.S(13), Math.Max(Theme.S(40), w - Theme.S(64)), Theme.S(16));

            int chipH = Theme.S(28), gap = Theme.S(6);
            int colW = (w - gap * 3) / 4;
            int wide = (w - gap * 2) / 3;
            for (int i = 0; i < opPalette.Controls.Count; i++)
            {
                Control chip = opPalette.Controls[i];
                int row = i / 4, col = i % 4;
                if (row == 0) chip.Bounds = new Rectangle(col * (colW + gap), 0, colW, chipH);
                else chip.Bounds = new Rectangle(col * (wide + gap), chipH + gap, wide, chipH);
            }
            opPalette.Bounds = new Rectangle(Theme.S(16), Theme.S(34), w, chipH * 2 + gap);

            int listTop = Theme.S(34) + chipH * 2 + gap + Theme.S(14);
            // 高度只认 editorNatural，不读 Visible：构造期窗体未显示时 Visible 恒为 false
            int wantEditor = editorNatural > 0 ? Theme.S(editorNatural) : 0;
            int room = cardSteps.Height - listTop - Theme.S(2 * 46) - Theme.S(12);
            int editorH = wantEditor > 0 ? Math.Min(wantEditor, Math.Max(Theme.S(150), room)) : 0;
            if (editorHost.Height != editorH) editorHost.Height = editorH;
            if (opEditor != null)
            {
                opEditor.AutoScroll = wantEditor > editorH;
                if (opEditor.AutoScroll)
                {
                    // 纵向滚动条会吃掉客户区宽度，先把右侧控件收进来，免得再冒一条横条
                    int cw = opEditor.ClientSize.Width - Theme.S(22);
                    foreach (Control ctl in opEditor.Controls)
                        if (ctl.Right > cw) ctl.Width = Math.Max(Theme.S(40), cw - ctl.Left);
                }
            }
            // 步骤不多时列表收缩到内容高度；编辑器钉在规则区底部，像一块常驻的检视面板
            int rowsH = ops.Count * Theme.S(46);
            int listMax = Math.Max(Theme.S(70), cardSteps.Height - listTop - editorH - Theme.S(12));
            int listH = listMax;
            stepScroll.Bounds = new Rectangle(Theme.S(16), listTop, w, listH);

            // 只在内容真的放不下时开自动滚动：WinForms 的 AutoScroll 在内容恰好等宽时也会冒滚动条
            bool needScroll = rowsH > listH;
            if (stepScroll.AutoScroll != needScroll) stepScroll.AutoScroll = needScroll;

            // 行宽让出可能出现的纵向滚动条
            int rowW = w - (needScroll ? SystemInformation.VerticalScrollBarWidth + Theme.S(2) : Theme.S(2));
            stepHost.Width = Math.Max(Theme.S(80), rowW);
            foreach (Control ctl in stepHost.Controls)
                if (ctl is StepRow) ctl.Width = stepHost.Width;
            if (lblStepEmpty != null)
            {
                // 空态紧贴工具栏下方，别让一句提示孤零零地漂在大片留白中间
                int cy = Theme.S(6);
                lblStepEmpty.SetBounds(0, cy, w, Theme.S(22));
                lblStepEmptyHint.SetBounds(0, cy + Theme.S(24), w, Theme.S(20));
            }

            int editorTop = cardSteps.Height - editorH;
            editorHost.Bounds = new Rectangle(0, editorTop, cardSteps.Width, editorH);
            if (editorH > 0)
            {
                int right = editorHost.Width - Theme.S(16);
                btnOpDel.Bounds = new Rectangle(right - Theme.S(24), Theme.S(8), Theme.S(24), Theme.S(24));
                btnOpDown.Bounds = new Rectangle(right - Theme.S(24) * 2 - Theme.S(4), Theme.S(8), Theme.S(24), Theme.S(24));
                btnOpUp.Bounds = new Rectangle(right - Theme.S(24) * 3 - Theme.S(8), Theme.S(8), Theme.S(24), Theme.S(24));
                opEditor.Bounds = new Rectangle(Theme.S(16), Theme.S(38), w, Math.Max(0, editorH - Theme.S(46)));
            }
        }

        private void RebuildSteps()
        {
            if (stepHost == null || cardSteps == null) return;

            // 一律展开一条：右栏不该出现「一大片空白 + 必须先点一下」的空转状态
            if (ops.Count == 0) selOp = -1;
            else if (selOp < 0) selOp = 0;
            else if (selOp >= ops.Count) selOp = ops.Count - 1;

            int rowW = Math.Max(Theme.S(80), cardSteps.Width - Theme.S(32) - SystemInformation.VerticalScrollBarWidth);
            stepHost.SuspendLayout();
            stepHost.Controls.Clear();
            stepHost.AutoSize = false;
            for (int i = 0; i < ops.Count; i++)
            {
                StepRow row = new StepRow();
                int idx = i;
                row.Index = i;
                row.Width = rowW;
                row.Height = Theme.S(46);
                row.Margin = Padding.Empty;
                row.Title = OpEngine.KindName(ops[i].Kind);
                row.Summary = OpEngine.Describe(ops[i]);
                row.Active = (i == selOp);
                row.Activate += delegate { selOp = idx; RebuildSteps(); MarkDirty(); };
                row.DeletePressed += delegate { selOp = idx; DeleteOp(); };
                stepHost.Controls.Add(row);
            }
            stepHost.Height = Math.Max(Theme.S(46), ops.Count * Theme.S(46));
            stepHost.ResumeLayout();
            stepHost.Visible = ops.Count > 0;
            if (lblStepEmpty != null) lblStepEmpty.Visible = ops.Count == 0;
            if (lblStepEmptyHint != null) lblStepEmptyHint.Visible = ops.Count == 0;
            if (lblStepCount != null) lblStepCount.Text = ops.Count == 0 ? "按顺序执行" : ops.Count + " 个步骤 · 从上往下";

            int editorH = RebuildEditor();   // 编辑器内部坐标是逻辑值
            bool hasEditor = editorH > 0 && selOp >= 0 && selOp < ops.Count;
            editorNatural = hasEditor ? editorH + 54 : 0;
            editorHost.Visible = hasEditor;
            if (hasEditor)
            {
                lblEditor.Text = (selOp + 1) + " · " + OpEngine.KindName(ops[selOp].Kind);
                lblEditor.Visible = true;
                btnOpUp.Visible = btnOpDown.Visible = btnOpDel.Visible = true;
                btnOpUp.Enabled = selOp > 0;
                btnOpDown.Enabled = selOp < ops.Count - 1;
                btnOpDel.Enabled = true;
                opEditor.Visible = true;
            }
            else
            {
                btnOpUp.Visible = btnOpDown.Visible = btnOpDel.Visible = false;
                opEditor.Visible = false;
            }
            LayoutStepsSection();
        }
        private void RefreshStepSummaries()
        {
            if (stepHost == null) return;
            for (int i = 0; i < stepHost.Controls.Count && i < ops.Count; i++)
            {
                StepRow row = stepHost.Controls[i] as StepRow;
                if (row == null) continue;
                row.Summary = OpEngine.Describe(ops[i]);
                row.Invalidate();
            }
        }

        private void AddOp(string kind)
        {
            ops.Add(OpEngine.Create(kind));
            selOp = ops.Count - 1;
            RebuildSteps();
            MarkDirty();
        }

        private void DeleteOp()
        {
            if (selOp < 0 || selOp >= ops.Count) return;
            ops.RemoveAt(selOp);
            if (selOp >= ops.Count) selOp = ops.Count - 1;
            RebuildSteps();
            MarkDirty();
        }

        private void MoveOp(int delta)
        {
            int to = selOp + delta;
            if (selOp < 0 || to < 0 || to >= ops.Count) return;
            Op o = ops[selOp];
            ops.RemoveAt(selOp);
            ops.Insert(to, o);
            selOp = to;
            RebuildSteps();
            MarkDirty();
        }

        private static int IndexOf(string[] arr, string v)
        {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == v) return i;
            return 0;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            if (int.TryParse((s ?? "").Trim(), out v)) return v;
            return def;
        }

        // ---------------------------------------------------------- 步骤编辑器
        // 统一成「标签在左、控件靠右」的设置行：标签和控件同一行，右边缘对齐到 356。
        private const int EdLeft = 12;
        private const int EdRight = 356;
        private const int RowH = 40;

        private int RebuildEditor()
        {
            if (opEditor == null) return 0;
            opEditor.SuspendLayout();
            opEditor.Controls.Clear();
            int h = 0;

            if (selOp >= 0 && selOp < ops.Count)
            {
                Op op = ops[selOp];
                lblEditor.Text = (selOp + 1) + " · " + OpEngine.KindName(op.Kind);
                switch (op.Kind)
                {
                    case "replace": h = BuildEditorReplace(op); break;
                    case "remove": h = BuildEditorRemove(op); break;
                    case "insert": h = BuildEditorInsert(op); break;
                    case "case": h = BuildEditorCase(op); break;
                    case "trim": h = BuildEditorTrim(op); break;
                    case "number": h = BuildEditorNumber(op); break;
                    case "date": h = BuildEditorDate(op); break;
                    default: h = RowH; break;
                }
            }
            else
            {
                lblEditor.Text = "";
            }

            // 示例：拿列表第一个文件，跑「到这一步为止」的链，直接看出这一步做了什么
            if (selOp >= 0 && selOp < ops.Count)
            {
                FileItem sit = SampleItemFor(selOp);
                string before = NameWith(sit, selOp);
                string after = NameWith(sit, selOp + 1);
                int sy = h + 6;
                Label cap = Lbl(opEditor, "示例", EdLeft, sy, 40);
                cap.Font = Theme.FontMeta;
                cap.ForeColor = Theme.Faint;
                Label val = Lbl(opEditor, before + "  →  " + after, EdLeft + 40, sy, EdRight - EdLeft - 40);
                val.Font = Theme.FontMono;
                val.ForeColor = Theme.Muted;
                h = sy + 24;
            }

            opEditor.ResumeLayout();
            return h;
        }

        // 取一个示例名字：有文件就用第一个文件，没有就用 IMG_0001.jpg
        private FileItem SampleItem()
        {
            if (items.Count > 0) return items[0];
            // 空列表时用一条虚拟样本，示例行仍然能说明这条规则做什么
            FileItem f = new FileItem();
            f.Name = "IMG_0001.jpg";
            f.Ext = ".jpg";
            f.Base = "IMG_0001";
            f.FullName = Path.Combine(Environment.CurrentDirectory, f.Name);
            return f;
        }

        private string SampleName(int upTo)
        {
            return NameWith(SampleItem(), upTo);
        }

        private string NameWith(FileItem it, int upTo)
        {
            CollectRules();
            RuleSet r = rules;
            string cur = r.RenameAll ? Core.ExpandTemplate(r.Template, it, r, 1) : it.Base;
            OpContext ctx = new OpContext();
            ctx.Item = it;
            ctx.Rules = r;
            ctx.NumberIndex = 1;
            for (int i = 0; i < upTo && i < r.Ops.Count; i++)
                cur = OpEngine.Apply(r.Ops[i], cur, ctx);
            return string.IsNullOrEmpty(cur) ? "（空）" : cur;
        }

        // 示例优先挑「这一步真的会改动」的文件，挑不到再用第一个
        private FileItem SampleItemFor(int step)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (NameWith(items[i], step) != NameWith(items[i], step + 1)) return items[i];
            }
            return SampleItem();
        }

        private Field EditInput(Control host, int x, int y, int w, string value, string ph, EventHandler changed)
        {
            Field f = Input(host, x, y, w);
            f.Placeholder = ph;
            f.Text = value;
            if (changed != null) f.TextChanged += changed;
            return f;
        }

        private Select EditSelect(Control host, int x, int y, int w, string[] labels, string[] values, string current, EventHandler changed)
        {
            Select s = Combo(host, x, y, w, labels);
            s.SelectedIndex = IndexOf(values, current);
            if (changed != null) s.SelectedIndexChanged += changed;
            return s;
        }

        private void ModeChanged(Op op, string[] values, Select s)
        {
            op.Mode = values[s.SelectedIndex];
            RebuildSteps();
            MarkDirty();
        }

        // ---- 设置行小工具
        private void EditLabel(string text, int y)
        {
            Label l = Lbl(opEditor, text, EdLeft, y, 160);
            l.ForeColor = Theme.Muted;
            l.TextAlign = ContentAlignment.MiddleLeft;
        }

        private Field EditField(int y, int w, string value, string ph, EventHandler changed)
        {
            return EditInput(opEditor, EdRight - w, y + 2, w, value, ph, changed);
        }

        private Select EditSelectField(int y, int w, string[] labels, string[] values, string current, EventHandler changed)
        {
            return EditSelect(opEditor, EdRight - w, y + 2, w, labels, values, current, changed);
        }

        private int EditHint(string text, int y)
        {
            Label hint = MkHint(opEditor, text, EdLeft, y + 2, EdRight - EdLeft);
            hint.Height = Theme.S(20);
            return y + Theme.S(30);
        }

        private int BuildEditorReplace(Op op)
        {
            int y = 6;
            Lbl(opEditor, "查找", 12, y, 344); y += 20;
            Field find = Input(opEditor, 12, y, 344);
            find.Placeholder = "要查找的文字";
            find.Text = op.Find;
            find.TextChanged += delegate { op.Find = find.Text; RefreshStepSummaries(); };
            y += 32 + 10;

            Lbl(opEditor, "替换为", 12, y, 344); y += 20;
            Field rep = Input(opEditor, 12, y, 344);
            rep.Placeholder = "留空 = 删掉";
            rep.Text = op.Replace;
            rep.TextChanged += delegate { op.Replace = rep.Text; RefreshStepSummaries(); };
            y += 32 + 10;

            CheckBoxFlat cs = Check(opEditor, "区分大小写", 12, y, 128);
            cs.Checked = op.MatchCase;
            cs.CheckedChanged += delegate { op.MatchCase = cs.Checked; RefreshStepSummaries(); };
            CheckBoxFlat rx = Check(opEditor, "正则表达式", 148, y, 128);
            rx.Checked = op.UseRegex;
            rx.CheckedChanged += delegate { op.UseRegex = rx.Checked; RefreshStepSummaries(); };
            y += 24 + 4;

            MkHint(opEditor, "留空即删除；正则可用 $1 引用捕获组", 12, y, 344);
            return y + 26;
        }

        private int BuildEditorRemove(Op op)
        {
            string[] labels = new string[] { "从指定位置删一段", "从头删掉 N 个字符", "从尾删掉 N 个字符", "删掉指定的文字" };
            string[] values = new string[] { "pos", "start", "end", "text" };
            int y = 6;
            Lbl(opEditor, "怎么删", 12, y, 344); y += 20;
            Select mode = Combo(opEditor, 12, y, 344, labels);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(values, op.Mode));
            mode.SelectedIndexChanged += delegate { op.Mode = values[mode.SelectedIndex]; RebuildSteps(); MarkDirty(); };
            y += 32 + 10;

            string hint;
            if (op.Mode == "text")
            {
                Lbl(opEditor, "要删除的文字", 12, y, 344); y += 20;
                Field t = Input(opEditor, 12, y, 344);
                t.Placeholder = "例如 IMG_";
                t.Text = op.Text;
                t.TextChanged += delegate { op.Text = t.Text; RefreshStepSummaries(); };
                y += 32 + 10;
                hint = "名字里所有出现的地方都会被删掉";
            }
            else if (op.Mode == "pos")
            {
                Lbl(opEditor, "从第几个字符", 12, y, 160); y += 20;
                Field p = Input(opEditor, 12, y, 160);
                p.Text = op.Pos.ToString();
                p.TextChanged += delegate { op.Pos = IntOf(p.Text, 1); RefreshStepSummaries(); };
                Lbl(opEditor, "删除几个", 188, y - 20, 160);
                Field n = Input(opEditor, 188, y, 160);
                n.Text = op.Count.ToString();
                n.TextChanged += delegate { op.Count = IntOf(n.Text, 1); RefreshStepSummaries(); };
                y += 32 + 10;
                hint = "位置从 1 开始；超出长度时按末尾处理";
            }
            else
            {
                string cap = op.Mode == "start" ? "删掉开头几个字符" : "删掉结尾几个字符";
                Lbl(opEditor, cap, 12, y, 160); y += 20;
                Field n2 = Input(opEditor, 12, y, 160);
                n2.Text = op.Count.ToString();
                n2.TextChanged += delegate { op.Count = IntOf(n2.Text, 1); RefreshStepSummaries(); };
                y += 32 + 10;
                hint = "位置从 1 开始；超出长度时按末尾处理";
            }

            MkHint(opEditor, hint, 12, y, 344);
            return y + 26;
        }

        private int BuildEditorInsert(Op op)
        {
            string[] labels = new string[] { "插到开头", "插到结尾", "插到指定位置" };
            string[] values = new string[] { "start", "end", "pos" };
            int y = 6;
            Lbl(opEditor, "插到哪里", 12, y, 344); y += 20;
            Select mode = Combo(opEditor, 12, y, 344, labels);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(values, op.Mode));
            mode.SelectedIndexChanged += delegate { op.Mode = values[mode.SelectedIndex]; RebuildSteps(); MarkDirty(); };
            y += 32 + 10;

            if (op.Mode == "pos")
            {
                Lbl(opEditor, "插到第几个字符前", 12, y, 160); y += 20;
                Field p = Input(opEditor, 12, y, 160);
                p.Text = op.Pos.ToString();
                p.TextChanged += delegate { op.Pos = IntOf(p.Text, 1); RefreshStepSummaries(); };
                y += 32 + 10;
            }

            Lbl(opEditor, "要插入的文字", 12, y, 344); y += 20;
            Field t = Input(opEditor, 12, y, 344);
            t.Placeholder = "例如 旅行_";
            t.Text = op.Text;
            t.TextChanged += delegate { op.Text = t.Text; RefreshStepSummaries(); };
            y += 32 + 10;

            MkHint(opEditor, "位置从 1 开始；超出长度时插到末尾", 12, y, 344);
            return y + 26;
        }

        private int BuildEditorCase(Op op)
        {
            string[] labels = new string[] { "全部小写", "全部大写", "首字母大写", "每词首字母大写" };
            string[] values = new string[] { "lower", "upper", "title", "words" };
            int y = 6;
            Lbl(opEditor, "转换方式", 12, y, 344); y += 20;
            Select mode = Combo(opEditor, 12, y, 344, labels);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(values, op.Mode));
            mode.SelectedIndexChanged += delegate { op.Mode = values[mode.SelectedIndex]; RefreshStepSummaries(); MarkDirty(); };
            y += 32 + 10;

            MkHint(opEditor, "只改字母，数字与符号不动", 12, y, 344);
            return y + 26;
        }

        private int BuildEditorTrim(Op op)
        {
            string[] labels = new string[] { "保留开头 N 个字符", "保留结尾 N 个字符" };
            string[] values = new string[] { "keep-start", "keep-end" };
            int y = 6;
            Lbl(opEditor, "保留哪一端", 12, y, 344); y += 20;
            Select mode = Combo(opEditor, 12, y, 344, labels);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(values, op.Mode));
            mode.SelectedIndexChanged += delegate { op.Mode = values[mode.SelectedIndex]; RefreshStepSummaries(); MarkDirty(); };
            y += 32 + 10;

            Lbl(opEditor, "保留几个字符", 12, y, 160); y += 20;
            Field n = Input(opEditor, 12, y, 160);
            n.Text = op.Count.ToString();
            n.TextChanged += delegate { op.Count = IntOf(n.Text, 1); RefreshStepSummaries(); };
            y += 32 + 10;

            MkHint(opEditor, "超长时保持原样", 12, y, 344);
            return y + 26;
        }

        private int BuildEditorNumber(Op op)
        {
            int y = 6;
            Lbl(opEditor, "起始", 12, y, 96);
            Lbl(opEditor, "步长", 122, y, 96);
            Lbl(opEditor, "位数", 232, y, 96);
            y += 20;
            Field st = Input(opEditor, 12, y, 96); st.Text = op.Start.ToString();
            st.TextChanged += delegate { op.Start = IntOf(st.Text, 1); RefreshStepSummaries(); };
            Field sp = Input(opEditor, 122, y, 96); sp.Text = op.Step.ToString();
            sp.TextChanged += delegate { op.Step = Math.Max(1, IntOf(sp.Text, 1)); RefreshStepSummaries(); };
            Field dg = Input(opEditor, 232, y, 96); dg.Text = op.Digits.ToString();
            dg.TextChanged += delegate { op.Digits = Math.Max(1, Math.Min(9, IntOf(dg.Text, 3))); RefreshStepSummaries(); };
            y += 32 + 10;

            Lbl(opEditor, "分隔符", 12, y, 96);
            Lbl(opEditor, "编号加在", 122, y, 200);
            y += 20;
            Field sep = Input(opEditor, 12, y, 96); sep.Text = op.Sep;
            sep.TextChanged += delegate { op.Sep = sep.Text; RefreshStepSummaries(); };
            string[] pl = new string[] { "加在开头", "加在结尾" };
            string[] pv = new string[] { "prefix", "suffix" };
            Select pos = Combo(opEditor, 122, y, 200, pl);
            pos.SelectedIndex = Math.Max(0, Array.IndexOf(pv, op.Mode));
            pos.SelectedIndexChanged += delegate { op.Mode = pv[pos.SelectedIndex]; RefreshStepSummaries(); MarkDirty(); };
            y += 32 + 10;

            CheckBoxFlat g = Check(opEditor, "每个文件夹单独编号", 12, y, 240);
            g.Checked = op.GroupByFolder;
            g.CheckedChanged += delegate { op.GroupByFolder = g.Checked; MarkDirty(); };
            y += 24 + 4;

            MkHint(opEditor, "按列表顺序编号，拖动可调整", 12, y, 344);
            return y + 26;
        }

        private int BuildEditorDate(Op op)
        {
            int y = 6;
            Lbl(opEditor, "日期格式", 12, y, 160);
            Lbl(opEditor, "加在", 122, y, 200);
            y += 20;
            Field fmt = Input(opEditor, 12, y, 160);
            fmt.Placeholder = "yyyyMMdd";
            fmt.Text = op.Format;
            fmt.TextChanged += delegate { op.Format = fmt.Text; RefreshStepSummaries(); };
            string[] pl = new string[] { "加在开头", "加在结尾" };
            string[] pv = new string[] { "prefix", "suffix" };
            Select pos = Combo(opEditor, 122, y, 200, pl);
            pos.SelectedIndex = Math.Max(0, Array.IndexOf(pv, op.Mode));
            pos.SelectedIndexChanged += delegate { op.Mode = pv[pos.SelectedIndex]; RefreshStepSummaries(); MarkDirty(); };
            y += 32 + 10;

            MkHint(opEditor, "格式如 20261109；写错会按 yyyyMMdd 处理", 12, y, 344);
            return y + 26;
        }
        // ---------------------------------------------------------- 输出
        private void BuildOutputSection(Panel c)
        {
            Label title = Lbl(c, "输出", 16, 12, 200);
            title.Font = Theme.FontLabel;
            title.Height = Theme.S(18);

            chkKeepExt = Check(c, "保持原扩展名", 16, 36, 160);
            chkKeepExt.Checked = true;
            chkKeepExt.CheckedChanged += delegate { LayoutOutputSection(); MarkDirty(); };

            lblExtLabel = Lbl(c, "改为", 16, 36, 60);
            lblExtLabel.Visible = false;

            txtExt = Input(c, 176, 34, 192);
            txtExt.Placeholder = "例如 jpg";
            txtExt.Enabled = false;
            txtExt.Visible = false;

            lblDup = Lbl(c, "冲突时", 16, 76, 64);
            lblDup.TextAlign = ContentAlignment.MiddleLeft;
            cboDup = Combo(c, 84, 70, 284, new string[] { "跳过，并在列表里标红", "自动加 (1)(2) 避让" });

            HookAll(c);
            LayoutOutputSection();
        }

        private void LayoutOutputSection()
        {
            if (cardOutput == null || cboDup == null) return;
            bool keep = chkKeepExt.Checked;
            int w = cardOutput.Width - Theme.S(32);
            chkKeepExt.Bounds = new Rectangle(Theme.S(16), Theme.S(40), Theme.S(160), Theme.S(24));
            txtExt.Visible = !keep;
            txtExt.Enabled = !keep;
            txtExt.Bounds = new Rectangle(Theme.S(176), Theme.S(37), Math.Max(Theme.S(80), w - Theme.S(160)), Theme.S(30));
            lblDup.Bounds = new Rectangle(Theme.S(16), Theme.S(85), Theme.S(64), Theme.S(20));
            cboDup.Bounds = new Rectangle(Theme.S(84), Theme.S(80), w - Theme.S(68), Theme.S(30));
            cardOutput.Height = Theme.S(124);
        }
        // ---------------------------------------------------------- 底部操作条
        /// <summary>只显示末级文件夹名，完整路径留给工具提示。</summary>
        private static string LeafFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            string s = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (s.Length == 0) return dir;
            int i = s.LastIndexOfAny(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (i < 0) return s;
            string leaf = s.Substring(i + 1);
            return leaf.Length > 0 ? leaf : s;
        }


        /// <summary>按列表可用宽度重算列宽，让「所在文件夹」吃掉剩余空间。</summary>
        // ---------------------------------------------------------- 小工具
        private Label Lbl(Control host, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontBody;
            l.ForeColor = Theme.Muted;
            l.BackColor = Color.Transparent;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Bounds = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(20));
            host.Controls.Add(l);
            return l;
        }

        private Field Input(Control host, int x, int y, int w)
        {
            Field f = new Field();
            f.Font = Theme.FontBody;
            f.Bounds = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(32));
            f.TextChanged += delegate { MarkDirty(); };
            host.Controls.Add(f);
            return f;
        }

        private CheckBoxFlat Check(Control host, string text, int x, int y, int w)
        {
            CheckBoxFlat c = new CheckBoxFlat();
            c.Text = text;
            c.Font = Theme.FontBody;
            c.Bounds = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(24));
            c.CheckedChanged += delegate { MarkDirty(); };
            host.Controls.Add(c);
            return c;
        }

        private Select Combo(Control host, int x, int y, int w, string[] items)
        {
            Select c = new Select();
            c.Font = Theme.FontBody;
            c.Bounds = new Rectangle(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(32));
            c.AddRange(items ?? new string[0]);
            c.TextChanged += delegate { MarkDirty(); };
            host.Controls.Add(c);
            return c;
        }

        private void HookAll(Control root)
        {
            // 每个自绘输入控件在创建处已经挂好「规则变化 → 重新预览」，
            // 这里只负责把嵌套容器的子控件也覆盖到（替换规则行是动态生成的）。
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                if (c.HasChildren) HookAll(c);
            }
        }

        private void MarkDirty()
        {
            if (!ready) return;
            // 自动化测试要求同步可见，交互场景用短去抖合并连续输入
            if (uiTest || refreshTimer == null) { RefreshPreview(); return; }
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        /// <summary>把待执行的去抖刷新立刻做完（执行改名、撤销等操作前调用）。</summary>
        private void FlushRefresh()
        {
            if (refreshTimer == null || !refreshTimer.Enabled) return;
            refreshTimer.Stop();
            if (ready) RefreshPreview();
        }

        // ================================================================== 规则读写
        private void CollectRules()
        {
            if (txtExt == null) return;
            RuleSet r = new RuleSet();
            r.RenameAll = renamingAll;
            r.Template = txtTemplate.Text;
            r.KeepExt = chkKeepExt.Checked;
            r.NewExt = chkKeepExt.Checked ? "" : (txtExt.Text ?? "").Trim();
            r.ResolveDuplicates = cboDup != null && cboDup.SelectedIndex == 1;
            r.Ops = new List<Op>();
            for (int i = 0; i < ops.Count; i++) r.Ops.Add(ops[i]);
            rules = r;
        }

        private void ApplyRulesToUi()
        {
            if (txtExt == null) return;
            bool old = ready;
            ready = false;

            RuleSet r = rules ?? new RuleSet();
            if ((r.Ops == null || r.Ops.Count == 0) && OpEngine.HasLegacy(r)) r.Ops = OpEngine.FromLegacy(r);

            ops.Clear();
            if (r.Ops != null)
                for (int i = 0; i < r.Ops.Count; i++) ops.Add(r.Ops[i].Clone());
            selOp = -1;   // 默认不展开编辑器：先让用户看到整块规则，点了再展开

            txtTemplate.Text = r.Template ?? "";
            chkKeepExt.Checked = r.KeepExt;
            txtExt.Text = r.NewExt ?? "";
            txtExt.Enabled = !r.KeepExt;
            if (cboDup != null) cboDup.SelectedIndex = r.ResolveDuplicates ? 1 : 0;

            SetRenameAll(r.RenameAll);
            RebuildSteps();
            ready = old;
        }

        private static int IntOf(string s, int def)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private static string CaseModeOf(int idx)
        {
            switch (idx)
            {
                case 1: return "lower";
                case 2: return "upper";
                case 3: return "title";
                default: return "keep";
            }
        }

        private static int CaseIndexOf(string mode)
        {
            if (mode == "lower") return 1;
            if (mode == "upper") return 2;
            if (mode == "title") return 3;
            return 0;
        }

        // ================================================================== 预览与列表
        private void RefreshPreview()
        {
            if (!ready) return;
            if (refreshTimer != null) refreshTimer.Stop();
            CollectRules();
            preview = Core.BuildPreview(items, rules);
            RebuildList();
            RebuildSources();
            UpdateCounts();
            UpdateButtons();
        }

        private bool FilterActive
        {
            get { return filterText.Length > 0 || filterStatus != 0; }
        }

        /// <summary>列表条左侧：现在看到的是哪一批文件。</summary>
        private void UpdateEmptyState()
        {
            if (empty == null) return;
            bool noFiles = items.Count == 0;
            bool noHits = !noFiles && list != null && list.Items.Count == 0;
            bool want = noFiles || noHits;
            if (empty.Visible != want) empty.Visible = want;
            if (noHits)
            {
                empty.Title = "没有匹配的文件";
                empty.Sub = "换一个关键词，或者把筛选切回「全部状态」。";
                empty.Step = "当前筛选条件不会改动任何文件。";
                empty.ActionVisible = false;
            }
            else
            {
                empty.Title = "把文件拖到这里";
                empty.Sub = "或者从左侧「添加文件 / 添加文件夹」开始";
                empty.Step = "支持整个文件夹递归导入 · 拖拽行可以调整顺序";
                empty.ActionVisible = true;
            }
            Control front = empty.Visible ? (Control)empty : (Control)list;
            if (want && front != null && front.Parent != null && front.Parent.IsHandleCreated
                && front.Parent.Controls.GetChildIndex(front) != 0) front.BringToFront();
        }

        private void UpdateCounts()
        {
            if (lblCntOk == null) return;
            int ok = 0, conflict = 0, same = 0, err = 0;
            for (int i = 0; i < preview.Count; i++)
            {
                PreviewItem p = preview[i];
                if (p == null) continue;
                if (p.Status == PreviewStatus.Ok) ok++;
                else if (p.Status == PreviewStatus.Conflict) conflict++;
                else if (p.Status == PreviewStatus.NoChange) same++;
                else err++;
            }
            lblCntOk.Caption = "将改名 " + ok;
            lblCntConflict.Caption = "冲突 " + conflict;
            lblCntSame.Caption = "未变化 " + same;
            lblCntError.Caption = "错误 " + err;
            lblCntError.Visible = err > 0;
            lblCntOk.Emphasis = ok > 0;

            btnRun.Enabled = ok > 0;
            btnRun.Text = ok > 0 ? ("执行重命名（" + ok + "）") : "执行重命名";
            btnRun.Width = Theme.S(ok > 99 ? 176 : 158);
            LayoutHeader();
            LayoutCenterFoot();
            UpdateListMeta();
            empty.Invalidate();
        }

        private void UpdateButtons()
        {
            if (btnRemove == null) return;
            bool has = list != null && list.SelectedIndices.Count > 0;
            btnRemove.Enabled = has;
            if (btnClear != null) btnClear.Enabled = items.Count > 0;
            btnExport.Enabled = preview.Count > 0;
            if (btnDelPreset != null) btnDelPreset.Enabled = presetList != null && presetList.SelectedIndices.Count > 0;
        }

        private void LayoutAll()
        {
            LayoutLeftRail();
            LayoutCenterTop();
            LayoutCenterFoot();
            LayoutBaseSection();
            LayoutStepsSection();
            LayoutOutputSection();
            AdjustColumns();
        }

        private void AdjustColumns()
        {
            if (list == null || list.Columns.Count < 4) return;
            int w = list.ClientSize.Width - Theme.S(2);
            if (w <= 0) return;
            int c0 = Theme.S(40);
            int c3 = Theme.S(92);
            int rest = Math.Max(Theme.S(140), w - c0 - c3);
            int c1 = (int)(rest * 0.46);
            int c2 = rest - c1;
            // 宽度没变就不碰列集合：改列宽会让整个 ListView 重新布局并闪一下
            if (list.Columns[0].Width == c0 && list.Columns[1].Width == c1
                && list.Columns[2].Width == c2 && list.Columns[3].Width == c3) return;
            list.BeginUpdate();
            try
            {
                list.Columns[0].Width = c0;
                list.Columns[1].Width = c1;
                list.Columns[2].Width = c2;
                list.Columns[3].Width = c3;
            }
            finally { list.EndUpdate(); }
        }

        private void UpdateListMeta()
        {
            if (lblCount == null) return;
            int shown = list == null ? 0 : list.Items.Count;
            bool narrowed = filterDir.Length > 0 || FilterActive;
            string scope = filterDir.Length > 0 ? LeafFolder(filterDir) : (FilterActive ? "筛选结果" : "全部文件");
            lblCount.Text = scope;
            lblCountSub.Text = narrowed ? (shown + " / " + items.Count + " 个文件") : (items.Count + " 个文件");
            if (lblCountSub.Width > 0)
            {
                using (Graphics g = lblCountSub.CreateGraphics())
                {
                    int need = TextRenderer.MeasureText(g, lblCountSub.Text, lblCountSub.Font).Width + Theme.S(6);
                    if (need > lblCountSub.Width) lblCountSub.Width = need;
                }
            }
        }

        private void RebuildSources()
        {
            if (srcList == null) return;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> dirs = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                string d = items[i].Dir ?? "";
                if (!counts.ContainsKey(d)) { counts[d] = 0; dirs.Add(d); }
                counts[d] = counts[d] + 1;
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);

            suppressRail = true;
            srcList.BeginUpdate();
            srcList.Items.Clear();
            ListViewItem all = new ListViewItem("全部文件");
            all.SubItems.Add(items.Count.ToString(CultureInfo.InvariantCulture));
            all.Tag = "";
            srcList.Items.Add(all);
            for (int i = 0; i < dirs.Count; i++)
            {
                ListViewItem row = new ListViewItem(LeafFolder(dirs[i]));
                row.SubItems.Add(counts[dirs[i]].ToString(CultureInfo.InvariantCulture));
                row.ToolTipText = dirs[i];
                row.Tag = dirs[i];
                srcList.Items.Add(row);
            }
            int sel = 0;
            for (int i = 0; i < srcList.Items.Count; i++)
                if (((string)srcList.Items[i].Tag) == filterDir) { sel = i; break; }
            if (sel >= 0 && sel < srcList.Items.Count) srcList.Items[sel].Selected = true;
            srcList.EndUpdate();
            suppressRail = false;
            LayoutLeftRail();
        }

        private void ReloadPresetList()
        {
            if (presetList == null) return;
            suppressRail = true;
            presetList.BeginUpdate();
            presetList.Items.Clear();
            List<string> names = Features.ListPresets();
            for (int i = 0; i < names.Count; i++)
            {
                ListViewItem row = new ListViewItem(names[i]);
                row.SubItems.Add("");
                presetList.Items.Add(row);
            }
            presetList.EndUpdate();
            suppressRail = false;
            if (lblPresetEmpty != null) { lblPresetEmpty.Visible = presetList.Items.Count == 0; lblPresetEmpty.BringToFront(); }
        }

        private void RebuildList()
        {
            if (list == null) return;
            int keepIndex = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
            string keepName = keepIndex >= 0 && keepIndex < visible.Count && visible[keepIndex] != null
                ? (visible[keepIndex].Item == null ? "" : visible[keepIndex].Item.FullName) : "";

            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                visible.Clear();

                for (int i = 0; i < preview.Count; i++)
                {
                    PreviewItem p = preview[i];
                    if (p == null || p.Item == null) continue;
                    if (filterDir.Length > 0 && !string.Equals(p.Item.Dir, filterDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (filterText.Length > 0)
                    {
                        bool hit = p.Item.Name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (p.NewName != null && p.NewName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!hit) continue;
                    }
                    if (filterStatus != 0)
                    {
                        bool match = (filterStatus == 1 && p.Status == PreviewStatus.Ok)
                                  || (filterStatus == 2 && p.Status == PreviewStatus.Conflict)
                                  || (filterStatus == 3 && p.Status == PreviewStatus.NoChange)
                                  || (filterStatus == 4 && p.Status == PreviewStatus.Error);
                        if (!match) continue;
                    }

                    ListViewItem row = new ListViewItem(p.Order.ToString(CultureInfo.InvariantCulture));
                    row.SubItems.Add(p.Item.Name);
                    row.SubItems.Add(p.Status == PreviewStatus.NoChange ? "—" : (p.NewName ?? ""));
                    row.SubItems.Add(StatusText(p.Status));
                    row.UseItemStyleForSubItems = false;
                    row.Tag = p;
                    string tipText = p.Message;
                    if (!string.IsNullOrEmpty(p.Item.Dir))
                        tipText = string.IsNullOrEmpty(tipText) ? p.Item.Dir : tipText + "\r\n" + p.Item.Dir;
                    if (!string.IsNullOrEmpty(tipText)) row.ToolTipText = tipText;

                    list.Items.Add(row);
                    visible.Add(p);

                    if (keepName.Length > 0 && p.Item.FullName == keepName)
                    {
                        row.Selected = true;
                        row.Focused = true;
                    }
                }

                // 列宽在批量更新内部调整，避免先看到旧列宽再跳一次
                AdjustColumns();
            }
            finally { list.EndUpdate(); }

            UpdateEmptyState();
            UpdateButtons();
            UpdateListMeta();
        }

        private static string StatusText(PreviewStatus st)
        {
            switch (st)
            {
                case PreviewStatus.Ok: return "将改名";
                case PreviewStatus.Conflict: return "冲突";
                case PreviewStatus.NoChange: return "未变化";
                default: return "错误";
            }
        }

        private void ListDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle b = e.Bounds;
            PreviewItem p = e.Item.Tag as PreviewItem;
            TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

            switch (e.ColumnIndex)
            {
                case 0:
                    TextRenderer.DrawText(g, e.SubItem.Text, Theme.FontNum,
                        new Rectangle(b.X, b.Y, b.Width - Theme.S(10), b.Height), Theme.Faint,
                        baseFlags | TextFormatFlags.Right);
                    break;
                case 1:
                    {
                        Color c = (p != null && p.Status == PreviewStatus.NoChange) ? Theme.Faint : Theme.Muted;
                        TextRenderer.DrawText(g, e.SubItem.Text, Theme.FontBody,
                            new Rectangle(b.X + Theme.S(2), b.Y, b.Width - Theme.S(14), b.Height), c, baseFlags);
                    }
                    break;
                case 2:
                    if (p != null)
                        DrawNewName(g, new Rectangle(b.X + Theme.S(2), b.Y, b.Width - Theme.S(12), b.Height), p);
                    else
                        TextRenderer.DrawText(g, e.SubItem.Text, Theme.FontBody,
                            new Rectangle(b.X + Theme.S(2), b.Y, b.Width - Theme.S(12), b.Height), Theme.Text, baseFlags);
                    break;
                default:
                    {
                        // 状态用「小圆点 + 文字」，不用胶囊：一屏十几行时不吵
                        Color dot = Theme.Faint;
                        Color fg = Theme.Muted;
                        if (p != null)
                        {
                            switch (p.Status)
                            {
                                case PreviewStatus.Ok: dot = Theme.Accent; fg = Theme.Muted; break;
                                case PreviewStatus.Conflict: dot = Theme.Danger; fg = Theme.Danger; break;
                                case PreviewStatus.Error: dot = Theme.Warn; fg = Theme.Warn; break;
                                case PreviewStatus.NoChange: dot = Theme.BorderStrong; fg = Theme.Faint; break;
                            }
                        }
                        int d = Theme.S(6);
                        int cx = b.X + Theme.S(10);
                        int cy = b.Y + (b.Height - d) / 2;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (SolidBrush sb = new SolidBrush(dot)) g.FillEllipse(sb, cx, cy, d, d);
                        TextRenderer.DrawText(g, e.SubItem.Text, Theme.FontSmall,
                            new Rectangle(cx + d + Theme.S(7), b.Y, b.Width - d - Theme.S(20), b.Height), fg, baseFlags);
                    }
                    break;
            }
        }

        private void DeletePresetDialog()
        {
            if (presetList == null || presetList.SelectedItems.Count == 0) { SetStatus("先在上面的方案列表里选一个方案"); return; }
            string name = presetList.SelectedItems[0].Text;
            if (MessageBox.Show(this, "删除方案「" + name + "」？", "删除方案", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { Features.DeletePreset(name); } catch { }
            ReloadPresetList();
            SetStatus("已删除方案「" + name + "」");
        }

        private void AddFilesDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = "选择要重命名的文件";
                d.Filter = "全部文件|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
            }
        }

        private void AddFolderDialog(bool recurse)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择文件夹（会加入其中的文件）";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;

                bool rec = recurse;
                if (!rec)
                {
                    DialogResult r = MessageBox.Show(this,
                        "要把子文件夹里的文件也一起加进来吗？\n\n是 = 包含子文件夹\n否 = 只加当前这一层",
                        "添加文件夹", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return;
                    rec = r == DialogResult.Yes;
                }
                AddFolder(d.SelectedPath, rec);
            }
        }

        private void AddFolder(string path, bool recurse)
        {
            int skipped;
            List<FileItem> got = Core.GetPathItems(path, recurse, out skipped);
            AddItems(got);
            if (skipped > 0) SetStatus("已跳过 " + skipped + " 个隐藏/系统文件");
        }

        private void AddPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            List<FileItem> got = new List<FileItem>();
            int skipped = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    if (Directory.Exists(p))
                    {
                        int sk;
                        got.AddRange(Core.GetPathItems(p, true, out sk));
                        skipped += sk;
                    }
                    else if (File.Exists(p))
                    {
                        got.Add(new FileItem(p));
                    }
                }
                catch { }
            }
            AddItems(got);
            if (skipped > 0) SetStatus("已跳过 " + skipped + " 个隐藏/系统文件");
        }

        private void AddItems(List<FileItem> got)
        {
            if (got == null || got.Count == 0) return;
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++) have.Add(items[i].FullName);
            int added = 0;
            for (int i = 0; i < got.Count; i++)
            {
                FileItem it = got[i];
                if (it == null || string.IsNullOrEmpty(it.FullName)) continue;
                if (have.Contains(it.FullName)) continue;
                have.Add(it.FullName);
                items.Add(it);
                added++;
            }
            if (added == 0) { SetStatus("没有新增文件（可能已在列表里）"); return; }
            RefreshPreview();
            SetStatus("");
        }

        private void RemoveSelected()
        {
            if (list.SelectedIndices.Count == 0) return;
            List<FileItem> kill = new List<FileItem>();
            for (int i = 0; i < list.SelectedIndices.Count; i++)
            {
                int idx = list.SelectedIndices[i];
                if (idx >= 0 && idx < visible.Count && visible[idx] != null) kill.Add(visible[idx].Item);
            }
            for (int i = 0; i < kill.Count; i++) items.Remove(kill[i]);
            RefreshPreview();
            SetStatus("已移除 " + kill.Count + " 个文件");
        }

        private void ClearAll()
        {
            if (items.Count == 0) return;
            items.Clear();
            RefreshPreview();
            SetStatus("列表已清空");
        }

        // ================================================================== 排序与顺序调整
        private int ModelIndexOf(FileItem it)
        {
            for (int i = 0; i < items.Count; i++) if (ReferenceEquals(items[i], it)) return i;
            return -1;
        }

        private FileItem SelectedItem()
        {
            if (list.SelectedIndices.Count == 0) return null;
            int idx = list.SelectedIndices[0];
            if (idx < 0 || idx >= visible.Count || visible[idx] == null) return null;
            return visible[idx].Item;
        }

        private void MoveSelected(int delta)
        {
            FileItem it = SelectedItem();
            if (it == null) return;
            if (FilterActive) { SetStatus("筛选状态下不能调整顺序，请先清空筛选"); return; }
            int from = ModelIndexOf(it);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= items.Count) return;
            items.RemoveAt(from);
            items.Insert(to, it);
            RefreshPreview();
            SelectModel(to);
            SetStatus("已移动到第 " + (to + 1) + " 位");
        }

        private void MoveSelectedTo(int mode)
        {
            FileItem it = SelectedItem();
            if (it == null) return;
            if (FilterActive) { SetStatus("筛选状态下不能调整顺序，请先清空筛选"); return; }
            int from = ModelIndexOf(it);
            if (from < 0) return;
            int to = mode == -1 ? 0 : items.Count - 1;
            if (from == to) return;
            items.RemoveAt(from);
            items.Insert(to, it);
            RefreshPreview();
            SelectModel(to);
        }

        private void SelectModel(int index)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                if (visible[i] == null || visible[i].Item == null) continue;
                if (ModelIndexOf(visible[i].Item) == index)
                {
                    list.SelectedIndices.Clear();
                    list.Items[i].Selected = true;
                    list.Items[i].Focused = true;
                    list.EnsureVisible(i);
                    return;
                }
            }
        }

        private void ShowSortMenu(Control anchor)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Theme.FontBody;
            m.Items.Add("上移", null, delegate { MoveSelected(-1); });
            m.Items.Add("下移", null, delegate { MoveSelected(1); });
            m.Items.Add("置顶", null, delegate { MoveSelectedTo(-1); });
            m.Items.Add("置底", null, delegate { MoveSelectedTo(-2); });
            m.Items.Add("反转顺序", null, delegate
            {
                if (FilterActive) { SetStatus("筛选状态下不能调整顺序，请先清空筛选"); return; }
                if (items.Count < 2) return;
                items.Reverse();
                RefreshPreview();
                SetStatus("已反转顺序");
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("按文件名（自然序）", null, delegate { SortBy("name", false); });
            m.Items.Add("按扩展名", null, delegate { SortBy("ext", false); });
            m.Items.Add("按修改时间（旧→新）", null, delegate { SortBy("date", false); });
            m.Items.Add("按修改时间（新→旧）", null, delegate { SortBy("date", true); });
            m.Items.Add("按大小（小→大）", null, delegate { SortBy("size", false); });
            m.Items.Add("按大小（大→小）", null, delegate { SortBy("size", true); });
            m.Items.Add("按所在文件夹", null, delegate { SortBy("dir", false); });
            m.Show(anchor, new Point(0, anchor.Height));
        }

        private void SortBy(string key, bool desc)
        {
            Comparison<FileItem> cmp;
            if (key == "ext")
                cmp = delegate(FileItem a, FileItem b)
                {
                    int c = string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : CompareNatural(a.Name, b.Name);
                };
            else if (key == "date")
                cmp = delegate(FileItem a, FileItem b) { return a.LastWrite.CompareTo(b.LastWrite); };
            else if (key == "size")
                cmp = delegate(FileItem a, FileItem b) { return a.Size.CompareTo(b.Size); };
            else if (key == "dir")
                cmp = delegate(FileItem a, FileItem b)
                {
                    int c = string.Compare(a.Dir, b.Dir, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : CompareNatural(a.Name, b.Name);
                };
            else
                cmp = delegate(FileItem a, FileItem b) { return CompareNatural(a.Name, b.Name); };

            // 稳定排序：先记录原始位置，比较相等时保持原序
            List<KeyValuePair<FileItem, int>> tmp = new List<KeyValuePair<FileItem, int>>(items.Count);
            for (int i = 0; i < items.Count; i++) tmp.Add(new KeyValuePair<FileItem, int>(items[i], i));
            tmp.Sort(delegate(KeyValuePair<FileItem, int> x, KeyValuePair<FileItem, int> y)
            {
                int c = cmp(x.Key, y.Key);
                if (c == 0) return x.Value.CompareTo(y.Value);
                return c;
            });

            items.Clear();
            if (desc) for (int i = tmp.Count - 1; i >= 0; i--) items.Add(tmp[i].Key);
            else for (int i = 0; i < tmp.Count; i++) items.Add(tmp[i].Key);

            RefreshPreview();
            SetStatus("已按 " + SortName(key) + (desc ? " 倒序" : "") + " 排序");
        }

        private static string SortName(string key)
        {
            switch (key)
            {
                case "ext": return "扩展名";
                case "date": return "修改时间";
                case "size": return "大小";
                case "dir": return "所在文件夹";
                default: return "文件名";
            }
        }

        private static int CompareNatural(string a, string b)
        {
            string ka = Core.NaturalKey(a ?? "");
            string kb = Core.NaturalKey(b ?? "");
            return string.Compare(ka, kb, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------- 拖拽
        private void ListItemDrag(object sender, ItemDragEventArgs e)
        {
            if (items.Count < 2) return;
            if (FilterActive) { SetStatus("筛选状态下不能拖拽排序，请先清空筛选"); return; }
            if (list.SelectedItems.Count != 1) return;
            ListViewItem row = list.SelectedItems[0];
            dragging = row.Tag as PreviewItem != null ? ((PreviewItem)row.Tag).Item : null;
            if (dragging == null) return;
            list.DoDragDrop(row, DragDropEffects.Move);
        }

        private void ListDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; list.InsertionMark.Index = -1; return; }
            if (dragging == null) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Move;
            Point p = list.PointToClient(new Point(e.X, e.Y));
            ListViewItem target = list.GetItemAt(p.X, p.Y);
            if (target == null) { list.InsertionMark.Index = -1; return; }
            list.InsertionMark.Index = target.Index;
            list.InsertionMark.AppearsAfterItem = p.Y > target.Bounds.Top + target.Bounds.Height / 2;
        }

        private void ListDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddPaths(files);
                return;
            }
            int mark = list.InsertionMark.Index;
            bool after = list.InsertionMark.AppearsAfterItem;
            list.InsertionMark.Index = -1;
            FileItem draggedItem = dragging;
            dragging = null;
            if (mark < 0 || draggedItem == null) return;
            if (mark >= visible.Count || visible[mark] == null) return;
            MoveItemTo(draggedItem, visible[mark].Item, after);
        }

        private void MoveItemTo(FileItem draggedItem, FileItem targetItem, bool after)
        {
            if (draggedItem == null || targetItem == null || ReferenceEquals(draggedItem, targetItem)) return;
            int from = ModelIndexOf(draggedItem);
            int to = ModelIndexOf(targetItem);
            if (from < 0 || to < 0) return;
            items.RemoveAt(from);
            if (to > from) to--;
            if (after) to++;
            if (to < 0) to = 0;
            if (to > items.Count) to = items.Count;
            items.Insert(to, draggedItem);
            RefreshPreview();
            SelectModel(to);
            SetStatus("已移动到第 " + (to + 1) + " 位");
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && !e.Control) { RemoveSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                for (int i = 0; i < list.Items.Count; i++) list.Items[i].Selected = true;
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
        }

        // 窗体级快捷键
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.O)) { AddFilesDialog(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.O)) { AddFolderDialog(false); return true; }
            if (keyData == Keys.F5) { RefreshPreview(); SetStatus("已刷新预览"); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { QuickUndo(); return true; }
            if (keyData == (Keys.Control | Keys.E)) { ExportCsvDialog(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ================================================================== 执行/撤销/导出
        private void ExecuteRename(bool confirm)
        {
            FlushRefresh();
            CollectRules();
            List<Mapping> map = new List<Mapping>();
            for (int i = 0; i < preview.Count; i++)
            {
                if (preview[i] != null && preview[i].Status == PreviewStatus.Ok) map.Add(new Mapping(preview[i].Item.FullName, preview[i].Target));
            }
            if (map.Count == 0) { SetStatus("没有需要改名的文件"); return; }

            if (confirm)
            {
                DialogResult r = MessageBox.Show(this,
                    "即将重命名 " + map.Count + " 个文件。\n\n改名后可以用「撤销历史」或 Ctrl+Z 还原。要继续吗？",
                    "确认重命名", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
            }

            RenameResult res = Core.BatchRename(map, logDirOverride, "rename");
            SyncModelAfterRename(res.Done);
            RefreshPreview();

            StringBuilder sb = new StringBuilder();
            sb.Append(res.Message);
            if (res.Errors.Count > 0) sb.Append("；失败 " + res.Errors.Count + " 个");
            SetStatus(sb.ToString());

            if (res.Errors.Count > 0)
            {
                StringBuilder detail = new StringBuilder();
                for (int i = 0; i < Math.Min(6, res.Errors.Count); i++) detail.AppendLine("· " + res.Errors[i]);
                if (res.Errors.Count > 6) detail.AppendLine("… 还有 " + (res.Errors.Count - 6) + " 条，详见日志");
                if (confirm) MessageBox.Show(this, detail.ToString(), "部分文件改名失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SyncModelAfterRename(List<Mapping> done)
        {
            if (done == null || done.Count == 0) return;
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < done.Count; i++) map[done[i].From] = done[i].To;
            for (int i = 0; i < items.Count; i++)
            {
                string to;
                if (map.TryGetValue(items[i].FullName, out to))
                {
                    try { items[i].SetPath(to); }
                    catch { }
                }
            }
        }

        private void QuickUndo()
        {
            FlushRefresh();
            RenameResult res = Core.Undo(logDirOverride == null ? Core.DefaultLogDir() : logDirOverride);
            SyncModelAfterRename(null);
            // 撤销后按真实磁盘状态刷新每个条目
            for (int i = 0; i < items.Count; i++)
            {
                try { items[i].SetPath(items[i].FullName); } catch { }
            }
            RefreshPreview();
            SetStatus(res.Message);
        }

        private void ShowUndoHistory()
        {
            List<UndoInfo> history = Features.GetUndoHistory(Core.DefaultLogDir());
            using (Form dlg = new Form())
            {
                dlg.Text = "撤销历史";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.BackColor = Theme.Bg;
                dlg.Font = Theme.FontBody;
                dlg.Size = new Size(Theme.S(760), Theme.S(460));

                ListView lv = new ListView();
                lv.Dock = DockStyle.Fill;
                lv.View = View.Details;
                lv.FullRowSelect = true;
                lv.MultiSelect = false;
                lv.Font = Theme.FontBody;
                lv.BorderStyle = BorderStyle.None;
                lv.Columns.Add("时间", Theme.S(170));
                lv.Columns.Add("文件数", Theme.S(80), HorizontalAlignment.Right);
                lv.Columns.Add("所在文件夹", Theme.S(420));
                for (int i = 0; i < history.Count; i++)
                {
                    ListViewItem it = new ListViewItem(string.IsNullOrEmpty(history[i].TimeText) ? "—" : history[i].TimeText);
                    it.SubItems.Add(history[i].Count.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(history[i].Summary);
                    it.Tag = history[i];
                    lv.Items.Add(it);
                }

                Panel bar = new Panel();
                bar.Dock = DockStyle.Bottom;
                bar.Height = Theme.S(56);
                bar.BackColor = Theme.Surface;

                Label tip = new Label();
                tip.Text = history.Count == 0 ? "还没有可撤销的改名记录" : "选中一条记录，点「撤销」把文件改回原名";
                tip.ForeColor = Theme.Muted;
                tip.Font = Theme.FontSmall;
                tip.AutoSize = false;
                tip.Bounds = new Rectangle(Theme.S(16), Theme.S(18), Theme.S(400), Theme.S(22));
                bar.Controls.Add(tip);

                FlatButton ok = new FlatButton();
                ok.Text = "撤销选中";
                ok.Kind = ButtonKind.Danger;
                ok.Bounds = new Rectangle(bar.Width - Theme.S(130), Theme.S(12), Theme.S(114), Theme.S(32));
                bar.Resize += delegate { ok.Left = bar.Width - Theme.S(130); };
                ok.Enabled = history.Count > 0;
                ok.Click += delegate
                {
                    if (lv.SelectedItems.Count == 0) return;
                    UndoInfo info = (UndoInfo)lv.SelectedItems[0].Tag;
                    RenameResult r = Core.UndoAt(info.Path);
                    MessageBox.Show(dlg, r.Message, "撤销结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };
                bar.Controls.Add(ok);

                dlg.Controls.Add(lv);
                dlg.Controls.Add(bar);
                dlg.ShowDialog(this);
            }
            for (int i = 0; i < items.Count; i++) { try { items[i].SetPath(items[i].FullName); } catch { } }
            RefreshPreview();
        }

        private void ExportCsvDialog()
        {
            if (preview.Count == 0) { SetStatus("列表是空的，没有可导出的内容"); return; }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "导出预览清单";
                d.Filter = "CSV 文件|*.csv";
                d.FileName = "改名预览_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (Features.ExportCsv(d.FileName, preview, out err))
                    SetStatus("已导出清单：" + d.FileName);
                else
                    MessageBox.Show(this, "导出失败：" + err, "导出清单", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SavePresetDialog()
        {
            string name = Prompt("保存方案", "给这套规则起个名字：", "我的方案");
            if (string.IsNullOrEmpty(name)) return;
            CollectRules();
            if (Features.SavePreset(name, rules))
            {
                ReloadPresetList();
                SetStatus("已保存方案：" + name);
            }
            else SetStatus("保存方案失败");
        }

        private string Prompt(string title, string label, string def)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.BackColor = Theme.Surface;
                dlg.Font = Theme.FontBody;
                dlg.ClientSize = new Size(Theme.S(380), Theme.S(120));

                Label l = new Label();
                l.Text = label;
                l.AutoSize = false;
                l.Bounds = new Rectangle(Theme.S(16), Theme.S(14), Theme.S(348), Theme.S(22));
                dlg.Controls.Add(l);

                TextBox t = new TextBox();
                t.Text = def;
                t.Bounds = new Rectangle(Theme.S(16), Theme.S(40), Theme.S(348), Theme.S(24));
                dlg.Controls.Add(t);

                FlatButton ok = new FlatButton();
                ok.Text = "确定";
                ok.Kind = ButtonKind.Primary;
                ok.Bounds = new Rectangle(Theme.S(192), Theme.S(78), Theme.S(88), Theme.S(30));
                ok.DialogResult = DialogResult.OK;
                dlg.Controls.Add(ok);

                FlatButton cancel = new FlatButton();
                cancel.Text = "取消";
                cancel.Kind = ButtonKind.Secondary;
                cancel.Bounds = new Rectangle(Theme.S(288), Theme.S(78), Theme.S(76), Theme.S(30));
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return t.Text.Trim();
            }
        }

        private void SetStatus(string msg)
        {
            if (lblMsg == null) return;
            lblMsg.Text = msg ?? "";
        }

        // ================================================================== 冒烟测试
        private void RunSmokeTest()
        {
            Reporter rep = new Reporter("UiTest");
            string root = Path.Combine(Path.GetTempPath(), "rn_uitest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                for (int i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(root, "a" + i + ".txt"), "x" + i, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(root, "sub", "b1.log"), "b", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(root, "sub", "b2.log"), "b", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                rep.Check("准备测试目录", false, ex.Message);
            }

            logDirOverride = Path.Combine(root, "logs");
            rep.Line("测试目录：" + root);

            // 1 初始状态（规则归零，避免被上次保存的设置影响）
            rules = new RuleSet();
            ApplyRulesToUi();
            RefreshPreview();

            rep.Check("初始列表为空", items.Count == 0, "count=" + items.Count);
            rep.Check("空状态引导可见", empty.Visible, "empty.Visible=" + empty.Visible);
            int iEmpty = empty.Parent.Controls.GetChildIndex(empty);
            int iList = list.Parent.Controls.GetChildIndex(list);
            rep.Check("空状态盖在列表之上", iEmpty >= 0 && iList >= 0 && iEmpty < iList, "empty.index=" + iEmpty + " list.index=" + iList);
            rep.Check("列表头区高 54 且显示计数", centerTop.Height == Theme.S(54) && lblCount.Text.Length > 0,
                "topH=" + centerTop.Height + " count=" + lblCount.Text);
            rep.Check("列表行高 42", list.SmallImageList != null && list.SmallImageList.ImageSize.Height == Theme.S(42),
                "rowH=" + (list.SmallImageList == null ? -1 : list.SmallImageList.ImageSize.Height));
            rep.Check("表格 4 列", list.Columns.Count == 4, "cols=" + list.Columns.Count);
            rep.Check("起始名字默认是保留原名", !renamingAll, "renamingAll=" + renamingAll);

            // 2 添加文件
            AddPaths(new string[] { Path.Combine(root, "a1.txt"), Path.Combine(root, "a2.txt"), Path.Combine(root, "a3.txt") });
            rep.Check("添加 3 个文件", items.Count == 3, "count=" + items.Count);
            rep.Check("列表行数 3", list.Items.Count == 3, "rows=" + list.Items.Count);
            rep.Check("空状态已隐藏", !empty.Visible, "empty.Visible=" + empty.Visible);

            // 3 步骤链：插入前缀
            ops.Add(OpEngine.Create("insert"));
            ops[0].Mode = "start";
            ops[0].Text = "P_";
            selOp = 0;
            RebuildSteps();
            MarkDirty();
            rep.Check("插入步骤生效", preview.Count == 3 && preview[0].NewName == "P_a1.txt",
                preview.Count > 0 ? preview[0].NewName : "(空)");

            // 4 步骤链：追加编号（加在名字后面）
            Op num = OpEngine.Create("number");
            num.Mode = "prefix";
            num.Sep = "_";
            num.Start = 1;
            num.Step = 1;
            num.Digits = 2;
            ops.Add(num);
            selOp = 1;
            RebuildSteps();
            MarkDirty();
            rep.Check("编号步骤生效", preview[0].NewName == "01_P_a1.txt", preview[0].NewName);

            // 5 调整步骤顺序：编号挪到最前
            MoveOp(-1);
            rep.Check("上移步骤改变结果", preview[0].NewName == "P_01_a1.txt", preview[0].NewName);
            rep.Check("选中步骤跟着移动", selOp == 0, "selOp=" + selOp);

            // 6 删除步骤
            selOp = 0;
            DeleteOp();
            rep.Check("删除步骤后回到前缀", preview[0].NewName == "P_a1.txt", preview[0].NewName);
            rep.Check("步骤列表同步", ops.Count == 1 && stepHost.Controls.Count == 1, "ops=" + ops.Count + " rows=" + stepHost.Controls.Count);

            // 7 完全重命名（丢弃原名：模板从零开始，不再带原名）
            ops.Clear();
            selOp = -1;
            RebuildSteps();
            SetRenameAll(true);
            txtTemplate.Text = "照片_{nn}";
            MarkDirty();
            rep.Check("完全重命名丢弃原名", preview[0].NewName == "照片_01.txt", preview[0].NewName);
            SetRenameAll(false);
            txtTemplate.Text = "";
            MarkDirty();
            rep.Check("切回保留原名", preview[0].NewName == "a1.txt", preview[0].NewName);

            // 8 大小写步骤（先补回前缀步骤）
            Op pre = OpEngine.Create("insert");
            pre.Mode = "start";
            pre.Text = "P_";
            ops.Add(pre);
            selOp = 0;
            RebuildSteps();
            Op cs = OpEngine.Create("case");
            cs.Mode = "upper";
            ops.Add(cs);
            RebuildSteps();
            MarkDirty();
            rep.Check("大小写步骤生效", preview[0].NewName == "P_A1.txt", preview[0].NewName);
            ops.RemoveAt(ops.Count - 1);
            RebuildSteps();
            MarkDirty();

            // 9 删除文字步骤
            Op rm = OpEngine.Create("remove");
            rm.Mode = "text";
            rm.Text = "a";
            ops.Add(rm);
            RebuildSteps();
            MarkDirty();
            rep.Check("删除文字步骤生效", preview[0].NewName == "P_1.txt", preview[0].NewName);
            ops.RemoveAt(ops.Count - 1);
            RebuildSteps();
            MarkDirty();

            // 10 顺序调整与排序
            MoveModelTest(0, 2);
            rep.Check("移动到第 3 位", items[2].Name == "a1.txt", "items[2]=" + items[2].Name);
            SortBy("name", false);
            rep.Check("按名称排序回到 a1..a3",
                items[0].Name == "a1.txt" && items[2].Name == "a3.txt",
                items[0].Name + ".." + items[2].Name);

            // 11 筛选
            txtSearch.Text = "a2";
            rep.Check("搜索筛选 1 行", list.Items.Count == 1, "rows=" + list.Items.Count);
            txtSearch.Text = "";
            rep.Check("清空筛选恢复 3 行", list.Items.Count == 3, "rows=" + list.Items.Count);

            // 12 计数守恒
            int okN = CountStatus(PreviewStatus.Ok), cfN = CountStatus(PreviewStatus.Conflict),
                ncN = CountStatus(PreviewStatus.NoChange), erN = CountStatus(PreviewStatus.Error);
            rep.Check("状态计数之和等于条目数", okN + cfN + ncN + erN == preview.Count,
                okN + "+" + cfN + "+" + ncN + "+" + erN + " vs " + preview.Count);

            // 13 方案存取（步骤链要能完整往返）
            CollectRules();
            RuleSet snapshot = rules;
            Features.SavePreset("__smoke", snapshot);
            RuleSet back = Features.LoadPreset("__smoke");
            rep.Check("方案往返保留步骤", back != null && back.Ops != null && back.Ops.Count == snapshot.Ops.Count,
                back == null ? "(null)" : ("ops=" + (back.Ops == null ? -1 : back.Ops.Count)));
            Features.DeletePreset("__smoke");

            // 14 导出清单
            string csv = Path.Combine(root, "list.csv");
            string err;
            bool csvOk = Features.ExportCsv(csv, preview, out err);
            rep.Check("导出 CSV", csvOk && File.Exists(csv), err);

            // 15 版式约束
            int sum = 0;
            for (int i = 0; i < list.Columns.Count; i++) sum += list.Columns[i].Width;
            rep.Check("列宽不超出列表可视宽度", sum <= list.ClientSize.Width,
                "sum=" + sum + " client=" + list.ClientSize.Width);
            // 反过来的断言：列宽必须铺满，否则右侧会留一条空白（拖拽后最容易出现）
            rep.Check("列宽铺满列表可视宽度", sum >= list.ClientSize.Width - Theme.S(6),
                "sum=" + sum + " client=" + list.ClientSize.Width);
            rep.Check("列表没有横向滚动条", !NativeState.HasHScroll(list),
                "range=" + NativeState.HScrollRange(list));
            rep.Check("右侧规则栏宽度 > 400px", rightRail.Width >= Theme.S(400), "w=" + rightRail.Width);
            rep.Check("左侧来源栏宽度 232", leftRail.Width == Theme.S(232), "w=" + leftRail.Width);
            rep.Check("主按钮在标题区", btnRun != null && btnRun.Parent == header, "parent=" + (btnRun == null || btnRun.Parent == null ? "null" : btnRun.Parent.Name));
            rep.Check("卡片高度都大于 0",
                cardBase.Height > 0 && cardSteps.Height > 0 && cardOutput.Height > 0,
                cardBase.Height + "/" + cardSteps.Height + "/" + cardOutput.Height);
            string bad;
            rep.Check("标签没有被截断", LabelsFitAllCards(out bad), bad);
            { string ov; bool any = AnyCardOverlap(out ov); rep.Check("标签没有压住输入框", !any, any ? ov : ""); }
            rep.Check("列表已开启双缓冲", NativeState.HasListViewDoubleBuffer(list),
                "LVS_EX_DOUBLEBUFFER 未设置，鼠标移动时会闪");
            rep.Check("窗口任意位置可接收拖放",
                AllowDrop && center.AllowDrop && rightRail.AllowDrop && leftRail.AllowDrop,
                AllowDrop + "/" + center.AllowDrop + "/" + rightRail.AllowDrop + "/" + leftRail.AllowDrop);

            // 16 实际改名 + 撤销
            ClearAll();
            AddPaths(new string[] { Path.Combine(root, "a3.txt") });
            RefreshPreview();
            rep.Check("改名前进预览为待改名", CountStatus(PreviewStatus.Ok) == 1, "ok=" + CountStatus(PreviewStatus.Ok));
            ExecuteRename(false);
            bool renamed = File.Exists(Path.Combine(root, "P_a3.txt"));
            rep.Check("实际改名落盘", renamed, "exists=" + renamed);
            QuickUndo();
            bool restored = File.Exists(Path.Combine(root, "a3.txt"));
            rep.Check("撤销恢复原名", restored, "restored=" + restored);

            SmokeFail = rep.Finish() > 0 ? 1 : 0;
            BeginInvoke(new Action(delegate { Close(); }));
        }

        private int CountStatus(PreviewStatus st)
        {
            int n = 0;
            for (int i = 0; i < preview.Count; i++) if (preview[i] != null && preview[i].Status == st) n++;
            return n;
        }

        private void MoveModelTest(int from, int to)
        {
            if (from < 0 || to < 0 || from >= items.Count || to >= items.Count) return;
            FileItem it = items[from];
            items.RemoveAt(from);
            items.Insert(to, it);
            RefreshPreview();
        }

        private bool LabelOverlapsInput(Control card)
        {
            string ignored;
            return LabelOverlapsInput(card, out ignored);
        }

        private bool LabelOverlapsInput(Control card, out string detail)
        {
            detail = "";
            List<Label> labels = new List<Label>();
            List<Control> inputs = new List<Control>();
            CollectLabelsAndInputs(card, labels, inputs);
            for (int i = 0; i < labels.Count; i++)
            {
                Label a = labels[i];
                string txt = a.Text;
                if (!a.Visible || txt.Length == 0 || txt == "→") continue;
                for (int j = 0; j < inputs.Count; j++)
                {
                    Control b = inputs[j];
                    if (!b.Visible || b.Parent != a.Parent) continue;
                    if (a.Bounds.IntersectsWith(b.Bounds))
                    {
                        detail = "\"" + txt + "\" " + a.Bounds + " ∩ " + b.GetType().Name + " " + b.Bounds;
                        return true;
                    }
                }
            }
            return false;
        }

        private Panel[] RailSections()
        {
            return new Panel[] { cardBase, cardSteps, cardOutput };
        }

        private static string SectionName(int i)
        {
            return i == 0 ? "命名方式" : (i == 1 ? "规则" : "输出");
        }

        private bool LabelsFitAllCards(out string bad)
        {
            bad = "";
            Panel[] cards = RailSections();
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                List<Label> labels = new List<Label>();
                List<Control> inputs = new List<Control>();
                CollectLabelsAndInputs(cards[i], labels, inputs);
                for (int j = 0; j < labels.Count; j++)
                {
                    Label l = labels[j];
                    if (l.Text.Length == 0 || l.Text.IndexOf('\n') >= 0) continue;
                    if (l.Height > l.Font.Height * 2) continue;
                    Size need = TextRenderer.MeasureText(l.Text, l.Font);
                    if (need.Width > l.Width)
                    {
                        bad = SectionName(i) + " / \"" + l.Text + "\" 需要 " + need.Width + "px，实际 " + l.Width + "px";
                        return false;
                    }
                }
            }
            return true;
        }

        private bool AnyCardOverlap(out string detail)
        {
            detail = "";
            Panel[] cards = RailSections();
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                string d;
                if (LabelOverlapsInput(cards[i], out d))
                {
                    detail = SectionName(i) + " → " + d;
                    return true;
                }
            }
            return false;
        }
        // 版式约束：同一张卡片里，每一行的「第一个输入框」必须落在同一条竖线上。
        // 这样各家卡片左边缘不会各写各的坐标（之前就是 54/84/52/66/0 混用）。
        private bool FirstInputsAligned(out string bad)
        {
            bad = "";
            Panel[] cards = RailSections();
            for (int i = 0; i < cards.Length; i++)
            {
                Panel card = cards[i];
                if (card == null) continue;
                List<Control> inputs = new List<Control>();
                CollectInputs(card, inputs);
                if (inputs.Count < 2) continue;
                List<int> rowTop = new List<int>();
                List<int> rowLeft = new List<int>();
                for (int j = 0; j < inputs.Count; j++)
                {
                    Control c = inputs[j];
                    if (!c.Visible) continue;
                    Point p = card.PointToClient(c.PointToScreen(Point.Empty));
                    int k = -1;
                    for (int r = 0; r < rowTop.Count; r++)
                        if (Math.Abs(rowTop[r] - p.Y) <= 6) { k = r; break; }
                    if (k < 0) { rowTop.Add(p.Y); rowLeft.Add(p.X); }
                    else if (p.X < rowLeft[k]) rowLeft[k] = p.X;
                }
                for (int r = 1; r < rowTop.Count; r++)
                {
                    if (Math.Abs(rowLeft[r] - rowLeft[0]) > 1)
                    {
                        bad = SectionName(i) + " 第 " + (r + 1) + " 行首列 x=" + rowLeft[r] + "，第 1 行 x=" + rowLeft[0];
                        return false;
                    }
                }
            }
            return true;
        }

        private bool UniformLeftAndWidth(Card card, int left, int width, out string bad)
        {
            bad = "";
            if (card == null) { bad = "卡片为空"; return false; }
            int n = 0;
            for (int i = 0; i < card.Controls.Count; i++)
            {
                Control c = card.Controls[i];
                if (!(c is Field) && !(c is Select)) continue;
                n++;
                if (c.Left != left)
                {
                    bad = card.Title + " 里 " + c.GetType().Name + ".Left=" + c.Left + "，期望 " + left;
                    return false;
                }
                if (width > 0 && c.Width != width)
                {
                    bad = card.Title + " 里 " + c.GetType().Name + ".Width=" + c.Width + "，期望 " + width;
                    return false;
                }
            }
            if (n == 0) { bad = card.Title + " 没有输入控件"; return false; }
            return true;
        }

        private static void CollectInputs(Control root, List<Control> inputs)
        {
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                if (c is Field || c is Select) inputs.Add(c);
                if (c.HasChildren) CollectInputs(c, inputs);
            }
        }
        private static void CollectLabelsAndInputs(Control root, List<Label> labels, List<Control> inputs)
        {
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                if (c is Label) labels.Add((Label)c);
                else if (c is Field || c is Select || c is CheckBoxFlat) inputs.Add(c);
                if (c.HasChildren) CollectLabelsAndInputs(c, labels, inputs);
            }
        }
        // 右栏是 AutoScroll 面板，但滚轮消息只会送到「有焦点」的控件（这里是列表），
        // 用户把鼠标停在规则卡片上滚动时面板不动。这里统一拦截：光标在规则面板内
        // 就把滚轮转成该面板的垂直滚动，其余情况照常交给子控件。
        private const int WM_MOUSEWHEEL = 0x020A;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && rightHost != null && rightHost.Visible)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point cp = rightHost.PointToClient(new Point(sx, sy));
                if (rightHost.ClientRectangle.Contains(cp))
                {
                    int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                    int cur = -rightHost.AutoScrollPosition.Y;
                    int next = cur - Math.Sign(delta) * Theme.S(70);
                    if (next < 0) next = 0;
                    rightHost.AutoScrollPosition = new Point(0, next);
                    return;
                }
            }
            base.WndProc(ref m);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Application.Exit();
        }
    }
}











