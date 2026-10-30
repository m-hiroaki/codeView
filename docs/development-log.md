# codeView 実装・検証記録

## 2026-10-30：実装開始

実装対象は[タスクリスト](implementation-plan.md)と[詳細設計](detailed-design/README.md)に従う。F-01〜F-09、Q-01〜Q-08、QS-01〜QS-12、D-01〜D-10の対応台帳はタスクリスト11・12章を使用し、実測・未完了をここへ追記する。

### 環境

| 項目 | 確認値・状態 |
| --- | --- |
| 手元のOS | macOS 27.0.1、arm64 |
| CPU・メモリ | 物理10コア / 論理10コア、32 GiB |
| SDK | .NET SDK 10.0.401、ターゲットnet10.0 |
| UI | Avalonia.Desktop / Fluent 12.1.3を初期固定 |
| 本文 | Avalonia.AvaloniaEdit 12.0.0を仮参照。Phase 2の採否は未判定 |
| 単体試験 | NUnit 4.1.0、Adapter 4.5.0、Microsoft.NET.Test.Sdk 17.14.1を初期固定 |
| Windows実機 | 未確保・実行未検証。最終受け入れの必須項目として残す |
| 別PC・署名・公証 | 未検証 |

依存はローカルNuGetキャッシュ内で確認できた組合せを固定し、ロックファイルを保存した。初期復元はキャッシュのみを使用し、ネットワーク依存のNuGet脆弱性監査は実施していない。今後の依存評価・配布前の監査を省略しない。最新LTSの根拠は[.NET 10の公式発表](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)、本文の参照元は[公式NuGetページ](https://www.nuget.org/packages/Avalonia.AvaloniaEdit/12.0.0)を参照する。

### 進捗と証跡

| 対象 | 状態・確認内容 | 残る作業 |
| --- | --- | --- |
| P0-01 | 要件・品質・シナリオ・D台帳を本計画の対応表へ照合 | 実装に応じて証跡を更新する |
| P0-02・P0-03 | 手元のSDK・macOS、UI・試験依存を確認 | Windows・別PC、採用候補の全OS検証 |
| P1-01 | 製品1・試験1、SDK固定、パッケージロック、Releaseビルド成立 | Windowsビルドと完全な再現手順 |
| P1-02 | UTF-16位置、改行チャンク境界、範囲の不正値・オーバーフロー、結果区分、旧Epoch拒否を実装 | 文書の所有移譲・SearchCompletionの統合検証 |
| P1-03 | ダークテーマ・最小ウィンドウ・メニューの骨格をビルド | GUI起動と初回描画の計測、操作接続 |

検証コマンドは以下。Sandbox内のMSBuildがプロセス間通信待ちに留まったため、取消・終了を確認した後、権限付きの単一ノードで実行した。これは製品の処理モデルの変更ではない。

```sh
dotnet restore codeView.slnx
dotnet test codeView.slnx -c Release --no-restore -m:1 -nr:false
```

実行時は`DOTNET_CLI_TELEMETRY_OPTOUT=1`を指定した。初期のオフライン復元には`--source /Users/hiroaki/.nuget/packages --ignore-failed-sources -p:NuGetAudit=false`を付けたが、このマシン固有のパスと監査省略を標準手順にしない。

- Release：合格15、失敗0、スキップ0。
- TRX：`tests/codeView.Tests/TestResults/foundation.trx`（生成物のためGit対象外）。
- 本体の初回描画・起動時間・本文性能は未測定。G0・G1は未完了。
- HTMLモック：8テスト合格、コミット`bb5576a`。実アプリの達成とは分けて扱う。

### 計画の進行条件の補足

Windows実機が未確保という環境条件だけで、macOS上の共通実装・単体試験まで停止するのは進行上不適切である。ゲートの証跡は未完了のまま保持し、判断に依存しない実装は先行する。これにより共通実装を進められる一方、OS差を後段で見つけるリスクがあるため、Windowsビルド・実行は継続して必須として追跡する。

この補足は計画の進行順の明確化であり、Windows対応、性能予算、本文採否、受け入れ試験を免除する変更ではない。G2の本文採否が未判定のまま本文モデルを確定して先へ進めない。
