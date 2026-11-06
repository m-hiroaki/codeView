# macOS読み取り専用コントロール・Clipboardの検証

P1-07-02。`165713f79b833b4e616fd1772a8789c2192bbc53`の[ネイティブGUI CI](https://github.com/m-hiroaki/codeView/actions/runs/42195231380)で、Windowsと同じ通常全文表示候補・3入力・各3プロセスをmacOSでも実行した。[手順と判定](../../native-gui-validation.md#読み取り専用のコントロール結合試験)。通常製品・依存・公開API・アーキテクチャは変更していない。

## 読み取り専用の結果

macOSの9プロセス/216操作は、先頭・EOF・全選択からの文字入力、改行、Paste、Delete、Cut、Undo、Redo、テキストDropの全24セルで、元本文の全文UTF-16 SHA-256、文書参照、タブ/パス、Undo/Redo履歴が不変だった。元ファイルのバイトSHAも不変で、原文ハッシュ/文字数をPythonから独立に照合した。全プロセスのOpened/complete/Closed、終了0を確認した。

各プロセスで別の編集可能なネイティブWindow/TextEditorを使い、同じRouted入力、Native ClipboardからのPaste、Delete、Undo/Redo、テキストDropが実際に内容を変えることも確認した。対照は9回すべて有効で、製品側へ戻った本文フォーカスも確認した。Clipboardは破棄されるCIだけで使用し、finallyで生成データを消去した。

## 初回失敗とCopyの改行差

`b13a3eb`の[初回CI](https://github.com/m-hiroaki/codeView/actions/runs/42193996027)は、Macの編集対照と最初の24操作を通過した後、CopyとCRLF原文の完全一致を待って失敗した。この試行を最終9プロセスへ混ぜていない。初回のJSON/ログ/実行識別も保存した。

[固定版AvaloniaEditのCopySelectedText](https://github.com/AvaloniaUI/AvaloniaEdit/blob/86fdebec4cff7affb0e14c7885df71d28edce777/src/AvaloniaEdit/Editing/EditingCommandHandler.cs#L390)はCRLF/CR/LFをOSの改行へ変換する。標準Copyの期待値に関する試験側の仮定を修正し、原文一致`NativeCopyRoundTrip`を消さず、OS形式との一致`NativeCopyOsRoundTrip`・実長・実ハッシュを別々に判定した。製品のCopy動作は変更していない。

| 入力 | 原文 UTF-16単位 | Mac標準Copy UTF-16単位 | 非空Copy往復 |
| --- | ---: | ---: | --- |
| 日本語/絵文字/CRLF・日本語パス | 76 | 71 | LFへの変換後の全文一致×3 |
| 空文書 | 0 | 対象文字なし | 3回を明示的に区別 |
| タブ/Unicodeの横に長い行・CRLF | 146 | 145 | LFへの変換後の全文一致×3 |

Macの非空6回すべて原文一致はfalseのままで、OS形式との一致はtrueだった。Windowsは同じCRLF入力のため、非空6回とも原文/OS形式の双方が一致した。Pythonは入力から独立にCRLF/CR/LFだけを変換し、他の文字・空白・Unicodeは維持して実Clipboardハッシュ/長さを照合した。原文一致の誤申告、欠落や異なるハッシュは失敗する。

**標準CopyのOS形式での往復を、改行保存された原文コピーの完成とはしない。** [原文コピーの専用試作](../../detailed-design/04-documents-tabs.md#65-原文コピーの受付とクリップボード専用試作)は改行を変換しない契約を維持し、通常UIへの採用・包括受入は未完了である。

## 回帰・証跡・限界

同コミットのWindows側も9プロセス/216操作、両OSの起動は各20プロセスが成功した。[通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42195231409)はmacOS .NET698/Python81合格、Windows .NET695合格+既存Darwin専用3スキップ、Python61合格+既存POSIX専用20スキップ。収集器10ケースは両OSで成功し、未知OS/取り違え、Unicode/空、原文差の保持、改行以外の不一致、無効な対照、欠落/重複、失敗/期限後の再実行なしを含む。明示GUIビルドは手元でも警告/エラー0、Python全81件が成功した。

成功した両OSの全JSON/ログ/入力を`macos-evidence.json.gz`/`windows-evidence.json.gz`へ保存した。原ZIPのGitHub digestと全ファイルのSHA-256を照合し、全18レポートを現収集器で再検証した。`summary.json`に入力別文字数/コピー差・合計、`ci.json`に成功/初回失敗の実行/ジョブ/artifact識別、`initial-*.json`/同.logと`ci-initial.log.gz`に失敗原記録、`ci-macos.log.gz`/`ci-windows.log.gz`に通常CIログ、`files-sha256.json`に保存物のハッシュを残した。原ZIPは`artifacts/macos-readonly-ci/`、Actions artifactは14日保存する。

Mac環境はmacOS15.8.2/24H232、Apple M1 (Virtual)、3 CPU/7 GiB、SDK10.0.403/runtime10.0.14。物理キー/マウス/IME、Finder/Explorerの実ドロップ、ロード/失敗状態の包括受入、物理描画・性能・通常配布物の受入ではない。通常製品のCPU/メモリ/起動/描画/検索へ処理を追加せず、変更はCI収集器・明示診断に限定した。P1-07/G1全体は未完了とする。
