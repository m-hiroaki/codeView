# 行別投影と独立した閲覧位置のコア試作

2026-11-06に[設計変更案](bounded-projection-proposal.md)の承認を受けて実装したA-04のコア段階。Documents内の既存DocumentDisplaySessionへ行別投影と独立した閲覧位置を追加した。元の本文保持/行索引/ReadLease・原文の検索/コピー契約・レイヤ・外部公開APIは維持する。通常UIと従来の専用GUIはまだ連続Sliceを使い、新コアのGUI接続は未実施である。

## 表示量と位置

CreateRowProjectionは指定先頭行から最大512行、各行の横方向の必要範囲を取り、表示テキスト全体を16,384 UTF-16単位以内にする。全長に比例する一覧・新索引・全行走査は追加しない。既存の行索引から対象行だけを取得する。文字予算から行間の区切りを引いて各行へ均等に配分し、EffectiveColumnsへ実際の上限を残す。スカラーを表示できる2単位/行を確保できない予算は拒否し、黙って行を捨てない。EOF付近は存在する残りの行だけを返す。

各行の不変メタデータは、原文行/列（1始まり）、原文Offset（0始まり・long）、長さ、表示Offset、左右の切詰め、横の表示範囲より完全に左へ外れた状態を持つ。サロゲート対の途中なら開始を前、終了を前の境界へ補正する。原文の改行は表示文字へ取り込まず、行間を表示専用LFで区切る。省略記号などを本文へ追加しない。GUIの切詰め表示は後続の接続で行う。

TrySourcePosition/TryDisplayPositionは表示された挿入位置を往復させ、サロゲート対の内側や画面外の位置を拒否する。TrySourceCharacterはUTF-16単位を対応させ、表示専用LFを原文文字としない。行末の挿入位置と改行文字そのものは別である。選択は原文範囲との行ごとの交差だけを表示用の複数Spanへ変換し、表示専用LFを選択範囲へ含めない。

## 閲覧位置と寿命

SetViewport/ScrollViewportは先頭原文行と横のUTF-16 Offsetだけを変更し、Anchor/Caretを変更しない。符号付き64bitの最大/最小スクロールも、加算前に範囲を判定して安全に収める。横の上限は暫定的に原文長であり、実際の最大行長/ピクセル幅を表すものではない。MoveCaret/Selectも閲覧位置を動かさず、RevealCaretを明示したときだけ閲覧位置を補正する。

横へ外れた短い行の行末を左端のCaretと誤認しない。投影が変わった後、別セッションの投影、連続Sliceとの方式切替、閲覧位置変更後の古い投影を、参照同一性で現在の入力へ採用しない。閉鎖後は既存のDispose/所有スレッド境界を維持する。

コピーは既存のBeginCopyと独立Leaseを使用し、改行を変換せず原文範囲から取得する。表示用文書のSelectedTextは原文コピーに使わない。投影自体は短い文字列と行メタデータだけを保持し、閉じた元文書やLeaseを保持しない。

## 読み出しと評価

内部のCopyRangeIntoは16 Ki単位までの範囲を最終投影文字列へ埋める。RopeTextSourceでは既存Ropeのprivate cloneからCopyToし、返却する一時バッファを消去する。Rope以外の不変Snapshotは有界なGetCharAtで取得する。元のRopeは変更せず、文書構築の挿入経路と保持形式は従来どおりである。既存の全文コピー/検索走査は変更しない。

単体16ケースで通常/長行混在、空/末尾空行、CR/LF/CRLF、日本語/絵文字/タブ、双方向位置往復、複数選択Span、画面外Caret、明示Reveal、stale/他セッション/方式切替、原文コピーの閉鎖後存続、元文書の弱参照解放、所有スレッド、範囲/予算/オーバーフロー、非Rope/一時バッファ上限を検証する。

性能ケースは50 MiB相当のASCII長行と49行の2,048単位の通常行をメモリ上で構築し、48可視行・16 Ki表示予算で64回切り出す。原文は52,529,250 UTF-16単位で、v2の正確な50 MiBファイルのI/O評価ではない。既存の取得50 ms/総割当4 MiBの比較予算を維持する。構築は計測外、アサーションは割当に含め、CPU・GCとOSメモリの前後値も残す。OSメモリ値はサンプルピークではない。

[Row projection core validation](../.github/workflows/row-projection-validation.yml)は両OSで単体と性能3プロセスを実行する。通常試験からPerformanceを除外する既存方針は維持し、この明示ワークフローで記録する。

`cb6e82c`の両OS CIで単体16ケース/OSと性能3プロセス/OSが成功し、既定予算内を確認した。[数値・原記録・測定限界](validation-results/2026-11-06-row-core/README.md)を保存する。

```sh
./test-mac.sh
dotnet test tests/codeView.Tests/codeView.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RowProjectionPerformanceTests --logger 'console;verbosity=normal' -p:PublishAot=false -p:CodeViewPerformanceProbe=false
```

行番号/背景選択/Caret/検索移動、全文の縦横スクロール、ポインター捕捉・自動移動、タブ/再読込/終了、フォント/タブ幅/結合文字/Bidi、長行メタデータ、10/50/100 MiB・5文書・文法有無のGUI評価は未完了。今回の横Offsetと幅はUTF-16単位であり、タブや絵文字の物理幅を評価済みとしない。通常採用・A-04/A-05/G2全体の完了とは分ける。
