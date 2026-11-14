# DTTS インストール手順（テスト署名版 MSIX）

DTTS は MSIX パッケージで配布します。テスト用の自己署名証明書で署名しているため、
インストールする PC で **一度だけ証明書を信頼させる** 必要があります。

## 動作環境

- Windows 10 バージョン 1809（ビルド 17763）以降、または Windows 11
- x64 版（`DTTS_<バージョン>_x64.msix`）
- Windows App Runtime 2.6（多くの PC には入っています。無い場合は手順 3 で一緒に入れます）
- .NET はパッケージに同梱しているため、別途インストールは不要です

## 配布物

`tools\packaging\Build-SignedMsix.ps1` の出力フォルダー（`artifacts\msix\DTTS_<バージョン>_x64_Test\`）に次のものがあります。フォルダーごと渡してください。

| ファイル | 内容 |
|---|---|
| `DTTS_<バージョン>_x64.msix` | アプリ本体 |
| `DTTS_<バージョン>_x64.cer` | 署名に使った証明書（公開鍵のみ） |
| `Dependencies\x64\*.msix` | Windows App Runtime（無い PC 向け） |
| `Install.ps1`・`Add-AppDevPackage.*` | 証明書の信頼からインストールまでをまとめて行うスクリプト |

## かんたんな方法：同梱の Install.ps1 を使う

フォルダー内の `Install.ps1` を右クリック →［PowerShell で実行］します。
証明書の信頼（管理者の確認が出ます）・Windows App Runtime の導入・アプリのインストールをまとめて行います。
うまくいかない場合や、何をしているか確認しながら進めたい場合は、以下の手順 1〜3 を手作業で行ってください。

## 1. 証明書を信頼させる（初回のみ・管理者権限が必要）

**エクスプローラーで行う場合**

1. `DTTS_<バージョン>_x64.cer` をダブルクリックし、［証明書のインストール］を押します。
2. 保存場所で **［ローカル コンピューター］** を選びます（管理者の確認が出ます）。
3. ［証明書をすべて次のストアに配置する］→［参照］→ **［信頼されたユーザー］（Trusted People）** を選びます。
4. ［完了］を押します。

**PowerShell（管理者として実行）で行う場合**

```powershell
Import-Certificate -FilePath .\DTTS_1.0.4.0_x64.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

> 「信頼されたルート証明機関」には入れないでください。MSIX のサイドロードには「信頼されたユーザー」で足ります。

## 2. アプリをインストールする

**エクスプローラーで行う場合**: `DTTS_<バージョン>_x64.msix` をダブルクリックし、［インストール］を押します。

**PowerShell で行う場合**（管理者権限は不要）:

```powershell
Add-AppxPackage -Path .\DTTS_1.0.4.0_x64.msix
```

インストール後、スタートメニューの **DTTS** から起動できます。

## 3. 「Microsoft.WindowsAppRuntime.2 が必要です」と出た場合

Windows App Runtime が入っていない PC です。同梱の依存パッケージを指定してインストールします。

```powershell
Add-AppxPackage -Path .\DTTS_1.0.4.0_x64.msix -DependencyPath (Get-ChildItem .\Dependencies\x64\*.msix).FullName
```

## 更新・アンインストール

- **更新**: 新しいバージョンの `.msix` を同じ手順でインストールすると上書き更新されます。設定と戦績は残ります。
- **アンインストール**: スタートメニューの DTTS を右クリック →［アンインストール］。
  設定・戦績（アプリのデータフォルダー内の `dtts.db`）も削除されます。

## 通信対戦（LAN）を使う場合

初めてホストになるとき、Windows Defender ファイアウォールの許可を求められることがあります。
**プライベート ネットワーク** を許可してください。同じ LAN 内の PC からロビーで部屋が見えるようになります。

## うまくいかないとき

| 症状 | 対処 |
|---|---|
| 「この アプリ パッケージの発行元証明書を確認できませんでした」(0x800B0109) | 手順 1 の証明書の信頼が済んでいません。保存場所が「ローカル コンピューター」の「信頼されたユーザー」か確認してください。 |
| 「Microsoft.WindowsAppRuntime.2 …」が見つからない | 手順 3 を行ってください。 |
| 開発用に登録した DTTS が既にある（0x80073CFB など） | 開発版をアンインストールしてからインストールしてください（`Get-AppxPackage *12f303fd* \| Remove-AppxPackage`）。開発版のデータは消えます。 |
| 起動直後に終了する | `%LOCALAPPDATA%\Packages\12f303fd-92ec-4cdb-bfb0-e30f02f5c93f_<発行元ハッシュ>\LocalState\crash.log` に原因が記録されます。 |

## 開発者向け：パッケージの作り方

```powershell
# 1. テスト用証明書を作る（初回のみ。現在のユーザーの証明書ストアに入る・管理者権限不要）
powershell -ExecutionPolicy Bypass -File tools\packaging\New-TestCertificate.ps1

# 2. Release ビルド・署名・出力（artifacts\msix\ 以下）
powershell -ExecutionPolicy Bypass -File tools\packaging\Build-SignedMsix.ps1
```

- 証明書のサブジェクトは `Package.appxmanifest` の `Publisher`（現在 `CN=seisu`）と一致している必要があります。
  公開用に発行元を変える場合は、マニフェストの `Publisher` と証明書を揃えて作り直してください。
- バージョンは `Package.appxmanifest` の `Identity/@Version` で管理します（タイトル画面の表示もここから読みます）。
- 秘密鍵（`.pfx`）や `artifacts\` はリポジトリに含めません（`.gitignore` 済み）。

## 開発者向け：テストを無音で実行する

- `dotnet run --project Tests/DTTS.Core.Tests/DTTS.Core.Tests.csproj` は常に無音です（再生処理をテストに含めておらず、起動時にそのことを確認します）。
- アプリを自動操作で動かすときは、起動引数 `--mute` を付けます。保存済みの設定は変えずに、その回だけ音を止めます。

```powershell
Start-Process "shell:AppsFolder\12f303fd-92ec-4cdb-bfb0-e30f02f5c93f_<発行元ハッシュ>!App" -ArgumentList "--mute"
```

- パッケージ化せずに起動する場合は、環境変数 `DTTS_MUTE=1` でも同じ効果があります。
