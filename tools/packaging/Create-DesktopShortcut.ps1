<#
.SYNOPSIS
  インストール済みの DTTS (MSIX) を起動するデスクトップショートカットを作成する。
#>
param(
    [string]$IconSourcePath
)

$ErrorActionPreference = "Stop"
$PackageName = "12f303fd-92ec-4cdb-bfb0-e30f02f5c93f"

# インストール済みパッケージから PackageFamilyName を取得する
$pkg = Get-AppxPackage -Name $PackageName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $pkg) {
    Write-Warning "DTTS package not found. Please install DTTS first."
    exit 1
}

# アイコンのコピー元を探索する（同梱の DTTS.ico -> リポジトリの Assets -> パッケージ内）
$candidates = @()
if ($IconSourcePath) { $candidates += $IconSourcePath }
$candidates += (Join-Path $PSScriptRoot "DTTS.ico")
$candidates += (Join-Path $PSScriptRoot "..\..\Assets\WindowIcon.ico")
if ($pkg.InstallLocation) {
    $candidates += (Join-Path $pkg.InstallLocation "Assets\WindowIcon.ico")
}

$resolvedIcon = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

# アプリ更新でパスが変わってもアイコンが消えないよう LocalAppData に配置する
$iconDir = Join-Path $env:LOCALAPPDATA "DTTS"
New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
$iconDest = Join-Path $iconDir "DTTS.ico"

if ($resolvedIcon) {
    Copy-Item -Path $resolvedIcon -Destination $iconDest -Force
}

# デスクトップにショートカット (.lnk) を作成する
$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "DTTS.lnk"

$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "explorer.exe"
$shortcut.Arguments = "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
if (Test-Path $iconDest) {
    $shortcut.IconLocation = "$iconDest,0"
}
$shortcut.Description = "DTTS"
$shortcut.Save()

Write-Host "Desktop shortcut created: $shortcutPath"
