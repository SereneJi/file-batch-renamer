<#
  unblock-files.ps1 — 去掉复制/下载带来的「来自 Internet」标记（Zone.Identifier）

  适用场景：exe 属性页底部出现「解除锁定」复选框，或者被 SmartScreen 当成网络来源程序。
  用法：
    pwsh -NoProfile -File build\unblock-files.ps1
#>
[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Path) { $Path = $root }
$Path = [System.IO.Path]::GetFullPath($Path)

$count = 0
$failed = 0
Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        Unblock-File -LiteralPath $_.FullName
        $count++
    } catch {
        $failed++
    }
}

Write-Host "已处理：$Path"
Write-Host "解除锁定：$count 个文件"
if ($failed -gt 0) { Write-Host "跳过（无权限或占用）：$failed 个" }
Write-Host ''
Write-Host '提示：若 exe 仍被拦截，请在 exe 上右键 →「属性」→ 勾选「解除锁定」→「确定」。'
