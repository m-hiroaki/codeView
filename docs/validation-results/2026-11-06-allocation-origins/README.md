# 本文構築の割当生成元の切り分け

A-01-01。製品コードを変更せず、[既存の両OS診断](../2026-11-06-windows-diagnostics/README.md)のgc-verbose各3件をオフライン解析した。対象は50 MiB UTF-8・Warm、各トレース10操作。Windows製品`f6eae8f`、macOS製品`0afb2f3`。入力内容は同一だが機材・runtime・メモリ指標は異なるため、両OSの絶対性能を同じ基準で比較しない。

## 解析と検証

TraceEvent 3.1.23のETLXへ変換し、採取済みランダウンに含まれる管理メソッド名とAllocationTickのスタックを対応付けた。ネットワークからのシンボル取得を無効化し、未解決アドレスは未解決のまま保持した。[公式のTraceLog/スタック解説](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md)と、固定版パッケージのXML API説明に従う。

全6件でイベント欠落・変換打切り・操作マーカー対応エラー0。元のEventPipe直接解析と、**全型のAllocationTick件数・AllocationAmount64重み合計・ObjectSize合計が一致**した。操作内/外はRound/StartとRound/Stopで分け、WindowsのBudgetFailedも操作内に対応した。CLIは引数なし/不存在入力/既存出力で2、不正トレースで非0となり、偽の解析結果JSONを生成しないことを実確認した。

| OS | 各トレースの割当サンプル数 | スタックなし | 深さ128での打切り |
| --- | --- | --- | --- |
| macOS | 24,229 / 24,228 / 24,214 | 1 / 1 / 0 | 0 / 0 / 0 |
| Windows | 24,308 / 24,269 / 24,299 | 0 / 0 / 0 | 0 / 0 / 0 |

各変換ログに、モジュール/メソッドへ解決できないアドレス1件も残る。欠落なしを全スタックの完全な解決と混同しない。

## 観測された生成元と次の対策

操作内の主要なサンプルは両OSとも次の経路だった。

| 型 | 管理スタックで確認した経路 | 意味 |
| --- | --- | --- |
| String | DocumentBuilder.Append → ReadOnlyMemory<char>.ToString | 借用チャンクの一時文字列化 |
| Char[] | DocumentBuilder.Append → TextDocument.DoReplace、およびCharRope.InsertText → String.ToCharArray | ライブラリの文字列挿入経路にも配列割当がある |
| DocumentLine | TextDocument.DoReplace → LineManager.Insert/InsertLineAfter | 通常改行の行管理 |
| ImmutableStack<RopeCacheEntry<char>> | LineManager.Insert → Rope.FindNodeUsingCache | 行挿入時のRopeアクセス |

例えばWindows第1トレースでは、先頭のChar[]グループ6,711サンプル、Append由来のStringグループ6,685サンプル、LineManager.Insert由来のDocumentLineグループ5,937サンプルが対応した。これは型ごとの全割当数/量ではない。AllocationAmount64はサンプリングの重み、ObjectSizeは観測された個別オブジェクトのサイズであり、重み合計をその型の正確な総バイト数として扱わない。

採取されたLOHサンプルは各トレース1件、6件すべて操作開始前の`Stream.CopyToAsync → SharedArrayPool<byte>.Rent → AllocateUninitializedArray`だった。計測ホストのWarm読取で生成されたByte[]であり、本文構築内のLOH割当とは分類しない。サンプリングであるため、本文内に他のLOH割当がなかったことの証明にはならない。

次は、同じTextDocument/所有/ReadLeaseを維持したまま、既存のRope配列コンストラクターとRopeTextSource挿入経路で一時文字列/配列のコピーを減らせるか比較する。借用配列の保持・再利用後の原文破壊、CRLF/サロゲート境界、Undo履歴、取消/未採用文書の解放を先に検証し、既存の入力/予算で割当・GC・CPU/メモリを比較する。行管理の保持量や長行描画の問題は別であり、GC予算未達の解消を現時点で主張しない。

## 再現と保存物

```sh
mkdir -p artifacts/allocation-reader
cp docs/validation-results/2026-11-06-allocation-origins/AllocationStacks.cs.txt artifacts/allocation-reader/AllocationStacks.cs
dotnet build artifacts/allocation-reader/AllocationStacks.cs -c Release --output artifacts/allocation-reader/bin
dotnet artifacts/allocation-reader/bin/AllocationStacks.dll artifacts/allocation-analysis-20261106/windows-acquisition-gc-verbose-1.nettrace artifacts/allocation-reader/new-result
```

入力は原nettrace、出力ディレクトリは未使用のパスを指定する。原記録の所在/ZIPハッシュは上記の両OS診断記録に従う。Windowsの原nettraceは検証済みZIPから抽出した。

`AllocationStacks.cs.txt`に解析器、`summary.json`に原トレース/解析器/圧縮結果のSHA-256と件数、各`*.json.gz`に型・スタック・区間内外・重み、各`*-conversion.log`に変換限界、`invalid-trace.log`に不正入力時の失敗を保存した。原トレースとETLXはartifactsへ保持する。再収集も製品/依存変更も行わず、CPU・GC停止の因果の確定、対策比較、文法/照合/UI/物理I/Oの分離とA-01全体は未完了のまま維持する。
