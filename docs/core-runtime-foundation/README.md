# Stage 0-B B4/B5 — Core Supply Chain / Build Foundation

2026-11-16 JST。状態は **PREPARED_STATIC_ONLY**。Build/Test/Vet/CI/Scanner/Signing/Commit/Pushは未実施。
B1〜B3のgo.mod/go.sum、Go 1.27.4、採用tupleと実Resolved Graphを保全する。

## 今回の準備範囲

| 区分 | 準備したもの | 今回行わないもの |
| --- | --- | --- |
| B4 | 26 modulesの固定baseline、52点のlicense原文資料、分類・SBOM・Advisory policy、例外契約 | 実SBOM、実CVE scan、配布NOTICE、依存更新 |
| B5 | 手動限定Core CI、将来用生成スクリプト、hash/evidence schema、保護attestation例、再現性契約 | Build/Test/CI実行、EXE生成、署名、環境設定、Commit/Push |

root moduleの最低要件はGo 1.26.5、実行toolchainはGo 1.27.4。toolchain directiveだけをexact lockと扱わず、CIでは明示patch指定・GOTOOLCHAIN=local・実GOVERSION照合を組み合わせる。今回Goコマンドを再実行せず、B1〜B3の26 modules/58 edgesの実証跡を静的baselineとして利用した。

## ファイル案内

- ci/core-runtime/dependency-baseline.json: B1〜B3の実解決結果とgo.mod/go.sum hash。新しく解決した結果ではない。
- tool-lock.json: 公式release/固定Action sourceを照合した固定候補。実導入・実行・安全性認定ではない。
- policy.json / sbom-profile.json: Policyと生成契約。advisory-register.jsonはNOT_SCANNED。
- license-inventory.json / license-evidence.json: 原文・hash・適用scope。実配布物の全license確定ではない。
- build-targets.json: 新Core entrypoint未存在のためUNASSIGNED。legacy server/agentへ自動代替しない。
- build-evidence-index.schema.json / release-contract-map.json: D15-A06 closed Manifestを変えずに証跡を結合する。
- scripts/core-runtime/foundation.py: 承認後の限定runner。今回import・実行しない。静的構文確認のみ。
- .github/workflows/core-runtime-foundation.yml: 新規定義。workflow_dispatchのみ、default入力false、別の実行許可・exact Commit・target承認が必要。
- protected-attestation.workflow.yml.example: 非activeの保護builder例。明示exit 1で停止。署名も公開もしない。

## 実行防止と権限

今回Repository Settings/Variables/Secrets/Environments/Runnerを変更しない。
CIの開始には別途ユーザー許可、保護された承認Commit variableとenable variable、明示手動入力が必要。これらの変数は今回登録しない。guardは誤実行防止であり、同じ権限でworkflowを書換えられる者へのセキュリティ境界ではない。

保護Environmentの名前を書くだけでは保護されたことにならない。存在・required reviewers・branch restriction・bypass権限を管理者が確認してから別工程を有効化する。Environment未作成時の暗黙作成に依存しない。

## 状態を混同しない

- Phase 4: DESIGN_FROZEN / P4-FREEZE-20261115-R1。
- H01-D02 / D06、D15-A01〜A06: 採用済み契約を維持。
- D15-COMPAT-01: DEPENDENCY_GRAPH_ALIGNMENT=PASS、全体OPEN。Engine/VFS/WAL/FULL/Backup/耐久性/性能/EXE安全性は未検証。
- R-UX-01: LOW_NON_BLOCKING / OPEN。
- Phase 3-A: BLOCKED。Endpoint正式判断と別許可なしにMedia/GPU/PoCを再開しない。
- B4/B5: 設定準備・静的確認の完了。実Pipeline成功やリリース可能の意味ではない。

次工程はB4/B5資料の確認と、対象を明示した実行許可の判断である。Stage 0-C/Stage 1へ自動移行しない。
