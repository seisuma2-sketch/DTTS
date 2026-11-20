<#
.SYNOPSIS
  Build-SignedMsix.ps1 の出力から配布用フォルダと ZIP（デスクトップアイコン作成付き）を生成する。
#>
param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

[xml]$manifest = Get-Content (Join-Path $repo "Package.appxmanifest") -Encoding UTF8
$version = $manifest.Package.Identity.Version

$msixDir = Join-Path $repo "artifacts\msix\DTTS_${version}_${Platform}_Test"
if (-not (Test-Path $msixDir)) {
    throw "MSIX 出力フォルダが見つかりません: $msixDir`n先に tools\packaging\Build-SignedMsix.ps1 を実行してください。"
}

$distName = "DTTS_v${version}_${Platform}"
$distDir = Join-Path $repo "artifacts\dist\$distName"
$zipPath = Join-Path $repo "artifacts\${distName}.zip"

if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# MSIX 一式（.msix, .cer, Dependencies, Install.ps1, Add-AppDevPackage.ps1 等）をコピーする
Copy-Item -Path (Join-Path $msixDir "*") -Destination $distDir -Recurse -Force

# デスクトップショートカット作成スクリプトとアイコンを同梱する
Copy-Item -Path (Join-Path $PSScriptRoot "Create-DesktopShortcut.ps1") -Destination (Join-Path $distDir "Create-DesktopShortcut.ps1") -Force
Copy-Item -Path (Join-Path $repo "Assets\WindowIcon.ico") -Destination (Join-Path $distDir "DTTS.ico") -Force

# インストール.bat（ブロック解除 → インストール → デスクトップアイコン作成）
$batContent = @"
@echo off
cd /d "%~dp0"
echo Installing DTTS...
echo Please click "Yes" if a permission prompt appears.
set "DTTS_INSTALL_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath $env:DTTS_INSTALL_DIR -Recurse | Unblock-File -ErrorAction SilentlyContinue"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
echo Creating desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Create-DesktopShortcut.ps1" -IconSourcePath "%~dp0DTTS.ico"
echo.
pause
"@
[IO.File]::WriteAllText((Join-Path $distDir "インストール.bat"), $batContent, [System.Text.Encoding]::ASCII)

# はじめに（インストール手順）.txt
$readmeContent = @"
============================================================
DTTS (大富豪) インストール＆遊び方ガイド (v${version} / ${Platform})
============================================================

【1. インストール方法】
1. 受け取った ZIP ファイルをすべて展開（解凍）してください。
   ※ ZIP を開いたまま実行すると失敗しますので、必ずフォルダへ展開してください。
2. 展開したフォルダ内の「インストール.bat」をダブルクリックしてください。
   （または「Install.ps1」を右クリック →「PowerShell で実行」）
3.「このアプリがデバイスに変更を加えることを許可しますか？」と出たら「はい」を押します。
4. 画面の案内に従って Enter キーを押すと、テスト証明書とアプリがインストールされます。
5. 完了すると、デスクトップに「DTTS」のアイコンが自動的に作成されます！
   そのデスクトップアイコンをダブルクリックして起動できます。
   （スタートメニューの「DTTS」からも起動できます）

【2. Bluetooth LE で近くの相手と対戦する方法】
1. お互いの Windows PC で「設定」→「Bluetooth とデバイス」から Bluetooth をオンにしてください。
2. 親になる人がアプリ内の「通信対戦」→「部屋を作る」を押します。
3. 参加する人が「通信対戦」→「部屋を探す」を押すと、近くの部屋が見つかります。
   ※部屋を作る側の PC が Bluetooth LE Peripheral ロールに対応している必要があります。
"@
[IO.File]::WriteAllText((Join-Path $distDir "はじめに（インストール手順）.txt"), $readmeContent, [System.Text.UTF8Encoding]::new($true))

# ZIP アーカイブを作成する（親フォルダごと圧縮）
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($distDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host "配布用フォルダ: $distDir"
Write-Host "配布用 ZIP:     $zipPath"
