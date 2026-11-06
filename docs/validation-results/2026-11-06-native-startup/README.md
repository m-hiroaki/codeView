# Windowsネイティブ起動の取得確認（2026-11-06）

P1-09-01。`bcd70cd`の[Native GUI validation](https://github.com/m-hiroaki/codeView/actions/runs/42185900241)で、既存の明示GUI診断ビルドを実行した。[手順・指標定義](../../native-gui-validation.md)。通常のUsePlatformDetectを使い、別レンダラーやヘッドレスへ切り替えずに空画面/小ファイルを交互に各10回、合計20独立プロセスで取得した。製品の通常コード/依存・アーキテクチャは変更していない。

## 成立した確認

- 20試行がすべて正常終了し、Opened/complete/Closedの実アプリログが対応した。製品コミット・DLL/apphostハッシュも記録した。
- 日本語パスのUTF-8/日本語/絵文字/CRLFファイルは76 UTF-16単位・6行で、独立した復号/改行数と一致した。各起動前後のSHA-256が不変だった。事前ハッシュ読取によるWarmでありColdではない。
- 空画面は本文処理なし、ファイルありは同期接続・接続後2通知・各64スクロール/通知、合計640回がそろった。表示用の有界試作へ切り替えていない。
- 実コントロールでIsReadOnly=true、EnableTextDragDrop=false、Undo/Redo履歴なしを確認した。入力/貼付/Undo/ドロップを試みる操作試験や物理キー・IMEはまだ実施していない。
- ZIPのGitHub digestと全JSON/ログのSHA-256を再照合し、収集器で全20レポートを再検証した。正常/異常/境界8ケースは[両OSの通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42185900152)でも成功（macOS .NET689/Python65、Windows .NET686+既存Darwin専用3スキップ/Python45+既存POSIX専用20スキップ）。

## 時間と未達

| Main相対の代理指標（ms） | 中央値 | 最大 |
| --- | ---: | ---: |
| 空画面→Opened | 1,343.998 | 1,702.553 |
| 空画面→Opened後2通知 | 1,622.450 | 2,128.964 |
| ファイル指定→Opened | 1,334.166 | 1,445.663 |
| ファイル指定→本文接続後2通知 | 1,743.620 | 1,893.778 |
| 同期本文接続 | 30.824 | 116.335 |

Main→Openedは全20試行で1秒を超えた。これを起動目標達成とは扱わない。同期本文接続も初回ファイル試行の116.335 msが既存100 ms予算を超え、未達として残す。取得ワークフローの成功と、性能の合格を分ける。

640回の同期スクロール処理は中央値0.0363 ms/最大0.4678 ms、通知間隔は中央値25.747 ms/最大81.369 ms。物理描画/入力、フレーム提示や60fpsを測った値ではない。プロセス全体時間は空画面中央値約1.886秒/最大2.471秒、ファイル約3.662秒/最大3.900秒だが、後者は64回のスクロールと正常終了処理も含むため起動時間へ転用しない。

## 環境と原記録

Windows Server 2025 Datacenter 10.0.26100 / X64 / AMD EPYC 7763（CIM報告2 cores/4 logical processors）/可視メモリ約16 GiB、SDK 10.0.403/runtime 10.0.14。CIMの表示装置はMicrosoft Hyper-V Video、1024×768、CurrentRefreshRate=1。レポートのClientSizeは1028×749 DIP/scaling 1。物理60 Hz環境・基準PC・一般的なユーザー機とは採用しない。表示/電源/セキュリティ設定は変更していない。

`runs.json.gz`に全レポートとコマンド/プロセス終了、`phase-logs.json.gz`に全20アプリログ、`files-sha256.json`に原ファイルのハッシュ、`startup-fixture.cs.txt`に入力、`environment.txt`に環境、`ci-normal.json`に通常CIの集計/収集器8ケース、`summary.json`に元ZIP/ソース/バイナリの識別と集計を保存した。元ZIPは`artifacts/gui-ci-probe/windows/native-gui.zip`へ保持する。Actions artifactは14日保管する。

Windowsでの実ウィンドウ/通知取得が成立したため、次の読み取り専用操作や初期化の切り分けへ進める。通常配布物・macOS・物理描画/入力・実起動1秒・本文接続未達の是正とP1-07/P1-09/G1全体は未完了のまま維持する。
