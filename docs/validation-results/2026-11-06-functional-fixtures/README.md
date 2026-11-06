# P0-05 固定試験データの確認

[生成手順](../../functional-fixtures.md)により30ファイルと6リンクを生成する。[manifest.json](manifest.json)はmacOSで実生成したバイト数・ハッシュ・期待値・ネイティブ結果。[local-result.json](local-result.json)は対象ソースのハッシュと通常試験結果を記録する。

macOSの通常試験は.NET 656件・Python 37件成功。FunctionalFixtureTestsが生成器の実出力30ファイルを製品のFileReader/文書/検索コアへ渡し、文字コード・不正バイト・行列・更新・読取前後ハッシュを検証した。UTF-16位置・検索は1/2/3/7/16384文字分割で一致した。CLIのネイティブ生成も終了0で、リンクcreated/実読取拒否verifiedを確認した。SID列検証と相対リンクの修正後にPython一式/ネイティブCLIを再実行し、C#とportable生成バイトに変更はないため.NETの結果は0442de6の実行を保持した。

最初の[CI 42166507922](https://github.com/m-hiroaki/codeView/actions/runs/42166507922)ではmacOS成功、Windowsのネイティブリンク読取がErrno 22で失敗した。[失敗箇所](initial-windows-failure.log)を保存し、相対リンク先をOSのPathへ変換する修正と、リンクが作れても読めなければ準備済みにしない生成時検査を加えた。成功へ読み替えず、修正後の両OS CIを再確認する。

Windowsのネイティブリンク・SID/ACL処理と両OS CIは確認待ち。結果が揃うまでP0-05は未完了とする。C#宣言の正解データ整備をPhase 5探索機能の完成とは扱わず、実機GUI・性能・大容量データ整備も別タスクに残す。
