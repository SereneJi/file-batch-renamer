<#
  make-signing-cert.ps1 — 生成本机用的自签名代码签名证书

  为什么要它：Windows 11 的 SmartScreen / “未知发布者” 对没有数字签名的 exe
  一律按“来历不明”处理。给 exe 签上名、并把证书装进本机的“受信任的根证书”
  与“受信任的发布者”，拦截提示就不再出现（只对装了这张证书的电脑有效）。

  特点：
    * 私钥只在内存里生成，然后导出成 PFX 文件，不碰用户证书存储区
      （所以在受限环境下也能跑，不需要管理员权限）。
    * 证书 10 年有效，含 Code Signing EKU 与 digitalSignature 用途。

  用法：
    pwsh -File build\make-signing-cert.ps1
    pwsh -File build\make-signing-cert.ps1 -OutDir cert

  口令来源：-Password 参数 → 环境变量 FBR_CERT_PASSWORD → 交互式安全输入。
  不要把口令写进脚本或提交到仓库。
#>
[CmdletBinding()]
param(
    [string]$Subject  = 'CN=FileBatRenamer Local Publisher, O=Local Tool, C=CN',
    [string]$OutDir   = 'cert',
    [string]$Password,
    [int]$Years       = 10
)

$ErrorActionPreference = 'Stop'

if (-not $OutDir) { throw 'OutDir 不能为空' }
$dir = [System.IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

. (Join-Path $PSScriptRoot 'cert-password.ps1')
$Password = Resolve-CertPassword -Provided $Password -Purpose '自签名代码签名证书'

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
    $Subject,
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

# 代码签名用途（1.3.6.1.5.5.7.3.3），加上 digitalSignature
$oids = New-Object System.Security.Cryptography.OidCollection
[void]$oids.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3'))
$eku = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oids, $false)
[void]$req.CertificateExtensions.Add($eku)

$ku = New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $false)
[void]$req.CertificateExtensions.Add($ku)

# BasicConstraints: 自签名根（CA=true 便于作为根证书被信任链使用）
$basic = New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($true, $false, 0, $true)
[void]$req.CertificateExtensions.Add($basic)

# SubjectKeyIdentifier，部分校验器需要
$ski = New-Object System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension($req.PublicKey, $false)
[void]$req.CertificateExtensions.Add($ski)

$notBefore = [DateTimeOffset]::Now.AddDays(-2)
$notAfter  = [DateTimeOffset]::Now.AddYears($Years)
$cert = $req.CreateSelfSigned($notBefore, $notAfter)

$pfxPath = Join-Path $dir 'FileBatRenamer_Local.pfx'
$cerPath = Join-Path $dir 'FileBatRenamer_Local.cer'

[System.IO.File]::WriteAllBytes($pfxPath, $cert.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
[System.IO.File]::WriteAllBytes($cerPath, $cert.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

"主题     : $($cert.Subject)"
"指纹     : $($cert.Thumbprint)"
"有效期   : $($cert.NotBefore.ToString('yyyy-MM-dd')) ~ $($cert.NotAfter.ToString('yyyy-MM-dd'))"
"PFX      : $pfxPath"
"CER      : $cerPath"
"口令     : 已设置（不显示，请自行妥善保存）"

$cert.Dispose()
$rsa.Dispose()
