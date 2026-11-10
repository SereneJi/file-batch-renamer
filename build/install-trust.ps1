<#
  install-trust.ps1 — 把本工具的自签名证书装入「当前用户」的受信任根与受信任发布者

  作用：
    装好之后，本机 Windows 会认为「文件批量重命名工具」的 exe 是可信发布者签发的，
    打开时不再出现「Windows 已保护你的电脑」这类 SmartScreen 提示。

  说明：
    · 只影响「当前用户」，不需要管理员权限，不会改动系统级证书库；
    · 只信任本工具这一张证书（按指纹精确匹配），不是关闭 SmartScreen；
    · 随时可用「卸载信任证书.cmd」移除。

  用法：
    pwsh -NoProfile -File build\install-trust.ps1
    pwsh -NoProfile -File build\install-trust.ps1 -Cer cert\FileBatRenamer_Local.cer
#>
[CmdletBinding()]
param(
    [string]$Cer
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Cer) { $Cer = Join-Path $root 'cert\FileBatRenamer_Local.cer' }
$Cer = [System.IO.Path]::GetFullPath($Cer)
if (-not (Test-Path -LiteralPath $Cer)) {
    throw "找不到证书公钥文件：$Cer（先运行 build\make-signing-cert.ps1 生成）"
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Cer)
Write-Host "证书主题：$($cert.Subject)"
Write-Host "证书指纹：$($cert.Thumbprint)"
Write-Host "有效期至：$($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ''

$targets = @(
    @{ Name = '受信任的根证书颁发机构'; Store = 'Root' },
    @{ Name = '受信任的发布者';         Store = 'TrustedPublisher' }
)

foreach ($t in $targets) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($t.Store, 'CurrentUser')
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $exists = $false
        foreach ($c in $store.Certificates) {
            if ($c.Thumbprint -eq $cert.Thumbprint) { $exists = $true; break }
        }
        if ($exists) {
            Write-Host "[跳过] $($t.Name)：证书已存在"
        } else {
            $store.Add($cert)
            Write-Host "[完成] $($t.Name)：已添加"
        }
    } finally {
        $store.Close()
    }
}

# 顺便把 exe 的「来自 Internet」标记去掉（有的话）
$exe = Get-ChildItem -LiteralPath $root -Filter '文件批量重命名工具_*.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($exe) {
    try {
        Unblock-File -LiteralPath $exe.FullName
        $sig = Get-AuthenticodeSignature -LiteralPath $exe.FullName
        Write-Host ''
        Write-Host "可执行文件：$($exe.Name)"
        Write-Host "签名验证  ：$($sig.Status)"
        if ($sig.Status -eq 'Valid') {
            Write-Host '结果：证书已被本机信任，打开 exe 不应再被拦截。'
        } else {
            Write-Host "结果：签名状态仍为 $($sig.Status)（若是证书缓存未刷新，注销后重新登录即可）。"
        }
    } catch {
        Write-Host "提示：验证签名时出错 —— $($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host '完成。若仍被拦截，可执行「解除文件锁定.cmd」，或对 exe 右键 → 属性 → 勾选「解除锁定」。'
