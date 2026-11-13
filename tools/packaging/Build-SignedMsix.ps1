<#
.SYNOPSIS
  Release 構成で DTTS の署名済み MSIX（サイドロード用）を作成する。

.DESCRIPTION
  1. 署名用証明書を探す（-Thumbprint 省略時は New-TestCertificate.ps1 が作った "DTTS Test Signing"）。
  2. dotnet build でパッケージを生成し、その証明書で署名する。
  ※ シンボルパッケージ（.msixsym）は配布に不要なので作らない（MsPdbCmfExeFullpath を存在しないパスにして生成ターゲットを止める）。
  3. 出力先（既定: artifacts\msix）に .msix・依存パッケージ・公開証明書（.cer）をまとめる。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\packaging\New-TestCertificate.ps1
  powershell -ExecutionPolicy Bypass -File tools\packaging\Build-SignedMsix.ps1
#>
param(
    [string]$Thumbprint,
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\artifacts\msix")
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

[xml]$manifest = Get-Content (Join-Path $repo "Package.appxmanifest") -Encoding UTF8
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version

if (-not $Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.FriendlyName -eq "DTTS Test Signing" -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "署名用の証明書がありません。先に tools\packaging\New-TestCertificate.ps1 を実行してください。" }
} else {
    $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint"
}
if ($cert.Subject -ne $publisher) {
    throw "証明書のサブジェクト（$($cert.Subject)）がマニフェストの Publisher（$publisher）と一致しません。"
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir | Out-Null

Write-Host "DTTS $version ($Platform) を Release でビルドして署名します..."
& dotnet build (Join-Path $repo "DTTS.csproj") -c Release "-p:Platform=$Platform" `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    "-p:PackageCertificateThumbprint=$($cert.Thumbprint)" `
    "-p:AppxPackageDir=$OutDir\" `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:MsPdbCmfExeFullpath=none-symbol-package-disabled `
    -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました（終了コード $LASTEXITCODE）。" }

$msix = Get-ChildItem $OutDir -Recurse -Filter *.msix |
    Where-Object { $_.Name -like "DTTS_*" } | Select-Object -First 1
if (-not $msix) { throw "MSIX が見つかりません: $OutDir" }

$sig = Get-AuthenticodeSignature $msix.FullName
if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
    throw "MSIX が想定した証明書で署名されていません。"
}
Export-Certificate -Cert $cert -FilePath (Join-Path $msix.DirectoryName "DTTS_TestCert.cer") -Force | Out-Null

Write-Host ""
Write-Host "完成: $($msix.FullName)"
Write-Host "署名: $($sig.SignerCertificate.Subject)（$($cert.Thumbprint)）"
Write-Host "同じフォルダーの DTTS_TestCert.cer と Dependencies\ を一緒に配布してください。手順は docs\INSTALL.md を参照。"
