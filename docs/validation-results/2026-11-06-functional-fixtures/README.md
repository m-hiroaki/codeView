# P0-05 固定試験データの確認

[生成手順](../../functional-fixtures.md)により30ファイルと6リンクを生成する。[manifest.json](manifest.json)はmacOSで実生成したバイト数・ハッシュ・期待値・ネイティブ結果。[local-result.json](local-result.json)は対象ソースのハッシュと通常試験結果を記録する。

macOSの通常試験は.NET 656件・Python 37件成功。FunctionalFixtureTestsが生成器の実出力30ファイルを製品のFileReader/文書/検索コアへ渡し、文字コード・不正バイト・行列・更新・読取前後ハッシュを検証した。UTF-16位置・検索は1/2/3/7/16384文字分割で一致した。CLIのネイティブ生成も終了0で、リンクcreated/実読取拒否verifiedを確認した。SID列検証と相対リンクの修正後にPython一式/ネイティブCLIを再実行し、C#とportable生成バイトに変更はないため.NETの結果は0442de6の実行を保持した。

最初の[CI 42166507922](https://github.com/m-hiroaki/codeView/actions/runs/42166507922)ではmacOS成功、Windowsのネイティブリンク読取がErrno 22で失敗した。[失敗箇所](initial-windows-failure.log)を保存し、相対リンク先をOSのPathへ変換する修正と、リンクが作れても読めなければ準備済みにしない生成時検査を加えた。成功へ読み替えず、修正後の両OS CIを再確認する。

## 修正後の両OS確認とP0-05完了

91097807cc43ee8402d0f1e1fd57dd84311c0c2dの[CI 42166897023](https://github.com/m-hiroaki/codeView/actions/runs/42166897023)は両OSとも成功した。[ci-run.json](ci-run.json)は実行・ジョブ・成果物の識別、[ci-results.json](ci-results.json)はTRXの集計、新規製品照合テストの結果、スキップ名とZIP/TRXのSHA-256を保存する。ダウンロードZIPはGitHubのdigestと一致することを検証した。

| OS | .NET | Python | 新しい固定データ試験 |
| --- | --- | --- | --- |
| macOS 15.8 ARM64 | 656成功、スキップ0 | 37成功 | 製品照合・実リンク・読取拒否/解除すべて成功 |
| Windows 10.0.26100 X64 | 653成功、3スキップ | 17成功、20スキップ | 製品照合・実リンク・読取拒否/解除すべて成功 |

両OSともSDK 10.0.403・ランタイム10.0.14。PythonはmacOS 3.12.10、Windowsのpy -3は3.15.2を使った。環境と全Python結果抜粋は[macOS環境](macos/environment.txt)・[試験](macos/test.log)、[Windows環境](windows/environment.txt)・[試験](windows/test.log)を参照する。

Windowsの.NETスキップは既存のMac権限/FIFO本文/FIFO設定の3件。Pythonの20スキップも既存のPOSIX/Macビルド/ランナーモック試験で、新規12件はすべて実行・成功した。作れないネイティブケースをportable成功に置換していない。

以上でP0-05のデータ整備を完了とする。C#宣言の正解データ整備をPhase 5探索機能の完成とは扱わず、実機GUI・性能・大容量データ整備も別タスクに残す。製品のレイヤ・公開API・本文データ構造・実行時処理は変更していない。
