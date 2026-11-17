# CI / Provenance / Reproducibility

## 1. 既存CIとの境界

既存ci-windows.ymlはPR/main Push/ManualでDesktop/.NET10/Smoke/Publish検証を行う。release-server.ymlとrelease-windows.ymlはtag/manualでBuild/Test/package/Release公開する。既存3ファイルとDockerfile/Makefile/package/signing scriptsを一切変更しない。
既存DockerのGo1.26.5やsetup-goのgo-version-fileと、刷新Coreの実行Go1.27.4を同じpinと扱わない。既存Workflowを使って今回のCoreをBuildしない。

| Trigger | 評価 | 今回 |
| --- | --- | --- |
| Pull Request | secretsなし、read-only token、使い捨てhosted runner。未信頼コードを実行する | 未有効 |
| Push | 対象branch/pathsをreviewして導入 | 未有効 |
| Release Tag | tag保護・署名・security floor・全artifact整合を別権限で確認 | 未有効 |
| Manual Dispatch | exact Commitと別実行承認を確認できる初期形 | 新規定義のみ。実行なし |
| Scheduled Security | frozen releaseを再照合、feed時点を記録 | 別承認まで未有効 |

workflow_dispatchはdefault branchへの配置等のGitHub条件も必要。今回Commit/Pushをしていないため稼働Workflowとして配備されたとは扱わない。

## 2. Jobの段階

Environment Profile / Toolchain実体照合 → Dependency Validation → 明示したCore packagesのStatic Source Analysis / Unit Tests → Windows/Linux compile → artifact SBOM / License / Advisory report → hashとunsigned evidence candidate。
Integration Testsは追加の隔離環境承認がある場合だけで、未実行をPASSにしない。build-targets.jsonは現時点UNASSIGNED。実装入口・unit/integration packageを承認済みexact Commitへ結合してから有効化する。arm64はcross compilation候補であり、native実行/Windows Desktop可用性の実証ではない。
既存./...、Desktop QA、Media/GPU、PoC、Service/DB操作を呼び出すscriptへ接続しない。未来のintegration実行にはDB等の対象・破棄条件・許可を別途特定する。

## 3. Actions権限

通常jobはcontents:readのみ。checkoutのpersist-credentials:false、cache:false、full Commit SHA固定。fork PRへSecretsを提供しない。pull_request_target/workflow_runで未信頼checkoutを行わない。GITHUB_TOKEN write-allやOIDC/attestation writeは通常jobに付与しない。
Actionのtop-level SHAだけでtransitive安全性が完成したとはしない。sourceとruns/usesを記録し、更新PRではAction自身と依存Action、runner互換性、advisory・権限をreviewする。可用性・組織policyが整った後、GitHub workflow dependency lockfileも別reviewできるが今回導入しない。

Workflow内GitHub式は信頼されたrun metadataに限定し、人の任意入力をshellへ展開しない。実行済みsourceはexact Commitとclean treeで照合し、generation中のgo.mod/go.sum変更は停止する。

## 4. Runner

| 構成 | 採用方針 |
| --- | --- |
| GitHub-hosted Windows/Linux | 初期Core CIの推奨。使い捨て、最小権限、固定image family。実image versionを記録 |
| Self-hosted Windows | 未信頼PRで禁止。常設token/他製品secret/内部networkへ到達し得る。導入は専用隔離・管理者承認後 |
| 署名専用 | 別のprotected builder/signing工程。鍵は通常CIへ配らない。製品/runner/鍵選択は保留 |

Endpoint保護除外や無効化、別EXEでの回避を前提にしない。Phase3-A安全性問題はこのCI定義では解消しない。
Hosted image familyはimmutable image pinではなく更新される。再現性やSLSAレベル達成をこの設定だけで宣言しない。

## 5. Artifact保存・受渡し

現在の定義は同一job内でbuildとhash/evidenceを結合し、upload後のraw Artifactを別jobでbuild由来と偽装しない。将来job間で渡す際はrun_id/run_attempt、producer workflow/Commit、upload digest、個別SHA256SUMS、size、expected path/set、署名attestationを照合し、unknown/extra/symlink/path traversalを拒否する。任意Artifactを実行したり、その中のmanifestを信頼の根にしない。

一時CI Artifactの14日保持は設計候補。公開release evidenceの長期不変保存・アクセス権・署名・再取得契約は別Gate。失敗jobの報告も保存するが、成果物やログにSecretsを含めない。通常CIはsecretsを使用しない。

## 6. Provenance

SLSA Provenance v1 predicateとin-toto statementを基準に、GitHubのOIDC-backed attestationを推奨する。build-evidence-index.jsonは**自己記録・未署名candidate**でありprovenance成立ではない。
保護された同一builder runでArtifactを生成し、repository/ref/Commit、workflow+revision、toolchain、dependency digest、image/OS/arch、args/env allowlist、SBOM/license/security refs、実timestamp、subject hashを結合する。attestationを生成するだけでなく、期待repo/workflow/Commit/ref/builder identity/digestで検証する。SLSA認定レベルは未判定。

protected-attestation.workflow.yml.exampleは非activeで明示BLOCKED。環境名だけで保護成立としない。GitHub plan/公開範囲によるattestation availability、required reviewer/branch rules、署名鍵製品等を確認後に別承認する。GitHub利用不可時は未署名JSONへfallbackせず、独立した承認済みbuilder/signer/verifier方式を選ぶ。

## 7. Code Signing

Authenticode、timestamp server、証明書/鍵/HSM/TPM/CA、ローテーション・失効、署名runnerは未選定。開発self-signed証明書をrelease用と扱わない。アプリの実行主体へ署名鍵を渡さない。
unsigned binary → 保護署名処理 → signed binaryは別bytesであり、unsigned/signed双方のdigest・変換証跡を保持する。最終配布bytesを再hashし、SBOM/artifact binding、attestation、D15-A06 Manifestを更新・署名・検証する。署名後改変やpackage再圧縮後は旧Manifestを再利用しない。Endpointの安全性認定とは別。

## 8. Reproducible Build仕様

| 項目 | 方針 |
| --- | --- |
| Toolchain/graph | Go1.27.4 exact、readonly、tuple/whole graph hash一致、tool graphを分離 |
| flags | -trimpath -buildvcs=false -compiler=gc -pgo=off -ldflags=-buildid=、CGO_ENABLED=0、-mod=readonly |
| source path/VCS | pathを除去。VCS無記録分は外部provenanceのexact Commit/clean treeで補う |
| timestamps | 実時刻をbinaryへ埋込まない。build時刻は外部証跡。package時刻は承認source commit基準 |
| module cache | CI cache初期無効。承認後もsumdb/lock/hash確認、未信頼PR cacheと署名工程を共有しない |
| OS/SDK | image family+実versionを記録。再現検証時は同一immutable VM/image/SDK/tool patchが必要 |
| env | 親環境のcopy禁止。固定/導出allowlistを子Process用に構成し、完全環境・Toolchain/Binding hashをpre-build Manifestへ記録。秘密は渡さない |
| packaging | sorted file list、固定timestamp/permissions、固定圧縮実装/level。未実装・未実測 |
| 検証 | 独立clean環境で同じinputを2回buildしてunsigned hash比較。差分は調査し署名timestamp差と分離 |

Build ID除去は万能ではない。Go binary/OS archごとの再現性、signature timestamp、ZIPの差分は未検証。今回は再現可能Build達成を宣言しない。

CIのjob timeout/処理timeout/保持14日とPython3.14.10は将来実行用の固定候補で、製品Runtimeのtimeout・保持や技術採用判断を上書きしない。対象profileはtracked fileに自分自身のCommitを埋め込まず、外部の保護Commitとprofile SHA-256で結合する。

## 9. SEC-03 Compiler Environment Gate

build-environment-profile.jsonは固定したpure-Go Core用の定義。Schema、tool-lockのprofile SHA-256、保護されたIPMV_CORE_BUILD_PROFILE_SHA256を一致させる。Profileのgo_toolchain_path / sha256は承認済bindingへの参照であり、未確定値を成功値やゼロhashで埋めない。

Workflowは各jobで最初のGo処理前にenvironment stageを実施する。順序は、固定hosted runnerとOS実測の識別、承認済bindingからのToolchain全tree/launcher/compile/link/asm照合、解決Profile・子環境の構成、allowlist/denylistとPATH検査、固定dependency digestの照合、最小go env probe、pre-build環境Manifest保存、実行Gate開放。通常Go呼出し時も実体と環境を再照合する。

保護変数IPMV_CORE_BUILD_BINDINGS_SHA256は登録済runner_profile_idから承認済binding bytesのSHA-256へのmap。参照先はci/core-runtime/environment-bindings/<登録ID>.json。今回、CIの実測bindingや保護変数は登録していない。欠落/未承認/不一致はGoやBuild開始前にBLOCKED。live環境をhashしただけで期待値へ採用するtrust-on-first-useは禁止。

BindingにはGo絶対path、launcher SHA-256、全fileのpath/size/SHA-256 inventory、tree digest、公式配布物SHA、出所証跡、host OS/arch、runner image ID/version、OS rootを含める。tree digestはpathで整列したinventoryをsort_keys=True / compact JSON / ensure_ascii=TrueでencodeしたbytesのSHA-256。承認者は取得済み公式archiveの由来と正規の期待inventoryを独立確認する。自己申告JSONだけでToolchainを信頼しない。

Go実行はbindingの絶対pathだけ。GOTOOLCHAIN=local、GOENV=off、GOWORK=off、Go1.27.4を維持。Module最低要件1.26.5と混同しない。Go1.27.4のcfg.EnvFile/MkEnvではGOENV=offはprobeで空文字、GOWORK=offはoffと報告される。inactive側のarch変数はprobeしない。

親環境を変更せず、子環境を空から構成する。GOAMD64=v1 / GOARM64=v8.0は対象archだけに設定。GOEXPERIMENT空は固定Toolchainの既定値を選択し、親の実験値を継承しない。Go/env file・GOCACHEPROG・proxy/private設定等も制御する。GOPATH/GOMODCACHE/GOCACHE/TMP/TEMP/HOME/USERPROFILE等は専用Output Rootのbundle外state directoryへ固定し、user cache/configを利用しない。

PATHは検証済GOROOT/binだけ。CC/CXX/CGO flags/pkg-config/MSVC/Windows SDK/MinGW/MSYSTEM/CL/INCLUDE/LIB/LIBPATHや他の未許可環境を子へ渡さない。WindowsのSystemRoot/WINDIRはkernel APIから取得してbindingと照合し、親値を採用しない。Native Compilerを自動探索せず、CGO_ENABLED=0 / gc compilerを維持する。CGO無効はOS・filesystem・Toolchain・stdlib等への依存が消えたことを意味しない。Media/GPU等のNative Workerは別Profile・別WorkflowでありPhase3-A BLOCKEDを維持する。

GitHub-hosted Windows / Linuxを別runner profileへ結合する。Self-hostedとlocalは別のBLOCKED Profileであり、今回の定義で自動採用しない。固定Workflow/手動承認/正確Commitの既存Gate、外部承認hash、OSからの測定、実file照合を併用する。RUNNER_*やJSON値の一致だけでhost真正性やOS侵害耐性を認定しない。Python/Git/setup actionsのbootstrapは独立した信頼境界で、権限・Secretsを拡張しない。

環境Manifestはbuild-environment-<stage>.jsonとしてBuild開始前に新規確定し、profile hash・binding ref・全正常化環境・観測したGo env・host/target・source Commit・dependency digestを保持する。status=VALIDATED_PRE_BUILDは将来そのpreflightが成功したときだけ出力し、Build/製品適合/署名PASSとはしない。今回の資料例はNOT_EXECUTED_EXAMPLE。

全環境Manifestとcommands-environment.jsonはSEC-02の固定payload集合へ含め、seal後は更新しない。build-evidence-indexの既存reports RefからD15-A06 build_provenanceへ接続し、closed Release Manifestに新しいfieldを追加しない。古いrun/target/source/binding/hashの再読込も拒否する。実体読み取りだけを承認済read-only Toolchain Rootへ限定し、SEC-01のArtifact/metadataの許可Rootを広げない。

Toolchain Rootとbuilder/private Output Rootは検証から使用完了まで他writerが変更しない前提。再parse point/Handle/hashの再照合は差替検出であり、悪意ある同Token processやadministrator/OS侵害に対する完全防御ではない。実Windows/Linuxでのfile identity/ACL/Node→Python→Go環境/Loader動作、image binding、Go probe、Build、reproducibilityは未実証。

今回の作業は定義・限定補正・静的確認だけ。Build/Test/CI/Scan/Commit/Pushの許可は追加しない。
