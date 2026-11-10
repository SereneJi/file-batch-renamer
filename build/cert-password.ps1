<#
  cert-password.ps1 — 解析代码签名证书口令

  优先级：
    1) 调用方传入的 -Password
    2) 环境变量 FBR_CERT_PASSWORD（当前进程或用户级）
    3) 交互式安全输入（仅在控制台可交互时）

  口令只驻留内存，不写磁盘、不打印。仓库与脚本中不再保存任何默认口令。
#>
function Resolve-CertPassword {
    [CmdletBinding()]
    param(
        [string]$Provided,
        [string]$Purpose = '代码签名证书'
    )

    if (-not [string]::IsNullOrWhiteSpace($Provided)) { return $Provided }

    $fromEnv = [Environment]::GetEnvironmentVariable('FBR_CERT_PASSWORD', 'Process')
    if ([string]::IsNullOrWhiteSpace($fromEnv)) {
        $fromEnv = [Environment]::GetEnvironmentVariable('FBR_CERT_PASSWORD', 'User')
    }
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv }

    if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        $secure = Read-Host -Prompt "请输入$Purpose 的口令（输入不显示，仅本次使用）" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    throw '缺少证书口令：请设置环境变量 FBR_CERT_PASSWORD，或用 -Password 传入。'
}
