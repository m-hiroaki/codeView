# C#逐次字句走査の初期検証

2026-11-07、macOS、SDK 10.0.403／.NET 10.0.14。基点b1a9f8dにCSharpLexerとCSharpFileLexerを追加。実装・テストはsource-sha256.jsonで照合できる。

- 対象76件（字句67・実ファイル9）すべて成功。コメント、文字/通常/逐語/raw/補間リテラル、全固定チャンク幅、UTF-16原文位置、未完/非対応/4,096単位/1,024深さ、取消/失敗/再利用を確認。
- UTF-8・UTF-16LE・CP932の実ファイルをFileReaderで読み、BOM/16 Ki読取境界と原文位置、入力バイト不変を確認。Stampはテスト用ISourceAccessで変更を注入し、EOFの最後のトークンと当該ファイルを確定しないことを確認。
- `./test-mac.sh`：.NET 891/891、Python 112/112成功。最初の対象試験ではテスト属性内の不正サロゲートが置換文字になって2件失敗。コード単位から実行時に組み立てる試験へ直し、実際の不正UTF-16を確認した。
- 補助スモーク：`src/codeView`・`tests/codeView.Tests`・`tests/GuiProbe`の`.cs`（bin/obj除外）を701 UTF-16単位ずつ入力し、151ファイル／1,007,918単位／196,840仮トークン、字句失敗0。`corpus-harness.cs.txt`は製品のRelease DLLを参照する一時コンソール（AssemblyName=codeView.Tests）で実行した原コード。補助スモークはソースの事前読込を含み、製品の性能測定や宣言精度の証明ではない。

[対応範囲と限界](../../detailed-design/06-navigation.md#31-逐次字句走査の実装2026-11-07)。候補抽出・検索イベント/画面接続・正式な性能受入は後続。
