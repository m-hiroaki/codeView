# NU1903のSDK更新による解消

2026-11-06、macOS arm64。基点d351cdbから最低SDKを10.0.401→10.0.403へ更新。システムSDKを変更せず、公式配布のSDK 10.0.403を一時ディレクトリへ取得し、公式メタデータのSHA512検証後にPATH/DOTNET_ROOTで選択した。

- 実際の`./build-mac.sh`が終了0。.app内のcodeViewはMach-O 64-bit arm64。発行用ロックのMicrosoft.DotNet.ILCompilerとruntime.osx-arm64.Microsoft.DotNet.ILCompilerは10.0.14（圧縮ロック参照）。
- `./test-mac.sh`：.NET 766/766、Python 108/108成功。スクリプト専用7単体ケースも別実行ですべて成功。
- リポジトリの依存ロック変更なし。NuGet監査・警告のエラー扱い・AOT/トリミング設定を維持した。
- SDK変更でもWindows実AOT発行CIを起動するようpathsへglobal.jsonを追加。今回のローカル記録はWindows結果、GUI起動、性能、署名/公証の証明ではない。

[脆弱性と修正版](https://github.com/advisories/GHSA-p3jp-9v83-76w9)、[公式SDK/ランタイム対応](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)。利用者の通常環境にはSDK 10.0.403のインストールが必要。[手順](../../build.md#nu1903ilcompiler-10012が出る場合)。
