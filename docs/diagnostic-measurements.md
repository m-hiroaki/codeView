# 計測記録とEventPipeの確認手順

P0-04用。[記録テンプレート](measurement-record.template.json)へ、ビルド/依存版、入力/ハッシュ/seed/キャッシュ、設定、機材、計測前に固定した予算、ツール/対象範囲、結果/未実施理由を残す。値を取得できなかった項目はnullとnot-measured、その理由を記録する。0で代用したり、部分的な結果からPhaseの合格を判断しない。

今回の[macOS確認結果](validation-results/2026-11-06-eventpipe/README.md)は、既存のCorePerformanceTestsを専用ホストから呼ぶ。通常の製品・試験プロジェクト、SDK/依存の固定、カバレッジ用ツール設定は変更しない。新たに保持するプロジェクトや製品の公開APIも追加しない。.NET 10の[ファイルベースアプリ](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)をartifactsへビルドする。

## 準備

- リポジトリのglobal.jsonに従う.NET SDK。
- Python 3（検証用の順次実行スクリプト）。
- [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)・[dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters) 10.0.750501。
- 生成済み50 MiBのUTF-8本文。今回の入力はP0-06、seed 20261106。

以下はmacOSで実行確認した手順。Windowsの収集確認は後述の専用ワークフローで行い、GUI・カーネルI/Oとの接続は別途確認する。コマンドはリポジトリのルートで実行する。ツールはartifacts配下だけへ導入し、グローバルツールや通常テストのmanifestへ追加しない。

```sh
mkdir -p artifacts/diagnostics
dotnet tool install dotnet-trace --tool-path artifacts/diagnostics-tools --version 10.0.750501
dotnet tool install dotnet-counters --tool-path artifacts/diagnostics-tools --version 10.0.750501
cp tests/Diagnostics/CoreHost.cs.txt artifacts/diagnostics/CoreHost.cs
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

計測負荷は計測なしと同じ入力・同じ回数の別プロセスで比較する。コアのCPU/時間/GC/割当/footprintと、外部ツールの起動終了を含むコマンド全体時間を分ける。今回のmacOS比較から、別機材・別処理・WindowsやGUIの負荷を推定しない。フレーム、ファイル別物理I/O、専用基準機での診断/負荷確認、LOH生成元の解析が残るため、P0-04全体は未完了を維持する。

## Windowsの取得確認

[Diagnostic acquisition](../.github/workflows/diagnostics.yml)は通常試験から独立した明示収集用ワークフローである。ActionsのRun workflow、または収集器/その試験/ワークフロー変更のmainへのpushで実行する。`contents: read`だけで、ホスト型windows-2025上にseed 20261106の50 MiB UTF-8を生成・検証し、同じ一時ホストと解析器を使用する。SDKはglobal.json、ツールは10.0.750501、TraceEventは3.1.23である。製品/試験のプロジェクトや依存を追加せず、製品公開APIも変更しない。

収集器`tools/measure_diagnostics.py`は、通常3回・gc-verbose 3回・gc-collect/counters各1回、各10操作を順次実行する。両OSの実行ファイル名、空白を含むパス、UTF-8 BOM付きJSON/CSVを扱い、既存結果の上書きを拒否する。入力の50 MiB、操作数、製品コミット、Warm条件、入力ハッシュ不変、ツール/ホスト終了コード、トレースの欠落/GC対応/20開始終了マーカー、CounterのGC行を検証する。

ホストは全10レポートの後に、ラベル・操作数・assertion失敗数を`*-host.json`へ記録する。失敗時は終了コード1を返す。dotnet-countersは子の失敗でもツール自体が0終了する場合があるため、ツール終了コードだけでコア成功と判断しない。完了記録が揃うassertion失敗は、原因調査のため残りの収集を継続する。追加のBudgetFailedマーカーを該当操作の開始/終了内に対応付け、ホスト記録の失敗数と照合する。取得の成立`acquisitionValidated`とコア試験の合否`coreAssertionsPassed`を分け、1件でもコア失敗があれば全収集後のスクリプトも非0終了する。既存予算を緩めたり合格へ変換したりしない。レポート欠落・予期しない終了・トレースの不整合は直ちに失敗させる。途中までのログとJSONも保存する。

手元でホスト/ツールの準備後に実行する例（commitは対象ビルドの完全なSHAに置換する）：

```sh
python3 tools/measure_diagnostics.py --input /private/tmp/codeview-perf-v2-20261106/text50-utf-8/group-00000/source-0000000.cs --host artifacts/diagnostics/host/CoreHost.dll --reader artifacts/diagnostics/reader/TraceSummary.dll --tools artifacts/diagnostics-tools --output artifacts/diagnostics/new-acquisition --commit 68935b452ede504cb39924eb13c17a9f597f2dc7
```

Windows CIではPowerShellから同じ引数を渡し、`--commit`に`$env:GITHUB_SHA`を使用する。取得ZIPは14日保管する。必要な環境・manifest・全操作JSON・CSV・解析JSON・原nettrace・各ログ・SHA-256は期限前に別途保存する。Windowsのメモリ指標はPrivateMemorySize64、macOSはphys_footprintなので、異なる指標を同じ絶対値として比較しない。収集成功はホスト型CIでの取得可否の確認であり、専用基準機・Cold・表示/入力・実機性能の合格ではない。

[Windowsの確認結果](validation-results/2026-11-06-windows-diagnostics/README.md)では取得が成立した一方、既存GC予算に違反してワークフローが非0終了した。結果の取得可否と性能の合否を分けて読む。

中間コピー削減の比較では、[比較時点のワークフロー](https://github.com/m-hiroaki/codeView/blob/3cc8228e8616b62707f78a311b35be6fb06f1474/.github/workflows/diagnostics.yml)で同一ランナーに変更前`f98f394`と候補`3cc8228`を別々にビルドし、同じ生成入力で各80操作を順次収集した。基準版のコア失敗を保存して候補へ進み、候補も失敗なら全体を非0終了した。候補は不採用のため通常コード/単独収集手順へ戻し、比較時点のGit/差分/原記録を保存する。[不採用の根拠](validation-results/2026-11-06-document-copy/README.md)。
