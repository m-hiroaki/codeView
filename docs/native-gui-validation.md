# ネイティブGUIの起動検証

P1-09の取得確認と、後続のP1-07の実操作検証に使う。[Native GUI validation](../.github/workflows/gui-validation.yml)は通常試験から独立し、手動実行または収集器/GUI診断ソース変更のmainへのpushで起動する。製品の通常ビルドや依存構成は変更せず、既存の明示`CodeViewPerformanceProbe=true`だけを使用する。

## 対象と判定

ホスト型windows-2025で、Avaloniaの通常UsePlatformDetectによる実ウィンドウを起動する。ヘッドレス化や別レンダラーへの変更は行わない。空画面と小さなUTF-8日本語/絵文字/CRLFファイルを交互に各10回、独立プロセスで実行する。指定ファイルは独立したUTF-16文字数/CRLF行数と照合し、入力SHA-256の不変を毎回確認する。各起動の前に小さな入力をハッシュしており、ファイル条件はWarmである。

`tools/measure_gui_startup.py`はプロセス終了、製品コミット、失敗/割込なし、通常の全文表示候補、正のクライアント領域/scaling、Openedとアニメーション通知の順序を確認する。ファイルありでは本文接続・2tick・64回のScrollTo/EnsureVisualLines・64通知を確認する。読取専用、テキストドロップ無効、Undo/Redo履歴なしも実コントロールの値から確認する。これらは入力を試みる試験ではなく、入力/貼付/Undo/ドロップ操作の受入は別途実施する。

60秒で終了しないプロセス、非0終了、結果欠落、別ビルド、不整合は失敗させる。Pythonのsubprocess.runがtimeout時に子を終了/待機し、同じ試行を自動再実行しない。途中のruns.jsonとログを保存し、既存出力を上書きしない。正常/異常/境界は収集器の単体試験で確認し、模擬レポートを実GUIの証跡に混ぜない。

## 指標の意味

| 記録 | 対象と制限 |
| --- | --- |
| OpenedMilliseconds | マネージドMain入口→Window.Opened。OSのプロセス生成/ランタイム起動は含まない |
| WindowTwoTicksMilliseconds | Opened後に予約した2回のRequestAnimationFrame通知への到達。物理描画完了ではない |
| DocumentAttachmentMilliseconds | 既存診断の同期本文接続区間。遅延レイアウト全体とは扱わない |
| DocumentTwoTicksMilliseconds | 本文接続後の2通知到達。実キーを受け付ける時刻の証明ではない |
| ScrollSynchronousMilliseconds / AnimationTickIntervalsMilliseconds | 64回の明示スクロール同期処理/通知間隔。物理スクロールやフレーム提示の計測ではない |
| wholeProcessSeconds | Pythonのプロセス生成→終了待機。64回のスクロールと終了処理も含むため起動時間として使わない |

定義は[既存GUI計測条件](performance-plan.md#6-ネイティブguiの代理指標の測定)に従う。ホスト型CIは基準PCではなく、表示アダプターの情報が取れても物理60 Hz/Retina/入力応答を受け入れたことにしない。1秒の実起動目標、通常配布物、両OS10回、macOS、GUIの実操作、G1の合格は別途検証する。

## 手元での実行例

- global.jsonに従う.NET SDK。
- Python 3。
- 実ウィンドウを作成できるWindowsのデスクトップセッション。
- 1 MiB以下の厳格UTF-8の検証用ファイル。既存ソースを編集せず生成データを使う。

PowerShellでリポジトリのルートから実行する。結果フォルダは新しい名前を使う。

```powershell
dotnet restore codeView.slnx --locked-mode -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
dotnet build src/codeView/codeView.csproj -c Release --no-restore -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=true --output artifacts/gui-validation/app -m:1 -nr:false
python tools/measure_gui_startup.py --app artifacts/gui-validation/app/codeView.exe --input artifacts/gui-validation/起動確認.cs --output artifacts/gui-validation/new-startup --rounds 10 --commit (git rev-parse HEAD) --os windows
```

CIは環境、生成入力、20試行のJSON/ログ、SHA-256一覧をartifactへ14日保管する。製品DLL/apphostのハッシュはruns.json、製品コミットは各GUIレポートにも残す。期間内に原記録を取得・照合して記録へ保存する。失敗した環境を黙って別方式へ置き換えて合格にしない。

[Windowsの初回取得結果](validation-results/2026-11-06-native-startup/README.md)は20試行すべて取得できたが、Main→Openedは全試行1秒超、同期本文接続も1回100 ms超だった。取得成功を性能目標達成とは扱わない。
