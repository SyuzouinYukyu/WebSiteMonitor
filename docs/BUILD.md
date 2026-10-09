# 🛠️ WebSiteMonitor ビルドガイド

## 📋 前提

- Windows 11 x64
- .NET 10 SDK
- PowerShell 7 推奨

## 📦 FFmpeg について

v1.1.6のEXEと公開ソースにはFFmpegの実行ファイル・DLL・ZIPを同梱しません。圧縮音声などの通知音を利用する場合は、利用者が別途FFmpegを導入し、`ffmpeg.exe` のある絶対パスのディレクトリをPATHへ登録してください。自動取得・インストールは行いません。FFmpegがなくても監視や音声以外の通知は継続します。

以下はv1.0.4以前の埋め込み方式に関する履歴であり、v1.1.6のビルド要件ではありません。GitHubリポジトリには次の旧版用ZIPを収録していません。

```text
WebSiteMonitor/Resources/ffmpeg-win64-lgpl-shared.zip
```

v1.0.4 正規ビルドで使用したリソース情報:

```text
Local filename: ffmpeg-win64-lgpl-shared.zip
Size:           76,981,137 bytes
SHA-256:        40633DAB97D235F7DE4FF5B8E34E80D778D4E89F97EFB142B081127F3D7C8633
Cache ID:       btbn-lgpl-shared-20260915
```

v1.1.6のビルドに上記ZIPの配置は不要です。

> [!NOTE]
> 現行版の外部FFmpeg方針と第三者ライセンスは `README.md` と `THIRD_PARTY_NOTICES.md` を確認してください。

## 🧱 ビルド

リポジトリのルートで実行します。

```powershell
dotnet restore .\WebSiteMonitor.sln
dotnet build .\WebSiteMonitor.sln -c Release
```

## 🧪 テスト

ソースツリーで実行します。外部FFmpegを使う統合テストの条件は各テストの記述に従ってください。

```powershell
dotnet test .\WebSiteMonitor.sln -c Release --no-build
```

v1.1.6は2026-10-09の独立検査で345件成功、失敗0、スキップ0を確認しています。今回の再確認結果と未検証範囲は `RELEASE_VERIFICATION.md` を参照してください。

## 📤 single-file Release

```powershell
dotnet publish .\WebSiteMonitor\WebSiteMonitor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:DebugSymbols=false `
  -o .\publish
```

## 🔎 正規 v1.1.6 の確認値

```text
WebSiteMonitor_v1.1.6.exe
SHA-256: 49EF123273673D8787CBB79614642812C08E1080A71E100EF916F5F0145D36FB
Size:     145,735,292 bytes
```

## 🔐 公開時の注意

GitHubへ追加してはいけないもの:

- ❌ `data/`
- ❌ `settings.json`
- ❌ `WebSiteMonitor.db` / WAL / SHM
- ❌ ログ
- ❌ 個人の通知音
- ❌ 実際の監視URL一覧
- ❌ トークン・Cookie・認証情報
- ❌ `bin/` / `obj/` / `TestResults/`
- ❌ Release EXE（GitHub Releasesへ配置）

`.gitignore` でこれらを除外していますが、公開前には必ず差分も確認してください。
