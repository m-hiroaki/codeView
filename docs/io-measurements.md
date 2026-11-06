# 読取量とプロセスI/Oの確認手順

P0-04の取得可否・負荷確認用。製品コードには計測処理を追加せず、テスト内の`IoPerformanceTests`を明示実行する。[今回の結果](validation-results/2026-11-06-io-accounting/README.md)はWarmのFileReaderだけを対象とする。本文構築・表示・検索・Coldの性能判定は含まない。

## 指標

| 指標 | 定義と制限 |
| --- | --- |
| 入力バイト数 | ファイル長。検証による再読取を含まない |
| API読取バイト数 | 元のFileStreamの成功したRead/ReadAsync/ReadByteが返した量。サンプル・事前検証・消費を含む。EOF/長さ0の呼出しは件数だけ加算し、失敗した呼出しは加算しない。OSへの要求量や物理I/Oではない |
| macOS.rusage_v2.diskio_bytes | 自プロセスの`proc_pid_rusage`のdiskio差分。採用SDKの構造体サイズ/オフセットをCで照合する。カーネルによるプロセス全体の集計で、ファイル別のハードウェア転送量とは扱わない |
| Windows.IO_COUNTERS.transfer_bytes | 自プロセスの`GetProcessIoCounters`のRead/WriteTransferCount差分。プロセスのI/O転送量であり、物理ディスク量やmacOSの指標とは同一視しない |
| CPU/時間 | Prepare開始からReadChunks終了まで。前後のネイティブカウンター問合せは除外し、問合せ10,000回の負荷を別記する |

macOSの定義は採用SDKの`sys/resource.h`と[Appleの定義](https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/sys/resource.h)、Windowsは[IO_COUNTERS](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-io_counters)と[GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters)を参照する。差分は同じ種類の単調なカウンター間だけで計算し、減少や取得失敗を0に変換しない。

## 実行

前提はglobal.jsonに従う.NET SDK、Python 3、[P0-06の生成済みUTF-8/CP932入力](performance-fixtures.md)。Cの対照試験にはmacOS SDKとclangを使う。コマンドはリポジトリのルートで実行する。

```sh
mkdir -p artifacts/io-probe
cp docs/validation-results/2026-11-06-io-accounting/IoHost.cs.txt artifacts/io-probe/IoHost.cs
cp docs/validation-results/2026-11-06-io-accounting/run.py.txt artifacts/io-probe/run.py
dotnet build artifacts/io-probe/IoHost.cs -c Release --output artifacts/io-probe/host
python3 artifacts/io-probe/run.py
```

ビルド完了後、他の性能試験と並走させない。再実行時はrun.pyの出力ディレクトリとラベルを新しくする。入力パスは今回の記録値なので、別の入力では両文字コードのパスとmanifestを確認する。各入力をSHA-256と独立した厳格復号で事前読取しWarmにする。各文字コードをbaseline/counted交互に各3プロセス、各10回読む。両モードともOSカウンターを取得し、countedだけAPI読取を数える。件数・文字数・消費バイト数・入力ハッシュを検証し、失敗時は非0終了する。JSONはホストのTestResultsへ保存する。

APIラッパーは元のSourceAccessの検査済みハンドルとバッファ付きストリームへ委譲する。API間の再委譲で二重加算しない。同期/非同期/EOF/境界/BOM/例外/解放は通常テストで検証する。製品のストリームや公開APIは変更しない。

## キャッシュと負荷の解釈

WarmでAPI読取が正でもmacOS diskio差分0は有効な観測である。取得不能を示す値ではない。今回のC対照試験では新規ファイルの書込と別プロセスの読取に正のカウンターを観測したが、50 MiB読取に対してdiskioは48 MiBだった。`F_NOCACHE`が成功しても、既存Warm入力では差分0だった。これらを完全なColdやファイル別物理I/Oの証明にしない。OS全体のキャッシュ削除や設定変更は行わない。

通常とラッパーの時間/CPUは3プロセスの分布を残す。計測環境の揺れを含むため、中央値の僅差を高速化や負荷ゼロの根拠にしない。WindowsでのネイティブAPI疎通は通常CIで確認するが、専用機での負荷・物理I/O・GUIフレーム等は別の未実施項目として残す。
