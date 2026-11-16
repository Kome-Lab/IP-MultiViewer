# Artifact Manifest / Gates / Handoff

## 1. D15-A06を変更しない連携

D15-A06 Release Manifestはclosed schema。既存24 artifact fieldsとroot manifest_revisionを維持する。今回license_report_ref等を勝手に既存descriptorへ足さない。
新しいbuild-evidence-indexはArtifact単位のsha256/size/version/dependency digest、SBOM/license/security/provenanceと対応するsidecarであり、Release Manifestそのものではない。D15のbuild_provenance型Refからimmutable evidence closureへ接続し、sbom_refは正確なSBOM bytesへ結合する。正式Digestは{algorithm:"sha256",value:...}、Refはschema_version/id/revision/content_hash、U64は文字列。foundationのpath/stringを未変換で貼り付けない。

Signerは実在する承認済みsigner identityを使用し、自己申告JSONのactor名で代用しない。Compatibilityは9軸、security revision/floorは単純semverで決めず、旧版rollbackでも下げない。
candidateはrelease_eligible=false、provenance_ref/signature相当は未生成。nullを互換wildcardや署名済みの意味にしない。Artifact/証跡が変わったら新revision・再hash・再attestation・再署名。自分自身の完成hashを署名bodyへ循環参照させない。

## 2. Gate Matrix

| Gate | 現在 | 次に必要な根拠 |
| --- | --- | --- |
| Branch/HEAD保全 | B1〜B3のbranch/commitを維持 | 今回のbefore/after全file・index・Git設定hash |
| Dependency graph | B1〜B3部分PASSを継承 | 実行許可後のfresh照合。今回変更なし |
| License source inventory | 26 rows / 25 external root LICENSE / 52 evidence files | 実Artifactの包含・NOTICE・自社配布権限 |
| SBOM definition | PREPARED | 実Build後の生成・native closure・hash/binding |
| Advisory policy | PREPARED / NOT_SCANNED | 承認後scanとSQLite/native/Go/toolの適用triage |
| Core CI | PREPARED / NOT_DISPATCHED | exact Commit/別実行承認、target mapping、profile/環境確認 |
| Workflow Action pins | 公式固定source照合 | transitive source・利用policy・実Runner互換 |
| Source/Unit/Integration | NOT_RUN | 対象packagesの個別登録。Integrationは隔離許可 |
| Windows/Linux Build | NOT_RUN | 本物のCore entrypoint、対応OS/profile、Build許可 |
| Artifact hash/manifest | GENERATION_DEFINITION_ONLY | 実最終bytes・hash・閉包・typed Ref |
| Provenance | UNATTESTED / NOT_RUN | 実builder由来OIDC署名とverifier、GitHub利用条件 |
| Code Signing | NOT_SELECTED / NOT_RUN | signer/key/証明書/runner/更新失効の別判断 |
| Reproducibility | SPECIFIED / NOT_MEASURED | 同一input/2環境のunsigned byte比較 |
| D15-COMPAT-01全体 | OPEN | 実Engine/VFS/locking/WAL/FULL/Backup/耐久性/性能等 |
| Phase3-A | BLOCKED | Falcon一次export、MinGW照合記録、Endpoint正式安全性判断・再開承認 |

## 3. 今回決めた範囲と保留

今回作成した設定・policy・workflow・scriptの草案はB4/B5範囲。新しい製品機能や受入済みD15契約を変更していない。
次の順で別工程の具体的な範囲と許可を決める。

1. B4/B5資料の確認。SBOM主形式、license分類・Advisory gate、manual CI/保護署名分離の方針を確認する。
2. 必要なら今回の新規ファイルとB1〜B3の既存変更を含むCommit/Push/CI実行の具体範囲を別承認する。現時点では実施しない。
3. 最初の実行範囲はdependency/設定検証か、対象の実装・Build/Testを含むかを明確にする。UNASSIGNEDを架空のObserverHostで埋めたり、legacyを代替したりしない。
4. 個別機能に進む前にDB実適合、IPC/Service/署名・鍵・CA/IdP/Providerなど必要な依存Gateを維持する。
5. Media/GPU/PoCはPhase3-A安全性判断と明示再開承認までBLOCKED。

Stage0-C/Stage1を開始した、初回リリース必須機能を先送りした、CVE0件/製品PASS、D15-COMPAT全体CLOSED、Phase3-A解除とは記録しない。
