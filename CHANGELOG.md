# Changelog

## 1.1.0 - 2026-10-04

- FFmpegの埋め込みZIP・展開・検証cacheを廃止。現在のプロセスPATHの絶対ディレクトリから外部ffmpeg.exeを直接起動し、非同梱・自動取得なしとした。
- 新規Siteと編集画面の初期値を音量100・更新ダイアログONへ変更。既存の保存値・旧DB移行時のOFF・スキーマ7は維持。
- 設定入出力をv1.1.0へ統合。形式1のwsmcfgをAES-256-GCM/PBKDF2-SHA256 200,000回で暗号化し、全ユーザー設定だけを移送する。パスワードは非保存。
- 検証後に追加・統合または確認付き置換を行い、履歴/FIFOの参照を壊す置換は中止。SQLiteトランザクションと原子的設定更新を連動し、失敗時に元の設定を復元する。
- 監視の排出待ち・設定編集の排他・非同期入出力・音源不在の件数警告を追加。PATH・外部デコード/終了・初期値・暗号化・統合/置換・ロールバック・GUIの回帰テストを追加。
- README、About、User-Agent、第三者権利表示を実際の構成へ更新。本体の権利留保は変更せず、GitHub操作は実施しない。

## 1.0.9 - 2026-10-03

- JSON形式・パーセントエンコード形式の秘密キーを、上限付きのキー分解とデコードで検出。正規表現による秘密キーの事前選別を廃止。
- 外部例外原文は固定分類・HTTP状態・秘匿表示だけに要約し、結果・LastError・ログ・Feedフォールバックの出力境界で共通処理を使用。
- A～Dの実処理経路とエンコード変種・解析失敗・未知の個人情報について回帰テストを追加。スキーマ7および既存監視・通知・UI仕様は変更なし。

## 1.0.8 - 2026-10-03

- 例外文中の入れ子例外・サーバー応答に含まれる秘密パラメーターを先行検出し、結果、SQLiteの`LastError`、新規ログへ秘匿済み文字列だけを渡すよう修正。
- スキーマ7、既存DB、通知・UI・監視の通常動作は変更なし。

## 1.0.7 - 2026-10-03

- 通知・履歴・起動先の安全なイベントURLを統一。Historyに安全な通知先のスナップショット列を追加し、オンラインバックアップ付きの最小移行を実施。
- ログの共通出力境界、エラー表示、旧履歴・未確認通知の読み取りで認証付きURLを秘匿。
- 秘密キーの派生名・区切り・エンコード変種の検査を強化。RSS/Atomの先頭記事を更新対象として推測する処理を除去。

## 1.0.6 - 2026-10-03

- 抑止済み更新の未確認通知登録を修正。サイト別通知先URLと旧通知の再検証を追加。
- 通常のページ識別用クエリを保持し、秘密情報を疑うURLには安全な代替先を使用。
- FileDescriptionを「WebSite Monitor」に統一。

## 1.0.5 - 2026-10-03

- ユーザー向け表示名を WebSite Monitor に統一。内部識別子とポータブルデータ配置は維持。
- サイト別の更新ダイアログ（既定OFF）を第4通知方式として追加。SQLiteの永続FIFOから最大1個を表示し、「×」の確認保存だけで次へ進む。
- 更新検出と未確認通知を同じトランザクションで保存し、再起動・OS終了・サイト削除後にも未確認通知を復元。リンクは秘密のクエリと認証情報を除去し、RSSの同一オリジンの通常ページを優先。
- Schema 5 へ必要な列・テーブルのみを追加。移行前はSQLiteオンラインバックアップを同じdataディレクトリへ保存し、移行を冪等化。
- 永続FIFO・移行とバックアップ・例外回復・通知設定・リンク・終了・10/14/18pt UIの対象テストを追加。FFmpeg・HTTP・スケジューラーの従来構成を維持。

## 1.0.4 - 2026-09-24

- サイトごとにブラウザー互換 User-Agent を任意で有効化。既定は OFF、Schema 4 へ冪等移行し、切替時は MonitorRevision を更新して旧巡回結果・HTTP 条件ヘッダー・baseline・Auto Feed キャッシュを破棄する。標準 403 には設定ヒントを表示。
- MainForm の「状態」列を Fill に変更し、拡大時の行右側グレー余白を解消。固定列の保存幅を保持し、縮小時は最小幅と水平スクロールを維持する。
- FFmpeg stdout の short read を NAudio の要求サイズまで連続読み取りし、周期的な無音化を修正。EOF の最終ブロックのみ短い読み取りを許容。
- FFmpeg は shell 非経由・非表示で直接起動。埋め込みZIPのハッシュを維持し、cacheを検証・再利用、破損時は staging で修復。未使用の ffprobe / ffplay は runtime cache へ展開しない。
- UA / migration / stale / UI幅 / 5秒PCM / cache破損回復 / 起動フラグの回帰テストを追加。

## 1.0.3 - 2026-09-17

- 通知音を容量256件の有界 FIFO queue と単一 consumer に統一。FFmpeg / NAudio / MIDI の同時 session を防ぎ、再生失敗後も後続要求を処理する。テスト再生停止は通知音と分離し、終了時には current / pending request を安全に cancel する。
- GUI フォント Zoom（10～18pt、settings.json永続化）を追加。Ctrl+ホイールは任意位置で、通常ホイールは非スクロール領域で拡大縮小し、DataGridView等の標準スクロールを維持する。
- 全主要フォーム・ToolStrip・DataGridView・Popupのフォント再適用とレイアウト再計算を追加し、10 / 14 / 18pt と100 / 125 / 150 / 175 / 200% DPI相当の文字幅検査を強化。
## 1.0.2 - 2026-09-16

- `MonitorRevision` による条件付き保存で、監視中の URL・方式・抽出条件変更後へ古い取得結果・履歴・通知済み状態を書き戻さないようにした。v1.0.1 DB は schema 3 へ冪等に移行する。
- ScheduleMode / 間隔 / 毎日時刻 / 有効状態の変更時に `NextDue` を正しく再計算し、保存後 scheduler を即時に再 arm する。
- 通知音の開始・完了・失敗・停止を非同期結果として扱い、FFmpeg/decoder/output device の失敗を明示。MIDIの完了・停止・終了時MCI closeを実装。
- FFmpeg統合テストを埋め込み archive の一時展開経路へ統一。第三者通知を正常UTF-8・正しいMarkdown・archive識別情報付きへ修正。
- MainForm を × でDisposeしてトレイから必要時に再生成する運用へ変更し、UI文字幅検査を強化。
## 1.0.1 - 2026-09-16

- URL・監視条件変更時の baseline / HTTP cache / Auto検出Feed の確実なリセット、v1.0.0 DB migration、明示FeedとAuto検出Feedの分離。
- 同一SiteIdの同時巡回抑止、CancellationTokenによる安全な scheduler shutdown、Retry-After HTTP-date対応。
- CSS Selector / XPath / 正規表現の保存前構文検証、regex timeout、RSS / Atom の updated・description・contentを含むitem正規化。
- BOM / HTTP charset / HTML meta の文字コード判定と Windows-31J・EUC-JP対応。
- 全通知経路に有効なグローバル通知マスター、複数ポップアップのDPI対応stack、Windows統合解除の永続化と再有効化UI。
- UTF-8日本語UIの全面再レイアウト、DPI AutoScale、完全な列見出し、自然な日本語の選択肢、サイト名・URL貼り付けボタン、PCM設定UI。
- LGPL shared FFmpeg resource を再生時だけhash検証・展開する streaming decoder backend、M4Pの非DRM対応、RAM安全解釈、MIDI実音量、代表形式の実デコード統合テスト。
- About画面から埋め込み第三者ライセンスを実表示。

## 1.0.0 - 2026-09-16

- 初版。RSS/Atom、自動Feed検出、HTML全体、本文、CSS Selector、XPath、正規表現監視。
- one-shotスケジューラ、ETag/Last-Modified、SQLite履歴、Windows通知、ポップアップ、通知音、タスクトレイ、単一インスタンス、自動起動、ポータブル設定を実装。

