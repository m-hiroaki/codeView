# 逐次通常照合のReadLease測定

固定コミット`e1a5316`を再ビルドし、JSONのProductInformationalVersionを照合した。macOS 27.0.1 arm64 / .NET runtime 10.0.12 / Release、生成済み各50 MiBのUTF-8/CP932/UTF-8単一長行を各1回。通常単体試験309件と性能試験3プロセスは別実行で全件合格した。

測定前に既存FileReaderで文書を構築し、不変ReadLeaseを取得してライブ文書ハンドルを解放した。測定対象は元の短いチャンクをLiteralMatcherで全走査する部分。ファイル読取/デコード/文書構築、検索セッション/結果一覧、UIを含まない。元の文字コードによってUTF-16単位数が異なるため、時間差をデコーダー性能の差と解釈しない。

| 入力 | UTF-16単位数 | marker ms | 単語marker ms | 未一致ms | 最大16 Ki語ms | 4走査のCPU秒 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| UTF-8通常改行 | 30,980,684 | 247.7 | 63.1 | 63.0 | 688.2 | 1.201 |
| CP932通常改行 | 38,947,124 | 264.6 | 76.3 | 76.4 | 860.5 | 1.421 |
| UTF-8単一長行 | 39,945,760 | 330.3 | 77.6 | 77.2 | 880.2 | 1.504 |

各プロセスはmarker→単語marker→未一致→最大語の順で実行した。最初のmarkerには照合部分の初回JIT等も含み、単語検索が通常検索より速いという比較にはしない。最大語は`q`の16,384回繰返しで、生成データでは未一致である。

- 各走査1秒・先頭一致50ms・割当256 KiBの事前予算内。先頭一致は0.082〜0.951ms、offset3・1行4列で、marker/単語markerとも1件だった。
- 全走査の配信文字数は各ReadLease長と一致した。最大語でもバッファ容量は32,772 UTF-16単位、通常語は16,402〜16,409単位で、行長に比例して増えない。
- 割当は各66,712〜99,448 bytes（約65〜97 KiB）。最初の予備測定では測定側のデリゲート再生成により約7.8 MBを割り当てたが、1個を再利用する形に直した。製品の予算を緩めていない。
- Gen0/1/2は11走査で0/0/0、単一長行の最大語で1/1/0・GC停止2ms。他の停止は0。LastGcLohBytesの98,408 bytesは最後のGC時点の領域サイズであり、照合のLOH割当量ではない。個別停止とLOH割当のトレースは未取得。

長行の照合コアが有界であることを確認した結果で、未達の長行GUIレイアウトが直ったとは扱わない。ファイル内方式の比較・有界結果イベント・抜粋・更新時の無効化・L1全体のCold/Warmと実機UIは未検証。P4-01/P4-02/G4およびG2/G3の全完了条件を満たしたとはしない。

実行例：

```sh
CODEVIEW_PERF_LITERAL_ROOT="$PWD/artifacts/fixtures/text50" \
CODEVIEW_PERF_CACHE=warm CODEVIEW_PERF_LABEL=literal-utf8-reproduce \
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~LiteralMatcherPerformanceTests
```

CP932/長行は入力を`cp93250`/`long50`へ変更する。TRXは`tests/codeView.Tests/TestResults/literal-{utf8,cp932,longline}-final.trx`。元入力の生成マニフェストと各JSONを同梱し、予備値は確定値へ混ぜていない。
