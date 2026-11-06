# Windowsの診断収集確認

P0-04-03。[収集手順](../../diagnostic-measurements.md#windowsの取得確認)、[ワークフロー](../../../.github/workflows/diagnostics.yml)、[収集器](../../../tools/measure_diagnostics.py)。製品コード・API・依存を変更せず、既存CorePerformanceTestsの50 MiB Warm操作を使う。通常試験とは独立して、通常3プロセス・gc-verbose 3プロセス・gc-collect/counters各1プロセス、各10操作を順次実行する。既存予算は維持する。

## 手元での収集器確認

製品`68935b4`、macOS 27.0.1 / Apple M4 / RAM 32 GiB / SDK 10.0.401 / runtime 10.0.12。固定ツール10.0.750501、TraceEvent 3.1.23。生成seed 20261106の50 MiB UTF-8。`macos-runs.json.gz`に最終8プロセス/80操作と4トレースの解析値を保存した。すべて既存コア試験成功、入力ハッシュ不変、製品コミット一致、EventsLost 0、各20操作マーカー対応、GC停止の対応エラー0。CounterにもSystem.RuntimeのGC行が存在した。原記録のハッシュは`macos-files-sha256.json`、入力/ソース/圧縮記録の識別は`summary.json`に残す。原nettrace/CSV/ログはローカルの`artifacts/diagnostics/portable-verified`に保持する。

10操作の構築時間合計の3プロセス間中央値は通常4,022.801 ms / gc-verbose 4,075.682 ms、CPU合計中央値は4,652.843 / 4,710.098 ms。約1.31% / 1.23%の差だが、少数の非専用機観測であり、WindowsやGUIの負荷へ外挿しない。問合せ/プロセス起動等を含む全コマンド時間は各runへ別記した。

初回の手元実行はPython 3.9に`hashlib.file_digest`がなく、子プロセス起動前に失敗した。ストリーミングSHA-256へ置換し、空/複数ブロックの試験を追加して最終8実行をやり直した。収集器の正常/不正/境界8ケースと通常Python全55ケースが成功した。

## Windows CI

初回`b8975f3`の[実行](https://github.com/m-hiroaki/codeView/actions/runs/42179181006)は、収集前のPython試験でパスの期待値を`/`に固定していたため失敗した。実際のWindows Pathは`space dir\\host.dll`であり、コマンド生成の問題ではなかった。期待値もPathから構築する形へ修正し、ワークフローを再実行する。実収集成功はこの失敗からは判断しない。

実行結果を確認して追記する。ホスト型windows-2025は基準PCではない。メモリはWindows PrivateMemorySize64とmacOS phys_footprintを別指標として扱い、絶対量を同一視しない。原記録はActions artifact（14日保管）で取得する。CIでの収集成立と、専用機での性能受入・フレーム・Cold/物理I/O・LOH生成元は区別し、P0-04全体は未完了を維持する。
