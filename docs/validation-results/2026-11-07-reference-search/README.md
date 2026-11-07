# 名前一致参照検索の実装・両OS検証

2026-11-07。識別子抽出ee780f8に続き、1b6416fe83f6d26fbe40fedfad2122646e3d7250で通常UIへ接続。

## 結果

[参照検索のネイティブCI](https://github.com/m-hiroaki/codeView/actions/runs/42332107131)でWindows 2025/macOS 15各3独立プロセス、各39検証すべて成功し、全プロセスが終了0。ZIPはActionsのSHA256 digestと照合済み。runs.jsonの実レポートを検証器でも再確認した（summary.json）。

- Shift+F12のrouted keyから原文識別子を抽出し、大小区別/単語/非Regexの条件で検索。
- メニューClickから選択語を検索し、バインドされた「名前一致・意味解析なし」の説明を表示。
- 宣言・コメント・文字列・CP932の結果を取得。結果ListBoxのEnterでCP932本文の原文位置7・6文字選択と本文フォーカスを確認。
- 不正選択で既存結果を維持、日本語選択で単語境界を照合、ルートなしを案内。生成した全入力のSHA256不変。

[通常両OS CI](https://github.com/m-hiroaki/codeView/actions/runs/42332106989)：macOS .NET815/Python112成功、Windows .NET812成功+既存OS専用3スキップ/Python92成功+既存OS専用20スキップ。対象の識別子38/参照検索11テストは計49件成功。単体側では@予約語、非正規化/補助平面、4,096単位境界・巨大prefix、範囲/閉鎖も確認。

追加の回帰CIも成功： [通常GUI](https://github.com/m-hiroaki/codeView/actions/runs/42332106994)、[行別GUI](https://github.com/m-hiroaki/codeView/actions/runs/42332107128)、[Windows Native AOT発行](https://github.com/m-hiroaki/codeView/actions/runs/42332107074)。[文字幅比較の取得](https://github.com/m-hiroaki/codeView/actions/runs/42332107020)も成功したが、このワークフローは既存の8未達を保持した取得成功であり、Unicode描画の是正完了ではない。

## 範囲

実コントロール・ネイティブバックエンドでのプログラムによるkey/menu入力。物理キー/IME、全GUI機能、巨大ワークスペース性能や簡易定義解析の合格とはしない。参照は全テキストの名前一致で、意味的な同一シンボルを解決しない。通常UIだけへ接続し、有界投影試作では原文位置を混同しないよう入口を拒否する。

新しい層/外部API/本文構造を追加せず、共用検索のワーカー枠・世代・取消・結果上限・結果移動を再利用する。原文抽出は要求時だけ最大8,196 UTF-16単位で、常駐索引/起動時走査を加えない。正式性能評価と探索全体はP4/P5-07/G5に残す。[仕様・使い方](../../detailed-design/06-navigation.md#61-参照検索uiの接続2026-11-07)。
