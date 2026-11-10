<#
  uninstall-trust.ps1 — 从「当前用户」证书库移除本工具的自签名证书

  用法：
    pwsh -NoProfile -File build\uninstall-trust.ps1
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
    throw "找不到证书公钥文件：$Cer"
}

$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Cer)).Thumbprint
Write-Host "要移除的证书指纹：$thumb"
Write-Host ''

foreach ($name in @('Root', 'TrustedPublisher')) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, 'CurrentUser')
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $hit = @()
        foreach ($c in $store.Certificates) {
            if ($c.Thumbprint -eq $thumb) { $hit += $c }
        }
        if ($hit.Count -eq 0) {
            Write-Host "[跳过] $name：没有这张证书"
        } else {
            foreach ($c in $hit) { $store.Remove($c) }
            Write-Host "[完成] $name：已移除 $($hit.Count) 张"
        }
    } finally {
        $store.Close()
    }
}

Write-Host ''
Write-Host '完成。之后打开 exe 会恢复「未知发布者」的提示。'
