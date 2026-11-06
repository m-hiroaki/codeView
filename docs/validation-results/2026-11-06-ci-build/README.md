# Windows/macOSのReleaseビルドと通常単体試験

[初回CI](https://github.com/m-hiroaki/codeView/actions/runs/42162663862)はコミット`8efffa1b5ebfd7ce46219aa3f4e00bfcbceb2c96`で両ジョブ成功。mainへのpushを契機に、既存のtest-windows.bat / test-mac.shを実行した。ジョブと各ステップ・Artifact識別は[run.json](run.json)。

## 確認した環境と結果

| 項目 | Windows | macOS |
| --- | --- | --- |
| Runner | windows-2025 | macos-15 |
| OS/アーキテクチャ | Windows 10.0.26100 / X64 | Mac OS X 15.8 / ARM64 |
| 実際に選択されたSDK | 10.0.403 | 10.0.403 |
| Runtime host | 10.0.14 | 10.0.14 |
| PATHのPython | 3.12.10 | 3.12.10 |
| スクリプトが使うPython | py -3で3.15.2 | python3で3.12.10 |
| .NET通常単体 | 627合格・3スキップ・失敗0 | 630合格・スキップ0・失敗0 |
| Python補助試験 | 5合格・20スキップ・失敗0 | 25合格・スキップ0・失敗0 |

SDKはglobal.jsonの10.0.401/rollForward=latestPatchの許容範囲で10.0.403となった。10.0.401そのものによる両OS試験ではない。NuGetはlocked-modeで復元し、Releaseで本体と試験をビルドしてからNUnitを実行した。テスト用SDK/文法ライブラリ等の依存を、今回のCIのために更新していない。

Pythonのsetupは3.12だが、既存Windowsスクリプトのpy -3はランナーにある新しい3.15.2を選んだ。Pythonのパッチ/マイナーまで固定した試験とはせず、実際の選択版を環境情報へ記録した。通常スクリプトの前提はPython 3であり、両方で補助データ生成試験を通した。

## スキップの扱い

Windowsの.NETでスキップされたのは次のDarwin専用試験3件。TRXの個々の結果を読み、Passed以外のNotExecutedをスキップとして数えた。TRX全体のCounters.notExecutedは0のため、その値だけでスキップなしと判断しない。

- MacPermissionFailureIsReportedAndSiblingFilesRemainWalkable：macOSの非root利用者での実際のアクセス拒否。
- MacFifoSettingsCannotBlockStartup：DarwinのFIFO設定ファイル。
- MacFifoIsRejectedWithoutWaitingForAWriter：Darwinの特殊ファイル。

WindowsのPython20件はMacのビルド/テストシェルの模擬試験で、既存のOS条件によりスキップした。Windowsバッチの通常経路そのものは今回のWindowsジョブで実行した。スキップされたDarwin試験はmacOSジョブで合格。Windows固有のアクセス拒否/特殊ファイル受入やバッチの異常系すべてを完了とするものではない。

## 保存した証跡

- [Windows環境](windows/environment.txt)・[全630件の結果](windows/test-results.json)・[ジョブログ](windows/job.log)
- [macOS環境](macos/environment.txt)・[全630件の結果](macos/test-results.json)・[ジョブログ](macos/job.log)
- [依存パッケージのライセンス確認値](package-licenses.json)：固定版の手元NuGet nuspecから記録。

Artifactを接続ツールで取得し、environment.txtとTRXを読んだ。JSONは個々の試験名/結果/所要時間・スキップ理由、元TRX/ZIPのSHA-256を保存したもの。環境/ログの改行と行末空白は保存時に整形した。元Artifactは14日で失効するため、環境・試験結果・ログの必要範囲をここへ保存した。ジョブ成功・復元/コンパイル/試験の各ステップ成功もrun.jsonで確認した。

## 達成範囲と残る作業

P1-01の製品1/試験1プロジェクト・SDK選択方針/依存ロック・両OSのReleaseビルド/通常試験手順を確認した。P0-03のSDK/UI/試験ランナーの共通ビルド確認にも使う。SDK/UI候補の確認と、AvaloniaEdit/TextMateの性能を含む最終採否は分ける。

P0-03の版/ライセンスの判断とD-01に残る採否は[設計07の5.1](../../detailed-design/07-settings-distribution.md#51-p0-03のsdkui試験ランナー確認2026-11-06)へ対応付けた。SDK/UI候補の初期確認を、配布物の全ライセンス監査/通知同梱の完了にはしない。

ホステッドランナーは性能基準機や別PCコピー起動の受入環境ではない。表示周波数/物理描画、Windows/macOSの実GUI・IME・ドロップ、Native AOT、署名/公証、オフライン、長時間、性能/CPU使用率・メモリ予算は今回未評価。P0-02/P0-04/P1-09/P2〜P6の条件とG0〜G6は未完了のまま残す。
