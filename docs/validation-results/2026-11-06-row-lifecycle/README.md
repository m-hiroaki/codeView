# 行別試作のタブ・再読込・検索・終了（2026-11-06）

対象は`c7f2fc9fa8baa9cc69c352a022aefef09f7640fd`。A-03の承認範囲で、専用GUIの原文選択/独立閲覧位置を同じHandleへ復元し、実際の切替・再読込・検索・閉鎖・終了を検証した。[仕様](../../row-lifecycle-prototype.md)。本文/Lease/TabState/検索結果の契約と通常UIは維持する。

[行別GUI CI](https://github.com/m-hiroaki/codeView/actions/runs/42216937981)、[通常試験](https://github.com/m-hiroaki/codeView/actions/runs/42216937993)、[通常GUI](https://github.com/m-hiroaki/codeView/actions/runs/42216937995)、[Windows Native AOT](https://github.com/m-hiroaki/codeView/actions/runs/42216937989)はすべて成功した。配布ZIPのSHA-256をGitHubと照合し、機能/終了/通常GUIの全JSONを収集ツールで再検証した。

## 実際のライフサイクル

長行混在（1 MiB長行+通常100行）、空、Unicode/混在改行の3入力を3独立プロセス/OSで実行した。各OS9プロセス、1,668検証（既存1,482+追加186）、576閲覧変更、90文書接続が成功した。

- 2つの実タブ間で、各本文の原文Anchor/Caretと独立した閲覧位置を復元した。画面外Caretを復元時に勝手にRevealせず、非表示タブの表示Leaseは0だった。
- 実際の再読込でHandleが交換され、旧投影と旧選択/閲覧状態を採用しないことを確認した。独立コピーLeaseは旧本文を読み終えられ、Dispose後の旧HandleのLeaseは0だった。
- 捕捉中のタブ切替と範囲外タイマー中の閉鎖を行い、捕捉・タイマー・旧本文が解放された。再オープンは新しいHandle/状態だった。
- 3入力だけのフォルダを開き、実際の検索を行った。非空6プロセス/OSでは文末から検索結果ListBoxのEnterを通して原文先頭の一致へ移動・選択した。空3プロセス/OSでは存在しないトークンの結果が0だった。閉じたWorkspaceの結果行は再採用しなかった。
- 再Attachを10回通しても試験ワークロードの開始は1回だけだった。全入力のファイルハッシュと、初期/再読込後の本文が不変であることを確認した。

## 実際の終了後

通常のWindow.Closing→ViewModel.Stop→StopAndWait後、最終Close前に別の`*.shutdown.json`を作成した。全18プロセスで履歴消去、文書なし、タブ0、保留作業/コピー完了が真であり、生存している弱参照Handleの未解放数/Active Leaseはともに0だった。収集側も終了0と終了後記録の両方を要求した。

Handle追跡は弱参照だけであり、数値の閲覧履歴も本文/Leaseを保持しない。追加単体6ケースで空/Unicode、更新と不正範囲、同内容の別Handle/閉鎖済みHandle、所有スレッド、弱キーからHandle/本文が解放されることを検証した。通常試験はmacOS .NET747/Python101、Windows .NET744+既存3スキップ/Python81+既存20スキップで成功した。収集側のTimeout/子プロセス失敗/終了記録なしも成功扱いしない。

## 通常表示・配布の回帰

各OS20起動・216読み取り専用操作が成功した。通常のAvaloniaEditコピーは既存のOS改行正規化の契約で照合し、専用原文コピーと混同しない。Native AOTは実際の配布バッチ、AMD64/PE32+・CLRヘッダーなし、通常の可視ウィンドウ、正常終了0、入力ファイル不変を確認した。終了確認フックは専用ビルドだけに含める。

## 性能の扱い

RowAttachmentSamplesへ各文書世代の接続時間を保存した。各OS90接続の最大はWindows 1,234.8133 ms、macOS 54.2986 ms（どちらも最初の長行混在プロセスの初回接続）。Windowsは既定100 msを超えた。最終接続だけを表すDocumentAttachmentMillisecondsで初回の未達を隠さず、サンプルを除外しない。別ランナーの過去記録との差を改善率とは扱わない。今回の小入力でmacOSの100 ms以内を観測したことから、大規模性能の達成を推定しない。

64表示更新の同期処理最大はWindows 24.2659 ms、macOS 45.5791 ms。2 callbackを待つ更新間隔の最大はWindows 94.3052 ms、macOS 115.8357 msで、単一tick16.7 msや物理描画時間へ換算しない。GUI全体のCPU/割当/GC/10 msメモリサンプルは、タブ/再読込/検索/Clipboardと検証用文字列・ハッシュを含む。OSメモリはWindows PrivateMemorySize64、macOS phys_footprintで互換な指標ではない。全数値を`summary.json`とZIPへ保存した。

物理入力/描画、結合列/Bidi/タブ幅、大規模10/50/100 MiB・5文書・文法の固定予算は未完了。A-04-03-02は専用GUIの機能接続と寿命検証の完了であり、A-04-03全体・A-04/A-05/G2と通常採用を完了扱いにしない。

## 原記録

- `ci.json`：最終コミット、実行ID、Artifact IDとGitHub SHA-256。
- `windows.zip` / `macos.zip`：行別GUIの配布ZIPそのもの。入力・環境・各プロセスのJSON/ログ・終了後JSON・`runs.json`を含む。
- `summary.json`：全プロセスの機能件数、90接続/OS、終了後の解放状態、CPU/割当/GC/メモリ。
- `native-gui-*.zip` / `windows-aot-publish.zip`：通常表示と配布の原記録。配布バイナリ自体は含めない。
- `job-*.log.gz`：最終通常試験とNative AOTのジョブログ。
- `local-unit.*.gz`：ローカル.NET747/Python101の記録。追加の収集条件はPython12ケースでも再確認した。
- `source-sha256.json`：対象ソースの指紋。`files-sha256.json`は本READMEと指紋一覧自身以外の原記録の指紋。
