# codeView

Windows / macOS向けの、コードリーディングに特化したソースコードビューア。
読み取り専用で、大量のコードを快適に閲覧できる速度・応答性・メモリ効率を重視します。

現在は開発途中です。ファイルツリー、複数タブ、文字コード指定、再読込、フォルダ横断検索、参照検索（名前一致）を実装しています。構文ハイライト、ファイル内検索、定義ジャンプ、大容量表示の性能評価、配布物の受け入れは未完了です。[実装状況](docs/implementation-plan.md)・[検証記録](docs/development-log.md)

## 必要なツール・SDK

- .NET SDK 10.0.403（許容パッチ版は[global.json](global.json)に従う）
- Python 3（通常テスト・カバレッジ計測用。macOSは`python3`、Windowsは`py`ランチャー）
- Xcode Command Line ToolsまたはXcode（macOSのNative AOT発行用）
- Visual Studio 2022以降または対応するBuild Toolsの「C++によるデスクトップ開発」（WindowsのNative AOT発行用。MSVC・Windows SDKを含む）
- VS CodeとMicrosoft C#拡張機能 `ms-dotnettools.csharp`（[VS Codeでデバッグする場合](docs/build.md#vs-codeでのデバッグ)）

## ビルド・起動

リポジトリのルートで実行します。

```sh
dotnet restore codeView.slnx --locked-mode
dotnet build codeView.slnx -c Release --no-restore
dotnet run --project src/codeView/codeView.csproj --no-restore
```

## テスト

### macOS

```sh
./test-mac.sh
```

### Windows

```bat
test-windows.bat
```

## カバレッジ計測・HTMLレポート

### macOS

```sh
./test-mac.sh --coverage
open artifacts/test-results/macos/coverage/html/index.html
```

### Windows

```bat
test-windows.bat --coverage
start "" "artifacts\test-results\windows\coverage\html\index.html"
```

## 性能試験（[データ準備](docs/performance-plan.md)）

### macOS

```sh
./test-mac.sh --performance
```

### Windows

```bat
test-windows.bat --performance
```

## 参照検索（名前一致）

フォルダとファイルを開き、識別子を選択するかCaretを置いてShift+F12、または「検索 → 参照を検索（名前一致）」を実行します。大小文字を区別した単語一致で、宣言・コメント・文字列も対象です。結果はEnterまたはダブルクリックで移動します。意味解析による参照検索ではありません。[対応範囲](docs/detailed-design/06-navigation.md#61-参照検索uiの接続2026-11-07)。

## Native AOT発行

### macOS

```sh
./build-mac.sh
```

### Windows

```bat
build-windows.bat
```

## ドキュメント

- [ビルド・テスト・カバレッジ・デバッグの詳細](docs/build.md)
- [要件定義](docs/requirements.md)
- [基本設計](docs/basic-design.md)
- [アーキテクチャ設計](docs/architecture.md)
- [詳細設計](docs/detailed-design/README.md)
- [実装計画](docs/implementation-plan.md)
- [実装・検証記録](docs/development-log.md)
- [性能評価条件](docs/performance-plan.md)
- [外観・操作モック](docs/mockup/README.md)
- [開発ルール](AGENTS.md)
