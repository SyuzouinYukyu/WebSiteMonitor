# WebSite Monitor v1.1.6 完了報告

判定: v1.1.6 完成（実GUI目視・物理操作は未実施）。検証日: 2026-10-09（日本時間）。

公開前競合修正: ApplyErrorの同一トランザクションで更新後の連続エラー数を取得し、commit後にCheckResult.CommittedConsecutiveErrorsへ固定。UIでDBを再読込せず閾値一致を判定する。閾値1/2/3の遅延完了、正常復帰、通常失敗・期限超過とMonitorRevision不一致の破棄を確認した。

連続エラー警告回数をアプリ全体設定へ追加（既定3、0で無効、1～9999）。各監視結果のDB確定ConsecutiveErrorsと閾値の完全一致で1回警告し、正常監視後の新しい連続障害で再通知します。更新通知OFFでも警告は有効です。新しいカウンター・サイト別フラグ・DB列は追加していません。

半角数字以外は「半角数字のみ入力可能です」、空欄・10000以上は「0～9999の範囲で入力してください。」を表示し、保存を中止して対象欄へフォーカス・全選択します。TextBoxに入力長制限は設けていません。旧settings.json・旧wsmcfgの項目省略時は3となります。wsmcfgの範囲外値は拒否します。

バージョン情報に https://github.com/SyuzouinYukyu/WebSiteMonitor を表示し、既存BrowserLaunchで開きます。起動例外は固定日本語で案内します。第三者ライセンス表示を維持しています。

GUIはAutoSize、TableLayoutPanel、既存ScrollableDialogLayout、DPI AutoScale、UiFontManagerを使用し、新しいTextBoxの高さをフォントに合わせ、GitHubリンクを利用可能な幅で折り返します。設定画面の保存・キャンセル・バージョン情報ボタンは固定下部に残します。

## テスト

- 最終Release全テスト: 成功 345 / 失敗 0 / スキップ 0。
- 既存342件を削除・無効化せず、競合修正向け3件を追加。
- 競合修正前のv1.1.6初回: 340成功・GUI検査2失敗。修正後の対象2件が成功し、最終全テストも成功。
- 10pt・18pt、通常／縮小状態でPreferredSize、重なり、コントロールの親領域、固定下部ボタン、スクロール後の入力欄到達性を自動検査。GUI新規検査2件とも成功。
- settings.json保存／再読込・旧項目なし設定、wsmcfg形式1の暗号化エクスポート／読込／統合インポート、旧項目なしwsmcfg、範囲外拒否を検証。
- 実際のMonitorEngine.CheckAsyncとテスト用DBで1→2→3→4→5、正常監視で0、その後の3回目の再警告を検証。失敗結果のSiteがDB増分前の値であることも確認。
- 閾値0、成功／Discarded等の非Failed、監視テスト成功／失敗によるカウント不変、サイト別の独立性、更新通知OFFを確認。
- GitHub URLの表示、リンク先、BrowserLaunchの起動先を確認。自動テストから実ブラウザーは起動していません。
- 結果: F:\codex\ウェブサイト更新通知_WebSiteMonitor\v1.1.6\src\verification\v116-race-final-tests.trx

## 正式成果物

EXE: F:\codex\ウェブサイト更新通知_WebSiteMonitor\v1.1.6\release\WebSiteMonitor_v1.1.6.exe

- サイズ: 145,735,292 bytes
- SHA-256: 49EF123273673D8787CBB79614642812C08E1080A71E100EF916F5F0145D36FB
- 本体/Core: 1.1.6（FileVersion / AssemblyVersion 1.1.6.0）、User-Agent: WebSiteMonitor/1.1.6。
- win-x64 / .NET 10 / SelfContained=true / PublishSingleFile=true / PublishTrimmed=false / DebugSymbols=false。
- FFmpeg非同梱、既存アイコンをバイト一致で維持。releaseは正式EXE1個のみ。
- DB schema: 7のまま。wsmcfg FormatVersion: 1のまま。
- v1.1.5の全80ファイルは作業前後SHA-256一致。ソースコピー時72ファイルの不一致0。
- DBと監視エンジンは競合修正に必要な確定値取得・受け渡しだけを変更。スケジューラー、既存サイト編集、通知・音声・再起動等の無関係な実装は変更していません。HttpFetcherはUser-Agentの版文字列だけ変更。
- 2026-10-09の完成検証時点ではGitHub push / PR / タグ / Release公開は未実施（以下の整理記録とは別の履歴）。

## 変更・追加ファイル（v1.1.6\src配下）

- CHANGELOG.md
- README.md
- RELEASE_VERIFICATION.md
- WebSiteMonitor.Core\ConfigurationTransfer.cs
- WebSiteMonitor.Core\Database.cs
- WebSiteMonitor.Core\HttpFetcher.cs
- WebSiteMonitor.Core\Models.cs
- WebSiteMonitor.Core\MonitorEngine.cs
- WebSiteMonitor.Core\Persistence.cs
- WebSiteMonitor.Core\WebSiteMonitor.Core.csproj
- WebSiteMonitor.Tests\V111Tests.cs
- WebSiteMonitor.Tests\V112Tests.cs
- WebSiteMonitor.Tests\V116Tests.cs
- WebSiteMonitor\AuxiliaryForms.cs
- WebSiteMonitor\ConsecutiveErrorAlert.cs
- WebSiteMonitor\TrayApplicationContext.cs
- WebSiteMonitor\WebSiteMonitor.csproj
- docs\BUILD.md
- docs\USER_GUIDE.md

docs\BUILD.md / docs\USER_GUIDE.md は基準ソースに存在しなかったため、新規作成しました。過去のCHANGELOG記述は保持しています。

## 既知の問題・未検証事項

既知の製品不具合: なし（今回の検証範囲）。実GUI目視確認: 未実施。物理クリックによるエラーダイアログ確認、実際のOS DPI切替、既定ブラウザーの起動・起動失敗時案内の物理操作は未実施です。自動検査を人間による実機目視確認とは扱っていません。

## 2026-10-10 正式版整理

独立検査で指摘された変更一覧の Database.cs / MonitorEngine.cs 記載漏れを補完しました。製品コード・プロジェクト・依存パッケージ・正式EXEは変更していません。v1.1.6、DB schema 7、wsmcfg FormatVersion 1を維持します。

verification配下は最終合格記録 v116-race-final-tests.trx（345成功 / 0失敗 / 0スキップ）だけを保持し、途中記録6件を除外しました。以下の旧版検証記録は履歴であり、その証跡ファイルは今回のパッケージには含みません。独立検査報告の42件成功は2026-10-09の検査結果であり、今回の再実行件数ではありません。

### 今回の再確認（2026-10-10）

隔離コピーをRelease構成でビルドし、全345件を再実行しました。ビルド時の警告・エラー表示はなく、全体結果は344成功 / 1失敗 / 0スキップでした。既存V115Tests.PastePreservesExpressionsAndSavedRegexStillPreviewsAndReloadsで、プレビューが「取得中… プレビュー整形」のまま「1.2.3」を取得できず失敗しました。

製品・テストコードを変更せず、同項目、V116Tests、ConfigurationTransferTestsを限定再実行し、15成功 / 0失敗 / 0スキップでした。初回の失敗原因は特定していません。今回の全体検査を345件全成功とは扱いません。正式EXEは再発行せず、独立検査済みSHA-256・145,735,292 bytes・ProductVersion 1.1.6の一致を再確認しました。

GitHub mainは既存履歴を親に持つ通常の追加コミットで更新します。配布EXEはローカルreleaseに保持し、GitHubのソース履歴には含めません。Release v1.1.6作成・EXE添付はコネクタ非対応のため手動作業として残ります。

---

## 参考: v1.1.5 の公開済み検証概要（履歴）

# WebSite Monitor v1.1.5 検証概要

検証日: 2026-10-06。対象: Windows 11 x64、.NET 10、単一ファイルEXE。

## 判定と範囲

v1.1.5では、サイト追加・編集画面の「監視方式の詳細」にRSS / Atom、CSSセレクター、XPath、正規表現と連動する「貼り付け」ボタンを追加しました。RSS URLは既存のURL正規化を使用し、CSS / XPath / 正規表現は式を保持して末尾CR/LFだけを除去します。自動・ページ全体・テキストでは詳細入力欄とボタンを表示しません。

Release構成の自動テストは337件成功、失敗0、スキップ0です。新規UI検査では7方式の連続切替、4方式の貼り付け、式の保持、保存後再編集、監視テスト／抽出プレビュー、10/18ptと縮小レイアウトを確認しました。

その後、Windows 11実機GUIで表示切替、貼り付け、監視テスト、保存・再編集を確認し、正常動作を確認しました。OSのDPI設定を運用値から切り替える網羅試験や長時間連続運用を保証するものではありません。

## 互換性

- 既存のSQLiteスキーマ7、設定形式1、暗号化・DPAPI、通知FIFO、外部FFmpeg方針を維持しています。
- 監視ロジック、HTTP取得方式、通知方式、再起動処理、ログ仕様に機能変更はありません。
- 旧版の正式資産・設定・履歴・dataを自動変更しません。

## 正式配布ファイル

- ファイル名: `WebSiteMonitor_v1.1.5.exe`
- サイズ: 145,733,756 bytes
- SHA-256: `BDD97A686A98477D12DA9BE92F1701EA4C9CA9308F1AAE6A10A6E099573DDDA4`
- Windows 11 x64 / .NET 10 self-contained / win-x64 / single-file / PublishTrimmed=false。
- FFmpegの実行ファイル・DLL・ZIPは同梱しません。圧縮音声などには利用者が導入した外部FFmpegのPATH設定が必要です。

本体コードの権利は `LICENSE` のとおり留保されています。第三者コンポーネントの表示は `THIRD_PARTY_NOTICES.md` を参照してください。