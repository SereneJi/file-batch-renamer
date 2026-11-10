<#
  capture-ui.ps1 — 截取已打开窗口的完整位图（支持 200% DPI 缩放）

  背景：DPI 不感知的进程调用 GetWindowRect / PrintWindow 时，系统只返回左上角
        “虚拟化”区域，截图会丢一大半。所以在截图线程里先
        SetThreadDpiAwarenessContext(-4)（= PER_MONITOR_AWARE_V2）再取矩形。

  说明：P/Invoke 部分只声明 blittable 结构体，System.Drawing 全部在 PowerShell
        侧调用——PowerShell 7 的 Add-Type 在编译 C# 时很难凑齐 .NET 10 的
        System.Drawing 引用（CS1069/CS0012），绕开它最省事。

  用法：
    pwsh -File build\capture-ui.ps1 -TitleLike '文件批量重命名' -Out build\ui-screenshot.png
#>
[CmdletBinding()]
param(
    [string]$TitleLike = '文件批量重命名',
    [string]$Out = 'ui-screenshot.png',
    [int]$WaitSeconds = 0,
    [int]$ScrollDown = 0,
    [int]$ProcessId = 0,
    [switch]$ScreenGrab
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Cap2
{
    [DllImport("user32.dll")] public static extern bool SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // 标题里可能同时匹配到别的窗口（例如宿主界面标签页），所以取面积最大的那个
    // 按进程 + 标题挑面积最大的可见窗口：MainWindowHandle 在部分启动方式下不可靠，
    // 而按标题匹配又可能撞上宿主里同名的标签页窗口（157x25）。
    public static IntPtr BestWindow(uint pid, string part)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (!IsWindowVisible(h)) return true;
            if (pid != 0)
            {
                uint wp;
                GetWindowThreadProcessId(h, out wp);
                if (wp != pid) return true;
            }
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            string t = sb.ToString();
            if (t.Length == 0) return true;
            if (!string.IsNullOrEmpty(part) && t.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
    public static IntPtr FindByTitle(string part, string exclude)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            string t = sb.ToString();
            if (t.Length == 0) return true;
            if (t.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (!string.IsNullOrEmpty(exclude) &&
                t.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

$deadline = (Get-Date).AddSeconds([Math]::Max($WaitSeconds, 0))
$hwnd = [IntPtr]::Zero
while ($true) {
    # 优先按进程挑最大的可见窗口（MainWindowHandle 不可靠；按标题可能撞到宿主同名标签页）
    $hwnd = [Cap2]::BestWindow([uint32]$ProcessId, $TitleLike)
    if ($hwnd -eq [IntPtr]::Zero) { $hwnd = [Cap2]::FindByTitle($TitleLike, 'DeepSeek Harness') }
    if ($hwnd -ne [IntPtr]::Zero) { break }
    if ((Get-Date) -gt $deadline) { throw "未找到标题包含「$TitleLike」的可见窗口" }
    Start-Sleep -Milliseconds 300
}

# DPI 感知必须在当前线程切好，再取窗口尺寸
[void][Cap2]::SetThreadDpiAwarenessContext([IntPtr](-4))
[void][Cap2]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 1200           # 让窗口完成首帧绘制

$rect = New-Object Cap2+RECT
[void][Cap2]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw '窗口没有有效尺寸' }

if ($ScrollDown -gt 0) {
    # 用真实滚轮输入（mouse_event）而不是 PostMessage：WinForms 的 AutoScroll 面板
    # 需要消息循环把 WM_MOUSEWHEEL 往上冒泡，SendMessage 直投会被忽略。
    [void][Cap2]::SetCursorPos([int]($rect.Right - 220), [int]($rect.Top + ($h * 0.6)))
    Start-Sleep -Milliseconds 200
    for ($i = 0; $i -lt $ScrollDown; $i++) {
        [Cap2]::mouse_event(0x0800, 0, 0, -120, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds 500
}

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
if ($ScreenGrab) {
    # 直接从屏幕抓：PrintWindow 只画目标窗口自己，不会被它弹出的子窗口（下拉层）带上
    $g.CopyFromScreen([int]$rect.Left, [int]$rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
} else {
    $hdc = $g.GetHdc()
    try { [void][Cap2]::PrintWindow($hwnd, $hdc, 2) } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
}

$outFull = [System.IO.Path]::GetFullPath($Out)
$dir = [System.IO.Path]::GetDirectoryName($outFull)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$bmp.Save($outFull, [System.Drawing.Imaging.ImageFormat]::Png)

# 另存一份半尺寸，方便直接查看
$half = [System.IO.Path]::Combine($dir, [System.IO.Path]::GetFileNameWithoutExtension($outFull) + '_half.png')
$b2 = New-Object System.Drawing.Bitmap ([int]($w / 2)), ([int]($h / 2))
$g2 = [System.Drawing.Graphics]::FromImage($b2)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($bmp, 0, 0, $b2.Width, $b2.Height)
$g2.Dispose()
$b2.Save($half, [System.Drawing.Imaging.ImageFormat]::Png)

"$w x $h -> $outFull"
"half -> $half"
$bmp.Dispose(); $b2.Dispose()
