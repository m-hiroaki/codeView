# 5タブの保持・状態切替・閉鎖

製品/測定プローブの固定コミット`3e58497`を再ビルドし、各JSONのProductInformationalVersionを照合した。macOS 27.0.1 arm64 / .NET runtime 10.0.12 / Release、別プロセスで逐次3回。通常単体試験267件と性能試験は別実行で、全件合格した。

入力はUTF-8の50 MiB本文のハードリンク別名5パス、論理250 MiB。同じ物理データのWarm読取であり、独立した5ファイルのCold I/O試験ではない。本文/別名のマニフェストを同梱した。測定前の独立した厳格UTF-8読取で各30,980,684 UTF-16単位を確認し、各タブのTextLengthも一致した。この事前処理はOSキャッシュと標準デコーダーも温めるため、初回起動/JITの評価へ転用しない。測定後も元データのSHA-256はマニフェストと一致した。

| 回 | 各本文の構築ms（最小〜最大） | CPU秒 | OSメモリ増分MiB | GC停止合計ms | 状態切替最大ms | 全閉鎖ms | 診断後の生存参照 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 344.6〜849.7 | 2.930 | 833.8 | 442.0 | 0.0763 | 0.765 | 0/10 |
| 2 | 322.3〜856.0 | 2.838 | 728.3 | 389.8 | 0.0797 | 0.714 | 0/10 |
| 3 | 321.6〜921.0 | 2.901 | 722.5 | 392.5 | 0.0737 | 0.732 | 0/10 |

性能評価条件13章の事前予算（各構築5秒・CPU25秒・OS増分1.5 GiB・GC合計750ms・状態切替100ms/回）内。64回の切替割当は各193,112 bytesで1 MiB以内だった。この割当にはテストの同一文書確認も含む。再Open後も製品の本文OpenReadは合計5回で、既存タブの具体文書を再利用した。Gen0/1/2は各173/64〜65/6回。メモリの約111 MiBの測定幅を単発の改善・悪化と断定しない。

全タブ閉鎖後、モデル自体を生存させたままテスト限定のGCを行い、5ハンドル/5具体文書への弱参照が全て消えることを確認した。診断後のManagedHeapは約1.67〜1.78 MiB。診断GCは通常処理のCPU/割当/GC指標から除外している。OSメモリサンプルは保持・閉鎖・診断GCを含む区間で10msごとに採り、真の瞬間ピークやOS予約領域の即時返却を保証しない。

ネイティブWindow/Editor、バインディング先の描画・レイアウト・入力、文法/検索、クリップボードは起動していない。状態切替の時間は画面の切替時間ではなく、弱参照の結果も実GUIの購読・ネイティブ資源や長時間リークの解放を証明しない。G2/P3-07/G3は未完了で、Windows・Cold・実機・長時間の結果は別途必要である。

再現する場合、生成済み`text50`本文から別名を用意できる（出力先が未作成の場合のみ）。

```sh
mkdir artifacts/fixtures/tabs50
for number in 1 2 3 4 5; do
  ln artifacts/fixtures/text50/group-00000/source-0000000.cs "artifacts/fixtures/tabs50/tab-$number.cs"
done
CODEVIEW_PERF_TAB_ROOT="$PWD/artifacts/fixtures/tabs50" \
CODEVIEW_PERF_CACHE=warm CODEVIEW_PERF_LABEL=tab-lifecycle-reproduce \
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~TabLifecyclePerformanceTests
```

独立した入力は生成器の`--files 5 --bytes 262144000 --layout wide`で別ルートに作れるが、今回のハードリンク別名の条件とは分けて評価する。TRXは`tests/codeView.Tests/TestResults/tab-lifecycle-verified-{1,2,3}.trx`、通常回帰は`tab-lifecycle-regression.trx`。保存JSONは`verified`の3回を確定値とし、独立文字数検査追加前の`f9843cf`/予備値は集計へ混ぜていない。
