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

## macOS：自己完結型.app

macOS上で同じ.NET SDKを用意して実行する。

```sh
./build-mac.sh
```

実行環境のCPUから`osx-arm64`（Apple Silicon）または`osx-x64`（Intel）を選ぶ。明示指定もできる。

```sh
./build-mac.sh osx-arm64
./build-mac.sh osx-x64
```

出力先：`artifacts/publish/<RID>/codeView.app`

`.app`全体を使う。Mac版はトリミングなしの自己完結発行とし、Native AOTは指定しない。`build/Info.plist`と発行された全ファイルを組み合わせる。署名・公証・インストーラー・アーカイブ生成は行わない。Info.plistの製品名/識別子/版は同ファイルで管理する。配布時のGatekeeperと署名確認は別途必要である。

## 依存関係と生成物

最初に製品プロジェクトを`--locked-mode`で復元し、リポジトリ内の依存ロックとの整合性を確認する。その後、ロックを作業用ディレクトリへコピーし、RID・ランタイム・AOTツールを含む発行用の依存グラフを生成する。この復元だけは`RestoreLockedMode=false`とする。RIDなしの元ロックではRID付き発行がNU1004となるためである。リポジトリ内の`packages.lock.json`は書き換えない。

発行時のロックは`artifacts/publish/<RID>/publish.packages.lock.json`に残す。Native AOTコンパイラーなどSDKが追加する依存は発行時に解決するため、発行用ロックまで含めた完全固定のリリース工程とは区別する。初回復元にはNuGetへの通信が必要。

中間生成物は`artifacts/build/<RID>`、一時発行はその下の一意なステージへ出す。発行成功と実行ファイルの存在を確認してから、同じRIDの既存の生成済みアプリを置き換える。失敗時は非0の終了コードを返す。`artifacts`はGit対象外。設定や閲覧対象のソースを成果物へコピーしない。

## 確認した範囲

- Mac arm64：実スクリプトで自己完結型.app生成に成功。Info.plist、arm64 apphost、同梱ランタイム/描画ライブラリ、元ロックからの管理対象依存の版/ハッシュ維持を確認。今回の成果物のGUI起動・配布署名・別PC起動は未検証。
- Intel Mac：RID選択とパッケージングを模擬SDKで確認。実際のx64発行/起動は未検証。
- スクリプト単体：正常、CPU選択、空白/`!`を含むパス、別ディレクトリからの実行、不正RID/余分な引数/未知CPU/非Mac、復元/発行失敗、実行ファイル欠落、前回成果物維持、一時物の解放を7件で確認。
- Windows：バッチの実行・Native AOTコンパイル・起動は未検証。MacからのWindows AOT発行を代替検証にしない。

Pythonによるスクリプト試験（ビルド自体にPythonは不要）：

```sh
python3 -m unittest discover -s tools -p 'test_build_scripts.py' -v
```

ライセンス同梱・署名/公証・両OS別PC・オフライン・性能・全機能の受入を含む最終配布ゲートは引き続き未完了。
