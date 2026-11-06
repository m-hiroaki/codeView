# 性能用データの準備

P0-06の生成手順。Python 3だけで実行できる。製品の起動や性能測定を同時に行わず、生成・検数・ハッシュ検証を先に終える。既存出力と隣接manifestを上書きしない。

## 本文プロファイル

| profile | ファイル数 | 正確な総バイト数 |
| --- | ---: | ---: |
| text10 | 1 | 10,485,760 |
| text50 | 1 | 52,428,800 |
| text100 | 1 | 104,857,600 |
| tabs50 | 5 | 262,144,000（各50 MiB） |
| l1 | 50,000 | 1,073,741,824 |
| l2 | 200,000 | 5,368,709,120 |
| l3 | 1,000,000 | 21,474,836,480 |

次の例はseed 20261106を固定する。まずplanで予定件数・容量・期待一致数を確認する。planは本文/manifestを書き込まず、contentDigestもまだ持たない。

```sh
python3 tools/generate_fixture.py artifacts/fixtures/new-text50 --profile text50 --seed 20261106 --plan
python3 tools/generate_fixture.py artifacts/fixtures/new-tabs50 --profile tabs50 --layout wide --seed 20261106 --plan
python3 tools/generate_fixture.py artifacts/fixtures/new-l3 --profile l3 --layout wide --distribution mixed --depth 10 --seed 20261106 --plan
```

通常本文、CP932、5タブをそれぞれ新しいルートへ生成する。10/100 MiBはprofileと出力先をtext10/text100へ変える。tabs50はwideにすると5ファイルが直下に並び、既存のTabLifecyclePerformanceTestsへ渡せる。各ファイルを個別に新規作成し、ハードリンク/クローンを作らない。

```sh
python3 tools/generate_fixture.py artifacts/fixtures/new-text50 --profile text50 --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-cp93250 --profile text50 --encoding cp932 --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-tabs50 --profile tabs50 --layout wide --seed 20261106
```

Windowsでは同じ引数を使い、python3をpy -3に置き換える。容量は表の本文合計に加えてディレクトリ/ファイルシステムのメタデータが必要となる。L2は拡張評価、L3は限界調査であり、生成や試験の全件完走・速度を保証したものではない。

## 改行・Unicode・多一致・長行

```sh
python3 tools/generate_fixture.py artifacts/fixtures/new-crlf50 --profile text50 --newlines crlf --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-unicode50 --profile text50 --content unicode --newlines mixed --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-many50 --profile text50 --content many-matches --newlines mixed --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-long50 --profile text50 --layout single-line --seed 20261106
```

- normalは既存のコード/日本語コメント。newlinesはlf/crlf/mixed（LF/CRLF/CR）を選ぶ。
- unicodeはUTF-8の絵文字・合成/分解表記を含む。CP932で表せない文字を置換せず、組合せをエラーにする。
- many-matchesは各行にCODEVIEW_MATCHを置き、正確なサイズ調整後の最終行にも残す。総一致数はmanifestのexpectedMatches。通常の1ファイル1一致と混同しない。検索結果上限10,000等による部分完了と、データ全体の一致総数を分けて判定する。
- single-lineは改行なし。通常/CP932/多一致と組み合わせられる。深さや改行指定の変更は受け付けない。

先頭のCODEVIEW_MATCHは常にUTF-16 offset3、行1・列4。CODEVIEW_ABSENT_QUERYは0件。通常・Unicodeでは各ファイル1件、多一致ではヘッダー/反復パターン/最終行から正確に数える。min64バイトのケースも含め、複数バイト文字の途中で切らず、端数はASCII空白で埋める。

## 規模別リポジトリ

```sh
python3 tools/generate_fixture.py artifacts/fixtures/new-l1 --profile l1 --layout wide --distribution mixed --depth 10 --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-l2 --profile l2 --layout wide --distribution mixed --depth 10 --seed 20261106
python3 tools/generate_fixture.py artifacts/fixtures/new-l3 --profile l3 --layout wide --distribution mixed --depth 10 --seed 20261106
```

wideは先頭10,000ファイルを直下、残りを500件ごとのgroupへ置く。depth 10は最深ディレクトリが10階層になる。この組合せでは直下は10,000ファイル+階層用1フォルダーの10,001項目で、ツリーの表示件数はファイル数と区別する。mixed分布は先頭を最大50 MiB、2件目を最大1 MiB、残りを±1バイトで均等にし、全体を指定容量へ合わせる。L1/L2/L3の通常容量では小・中・大の3段階と、直下1万ファイル/深さ10を同じコーパスに含められる。layout mixedは従来のnested配置の別名でmixed分布を使う。v2ではその分布を大/中/小へ更新したため、v1のmixed結果と同一入力とは扱わない。

encoding utf-8/cp932ごとの独立ルートを作る。混合比率は標準比較ではUTF-8 100%またはCP932 100%とし、2条件を分けて記録する。リンク・不正バイト・アクセス拒否・除外は[機能用データ](functional-fixtures.md)で別に扱い、標準の速度データへ混ぜない。

## 生成後の検証と保存

```sh
python3 tools/generate_fixture.py artifacts/fixtures/new-text50 --verify
python3 tools/generate_fixture.py artifacts/fixtures/new-l1 --verify
```

v2のmanifestは検索対象ルートの外側（例new-l1.manifest.json）に置く。seed、文字コード、配置、分布、深さ、改行/内容、ファイル数、総バイト数、UTF-16単位数、行数、期待一致数、パス/サイズdigest、生成条件+各ファイルSHA-256から作るcontentDigestを記録する。出力先の絶対パスは内容の識別digestに含めず、移動後も検証できる。

verifyは保存済み条件から期待値を再計算し、実ファイル/ディレクトリ数、リンク混入、各ファイルサイズ、全バイトの内容digestを検証する。想定外の.DS_Store等も余分なファイルとして失敗させ、黙って除外しない。試験ルートをファイルブラウザーで開く等の外部変更を避ける。v1には内容digestがないため、この検証の代用にせず過去の測定入力として保管する。既存の通常LFデータは同じ条件ならv1とバイト互換であることを単体試験で確認する。

生成・verify・独立検数はキャッシュを温める。結果はWarm/unknownとして記録し、Cold性能とはしない。検証後はソースハッシュ・manifest・生成器のハッシュを試験記録に保存してから、別のプロセスで[性能測定](performance-plan.md)へ進む。結果から既存の性能予算を緩めない。

## 今回の準備の再現

以下はUTF-8/CP932の10/50/100 MiB・50 MiB×5・長行・多一致・L1と、Unicode 50 MiBを新規生成し、全バイトのハッシュおよび標準デコーダーでの独立した文字数/行数/一致数を検証する。約3.1 GiBとメタデータ領域を使う。L2/L3は書込みなしの計画だけを保存する。

```sh
python3 docs/validation-results/2026-11-06-perf-data-v2/prepare.py /private/tmp/codeview-perf-v2-new
```

```bat
py -3 docs\validation-results\2026-11-06-perf-data-v2\prepare.py artifacts\fixtures\perf-v2-new
```

中断後はresumeで既存manifestの条件を照合し、生成済みデータを再読取検証して再利用できる。不一致や追加ファイルを上書きして修復する機能ではない。

```sh
python3 docs/validation-results/2026-11-06-perf-data-v2/prepare.py /private/tmp/codeview-perf-v2-new --resume
```

POSIXではfreeze-directoriesを追加すると、生成済みの各コーパスのフォルダーをmode 0555にしてから検証する。外部からのメタデータ追加を防ぐための、試験データだけの処置である。既存の想定外ファイルは削除せず検証エラーにする。通常の作業フォルダーやOS設定を変更しない。

```sh
python3 docs/validation-results/2026-11-06-perf-data-v2/prepare.py /private/tmp/codeview-perf-v2-new --resume --freeze-directories
```

片付ける前に、生成したルートへの書込み権限を戻す。

```sh
chmod -R u+w /private/tmp/codeview-perf-v2-new
```
