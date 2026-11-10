<#
  生成多尺寸程序图标（256/48/32/16，PNG 负载的 ICO）。
  用法：pwsh -NoProfile -File "build\make-icon.ps1" -Path "build\app.ico"
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = @(256, 48, 32, 16)
$pngs = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF(0.5, 0.5, ($s - 1), ($s - 1))
    $gp = New-RoundPath -X 0.5 -Y 0.5 -W ($s - 1) -H ($s - 1) -R ([single]($s * 0.22))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 62, 126, 224),
        [System.Drawing.Color]::FromArgb(255, 20, 62, 148),
        [single]90)
    $g.FillPath($brush, $gp)

    $w = [single][math]::Max(1.4, $s * 0.085)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # 上行右向箭头
    $y1 = [single]($s * 0.37)
    $g.DrawLine($pen, [single]($s * 0.24), $y1, [single]($s * 0.64), $y1)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF([single]($s * 0.58), [single]($s * 0.25))),
            (New-Object System.Drawing.PointF([single]($s * 0.58), [single]($s * 0.49))),
            (New-Object System.Drawing.PointF([single]($s * 0.80), $y1))
        ))
    # 下行左向箭头
    $y2 = [single]($s * 0.63)
    $g.DrawLine($pen, [single]($s * 0.76), $y2, [single]($s * 0.36), $y2)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF([single]($s * 0.42), [single]($s * 0.51))),
            (New-Object System.Drawing.PointF([single]($s * 0.42), [single]($s * 0.75))),
            (New-Object System.Drawing.PointF([single]($s * 0.20), $y2))
        ))

    $pen.Dispose(); $white.Dispose(); $brush.Dispose(); $gp.Dispose(); $g.Dispose()

    $msPng = New-Object System.IO.MemoryStream
    $bmp.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngs.Add($msPng.ToArray())
    $msPng.Dispose(); $bmp.Dispose()
}

# 组装 ICO（Vista+ 支持 PNG 负载的多尺寸图标）
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($dim); $bw.Write($dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

# 顺便导出最大帧供预览
$raw = [System.IO.File]::ReadAllBytes($Path)
$len = [BitConverter]::ToUInt32($raw, 6 + 8)
$off = [BitConverter]::ToUInt32($raw, 6 + 12)
$preview = [System.IO.Path]::ChangeExtension($Path, '.png')
[System.IO.File]::WriteAllBytes($preview, $raw[$off..($off + $len - 1)])

Write-Host ("图标已生成：{0}（{1} 尺寸，{2:N1} KB）" -f $Path, $sizes.Count, ((Get-Item $Path).Length / 1KB))
