<#
.SYNOPSIS
  DTTS の MSIX に署名するためのテスト用自己署名証明書を作成する。

.DESCRIPTION
  - 証明書のサブジェクトは Package.appxmanifest の Identity/@Publisher と一致させる必要がある。
    既定ではマニフェストから読み取る。
  - 作成した証明書は「現在のユーザー」の個人ストア（Cert:\CurrentUser\My）に入る。管理者権限は不要。
  - 配布用の公開証明書（.cer、秘密鍵なし）を -OutDir に書き出す。インストールする PC ではこれを信頼させる。
  - 秘密鍵付きの .pfx は書き出さない（必要なら -ExportPfx を指定）。.pfx はリポジトリに含めないこと。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\packaging\New-TestCertificate.ps1
#>
param(
    [string]$Subject,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\artifacts"),
    [int]$ValidYears = 2,
    [switch]$ExportPfx
)
$ErrorActionPreference = "Stop"

$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Subject) {
    [xml]$manifest = Get-Content (Join-Path $repo "Package.appxmanifest") -Encoding UTF8
    $Subject = $manifest.Package.Identity.Publisher
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.FriendlyName -eq "DTTS Test Signing" -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "既存のテスト証明書を使います: $($cert.Thumbprint)"
} else {
    # EKU 1.3.6.1.5.5.7.3.3 = コード署名、2.5.29.19 = 基本制約（CA ではない）
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Subject `
        -KeyUsage DigitalSignature -FriendlyName "DTTS Test Signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears($ValidYears) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Write-Host "テスト証明書を作成しました: $($cert.Thumbprint)"
}

$cerPath = Join-Path $OutDir "DTTS_TestCert.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "公開証明書: $cerPath"

if ($ExportPfx) {
    $pwd = Read-Host "PFX のパスワード" -AsSecureString
    $pfxPath = Join-Path $OutDir "DTTS_TestCert.pfx"
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd -Force | Out-Null
    Write-Host "秘密鍵付き証明書（リポジトリに含めないこと）: $pfxPath"
}

$cert.Thumbprint
