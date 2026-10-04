# WebSite Monitor v1.1.0 検証記録

検証日：2026-10-04。v1.0.9のsrcを複製し、新版だけを変更。実運用の認証情報やDBはテストに使用していません。

## A. 実装結果

- FFmpeg外部参照化、新規サイトの音量100・更新ダイアログON：PASS。既存の音量0/37/100と通知ON/OFFは保存・再読込・編集でも維持。
- 設定入出力：PASS。全19項目のサイトユーザー設定と全AppSettingsを移送。ID・監視世代・履歴・FIFO・取得状態・音声ファイル本体は対象外。
- wsmcfg形式1：AES-256-GCM、PBKDF2-HMAC-SHA256 200,000回、Salt16/Nonce12/Tag16bytes、パスワード8文字以上。エクスポート時の確認入力とマスク表示、ヘッダー認証、原子的暗号文保存。平文のエクスポート一時ファイル・パスワードの保存/ログ記録なし。
- 誤パスワード・改ざん・破損・非対応形式・必須項目欠落・範囲外値：既存データを変更せず拒否。
- 追加・統合：再取り込みの増殖なし。同じURLでも抽出条件の違う定義を区別。既存ID・取得状態・履歴・FIFOを保持。
- 置換：同一定義のID・履歴・FIFOを保持。履歴/未確認通知の参照を壊す削除は全体を中止。
- アプリ全体の設定は両方式とも取り込み元へ置換。音源絶対パス不在は件数だけ警告し、インポートを成功扱いとする。
- DB書込み失敗・DBコミット失敗・設定ファイルの上書き失敗を実際に発生させ、サイト/履歴/FIFOと元の設定ファイルの完全一致を確認。復元そのものが失敗する媒体障害やプロセス強制終了についての保証はREADMEの制限事項を参照。

## B. FFmpeg

- 新版の埋め込みZIPを削除し、展開・アーカイブ検証・旧FFmpeg cache処理を廃止。ダウンロード・導入処理なし。
- 現在のプロセスPATHから絶対ディレクトリだけを探索。空要素・相対/CWD・不正要素を除外し、引用符・空白・日本語を含むディレクトリを回帰テスト。
- PATH未導入では音声だけを固定案内付きで失敗させ、後続の音声キューと監視の継続を確認。最終EXEの自己診断もPATH空で成功。
- 実際のPATH上のFFmpegで15件成功：11形式デコード、APE decoder能力、不正音源、5秒PCM連続読出し、実外部プロセスのDispose終了。PCM 44100Hz/16bit/2ch・短読出し・shell不使用・非表示起動の既存処理を維持。
- スピーカーからの実音の試聴は未実施。PCM/MIDI・停止・終了の既存自動テストは維持。

## C. ビルド・テスト

- Releaseビルド：エラー0、警告0。依存パッケージの新規追加なし。
- 既存171件＋追加13件＝184件。全体は1回実行し、初回180成功/4失敗/0スキップ。
- 4失敗は旧FFmpegアーカイブ前提1件と、並列STAテストがプロセス共通フォント状態を共有した競合3件。旧前提を非同梱検証へ更新し、GUIテストを既存の非並列WinFormsグループへまとめた。製品のフォント処理は変更していない。
- 失敗関連だけ8件を再実行し、8成功/0失敗/0スキップ。必要な通知排他・Feed認証情報往復の直接確認も成功。最終的に184件すべての確認が済み、未解消失敗0、スキップ0。追加13件も全成功。
- RSS/Atom、抽出、スケジューラー、スキーマ移行、秘匿、通知/FIFO、音声キュー/停止の既存テストを削除・無条件スキップしていない。
- 新規ダイアログのマスク入力・初期統合選択・主要操作・文字寸法を10pt/18ptで自動確認。最終EXEの既存UIプローブは10pt/18pt・幅980で終了コード0。
- 自動確認と物理操作は区別する。実マウスでの標準保存/選択ダイアログを含む一連の操作、ブラウザー起動、実スピーカー試聴、実サイトでの監視は未実施。

## D. 変更ファイルと理由（src基準）

| ファイル | 理由 |
| --- | --- |
| WebSiteMonitor.Core/Models.cs、WebSiteMonitor/SiteEditForm.cs | 新規音量100・更新ダイアログON、保存値は維持 |
| WebSiteMonitor/SoundService.cs | 外部PATH解決、埋め込み/cache処理除去、プロセス終了の検証用PID |
| WebSiteMonitor/WebSiteMonitor.csproj | ZIP埋め込み除去、版情報更新 |
| WebSiteMonitor.Core/ConfigurationTransfer.cs（新規） | ユーザー設定DTO、形式1、暗号化/検証/入出力 |
| WebSiteMonitor.Core/Database.cs | 既存保存処理をトランザクション内で再利用し、統合/置換を原子的に反映。スキーマ変更なし |
| WebSiteMonitor.Core/Persistence.cs | 原子的設定更新とDB失敗時の元ファイル復元 |
| WebSiteMonitor/ConfigurationDialogs.cs（新規） | マスク付きパスワード/確認、統合/置換確認、10～18pt対応 |
| WebSiteMonitor/MainForm.cs | 設定入出力メニュー、取り込み時の表示反映、処理中の閉じる/保存抑止 |
| WebSiteMonitor/TrayApplicationContext.cs | 標準ファイルダイアログ、非同期処理、排他、通知完了の遅延処理、固定エラー表示 |
| WebSiteMonitor/OneShotScheduler.cs | インポート中の新規監視抑止・実行中監視の排出待ち・元の一時停止状態保持 |
| WebSiteMonitor/UpdateDialog.cs | 処理中に遅延FIFO表示が新しく開くことを抑止 |
| WebSiteMonitor/Program.cs | 最終EXE自己診断で埋め込みリソースを列挙・確認 |
| WebSiteMonitor/AuxiliaryForms.cs、WebSiteMonitor/UiProbeApplication.cs | About/プローブの版更新、新ダイアログを既存プローブへ追加 |
| WebSiteMonitor.Core/HttpFetcher.cs、WebSiteMonitor.Tests/V104UserAgentTests.cs | 既定User-Agentの版更新。ブラウザー互換UAの仕様は変更なし |
| WebSiteMonitor.Tests/ConfigurationTransferTests.cs、WebSiteMonitor.Tests/V110Tests.cs（新規） | 暗号化/拒否/統合/置換/ロールバック/初期値/PATH/排他/GUIの追加13件 |
| WebSiteMonitor.Tests/FfmpegDecoderIntegrationTests.cs、WebSiteMonitor.Tests/V104AudioTests.cs | 外部FFmpegテストへ変更、実プロセス終了、不要cacheが生成されないことを確認 |
| WebSiteMonitor.Tests/V102RegressionTests.cs | 旧アーカイブ表示の前提を非同梱表示の検証へ更新。制御文字検査は維持 |
| WebSiteMonitor.Tests/FontSettingsTests.cs、WebSiteMonitor.Tests/UiLayoutTests.cs、WebSiteMonitor.Tests/V105DialogTests.cs | GUIテストの実行グループ統一、通知入出力排他を既存FIFOテストへ追加 |
| README.md、CHANGELOG.md、THIRD_PARTY_NOTICES.md、LICENSE、.gitignore | 実構成/使用法/入出力仕様/非同梱表示/公開除外を更新。本体権利留保は維持 |
| RELEASE_VERIFICATION.md（新規） | 本検証記録 |
| WebSiteMonitor/Resources/ffmpeg-win64-lgpl-shared.zip（削除） | 新版に限り非同梱化。旧版は保持 |

## E. 最終EXE

- 絶対パス：F:\codex\ウェブサイト更新通知_WebSiteMonitor\v1.1.0\release\WebSiteMonitor_v1.1.0.exe
- サイズ：145681532 bytes
- SHA-256：39730EC28A5D845002879A2E59E42EF73C36501833F3A66FFB1BA626E575BE91
- ProductVersion 1.1.0 / FileVersion 1.1.0.0
- ProductName / FileDescription：WebSite Monitor
- PE Machine 0x8664、Subsystem 2：x64 Windows GUI。既存アイコンを保持。
- バンドル形式6.0の271構成を実EXEから直接読取り。FFmpeg/avcodec/avformat/avutil/swresample/swscaleに該当する構成0。
- self-contained構成のMicrosoft.NETCore.App / Microsoft.WindowsDesktop.App 10.0.11を内包。
- 実EXEのアプリ内蔵リソースはWebSiteMonitor.Resources.THIRD_PARTY_NOTICES.mdだけ。FFmpeg ZIP/DLLリソースなし。
- PATH空の自己診断：終了コード0、SELF_TEST_OK。発行元と正式EXEのSHA-256一致。

## F. 公開準備・整理

- 公開srcにEXE/DLL/ZIP/DB/ログ/一時ファイル/鍵/設定書出しファイルの混入なし。合成資格情報だけをテストに使用。
- 再ビルド用のソリューション・3プロジェクト・ソース・テスト・Resourcesアイコン・tools・README・CHANGELOG・権利表示を保持。
- NAudio/SQLite/HTML解析ライブラリ等の従来権利表示を保持。.NET 10.0.11のライセンス/第三者通知本文は旧版から内容不変。Aboutには更新済みの同一通知文書を埋め込む。
- 本体コードは権利留保。外部FFmpegのライセンスは利用者の導入ビルドに別途適用。GitHubへのPush/タグ/Release操作なし。
- 削除：新版のpublish-work、release/data、3プロジェクトそれぞれのbin/obj（計8フォルダー）と、今回生成したテスト用一時フォルダー37個。生成物はソースから再作成可能。
- 最終rootはsrcとreleaseのみ。releaseは正式EXE1個のみ。TestResults/DB/log/tmp等の不要生成物なし。

## G. 旧資産

- v1.0.0～v1.0.5、v1.0.7～v1.0.9の全ファイル集合とハッシュは作業開始時と一致。マスターPNGも一致。新版のResourcesアイコンも旧版と同一。
- 旧v1.0.6プロセス（PID30556）が稼働しており、実運用のsettings.json・DB/WAL/SHMが更新/使用中だった。したがって同版の全体ハッシュ不変性は再照合できない。稼働プロセスや実運用データは停止・変更・削除していない。同版のsrcとEXEについて作業中の更新時刻変更は認められない。
- SQLiteスキーマ7・旧設定の保存値・移行OFF仕様・履歴/FIFO互換性の自動回帰は成功。

判定：完成：軽微な留意事項あり。未確認事項は上記の物理操作/試聴と、稼働中v1.0.6実運用データの全体ハッシュ再照合。

暗号API参考：[.NET 10 AesGcm.Encrypt](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0)。鍵導出方式・パラメーターは本版の実装と回帰テストで確認。
