# Markdown表示の検証（2026-11-27）

macOS 27.0.1 / arm64 / .NET SDK 10.0.403。Avalonia 12.1.4、MarkView関連12.4.1、ローカルPlantUML 1.2026.1（Java 17）で確認した。

- Avaloniaのみ更新した段階で、1,302件の単体試験が合格し、macOS arm64のNative AOT発行が成功した。
- 機能追加後、1,317件合格、26件スキップ、失敗0件。[単体試験ログ](unit.log.gz)
- macOS arm64の配布物を起動し、Markdown見出し、C#コードの構文色、Mermaidのフローチャート、PlantUMLのシーケンス図、相対画像を画面で確認した。実行中にPlantUMLのJARを明示指定した。[配布版の画像](macos-aot.png)
- Debugの実ウィンドウでは同じ描画に加え、原文への切替、ファイル内検索での原文への復帰、通常ファイルとの切替、再読込を確認した。[GUIログ](gui.log)
- 採用したMermaid拡張のエラー分岐が図のソースを表示することを、[固定版の実装](https://github.com/Kryptos-FR/MarkView.Avalonia/blob/790cbc20145c6ceb52b25b3d98acd1f163e6b152/src/MarkView.Avalonia.Mermaid/MermaidBlockRenderer.cs#L212-L225)で確認した。
- Windowsの配布検証ワークフローにMarkdownの見出しをUI Automationで検出する確認を追加した。ローカルVMでの確認は下記に記載する。

## 長文の表示

見出しとインライン装飾を反復した文書を、同じDebugウィンドウで別のMarkdownタブを開いた後に測った。ファイルを開く要求から、プレビューのScrollViewerにスクロール範囲ができるまでを測定した。OSの物理描画完了時間ではない。

| 入力 | プレビューの初回表示 | スクロール20回の最大ディスパッチ時間 | プロセスRSS差 |
| --- | ---: | ---: | ---: |
| 425,920 UTF-16コード単位（旧上限で測定） | 1,233.7 ms | 0.9 ms | +157.6 MiB |
| 127,719 UTF-16コード単位（現上限付近） | 617.3 ms | 0.6 ms | +49.0 MiB |

スクロールはプログラムからオフセットを設定した結果で、入力遅延やGPU時間は含まない。RSSはGCの状態と他の表示タブを含むため、専用メモリやピークの値ではない。自動プレビューを128 Ki UTF-16単位までとし、超える文書は原文で開く。

Native AOTのmacOSペイロードは、同梱の言語サーバーを追加する前のサイズで132,160,221バイトから194,613,177バイトになった（約59.6 MiB増）。Mermaid拡張の依存関係によりSkiaSharpが3.119.4から4.153.1へ変わり、SVG関連ライブラリも追加された。

## 通常ソースの起動・描画への影響

同一Mac上のRelease GUIプローブで、Avalonia 12.1.4のみ更新した状態とMarkdown機能追加後を比較した。両方に括弧ガイドを含む。空起動と38 UTF-16単位のC#ファイルを各5回測った。各行はC#ファイルの5試行の中央値。

| 指標 | Avalonia更新のみ | Markdown追加後 |
| --- | ---: | ---: |
| ウィンドウのOpened | 421.5 ms | 409.3 ms |
| 文書接続後の第2アニメーションコールバック | 558.7 ms | 547.2 ms |
| プロセスCPU時間 | 969.0 ms | 990.1 ms |
| サンプリングしたピークの物理フットプリント | 250.8 MiB | 255.5 MiB |
| 各起動のスクロール同期処理64回の最大値 | 2.22 ms | 2.31 ms |

この条件では起動の遅延は観測されず、CPU時間とメモリは小幅増えた。少数試行の小ファイル比較で、大容量ソース・長時間のCPU使用率・プロジェクト検索速度・Native AOTの起動差は別途確認が必要。検索ロジック自体に変更はなく、原文への復帰を実ウィンドウで確認した。

[ベースライン生データ](baseline-startup.json.gz)・[Markdown追加後の生データ](candidate-startup.json.gz)

## Windows配布版の検証用文書

CIの見出し検出と実機の目視確認には、同じ[Markdown文書](../../../tests/Fixtures/MarkdownPreview.md)を使う。Native AOTの配布版で開き、見出しと太字、C#のキーワードと型の構文色、Mermaidのフローチャートを確認する。PlantUMLのJARを設定した起動では「AliceからBob」のシーケンス図を確認する。最後に「表示 → ソースを表示」でコードフェンスと行番号が現れ、もう一度切り替えると図が戻ることを確認する。

2026-11-27に、VMware Fusion上のWindows 11 ARM（ビルド26300）で `build-windows.bat` を実行した。.NET SDK 10.0.404とVisual Studio 2022 Build ToolsでNative AOT発行が成功した。生成物はPEマシン値 `0x8664` のx64実行ファイルで、Windowsのx64エミュレーションで起動した。[ビルドログ](windows-build.log)・[起動ログとSHA-256](windows-launch.log)

検証用Markdownが整形済みで開き、太字、C#の構文色、Mermaidのフローチャート、ローカルPlantUML 1.2026.1とMicrosoft OpenJDK 21で描画されたシーケンス図を画面で確認した。Ctrl+Fでファイル内検索を開くと、同じタブがコードフェンスと行番号のある原文表示に切り替わることも確認した。表示メニューでの往復はmacOSのGUI試験で確認した。Windowsのx64物理機とActionsワークフロー自体は今回の試験範囲外。
