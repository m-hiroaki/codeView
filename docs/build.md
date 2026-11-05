# OS別の発行スクリプト

通常のアプリをReleaseで発行する。性能比較用の試作コードは含めない。スクリプトは自身の場所を基準にリポジトリへ移動するため、別の作業ディレクトリからも実行できる。

## Windows：Native AOT（x64）

Windows上で、次を用意する。

- [global.json](../global.json)で指定した.NET SDK（10.0.401、許容パッチ版は同ファイルに従う）。
- Visual Studio 2022以降、または対応するBuild Tools。「C++によるデスクトップ開発」と既定のMSVC/Windows SDKコンポーネントを含める。[Microsoftの前提条件](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites)を参照。

コマンドプロンプトで実行する。

```bat
build-windows.bat
```

出力先：`artifacts\publish\win-x64\codeView\codeView.exe`

`codeView`フォルダ全体を使う。Native AOT本体に加え、描画などに必要なネイティブDLLも残す。実行先への.NETランタイムの別途インストールは不要な自己完結構成である。Windows ARM64を対象とするバッチではない。

バッチは`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true`を実行する。通常のJIT発行へ切り替えるフォールバックはない。AOT/トリミングの警告を隠さず、コンパイラー/リンカーの警告もエラーとして扱う。[AvaloniaのAOT資料](https://docs.avaloniaui.net/docs/deployment/native-aot)も参照する。

このバッチの実行とWindows向けAOTリンク・起動はWindows環境で確認が必要。今回の作成環境はMacであり、Windowsでの実行検証は未実施。AOT時の全機能・文法・ネイティブ依存の互換性を確認済みとはしない。

## macOS：Native AOTの自己完結型.app

macOS上で同じ.NET SDKと、Xcode Command Line ToolsまたはXcodeのC/C++ツールチェーンを用意して実行する。[MicrosoftのNative AOT前提条件](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites)を参照。

```sh
./build-mac.sh
```

実行環境のCPUから`osx-arm64`（Apple Silicon）または`osx-x64`（Intel）を選ぶ。明示指定もできる。

```sh
./build-mac.sh osx-arm64
./build-mac.sh osx-x64
```

出力先：`artifacts/publish/<RID>/codeView.app`

`.app`全体を使う。Mac版もPublishAot=trueとPublishTrimmed=trueを指定してNative AOTで発行し、コンパイラー/リンカーの警告をエラーとする。通常のJIT発行へフォールバックしない。SDKが生成する.dSYMも同梱する。`build/Info.plist`と発行された全ファイルを組み合わせる。署名・公証・インストーラー・アーカイブ生成は行わない。Info.plistの製品名/識別子/版は同ファイルで管理する。配布時のGatekeeperと署名確認は別途必要である。

## 依存関係と生成物

最初に製品プロジェクトを`--locked-mode`で復元し、リポジトリ内の依存ロックとの整合性を確認する。その後、ロックを作業用ディレクトリへコピーし、RID・ランタイム・AOTツールを含む発行用の依存グラフを生成する。この復元だけは`RestoreLockedMode=false`とする。RIDなしの元ロックではRID付き発行がNU1004となるためである。リポジトリ内の`packages.lock.json`は書き換えない。

発行時のロックは`artifacts/publish/<RID>/publish.packages.lock.json`に残す。Native AOTコンパイラーなどSDKが追加する依存は発行時に解決するため、発行用ロックまで含めた完全固定のリリース工程とは区別する。初回復元にはNuGetへの通信が必要。

中間生成物は`artifacts/build/<RID>`、一時発行はその下の一意なステージへ出す。発行成功と実行ファイルの存在を確認してから、同じRIDの既存の生成済みアプリを置き換える。失敗時は非0の終了コードを返す。`artifacts`はGit対象外。設定や閲覧対象のソースを成果物へコピーしない。

## 確認した範囲

- Mac arm64：実スクリプトでNative AOT（コンパイラー10.0.12）の.app生成に成功。Info.plist、arm64ネイティブ実行ファイル、CoreCLR/アプリDLLが残らないこと、描画ライブラリ、元ロックからの管理対象依存の版/ハッシュ維持を確認。生成済み14,000 bytes/1,001行のテキストをCLIで開き、タブ/本文/UTF-8表示と通常終了0を確認した。全機能/性能・配布署名・別PC起動は未検証。
- Intel Mac：RID選択とパッケージングを模擬SDKで確認。実際のx64発行/起動は未検証。
- スクリプト単体：正常、CPU選択、空白/`!`を含むパス、別ディレクトリからの実行、不正RID/余分な引数/未知CPU/非Mac、復元/発行失敗、実行ファイル欠落、前回成果物維持、一時物の解放を7件で確認。
- Windows：バッチの実行・Native AOTコンパイル・起動は未検証。MacからのWindows AOT発行を代替検証にしない。

Pythonによるスクリプト試験（ビルド自体にPythonは不要）：

```sh
python3 -m unittest discover -s tools -p 'test_build_scripts.py' -v
```

ライセンス同梱・署名/公証・両OS別PC・オフライン・性能・全機能の受入を含む最終配布ゲートは引き続き未完了。

## テスト実行

リポジトリのルートに以下の入口を用意している。どちらもglobal.jsonの.NET SDKが必要。通常モードではPython補助ツールも試すため、Macはpython3、WindowsはPython 3とpyランチャー（`py -3`）を用意する。C++ビルドツールは単体テストの実行には不要。

```sh
# macOS：通常単体テストとPython試験
./test-mac.sh

# macOS：Performanceカテゴリのみ
./test-mac.sh --performance
```

```bat
rem Windows：通常単体テストとPython試験
test-windows.bat

rem Windows：Performanceカテゴリのみ
test-windows.bat --performance
```

スクリプトの場所を基準に実行するため、別CWDや空白を含むパスにも対応する。通常モードは`TestCategory!=Performance`、性能モードは`TestCategory=Performance`で選別する。依存をlocked-modeで復元してからReleaseでビルド・試験する。製品の性能計測用GUI試作とAOT/トリミングを無効にし、通常の.NET/NUnitランナーで単体試験を実行する。AOT配布物の受入試験は別途行う。

.NETの結果は`artifacts/test-results/macos/`または`artifacts/test-results/windows/`にTRX形式で保存する。通常はunit.trx、性能はperformance.trx。同じモードの再実行で結果を上書きする。Pythonの結果はコンソールへ表示する。復元・.NET試験・Python試験のいずれかが失敗するとそこで停止し、元の非0終了コードを返す。不正引数は2、前提ツール不足は1。

Performanceカテゴリには生成入力やCODEVIEW_PERF_*環境変数が必要な試験が含まれる。[性能計画](performance-plan.md)に従って準備する。未準備の試験はスキップされるため、終了0だけで性能受入を完了扱いにせず、TRXの実行/スキップ件数と個々の結果を確認する。性能モードはPython試験を実行しない。通常のWindows実行ではmacOS用シェルの模擬試験がスキップされる。

Macではtest-mac.shを通じて.NET590件・Python19件合格、終了0を確認した。テスト実行スクリプト自身の模擬試験7件は、通常/性能の選別、別CWD/空白と!を含むパス、不正引数/非Mac、復元/.NET/Python各失敗時の終了コードと後続停止を検証する。最初の一時パス表記の比較失敗は同一ファイルシステム位置の判定へ修正した。Windows用.batはこのMacでは実行未検証。

## コードカバレッジ

通常単体テストと同じSDK/Pythonを用意し、次を実行する。

```sh
# macOS
./test-mac.sh --coverage
```

```bat
rem Windows
test-windows.bat --coverage
```

テスト専用依存の`coverlet.collector` 6.0.4を固定し、[CoverletのVSTest統合](https://github.com/coverlet-coverage/coverlet/blob/v6.0.4/Documentation/VSTestIntegration.md)を使う。`coverage.runsettings`で対象を本体アセンブリ`codeView`に限定し、テストアセンブリ・外部依存・`obj`配下の生成ソースを除く。手書きのUIコードは対象に含める。通常単体テスト（`TestCategory!=Performance`）の.NET実行だけを計測し、その後にPython補助ツールの試験も実行する。Pythonコード自体のカバレッジは計測しない。

TRXは`artifacts/test-results/<macos|windows>/coverage/current/coverage.trx`、行・分岐のレポートは`current`配下のVSTestが生成する実行IDごとのフォルダへ保存する。

- `coverage.cobertura.xml`：Cobertura形式。ルートの`line-rate`/`branch-rate`とクラス・ファイル別の値を確認できる。
- `coverage.info`：LCOV形式。対応するエディター拡張やレポートツールで表示できる。

計測成功後、[ReportGenerator](https://github.com/danielpalme/ReportGenerator/tree/v5.5.11)でHTMLも生成する。リポジトリの`.config/dotnet-tools.json`に5.5.11を固定し、計測モードでのみローカルツールを自動復元する。グローバルインストールは不要。初回はNuGetからのツール取得に通信が必要になる。

`artifacts/test-results/<macos|windows>/coverage/html/index.html`をブラウザーで開く。全体の行/分岐カバレッジ、クラス・ファイル別の割合を確認し、クラス名をクリックするとソース行ごとの実行状況を確認できる。HTMLにはソース本文が含まれる。ローカル閲覧にサーバーやインターネット接続は不要。

```sh
# macOS：計測後にレポートを開く
open artifacts/test-results/macos/coverage/html/index.html
```

```bat
rem Windows：計測後にレポートを開く
start "" "artifacts\test-results\windows\coverage\html\index.html"
```

再計測の開始時に生成済みの`coverage/current`と`coverage/html`だけを削除し、今回の結果で作り直す。過去の別ディレクトリのレポートは入力に含めず、異なる実行のカバレッジを混ぜない。ツール復元・HTML生成の失敗やindex.html未生成も非0終了とする。レポートはGit対象外の`artifacts`内に保存する。収集対象を変更すると比較条件も変わるため、`coverage.runsettings`と実行コミットを併せて記録する。

`--coverage`と`--performance`の併用は拒否する。計測にはIL書換え・実行履歴収集の時間/メモリ負荷があるため、この実行の値を性能評価へ流用しない。通常/性能モードでは収集を有効にせず、本体の依存・起動・描画・検索処理には変更を加えない。カバレッジは実行されたコードの指標であり、GUI実機受入やAOT配布物の検証を代替しない。最低カバレッジによる合否判定は設けていない。

## VS Codeでのデバッグ

codeViewリポジトリのルートをVS Codeで開く。global.json指定の.NET SDKと、推奨拡張Microsoft C#（ms-dotnettools.csharp）が必要。`.vscode/extensions.json`は推奨を表示し、拡張機能を自動インストールしない。

1. `src/codeView/Program.cs`のMain内などへブレークポイントを置く。
2. 「実行とデバッグ」で`codeView: Debug`を選択する。
3. F5で開始する。`codeView: restore`→`codeView: build (Debug)`が順に実行され、Debug/net10.0/codeView.dllへデバッガーを接続して起動する。
4. Shift+F5で終了する。ビルドだけならCtrl+Shift+Bで既定のDebugビルドを実行できる。

設定は`.vscode/launch.json`、`.vscode/tasks.json`、`.vscode/extensions.json`。Mac/Windowsで共通のcoreclrデバッガーを使い、作業ディレクトリはリポジトリルート、出力はVS Codeのデバッグコンソールとする。復元はlocked-mode、DebugビルドではAOT/トリミング/性能GUI試作を明示的に無効化する。Windows配布用Native AOTの設定はbuild-windows.batで維持する。

起動時にファイル/フォルダを開く場合は、launch.jsonのargs配列へパスを個別の文字列として追加する。例：`"args": ["${workspaceFolder}/src"]`。空白を含むパスを1つの文字列にし、文字列自体へ余分な引用符を付けない。

[MicrosoftのC#デバッガー設定](https://code.visualstudio.com/docs/csharp/debugger-settings)と[VS Codeタスク](https://code.visualstudio.com/docs/debugtest/tasks)に従う。Mac上で設定のJSONとタスク参照、登録タスクと同じ引数でのDebug復元/ビルド（警告0・エラー0）、起動対象DLLとPDBを確認済み。VS Code上でF5を操作した起動・ブレークポイント停止とWindows実機での動作確認は未検証。
