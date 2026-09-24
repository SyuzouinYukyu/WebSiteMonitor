# 🛠️ WebSiteMonitor ビルドガイド

## 📋 前提

- Windows 11 x64
- .NET 10 SDK
- PowerShell 7 推奨

## 📦 FFmpeg リソースについて

公式配布EXEには BtbN FFmpeg-Builds の Windows x64 LGPL shared build を埋め込んでいます。

GitHubリポジトリには、容量の大きい第三者バイナリをGit履歴へ恒久的に含めないため、次のファイルを収録していません。

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

完全なReleaseビルドおよびFFmpeg統合テストを再現する場合は、上記SHA-256と一致するリソースを `WebSiteMonitor/Resources/` に配置してください。

> [!NOTE]
> FFmpegの由来・ライセンス・実行時展開方式は `THIRD_PARTY_NOTICES.md` と `WebSiteMonitor/SoundService.cs` で確認できます。

## 🧱 ビルド

リポジトリのルートで実行します。

```powershell
dotnet restore .\WebSiteMonitor.sln
dotnet build .\WebSiteMonitor.sln -c Release
```

## 🧪 テスト

FFmpeg リソースを配置した完全なソースツリーで実行します。

```powershell
dotnet test .\WebSiteMonitor.sln -c Release --no-build
```

v1.0.4 正規ソースでは合計115件のテストが成功し、失敗0・スキップ0を確認しています。

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

## 🔎 正規 v1.0.4 の確認値

```text
WebSiteMonitor_v1.0.4.exe
SHA-256: 63F6484ACFDBF43EDDA4F43C576C49B787EF3E9B0AA5F3DEE4D6D04780341F63
Size:     222,545,532 bytes
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
