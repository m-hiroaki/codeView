# 小さな固定試験データ

P0-05用。[詳細設計08の2.1](detailed-design/08-validation.md#21-最小固定データの期待値)を再現する。大容量・速度評価用のgenerate_fixture.pyとは別のデータで、生成時のI/Oを性能測定へ含めない。

前提ツール:

- Python 3（追加パッケージ不要）。
- Windowsのネイティブケースは標準のwhoami・icaclsと、シンボリックリンクを作成できる権限/環境。
- macOSは通常ユーザー。root等で読取拒否を回避できる環境を、拒否成功とは扱わない。

新しい出力先を指定する。既存ファイル/ディレクトリ/リンクを上書きしない。

```sh
python3 tools/generate_functional_fixture.py artifacts/fixtures/functional
```

```bat
py -3 tools\generate_functional_fixture.py artifacts\fixtures\functional
```

## 内容・期待値

30個の通常ファイルとmanifest.jsonを生成する。パス・正確なバイト数・SHA-256・復号後の本文・UTF-16位置・検索結果・宣言候補をmanifestへ記録し、日時/出力先に依存しない。生成結果はソース管理対象外のartifactsへ置く。

| ディレクトリ | データ |
| --- | --- |
| text | foo/food/Foo、非重複一致、サロゲート対、合成/分解表記、CRLF、空本文、空の最終行 |
| encoding | UTF-8・CP932・UTF-16/32 LE/BE。CP932以外はBOMあり/なしの両方 |
| invalid | UTF-8切断/過長、CP932切断/不正後続、UTF-16単独サロゲート、UTF-32範囲外、PNG署名 |
| csharp | C/Mの既知の位置、コメント/文字列内の偽宣言、型/入れ子/メンバー等の基本宣言 |
| io | 更新対象と更新前後の本文、読取拒否対象 |
| links | 通常ファイル/フォルダ、壊れたリンク、相互循環、親へ戻る循環 |

文字コードを明示する不正データの期待値と、自動検出の期待値を混同しない。BOMなしUTF-16/32は文字コードを指定して使う。C#候補はPhase 5用の正解データであり、探索機能の実装/精度の受入完了を意味しない。

ネイティブ生成ではリンク6件を作り、io/denied.txtに現在ユーザーの読取拒否を設定する。POSIXではmode 000、Windowsでは現在ユーザーSIDへのRead Data拒否のみを使う。SIDはwhoamiのCSVのSID列から検証して取得し、アカウント名に似た文字列があっても採用しない。変更は生成した単一ファイルだけに限定する。Windowsの操作は[icaclsの仕様](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/icacls)に従う。生成器自身でreadがPermissionErrorとなることまで確認する。

リンク/拒否の準備に失敗した場合はmanifestのnative/errorsに記録し、CLIを非0で終了する。部分生成物へ再実行せず、新しい出力先を使う。バイトデータだけが必要な場合は明示的にportableで生成する。この場合のnativeはnot-runで、リンク/権限失敗の検証には使えない。

```sh
python3 tools/generate_functional_fixture.py artifacts/fixtures/functional-portable --portable
```

```bat
py -3 tools\generate_functional_fixture.py artifacts\fixtures\functional-portable --portable
```

更新失敗はFileReader.Prepare後、ReadChunks前にio/update-after.txtをio/update.txtへコピーする。更新後の長さは必ず異なり、ChangedDuringReadを期待する。初期状態はio/update-before.txtから復元する。これらのコピーは試験用ディレクトリだけで行う。

## 検証と片付け

通常テストのFunctionalFixtureTestsはportableデータを一時ディレクトリへ生成し、本体のFileReader・文書・検索コアで全30ファイルを読む。読取前後のハッシュ、文字コード/不正バイト、行列、更新検出、1/2/3/7/16384文字チャンクの検索を確認する。Python 3を試験実行時にも使用するため、dotnetだけの直接実行でもPythonが必要となる。

Pythonのtest_functional_fixture.pyは実際のリンク・読取拒否/解除も確認する。既存出力の保護、生成の再現性、未対応時の報告、Windows SID/ACL引数も試験する。ネイティブケースを実施できない場合は未確認として残し、portable成功を代用にしない。

```sh
./test-mac.sh
```

```bat
test-windows.bat
```

手動生成した拒否ファイルを読む/片付ける前に、以下でアクセスを戻す。Windowsは生成したときと同じユーザーで実行する。

```sh
chmod u+rw artifacts/fixtures/functional/io/denied.txt
```

```bat
icacls artifacts\fixtures\functional\io\denied.txt /remove:d "%USERDOMAIN%\%USERNAME%"
```

リンクをたどらずに試験ディレクトリを片付ける。ツリー/検索がリンクを再帰しないこと自体も確認対象である。
