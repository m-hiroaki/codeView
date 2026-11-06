# Windowsの診断収集確認

P0-04-03。[収集手順](../../diagnostic-measurements.md#windowsの取得確認)、[ワークフロー](../../../.github/workflows/diagnostics.yml)、[収集器](../../../tools/measure_diagnostics.py)。製品コード・API・依存を変更せず、既存CorePerformanceTestsの50 MiB Warm操作を使う。通常試験とは独立して、通常3プロセス・gc-verbose 3プロセス・gc-collect/counters各1プロセス、各10操作を順次実行する。既存予算は維持する。

## 手元での収集器確認

製品`0afb2f3`、macOS 27.0.1 / Apple M4 / RAM 32 GiB / SDK 10.0.401 / runtime 10.0.12。固定ツール10.0.750501、TraceEvent 3.1.23。生成seed 20261106の50 MiB UTF-8。`macos-runs.json.gz`に最終収集器の8プロセス/80操作と4トレースの解析値を保存した。すべて既存コア試験成功、入力ハッシュ不変、製品コミット一致、EventsLost 0、各20操作マーカー対応、GC停止の対応エラー0。CounterにもSystem.RuntimeのGC行が存在した。原記録のハッシュは`macos-files-sha256.json`、入力/ソース/圧縮記録の識別は`summary.json`に残す。原nettrace/CSV/ログはローカルの`artifacts/diagnostics/portable-host-record`に保持する。

10操作の構築時間合計の3プロセス間中央値は通常4,044.164 ms / gc-verbose 4,101.161 ms、CPU合計中央値は4,672.903 / 4,746.181 ms。約1.41% / 1.57%の差だが、少数の非専用機観測であり、WindowsやGUIの負荷へ外挿しない。問合せ/プロセス起動等を含む全コマンド時間は各runへ別記した。

初回の手元実行はPython 3.9に`hashlib.file_digest`がなく、子プロセス起動前に失敗した。ストリーミングSHA-256へ置換し、空/複数ブロックの試験を追加した。Windowsのコア失敗保持を追加した最終版でも8実行をやり直した。収集器は正常/不正/境界10ケースで検証し、コア失敗後の全モード収集・証跡保存・最終非0終了を模擬プロセスでも確認する。

## Windows CI

`55a4754`の[2回目](https://github.com/m-hiroaki/codeView/actions/runs/42179404164)では収集器8試験を通過し、最初の通常プロセスで10操作すべてがGC停止150 msの既存上限を超えた（170.151–223.341 ms）。`windows-first-core-failure.json.gz`と同名.logへ結果/例外を保存した。取得ZIPのSHA-256は`0ac66e50851b00324e45a94d266bc28fc3ffcdbcaf1c7c4374d6761750c005e6`。この時点の収集器は最初のコア試験失敗で停止したため、トレース/Counterは未取得だった。

原因調査のトレースも残せるよう、ホストの既知のassertion終了コード1と全レポートを確認した上で後続の収集を継続し、全収集後に非0終了する形へ修正した。BudgetFailedマーカーの対応と重複/区間外拒否も試験する。予算/製品は変更せず、取得の成立とコア合否を分けて記録する。

固定版ツールの終了コード伝達も手元で対照確認した。1.5秒後に1を返す最小ホスト（`ExitHost.cs.txt`）に対し、dotnet-traceは1、dotnet-countersは0だった（`exit-one.json`）。Counter側のコア失敗を見落とさないよう、試験専用ホストを`tests/Diagnostics/CoreHost.cs.txt`へ置き、全操作後の完了記録を必須にした。ツールの終了値とホストのassertion合否を別々に保持し、Counterが0でもホスト失敗なら最終非0となることを模擬試験で確認した。ホスト記録付きの最終版をmacOSで再度80操作実行して合格し、Python全57ケースも成功した。

初回`b8975f3`の[実行](https://github.com/m-hiroaki/codeView/actions/runs/42179181006)は、収集前のPython試験でパスの期待値を`/`に固定していたため失敗した。実際のWindows Pathは`space dir\\host.dll`であり、コマンド生成の問題ではなかった。期待値もPathから構築する形へ修正し、ワークフローを再実行する。実収集成功はこの失敗からは判断しない。

## 最終結果（f6eae8f）

[収集ワークフロー](https://github.com/m-hiroaki/codeView/actions/runs/42180488290)で8プロセス/80操作、4トレース、Counter 232行を取得し、`acquisitionValidated=true`を確認した。4トレースすべてEventsLost 0・GC対応エラー0。各20開始/終了マーカーと10 BudgetFailedマーカーの計30イベントが対応し、ホスト完了記録の失敗数とも一致した。入力SHAはmacOSと一致し、各実行後も不変。ZIPのGitHub digestと全収集ファイルのSHA-256も照合した。

通常試験の[両OS CI](https://github.com/m-hiroaki/codeView/actions/runs/42180488223)は成功（macOS .NET677/Python57、Windows .NET674+既存3スキップ/Python37+既存20スキップ）。収集器10ケースは両OSすべて成功した（`ci-normal.json`）。製品コード/予算は変更していない。

収集先はWindows Server 2025 Datacenter 10.0.26100 / X64 / AMD EPYC 7763（CIM報告2 cores・4 logical processors）/可視メモリ約16 GiB、SDK 10.0.403/runtime 10.0.14。専用基準機ではない。ストレージ・表示条件・他負荷・オフライン受入は未確定。メモリ指標はPrivateMemorySize64で、macOS phys_footprintの絶対量と同一視しない。

| 指標（各10操作の合計の3プロセス間中央値） | 通常 | gc-verbose | 差 |
| --- | ---: | ---: | ---: |
| 構築時間ms | 8,551.958 | 8,594.044 | +0.49% |
| CPU ms | 10,843.750 | 11,078.125 | +2.16% |

少数のホスト型CI観測であり、性能改善/一般的な計測負荷や他機材の合否へ外挿しない。**80操作すべてGC停止合計150 msの既存予算に違反**し、観測範囲は155.609–212.933 msだった。構築/メモリ/ReadLeaseの既存上限は取得JSONで満たしたが、`coreAssertionsPassed=false`を維持し、全収集後もワークフローは終了コード1（failure）とした。Counterツールは0終了でもホスト記録は失敗1であり、誤った成功判定を防げた。150 msは1操作の累積値で、トレース全区間の単独GC停止最大は各約17.999/17.832/20.038/17.408 msだった。単独停止と累積値、トレース全区間と操作内を混同しない。

`windows-runs.json.gz`に全操作/完了記録/解析、`windows-files-sha256.json`に原記録のハッシュ、`windows-counters.csv`、`windows-environment.txt`、`windows-tools.txt`、`windows-input.manifest.json`を保存した。`summary.json`に取得ZIPのハッシュ/ID/限界を保存。原nettraceと各ログを含むZIPは`artifacts/diagnostics/windows-ci/host-record.zip`に保持し、Actions artifactでも14日保管する。

P0-04-03は取得手順と負荷比較の確認として完了する。GC予算未達の原因/専用機での再評価はP2-01/P2-07・必要時A-01へ残す。フレーム・Cold/ファイル別物理I/O・LOH生成元・基準機と実UIの性能受入は未完了で、P0-04全体やG0/G1/G2は完了にしない。
