# ツリー論理構築・行投影

製品コミット`25eb63f`を再ビルドし、JSONのProductInformationalVersionを照合した。macOS 27.0.1 arm64 / .NET runtime 10.0.12 / Release、生成済みUTF-8 L1・Warm条件、各1回。各マニフェストの対象は5万ファイル/1 GiB。本文と全階層は読まず、ルートと最初の通常子フォルダだけを列挙する。

| 条件 | ルート直下行 | Open ms | 行投影 ms | 子500行の展開 ms | CPU ms | 割当 bytes | 観測メモリ増分MiB | GC停止ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 入れ子 | 100 | 4.00 | 1.06 | 3.04 | 10.84 | 475,216 | 0.86 | 0 |
| 直下大量 | 10,080 | 37.31 | 3.64 | 2.87 | 47.14 | 8,129,008 | 4.81 | 2.92 |

性能評価条件12章の事前予算（Open+投影1秒、子展開1秒、CPU1秒、観測増分64 MiB）内。範囲追加は最大256行で、yieldは入れ子1回/直下大量40回。直下大量でGen0が1回、Gen1/2は0回。子の折りたたみ後にルート行数が保たれ、閉鎖後の表示行が0になることも測定試験内で確認した。

表示行への投影はUI相当の直列テストコンテキスト上で行い、制御を譲る箇所ではTask.Yieldを使用した。実製品のDispatcher、ListBoxコンテナ、バインディング通知の描画処理、選択スクロール、OS入力、起動は計測していない。メモリは処理境界とyield時の観測であり、連続10msサンプルのピークではない。観測値だけで実GUIのメモリ上限や仮想化を合格にしない。Cold・Windows・L2/L3・検索性能にも転用しない。

実行例（リポジトリのルート）：

```sh
CODEVIEW_PERF_DIRECTORY="$PWD/artifacts/fixtures/l1-wide" \
CODEVIEW_PERF_CACHE=warm CODEVIEW_PERF_LABEL=tree-l1-wide \
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~FileTreePerformanceTests
```

入れ子は入力を`l1`、ラベルを`tree-l1-nested`へ変更。TRXは`tests/codeView.Tests/TestResults/tree-l1-wide.trx`と`tree-l1-nested.trx`、元JSONはテスト出力の`TestResults`配下。通常単体試験は別実行で227件合格している。
