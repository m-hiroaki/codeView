# codeView

<img src="docs/codeView-icon.png" width="96" height="96" alt="codeView アプリアイコン">

Windows / macOS向けの、コードリーディングに特化したソースコードビューア。
読み取り専用で、大量のコードを快適に閲覧できる速度・応答性・メモリ効率を重視します。

現在は開発途中です。大容量表示の性能評価と配布物の受け入れを進めています。[実装計画](docs/implementation-plan.md)・[検証記録](docs/development-log.md)

## 機能一覧

| 機能名 | 概要 | ショートカット |
| --- | --- | --- |
| ファイルを開く | ファイル選択ダイアログやドラッグ＆ドロップから、単体または複数のファイルを開きます。 | Windows `Ctrl+O` ／ macOS `Cmd+O` |
| フォルダを開く | 作業フォルダを開き、左側のファイルツリーとフォルダ横断検索を利用します。フォルダはドラッグ＆ドロップでも開けます。 | — |
| ファイルツリー | フォルダを展開し、言語アイコン付きのファイルを選んで開けます。「表示」メニューで開閉し、右端で幅を調整できます。 | ツリー内で `←`／`→`、`Enter` で開く、`F5` で再読込 |
| タブ | 複数のファイルを開き、タブごとにカーソルやスクロール位置を保持します。タブがない状態で閉じる操作をするとアプリを終了します。 | 次へ `Ctrl+Tab`、前へ `Ctrl+Shift+Tab`（両OS）。閉じる Windows `Ctrl+W` ／ macOS `Cmd+W` |
| ソースコード閲覧 | 読み取り専用の本文に行番号と現在行を表示し、対応言語は構文色分けします。原文を選択してコピーできます。 | コピー Windows `Ctrl+C` ／ macOS `Cmd+C` |
| 再読込・文字コード指定 | 開いているファイルを読み直せます。自動判定のほか、UTF-8、UTF-16、UTF-32、Shift_JIS（CP932）を指定できます。 | — |
| ファイル内検索 | 検索語と条件をタブごとに保持します。文字列、大小文字の区別、単語単位、正規表現に対応します。[検索仕様](docs/detailed-design/05-search.md#74-ファイル内検索の実装) | Windows `Ctrl+F` ／ macOS `Cmd+F`。次へ `Enter`、前へ `Shift+Enter`、閉じる `Esc` |
| フォルダ横断検索 | 作業フォルダ内のテキストを検索します。大小文字の区別、単語単位、正規表現に対応します。検索語の上限はファイル内検索と共通で16,384 UTF-16単位です。 | 開く Windows `Ctrl+Shift+F` ／ macOS `Cmd+Shift+F`。検索欄で `Enter`、検索欄・結果一覧で取消 `Esc` |
| 検索結果パネル | 右側にファイル名・行と列・一致の抜粋を表示します。左端で幅を調整でき、閉じても検索・結果・幅を保持します。マウスを重ねるとフルパスと抜粋を確認できます。 | 閉じる Windows `Ctrl+J` ／ macOS `Cmd+J`。確定行に移動 `Enter` |
| 同じ識別子のハイライト | 識別子をクリックすると、同じタブ内で大小文字まで一致する単語を本文と縦スクロールバーに最大10,000件表示します。コメントや文字列も対象です。 | 解除 `Esc`（本文にフォーカス中） |
| 定義へジャンプ | C#・C・C++で、簡易検索または意味解析を使って定義へ移動します。検索が完了し候補が1件なら直接移動し、複数候補や部分結果は一覧で確認できます。 | `F12` |
| 参照検索 | 識別子の参照箇所を一覧表示します。名前一致では宣言・コメント・文字列も対象です。意味解析を有効にすると型やプロジェクトの情報を使って検索します。 | `F1` |
| 意味解析の設定（C#） | Native AOT配布物に同梱のRoslynは、フォルダ直下にソリューションが1件、またはソリューションがなくプロジェクトが1件ある場合に自動で有効になります。Roslynはプロジェクトの処理を実行する場合があるため、信頼できるフォルダで利用してください。[利用条件・手動設定](docs/detailed-design/06-navigation.md#8-意味解析による定義ジャンプ参照検索) | — |
| 意味解析の設定（C/C++） | 「検索 → 意味解析の設定…」で有効にします。同梱clangdが `compile_commands.json` を使い、未指定の場合は作業フォルダ直下と `build` を探します。 | — |
| ジャンプ履歴 | 定義や検索結果へ移動した位置をたどり、ファイル・カーソル・スクロール位置を復元します。最大100件を保持し、作業フォルダの切り替えやアプリ終了で消去します。 | Windows `Alt+←`／`Alt+→` ／ macOS `Cmd+←`／`Cmd+→` |
| 除外フォルダの設定 | 「ファイル」メニューからフォルダ名を1行ずつ設定します。再起動後にツリーとフォルダ検索に適用されます。 | — |
| メニュー・ファイルパス | ☰から「ファイル」「表示」「検索」「移動」を開けます。タイトルバーのパンくずにマウスを重ねるとフルパスを確認できます。 | 開く Windows `Alt` ／ macOS `Option` を単独で押して離す |

## 必要なツール・SDK

- .NET SDK 10.0.403（許容パッチ版は[global.json](global.json)に従う）
- Python 3（通常テスト・カバレッジ計測・Native AOT発行用。macOSは`python3`、Windowsは`py`ランチャー）
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
