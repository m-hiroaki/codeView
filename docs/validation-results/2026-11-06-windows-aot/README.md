# Windows実バッチのNative AOT発行・起動

P6-03-01。`0fb1b5871b8fcc3639c0db77421895bad0d6bd63`の[発行CI](https://github.com/m-hiroaki/codeView/actions/runs/42191000330)で、既存の`build-windows.bat`をそのまま実行した。通常製品のRelease/win-x64/self-contained/Native AOT/trimmedであり、CodeViewPerformanceProbe=false、JITフォールバックなし、AOT/トリミングの警告エラー扱いを維持した。製品コード・依存版・公開API・アーキテクチャは変更していない。

## 結果

- Windows Server 2025（10.0.26100）、SDK 10.0.403、Native AOTコンパイラー10.0.14、Visual Studio Enterprise 2026（18.11.12328.437）。`build.log`に実復元・Generating native code・最終発行先を保存した。発行ログに警告/エラーはない。
- `codeView.exe`とネイティブDLL4個はAMD64 PE32+でCLRヘッダーなし。製品DLL/CoreCLR/CLR JIT/hostfxr/hostpolicy、deps/runtimeconfigは成果物にない。全8ファイルのサイズ/SHA-256を`manifest.json`へ保存した。EXEは25,207,808 bytes、全体は246,118,952 bytes（234.72 MiB）、そのうちPDB3個を除く合計は42.52 MiB。現行スクリプトはPDBを含めて出力する。容量の最終選定はP6-04に残す。
- 日本語パスの生成入力を通常EXEへ渡し、可視の`codeView`ウィンドウと1028×749のclient領域を確認した。生成元プロセスの読み込みモジュールにJITランタイムなし。自身のMainWindowへ通常Closeを要求し、終了0と入力バイトSHA-256不変を確認した。2秒の初期化待ちは計測値や本文表示の検証ではない。
- checked-in lockは実行前後のgit diffで不変。発行用ロックを保存し、元ロックの30依存すべてについてresolved/contentHash一致も照合した。RID/AOT追加依存は発行時に解決する現行方式であり、完全固定の最終リリースとはしない。
- [同コミットの通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42191000328)も両OS成功。macOSは.NET689/Python79合格、Windowsは.NET686合格+既存Darwin専用3スキップ、Python59合格+既存POSIX専用20スキップ。新しいバイナリ検査6件は両OSで成功した。起動スクリプトのOS API/Close操作は実Windows CIの結合試験で確認した。

## 証跡と範囲

GitHub artifact `13490161240`のZIP SHA-256は`e3938b218145427fa92439e9f3e6eed4b6f8b2b91248c0a0268782a2fe885c46`で、取得後にGitHub digestと照合した。ZIP全7ファイルをここへ保存し、入力の独立ハッシュとlaunchの前後ハッシュ、製品コミット、終了コードを再照合した。原ZIPは`artifacts/publish-ci/windows-aot-publish.zip`、Actions保存は14日。バイナリ本体はこの証跡artifactへ含めず、manifestで識別する。

`environment.txt`/`toolchain.json`、`build.log`/`publish.packages.lock.json`、`manifest.json`/`launch.json`/生成入力が原記録。`job.log.gz`/`normal-*.log.gz`は実ジョブログ、`ci.json`は実行IDと通常試験集計、`files-sha256.json`は保存物のSHA-256である。

**本文の表示内容・全機能/文法・物理描画/操作・性能・別PC/SDKなし/オフライン・署名/ライセンスは未検証。** 特に`libonigwrap.dll`の同梱は文法機能の実動作証明ではない。P6-03全体と配布/受入ゲートは未完了のまま維持する。
