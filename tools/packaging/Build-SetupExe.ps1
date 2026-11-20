<#
.SYNOPSIS
  証明書・管理者権限不要の単一インストーラー (DTTS_Setup.exe) と配布用 ZIP を生成する。
#>
param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

function Invoke-CodeSign {
    param([string]$TargetPath)
    if ([string]::IsNullOrWhiteSpace($CertThumbprint)) { return }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $signtool) {
        $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        if (Test-Path $kits) {
            $signtool = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $signtool) { throw "signtool.exe が見つかりません。Windows SDK を確認してください。" }
    & $signtool sign /fd SHA256 /sha1 $CertThumbprint /tr $TimestampUrl /td SHA256 $TargetPath
    if ($LASTEXITCODE -ne 0) { throw "コード署名に失敗しました: $TargetPath" }
}

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$rid = "win-" + $Platform.ToLowerInvariant()

[xml]$manifest = Get-Content (Join-Path $repo "Package.appxmanifest") -Encoding UTF8
$rawVersion = $manifest.Package.Identity.Version
$verParts = $rawVersion.Split('.')
$version = if ($verParts.Count -ge 3) { "$($verParts[0]).$($verParts[1]).$($verParts[2])" } else { $rawVersion }

$publishDir = Join-Path $repo "artifacts\publish_unpackaged"
$payloadZip = Join-Path $repo "tools\installer\payload.zip"
$setupProj = Join-Path $repo "tools\installer\DTTS.Setup.csproj"
$setupOutDir = Join-Path $repo "artifacts\setup_out"
$finalExe = Join-Path $repo "artifacts\DTTS_Setup.exe"
$distName = "DTTS_v${version}_${Platform}"
$distDir = Join-Path $repo "artifacts\dist_setup\$distName"
$finalZip = Join-Path $repo "artifacts\${distName}.zip"

# 1. DTTS 本体をパッケージなし・自己完結型でパブリッシュする
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "1/4: Publishing DTTS ($Platform)..."
& dotnet publish (Join-Path $repo "DTTS.csproj") -c Release -r $rid "-p:Platform=$Platform" `
    -p:WindowsPackageType=None `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:AppxBundle=Never `
    -p:GenerateAppxPackageOnBuild=false `
    -o $publishDir -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "DTTS publish failed." }

Invoke-CodeSign (Join-Path $publishDir "DTTS.exe")

# 2. 本体フォルダを payload.zip に圧縮する
Write-Host "2/4: Compressing payload..."
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 3. 単一実行ファイル DTTS_Setup.exe をビルドする
if (Test-Path $setupOutDir) { Remove-Item $setupOutDir -Recurse -Force }
Write-Host "3/4: Building DTTS_Setup.exe..."
& dotnet publish $setupProj -c Release -r $rid `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $setupOutDir -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "DTTS_Setup.exe publish failed." }

$builtExe = Join-Path $setupOutDir "DTTS_Setup.exe"
if (-not (Test-Path $builtExe)) { throw "DTTS_Setup.exe not found: $builtExe" }
Invoke-CodeSign $builtExe
Copy-Item -Path $builtExe -Destination $finalExe -Force
if ([string]::IsNullOrWhiteSpace($CertThumbprint)) {
    Write-Warning "CertThumbprint 未指定のため DTTS_Setup.exe は未署名です。公開配布時は -CertThumbprint を指定してください。"
}

# 4. 配布用フォルダと ZIP を作成する
Write-Host "4/4: Creating distribution ZIP..."
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item -Path $finalExe -Destination (Join-Path $distDir "DTTS_Setup.exe") -Force

$readme = @"
============================================================
DTTS (大富豪) 導入ガイド (v${version} / ${Platform})
============================================================

【インストール方法】
1. 「DTTS_Setup.exe」をダブルクリックしてください。
   （ZIP の場合は展開してから実行してください）
2. 自動でパソコンに導入され、デスクトップに「DTTS」のアイコンが作成されて起動します。
   ※テスト証明書の登録や管理者権限は不要です。

【Bluetooth LE で近くの相手と対戦する方法】
1. お互いの Windows PC で「設定」→「Bluetooth とデバイス」から Bluetooth をオンにしてください。
2. 親になる人がアプリ内の「通信対戦」→「部屋を作る」を押します。
3. 参加する人が「通信対戦」→「部屋を探す」を押すと、近くの部屋が見つかります。
   ※部屋を作る側の PC が Bluetooth LE Peripheral ロールに対応している必要があります。
"@
[IO.File]::WriteAllText((Join-Path $distDir "はじめに.txt"), $readme, [System.Text.UTF8Encoding]::new($true))

if (Test-Path $finalZip) { Remove-Item $finalZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($distDir, $finalZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

# 中間ファイルを片付ける
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done!"
Write-Host "  Setup EXE: $finalExe"
Write-Host "  Dist ZIP:  $finalZip"
