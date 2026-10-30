# 性能評価条件・予算（実測前の初期固定）

作成：2026-10-31。対象：[実装計画](implementation-plan.md)のP0-04・P0-06・P1-09・P2-01〜P2-09。数値はこの時点で固定する初期合格予算であり、達成済みの性能ではない。

## 1. 基準機と測定の区別

手元の基準はmacOS arm64・物理10コア・32 GiB、.NET SDK 10.0.401、Release、Avalonia 12.1.3、AvaloniaEdit 12.0.0候補とする。WindowsとmacOS x64、別PC・自己完結発行は別の測定対象であり、手元の結果で合格にしない。

通常の機能試験、処理モデルのコア測定、ネイティブGUIの表示/スクロール測定、詳細GCトレースを区別する。コア測定の短い時間をGUIの描画応答の証拠にしない。生成直後のデータはWarmまたはキャッシュ不明とし、Cold測定には準備・物理I/Oの確認を別途行う。

## 2. 本文・表示の合格予算

| 項目 | 初期予算・判定条件 |
| --- | --- |
| 起動 | プロセス開始から初回ウィンドウ表示/描画まで目標1秒以内。10回以上、中央値と最大、初回署名確認を分離 |
| 50 MiBの読取・文書構築 | UTF-8とCP932の検証・消費読込・構築を含め各5秒以内。通常改行と長い単一行を分けて記録 |
| 50 MiBの単体保持 | 基盤からのサンプル計測ピークのOSメモリ指標増分512 MiB以内。Managed・Working Set・割当を併記 |
| 50 MiB×5本文 | 同時保持のOSメモリ指標増分1.5 GiB以内。タブUI前は評価ハーネスで保持を測る |
| 再読込 | 旧本文を成功まで保持するピークを測り、1ファイル当たりの増分を単体保持予算の2倍以内に収める |
| 読取ビュー | 取得50ms以内、取得時の割当1 MiB以内。全文複製がないことを実測で確認 |
| UI接続・操作 | UI上の接続処理と入力応答は100ms以内。連続スクロールは60Hz環境で概ね16.7ms/フレーム、100ms超停止なしを目標 |
| GC停止 | 単体読込のGC停止合計150ms以内、単一停止50ms以内を初期予算とし、UI停止との相関も確認 |
| 100 MiB | 拡張評価。10/50 MiBと同じ指標と限界点を報告し、必須保証の上限を無断で拡大しない |

GCの累積停止合計と、トレースで確認する個別停止は別の指標である。最後のGCに含まれる停止だけで実行全体の最大値と断定しない。トレース未取得の指標は未測定とする。OSメモリ指標はWindowsではPrivate Memory、macOSでは`proc_pid_rusage`の`ri_phys_footprint`を使い、指標名を記録する。OS間で同じ値とはみなさない。定期サンプルは真の瞬間最大値ではないため間隔も記録する。取得値0・取得失敗では測定を失敗させる。macOSの.NETプロセス統計が0を返した初回測定はメモリ判定無効とし、上記の数値予算を維持して再測定する。

本文モデルが予算を超える場合はA-01から切り分け、プレーン表示でも長行・スクロールが成立しなければ採用を確定しない。予算を結果に合わせて緩めない。

## 3. データ生成

外部依存なしのPythonスクリプトで、既存出力を上書きせず新しい場所へ生成する。マニフェストは検索対象ルートの外側に保存し、ファイル数・総サイズ・文字コード・配置・seed・期待一致数・不一致語を記録する。生成途中で失敗した出力は不完全として残し、自動削除しない。

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/generate_fixture.py artifacts/fixtures/text50 --bytes 52428800
python3 tools/generate_fixture.py artifacts/fixtures/cp93250 --bytes 52428800 --encoding cp932
python3 tools/generate_fixture.py artifacts/fixtures/long50 --bytes 52428800 --layout single-line
python3 tools/generate_fixture.py artifacts/fixtures/l1 --profile l1
python3 tools/generate_fixture.py artifacts/fixtures/l2 --profile l2 --layout mixed
python3 tools/generate_fixture.py artifacts/fixtures/l3 --profile l3 --layout wide
```

L1は5万ファイル・1 GiB、L2は20万・5 GiB、L3は100万・20 GiB。実行する前に空き容量と試験対象を確認する。L3は限界調査であり完走を保証しない。UTF-8/CP932を任意バイト位置で切らず、端数はASCIIの空白で埋めるため正確なサイズと厳格なデコードを両立する。`CODEVIEW_MATCH`は各ファイルに1回、`CODEVIEW_ABSENT_QUERY`は0回である。

## 4. 後続評価

TextMate文法の対象言語別の負荷とキャッシュ上限は、同じ予算内で比較して停止閾値を決定する。検索のCold/Warmごとの時間・メモリ・GC予算はP4-01で実装前に固定し、本文の予算から推測しない。長時間試験はP6-01で固定する。現在の未測定指標・実測結果・採否は[実装記録](development-log.md)へ対応付ける。

## 5. コア測定の再現

生成完了後にRelease単体試験でハーネスをビルドし、別プロセスで性能カテゴリを実行する。次はUTF-8の1本文の例。CP932/long50は入力とラベルを変更し、5本文の保持は`CODEVIEW_PERF_COPIES=5`を追加する。キャッシュ条件を確認できなければ`unknown`と記録する。

```sh
dotnet test codeView.slnx -c Release --filter 'TestCategory!=Performance'
CODEVIEW_PERF_FILE="$PWD/artifacts/fixtures/text50/group-00000/source-0000000.cs" \
CODEVIEW_PERF_LABEL=utf8-50 CODEVIEW_PERF_CACHE=warm \
dotnet test codeView.slnx -c Release --no-build --filter TestCategory=Performance
```

JSONはテスト出力ディレクトリの`TestResults/<label>-<copies>.json`へ保存する。同名の測定は上書きされるため比較前に保存する。測定ではファイル読取・検証・文書構築・Snapshot生成を行う。コア試験だけで本文ライブラリの採用は確定しない。

## 6. ネイティブGUIの代理指標の測定

通常の製品ビルドからは除外する`tests/GuiProbe`を、明示した性能ビルドにだけ組み込む。起動時に文法などを事前初期化せず、同じ画面・読込処理を通る。通常ビルドに計測型が含まれないことを単体試験で確認する。

```sh
dotnet build src/codeView/codeView.csproj -c Release \
  -p:CodeViewPerformanceProbe=true -o artifacts/gui-probe
mkdir -p artifacts/gui-results
CODEVIEW_GUI_PERF_REPORT="$PWD/artifacts/gui-results/new-run.json" \
CODEVIEW_PERF_CACHE=warm \
artifacts/gui-probe/codeView "$PWD/artifacts/fixtures/text50/group-00000/source-0000000.cs"
```

macOSでは基準の開発用.appにこの出力とInfo.plistを配置して実行する。報告先には既存ファイルを指定できない。入力なしではウィンドウ表示イベントと2回のアニメーションコールバックまでを記録する。入力ありでは文書接続とカーソル復元の同期区間、本文接続後2回のコールバック、64回のScrollTo/EnsureVisualLinesの同期時間とコールバック間隔を測る。前半32回は8行刻み、後半は全体の等間隔移動、単一行は行内の等間隔移動とする。物理入力の注入は行わない。

`OpenedMilliseconds`等の原点はマネージドMain入口であり、OSプロセス生成・ランタイム起動・その前のJITは含まない。`WindowTwoTicksMilliseconds`はOpened後に予約したコールバック到達で、本文読込と重なり得る。文書接続時間も、その後の遅延レイアウトをすべて含むとは断定しない。[RequestAnimationFrame](https://api-docs.avaloniaui.net/docs/M_Avalonia_Controls_TopLevel_RequestAnimationFrame)は次のアニメーションtickの予約であり、実際の物理描画完了の証拠ではない。フレーム間隔・同期区間が良好でも、物理描画/入力と起動目標は別途検証する。

計測ビルドは各段階を標準出力へ記録して終了する。停止した場合は対象セッション/プロセスと最後の段階を確認し、スレッドサンプルなどで原因を調べる。観測のタイムアウトだけを理由に同じ測定を再起動しない。OSの描画初期化が失敗した場合は未測定とし、別レンダラーやヘッドレス実行でネイティブGUI合格を代用しない。

## 7. 文法候補の初期化測定

候補はAvaloniaEdit.TextMate 12.0.0 / TextMateSharp.Grammars 2.0.3 / TextMateSharp 2.0.3とする。通常起動で初期化せず、対応言語を使う時にだけ初期化する。採用時の共有文法データの初期保持予算は増分64 MiB以内とし、測定前に固定する。通常本文の512 MiB予算の中に含め、上乗せ扱いにしない。本文ごとのトークン・行モデルは別途本文込みの予算で測る。

`CODEVIEW_PERF_GRAMMARS=1`を指定して性能カテゴリを実行すると、単一のRegistryにC#・C・C++・JSON・XML・Markdownを順にロードし、代表行を初回/100回トークン分割する。作成・文法ロード・トークン分割の時間、割当・GC・CPU・OSメモリ値を記録する。単一プロセス内の後続言語はライブラリ/ネイティブ正規表現初期化の影響を共有するため、言語間の優劣の証拠とはしない。各言語の初回時間をGUIで同期実行してよい根拠にもせず、UIへの接続時に100ms予算で再検証する。メモリの前後値は保持の近似でありサンプルピークではない。
