# codeView 実装・検証記録

## 2026-10-30：実装開始

実装対象は[タスクリスト](implementation-plan.md)と[詳細設計](detailed-design/README.md)に従う。F-01〜F-09、Q-01〜Q-08、QS-01〜QS-12、D-01〜D-10の対応台帳はタスクリスト11・12章を使用し、実測・未完了をここへ追記する。

### 環境

| 項目 | 確認値・状態 |
| --- | --- |
| 手元のOS | macOS 27.0.1、arm64 |
| CPU・メモリ | 物理10コア / 論理10コア、32 GiB |
| SDK | .NET SDK 10.0.401、ターゲットnet10.0 |
| UI | Avalonia.Desktop / Fluent 12.1.3を初期固定 |
| 本文 | Avalonia.AvaloniaEdit 12.0.0を仮参照。Phase 2の採否は未判定 |
| 単体試験 | NUnit 4.1.0、Adapter 4.5.0、Microsoft.NET.Test.Sdk 17.14.1を初期固定 |
| Windows実機 | 未確保・実行未検証。最終受け入れの必須項目として残す |
| 別PC・署名・公証 | 未検証 |

依存はローカルNuGetキャッシュ内で確認できた組合せを固定し、ロックファイルを保存した。初期復元はキャッシュのみを使用し、ネットワーク依存のNuGet脆弱性監査は実施していない。今後の依存評価・配布前の監査を省略しない。最新LTSの根拠は[.NET 10の公式発表](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)、本文の参照元は[公式NuGetページ](https://www.nuget.org/packages/Avalonia.AvaloniaEdit/12.0.0)を参照する。

### 進捗と証跡

| 対象 | 状態・確認内容 | 残る作業 |
| --- | --- | --- |
| P0-01 | 要件・品質・シナリオ・D台帳を本計画の対応表へ照合 | 実装に応じて証跡を更新する |
| P0-02・P0-03 | 手元のSDK・macOS、UI・試験依存を確認 | Windows・別PC、採用候補の全OS検証 |
| P1-01 | 製品1・試験1、SDK固定、パッケージロック、Releaseビルド成立 | Windowsビルドと完全な再現手順 |
| P1-02 | UTF-16位置、改行チャンク境界、範囲の不正値・オーバーフロー、結果区分、旧Epoch拒否を実装 | 文書の所有移譲・SearchCompletionの統合検証 |
| P1-03 | ダークテーマ・最小ウィンドウ・メニューの骨格をビルド | GUI起動と初回描画の計測、操作接続 |

検証コマンドは以下。Sandbox内のMSBuildがプロセス間通信待ちに留まったため、取消・終了を確認した後、権限付きの単一ノードで実行した。これは製品の処理モデルの変更ではない。

```sh
dotnet restore codeView.slnx
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false
```

実行時は`DOTNET_CLI_TELEMETRY_OPTOUT=1`を指定した。初期のオフライン復元には`--source /Users/hiroaki/.nuget/packages --ignore-failed-sources -p:NuGetAudit=false`を付けたが、このマシン固有のパスと監査省略を標準手順にしない。

- Release：合格15、失敗0、スキップ0。
- TRX：`tests/codeView.Tests/TestResults/foundation.trx`（生成物のためGit対象外）。
- 本体の初回描画・起動時間・本文性能は未測定。G0・G1は未完了。
- HTMLモック：8テスト合格、コミット`bb5576a`。実アプリの達成とは分けて扱う。

### 計画の進行条件の補足

Windows実機が未確保という環境条件だけで、macOS上の共通実装・単体試験まで停止するのは進行上不適切である。ゲートの証跡は未完了のまま保持し、判断に依存しない実装は先行する。これにより共通実装を進められる一方、OS差を後段で見つけるリスクがあるため、Windowsビルド・実行は継続して必須として追跡する。

この補足は計画の進行順の明確化であり、Windows対応、性能予算、本文採否、受け入れ試験を免除する変更ではない。G2の本文採否が未判定のまま本文モデルを確定して先へ進めない。

## 2026-10-30：設定検証と起動待ち状態

- SettingsSnapshot / Validator / Storeを追加。64 KiB・255スカラー・256名の境界、空配列、未知版・未知項目、不正UTF-8、BOM、重複プロパティ、ポータブル優先、取消を検証した。
- 起動待ちのStartupOpenQueueを追加。設定の一度だけの確定、FIFO、入力コピー、要求/パス/文字の上限、実行中の予算、完了の二重通知、停止後の遅延通知を検証した。
- Release：合格45、失敗0、スキップ0。TRXは`tests/codeView.Tests/TestResults/settings-startup.trx`。
- P1-04のロジックとP1-05の状態部分が進行。UI・実I/Oのアクセス拒否・起動タイマーへの統合が残るため、タスク全体は未完了。設定書込は未実装・無効のまま。
- ビルド基盤・共通契約はコミット`daca8bb`。将来の性能・GUI・Windows確認をこれらの単体試験で代用しない。

## 2026-10-30：読取専用I/Oと厳格デコード

- SourceAccessとFileReaderを追加。UTF-8/CP932/UTF-16/UTF-32、BOM・手動指定、バイナリ疑い、チャンク境界、再読込時の変更、リンク先の更新、取消・ハンドル解放とソースハッシュ不変を検証した。
- macOSのFIFOを非ブロックOpenと通常ファイル判定で拒否する。設定読込も同じ境界を使うよう設計を補い、設定パスがFIFOでも起動を待たせない試験を追加した。
- Release：合格61、失敗0、スキップ0。TRXは`tests/codeView.Tests/TestResults/file-reader.trx`。macOS arm64での結果であり、WindowsとmacOS x64のnative経路は未実行。
- P1-06・P2-02のロジックが進行。現時点は同期ワーカー用の入口であり、UI接続・検索用の非同期返却・全バイナリ形式・性能評価は残る。G1・G2は未完了。
- 設定・起動待ちはコミット`b81e630`。AppOperationsへの統合は次の作業。

## 2026-10-31：単体ファイルのGUI表示

- DocumentBuilder / Handle / ReadLeaseを追加。ワーカーの同一スレッドで構築し、所有解放後に表示スレッドで取得する。Undo履歴なし、閉鎖後の不変ビュー、二重解放・範囲外・異なる構築スレッドを試験した。
- AppOperationsで読込枠1件、旧ロード取消・世代拒否、失敗時の旧本文維持を接続。ViewModelで設定確定待ちの共通Openを処理し、CLI・ファイルダイアログ・再読込・閉鎖・Cmd/Ctrl+O/Wを最小UIへ接続した。
- Release：合格69、失敗0、スキップ0。TRXは`tests/codeView.Tests/TestResults/initial-viewer.trx`。画面モデルの試験はUI相当の単一スレッドSynchronizationContextを使い、本文ライブラリのスレッド制約を緩めず確認した。
- macOS arm64で開発用.appを作り、実GUIで`docs/requirements.md`をCLIから表示、本文への入力が反映されないこと、Cmd+Wの空画面、Cmd+OのOSダイアログから`TextCursor.cs`を開けることを確認した。閉じる操作後のプロセス終了コードは0。
- 開発用.appは`artifacts/dev/codeView.app`にRelease出力をコピーし、開発用Info.plistを付けた検証物。追加ランタイム不要の配布物、署名・公証、別PC起動を達成したものではない。
- 本文の大容量性能・初回描画時間・ドラッグ&ドロップ・タブ・ツリー・検索・TextMate・文字コード変更UIは未実装/未検証。G1・G2・最終受け入れは未完了。
- 読取専用I/Oはコミット`bed15f8`。以後の文書・UI変更はこのセクションの試験結果に対応するコミットへ記録する。

## 2026-10-31：大容量データとコア性能の初回評価

- P0-06用に外部依存のないデータ生成器を追加。ファイル数・正確な総バイト数・文字コード・配置・seed・期待一致数のマニフェストを検索ルート外に出す。既存出力は上書きしない。Python単体試験は正常・不正・最小サイズ・デコード境界・再現性の5件合格。
- [性能予算](performance-plan.md)を測定前に固定。CorePerformanceTestsは環境変数指定時だけ測り、通常試験とは別に実行する。失敗経路を含め文書を破棄する。製品側への計測コード追加はない。
- macOS上で.NETのPrivateMemorySize64/PeakWorkingSet64が0だった初回値はメモリ判定無効。SDKの`sys/resource.h`のrusage_info_v0に従い`proc_pid_rusage`のphys_footprintへ訂正した。取得失敗・0を合格値として扱わない単体試験を追加。WindowsのPrivate Memoryとの直接比較は行わない。予算の数値は変更していない。
- Release単体試験：合格73、失敗0、スキップ0（性能カテゴリ除外）。TRXは`tests/codeView.Tests/TestResults/memory-measurement.trx`。
- macOS 27.0.1 arm64、32 GiB、SDK 10.0.401 / runtime 10.0.12、Release、文法なし。生成後のWarmデータを、各条件で独立したテストプロセスにより1回測定。結果JSONと生成マニフェストは[測定記録](performance-results/2026-10-31-core/)へ保存した。

| 条件 | 構築ms | サンプルピークメモリ増分MiB | GC停止合計ms | CPU ms |
| --- | ---: | ---: | ---: | ---: |
| UTF-8 50 MiB | 890.1 | 154.8 | 84.4 | 1079.5 |
| CP932 50 MiB | 1083.7 | 190.4 | 105.3 | 1268.7 |
| UTF-8 50 MiB単一行 | 156.4 | 107.4 | 45.0 | 159.7 |
| UTF-8 50 MiB×5同時保持 | 336.6〜896.2/本文 | 720.1 | 426.4 | 2781.9 |

- 今回の構築時間・メモリ増分・GC合計・読取ビュー取得は初期予算内。Snapshotは全条件0.08ms未満・248バイト以下。これは全文描画・走査の証拠ではない。サンプリングは10msで瞬間最大値の保証はなく、Managed Heapとphys_footprintは異なる指標である。CPU値はプロセス累積時間の差であり、コア使用率ではない。
- LastGcLohBytesは最後のGC時点のLOHサイズで、実行全体のLOH割当量ではない。個別GC停止のトレース、Coldの物理I/O、GUI、文法、再読込ピーク、10/100 MiB、反復による分布は未測定。G2と本文モデル採否は未完了のまま。

## 2026-10-31：文字コード指定と再読込の位置復元

- P2-06の単体ファイルUIとして、自動判定・UTF-8・CP932・UTF-16/32 LE/BEをファイルメニューとステータスバーから選択できるようにした。自動判定の失敗後も再試行できる。表示本文がある場合はその本文が指定対象で、別のファイルを開く要求の失敗によって対象を切り替えない。
- 新規Openは自動判定、通常の再読込は最後に成功した指定を使う。失敗・取消では旧本文/文字コードを維持し、成功後だけ指定と本文を更新する。Operationsの既存のLoadEpoch・取消・読込枠を使い、新しいレイヤや依存方向は追加していない。
- 再読込/文字コード変更成功時はカーソルを新本文の行数・行長へ補正し、UI接続による初期位置へのリセット後に復元する。別ファイルの新規Openは先頭から表示する。詳細設計04に入口・失敗対象・表示名・位置復元を具体化した。
- Release単体試験：合格80、失敗0、スキップ0（性能カテゴリ除外）。6種類の文字コード・BOMなし再試行・指定維持・失敗時の旧本文/指定/位置・閉鎖後の無操作・範囲外・短縮・空本文・ソースバイト不変を検証。TRXは`tests/codeView.Tests/TestResults/encoding-reload.trx`。
- macOS arm64の別の開発用.appで、BOMなしUTF-16 LEの自動表示拒否と再試行ボタン、文字コード選択後の`first / 日本語 / last`の3行表示を実GUI確認した。検証用プロセスは閉じる操作後に終了コード0で終了した。ポップアップ項目は自動取得のAX/スクリーンショットに現れなかったが、キーボード選択による結果は確認できた。メニューの視認性とアクセシビリティは別途実機受け入れで追跡する。
- UIの選択部品は[Avalonia MenuFlyoutの公式手順](https://docs.avaloniaui.net/controls/menus/menuflyout)を参照。文法・本文性能に作用する本文モデルは変更していない。操作時に既存の非同期ロードを実行し、起動時の一括読込や監視は追加していない。
- P2-06全体は、タブごとのDocumentVersion・スクロール位置・本文内検索の無効化との接続、連続変更/終了の実GUI試験が残るため未完了。G2採否の性能・文法評価も継続する。計測基盤と初回証跡はコミット`37a8e2b`。

## 2026-10-31：ネイティブGUI計測ビルドの分離

- P1-03/P2-07用に`CodeViewPerformanceProbe=true`時だけ組み込むGUI計測コードを`tests/GuiProbe`へ追加した。通常ビルドに計測型がないこと、スクロール先の通常・空・短い本文・整数境界・不正入力を単体試験する。
- 計測内容と代理指標の限界を[性能評価条件6章](performance-plan.md#6-ネイティブguiの代理指標の測定)に記載。文書接続・同期レイアウト処理・コールバック間隔と、物理描画/入力を区別する。測定開始はMain入口なのでOS起動全体の合格証拠にしない。通常の製品動作・本文モデル・レイヤ構成は変更しない。
- 計測用Releaseビルド：警告0・エラー0。通常のRelease単体試験：合格88、失敗0、スキップ0（性能カテゴリ除外）。TRXは`tests/codeView.Tests/TestResults/gui-probe-plan.trx`。
- 50 MiB UTF-8の実行では、画面生成前に`Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`で初期化に失敗した。セッション83213を再確認し、終了コード134で終端となった。文書接続やスクロールの段階には到達せず、結果JSONも生成されていないためGUI性能は未測定。
- エラー原因をOS画面状態・ライブラリ不具合などと推定して確定せず、ネイティブ描画環境が利用できる時に再評価する。別レンダラーへの変更や、コア計測での代用はしていない。G1/G2は未完了で、文法同梱など進行可能な作業を先行する。

## 2026-10-31：文法候補の同梱・字句分類と予算未達

- P2-05の候補基盤としてAvaloniaEdit.TextMate 12.0.0 / TextMateSharp.Grammars 2.0.3を固定し、製品/試験のロックファイルを更新。依存はTextMateSharp 2.0.3 / Onigwrap 1.0.10。既存NuGetキャッシュから復元し、この復元時のNuGet脆弱性監査は未実施。最新版・最終採用版とする判断はしていない。
- BundledGrammarsでC#、C、C++、`.h`のC++、JSON、XML系、XAMLのXMLフォールバック、Markdownを対応付けた。未知拡張子と生成のみでは文法/テーマを初期化しない。実際の文法は同梱資源から取得し、ネットワークや読込対象ルートを文法検索先にしない。
- 実パッケージによる資源の存在、C/C++双方のヘッダーのコメント・文字列・型/キーワード、複数行コメント状態、JSON・XML/XAML・Markdownの分類を検証。macOS arm64でOnigurumaネイティブ経路を実行した。画面での色表示や他OSの実行を達成した証拠にはしない。
- Release単体試験：合格120、失敗0、スキップ0（性能カテゴリ除外）。TRXは`tests/codeView.Tests/TestResults/bundled-grammars.trx`。
- 初期文法保持予算64 MiBを測定前に固定し、1つのRegistryへ6言語を順にロード・代表行を初回/100回トークン分割した。初期化20.1ms、C++ロード16.0ms＋初回分割43.5ms、Markdown31.0ms＋50.5ms、割当107.6 MiB、GC停止合計44.4ms、CPU240.0ms。後続言語は同一プロセスの初期化を共有するため、言語間比較やUI同期処理可否の判定には使わない。
- OSメモリ増分71.5 MiBで64 MiB予算に**不合格**。結果は[測定JSON](performance-results/2026-10-31-grammars/all-registry-budget-failed.json)、TRXは`tests/codeView.Tests/TestResults/grammar-performance.trx`。前後値であり瞬間ピーク/強参照の厳密なサイズではない。GC未回収とJIT等をリークと混同しない。予算は緩めず、全コンパイル済み文法の共有保持を採用せず、アクティブ表示単位の寿命と切替時のGCを次に比較する。
- ライセンスの初期確認は各nupkgのnuspecでTextMate関連とOnigwrapがMIT、Onigwrap同梱THIRD-PARTY-NOTICESでOnigurumaの再配布通知を確認した。文法個別の通知収集・配布物への同梱はP6-03で追跡し、パッケージのMIT表示だけで完了扱いにしない。
- 根拠：[固定版TextMate統合ソース](https://github.com/AvaloniaUI/AvaloniaEdit/blob/86fdebec4cff7affb0e14c7885df71d28edce777/src/AvaloniaEdit.TextMate/TextMate.cs)、[固定版RegistryOptions](https://github.com/danipen/TextMateSharp/blob/4532112f6ee96c8d7847ee66a79719dfe58e9f43/src/TextMateSharp.Grammars/RegistryOptions.cs)。P2-05のUI接続・失敗時の切離し、P2-09の採否・文法キャッシュ寿命・閾値は未完了。通常UIでは色分けをまだ有効化していない。GUI計測基盤はコミット`eeb0549`。

## 2026-10-31：長行のGUI未達と有界表示の比較試作

- 前回ターンはコミット`eeb0549`/`a3acd90`で計測基盤・文法資源・字句分類・予算未達を記録し、進捗があった。今回は当初文法寿命を対象としたが、利用可能なGUI状態の変化と、より大きい本文描画の未達を確認したためA-01/A-02へ進んだ。
- 読取専用の`pmset -g assertions`でUserIsActive=0を確認。既存検証アプリへのUI操作後にUserIsActive=1とdisplay-on関連のassertionを確認し、初期化失敗で終端だった計測を新しい状態で再実行した。レンダラー・スリープ設定・セキュリティ設定は変更していない。これだけでRenderTimerエラーの原因を確定はしない。
- 50 MiB UTF-8通常改行の初回GUI値：Main→Opened 452.7ms、Opened後2tick到達676.6ms、本文接続同期89.5ms、64回のScrollTo/EnsureVisualLinesは中央値0.23ms・最大0.64ms、tick間隔中央値16.66ms・最大18.11ms。1180×800 DIP、scaling 2、文法なし、Warm。OSプロセス生成〜物理描画・物理入力の達成とはしない。
- 50 MiB UTF-8単一行では、Main→接続開始638.9ms、接続終了28364.0ms（区間約27.7秒）。15秒時点RSS 2,616,352 KiB・CPU約100%、約28秒時点のsampleで物理footprint/peak表示9.3G。scroll-2まで進んだが継続して予算超過していたため対象PID56035のみをSIGTERMで停止し、セッション16926の終了コード143を確認。正常完了JSONはない。生成済みの同一長行を拒否して解決扱いにしない。
- この初回GUI計測は`eeb0549`作業中の開発用.app（終了処理・報告メタデータ追記前）の予備値である。測定DLLのSHA256、段階別ログ、sampleのハッシュ・抜粋、通常改行のJSONを[証跡](performance-results/2026-10-31-long-lines/)へ保存した。最新コミットそのものの完全な回帰結果と称さず、採用判断前に固定ビルドで再測定する。完全なsampleは`artifacts/gui-results/long50.sample.txt`に残す。
- 文法なしでGUIの時間・メモリが大きく超過したため、まず表示レイアウトを是正する。[方式比較・影響範囲・移行手順](long-line-rendering.md)を追加。通常本文・不変ReadLeaseを維持し、表示範囲だけを限定する試作を専用ビルドへ追加した。通常UIの層・依存・本文モデルを変更せず、採用判断もしていない。
- TextWindow試作は最大16,384 UTF-16単位、原文への範囲対応、サロゲート/CRLFの端・ターゲット補正を持つ。元の長行全域を保持して64箇所を移動するGUI計測を準備し、10msのOSメモリサンプルと表示文字数・原文オフセットを追加した。
- GUIなしの50 MiB長行切出し：64箇所、各16,384文字以内、最大0.498ms・中央値0.0067ms、合計割当2,163,672 bytes。4 MiB・各50msの事前比較予算内。TRXは`tests/codeView.Tests/TestResults/bounded-window-core.trx`、JSONは上記証跡の`bounded-window-core.json`。本文構築を含む値でもGUI描画の合格でもない。
- Release単体試験：合格126、失敗0、スキップ0（性能カテゴリ除外）。原文範囲・先頭/末尾・サロゲート・CRLF・空本文・無効な上限/位置・Lease閉鎖を追加。TRXは`tests/codeView.Tests/TestResults/text-window.trx`。専用GUI試作ビルドは警告0・エラー0。
- GUI試作の実行前にコンピュータ操作ツールがMacのロック状態を検出し、手動解除を要求した。迂回せず解除を依頼済み。GUI比較は未実施のまま。原文座標のUI・選択・コピー・Unicodeの結合列・文法状態を満たす完全実装も残るため、G2/本文採否・A-01/A-02は未完了。文法キャッシュ64 MiB未達の是正も継続する。

## 2026-10-31：本文ReadLeaseの逐次走査

- 前回の`92cb704`は長行の実測未達と比較試作を記録した進捗ターン。GUI比較は手動ロック解除待ちのため、P2-04の読取ビュー走査と寿命の検証を進めた。コンピュータのロックを迂回する操作や未確認のGUI再起動は行っていない。
- DocumentReadLease.VisitChunksを実装。ワーカー上でsnapshotのTextReaderを開き、最大16 Ki UTF-16単位の借用メモリを通知する。falseで早期終了、取消は読取/通知前後、利用側例外は伝播し、全終了経路でreaderを破棄する。メモリ保持・awaitをしない契約を詳細設計04/05へ追加。層・依存方向・UI接続・全文保持モデルは変更していない。
- 単体試験は空・上限前後・長い本文、チャンクをまたぐサロゲート/CRLF、原文位置、不変スナップショット、早期終了、取消・利用側例外後の再走査、破棄後の拒否を検証。AppOperationsを用いた結合試験で、ワーカーの走査途中に文書を閉じる/別文書へ入れ替えても旧Leaseは正しい原文を読み、解放後にLease数が0になることを確認した。
- Release：合格141、失敗0、スキップ0（性能カテゴリ除外）。TRXは`tests/codeView.Tests/TestResults/document-scan.trx`。macOS arm64での結果で、Windows実行は未実施。
- 50 MiBの各生成済み本文を構築後にハンドルを閉じ、既存Leaseで3回全域走査しUTF-16単位の加算を行った。[測定証跡](performance-results/2026-10-31-document-scan/)を保存。以下の時間・割当は本文構築・ディスク再読込・検索照合を含まない。

| 入力 | 全域走査ms（3回の範囲） | 先頭チャンクms（最大） | 走査割当bytes（最大） | CPU ms（3回＋早期終了） |
| --- | ---: | ---: | ---: | ---: |
| UTF-8 通常改行 | 29.4〜37.7 | 0.292 | 33,528 | 98.2 |
| CP932 通常改行 | 37.5〜43.7 | 0.302 | 33,528 | 120.1 |
| UTF-8 単一長行 | 37.5〜45.8 | 0.305 | 33,528 | 122.2 |

- 3条件とも1秒・先頭50ms・256 KiB割当の事前予算内。測定区間のGC回数はGen0/1/2とも0・停止合計0ms。早期終了は1チャンク・0.004〜0.008msだった。UI・照合の速さを証明する値ではない。
- 候補readerは小さなleafで短く返すため、長行では約15.6万回通知する。ReadBlockと配列Readループで16 Kiまで集約する方式も比較したが、全域走査が約50〜55msへ増えたため採用しなかった。通知数低減だけで高速化と称さず、単純な短チャンク方式を残す。検索側の必要な集約は実際の照合を含むD-04で再評価する。
- P2-04の評価用走査は完了。P2-03のGUI移譲/長行是正、D-04の検索方式採否、本文モデル採否とG2は未完了のまま。毎回の全文コピーがないことと初期応答の評価基盤を得たもので、実際のファイル内検索を完了にしていない。

## 2026-10-31：手動文字コード指定でもPE形式を拒否

- P2-02の既知形式判定を補い、PE形式を自動・全6種類の手動文字コードでBinaryとして拒否するようにした。従来はNUL等のヒューリスティックに依存し、手動指定でその判定を回避するとPEがデコード可能な場合に本文として扱われ得た。
- MZだけで拒否せず、DOSヘッダーのオフセットと`PE\0\0`署名を確認。標本内は追加I/Oなし、標本外/境界は同じハンドルで4バイトだけ追加取得する。通常の非MZファイルでは追加I/O・追加バッファ確保はない。MZ対象では追加seek/readが最大1回増えるため、D-06のI/O評価に含める。層・依存・読取専用の構造は維持した。
- 64/128バイト、8 KiB標本の末尾と跨ぎ、64 KiB先の署名、全手動指定、短いMZ本文、不正/範囲外/最大値のオフセット、署名不一致後の巻戻し、元ファイルハッシュ不変と解放を試験した。
- Release：合格161、失敗0、スキップ0（性能カテゴリ除外）。TRXは`tests/codeView.Tests/TestResults/pe-binary.trx`。これはmacOS上の形式判定試験でありWindowsでの実行試験ではない。起動・描画・検索の実GUI性能の再試験は未実施。
- 詳細設計03に判定条件と追加I/Oを具体化。形式全体の正当性を確認するパーサや安全な実行の保証として扱わない。P2-02全体とG2は未完了。逐次走査はコミット`9b78c9b`。
