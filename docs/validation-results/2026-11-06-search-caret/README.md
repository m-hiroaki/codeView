# 検索ジャンプの同期Caret是正（2026-11-06）

修正対象は`f0546edc2bd1e4f40d2cd9056dc937b8ae1f253e`。[原因と変更範囲](../../search-caret-regression.md)。確定した検索移動先を、位置通知で書き換わり得る画面側の_line/_columnからRestoreCaretへ渡していた。確定済みDocumentMove.Positionを直接渡す最小修正を行い、表示補正/原文UTF-16位置/イベント順/選択/コピー/レイヤと公開APIは維持した。

## 修正前の再現

単体ではPosition通知中に旧位置を再入させ、通常一致、次行の境界、外部変更で一致選択を抑制する場合の3ケースがすべて失敗した。期待した(1,5)/(2,1)ではなく旧位置(1,1)がRestoreCaretへ届いた。ログ/TRXと、変更前ViewModelの指紋を保存する。

`d2c8cd8`の[両OSネイティブ追試](https://github.com/m-hiroaki/codeView/actions/runs/42222443691)でも、最初の長行混在入力で`search-caret-before-deferred-selection`が失敗した。文末から実際の検索結果Enterを通し、同期RestoreCaret直後の原文Caretを捕捉して遅延SelectSearchRangeとは別に照合した。従来の最終選択だけの確認では見えなかった不具合である。両OSの失敗ZIPも保持し、最終成功回数へ混ぜない。

## 修正後の検証

同じ新規3ケースを含む検索移動23ケースは成功した。[最終通常CI](https://github.com/m-hiroaki/codeView/actions/runs/42223204130)もmacOS .NET753/Python108、Windows .NET750+既存3スキップ/Python88+既存20スキップで成功した。

[最終行別GUI CI](https://github.com/m-hiroaki/codeView/actions/runs/42223204424)は両OSで、長行混在/空/Unicodeを各3プロセス、計9プロセス/1,674検証ずつ成功した。非空6プロセス/OSすべてで同期Caretが正しい原文移動先へ到達し、その後の遅延選択も一致した。空入力は検索結果0件の既存条件を維持する。タブ/再読込/独立コピー/捕捉/閉鎖と全9終了後記録も成功し、未解放Handle/Active Leaseは0だった。

配布ZIPのGitHub SHA-256を照合し、全レポート・終了後JSONを収集ツールで再検証した。最終Sourceの指紋と、ローカルの修正前3失敗・修正後23成功・全753成功のTRX件数も照合した。

## 性能・残課題

旧位置へ誤って復元する経路を除いたが、描画回数や速度の改善率は計測していない。新たな全文走査/コピー/ワーカー/保持を追加しない。最終GUIの接続最大はWindows 258.7135 ms、macOS 55.7093 msで、Windowsの既定100 ms超過は残す。別ランナーの過去結果との差をこの修正の改善率として扱わない。小入力の機能回帰であり、物理入力/大規模/文法・A-05/G2の性能受入ではない。

Unicode視覚比較の8未達は別件であり、今回の修正で解消済みとはしない。[追加設計案](../../unicode-context-proposal.md)は2026-11-06にユーザー承認済みで、既定OFF/上限/原文契約を維持して次の試作へ進める。設定保存や通常UIへの行別採用の承認とはしない。

## 原記録

- `ci.json`：変更前/後コミット、実行ID、Artifact ID、GitHubのSHA-256。
- `before-*.zip` / `after-*.zip`：両OSの修正前失敗/修正後成功の配布ZIPそのもの。
- `summary.json`：最終9プロセス/OSの件数、同期Caret照合、接続、終了後の状態、CPU/割当/GC/メモリ。
- `unit-before.log.gz` / `before.trx.gz`：修正前の新規3失敗。
- `unit-after.log.gz` / `after.trx.gz`：修正後の検索移動23成功。
- `unit-full.*.gz` / `job-*.log.gz`：ローカル全753/Python108と両OSの最終通常CI。
- `source-sha256.json` / `before-source-sha256.json` / `files-sha256.json`：対象ソース、変更前ViewModel、本README/指紋一覧自身を除く原記録の指紋。
