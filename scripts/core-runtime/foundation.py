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


def stable_file(file, keep_bytes=False, roots=None):
    file = checked_path(file, roots=roots, exists=True)
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
    checked_path(file, roots=roots, exists=True)
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
    if OUTPUT.name in ("tools", "workspace", "build-environment-state"):
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
    names = {"commands-seal.json", "commands-environment.json", "build-environment-environment.json"}
    if (args.output / "build-record.json").exists():
        record = load(args.output / "build-record.json")
        items = artifact_records(args, record)
        names.update({"dependency-validation.json", "build-record.json", "sbom-generation.json",
                      "license-report.json", "security-evaluation.json", "osv-scanner.json",
                      "osv-module-results.json", "build-evidence-index.json"})
        names.update("commands-" + stage + ".json" for stage in
                     ("dependency", "build", "sbom", "license", "vulnerability", "evidence"))
        names.update("build-environment-" + stage + ".json" for stage in ("environment", "dependency", "build"))
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
        required_reports.update("build-environment-" + stage + ".json" for stage in ("environment", "dependency", "build"))
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
        names.update("build-environment-" + stage + ".json" for stage in stages)
    else:
        names.update({"dependency-validation.json", "commands-dependency.json", "build-environment-dependency.json"})
    for name in sorted(n for n in names if n.startswith("build-environment-")):
        validate_environment_report(args, name)
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
    if env is not None:
        compiler_command_guard(args, env)
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



# SEC-03: trusted orchestration reads approval pins; Go receives only this profile.
# Bootstrap Python/Git/setup actions and the OS remain separate trust boundaries.
ACTIVE_BUILD_ENVIRONMENT = None
BUILD_ENVIRONMENT_MEMBERS = ("environment", "dependency", "static", "unit", "integration", "build")


def validate_contract(value, schema):
    # Closed subset used by our fixed v1 schemas, not a general JSON Schema engine.
    if "const" in schema and (value != schema["const"] or isinstance(value, bool) != isinstance(schema["const"], bool)):
        fail("JSON contract constant mismatch")
    if "enum" in schema and value not in schema["enum"]:
        fail("JSON contract enum mismatch")
    kinds = schema.get("type", [])
    kinds = [kinds] if isinstance(kinds, str) else kinds
    checks = {"object": type(value) is dict, "array": type(value) is list,
              "string": type(value) is str, "boolean": type(value) is bool,
              "null": value is None, "integer": type(value) is int}
    if kinds and not any(checks.get(k, False) for k in kinds):
        fail("JSON contract type mismatch")
    if type(value) is dict:
        if set(schema.get("required", [])) - value.keys():
            fail("missing JSON contract field")
        props = schema.get("properties", {})
        extra = schema.get("additionalProperties", True)
        for key, item in value.items():
            if key in props:
                validate_contract(item, props[key])
            elif extra is False:
                fail("unknown JSON contract field")
            elif type(extra) is dict:
                validate_contract(item, extra)
    if type(value) is list:
        if len(value) < schema.get("minItems", 0) or len(value) > schema.get("maxItems", 50000):
            fail("JSON contract collection size mismatch")
        if schema.get("uniqueItems") and len({json.dumps(x, sort_keys=True) for x in value}) != len(value):
            fail("duplicate JSON contract item")
        for item in value:
            validate_contract(item, schema.get("items", {}))
    if type(value) is str:
        if len(value) < schema.get("minLength", 0) or ("pattern" in schema and not re.search(schema["pattern"], value)):
            fail("JSON contract string mismatch")
    for child in schema.get("allOf", []):
        validate_contract(value, child)
    if "if" in schema:
        try:
            validate_contract(value, schema["if"])
        except RuntimeError:
            branch = "else"
        else:
            branch = "then"
        validate_contract(value, schema.get(branch, {}))


def toolchain_inventory(root):
    # Separate read-only trusted software input; never widen artifact path roots.
    files = []
    def visit(directory):
        checked_path(directory, roots=(root,), exists=True)
        parents = path_chain(directory)
        for child in sorted(directory.iterdir()):
            checked_path(child, roots=(root,), exists=True)
            info = child.lstat()
            if stat.S_ISDIR(info.st_mode):
                visit(child)
            else:
                facts, _ = stable_file(child, roots=(root,))
                files.append({"path": child.relative_to(root).as_posix(), **facts})
        same_chain(parents)
    visit(root)
    files.sort(key=lambda item: item["path"])
    if not files or len(files) > 50000 or len({f["path"].casefold() for f in files}) != len(files):
        fail("invalid toolchain tree inventory")
    return files


def verify_toolchain(context):
    root, binding = context["root"], context["binding"]
    actual = toolchain_inventory(root)
    if actual != binding["toolchain_files"]:
        fail("toolchain tree path/size/hash/set mismatch")
    tree_hash = digest(json.dumps(actual, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8"))
    if tree_hash != binding["toolchain_tree_sha256"]:
        fail("toolchain tree digest mismatch")
    index = {f["path"]: f for f in actual}
    suffix = ".exe" if binding["host_os"] == "windows" else ""
    go_name = "bin/go" + suffix
    host_tools = "pkg/tool/" + binding["host_os"] + "_" + binding["host_arch"] + "/"
    required = ["VERSION", go_name] + [host_tools + name + suffix for name in ("compile", "link", "asm")]
    if any(name not in index for name in required) or index[go_name]["sha256"] != binding["go_toolchain_sha256"]:
        fail("launcher/compiler/linker/assembler pins missing or inconsistent")
    _, version = stable_file(root / "VERSION", keep_bytes=True, roots=(root,))
    if version.decode("utf-8").splitlines()[0] != "go1.27.4":
        fail("toolchain VERSION differs from adopted patch")


def child_environment(profile, binding, args):
    state = OUTPUT_ROOT / "build-environment-state" / args.output.name
    directories = {name: ensure_directory(state / name) for name in
                   ("gopath", "modcache", "gocache", "tmp", "home", "config", "local", "cache")}
    env = dict(profile["environment_fixed"])  # Never os.environ.copy/update(parent).
    env.update({"GOROOT": str(lexical_path(binding["go_toolchain_root"])),
                "GOPATH": str(directories["gopath"]), "GOMODCACHE": str(directories["modcache"]),
                "GOCACHE": str(directories["gocache"]), "GOTMPDIR": str(directories["tmp"]),
                "TMPDIR": str(directories["tmp"]), "TMP": str(directories["tmp"]),
                "TEMP": str(directories["tmp"]), "HOME": str(directories["home"]),
                "USERPROFILE": str(directories["home"]), "APPDATA": str(directories["config"]),
                "LOCALAPPDATA": str(directories["local"]), "XDG_CONFIG_HOME": str(directories["config"]),
                "XDG_CACHE_HOME": str(directories["cache"]), "GOOS": args.goos, "GOARCH": args.goarch,
                "PATH": str(lexical_path(binding["go_toolchain_root"]) / "bin")})
    env["GOAMD64" if args.goarch == "amd64" else "GOARM64"] = profile["goamd64" if args.goarch == "amd64" else "goarm64"]
    if binding["host_os"] == "windows":
        import ctypes
        kernel = ctypes.WinDLL("kernel32.dll", winmode=0x800, use_last_error=True)
        kernel.GetSystemWindowsDirectoryW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint]
        kernel.GetSystemWindowsDirectoryW.restype = ctypes.c_uint
        buffer = ctypes.create_unicode_buffer(32768)
        length = kernel.GetSystemWindowsDirectoryW(buffer, len(buffer))
        if not length or length >= len(buffer):
            fail("Windows directory identity unavailable")
        system_root = lexical_path(buffer.value)
        if binding["os_system_root"] is None or system_root != lexical_path(binding["os_system_root"]):
            fail("Windows OS root differs from approved binding")
        env.update({"SystemRoot": str(system_root), "WINDIR": str(system_root), "PATHEXT": ".EXE"})
    elif binding["os_system_root"] is not None:
        fail("unexpected Windows SDK/system-root binding on Linux")
    allowed = set(profile["environment_allowlist"])
    expected = allowed - ({"GOARM64"} if args.goarch == "amd64" else {"GOAMD64"})
    if binding["host_os"] != "windows":
        expected -= {"SystemRoot", "WINDIR", "PATHEXT"}
    if set(env) != expected or any(re.search(rule, key) for rule in profile["environment_denylist"] for key in env):
        fail("effective child environment allowlist/denylist mismatch")
    if env["PATH"] != str(lexical_path(binding["go_toolchain_root"]) / "bin") or env["CGO_ENABLED"] != "0":
        fail("native compiler/SDK/PATH policy mismatch")
    return env


def compiler_command_guard(argv, env):
    context = ACTIVE_BUILD_ENVIRONMENT
    if context is None or tuple(sorted(env.items())) != context["expected_env"] or str(argv[0]) != str(context["go"]):
        fail("Go execution requires the exact normalized environment and verified executable")
    verify_toolchain(context)
    for directory in context["state_dirs"]:
        checked_path(directory, roots=(OUTPUT_ROOT,), exists=True)
        if not directory.is_dir():
            fail("normalized state directory replaced")
    args = [str(a) for a in argv[1:]]
    if not context["released"]:
        if args != context["probe"]:
            fail("only environment probe allowed before manifest completion")
        return
    if not args or args[0] not in ("env", "list", "mod", "vet", "test", "build", "version"):
        fail("Go subcommand outside approved foundation scope")
    if args[0] == "mod" and args[1:] not in (["graph"], ["verify"]):
        fail("mutating module operation rejected")
    if args[0] == "env" or any(a.startswith(("-toolexec", "-overlay", "-modfile")) for a in args):
        fail("unapproved tool/environment override rejected")
    if args[0] == "build" and args[1:1 + len(context["profile"]["build_flags"])] != context["profile"]["build_flags"]:
        fail("build arguments differ from approved profile")


def prepare_build_environment(args):
    global ACTIVE_BUILD_ENVIRONMENT
    lock = lockcheck()
    raw = read_bytes(CONFIG / "build-environment-profile.json")
    profile_hash = digest(raw)
    expected_hash = os.environ.get("IPMV_BUILD_PROFILE_SHA256", "")
    tools = load(CONFIG / "tool-lock.json")
    if not re.fullmatch(r"[0-9a-f]{64}", expected_hash) or profile_hash != expected_hash or profile_hash != tools["go"]["environment_profile_sha256"]:
        fail("protected environment-profile approval pin missing/mismatched")
    profile = json.loads(raw, object_pairs_hook=pairs)
    validate_contract(profile, load(CONFIG / "build-environment-profile.schema.json"))
    manifest_schema = load(CONFIG / "build-environment-manifest.schema.json")
    runner_id = os.environ.get("IPMV_BUILD_RUNNER_PROFILE", "")
    registered = next((r for r in profile["runner_profiles"] if r["id"] == runner_id), None)
    if registered is None or registered["state"] != "APPROVED_BINDING_REQUIRED" or registered["kind"] != "github-hosted":
        fail("unregistered/local/self-hosted runner has no approved active profile")
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        fail("hosted workflow context required; runner JSON alone is not identity")
    host_os = "windows" if os.name == "nt" else "linux" if sys.platform.startswith("linux") else "unsupported"
    host_arch = {"AMD64": "amd64", "x86_64": "amd64", "ARM64": "arm64", "aarch64": "arm64"}.get(platform.machine())
    if (host_os, host_arch) != (registered["host_os"], registered["host_arch"]) or args.goos + "/" + args.goarch not in registered["targets"]:
        fail("OS-measured host/target differs from runner profile")
    pins = json.loads(os.environ.get("IPMV_BUILD_BINDINGS_SHA256", "{}"), object_pairs_hook=pairs)
    expected_binding_hash = pins.get(runner_id) if type(pins) is dict else None
    if not isinstance(expected_binding_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", expected_binding_hash):
        fail("approved per-runner binding hash missing; never enroll observed hashes automatically")
    binding_file = relative_path(CONFIG, "environment-bindings/" + runner_id + ".json")
    binding_bytes = read_bytes(binding_file)
    if digest(binding_bytes) != expected_binding_hash:
        fail("environment binding approval hash mismatch")
    binding = json.loads(binding_bytes, object_pairs_hook=pairs)
    validate_contract(binding, manifest_schema["$defs"]["binding"])
    if (binding["runner_profile_id"], binding["host_os"], binding["host_arch"]) != (runner_id, host_os, host_arch):
        fail("binding belongs to another runner")
    if not os.environ.get("ImageOS") or not os.environ.get("ImageVersion") or (binding["runner_image_id"], binding["runner_image_version"]) != (os.environ["ImageOS"], os.environ["ImageVersion"]):
        fail("runner image identity/version not approved")
    distribution = next(d for d in profile["official_distributions"] if (d["host_os"], d["host_arch"]) == (host_os, host_arch))
    if any(binding["distribution"][k] != distribution[k] for k in ("url", "archive_sha256")):
        fail("toolchain distribution origin differs from pinned official archive")
    root = lexical_path(binding["go_toolchain_root"])
    if root == root.parent or any(root.is_relative_to(r) or r.is_relative_to(root) for r in (REPO, OUTPUT_ROOT)):
        fail("toolchain must be a separate approved read-only software root")
    go = checked_path(root / "bin" / ("go.exe" if host_os == "windows" else "go"), roots=(root,), exists=True)
    if lexical_path(binding["go_toolchain_path"]) != go or (args.go is not None and lexical_path(args.go) != go):
        fail("Go executable path differs from approved binding; PATH lookup forbidden")
    if (profile["dependency_set_digest"] != lock["dependency_set_digest"] or len(lock["modules"]) != 26 or len(lock["graph_edges"]) != 58 or tools["go"]["version"] != "1.27.4"):
        fail("dependency/toolchain profile drift")
    context = {"profile": profile, "binding": binding, "root": root, "go": go,
               "released": False, "probe": [], "env": {}}
    verify_toolchain(context)  # Hash before executing even go env.
    context["env"] = child_environment(profile, binding, args)
    context["expected_env"] = tuple(sorted(context["env"].items()))
    context["state_dirs"] = [pathlib.Path(context["env"][key]) for key in
                             ("GOPATH", "GOMODCACHE", "GOCACHE", "GOTMPDIR", "HOME", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME")]
    probe_expected = {key: context["env"][key] for key in
                      ("GOROOT", "GOPATH", "GOMODCACHE", "GOCACHE", "GOOS", "GOARCH", "CGO_ENABLED",
                       "GOTOOLCHAIN", "GOENV", "GOWORK", "GOFLAGS", "GOEXPERIMENT", "GOCACHEPROG",
                       "GOAUTH", "GOPROXY", "GOSUMDB", "GOVCS", "GOINSECURE", "GOPRIVATE", "GONOPROXY", "GONOSUMDB", "GO111MODULE")}
    probe_expected.update({"GOVERSION": "go1.27.4", "GOHOSTOS": host_os, "GOHOSTARCH": host_arch,
                           "GOTOOLDIR": str(root / "pkg" / "tool" / (host_os + "_" + host_arch)),
                           "GOMOD": str(REPO / "go.mod")})
    probe_expected["GOENV"] = ""  # Go 1.27.4 cfg.EnvFile: configured off is reported as empty.
    arch_key = "GOAMD64" if args.goarch == "amd64" else "GOARM64"
    probe_expected[arch_key] = context["env"][arch_key]
    context["probe"] = ["env", "-json", *sorted(probe_expected)]
    ACTIVE_BUILD_ENVIRONMENT = context
    args.go = str(go)
    observed = json.loads(command([go, *context["probe"]], env=context["env"]).stdout, object_pairs_hook=pairs)
    if observed != probe_expected:
        fail("effective go env does not match the resolved build profile")
    manifest = {"schema": "ipmv.core.build-environment-manifest.v1", "status": "VALIDATED_PRE_BUILD",
                "profile_id": profile["profile_id"], "profile_version": profile["profile_version"],
                "profile_sha256": profile_hash, "source_commit": WORKSPACE_CONTEXT["commit"],
                "dependency_set_digest": lock["dependency_set_digest"],
                "binding_ref": {"path": binding_file.relative_to(REPO).as_posix(), "sha256": expected_binding_hash, "size_bytes": str(len(binding_bytes))},
                "runner": {"profile_id": runner_id, "kind": "github-hosted", "host_os": host_os, "host_arch": host_arch,
                           "image_id": binding["runner_image_id"], "image_version": binding["runner_image_version"],
                           "run_id": os.environ["GITHUB_RUN_ID"], "run_attempt": os.environ["GITHUB_RUN_ATTEMPT"]},
                "target": {"goos": args.goos, "goarch": args.goarch, "goamd64": profile["goamd64"] if args.goarch == "amd64" else None,
                           "goarm64": profile["goarm64"] if args.goarch == "arm64" else None, "cgo_enabled": "0"},
                "go_toolchain_path": str(go), "go_toolchain_sha256": binding["go_toolchain_sha256"],
                "toolchain_tree_sha256": binding["toolchain_tree_sha256"], "normalized_environment": context["env"],
                "observed_go_environment": observed, "discarded_parent_environment_names": sorted(set(os.environ) - set(context["env"])),
                "build_flags": profile["build_flags"], "profile_match": True, "build_started": False, "release_eligible": False}
    validate_contract(manifest, manifest_schema)
    save(args.output / ("build-environment-" + args.stage + ".json"), manifest)
    context["released"] = True  # Only after a complete, persisted environment manifest.
    args.build_environment = context
    return dict(context["env"])


def validate_environment_report(args, name):
    report = load(relative_path(args.output, name))
    validate_contract(report, load(CONFIG / "build-environment-manifest.schema.json"))
    if (report["status"] != "VALIDATED_PRE_BUILD" or report["source_commit"] != WORKSPACE_CONTEXT["commit"]
            or report["dependency_set_digest"] != lockcheck()["dependency_set_digest"]
            or report["profile_sha256"] != filehash(CONFIG / "build-environment-profile.json")
            or report["target"]["goos"] != args.goos or report["target"]["goarch"] != args.goarch):
        fail("stale/mismatched/unobserved build-environment report")
    if report["runner"]["run_id"] != WORKSPACE_CONTEXT["run_id"] or report["runner"]["run_attempt"] != WORKSPACE_CONTEXT["run_attempt"]:
        fail("build environment belongs to another run")
    binding_path = "ci/core-runtime/environment-bindings/" + report["runner"]["profile_id"] + ".json"
    if report["binding_ref"]["path"] != binding_path or report["binding_ref"] != file_ref(REPO, relative_path(REPO, binding_path)):
        fail("environment binding reference is not closed/path-size-hash consistent")


def goenv(args):
    if getattr(args, "build_environment", None) is None:
        return prepare_build_environment(args)
    return dict(args.build_environment["env"])


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
    dependency_result = load(args.output / "dependency-validation.json")
    if (dependency_result.get("status") != "DEPENDENCY_GRAPH_MATCH" or dependency_result.get("dependency_set_digest") != lock["dependency_set_digest"]
            or dependency_result.get("modules") != lock["modules"] or dependency_result.get("edges") != lock["graph_edges"]):
        fail("fresh exact dependency verification required before build")
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
        argv = [args.go, "build", *args.build_environment["profile"]["build_flags"],
                "-o", binary, pkgs[0]]
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
                            "GOOS", "GOARCH", "GOAMD64", "GOARM64", "GOEXPERIMENT", "GOROOT",
                            "GOPATH", "GOMODCACHE", "GOCACHE", "PATH", "GOPROXY", "GOSUMDB", "GOVCS")}
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
    required += ["build-environment-" + stage + ".json" for stage in ("environment", "dependency", "build")]
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
                                          "environment", "seal", "verify-upload"])
    parser.add_argument("--output-root", default=os.environ.get("IPMV_CORE_OUTPUT_ROOT"),
                        required=not os.environ.get("IPMV_CORE_OUTPUT_ROOT"))
    parser.add_argument("--output", required=True)
    parser.add_argument("--tools")
    parser.add_argument("--go", help="Optional exact approved absolute executable; never a PATH name")
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
            if args.stage == "environment":
                goenv(args)
            elif args.stage == "dependency":
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
