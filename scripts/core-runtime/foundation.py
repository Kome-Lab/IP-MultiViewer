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
import stat
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
    return json.loads(read_bytes(file).decode("utf-8"), object_pairs_hook=pairs,
                      parse_constant=lambda value: fail("invalid JSON " + value))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def filehash(file):
    return stable_file(file)[0]["sha256"]


def save(file, value):
    raw = (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    write_bytes(file, raw)


# SEC-01: only source reads and a separately designated, private output workspace.
# The approved builder must exclude concurrent/untrusted writers from OUTPUT_ROOT
# and its parents (Windows ACL / private runner workspace). These checks detect
# substitutions, not a compromised OS/administrator or hostile same-token writer.
OUTPUT_ROOT = None
OUTPUT = None
WORKSPACE_CONTEXT = None
CONTROL_FILES = ("artifact-set.json", "SHA256SUMS")


def lexical_path(value, base=None):
    raw = os.fspath(value)
    if not isinstance(raw, str) or not raw or any(ord(c) < 32 for c in raw):
        fail("invalid path text")
    win = pathlib.PureWindowsPath(raw)
    if raw.startswith(("//", "\\\\")) or (win.drive and not win.root):
        fail("UNC/device/drive-relative path rejected")
    if os.name != "nt" and (win.drive or "\\" in raw):
        fail("foreign Windows path rejected")
    if os.name == "nt" and win.root and not win.drive:
        fail("root-relative Windows path rejected")
    parts = re.split(r"[/\\]", raw)
    for i, part in enumerate(parts):
        if not part or (i == 0 and re.fullmatch(r"[A-Za-z]:", part)):
            continue
        if part in (".", "..") or part.casefold() == ".git":
            fail("dot/traversal/.git path rejected")
        if part.endswith((" ", ".")) or ":" in part or "~" in part:
            fail("ambiguous path/ADS/short-name rejected")
        if re.fullmatch(r"(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?", part):
            fail("reserved Windows path rejected")
    path = pathlib.Path(raw)
    if not path.is_absolute():
        if base is None:
            fail("explicit absolute root required")
        path = base / path
    return pathlib.Path(os.path.abspath(path))


def object_id(info):
    return (info.st_dev, info.st_ino, stat.S_IFMT(info.st_mode),
            getattr(info, "st_file_attributes", 0))


def file_id(info):
    return (*object_id(info), info.st_nlink, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns)


def path_chain(path):
    result = []
    for item in (*reversed(path.parents), path):
        try:
            info = item.lstat()
        except FileNotFoundError:
            break
        if stat.S_ISLNK(info.st_mode) or (getattr(info, "st_file_attributes", 0) &
                                         getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)):
            fail("symlink/junction/reparse point rejected")
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            fail("non-regular filesystem object rejected")
        if stat.S_ISREG(info.st_mode) and info.st_nlink != 1:
            fail("hard-linked file rejected")
        result.append((item, object_id(info)))
    return result


def checked_path(value, roots=None, exists=False):
    path = lexical_path(value)
    allowed = roots if roots is not None else (REPO, OUTPUT_ROOT)
    if not any(root is not None and path.is_relative_to(root) for root in allowed):
        fail("path outside approved repository/output roots")
    path_chain(path)  # Check existing ancestors even for a not-yet-created output.
    if WORKSPACE_CONTEXT is not None and path.is_relative_to(OUTPUT_ROOT):
        if list(object_id(OUTPUT_ROOT.lstat())) != WORKSPACE_CONTEXT["root_identity"]:
            fail("approved output root instance changed")
    resolved = path.resolve(strict=exists)
    if resolved != path or any(p.casefold() == ".git" for p in resolved.parts):
        fail("path alias/resolved .git reference rejected")
    if not any(root is not None and resolved.is_relative_to(root) for root in allowed):
        fail("resolved path escaped approved root")
    return path


def same_chain(before):
    for path, expected in before:
        current = path_chain(path)
        if not current or current[-1] != (path, expected):
            fail("filesystem parent/instance changed during operation")


def relative_path(root, value):
    # Metadata paths are canonical, portable, relative POSIX paths, never CLI roots.
    if not isinstance(value, str) or not value or "\\" in value:
        fail("invalid metadata path")
    if any(not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*", p)
           for p in value.split("/")):
        fail("absolute/traversal/hidden/nonportable metadata path rejected")
    result = checked_path(root / value)
    if not result.is_relative_to(root) or result == root:
        fail("metadata path escaped its declared root")
    return result


def stable_file(file, keep_bytes=False):
    file = checked_path(file, exists=True)
    parents = path_chain(file.parent)
    initial = file.lstat()
    if not stat.S_ISREG(initial.st_mode) or (keep_bytes and initial.st_size > LIMIT):
        fail("regular bounded input file required")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    hasher, blocks, size = hashlib.sha256(), [], 0
    with os.fdopen(os.open(file, flags), "rb") as handle:
        opened = os.fstat(handle.fileno())
        if file_id(initial) != file_id(opened):
            fail("input file changed before open")
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            size += len(block)
            if size > opened.st_size or (keep_bytes and size > LIMIT):
                fail("input grew during read")
            hasher.update(block)
            if keep_bytes:
                blocks.append(block)
        if size != opened.st_size or file_id(opened) != file_id(os.fstat(handle.fileno())):
            fail("input changed during read")
    same_chain(parents)
    if file_id(file.lstat()) != file_id(opened):
        fail("input replaced during read")
    checked_path(file, exists=True)
    return {"sha256": hasher.hexdigest(), "size_bytes": str(size)}, b"".join(blocks)


def read_bytes(file):
    return stable_file(file, keep_bytes=True)[1]


def ensure_directory(path):
    path = checked_path(path, roots=(OUTPUT_ROOT,))
    missing = []
    ancestor = path
    while not ancestor.exists():
        missing.append(ancestor)
        ancestor = ancestor.parent
    checked_path(ancestor, roots=(OUTPUT_ROOT,) if ancestor.is_relative_to(OUTPUT_ROOT)
                 else (OUTPUT_ROOT.parent,), exists=True)
    for directory in reversed(missing):
        before = path_chain(directory.parent)
        directory.mkdir(mode=0o700)
        same_chain(before)
        checked_path(directory, roots=(OUTPUT_ROOT,), exists=True)
    if not path.is_dir():
        fail("output directory required")
    return path


def seal_path():
    return OUTPUT_ROOT / ("seal-" + OUTPUT.name + ".json")


def writable_path(file):
    file = checked_path(file, roots=(OUTPUT_ROOT,))
    if OUTPUT is not None and file.is_relative_to(OUTPUT) and seal_path().exists():
        fail("sealed output is immutable; start a separately approved new run")
    ensure_directory(file.parent)
    if file.exists():
        fail("immutable output already exists: " + str(file))
    return file


def write_bytes(file, raw):
    file = writable_path(file)
    parents = path_chain(file.parent)
    # Private temporary + no-replace hard-link publication; no os.replace overwrite.
    temp = file.parent / ("pending-" + os.urandom(16).hex())
    checked_path(temp, roots=(OUTPUT_ROOT,))
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(temp, flags, 0o600), "wb") as handle:
        handle.write(raw)
        handle.flush()
        os.fsync(handle.fileno())
    same_chain(parents)
    checked_path(file, roots=(OUTPUT_ROOT,))
    checked_path(temp, roots=(OUTPUT_ROOT,), exists=True)
    os.link(temp, file)  # Fails rather than replacing an existing final name.
    temp.unlink()       # Only our random private temporary name, never caller data.
    same_chain(parents)
    if read_bytes(file) != raw:
        fail("published output identity/content mismatch")


def prepare_external(file):
    file = writable_path(file)
    return path_chain(file.parent)


def finish_external(file, parents):
    same_chain(parents)
    return file_ref(OUTPUT_ROOT, file)


def file_ref(root, file):
    file = checked_path(file, exists=True)
    if not file.is_relative_to(root):
        fail("reference outside declared root")
    facts, _ = stable_file(file)
    return {"path": file.relative_to(root).as_posix(), **facts}


def configure_workspace(args, commit):
    global OUTPUT_ROOT, OUTPUT, WORKSPACE_CONTEXT
    OUTPUT_ROOT = lexical_path(args.output_root)
    if OUTPUT_ROOT == OUTPUT_ROOT.parent or OUTPUT_ROOT.is_relative_to(REPO) or REPO.is_relative_to(OUTPUT_ROOT):
        fail("dedicated output root must be disjoint from source and not a filesystem root")
    checked_path(REPO, roots=(REPO,), exists=True)
    checked_path(OUTPUT_ROOT, roots=(OUTPUT_ROOT,))
    # Only the dedicated leaf may be created; its trusted parent must already exist.
    if not OUTPUT_ROOT.parent.is_dir():
        fail("approved private output parent must already exist")
    args.output = lexical_path(args.output, OUTPUT_ROOT)
    if args.output.parent != OUTPUT_ROOT or not re.fullmatch(r"[a-z0-9-]+", args.output.name):
        fail("output must be a named direct child of the dedicated output root")
    OUTPUT = args.output
    if args.tools is not None:
        args.tools = lexical_path(args.tools, OUTPUT_ROOT)
        if args.tools != OUTPUT_ROOT / "tools" or args.tools == OUTPUT:
            fail("tool cache must be the separate output-root/tools directory")
    if OUTPUT.name in ("tools", "workspace"):
        fail("reserved output name")
    if not OUTPUT_ROOT.exists():
        before = path_chain(OUTPUT_ROOT.parent)
        OUTPUT_ROOT.mkdir(mode=0o700)
        same_chain(before)
    checked_path(OUTPUT_ROOT, roots=(OUTPUT_ROOT,), exists=True)
    if os.name != "nt":
        info = OUTPUT_ROOT.stat()
        if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
            fail("output root must be private to the approved builder")
    WORKSPACE_CONTEXT = {"schema": "ipmv.core.output-workspace.v1", "root": str(OUTPUT_ROOT),
                         "root_identity": list(object_id(OUTPUT_ROOT.stat())),
                         "repository": str(REPO), "commit": commit,
                         "goos": args.goos, "goarch": args.goarch,
                         "run_id": os.environ.get("GITHUB_RUN_ID"),
                         "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT")}
    marker = OUTPUT_ROOT / "workspace.json"
    if marker.exists():
        if load(marker) != WORKSPACE_CONTEXT:
            fail("output workspace belongs to a different source/run/instance")
    else:
        if any(OUTPUT_ROOT.iterdir()):
            fail("unregistered output root must be empty; never adopt existing files")
        save(marker, WORKSPACE_CONTEXT)
    ensure_directory(OUTPUT)


def artifact_records(args, record):
    if record.get("actual_build") is not True or record.get("goos") != args.goos or record.get("goarch") != args.goarch:
        fail("matching actual build record required")
    items = record.get("artifacts")
    if not isinstance(items, list) or not items:
        fail("nonempty artifact set required")
    seen = set()
    for item in items:
        ident = item.get("id")
        if not isinstance(ident, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]*", ident) or ident in seen:
            fail("invalid/duplicate artifact identity")
        seen.add(ident)
        expected = "artifacts/" + ident + "." + args.goos + "-" + args.goarch + (".exe" if args.goos == "windows" else "")
        binary = relative_path(args.output, item.get("path"))
        if item["path"] != expected:
            fail("artifact path does not match identity/platform")
        current = file_ref(args.output, binary)
        if current != {k: item.get(k) for k in ("path", "sha256", "size_bytes")}:
            fail("artifact path/size/hash changed")
    return items


def output_files(args):
    files = []
    def visit(directory):
        checked_path(directory, roots=(OUTPUT_ROOT,), exists=True)
        for child in sorted(directory.iterdir()):
            relative_path(args.output, child.relative_to(args.output).as_posix())
            info = child.lstat()
            if stat.S_ISDIR(info.st_mode):
                visit(child)
            elif stat.S_ISREG(info.st_mode):
                files.append(child.relative_to(args.output).as_posix())
            else:
                fail("non-regular upload member")
    visit(args.output)
    if len({x.casefold() for x in files}) != len(files):
        fail("duplicate/case-colliding upload paths")
    return sorted(files)


def expected_payload(args):
    names = {"commands-seal.json"}
    if (args.output / "build-record.json").exists():
        record = load(args.output / "build-record.json")
        items = artifact_records(args, record)
        names.update({"dependency-validation.json", "build-record.json", "sbom-generation.json",
                      "license-report.json", "security-evaluation.json", "osv-scanner.json",
                      "osv-module-results.json", "build-evidence-index.json"})
        names.update("commands-" + stage + ".json" for stage in
                     ("dependency", "build", "sbom", "license", "vulnerability", "evidence"))
        for item in items:
            names.add(item["path"])
            names.update(item["id"] + suffix for suffix in (".cdx.json", ".spdx.json"))
        index = load(args.output / "build-evidence-index.json")
        if index.get("commit") != WORKSPACE_CONTEXT["commit"] or index.get("artifacts") != [
                {**item, "sbom_refs": [file_ref(args.output, relative_path(args.output, item["id"] + suffix))
                                       for suffix in (".cdx.json", ".spdx.json")]} for item in items]:
            fail("evidence index does not match actual artifacts/SBOMs")
        required_reports = {"dependency-validation.json", "build-record.json", "sbom-generation.json",
                            "license-report.json", "security-evaluation.json"}
        if set(index.get("reports", {})) != required_reports:
            fail("unexpected evidence report set")
        for name, ref in index["reports"].items():
            if ref != file_ref(args.output, relative_path(args.output, name)):
                fail("evidence report path/size/hash mismatch")
    elif (args.output / "static-result.json").exists():
        stages = ["static", "unit"]
        if (args.output / "integration-result.json").exists():
            stages.append("integration")
        names.update(stage + "-result.json" for stage in stages)
        names.update("commands-" + stage + ".json" for stage in stages)
    else:
        names.update({"dependency-validation.json", "commands-dependency.json"})
    return sorted(names)


def seal_output(args, lock, commit):
    # SEC-02: all commands (including commands-seal.json) have finished before here.
    payload = expected_payload(args)
    if output_files(args) != payload:
        fail("final output set differs from the explicit payload policy")
    refs = [file_ref(args.output, relative_path(args.output, name)) for name in payload]
    if refs != [file_ref(args.output, relative_path(args.output, name)) for name in output_files(args)]:
        fail("payload changed while freezing the final set")
    manifest = {"schema": "ipmv.core.artifact-set.v1", "status": "SEALED_UNSIGNED_CANDIDATE",
                "commit": commit, "dependency_set_digest": lock["dependency_set_digest"],
                "output": args.output.name, "payload": refs,
                "upload_control_files": list(CONTROL_FILES),
                "checksum_exclusions": list(CONTROL_FILES),
                "exclusion_reason": "Explicit self-reference control files; anchored by an outside-bundle seal receipt. No other upload exclusions."}
    save(args.output / CONTROL_FILES[0], manifest)
    sums = "".join(ref["sha256"] + "  " + ref["path"] + "\n" for ref in refs)
    write_bytes(args.output / "SHA256SUMS", sums.encode("utf-8"))
    save(seal_path(), {"schema": "ipmv.core.output-seal.v1", "workspace": WORKSPACE_CONTEXT,
                       "output": args.output.name, "payload": refs,
                       "controls": [file_ref(args.output, args.output / name) for name in CONTROL_FILES]})
    verify_upload(args, lock, commit)


def verify_upload(args, lock, commit):
    # Read-only, no command-log file is emitted into the sealed upload directory.
    seal = load(seal_path())
    manifest = load(args.output / CONTROL_FILES[0])
    if seal.get("workspace") != WORKSPACE_CONTEXT or seal.get("output") != args.output.name:
        fail("seal belongs to a different workspace/run")
    if manifest.get("commit") != commit or manifest.get("dependency_set_digest") != lock["dependency_set_digest"]:
        fail("manifest source/dependency mismatch")
    if manifest.get("status") != "SEALED_UNSIGNED_CANDIDATE" or manifest.get("output") != args.output.name:
        fail("incomplete or mismatched artifact manifest")
    payload = expected_payload(args)
    refs = [file_ref(args.output, relative_path(args.output, name)) for name in payload]
    if manifest.get("payload") != refs or seal.get("payload") != refs:
        fail("payload existence/path/size/SHA-256/manifest mismatch")
    if manifest.get("upload_control_files") != list(CONTROL_FILES) or manifest.get("checksum_exclusions") != list(CONTROL_FILES):
        fail("unknown checksum exclusion policy")
    if output_files(args) != sorted(payload + list(CONTROL_FILES)):
        fail("upload set differs from manifest/checksum set plus explicit controls")
    controls = [file_ref(args.output, args.output / name) for name in CONTROL_FILES]
    if seal.get("controls") != controls:
        fail("manifest/checksum control bytes changed after sealing")
    expected_sums = "".join(ref["sha256"] + "  " + ref["path"] + "\n" for ref in refs).encode("utf-8")
    if read_bytes(args.output / "SHA256SUMS") != expected_sums:
        fail("SHA256SUMS membership/hash mismatch")
    # A second complete pass detects changes during verification. Private workspace
    # ownership must exclude writers until the following pinned upload action ends.
    if refs != [file_ref(args.output, relative_path(args.output, name)) for name in payload] or controls != [
            file_ref(args.output, args.output / name) for name in CONTROL_FILES] or output_files(args) != sorted(payload + list(CONTROL_FILES)):
        fail("sealed upload changed during final verification")


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
    text = read_bytes(REPO / "go.mod").decode("utf-8")
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
        destination = checked_path(REPO / value, roots=(REPO,), exists=True)
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
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", ident):
            fail("invalid artifact identifier")
        pkgs = packages({"target": [target["package"]]}, "target")
        binary = args.output / "artifacts" / (ident + ("." + args.goos + "-" + args.goarch) +
                                               (".exe" if args.goos == "windows" else ""))
        binary_parents = prepare_external(binary)
        if binary.exists():
            fail("refuse artifact replacement")
        argv = [args.go, "build", "-mod=readonly", "-trimpath", "-buildvcs=false",
                "-ldflags=-buildid=", "-o", binary, pkgs[0]]
        command(argv, env=env)
        finish_external(binary, binary_parents)
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
    ensure_directory(directory)
    archive = directory / asset["file_name"]
    if not archive.exists():
        request = urllib.request.Request(asset["url"], headers={"User-Agent": "IPMV-approved-core-ci"})
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read(LIMIT + 1)
        if len(raw) > LIMIT or digest(raw) != asset["sha256"]:
            fail("official tool asset size/hash mismatch")
        write_bytes(archive, raw)
    if filehash(archive) != asset["sha256"]:
        fail("tool cache hash mismatch")
    binary = directory / (tool_id + (".exe" if host_os == "windows" else ""))
    if tool_id == "syft":
        if archive.suffix == ".zip":
            with zipfile.ZipFile(io.BytesIO(read_bytes(archive))) as bundle:
                entries = [i for i in bundle.infolist()
                           if pathlib.PurePosixPath(i.filename).name == binary.name]
                if len(entries) != 1 or entries[0].file_size > LIMIT:
                    fail("ambiguous tool archive")
                raw = bundle.read(entries[0])
        else:
            with tarfile.open(fileobj=io.BytesIO(read_bytes(archive)), mode="r:gz") as bundle:
                entries = [i for i in bundle.getmembers()
                           if pathlib.PurePosixPath(i.name).name == binary.name and i.isfile()]
                if len(entries) != 1 or entries[0].size > LIMIT:
                    fail("ambiguous tool archive")
                stream = bundle.extractfile(entries[0])
                if stream is None:
                    fail("missing tool entry")
                raw = stream.read(LIMIT + 1)
    else:
        raw = read_bytes(archive)
    if binary.exists():
        if filehash(binary) != digest(raw):
            fail("extracted tool modified")
    else:
        write_bytes(binary, raw)
        if host_os != "windows":
            binary.chmod(0o700)
    return binary


def sbom(args):
    record = load(args.output / "build-record.json")
    if record["actual_build"] is not True:
        fail("actual artifact required; no synthetic SBOM")
    syft = tool_path("syft", args)
    for artifact in artifact_records(args, record):
        binary = relative_path(args.output, artifact["path"])
        if filehash(binary) != artifact["sha256"]:
            fail("artifact changed before SBOM")
        stem = artifact["id"]
        outputs = [args.output / (stem + ".cdx.json"), args.output / (stem + ".spdx.json")]
        output_parents = [prepare_external(file) for file in outputs]
        command([syft, "scan", binary, "-o", "cyclonedx-json@1.6=" + str(outputs[0]),
                 "-o", "spdx-json@2.3=" + str(outputs[1])])
        for file, parents in zip(outputs, output_parents):
            finish_external(file, parents)
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
    for item in artifact_records(args, build_record):
        binary = relative_path(args.output, item["path"])
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["dependency", "static", "unit", "integration",
                                          "build", "sbom", "license", "vulnerability", "evidence",
                                          "seal", "verify-upload"])
    parser.add_argument("--output-root", default=os.environ.get("IPMV_CORE_OUTPUT_ROOT"),
                        required=not os.environ.get("IPMV_CORE_OUTPUT_ROOT"))
    parser.add_argument("--output", required=True)
    parser.add_argument("--tools")
    parser.add_argument("--go", default="go")
    parser.add_argument("--goos", choices=["windows", "linux"], required=True)
    parser.add_argument("--goarch", choices=["amd64", "arm64"], required=True)
    args = parser.parse_args()
    commit = guarded()
    configure_workspace(args, commit)
    lock = lockcheck()
    if args.stage != "verify-upload" and seal_path().exists():
        fail("sealed output cannot be reopened for mutation")
    # Serialize cooperating stages; stale tickets fail closed, never auto-delete.
    stage_lock = checked_path(OUTPUT_ROOT / "stage.lock", roots=(OUTPUT_ROOT,))
    ticket = os.urandom(32)
    write_bytes(stage_lock, ticket)
    try:
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
                    fail("explicit output-root/tools cache required")
                (sbom if args.stage == "sbom" else lambda a: vulnerability(a, lock))(args)
            elif args.stage == "license":
                license_report(args, lock)
            elif args.stage == "evidence":
                evidence(args, lock, commit)
            elif args.stage == "verify-upload":
                verify_upload(args, lock, commit)
        finally:
            if before != (filehash(REPO / "go.mod"), filehash(REPO / "go.sum")):
                fail("unexpected dependency file mutation; stop without restoration")
            if args.stage != "verify-upload" and COMMAND_LOG:
                save(args.output / ("commands-" + args.stage + ".json"), COMMAND_LOG)
        if args.stage == "seal":
            seal_output(args, lock, commit)
    finally:
        if read_bytes(stage_lock) != ticket:
            fail("stage lock changed; leave it untouched and stop")
        stage_lock.unlink()  # Our verified ticket only, outside the upload directory.


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("CORE_FOUNDATION_BLOCKED: " + str(exc), file=sys.stderr)
        sys.exit(2)
