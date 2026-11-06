# macOSネイティブ起動の取得確認

P1-09-02。`6e1204580b9cd8e6d021a0c3d8881da0daf7d269`の[Native GUI validation](https://github.com/m-hiroaki/codeView/actions/runs/42193098555)で、macOSの実ウィンドウによる空画面/日本語小ファイル各10回、計20独立プロセスを取得した。[手順と指標定義](../../native-gui-validation.md#macosの取得)。製品コード・依存・アーキテクチャは変更せず、既存の明示診断ビルドとUsePlatformDetectを使った。ヘッドレスや別レンダラーへ変更していない。

## 確認できたこと

- 全20プロセスでOpened、2通知、complete/Closed、終了0を照合した。ファイル10回は本文接続・各64スクロール/通知、合計640回がそろった。
- 同じUTF-8日本語/絵文字/CRLF入力は76 UTF-16単位/6行。独立した復号/改行数・SHA-256と各レポートが一致し、入力を変更していない。毎回ハッシュするWarm条件でありColdとはしない。
- 全レポートの製品コミット、通常全文表示候補、IsReadOnly=true、テキストDrop無効、Undo/Redoなし、正のclient領域/scalingと通知順序を再検証した。これは編集を試みる操作試験ではない。
- 原ZIPのSHA-256は`9ab47474846f49e9f36a3a20d2e7fed45c1941913de9372874d52a63ce28734b`で、GitHub artifact digestと一致。全JSON/ログのSHA-256も再照合した。
- 同じCIのWindows起動20回/読取専用216操作も成功。[通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42193098424)はmacOS .NET698/Python79、Windows .NET695+既存Darwin専用3スキップ/Python59+既存POSIX専用20スキップ。既存8ケースにMacの正常/空/ファイルとOS取り違え拒否を追加し、両OSで成功した。

## 代理指標と性能の限界

| macOSの代理指標 (ms) | 中央値 | 最大 |
| --- | ---: | ---: |
| 空画面 Main→Opened | 734.286 | 993.033 |
| 空画面 Main→Opened後2通知 | 869.659 | 1,429.359 |
| ファイル Main→Opened | 640.758 | 972.772 |
| ファイル Main→本文接続後2通知 | 790.218 | 1,143.437 |
| 同期本文接続 | 14.623 | 19.894 |

Main→Openedは20回すべて1秒以内だが、OSプロセス生成/ランタイム起動を含まない。空画面2通知は2回、本文接続後2通知は1回が1秒超であり、実起動・初回物理描画/操作可能の1秒目標を達成とはしない。同期本文接続は今回の小入力10回すべて100 ms以内だった。大容量・長行の未達を解決した結果ではない。

全試験処理CPUは空画面中央値868.765/最大935.419 ms、ファイル中央値1,146.334/最大1,305.139 ms。採取phys_footprintのピークは空画面中央値86,706,816/最大88,787,520 bytes、ファイル中央値105,360,032/最大105,728,704 bytes。CPUは64スクロール/終了も含み、ピークはサンプル方式である。前回の別OS/別機材との速度差を改善とは扱わない。

## 環境・保存物・残課題

ホスト型macOS 15.8.2 / 24H232、Apple M1 (Virtual)、Arm64、3 physical/logical CPU、7 GiB、SDK10.0.403/runtime10.0.14。system_profilerの表示デバイス情報は空だった。client領域と通知は実アプリから取得したが、物理60 Hz/Retina・一般のユーザー機/基準PCとは認めない。ログイン/ロック/表示/電源/セキュリティ設定は変更していない。

`runs.json.gz`に全20レポート/製品DLL・apphost識別、`phase-logs.json.gz`に全20ログ、`input-macos.cs.txt`に元入力、`environment.txt`に環境、`raw-sha256.json`に原全ファイルのハッシュを保存。`summary.json`/`ci.json`/`ci-*.log.gz`に集計・実行/ジョブ/artifact・原CIログを残した。`files-sha256.json`は保存物のハッシュ。原ZIPは`artifacts/macos-startup-ci/<artifact-id>.zip`、Actions artifactは14日保管する。

**macOSでもネイティブ起動取得が成立したため、後続のコントロール操作検証へ進める。** .app/AOT配布物、Macの編集試行/Clipboard/実キー/IME/Finder、物理描画・操作、基準PC・両OSの性能未達是正は残る。P1-07/P1-09/G1全体は未完了のままとする。
