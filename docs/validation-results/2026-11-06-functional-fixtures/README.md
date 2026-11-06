# P0-05 固定試験データの確認

[生成手順](../../functional-fixtures.md)により30ファイルと6リンクを生成する。[manifest.json](manifest.json)はmacOSで実生成したバイト数・ハッシュ・期待値・ネイティブ結果。[local-result.json](local-result.json)は対象ソースのハッシュと通常試験結果を記録する。

macOSの通常試験は.NET 656件・Python 36件成功。FunctionalFixtureTestsが生成器の実出力30ファイルを製品のFileReader/文書/検索コアへ渡し、文字コード・不正バイト・行列・更新・読取前後ハッシュを検証した。UTF-16位置・検索は1/2/3/7/16384文字分割で一致した。CLIのネイティブ生成も終了0で、リンクcreated/実読取拒否verifiedを確認した。Windows SID抽出の追加検証後にPython一式/ネイティブCLIを再実行し、C#と生成バイトに変更はないため.NETの結果は0442de6の実行を保持した。

Windowsのネイティブリンク・SID/ACL処理と両OS CIは確認待ち。結果が揃うまでP0-05は未完了とする。C#宣言の正解データ整備をPhase 5探索機能の完成とは扱わず、実機GUI・性能・大容量データ整備も別タスクに残す。
