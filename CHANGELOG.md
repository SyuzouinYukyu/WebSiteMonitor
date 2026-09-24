# Changelog

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