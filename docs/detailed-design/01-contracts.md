# 01 共通契約・規約

[詳細設計の索引](README.md) / 上位：アーキテクチャ設計3.3・3.5・7章

## 1. 型の配置と変更規則

共有契約はUI非依存の少量の値に限定する。検索要求はSearch、探索要求はNavigation、タブ・世代の状態はOperations、UI選択・文言はPresentationが所有する。複数の利用元が実在する位置・ファイル情報・エラー分類だけをContractsへ置く。識別子型の細かなファイル分割は必要になった時点で行う。

すべて内部型とし、共有型へViewModel、Avalonia、AvaloniaEdit、サービス、キャッシュを持たせない。可変コレクションをワーカーとUIで共有せず、要求は開始時点の不変スナップショットにする。

## 2. 位置とファイル情報

| 型 | フィールドと制約 | 所有・用途 |
| --- | --- | --- |
| `SourcePath` | OSに渡す絶対パス文字列、表示用の指定パス | Contracts。相対成分を整理するがNFC変換・一律小文字化・リンク同一化をしない |
| `SourceStamp` | `LengthBytes: long >= 0`、`LastWriteUtc` | Contracts。読取前後・結果移動時の補助的な変更判定 |
| `TextPosition` | `Line: long >= 1`、`Column: long >= 1` | Contracts。列は行頭からのUTF-16コード単位数+1。タブ幅や描画桁数とは別 |
| `TextRange` | `Offset: long >= 0`、`Length: long >= 0`、開始位置 | Contracts。元本文のUTF-16単位、区間は`[Offset, Offset+Length)` |
| `FileDescriptor` | パス、通常ファイル／フォルダ／リンク等の種類、サイズ・更新情報 | Contracts。本文や開いたハンドルは保持しない |

表示文書が32bit位置を使う場合はDocumentAdapter内でチェック付き変換する。表現限界を超える場合は`UnsupportedSize`として通知し、整数の桁あふれや不正ジャンプを起こさない。これは数十MiBの要件を免除する理由にしない。

CRLFは1つの改行、CR・LF単独も改行とする。最終改行の直後には空の最終行がある。空本文は1行・1列。抜粋や選択範囲はサロゲートペアの途中で区切らない。位置一致の単体テストは本文・横断検索・探索で同じ期待値を使う。

## 3. 操作の識別と採用判定

Operationsが次を所有する。数値の使い回しを避け、同じパスで閉じ直したタブも別IDとする。

所有するのは採番・更新の権限である。下位の要求へ渡す識別値は数値等のUI非依存な値とし、SearchやFilesからOperations内のセッション型・TabStateを参照させない。共通の値型が必要になった場合だけContractsへ置く。

| 値 | 更新契機 |
| --- | --- |
| `RequestId` | 受け付けたOpen・検索等ごとに採番 |
| `WorkspaceEpoch` | フォルダ切替・ワークスペース無効化 |
| `TabId` | 新規タブ作成 |
| `LoadEpoch` | タブの初回読込・文字コード変更・再読込・閉鎖による無効化 |
| `DocumentVersion` | 成功した本文採用・入替。失敗した再読込では維持 |
| `SearchEpoch` | 共用結果の新検索・明示クリア・ルート切替 |
| `InFileEpoch` | ファイル内の条件変更・本文版変更・取消 |

結果の採用条件は「アプリ受付中、所有者が存在、該当Epoch一致、必要なDocumentVersion一致、不採用の取消ではない」をすべて満たすこと。フォルダ読込はWorkspaceEpoch、本文読込はTabIdとLoadEpoch、共用検索はWorkspaceEpochとSearchEpoch、ファイル内検索はTabId・DocumentVersion・InFileEpochを照合する。

明示した全文検索の取消では、既にUIが採用した結果を残す。まだキュー内にある結果は停止時に破棄してよい。新検索への置換では旧表示もクリアする。この違いを単純なToken有無だけで判断せず、停止理由をOperationsが保持する。

## 4. 成功・失敗・部分結果

| 型・値 | 内容 |
| --- | --- |
| `OperationError` | `Code`、処理種別、必要な対象パス、再試行可否。本文・検索語・例外全文を保持しない |
| `ErrorCode` | `NotFound`、`AccessDenied`、`NotRegularFile`、`BrokenLink`、`Binary`、`DecodeFailed`、`ChangedDuringRead`、`InvalidPattern`、`RegexTimedOut`、`UnsupportedSize`、`QueueFull`、`InvalidSettings`、`Unexpected`等 |
| `OperationResult<T>` | `Success(value)`、`Cancelled`、`Failure(error)`のいずれか。成功でない場合に値を参照しない |
| `SearchStopReason` | `Completed`、`Cancelled`、`LimitReached`、`Failed`。Search内に置く |
| `SearchCompletion` | StopReason、走査件数・バイト数、一致数、スキップ内訳、未評価行数、代表エラー、全件評価できたか。Search内に置く |

除外名・バイナリ・リンクは仕様上の対象外として件数を分ける。読取拒否・デコード不能・解析上限・正規表現行上限は評価できなかった範囲として扱う。後者がある、走査が止まった、または結果上限へ達した場合は全件評価済みとしない。正常0件と定義候補未検出は失敗例外にしない。

ワーカーで未知の例外を`Unexpected`にする場合も、回復可能であるとの宣言にはしない。[例外境界](02-runtime-ui.md)が継続・異常終了を判断する。

## 5. 所有権の共通規則

- 列挙器・ストリーム・デコーダーは作成した処理が解放する。本文採用時だけ構築済み文書の所有権をタブへ渡す。
- FileReaderが渡すチャンクは借用メモリとし、受取コールバックの完了までのみ有効。受取側が後で使うなら必要部分を所有データに移す。借用メモリをUIキューや結果へ直接積まない。
- 結果バッチは自身が所有する小さなレコードの配列として渡し、投入後に変更しない。本文全体を捕捉するクロージャやスライスを入れない。
- 通常のバッファはセッション内で再利用する。共用プールの採用は割当計測後に判断し、先行して複雑な貸出管理を導入しない。
- 非同期処理終了後の完了・失敗を必ず観測する。UIへ不採用でも生成物の解放を省略しない。

## 6. 内部予算の初期候補

以下は詳細設計で置く測定開始値であり、性能達成済みの値ではない。1 KiBは1,024バイト、1 MiBは1,048,576バイト。文字数上限は特記がなければUTF-16コード単位で数える。実装では1か所の内部定数定義を参照し、設定JSONに増やさない。

| 名称 | 初期候補 | 対象・上限到達時 |
| --- | --- | --- |
| `OpenQueueLimit` | 64要求・全体256パス・パス文字総量256 Ki文字 | 起動待ちと読込待ちに共通予算。要求単位で受付拒否し通知 |
| `SettingsWait` | 2秒 | 既定値で一度だけ確定。遅いI/Oは追跡して不採用 |
| `ShutdownWait` | 2秒 | 新要求停止・取消後の待ち上限。終了の完了を無期限に延長しない |
| `ReadChunkBytes` / `TextChunkChars` | 64 KiB / 16 Ki文字 | セッションで再利用。デコーダーの持越し状態を別に保持 |
| `BinarySampleBytes` | 8 KiB | BOM・既知形式・NUL・制御文字判定の標本 |
| `ResultLimit` / `SnippetChars` | 10,000件 / 200文字 | 上位案を引継ぎ。上限到達・未完了を通知 |
| `BatchSize` / `ResultQueueBatches` | 128件 / 8バッチ | 満杯時は取消可能な待機。1件の抜粋上限も適用 |
| `UiDrainBudget` / `UiDrainRows` | 4ms / 256件 | 先に達した方でUI反映を区切る |
| `ProgressInterval` / `InputDelay` | 100ms / 150ms | 進捗通知を集約。検索入力はIME確定後にまとめる |
| `RegexTimeout` / `RegexLineChars` | 100ms / 1,048,576文字 | タイムアウトで検索全体停止、長行は未評価を通知 |
| `QueryChars` | 16 Ki文字 | 通常語は初期実装で固定し超過/不正サロゲートを入力エラー。語・パターンを黙って切らず、Regex/表示のD-10は別検証 |
| `InFileStoredMatches` | 10,000件/タブ | 超過後も方向別に追加走査。正確な全件数の断言を避ける |
| `ErrorSamples` | 20件/セッション | 内訳件数を継続加算し、詳細の保持を増やさない |
| `TreeQueueLimit` / `CollapsedCacheNodes` | 64要求 / 10,000ノード | 過剰要求を通知。折りたたみ済み子一覧を破棄 |
| `SettingsBytes` | 64 KiB | 超過する設定は無効として既定値へ |
| `LexerTokenChars` / `DeclarationTokens` / `DeclarationChars` / `LexerDepth` | 4,096文字 / 256トークン / 16 Ki文字 / 1,024階層 | 宣言解析の上限。超過は未評価として通知し、Phase 5前に固定 |

100msの正規表現上限は1回の評価に対する候補であり、任意のパターンの全体処理や構築まで100msで止められる保証ではない。L3にL1と同じ時間・メモリ予算を課さない。本文の絶対メモリ上限・色分け閾値・検索速度・GC停止予算は[索引の未決事項](README.md)に残す。

## 7. 共通テスト

| ID | 正常 | 異常・境界 |
| --- | --- | --- |
| CT-01 | CRLF / LF / CRと位置の相互変換 | 空、最終改行、補助平面文字、加算・32bit変換の桁あふれ |
| CT-02 | 現世代だけ採用 | 同パスのタブ再作成、旧読込・旧検索・取消後の通知 |
| CT-03 | 借用中の読取、結果の所有移譲 | コールバック後のバッファ再利用、未採用生成物、二重破棄 |
| CT-04 | 完了と全件評価の組合せ | 部分失敗、正規表現長行、上限ちょうど、0件、取消 |
