# OS別の発行スクリプト

通常のアプリをReleaseで発行する。性能比較用の試作コードは含めない。スクリプトは自身の場所を基準にリポジトリへ移動するため、別の作業ディレクトリからも実行できる。

## Windows：Native AOT（x64）

Windows上で、次を用意する。

- [global.json](../global.json)で指定した.NET SDK（10.0.403、許容パッチ版は同ファイルに従う）。
- Visual Studio 2022以降、または対応するBuild Tools。「C++によるデスクトップ開発」と既定のMSVC/Windows SDKコンポーネントを含める。[Microsoftの前提条件](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites)を参照。

コマンドプロンプトで実行する。

```bat
build-windows.bat
```

出力先：`artifacts\publish\win-x64\codeView\codeView.exe`

`codeView`フォルダ全体を使う。Native AOT本体に加え、描画などに必要なネイティブDLLも残す。実行先への.NETランタイムの別途インストールは不要な自己完結構成である。Windows ARM64を対象とするバッチではない。

バッチは`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true`を実行する。通常のJIT発行へ切り替えるフォールバックはない。AOT/トリミングの警告を隠さず、コンパイラー/リンカーの警告もエラーとして扱う。[AvaloniaのAOT資料](https://docs.avaloniaui.net/docs/deployment/native-aot)も参照する。

Windows 2025 CIで実バッチのAOTリンク・通常ウィンドウ起動・終了0を確認した。[実行と結果](validation-results/2026-11-06-windows-aot/README.md)。AOT時の全機能・文法・ネイティブ依存の互換性を確認済みとはしない。

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

## NU1903（ILCompiler 10.0.12）が出る場合

.NET SDKを10.0.403以降の許容パッチ版へ更新する。旧SDK 10.0.401が選択するILCompiler 10.0.12は[GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9)の対象で、修正版は10.0.14。SDK 10.0.403は[公式リリース情報](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)で対応する修正ランタイムを含む。ランタイムだけでなくSDKを更新する。NuGet監査や警告のエラー扱いを無効にしない。

macOS arm64で修正版SDKによるAOT発行を確認済み。[検証記録](validation-results/2026-11-06-aot-sdk-fix/README.md)。

SDKの更新後、リポジトリのルートで確認して再発行する。

```sh
dotnet --version
./build-mac.sh
```

## 依存関係と生成物

最初に製品プロジェクトを`--locked-mode`で復元し、リポジトリ内の依存ロックとの整合性を確認する。その後、ロックを作業用ディレクトリへコピーし、RID・ランタイム・AOTツールを含む発行用の依存グラフを生成する。この復元だけは`RestoreLockedMode=false`とする。RIDなしの元ロックではRID付き発行がNU1004となるためである。リポジトリ内の`packages.lock.json`は書き換えない。

発行時のロックは`artifacts/publish/<RID>/publish.packages.lock.json`に残す。Native AOTコンパイラーなどSDKが追加する依存は発行時に解決するため、発行用ロックまで含めた完全固定のリリース工程とは区別する。初回復元にはNuGetへの通信が必要。

中間生成物は`artifacts/build/<RID>`、一時発行はその下の一意なステージへ出す。発行成功と実行ファイルの存在を確認してから、同じRIDの既存の生成済みアプリを置き換える。失敗時は非0の終了コードを返す。`artifacts`はGit対象外。設定や閲覧対象のソースを成果物へコピーしない。

## 確認した範囲

- Mac arm64：実スクリプトでNative AOT（コンパイラー10.0.12）の.app生成に成功。Info.plist、arm64ネイティブ実行ファイル、CoreCLR/アプリDLLが残らないこと、描画ライブラリ、元ロックからの管理対象依存の版/ハッシュ維持を確認。生成済み14,000 bytes/1,001行のテキストをCLIで開き、タブ/本文/UTF-8表示と通常終了0を確認した。全機能/性能・配布署名・別PC起動は未検証。
- Intel Mac向け：Apple Silicon上で `./build-mac.sh osx-x64` の実発行に成功。実行ファイルと同梱ライブラリにx86_64が含まれることを確認した。Intel実機での起動確認は残る。
- スクリプト単体：正常、CPU選択、空白/`!`を含むパス、別ディレクトリからの実行、不正RID/余分な引数/未知CPU/非Mac、復元/発行失敗、実行ファイル欠落、前回成果物維持、一時物の解放を7件で確認。
- Windows x64：Windows 2025 CIで実バッチのNative AOT発行、全EXE/DLLのAMD64/CLRヘッダー不在、元ロック不変、通常製品の可視ウィンドウ/正常終了0と生成入力SHA不変を確認。本文の表示内容/全機能・別PC・オフライン・性能は未検証。[証跡](validation-results/2026-11-06-windows-aot/README.md)。

### Windowsの実発行CI

[Windows Native AOT publish validation](../.github/workflows/publish-validation.yml)はWindows 2025上で実際の`build-windows.bat`を実行する。SDK・C++ツールチェーン・発行ログ・発行用ロック・全成果物のサイズ/SHA-256をActions artifact `windows-aot-publish`へ14日保存する。通常製品のまま、診断コードなし・AOT/トリミング警告をエラーとする設定で発行する。

`tools/check_windows_publish.py`は全EXE/DLLのAMD64 PE32+とCLRヘッダー不在、JIT用ランタイム/製品DLL不在を判定する。不正/切断ヘッダー・異なるCPU・管理対象へのフォールバック・欠落を含む単体6件を用意した。これはバイナリ形式の検査であり、AOTコンパイルの証拠には実発行ログも併用する。

`tools/check_windows_launch.ps1`は破棄されるWindows CI専用で、生成した日本語パスの小入力を通常EXEへ渡す。自身が起動したプロセスの可視ウィンドウ/非空client領域、読み込んだモジュール、通常Closeと終了0、入力SHA不変を確認し、失敗でもレポートを残す。OS操作そのものは実CI結合試験で検証する。本文の表示内容、物理描画・入力、起動性能、オフライン/SDKなしの別PC、全機能・文法・署名をこの起動確認から合格とはしない。

Pythonによるスクリプト試験（ビルド自体にPythonは不要）：

```sh
python3 -m unittest discover -s tools -p 'test_build_scripts.py' -v
```

Native AOT発行では、対象OS用のSkiaSharp、HarfBuzzSharp、Onigwrap、WindowsのANGLEについて、NuGetパッケージ内のライセンス・第三者通知を `licenses/` に同梱する。macOSでは `codeView.app/Contents/MacOS/licenses/` に入る。管理ライブラリ・文法・ランタイムの通知、署名、公証、別PCでの受け入れは最終配布に向けて確認する。

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

## GitHub Actionsでの通常試験

[Build and test](../.github/workflows/test.yml)はmainへのpush・main向けpull request・手動実行で動く。Windows Server 2025とmacOS 15のGitHubホステッドランナーで、global.json指定のSDKとPython 3.12を用意し、既存のtest-windows.bat / test-mac.shを実行する。SDK・実際のOS/アーキテクチャ・PythonとWindowsのpyランチャーの選択版をenvironment.txtへ記録する。各ランナーのCPU/OSイメージは更新されるため、性能の基準機として使わない。

Actionは確認したリリースのコミットSHAへ固定し、リポジトリの権限はcontents:read、checkoutの資格情報永続化は無効にする。片方のOSが失敗しても他方の試験は実行する。復元・ビルド・単体試験の失敗はジョブ失敗とし、失敗時も環境情報と生成済みTRXをtest-results-<OS>のArtifactへ14日間保存する。スキップ件数もTRXで確認する。後続pushで同じブランチの古い実行は取り消す。

対象は通常のReleaseビルド・.NET単体/I/O結合・Python補助ツール試験。カバレッジ/性能データ生成/GUI操作/Native AOT発行/別PC/署名/完全オフライン/長時間受入は含めない。CI成功をG0〜G6全体の完了や両OSの実機受入へ読み替えない。

[初回結果と証跡](validation-results/2026-11-06-ci-build/README.md)：Windowsは.NET627合格/3スキップ・Python5合格/20スキップ、macOSは.NET630/Python25合格。SDKは許容パッチの10.0.403を選択した。Windowsのpy -3はPATHのPython 3.12とは別に3.15.2を選ぶため、Pythonマイナー版を共通固定した試験とはしない。

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
