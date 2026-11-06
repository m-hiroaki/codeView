# 計測記録とEventPipeの確認手順

P0-04用。[記録テンプレート](measurement-record.template.json)へ、ビルド/依存版、入力/ハッシュ/seed/キャッシュ、設定、機材、計測前に固定した予算、ツール/対象範囲、結果/未実施理由を残す。値を取得できなかった項目はnullとnot-measured、その理由を記録する。0で代用したり、部分的な結果からPhaseの合格を判断しない。

今回の[macOS確認結果](validation-results/2026-11-06-eventpipe/README.md)は、既存のCorePerformanceTestsを専用ホストから呼ぶ。通常の製品・試験プロジェクト、SDK/依存の固定、カバレッジ用ツール設定は変更しない。新たに保持するプロジェクトや製品の公開APIも追加しない。.NET 10の[ファイルベースアプリ](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)をartifactsへビルドする。

## 準備

- リポジトリのglobal.jsonに従う.NET SDK。
- Python 3（検証用の順次実行スクリプト）。
- [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)・[dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters) 10.0.750501。
- 生成済み50 MiBのUTF-8本文。今回の入力はP0-06、seed 20261106。

以下はmacOSで実行確認した手順。Windowsの診断ツール・GUI・カーネルI/Oとの接続は別途確認が必要。コマンドはリポジトリのルートで実行する。ツールはartifacts配下だけへ導入し、グローバルツールや通常テストのmanifestへ追加しない。

```sh
mkdir -p artifacts/diagnostics
dotnet tool install dotnet-trace --tool-path artifacts/diagnostics-tools --version 10.0.750501
dotnet tool install dotnet-counters --tool-path artifacts/diagnostics-tools --version 10.0.750501
cp docs/validation-results/2026-11-06-eventpipe/CoreHost.cs.txt artifacts/diagnostics/CoreHost.cs
cp docs/validation-results/2026-11-06-eventpipe/TraceSummary.cs.txt artifacts/diagnostics/TraceSummary.cs
cp docs/validation-results/2026-11-06-eventpipe/run.py.txt artifacts/diagnostics/run.py
dotnet build artifacts/diagnostics/CoreHost.cs -c Release --output artifacts/diagnostics/host
dotnet build artifacts/diagnostics/TraceSummary.cs -c Release --output artifacts/diagnostics/reader
```

既に導入済みのツールはversionを確認して再利用する。一時ホストは既存の公開コア試験を呼ぶため、dotnet testの親CLIをトレースしてtesthostを測ったと誤認することを避けられる。トレース対象は子として起動したCoreHostだけである。ネイティブWindow/Editor、物理入力、文法接続は起動しない。

入力が未生成の場合だけ、次で新規生成する。既存入力がある場合はmanifestとハッシュを確認して再利用する。

```sh
python3 tools/generate_fixture.py /private/tmp/codeview-perf-v2-20261106/text50-utf-8 --profile text50 --seed 20261106
python3 tools/generate_fixture.py /private/tmp/codeview-perf-v2-20261106/text50-utf-8 --verify
```

## 収集

run.py.txtは今回の入力パス/出力先を記録した再現用ソースである。別条件を使う場合はinput_file/outとラベルを変えて既存結果を保管する。既存のcomparisonディレクトリを上書きしない。ビルドを完了してから、ほかの性能試験と並走させず実行する。

```sh
python3 artifacts/diagnostics/run.py
```

通常3回、gc-verbose 3回、gc-collect/counters各1回の計8プロセスで、それぞれ10操作を行う。各プロセスは測定前に入力全バイトを読み、Warmを固定する。元のコア試験の予算判定を維持し、違反時はログ/マーカーへ残して終了コードを非0にする。同じ操作回数を観測するため予算違反後も次の操作へ進むが、成功へ変換しない。

単独の収集は以下の形になる。

```sh
CODEVIEW_PERF_FILE=/private/tmp/codeview-perf-v2-20261106/text50-utf-8/group-00000/source-0000000.cs artifacts/diagnostics-tools/dotnet-trace collect --profile gc-verbose --buffersize 32 --providers codeView-Diagnostics:0xFFFFFFFFFFFFFFFF:4 --show-child-io --output artifacts/diagnostics/new-run.nettrace -- dotnet artifacts/diagnostics/host/CoreHost.dll new-run 10
dotnet artifacts/diagnostics/reader/TraceSummary.dll artifacts/diagnostics/new-run.nettrace artifacts/diagnostics/new-summary.json
```

## 結果の解釈

| 指標 | 観測と制限 |
| --- | --- |
| CPU | コアJSONのProcess.TotalProcessorTime差分と、System.Runtimeのdotnet.process.cpu.time。後者のCSVは秒/秒のRateで、CPU%ではない |
| OSメモリ | コアJSONのmacOS.phys_footprintを10msで標本化。Counterのworking_setと同じ指標とは扱わず、真の瞬間ピークとも断定しない |
| 管理メモリ/割当 | コアJSONのGC.GetTotalMemory/GetTotalAllocatedBytesと、Counterの最終GCヒープ/断片化/割当Rate。ネイティブヒープの内訳は未測定 |
| GC回数 | コアJSONのGC.CollectionCountは上位世代の回収を含む。traceのDepthやSystem.Runtimeのgenerationタグは最大回収世代別。計測区間も異なるため単純に同じ回数として比較しない |
| 個別停止 | SuspendForGC/SuspendForGCPrepのSuspendEEStart→RestartEEStopを同一トレース時刻で対応付ける。SuspendOtherはGC停止集計から外す |
| LOH | HeapStats/Counterの最終GC後の保持・断片化と、gc-verboseのAllocationTickサンプル。全割当量/件数、生成元スタックの確定には使わない |
| 操作範囲 | Round/Start・Round/Stopマーカーで試験呼出しを識別。JSONの読込/構築の計測窓より広く、assert/レポート処理も含む |
| 欠落 | EventsLost、開始/終了の重複/欠落、10操作の20マーカーを確認。Dropバッファで欠落0だった事実と、割当のサンプリングは区別する |

[Microsoftのランタイム指標の定義](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)と、取得に使った版を記録する。今回の解析器は公式TraceEvent 3.1.23である。生のnettraceをSpeedscope等へ変換しただけで、GCの全情報を保持したとは扱わない。

元のnettrace、Counter CSV、各コアJSON、解析結果、コマンド・ハッシュを保存する。Counter CSVの時刻は今回JSTで、トレース内の相対ミリ秒とは別の軸である。GUIとの相関はUI側の時刻/イベント接続まで確認してから判断する。

計測負荷は計測なしと同じ入力・同じ回数の別プロセスで比較する。コアのCPU/時間/GC/割当/footprintと、外部ツールの起動終了を含むコマンド全体時間を分ける。今回の比較から、別機材・別処理・WindowsやGUIの負荷を推定しない。フレーム、物理I/O、Windowsの診断接続、LOH生成元の解析が残るため、P0-04全体は未完了を維持する。
