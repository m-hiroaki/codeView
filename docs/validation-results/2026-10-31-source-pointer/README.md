# 有界表示の原文マウス選択

2026-10-31。`9321cc7`からの差分。専用GUIの左クリック/ドラッグ/Shiftクリックを原文位置・選択へ接続した。通常UIへの有界表示採用、既知の再帰障害全体の受入は未完了。

## 構造と制約

BoundedPointerSelectionはPointerIdだけを保持し、本文/表示Slice/デバイスを保持しない。原文Anchor/CaretはDocumentDisplaySessionが所有する。開始/移動は現在Sliceの同一性を検証し、他ポインターや旧Sliceのイベントで原文を変えない。クリック位置はTextViewのヒットテストとスクロールOffsetを使って局所位置へ変換してから原文へ渡す。Shiftは元のAnchorを保持し、描画は選択とSliceの交差部分だけを使う。

専用GUIはライブラリの局所ドラッグ開始を受け渡さず、同じポインターのキャプチャを追跡する。解放/キャプチャ喪失/文書切替/終了/キー入力で追跡とタイマーを止め、原文選択は維持する。ドラッグ中は窓内の余白だけで先行切替せず、Caretが窓外へ出たときに切り替える。範囲外への延長はUIタイマー1件、40ms間隔で縦1論理行または横32スカラー/改行境界。縦を優先し、原文端で止まる。

ScrollOffsetChangedから選択を変更しない。既存の最新1件のLoaded可視化を使い、選択変更とスクロール通知の同期再帰を作らない。上流の[SelectionMouseHandler 12.0.0](https://github.com/AvaloniaUI/AvaloniaEdit/blob/12.0.0/src/AvaloniaEdit/Editing/SelectionMouseHandler.cs#L652)には、選択モード時にスクロール通知から選択を更新する経路がある。過去のスタックと同じ経路だが、以前の障害の開始条件を完全に特定したものではない。

理由・利点/コスト・影響・移行は[詳細設計04の6.6](../../detailed-design/04-documents-tabs.md#66-マウス選択と範囲外ドラッグ専用試作)。層/公開API/依存方向は変更していない。採用時は計測依存を残さず、入力接続をPresentationへ移す。

## 未達と修正

最初の実ドラッグは空選択となった。[初回報告](source-pointer-20261031.json)はPointerUpdates=3、コピー成功0であり、プロセスの終了0を機能合格と扱わない。前面化前の最初のCUA操作はnoWindowsAvailableだったが、CLIは同じセッションで実行継続を確認しており、再起動せず前面化した。

[診断](source-pointer-diagnostic-20261031.json)では、左押下→TextEditorへのキャプチャ→Left=falseの移動→解放が届いた。移動時のボタンフラグだけを使って途中終了していたため、受付済み左押下と同じPointerId/キャプチャを基準とし、解放/喪失で止めるよう修正した。ツール/OS/ランタイムのどの層でこのフラグになるかは未特定。上流の[マウス移動処理](https://github.com/AvaloniaUI/AvaloniaEdit/blob/12.0.0/src/AvaloniaEdit/Editing/SelectionMouseHandler.cs#L622)も、既存の選択モードで移動を扱う。

反復性能の[初回未達](pointer-cycles-initial-over-budget.json)は10万回46.0218ms/CPU46.619ms/割当16,000,128 bytes、GC停止4.183ms、Gen0/1/2=2/1/0。1 MiBの割当予算を超えた。[切分け](pointer-cycles-diagnostic-over-budget.json)では同位置の単独移動は80 bytesだけで、ドラッグの2点間反復は16,000,000 bytesだった。位置変換単独は0。これだけで全割当の内部型までは断定しない。

境界補正に必要な左右2文字が現在の不変Sliceにあれば、その既存文字列を使う。端/範囲外では従来どおり原文から読む。追加のキャッシュ/全文複製/データ構造変更をせずに、原文の遠い位置を反復参照する割当を避けた。本文を変更しない契約とSliceの寿命は維持する。

## 最終単体と性能

Release通常569件合格、失敗0・スキップ0（`bounded-pointer-normal-verified.trx`）。追加11件はドラッグ状態10とUnicode/CRLF補正同値1。

- 通常/逆方向/Shift選択、窓をまたいだ原文Anchorとコピー範囲。
- 他ポインターの開始/移動/解放拒否、旧/複製Slice拒否、取消後の不採用と再開。
- 空/EOF/不正範囲、サロゲート/CRLF、縦優先・希望列・原文端での停止。
- Slice内/端/外の全位置を、表示文字列を持たない場合の補正と照合。

事前の[性能予算22章](../../performance-plan.md#22-原文ドラッグ状態の反復)を変えず、[最終測定](pointer-cycles.json)が合格した（`bounded-pointer-performance-final.trx`）。macOS arm64 / .NET 10.0.12 / Release、同時に実GUI/ビルドは実行していない。小さい読込済み本文で1,000回のウォームアップ後、10万回のBegin→Move→Endを測定した。

| 項目 | 最終値 |
| --- | ---: |
| 経過 | 12.1398 ms |
| CPU | 12.646 ms |
| 当該スレッド割当 | 128 bytes |
| GC停止 / Gen0・1・2 | 0 ms / 0・0・0 |
| 終了時の追跡 | なし |
| 読取Lease | セッション所有1件のみ |

測定後の各10万回の切分けは位置変換/同位置の原文移動/表示からの移動/ドラッグ反復すべて割当0。JSON保存/構築/検証は測定外。ネイティブ入力、ヒットテスト、描画、範囲外タイマー、OSメモリ、起動、検索性能の数値ではない。通常製品の起動/検索へ試作の入力処理を追加していない。

## macOS実画面

前回と同じ生成済み10,435 bytes・8,033 UTF-16単位・5行、2行目8,008単位を使った。生成方法とハッシュは[前回の記録](../2026-10-31-source-clipboard/README.md)と[input-manifest.json](input-manifest.json)。原文ハッシュは試験後も一致した。

専用.appをCODEVIEW_GUI_WINDOWED=1 / CODEVIEW_GUI_INTERACTIVE=1 / CODEVIEW_GUI_WINDOW_CHARS=1024 / CODEVIEW_GUI_VERIFY_CLIPBOARD=1で起動し、生成フォルダと本文ファイルをCLI引数で渡した。CUAで前面化して次の操作をした。VERIFYは追加読戻しを行う試験モードで、本文文字列はログへ保存しない。

| 操作 | コピーされたUTF-16単位 |
| --- | ---: |
| 先頭行のfirstを左から右へドラッグ | 5 |
| 日本語/絵文字を含む行を右から左へドラッグ | 19 |
| Shiftクリックで既存Anchorから先頭行へ拡張 | 23 |
| キーで長行末尾へ移動後、左の表示外へドラッグして解放 | 98 |

割当修正前の[報告](source-pointer-final-20261031.json)/[ログ](source-pointer-final-20261031.log)と、最終ソースの[報告](source-pointer-verified-20261031.json)/[ログ](source-pointer-verified-20261031.log)を区別した。最終版でも4件のOS読戻しが渡した文字列とOrdinal一致、不一致0。表示最大1,024、PointerUpdates=10、PointerActive=false、Closed/CLI終了0。最終の原文位置は2行7909列、選択Anchor=8015/Caret=7917。表示行番号は局所座標のまま。

[先頭選択画面](first-selection.png)/[AX](first-selection.ax.txt)、[割当修正前の長行画面](long-selection.png)/[AX](long-selection.ax.txt)、[最終版の長行画面](verified-long-selection.png)/[AX](verified-long-selection.ax.txt)を保存した。通常/逆方向の選択色とコピー成功、原文ステータスを目視確認した。ソース/アセンブリハッシュと実行状態は[provenance.json](provenance.json)へ保存した。

## 残るゲート

今回の範囲外ドラッグはタイマー発火0回で、解放前の長押しによる自動移動・窓入替を実機で検証していない。予期しないキャプチャ喪失、ドラッグ中のタブ切替/再読込、スクロールバー/ホイール、多重クリックの単語/行選択、タッチ/ペン、IME/結合列/双方向表示、50/100 MiB/別文字コード/Windows/長時間利用は残る。既知の再帰障害全体を解決済みとしない。通常UI採用とG2/A-02も未完了。
