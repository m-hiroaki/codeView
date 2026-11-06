# UI失敗通知から終了待ちへの接続

P1-08-07。`a9c9b34cd20e04fb9ae14bef3d6c85647f89b7de`で[修正・試験](../../ui-failure-boundary.md)を実施した。FileDragOverの未知例外に続くStatus通知障害でMarkFailed/Closeへ到達できない経路と、RunAsyncの通知例外がasync-voidから再送出される経路を修正した。同期のタブ/検索/ツリー/閉鎖操作も同じ失敗通知へ接続した。層・依存・公開API・製品データ構造は変更せず、設定保存は有効化していない。

## 試験結果

[両OS通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42192151448)はmacOS .NET698/Python79合格、Windows .NET695合格+既存Darwin専用3スキップ、Python59合格+既存POSIX専用20スキップ。追加9ケースはいずれのOSでも合格した。TRXのtestIdからクラス名を対応させて9ケースのPassedを確認し、`summary.json`へ各結果を保存した。

- UiFailureTestsの7ケース：正常操作、同期例外/予期しない取消、異常記録→通知→Closeの順序、通知成功/失敗/取消、Close自体の失敗伝播。
- ShutdownIntegrationTestsの追加2ケース：実ViewModel/Operationsの本文とLeaseを持ち、取消不能I/Oを保留した状態からコマンド失敗を注入。Status通知の成功/失敗の両方でClose要求は一度、本文所有を解放し、借用本文と実Task待ちを維持。解放後のI/O完了まで待ち、生成入力も不変だった。

ローカルMacでも通常.NET698/Python79件と対象12ケースが成功した。異常時の境界は制御可能な試験で確認し、OS自体の異常操作網羅とはしない。

## 実GUI・配布物の回帰

[Windows実GUI CI](https://github.com/m-hiroaki/codeView/actions/runs/42192151438)で空画面/小ファイル各10プロセス、64スクロール×10ファイル、読取専用9プロセス/216編集試行・編集可能な対照9回・非空Copy6回がすべて成功した。全レポートを収集器で再判定し、入力ハッシュ、JSON/ログのSHA-256を照合した。これは通常終了の回帰であり、実ウィンドウで未知例外を発生させた受入試験ではない。

[Windows AOT CI](https://github.com/m-hiroaki/codeView/actions/runs/42192151377)でも、通常製品の既存バッチでAOT実発行、可視ウィンドウ/終了0、生成入力SHA不変、元ロック不変が成功した。`aot-evidence.json.gz`へ原記録を保存した。

| 実GUI診断ビルドの指標 | 空画面 中央値 / 最大 | 小ファイル 中央値 / 最大 |
| --- | ---: | ---: |
| Main→Opened (ms) | 999.048 / 1,811.205 | 1,002.154 / 1,340.308 |
| 全試験処理のCPU (ms) | 1,359.375 / 1,468.750 | 2,007.813 / 2,203.125 |
| 採取PrivateMemorySize64 (bytes) | 56,807,424 / 57,565,184 | 70,703,104 / 74,223,616 |

小ファイルの同期本文接続は中央値27.345 ms/最大117.890 ms。起動最大1秒超と初回接続100 ms超は引き続き未達である。CPUはスクロール/終了も含む。ホスト型CIの前回との数値差を改善の証明には使わない。追加制御は操作/異常時に限り、本文読取・検索・構築・スクロール/描画/カーソル通知のループは変更していない。実機性能の受入は残る。

## 保存物と限界

4件のActions artifact ZIPを取得し、全件のSHA-256をGitHub digestと照合した。`ci.json`に実行/ジョブ/各段階とartifactの識別、`summary.json`にTRX集計/追加9ケース・回帰集計、`normal-*.log.gz`に両OSの原ログ、`native-gui-evidence.json.gz`と`aot-evidence.json.gz`に取得した全ファイルの文字列を保存した。UTF-8 BOMは文字列化の際だけ除去し、元バイトのdigest/検証結果は保持した。原ZIPは`artifacts/ui-failure-ci/<artifact-id>.zip`、Actions artifactの保存は14日、保存物のハッシュは`files-sha256.json`。

全UI通知/Dispatcherコールバック、OS実入力、両OS実機の異常終了、設定保存停止の受入、既存性能未達の是正は残る。P1-08/G1全体は未完了とする。
