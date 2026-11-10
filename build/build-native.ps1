<#
================================================================================
 原生版打包脚本：把 native\*.cs 编译成单个 exe（不依赖 PowerShell）
================================================================================
 用法：
   pwsh -NoProfile -File "build\build-native.ps1"
   pwsh -NoProfile -File "build\build-native.ps1" -Version v2
   pwsh -NoProfile -File "build\build-native.ps1" -Verify     # 编译后自动跑两套测试
   pwsh -NoProfile -File "build\build-native.ps1" -Sign       # 编译后用本机证书签名

 签名口令不写在脚本里：优先 -CertPassword，其次环境变量 FBR_CERT_PASSWORD，
 都没有时在交互终端里安全输入。详见 build\cert-password.ps1。

 依赖：系统自带 .NET Framework 编译器 csc.exe（Windows 10/11 内置），无需安装。
 产物：上级目录下的 文件批量重命名工具_原生版_YYYYMMDD_<版本>.exe
================================================================================
#>
[CmdletBinding()]
param(
    [string]$Version = 'v1',
    [switch]$Verify,
    [switch]$Sign,
    [string]$Pfx,
    [string]$CertPassword
)

$ErrorActionPreference = 'Stop'
$buildDir = $PSScriptRoot
$root = Split-Path -Path $buildDir -Parent
$nativeDir = Join-Path $root 'native'

# ---------------------------------------------------------------- 1) 图标
$icon = Join-Path $buildDir 'app.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    & (Join-Path $buildDir 'make-icon.ps1') -Path $icon
}

# ---------------------------------------------------------------- 2) 源文件
$sources = @(Get-ChildItem -LiteralPath $nativeDir -Filter '*.cs' -File | Sort-Object Name)
if ($sources.Count -eq 0) { throw "未在 $nativeDir 找到任何 .cs 源文件" }

# csc 读取无 BOM 的 UTF-8 源文件会按 ANSI 处理，中文会乱码 —— 统一补 BOM
foreach ($f in $sources) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    if (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        [System.IO.File]::WriteAllText($f.FullName, $text, (New-Object System.Text.UTF8Encoding($true)))
        Write-Host "已补写 UTF-8 BOM：$($f.Name)"
    }
}
$manifest = Join-Path $nativeDir 'app.manifest'
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少应用程序清单：$manifest" }

# ---------------------------------------------------------------- 3) 编译器
$csc = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '未找到 csc.exe（.NET Framework 编译器），无法打包。' }
Write-Host "编译器：$csc"

# ---------------------------------------------------------------- 4) 编译
$exeName = '文件批量重命名工具_{0}_{1}.exe' -f (Get-Date -Format 'yyyyMMdd'), $Version
$exe = Join-Path $root $exeName

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output /noconfig `
    "/win32icon:$icon" `
    "/win32manifest:$manifest" `
    "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    ($sources | ForEach-Object { $_.FullName })
if ($LASTEXITCODE -ne 0) { throw "编译失败，csc 退出码 $LASTEXITCODE" }

# ---------------------------------------------------------------- 4b) 数字签名（可选）
if ($Sign) {
    $pfxPath = $Pfx
    if (-not $pfxPath) { $pfxPath = Join-Path $root 'cert\FileBatRenamer_Local.pfx' }
    Write-Host ''
    Write-Host '=== 数字签名 ==='
    & (Join-Path $buildDir 'sign-exe.ps1') -Exe $exe -Pfx $pfxPath -Password $CertPassword
    if ($LASTEXITCODE -ne 0) { throw "签名失败，退出码 $LASTEXITCODE" }
}

# ---------------------------------------------------------------- 5) 结果
$fi = Get-Item -LiteralPath $exe
Write-Host ''
Write-Host ("打包完成：{0}" -f $fi.FullName)
Write-Host ("体积：{0:N1} KB（源文件 {1} 个）" -f ($fi.Length / 1KB), $sources.Count)

# ---------------------------------------------------------------- 6) 可选验证
if ($Verify) {
    Write-Host ''
    Write-Host '=== 运行核心自检 ==='
    $logDir = Join-Path $env:TEMP 'FileBatRenamer'
    $selfLog = Join-Path $logDir 'test-SelfTest.log'
    Remove-Item $selfLog -Force -ErrorAction SilentlyContinue
    & $exe -SelfTest
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $selfLog)) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $selfLog) { Get-Content -LiteralPath $selfLog -Encoding utf8 | Select-Object -Last 2 }
    else { throw '自检结果文件未生成' }

    Write-Host ''
    Write-Host '=== 运行界面冒烟测试（窗口会闪现后自动关闭）==='
    $uiLog = Join-Path $logDir 'test-UiTest.log'
    Remove-Item $uiLog -Force -ErrorAction SilentlyContinue
    & $exe -UiTest
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $uiLog)) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $uiLog) { Get-Content -LiteralPath $uiLog -Encoding utf8 | Select-Object -Last 2 }
    else { throw '界面测试结果文件未生成' }
}
