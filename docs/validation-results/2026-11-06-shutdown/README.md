# 終了境界の障害注入・タブ解放確認（2026-11-06）

P1-08-01〜05の段階検証。P1-08全体・G1は未完了。対象ソースと試験ファイルのSHA-256、TRXのハッシュ/集計は[summary.json](summary.json)、通常タブ経路の補助測定は[tab-lifecycle.json](tab-lifecycle.json)へ保存した。計測ビルドの親コミットは66b4ce1で、今回の未コミット差分を含むため、対象の同一性はソースハッシュで確認する。

## 通常・障害・競合試験

`./test-mac.sh`でRelease通常.NET 654件・Python 25件成功。通常試験は性能カテゴリを除く。

- ShutdownTests：正常終了、Stop/Task取得の障害、処理自身のTimeoutException、無関係な取消、2秒の待機期限、期限後のfault観測。
- StartupSettingsIntegrationTests：通常設定/既定値/遅延成功/遅延失敗、期限前後の終了、停止後の未知例外/別トークン取消、状態通知自体の失敗。
- ShutdownIntegrationTests：正常・UI通知障害1件/3件。Stop後の別の後始末、元本文ReadLeaseの存続、文書所有の解放、停止後のOpen拒否、取消不能I/Oの実終了待ちと旧ルート復活の防止。
- ShutdownCancellationTests：5ケース。読込中+待機タブ、検索+既存本文、展開中+待機ツリーへ取消コールバック障害を注入。複数障害の保持、残りの所有者の解放、待機要求のI/O抑止、退役ツリーのPendingWorkと実終了を確認。

実I/Oは制御可能な読取専用ISourceAccessで待たせ、取消通知が例外を投げた後も明示的な解放までワーカーを保持する。テスト自体の5秒の上限はデッドロック検出用で、製品の2秒の待機期限とは区別する。

## 通常タブ経路の補助測定

macOS / ARM64、.NET 10.0.12。既存の50 MiB×5ファイルを使い、UTF-8文字数を独立に検証してWarmにした後、通常ViewModelのOpen・切替64回・再Open・全閉鎖を1回実行した。事前の固定予算を維持した既存TabLifecyclePerformanceTestsが成功。

| 項目 | 結果 |
| --- | ---: |
| 最大の本文読込 | 759.4961 ms |
| 最大のタブ切替 | 0.0665 ms |
| 全タブ閉鎖 | 0.7617 ms |
| 全処理CPU時間 | 2808.067 ms |
| 全処理割当 | 1,403,170,968 bytes |
| GC停止合計 | 417.865 ms |
| phys_footprint増分 | 872,235,800 bytes |
| 閉鎖・診断用GC後の追跡参照生存数 | 0 |

生成・独立した文字数検証・診断用GCは処理CPU/割当/GC停止の計測外。ネイティブのWindow/Editorは作成せず、実描画・物理入力・起動・検索速度は計測していない。1回の補助測定であり、変更前後の隔離比較やWindows/実機GUI終了の受入の代用にはしない。異常時のCPU/メモリの終了ピーク、全UI経路、残るRT-02/RT-05はP1-08/P1-09/P6に残す。

```sh
./test-mac.sh
CODEVIEW_PERF_TAB_ROOT="$PWD/artifacts/fixtures/tabs50" CODEVIEW_PERF_CACHE=warm CODEVIEW_PERF_LABEL=shutdown-cancellation-20261106 dotnet test tests/codeView.Tests/codeView.Tests.csproj -c Release --no-restore --no-build --filter FullyQualifiedName~TabLifecyclePerformanceTests --logger 'trx;LogFileName=shutdown-tab-lifecycle.trx' --results-directory artifacts/test-results/macos/shutdown
```
