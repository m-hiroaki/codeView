# 検索パネル遅延構築の比較：候補不採用

A-01-04。[事前の条件](../../deferred-search-comparison.md)に従い、変更前94bbefdと最終候補0391352を、[同一ホストの両OS CI](https://github.com/m-hiroaki/codeView/actions/runs/42205034705)で交互に比較した。**候補は不採用**とし、786d62eでMainWindowのXAML/コードと入力Epoch getterを変更前へ戻した。復元対象3ファイルは4e2aed5時点と同一である。

## 最終結果

各OSで変更前/候補×空/ファイルを各10回、40プロセス、両OS合計80プロセスを取得した。すべてコミット・入力SHA・12段階・全文表示・通知/スクロール・終了0が整合した。

| Main→Opened中央値 (ms) | 変更前 | 候補 | 候補の変化 |
| --- | ---: | ---: | ---: |
| Windows 空 | 1,303.079 | 1,258.848 | −3.39% |
| Windows ファイル | 1,288.930 | 1,255.977 | −2.56% |
| macOS 空 | 928.707 | 910.748 | −1.93% |
| macOS ファイル | 832.879 | 879.139 | +5.55% |

CPU/OSメモリ中央値は両OSとも5%増以内だったが、全4条件で5%以上の起動短縮という事前基準を満たさなかった。特にMacのファイル条件は遅くなった。取得成立`acquired=true`と不採用`candidateAccepted=false`を保持し、CIの非0終了は性能不採用を表す。条件を測定後に緩めていない。

## 機能と初回表示

最終候補の正式な機能試験は両OS各3プロセスが成功した。初期未構築、初回メニュー表示とQuery/Focus、TwoWay Binding、不正式Regex、1確定結果、結果Enterでの既存タブ再利用/本文選択/Focus、表示3反復で同じコントロールと状態を保持、クリア、空語の無効、元本文/ファイル不変を確認した。初回のFocusを遅延した後に新しい入力世代を発生させ、Focusを奪い返さないことも確認した。先行の機能確認も別に各3プロセス成功し、正式な3回と混ぜず保存している。

初回表示の同期処理はWindows 49.399〜51.156 ms、Mac 32.568〜40.645 msで全正式6回が100 ms以内。2通知到達はWindows最大211.028 ms、Mac最大159.307 msだった。物理描画/実キーの操作可能時刻ではなく、実起動1秒やUI全体の応答達成とはしない。入力は1生成ファイルだけの専用フォルダ、操作はRoutedメニュー/結果Enterであり、OS入力を注入していない。

## 初期失敗と是正

- [39e661cの試行](https://github.com/m-hiroaki/codeView/actions/runs/42200076275)：起動取得後、初回検索表示で異常終了。DataTemplate.Buildの結果だけを取り出していたため、[固定版のテンプレート生成](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Markup/Avalonia.Markup.Xaml/Templates/DataTemplate.cs)と名前スコープの扱いを確認し、TemplateContent.Loadの結果からスコープも保持するよう修正した。
- [11dc639の試行](https://github.com/m-hiroaki/codeView/actions/runs/42203817166)：パネル生成後のBinding/Focus判定で停止。初回レイアウト後のFocusと後続入力の世代ガード、失敗時の詳細記録を追加した。最終0391352では両OSの全機能条件が成功した。

初期失敗は各試行のJSON/ログとartifact識別へ残し、正式な性能採否や機能成功数へ混ぜていない。候補全体は採用していないため、これらのUI実装修正も比較差分の中へ保持し、通常製品へ残していない。

## 差し戻しと検証

復元786d62eの[両OS通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42206084209)はmacOS .NET703/Python89成功、Windows .NET700成功+既存Darwin専用3スキップ/Python69成功+既存POSIX専用20スキップ。[実GUI](https://github.com/m-hiroaki/codeView/actions/runs/42206084178)も両OSの起動各20プロセス/読取専用各216操作が成功し、[Windows AOTの実発行・通常起動](https://github.com/m-hiroaki/codeView/actions/runs/42206084160)も成功した。比較器の正常/異常/期限/採否境界7ケースは通常CIで成功した。

候補の3ファイル分は`candidate.patch`、最終両OSの全取得物は`*-final.json.gz`、初期の機能失敗は`failure-*.json.gz`、復元後の通常ログは`restored-*.log.gz`、実行/ジョブ/artifactは`ci.json`、再集計した値は`summary.json`へ保存した。元ZIP全6件をGitHub digestと照合し、最終の全取得ファイルのSHA、80レポートと正式/先行の機能12レポートを再検証した。保存物のSHAは`files-sha256.json`。元ZIPは`artifacts/deferred-search/ci/`、Actions artifactは14日保存する。

この比較は候補不採用の判断までの完了であり、起動未達の是正ではない。通常製品の構築方式は従来のままとし、A-01/P1-09/G1は未完了を維持する。承認済みの行別投影は別のA-04で進める。
