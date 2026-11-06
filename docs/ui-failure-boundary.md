# UI操作失敗時の終了境界

MainWindowの同期コマンド（タブ切替/閉鎖、検索パネルの表示/取消/クリア、ツリー折りたたみ、ワークスペース閉鎖など）と既存の非同期操作は、未知例外で異常終了を記録し、通常のCloseへ進む。Treeの展開ボタンも選択更新・展開・フォーカス復帰の全体を同じ例外境界へ含める。

失敗時は次の順序を守る。

1. Program.MarkFailedで非0の終了状態を保持する。
2. Statusへ終了理由を通知する。通知購読側の例外は、すでに異常として記録しているため、Closeを妨げないようこの箇所で捕捉する。
3. Closeを要求し、既存のClosingによるStop・実Task待機・2秒の上限へ進む。Close自体の例外を成功扱いにする捕捉は追加しない。

従来のFileDragOverでは、異常データの後にStatus通知も失敗するとMarkFailed/Closeへ到達できなかった。既存RunAsyncでも通知例外がasync-voidイベントから再送出され、Closeの非同期停止待ちを終える前にプロセスが終了し得た。どちらも共通のReportUiFailureを使い、通知障害を終了境界から外へ漏らさない。

これはMainWindow内の小さな制御フロー修正であり、層・モジュール・依存方向・公開API・製品データ構造を変更しない。設定保存は引き続き無効のままとする。

## 検証

UiFailureTestsの7ケースで正常操作、同期例外/予期しない取消、一度だけの失敗通知、通知成功/例外/取消での順序とClose、Close自体の失敗伝播を確認する。ShutdownIntegrationTestsの追加2ケースは、実MainWindowViewModel/Operationsの本文とReadLeaseを持ち、取消不能I/Oを保留した状態でコマンド失敗を注入する。Status通知の成功/失敗の両方で、一度だけCloseへ進み、本文所有を解放し、Leaseを維持して実I/O終了を待つことを確認する。

```sh
dotnet test tests/codeView.Tests/codeView.Tests.csproj -c Release --filter 'FullyQualifiedName~UiFailureTests|FullyQualifiedName~ShutdownIntegrationTests' -p:PublishAot=false -p:CodeViewPerformanceProbe=false
./test-mac.sh
```

実GUI回帰はNative GUI validation、通常配布回帰はWindows Native AOT publish validationを使う。どちらもMainWindow.axaml.csの変更をpushしたときに実行する。

追加境界はコマンド操作時と異常時に限る。ファイル読取/検索/本文構築のループ、スクロール/描画/カーソル通知には処理を追加しない。通常GUI回帰で起動/本文接続/スクロール、読取専用操作、正常終了を確認するが、実機CPU/メモリや既存の起動予算未達をこの修正で解決したとは扱わない。

全UIの通知/DispatcherコールバックやOS入力の網羅、両OS実機の異常終了受入はP1-08に残る。通常のファイル読取失敗など期待されるOperationResultを異常終了へ変換する変更ではない。
