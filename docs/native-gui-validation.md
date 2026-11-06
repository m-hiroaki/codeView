# ネイティブGUIの起動検証

P1-09の取得確認と、後続のP1-07の実操作検証に使う。[Native GUI validation](../.github/workflows/gui-validation.yml)は通常試験から独立し、手動実行または収集器/GUI診断ソース変更のmainへのpushで起動する。製品の通常ビルドや依存構成は変更せず、既存の明示`CodeViewPerformanceProbe=true`だけを使用する。

## 対象と判定

ホスト型windows-2025/macOS-15で、Avaloniaの通常UsePlatformDetectによる実ウィンドウを起動する。ヘッドレス化や別レンダラーへの変更は行わない。空画面と小さなUTF-8日本語/絵文字/CRLFファイルを交互に各10回、独立プロセスで実行する。指定ファイルは独立したUTF-16文字数/CRLF行数と照合し、入力SHA-256の不変を毎回確認する。各起動の前に小さな入力をハッシュしており、ファイル条件はWarmである。

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

定義は[既存GUI計測条件](performance-plan.md#6-ネイティブguiの代理指標の測定)に従う。ホスト型CIは基準PCではなく、表示アダプターの情報が取れても物理60 Hz/Retina/入力応答を受け入れたことにしない。1秒の実起動目標、通常配布物、両OS実機10回、GUIの実操作、G1の合格は別途検証する。

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

## macOSの取得

macOSジョブも同じ収集器/通常UsePlatformDetect/全文表示候補を使い、明示診断ビルドのapphostを直接起動する。.app配布物の受入ではない。OS/CPU/メモリ/表示情報を保存し、MacのレポートをWindowsと誤認しないことも単体で検証する。ネイティブウィンドウを作成できない場合はそのジョブを失敗させ、別方式のレポートへ置き換えない。ローカルのログイン/ロック/権限を変更する処理はない。

Mac側は起動/本文接続/64スクロール通知/終了だけを測定し、Windows専用のClipboard操作試験は実行しない。GUIコールバックは実OS上での結合試験、収集器の正常/異常/境界は既存8件の単体試験で検証する。

```sh
dotnet restore codeView.slnx --locked-mode -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
dotnet build src/codeView/codeView.csproj -c Release --no-restore -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=true --output artifacts/gui-validation/app -m:1 -nr:false
python3 tools/measure_gui_startup.py --app artifacts/gui-validation/app/codeView --input artifacts/gui-validation/起動確認.cs --output artifacts/gui-validation/new-startup --rounds 10 --commit "$(git rev-parse HEAD)" --os macos
```

## 読み取り専用のコントロール結合試験

同じ明示診断ビルドを、別プロセスの`CODEVIEW_GUI_READONLY_CHECKS=1`で実行する。自動操作は`GITHUB_ACTIONS=true`の専用CIセッションだけで許可し、通常起動や手元の利用者のクリップボードへ適用しない。専用セッションのClipboardへ生成文字列を書き、試験後に消去する。

`GuiReadOnlyProbe`は通常の製品エディターへRouted TextInput/DropとコントロールのPaste/Delete/Cut/Undo/Redoを適用する。先頭・EOF・全選択の各8操作で、全文のUTF-16 SHA-256、文書同一性、タブ、Undo/Redo不変を確認する。非空文書はCopyからネイティブClipboardへ出した文字列も往復照合する。空文書のCopyは対象文字なしとして分ける。入力例は日本語/絵文字/CRLF、空、タブ/Unicodeを含む横に長い小ファイルの3条件で、各3プロセス実行する。

先に別の編集可能なネイティブWindow/TextEditorで、同じRouted入力/Drop、ネイティブClipboard貼付、Delete、Undo/Redoによって本文が実際に変わることを確認する。PasteはTextPasted完了通知、CopyはClipboardの実値を期限付きで待ち、Main側へ戻ってフォーカスも確認する。対照が機能しない・行列欠落・不整合・期限切れは非0終了し、途中までのJSON/ログを残す。入力ファイルのバイトSHAも前後で一致する必要がある。

この試験はネイティブUI/Clipboardを使うコントロール結合試験であり、OSのキー/マウスやIME、Explorer/Finderのドラッグ操作を注入したものではない。物理操作・メニューショートカット、macOS、ロード/失敗状態の包括受入、実配布物とP1-07全体は別途確認する。通常製品へ試験コード/タイマー/書込経路を追加しない。UI依存処理はこの実機構成で結合検証し、通常単体ではレポートの正常/異常/空/境界・欠落・対照の無効・期限と非CI拒否を検証する。

CanDeleteの値だけで削除可能と判断しない。固定版AvaloniaEditは文書があればCanDelete=trueとし、実行時の読取専用範囲で保護する。試験では値を保存して実際にDeleteを呼び、その後の原文ハッシュ/履歴を判定する。初回のCIはfalseを要求した試験側の仮定で停止したため、製品を変えずこの判定を是正した。

[Windowsの検証結果](validation-results/2026-11-06-readonly-ui/README.md)では、9プロセス/216操作の不変性、9回の有効な編集対照、6回のNative Copy往復、空文書の区別、全プロセスの正常終了を確認した。物理操作やP1-07全体の受入へ読み替えない。
