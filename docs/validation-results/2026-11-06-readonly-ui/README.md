# Windowsの読み取り専用コントロール結合試験

P1-07-01。`2749c1d`の[ネイティブGUI CI](https://github.com/m-hiroaki/codeView/actions/runs/42189237117)で、製品の通常全文表示を使う明示診断ビルドを実行した。[手順と範囲](../../native-gui-validation.md#読み取り専用のコントロール結合試験)。通常製品のコード/依存/公開API/アーキテクチャは変更していない。追加コードは既存のCodeViewPerformanceProbe条件内だけに含める。

## 最終結果

| 生成入力 | UTF-16単位 | プロセス数 | 編集を試みた操作数 | CopyのNative Clipboard往復 |
| --- | ---: | ---: | ---: | --- |
| 日本語/絵文字/CRLF・日本語パス | 76 | 3 | 72 | 3回一致 |
| 空文書 | 0 | 3 | 72 | 対象文字なしとして3回を区別 |
| Unicode/タブの横に長い行・CRLF | 146 | 3 | 72 | 3回一致 |

各プロセスで先頭・EOF・全選択から、文字入力・改行・貼付・削除・切取・Undo・Redo・テキストDropの24操作を実行した。**全9プロセス/216操作で、原文の全文UTF-16 SHA-256、文書参照、タブ/パス、Undo/Redo履歴が不変**だった。独立したPython復号から求めた原文ハッシュとも一致し、入力ファイルのバイトSHAも全実行前後で一致した。実コントロールの読取専用/テキストDrop無効設定と本文フォーカスも確認した。

9プロセスそれぞれで、別の編集可能なネイティブWindow/TextEditorも使用した。同じRouted入力、Native ClipboardからのPaste、Delete、Undo/Redo、テキストDropによって対照の内容が実際に変わることをすべて確認した。製品本文の不変を、操作が届かなかったための空振りと混同しない。生成Clipboardデータは専用CIセッション内だけで使用し、終了前に消去した。

各アプリのOpened/complete/Closedと正常終了をログで照合した。同じコミットで、従来の空画面/小ファイル各10回、計20プロセスの起動取得も再度成功している。これは起動/物理描画の性能目標達成ではない。

## 初回失敗と是正

`d89d7a2`の[初回CI](https://github.com/m-hiroaki/codeView/actions/runs/42188557407)は、対照の操作と製品の文字/改行/貼付を通過した後、試験がCanDelete=falseを要求して停止した。固定版の[EditingCommandHandler](https://github.com/AvaloniaUI/AvaloniaEdit/blob/86fdebec4cff7affb0e14c7885df71d28edce777/src/AvaloniaEdit/Editing/EditingCommandHandler.cs)は文書があればCanDelete=trueとし、実行時に読取専用範囲を保護する。検証側の仮定だったため製品を変えず、値を保存してDeleteを実行し、その後の本文/履歴を判定する形へ修正した。

最終結果では27回のDeleteすべてCanDelete=trueで、実行後の本文/履歴は不変だった。`initial-ci.json`/`initial-failure.json`/同.logに失敗時の原記録とZIPハッシュを保存した。失敗した試行を最終9プロセスへ混ぜていない。

## 検証と保存物

収集器は全24セルの欠落/重複、操作の未到達、無効な対照、原文/フォーカス/タブ/履歴の不整合を失敗させる。非CI実行拒否、件数上限、空/Unicode、失敗終了/期限切れ後の再実行なしを含む単体8ケースは、[両OS通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42189237167)ですべて成功した。macOSは.NET689/Python73、Windowsは.NET686+既存Darwin専用3スキップ/Python53+既存POSIX専用20スキップ。UI操作本体は実ウィンドウ/Clipboardのある結合試験で検証し、通常製品へ診断型が含まれないことも既存単体で確認した。

元ZIPのGitHub digestと、全JSON/ログのSHA-256を再照合した。各レポートの製品コミット、apphost/DLL識別、3入力の原文ハッシュ、24セルと9回の正常終了も再検証した。

- `runs.json.gz`：全9プロセス、対照・操作別ハッシュ、Clipboard結果、ビルド/入力識別。
- `phase-logs.json.gz`：全9アプリの実行段階、24操作と正常終了。
- `fixture-*.cs.txt`：元の3入力。空ファイルも0バイトのまま保存。
- `files-sha256.json` / `summary.json`：原ファイル・ZIP・ソース/バイナリのハッシュ、件数、範囲。
- `environment.txt` / `ci-normal.json`：実CI環境と通常試験/収集器の結果。

Windows Server 2025 / SDK 10.0.403 / runtime 10.0.14、Hyper-V Videoのホスト型CIである。元ZIPは`artifacts/gui-ci-probe/readonly-ci/success.zip`に保持し、Actions artifactでも14日保管する。

**OSのキー/マウス/IME、Explorer/Finderの実ドラッグ、ショートカット、物理描画、macOS、通常配布物の受入ではない。** Routed TextInput/Dropとコントロールコマンドを使うネイティブ結合試験として完了する。読込中/失敗画面を含む包括受入、起動/描画の性能未達、P1-07/G1全体は未完了のまま維持する。
