# 文字素境界索引の初期ローカル検証

2026-11-06、macOS、基点a897b92からの独立部品追加。ソースdigestはsource-sha256.json。

- 対象テスト：13/13成功（grapheme.trx.gz）。
- 通常検証：.NET 766/766、Python 108/108成功（unit.trx.gz、full-test.log.gz）。
- 実行：`./test-mac.sh`。対象テストは`FullyQualifiedName~DocumentGraphemeIndexTests`で別実行。
- 最初の制限環境での実行はAvaloniaのユーザーログ書込み権限でビルド前に停止。通常の権限付き実行で上記を確認。製品やOS設定の変更なし。

[部品の範囲と残課題](../../grapheme-index-prototype.md)。Windows/公式適合データ/性能/描画の結果ではない。通常経路への接続と親タスクは未完了。
