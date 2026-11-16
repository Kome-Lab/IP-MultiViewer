#!/usr/bin/env python3
"""Future non-GPU Core foundation runner. B4/B5 prepares this file; it is NOT run now.
No product process, DB, Service, ACL, secret, updater or Media/GPU operation exists here.
Every operational subcommand requires separate execution authorization and a clean,
exact approved commit. This is an accidental-execution guard, not an OS security boundary.
"""
from __future__ import annotations
import argparse
import datetime as dt
import hashlib
import io
import json
import os
import pathlib
import platform
import re
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

REPO = pathlib.Path(__file__).resolve().parents[2]
CONFIG = REPO / "ci" / "core-runtime"
LIMIT = 256 * 1024 * 1024
PREFIX = "SEPARATELY_APPROVED_NON_GPU_CORE"
COMMAND_LOG = []


def fail(message):
    raise RuntimeError(message)


def pairs(items):
    result = {}
    for key, value in items:
        if key in result:
            fail("duplicate JSON key: " + key)
        result[key] = value
    return result


def load(file):
    with pathlib.Path(file).open(encoding="utf-8") as handle:
        return json.load(handle, object_pairs_hook=pairs,
                         parse_constant=lambda value: fail("invalid JSON " + value))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def filehash(file):
    hasher = hashlib.sha256()
    with pathlib.Path(file).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def save(file, value):
    file = pathlib.Path(file)
    file.parent.mkdir(parents=True, exist_ok=True)
    if file.exists():
        fail("immutable output already exists: " + str(file))
    with file.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2, allow_nan=False)
        handle.write("\n")


def command(args, env=None, accepted=(0,)):
    # Never use shell=True or execute payload-supplied commands.
    result = subprocess.run([str(a) for a in args], cwd=REPO, env=env,
                            capture_output=True, text=True, encoding="utf-8",
                            errors="strict", timeout=900, check=False)
    COMMAND_LOG.append({"argv": [str(a) for a in args], "exit_code": result.returncode,
                        "stdout": result.stdout, "stderr": result.stderr})
    if result.returncode not in accepted:
        fail("command failed; see bounded command record: " + str(args[0]))
    return result


def git(*args):
    return command(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
                    "-c", "gc.auto=0", "-C", REPO, *args]).stdout.strip()


def guarded():
    if os.environ.get("IPMV_CORE_EXECUTION_PERMIT") != PREFIX:
        fail("execution is not authorized")
    commit = git("rev-parse", "HEAD")
    expected = os.environ.get("IPMV_APPROVED_SOURCE_COMMIT", "")
    if not re.fullmatch(r"[0-9a-f]{40}", expected) or commit != expected:
        fail("exact approved source commit required")
    if git("status", "--porcelain=v1", "--untracked-files=all"):
        fail("clean committed source required; never clean/reset/restore here")
    if os.environ.get("GITHUB_ACTIONS") == "true":
        if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch":
            fail("only separately approved manual execution")
        if os.environ.get("GITHUB_REPOSITORY") != "Kome-Lab/IP-MultiViewer":
            fail("unexpected repository")
    return commit


def lockcheck():
    lock = load(CONFIG / "dependency-baseline.json")
    for name, field in (("go.mod", "go_mod_sha256"), ("go.sum", "go_sum_sha256")):
        if filehash(REPO / name) != lock[field]:
            fail(name + " differs; request dependency review, do not regenerate")
    if (REPO / "go.work").exists() or (REPO / "vendor").exists():
        fail("workspace/vendor requires explicit separate adoption")
    text = (REPO / "go.mod").read_text(encoding="utf-8")
    if re.search(r"(?m)^\s*(replace|exclude)\b", text):
        fail("replace/exclude rejected by dependency policy")
    if not re.search(r"(?m)^go 1\.26\.5$", text):
        fail("module minimum differs from approved baseline")
    if not re.search(r"(?m)^toolchain go1\.27\.4$", text):
        fail("toolchain recommendation differs")
    return lock


def goenv(args):
    env = os.environ.copy()
    env.update({"GOTOOLCHAIN": "local", "GOENV": "off", "GOWORK": "off",
                "GOFLAGS": "-mod=readonly", "CGO_ENABLED": "0",
                "GOPROXY": "https://proxy.golang.org", "GOSUMDB": "sum.golang.org",
                "GOPRIVATE": "", "GONOPROXY": "", "GONOSUMDB": "",
                "GOVCS": "*:off", "GOTELEMETRY": "off"})
    env["GOOS"], env["GOARCH"] = args.goos, args.goarch
    actual = command([args.go, "env", "GOVERSION"], env=env).stdout.strip()
    if actual != "go1.27.4":
        fail("exact Go 1.27.4 required; never download an implicit toolchain")
    return env


def json_stream(text):
    decoder, pos, items = json.JSONDecoder(object_pairs_hook=pairs), 0, []
    while pos < len(text):
        while pos < len(text) and text[pos].isspace():
            pos += 1
        if pos < len(text):
            value, pos = decoder.raw_decode(text, pos)
            items.append(value)
    return items


def dependency(args, lock):
    env = goenv(args)
    selected = json_stream(command([args.go, "list", "-m", "-mod=readonly",
                                    "-json", "all"], env=env).stdout)
    fields = (("path", "Path"), ("version", "Version"), ("main", "Main"),
              ("sum", "Sum"), ("go_mod_sum", "GoModSum"), ("go_version", "GoVersion"))
    values = []
    for module in selected:
        if module.get("Replace"):
            fail("unexpected selected replacement")
        values.append({key: bool(module.get(src)) if key == "main" else module.get(src)
                       for key, src in fields})
    values.sort(key=lambda value: value["path"])
    edges = sorted(command([args.go, "mod", "graph"], env=env).stdout.splitlines())
    if values != lock["modules"] or edges != lock["graph_edges"]:
        fail("dependency graph drift; no forced replace or automatic baseline rewrite")
    command([args.go, "mod", "verify"], env=env)
    lockcheck()
    save(args.output / "dependency-validation.json",
         {"status": "DEPENDENCY_GRAPH_MATCH", "modules": values, "edges": edges,
          "dependency_set_digest": lock["dependency_set_digest"],
          "toolchain": "go1.27.4", "runtime_sqlite_tested": False})


def targets(args):
    profile = load(CONFIG / "build-targets.json")
    if profile["status"] != "APPROVED_FOR_EXECUTION":
        fail("real Core target mapping not approved; legacy server/agent are not substitutes")
    expected = os.environ.get("IPMV_CORE_TARGETS_SHA256", "")
    if not re.fullmatch(r"[0-9a-f]{64}", expected) or filehash(CONFIG / "build-targets.json") != expected:
        fail("externally approved target-profile digest required; never self-reference its containing commit")
    return profile


def packages(profile, key):
    values = profile.get(key, [])
    if not values:
        fail("empty " + key + " is NOT a passing test")
    for value in values:
        if not isinstance(value, str) or not re.fullmatch(r"\./[A-Za-z0-9_./-]+", value):
            fail("invalid explicit package")
        if value == "./..." or ".." in value.split("/") or "..." in value:
            fail("unbounded package selection rejected")
        if any(x in value for x in ("desktop-windows", "renewal-poc")):
            fail("Media/Desktop/PoC target rejected")
        destination = (REPO / value).resolve()
        if not destination.is_relative_to(REPO) or not destination.is_dir():
            fail("package path does not exist inside repository")
    return values


def analysis_or_tests(args):
    profile = targets(args)
    key = {"static": "source_analysis_packages", "unit": "unit_test_packages",
           "integration": "integration_test_packages"}[args.stage]
    pkgs = packages(profile, key)
    env = goenv(args)
    if args.stage == "static":
        command([args.go, "vet", "-mod=readonly", *pkgs], env=env)
    else:
        # Integration packages need a separate approved environment and data plan.
        if args.stage == "integration" and os.environ.get("IPMV_INTEGRATION_PERMIT") != "APPROVED_ISOLATED_NON_GPU":
            fail("isolated integration execution not authorized")
        command([args.go, "test", "-mod=readonly", "-count=1", "-timeout=120s", *pkgs], env=env)
    save(args.output / (args.stage + "-result.json"),
         {"status": "COMMANDS_SUCCEEDED", "scope": pkgs, "goos": args.goos,
          "goarch": args.goarch, "not_media_gpu_or_durability_acceptance": True})


def build(args, lock, commit):
    profile = targets(args)
    env = goenv(args)
    platform_key = args.goos + "/" + args.goarch
    selected = [x for x in profile["targets"] if platform_key in x["platforms"]]
    if not selected:
        fail("no approved build target")
    records = []
    start = dt.datetime.now(dt.timezone.utc).isoformat()
    for target in selected:
        ident = target["id"]
        if not re.fullmatch(r"[a-z0-9-]+", ident):
            fail("invalid artifact identifier")
        pkgs = packages({"target": [target["package"]]}, "target")
        binary = args.output / "artifacts" / (ident + ("." + args.goos + "-" + args.goarch) +
                                               (".exe" if args.goos == "windows" else ""))
        binary.parent.mkdir(parents=True, exist_ok=True)
        if binary.exists():
            fail("refuse artifact replacement")
        argv = [args.go, "build", "-mod=readonly", "-trimpath", "-buildvcs=false",
                "-ldflags=-buildid=", "-o", binary, pkgs[0]]
        command(argv, env=env)
        metadata = command([args.go, "version", "-m", binary], env=env).stdout
        records.append({"id": ident, "artifact_kind": target["artifact_kind"],
                        "path": binary.relative_to(args.output).as_posix(),
                        "sha256": filehash(binary), "size_bytes": str(binary.stat().st_size),
                        "version": target["version"], "go_build_information": metadata,
                        "compatibility_profile_ref": target["compatibility_profile_ref"]})
    allowed_environment = {name: env.get(name) for name in
                           ("GOTOOLCHAIN", "GOENV", "GOWORK", "GOFLAGS", "CGO_ENABLED",
                            "GOOS", "GOARCH", "GOPROXY", "GOSUMDB", "GOVCS")}
    save(args.output / "build-record.json",
         {"schema": "ipmv.core.build-record.v1", "actual_build": True,
          "repository": "https://github.com/Kome-Lab/IP-MultiViewer.git", "commit": commit,
          "branch": git("symbolic-ref", "--short", "-q", "HEAD") if not os.environ.get("GITHUB_ACTIONS") else os.environ.get("GITHUB_REF"),
          "workflow_ref": os.environ.get("GITHUB_WORKFLOW_REF"),
          "workflow_sha": os.environ.get("GITHUB_WORKFLOW_SHA"),
          "run_id": os.environ.get("GITHUB_RUN_ID"), "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
          "go_toolchain": "1.27.4", "python": platform.python_version(),
          "runner_os": os.environ.get("RUNNER_OS"), "runner_image": os.environ.get("ImageOS"),
          "runner_image_version": os.environ.get("ImageVersion"),
          "dependency_set_digest": lock["dependency_set_digest"],
          "goos": args.goos, "goarch": args.goarch, "environment": allowed_environment,
          "started_at": start, "finished_at": dt.datetime.now(dt.timezone.utc).isoformat(),
          "artifacts": records, "signed": False, "attested": False,
          "sqlite_runtime_engine_verified": False})


def tool_path(tool_id, args):
    lock = load(CONFIG / "tool-lock.json")
    tool = next(x for x in lock["tools"] if x["id"] == tool_id)
    host_os = "windows" if sys.platform == "win32" else "linux"
    host_arch = {"AMD64": "amd64", "x86_64": "amd64", "aarch64": "arm64",
                 "ARM64": "arm64"}.get(platform.machine())
    if not host_arch:
        fail("unsupported tool host")
    suffix = host_os + "_" + host_arch
    asset = next((x for x in tool["assets"] if suffix in x["file_name"]), None)
    if asset is None or not asset["sha256"]:
        fail("no verified official tool asset")
    directory = args.tools / tool_id / tool["version"]
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / asset["file_name"]
    if not archive.exists():
        request = urllib.request.Request(asset["url"], headers={"User-Agent": "IPMV-approved-core-ci"})
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read(LIMIT + 1)
        if len(raw) > LIMIT or digest(raw) != asset["sha256"]:
            fail("official tool asset size/hash mismatch")
        with archive.open("xb") as handle:
            handle.write(raw)
    if filehash(archive) != asset["sha256"]:
        fail("tool cache hash mismatch")
    binary = directory / (tool_id + (".exe" if host_os == "windows" else ""))
    if tool_id == "syft":
        if archive.suffix == ".zip":
            with zipfile.ZipFile(archive) as bundle:
                entries = [i for i in bundle.infolist()
                           if pathlib.PurePosixPath(i.filename).name == binary.name]
                if len(entries) != 1 or entries[0].file_size > LIMIT:
                    fail("ambiguous tool archive")
                raw = bundle.read(entries[0])
        else:
            with tarfile.open(archive, "r:gz") as bundle:
                entries = [i for i in bundle.getmembers()
                           if pathlib.PurePosixPath(i.name).name == binary.name and i.isfile()]
                if len(entries) != 1 or entries[0].size > LIMIT:
                    fail("ambiguous tool archive")
                stream = bundle.extractfile(entries[0])
                if stream is None:
                    fail("missing tool entry")
                raw = stream.read(LIMIT + 1)
    else:
        raw = archive.read_bytes()
    if binary.exists():
        if filehash(binary) != digest(raw):
            fail("extracted tool modified")
    else:
        with binary.open("xb") as handle:
            handle.write(raw)
        if host_os != "windows":
            binary.chmod(0o700)
    return binary


def sbom(args):
    record = load(args.output / "build-record.json")
    if record["actual_build"] is not True:
        fail("actual artifact required; no synthetic SBOM")
    syft = tool_path("syft", args)
    for artifact in record["artifacts"]:
        binary = args.output / artifact["path"]
        if filehash(binary) != artifact["sha256"]:
            fail("artifact changed before SBOM")
        stem = artifact["id"]
        outputs = [args.output / (stem + ".cdx.json"), args.output / (stem + ".spdx.json")]
        if any(file.exists() for file in outputs):
            fail("do not replace SBOM revision")
        command([syft, "scan", binary, "-o", "cyclonedx-json@1.6=" + str(outputs[0]),
                 "-o", "spdx-json@2.3=" + str(outputs[1])])
        cdx, spdx = load(outputs[0]), load(outputs[1])
        if cdx.get("specVersion") != "1.6" or spdx.get("spdxVersion") != "SPDX-2.3":
            fail("SBOM format drift")
    save(args.output / "sbom-generation.json",
         {"actual_artifact_subjects": record["artifacts"], "tool": "syft 1.57.1",
          "native_closure": "UNKNOWN_PENDING_RECONCILIATION",
          "sqlite_engine": "SOURCE_DOCUMENTED_NOT_RUNTIME_VERIFIED",
          "complete_inventory_or_safety_certificate": False})


def license_report(args, lock):
    inventory = load(CONFIG / "license-inventory.json")
    if inventory["baseline_digest"] != lock["dependency_set_digest"]:
        fail("license inventory baseline mismatch")
    evidence = load(CONFIG / "license-evidence.json")
    indexed = {(x["module"], x["path"]): x for x in evidence["modules"]}
    for module in inventory["modules"]:
        for ref in module["license_evidence"]:
            raw = indexed.get((module["module"], ref["path"]))
            if raw is None or digest(raw["text"].encode("utf-8")) != ref["sha256"]:
                fail("license source evidence changed")
    save(args.output / "license-report.json",
         {"schema": "ipmv.core.license-report.v1", "dependency_set_digest": lock["dependency_set_digest"],
          "status": "REVIEW_REQUIRED", "artifact_inclusion": "NOT_YET_APPROVED",
          "inventory": inventory, "final_NOTICE": False,
          "reason": "First-party distribution terms, MPL/testdata/native inclusion and exact artifacts require review"})


def vulnerability(args, lock):
    tool = tool_path("osv-scanner", args)
    packages_input = [{"package": {"name": x["path"], "version": x["version"], "ecosystem": "Go"}}
                      for x in lock["modules"] if not x["main"]]
    packages_input += [{"package": {"name": x, "version": "1.27.4", "ecosystem": "Go"}}
                       for x in ("stdlib", "toolchain")]
    source = args.output / "osv-scanner.json"
    save(source, {"results": [{"packages": packages_input}]})
    raw = command([tool, "scan", "source", "--lockfile", "osv-scanner:" + str(source),
                   "--format=json"], accepted=(0, 1))
    parsed = json.loads(raw.stdout, object_pairs_hook=pairs)
    if not isinstance(parsed, dict) or not isinstance(parsed.get("results"), list):
        fail("scanner output missing complete results array; UNKNOWN, not zero findings")
    save(args.output / "osv-module-results.json", parsed)
    findings = [v for result in parsed.get("results", [])
                for package in result.get("packages", []) for v in package.get("vulnerabilities", [])]
    save(args.output / "security-evaluation.json",
         {"schema": "ipmv.core.security-evaluation.v1",
          "status": "BLOCKED_FINDINGS_REVIEW" if findings else "REVIEW_REQUIRED_NATIVE_AND_REACHABILITY",
          "scanner": "osv-scanner 2.7.1", "known_findings_returned": len(findings),
          "exit_code": raw.returncode, "coverage": "25 selected Go modules + stdlib + toolchain",
          "native_SQLite_CVE_mapping": "UNKNOWN", "reachability": "UNKNOWN",
          "optional_govulncheck": "NOT_RUN_PENDING_SEPARATE_TOOL_GRAPH_APPROVAL",
          "zero_findings_does_not_mean_safe": True,
          "not_an_approved_release": True})
    if findings:
        fail("Advisory findings require triage; no blanket or automatic suppression")


def evidence(args, lock, commit):
    build_record = load(args.output / "build-record.json")
    if not build_record.get("actual_build") or build_record["commit"] != commit:
        fail("actual matching build required")
    required = ["dependency-validation.json", "build-record.json", "sbom-generation.json",
                "license-report.json", "security-evaluation.json"]
    refs = {}
    for name in required:
        file = args.output / name
        if not file.is_file():
            fail("missing required evidence " + name)
        refs[name] = {"path": name, "sha256": filehash(file),
                      "size_bytes": str(file.stat().st_size)}
    artifacts = []
    for item in build_record["artifacts"]:
        binary = args.output / item["path"]
        if filehash(binary) != item["sha256"]:
            fail("artifact replaced; regenerate/re-attest all dependent evidence")
        sboms = []
        for suffix in (".cdx.json", ".spdx.json"):
            file = args.output / (item["id"] + suffix)
            sboms.append({"path": file.name, "sha256": filehash(file),
                          "size_bytes": str(file.stat().st_size)})
        artifacts.append({**item, "sbom_refs": sboms})
    result = {"schema": "ipmv.core.build-evidence-index.v1", "release_eligible": False,
              "status": "UNATTESTED_REVIEW_CANDIDATE", "commit": commit,
              "dependency_set_digest": lock["dependency_set_digest"],
              "artifacts": artifacts, "reports": refs,
              "provenance_ref": None, "signer_identity": None,
              "compatibility_approved": False, "security_revision_approved": False,
              "reason": "Not a D15-A06 Release Manifest; protected attestation, license/security closure and signing pending"}
    save(args.output / "build-evidence-index.json", result)
    # This checksum list may be the subject of a later same-build protected attestation.
    files = sorted(p for p in args.output.rglob("*")
                   if p.is_file() and p.name != "SHA256SUMS" and "tools" not in p.parts)
    with (args.output / "SHA256SUMS").open("x", encoding="utf-8", newline="\n") as handle:
        for file in files:
            handle.write(filehash(file) + "  " + file.relative_to(args.output).as_posix() + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["dependency", "static", "unit", "integration",
                                          "build", "sbom", "license", "vulnerability", "evidence"])
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--tools", type=pathlib.Path)
    parser.add_argument("--go", default="go")
    parser.add_argument("--goos", choices=["windows", "linux"], required=True)
    parser.add_argument("--goarch", choices=["amd64", "arm64"], required=True)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if args.output == REPO or args.output in REPO.parents or args.output.is_relative_to(CONFIG):
        fail("unsafe output directory")
    if args.tools is not None:
        args.tools = args.tools.resolve()
        if args.tools.is_relative_to(REPO):
            fail("tools must be outside source repository")
    commit = guarded()
    lock = lockcheck()
    before = (filehash(REPO / "go.mod"), filehash(REPO / "go.sum"))
    try:
        if args.stage == "dependency":
            dependency(args, lock)
        elif args.stage in ("static", "unit", "integration"):
            analysis_or_tests(args)
        elif args.stage == "build":
            build(args, lock, commit)
        elif args.stage in ("sbom", "vulnerability"):
            if args.tools is None:
                fail("explicit outside-repository tools directory required")
            (sbom if args.stage == "sbom" else lambda a: vulnerability(a, lock))(args)
        elif args.stage == "license":
            license_report(args, lock)
        else:
            evidence(args, lock, commit)
    finally:
        if before != (filehash(REPO / "go.mod"), filehash(REPO / "go.sum")):
            fail("unexpected dependency file mutation; stop without restoration")
        if COMMAND_LOG:
            save(args.output / ("commands-" + args.stage + ".json"), COMMAND_LOG)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("CORE_FOUNDATION_BLOCKED: " + str(exc), file=sys.stderr)
        sys.exit(2)
