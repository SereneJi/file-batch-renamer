<#
  sign-exe.ps1 — 用本机自签名证书给 exe 打数字签名

  用法：
    pwsh -File build\sign-exe.ps1 -Exe 文件批量重命名工具_20261019_v3.exe
    pwsh -File build\sign-exe.ps1 -Exe out.exe -Pfx cert\FileBatRenamer_Local.pfx

  口令来源：-Password 参数 → 环境变量 FBR_CERT_PASSWORD → 交互式安全输入。
  口令不写进脚本、不打印。

  说明：私钥以 EphemeralKeySet 方式从 PFX 读入内存，不写用户证书存储区，
        所以不需要管理员权限；也不需要 signtool.exe。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Pfx,
    [string]$Password
)

$ErrorActionPreference = 'Stop'

$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { throw "找不到要签名的文件：$Exe" }
if (-not $Pfx) { $Pfx = Join-Path (Split-Path -Parent $PSScriptRoot) 'cert\FileBatRenamer_Local.pfx' }
$Pfx = [System.IO.Path]::GetFullPath($Pfx)
if (-not (Test-Path $Pfx)) { throw "找不到证书：$Pfx（先运行 build\make-signing-cert.ps1）" }

. (Join-Path $PSScriptRoot 'cert-password.ps1')
$Password = Resolve-CertPassword -Provided $Password -Purpose '代码签名证书'

$flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Pfx, $Password, $flags)
if (-not $cert.HasPrivateKey) { throw '证书里没有私钥，无法签名' }

$res = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
if ($res -and $res.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw '签名失败：文件没有被写入签名'
}

$check = Get-AuthenticodeSignature -FilePath $Exe
if ($check.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned -or
    $check.Status -eq [System.Management.Automation.SignatureStatus]::Invalid -or
    $check.Status -eq [System.Management.Automation.SignatureStatus]::HashMismatch) {
    throw "签名校验失败：$($check.Status) $($check.StatusMessage)"
}

"已签名   : $Exe"
"签名状态 : $($check.Status)"
if ($check.SignerCertificate) { "签名者   : $($check.SignerCertificate.Subject)" }
"指纹     : $($cert.Thumbprint)"
if ($check.Status -eq [System.Management.Automation.SignatureStatus]::UnknownError) {
    "提示     : 证书链还没被本机信任（UntrustedRoot）。运行 工具\安装信任证书.cmd 之后即变为 Valid。"
}
