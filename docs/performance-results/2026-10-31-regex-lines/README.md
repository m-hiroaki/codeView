# 行単位Regexのコア測定

固定コミット`f6d2ce4`を再ビルドし、保存JSONのProductInformationalVersionを照合した。macOS 27.0.1 arm64 / .NET runtime 10.0.12 / Release。通常単体試験339件、Regex性能3プロセス、通常照合の回帰1プロセスは別実行で全件合格した。

各50 MiBの生成済みUTF-8/CP932/UTF-8単一長行から構築したReadLeaseを走査する。文書構築/デコード/ファイルI/Oは測定外で、元入力の文字コードによる時間差をデコーダー性能と解釈しない。ネイティブUI、検索ワーカー全体、イベント/抜粋/結果一覧は含まない。

| 元入力 | 評価した行数/走査 | marker ms | 後読みmarker ms | 単語marker ms | 未一致ms | 4走査のCPU秒 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| UTF-8通常改行 | 1,191,565 | 316.9 | 117.6 | 119.4 | 118.6 | 0.708 |
| CP932通常改行 | 1,497,967 | 355.1 | 151.2 | 151.3 | 153.5 | 0.971 |
| UTF-8単一長行 | 0（未評価1行） | 337.3 | 90.2 | 90.0 | 90.0 | 0.727 |

各プロセスの順序はmarker→後読み→単語→未一致。最初のmarkerにはRegex照合部分の初回JIT等も含むため、後読み/単語条件の方が速いとの比較にはしない。後読み式は`(?<=// )CODEVIEW_MATCH`。通常改行ではmarker系はoffset3・1行4列に各1件、未一致は0件だった。最終空行も評価行数に含む。

- 各2秒・先頭一致50ms・割当3 MiBの事前予算内。通常改行の先頭一致は約0.080〜1.195msだった。
- 割当は各2,134,632〜2,137,584 bytes（約2.04 MiB）。1 Mi UTF-16単位の配列を1個持ち、約119〜150万行でも行ごとの文字列/Match一覧を作らない。今回の12走査のGen0/1/2は全て0/0/0、GC停止0。
- 単一長行は39,945,760 UTF-16単位を読み流し、4条件とも一致0・未評価行1・評価行0。接頭辞内のmarkerだけを検索成功として返していない。通常改行は未評価行0、全条件で非スカラー候補0だった。
- タイムアウト、上限±1、非スカラー/0文字、利用側例外、Reset/Abortは単体試験で別に確認した。任意の式の時間/メモリや個別GC/LOH割当のトレースを保証する結果ではない。

共有Rune境界を使う通常照合もUTF-8 50 MiBで回帰測定した。marker約250.5ms・単語約60.4ms・未一致約60.2ms・最大16 Ki語約681.6ms、各割当約65〜97 KiBで既存予算内だった。条件順と範囲は前回の通常照合コアと同じで、GUI検索の性能へ転用しない。

実行例：

```sh
CODEVIEW_PERF_REGEX_ROOT="$PWD/artifacts/fixtures/text50" \
CODEVIEW_PERF_CACHE=warm CODEVIEW_PERF_LABEL=regex-utf8-reproduce \
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~RegexLineMatcherPerformanceTests
```

入力を`cp93250`/`long50`へ変更して別条件を測る。TRXは`tests/codeView.Tests/TestResults/regex-{utf8,cp932,longline}-final.trx`、通常照合回帰は`literal-regex-regression.trx`。入力マニフェストと確定JSONを同梱する。検索ワーカー、制約のUI伝達、本文内方式比較、L1 Cold/Warm、別OS/実GUIは未完了であり、P4-03/G4や長行表示のG2を完了にしない。
