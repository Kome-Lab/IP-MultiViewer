# SBOM / Dependency / License Contract

## 1. SBOM方式

| 比較 | CycloneDX 1.6 | SPDX 2.3 |
| --- | --- | --- |
| 主用途 | component/dependency graph、evidence、assembly/lifecycle、vulnerability/VEXへの接続 | license・package/file・relationshipと配布先の互換性 |
| IPMultiViewer | 主形式として推奨。pre-build/build/post-buildの範囲を分ける | 必要に応じて同じ入力から併記export |
| 制約 | 生成器だけでnative/source closure完成とはならない | Go module一覧だけでfile license/実binary包含は分からない |

1.7やSPDX3系の存在と、今回指定の1.6/2.3 profileを区別する。可逆変換を保証しない。Syft 1.57.1を固定候補とし、出力versionを明示し、想定specVersion以外は拒否する。Toolは今回取得・導入・実行しない。

## 2. 生成時点・Artifact結合

1. dependency変更review時: go.mod/go.sum、実graph、checksum、direct/indirect差分を確定する。
2. pre-build: source inventoryとbuild environment inventoryを別scopeで記録する。
3. 実Build後: 正確なArtifact bytesを入力にpost-build SBOMを生成する。Go build informationとactual graph、embedded/nativeの由来を照合する。
4. Native/SQLite/libc生成元・Go標準ライブラリ内vendor・SDK・外部DLL・Media/GPU追加物の未説明範囲はUNKNOWNとする。文書上のSQLite3.54.1を実行Engine検証済みにしない。
5. BOMとArtifactのsize/hash、source Commit、platform、dependency_set_digest、tool versionを不変indexへ結合する。Artifact交換後は新BOM/revision/hash/attestationを作り、旧証跡を保全する。
6. 公開対象は実SBOM・License/Security report・verified provenanceをpinしたrelease closure。CI14日保持は暫定作業保持であり、公開releaseの保持契約の代用ではない。

SBOMの署名・attestationは別承認された保護工程。今回は構成のみで、実Artifact由来SBOMは0件。

## 3. Dependency Policy

採用tuple SQLite1.62.0 / libc1.80.1 / x/sys0.49.0を変更しない。B1〜B3で取得した全26 modules/58 edgesをbaselineとする。将来CIはfresh go list -m / mod graphと選択済みsumを照合するが、今回は実行しない。

依存追加・直接/間接更新・module最低Go変更は理由・由来・Windows条件・license・Advisory・互換性をreviewし承認する。libcのみの独立更新や強制replaceは禁止。replace/excludeは既定拒否、必要なら別の変更管理。@latest/branch/floating tag、未確認Git URL/配布元、GOSUMDB無効化を禁止する。SumDBの整合は悪性なしやEXE安全性を証明しない。

Vendorは現在非採用。導入する場合はmodules.txt、原文license、graph対応、全hashと由来を別reviewする。Tool用module graphは製品graphへ混ぜない。Govulncheck1.1.4はsource version/h1を記録した補完候補で、tool graph/生成binaryは未確定である。

## 4. License Inventoryの根拠

25個の外部moduleのroot LICENSEを固定cacheから直接読み、52点のroot/補足license・著作権資料を収録した。SPDX idはroot本文の条項との対応を静的確認した。ファイル別適用を未確認のままmodule全体を一つのSPDX式で確定しない。各rowに版、原文path/hash、著作権、再配布/NOTICE/開示/binary条件、包含状態を記録した。

初回26件にはrootの自社moduleも含む。Repositoryには自社LICENSEがなくNOASSERTION/UNKNOWN。これを無断でOSS化しない。配布条件の権利者判断は配布前Gateであり、今回の設定作成を止める理由にはしない。

| 分類 | 方針 |
| --- | --- |
| ALLOW | 条件を個別確認済みの許可対象。無条件安全の意味ではない |
| ALLOW_WITH_NOTICE | MIT/BSD/Apache等。原文・著作権・条件・必要NOTICEを実配布物へ |
| REVIEW_REQUIRED | MPL、複合由来、file scope/配布方法等の判断が必要 |
| RESTRICTED | GPL/AGPL等の未承認の強いcopyleft。用途・配布形態ごとに別判断 |
| UNKNOWN | 不明・参照切れ・権利不明。自動許可しない |

## 5. 具体的な保留

- hashicorp/golang-lru/v2 v2.0.7: MPL-2.0。simplelru/list.goのBSD-3-Clauseも別保持。MPL対象sourceの入手方法を受領者へ示す等の条件を適用範囲に応じて満たす。module graphだけで製品全体のsource公開が必要、またはbinaryに含まれるとは断定しない。
- modernc/libc: root BSDに加えmusl/MIT、BSD、数学由来などの補足条項を確認する。testdata/crlibmのGPL2本文はtestdata scopeで検出した。GPL2-only/or-laterは個別file grant次第であり、汎用COPYING末尾の例から決めない。保持済みWindows package情報ではtestdataは選択されていない。fixture/source bundleの再配布には別reviewが必要。
- modernc/sqlite: wrapper BSD、engine public domain、vec MITを分離。LICENSE-3RD-PARTY.mdのupstream dependency一覧は、製品の現Resolved Graphそのものではない。vecやVFS、test、generatorの包含を別々に扱う。
- memory logo: LICENSE-LOGOはoriginリンクだけ。画像の利用許諾を推定せず、配布対象に自動追加しない。
- Go runtime/vendor、将来のSDK/native/Media/GPU: root licenseだけでclosure完成とはしない。
- 収集したlicense資料中にAGPLは見つかっていない。ただし全fileの非AGPLを証明したとはしない。

実Artifact生成後、実包含のlicense source/hashを照合し、distribution-specific THIRD_PARTY_NOTICESと必要原文を組み立ててreviewする。今回のlicense-evidence.jsonは監査原文資料であり、完成NOTICEではない。新規/変更LICENSE/NOTICEのhash差分は更新reviewへ戻す。
