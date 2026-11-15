# codeView

<img src="docs/codeView-icon.png" width="96" height="96" alt="codeView アプリアイコン">

Windows / macOS向けの、コードリーディングに特化したソースコードビューア。
読み取り専用で、大量のコードを快適に閲覧できる速度・応答性・メモリ効率を重視します。

現在は開発途中です。ファイルツリー、複数タブ、文字コード指定、再読込、対応拡張子の構文ハイライト、フォルダ横断検索、ファイル内検索、簡易定義ジャンプ、参照検索（名前一致）を実装しています。大容量表示の性能評価、配布物の受け入れは未完了です。[実装状況](docs/implementation-plan.md)・[検証記録](docs/development-log.md)

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

## パネルの表示とサイズ

「表示」メニューでファイルツリーと検索結果を切り替えます。ツリーの右端と検索結果の上端をドラッグしてサイズを調整できます。パネルを再表示すると、調整したサイズに戻ります。

## 除外フォルダの設定

「ファイル」メニューの「除外フォルダの設定」でフォルダ名を1行に1つ入力し、保存します。再起動後にファイルツリーとフォルダ検索に適用されます。

## ファイル内検索

Ctrl+F（macOSはCmd+F）または「検索 → ファイル内を検索」で開きます。Enterで次へ、Shift+Enterで前へ移動し、Escapeで閉じます。検索語と条件はタブごとに保持します。通常の文字列検索、大小文字の区別、単語単位、正規表現に対応します。件数が「少なくとも」と表示されているときも、次・前で保持範囲の外へ移動できます。[検索仕様](docs/detailed-design/05-search.md#74-ファイル内検索の実装)

ファイル内検索とフォルダ検索は、検索語を16,384 UTF-16単位まで受け付けます。正規表現を選ぶと行長とタイムアウトの制限を表示します。

## 定義へジャンプ（簡易）

フォルダとC#、C、C++のファイルを開き、識別子にカーソルを置くか選択してF12を押します。「検索 → 定義へジャンプ（簡易）」からも実行できます。大小文字を区別して名前を照合し、全ファイルを評価できて候補が1件なら直接移動します。複数候補や部分結果では、確定した候補をEnterまたはダブルクリックで選べます。[対応範囲](docs/detailed-design/06-navigation.md#51-候補一覧と移動の実装)

## 参照検索（名前一致）

フォルダとファイルを開き、識別子を選択するかCaretを置いてShift+F12、または「検索 → 参照を検索（名前一致）」を実行します。大小文字を区別した単語一致で、宣言・コメント・文字列も対象です。結果はEnterまたはダブルクリックで移動します。意味解析による参照検索ではありません。[対応範囲](docs/detailed-design/06-navigation.md#61-参照検索uiの接続2026-11-07)。

## 定義ジャンプ・参照検索（意味解析）

信頼できる作業フォルダを開き、「検索 → 意味解析の設定…」で、使う言語のサーバー実行ファイルを指定します。

- **C#** は Roslyn Language Server と、解析対象のプロジェクトに対応する .NET SDK を用意します。必要に応じて `.sln`、`.slnx`、`.csproj` を指定します。
- **C/C++** は clangd と、対象構成で生成した `compile_commands.json` を用意します。ファイルが作業フォルダの直下か `build` にあれば、自動で探します。

有効にした言語では、識別子にカーソルを置いて **F12** で定義へ、**Shift+F12** で参照を検索します。初回はプロジェクトの読込や索引作成に時間がかかることがあります。設定ダイアログの「簡易検索に戻す」で切り替えられます。設定は現在の作業フォルダを開いている間だけ有効です。[解析範囲と制限](docs/detailed-design/06-navigation.md#8-意味解析による定義ジャンプ参照検索)

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
