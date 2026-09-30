#!/bin/bash
# Domain-model metrics for the SqlOS 8.0.0 refactor (issue #435, section 10).
#
# Scans every *.cs file under src/SqlOS (bin/ and obj/ excluded) and prints one
# key=value line per metric in a fixed order. Counting rules are defined in
# docs/architecture/8.0-baseline-metrics.md; this script is their reference
# implementation. Later refactor layers report deltas by measuring the base
# and the change with the same copy of this script:
#
#   scripts/domain-metrics.sh --json --root ../sqlos-base > before.json
#   scripts/domain-metrics.sh --json > after.json
#   diff before.json after.json
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/domain-metrics.sh [--json] [--root <dir>]

  --json        Print one JSON object instead of key=value lines.
  --root <dir>  Checkout to measure (default: the repository containing this script).
  -h, --help    Show this help.
USAGE
}

script_dir="$(CDPATH= cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(CDPATH= cd "$script_dir/.." && pwd)"
format="kv"

while [ $# -gt 0 ]; do
  case "$1" in
    --json)
      format="json"
      shift
      ;;
    --root)
      if [ $# -lt 2 ]; then
        echo "error: --root requires a directory" >&2
        usage >&2
        exit 2
      fi
      root="$2"
      shift 2
      ;;
    --root=*)
      root="${1#--root=}"
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [ ! -d "$root/src/SqlOS" ]; then
  echo "error: $root/src/SqlOS not found" >&2
  exit 2
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "error: python3 is required" >&2
  exit 2
fi

python3 - "$root" "$format" <<'PY'
import bisect
import json
import os
import re
import sys

ROOT = os.path.abspath(sys.argv[1])
FORMAT = sys.argv[2]
SRC = os.path.join(ROOT, "src", "SqlOS")

# --------------------------------------------------------------------------
# Lexing: blank comments, preprocessor lines, and string/char literal text so
# that regexes only see code. Offsets and newlines are preserved. Code inside
# interpolation holes is kept, so $"{DateTime.UtcNow:O}" is still a use.
# --------------------------------------------------------------------------

STRING_START = re.compile(r'(?:\$+@?|@\$*)?"')
INTERESTING = re.compile(r"[/\"'@${}#]")


def sanitize(text):
    n = len(text)
    parts = []
    pos = [0]

    def blank(a, b):
        if b > a:
            parts.append(text[pos[0]:a])
            parts.append(re.sub(r"[^\n]", " ", text[a:b]))
            pos[0] = b

    def scan_char(i):
        j = i + 1
        if j < n and text[j] == "\\":
            j += 2
            while j < n and text[j] not in "'\n":
                j += 1
        else:
            j += 1
        if j < n and text[j] == "'":
            blank(i + 1, j)
            return j + 1
        return i + 1

    def scan_hole(i, braces):
        end = scan_code(i, braces)
        blank(end, min(n, end + braces))
        return end + braces

    def scan_string(i):
        q = STRING_START.match(text, i).end() - 1
        prefix = text[i:q]
        dollars = prefix.count("$")
        verbatim = "@" in prefix
        k = q
        while k < n and text[k] == '"':
            k += 1
        quotes = k - q
        if quotes >= 3:  # raw string literal
            j = seg = k
            while j < n:
                c = text[j]
                if c == '"':
                    r = j
                    while r < n and text[r] == '"':
                        r += 1
                    if r - j >= quotes:
                        blank(seg, j)
                        return j + quotes
                    j = r
                elif dollars and c == "{":
                    r = j
                    while r < n and text[r] == "{":
                        r += 1
                    if r - j >= dollars:
                        blank(seg, r)
                        j = seg = scan_hole(r, dollars)
                    else:
                        j = r
                else:
                    j += 1
            blank(seg, n)
            return n
        if quotes == 2 and not dollars:
            return q + 2
        j = seg = q + 1
        while j < n:
            c = text[j]
            if c == "\\" and not verbatim:
                j += 2
            elif c == '"':
                if verbatim and j + 1 < n and text[j + 1] == '"':
                    j += 2
                    continue
                blank(seg, j)
                return j + 1
            elif c == "\n" and not verbatim:
                blank(seg, j)
                return j
            elif dollars and c == "{":
                if j + 1 < n and text[j + 1] == "{":
                    j += 2
                    continue
                blank(seg, j + 1)
                j = seg = scan_hole(j + 1, 1)
            elif dollars and c == "}" and j + 1 < n and text[j + 1] == "}":
                j += 2
            else:
                j += 1
        blank(seg, n)
        return n

    def scan_code(i, close):
        depth = 0
        while i < n:
            m = INTERESTING.search(text, i)
            if not m:
                return n
            i = m.start()
            c = text[i]
            if c == "/" and text.startswith("//", i):
                j = text.find("\n", i)
                j = n if j < 0 else j
                blank(i, j)
                i = j
            elif c == "/" and text.startswith("/*", i):
                j = text.find("*/", i + 2)
                j = n if j < 0 else j + 2
                blank(i, j)
                i = j
            elif c == "#" and close == 0 and text[text.rfind("\n", 0, i) + 1:i].strip() == "":
                j = text.find("\n", i)
                j = n if j < 0 else j
                blank(i, j)
                i = j
            elif c == "'":
                i = scan_char(i)
            elif c in "\"@$" and STRING_START.match(text, i):
                i = scan_string(i)
            elif c == "{":
                depth += 1
                i += 1
            elif c == "}":
                if close and depth == 0:
                    return i
                depth -= 1
                i += 1
            else:
                i += 1
        return n

    scan_code(0, 0)
    parts.append(text[pos[0]:])
    return "".join(parts)


# --------------------------------------------------------------------------
# Structure: walk braces and parentheses to find type declarations and
# method-like members (methods, constructors, local functions; block- or
# expression-bodied) with their spans.
# --------------------------------------------------------------------------

MODIFIERS = {
    "public", "private", "protected", "internal", "static", "async", "override",
    "virtual", "sealed", "abstract", "extern", "unsafe", "new", "partial",
    "readonly", "required", "file", "ref", "volatile", "const", "implicit", "explicit",
}
NOT_A_MEMBER = {
    "if", "else", "for", "foreach", "while", "do", "switch", "try", "catch", "finally",
    "using", "lock", "fixed", "checked", "unchecked", "return", "yield", "await", "throw",
    "new", "case", "default", "goto", "break", "continue", "get", "set", "init", "add",
    "remove", "var", "delegate", "namespace", "when", "typeof", "sizeof", "nameof", "is",
    "as", "in", "out", "base", "this", "stackalloc", "select", "from", "where", "let",
    "operator",
}
TYPE_HEADER = re.compile(
    r"^(?P<mods>(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|new|file)\s+)*)"
    r"(?P<kind>class|struct|interface|enum|record(?:\s+class|\s+struct)?)\s+(?P<name>@?[A-Za-z_]\w*)"
)
NAMESPACE_HEADER = re.compile(r"^namespace\s+([\w.]+)")
FILE_NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)\s*;", re.M)
AFTER_PARAMS = re.compile(r"\)\s*(?:where\s[^()]*(?:\([^()]*\)[^()]*)*|:\s*(?:base|this)\s*\(.*\))\s*$", re.S)
NAME_AT_END = re.compile(r"(@?[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*$")
TOKENS = re.compile(r"=>|[{}()\[\];,]")
CODE_BLOCKS = ("method", "local", "lambda", "control", "accessor")


def strip_attributes(s, base):
    i, n = 0, len(s)
    while True:
        while i < n and s[i].isspace():
            i += 1
        if i < n and s[i] == "[":
            depth = 0
            while i < n:
                if s[i] == "[":
                    depth += 1
                elif s[i] == "]":
                    depth -= 1
                    if depth == 0:
                        i += 1
                        break
                i += 1
            continue
        return s[i:], base + i


def split_top_level(s):
    depth, parts, cur = 0, [], []
    for ch in s:
        if ch in "(<[{":
            depth += 1
        elif ch in ")>]}":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p for p in (x.strip() for x in parts) if p]


def parse_signature(header):
    """(name, name offset, return type, params, modifiers) for a method-like header."""
    text = header.rstrip()
    if not text.endswith(")"):
        m = AFTER_PARAMS.search(text)
        if not m:
            return None
        text = text[:m.start() + 1]
    first = re.match(r"\s*(@?\w+)", text)
    if not first or first.group(1) in NOT_A_MEMBER:
        return None
    depth, open_ = 0, -1
    for k in range(len(text) - 1, -1, -1):
        if text[k] == ")":
            depth += 1
        elif text[k] == "(":
            depth -= 1
            if depth == 0:
                open_ = k
                break
    if open_ < 0:
        return None
    m = NAME_AT_END.search(text[:open_])
    if not m or m.group(1) in NOT_A_MEMBER or m.group(1) in MODIFIERS:
        return None
    before = text[:m.start()]
    if "=" in before or before.rstrip().endswith("."):
        return None
    words = before.split()
    if "operator" in words:
        return None
    i = 0
    while i < len(words) and words[i] in MODIFIERS:
        i += 1
    return m.group(1), m.start(1), " ".join(words[i:]), text[open_ + 1:len(text) - 1], frozenset(words[:i])


class Member(object):
    pass


def parse_structure(rel, S):
    m_ns = FILE_NAMESPACE.search(S)
    file_ns = m_ns.group(1) if m_ns else ""
    frames = [{"paren": False, "block": None, "hs": 0, "pending": None}]
    blocks = []
    types, methods, declarations = [], [], set()

    def current_block():
        for f in reversed(frames):
            if not f["paren"]:
                return f["block"]
        return None

    def enclosing_type(b):
        while b is not None and blocks[b]["kind"] != "type":
            b = blocks[b]["parent"]
        return b

    def qualified_name(b):
        names, spaces = [], []
        while b is not None:
            if blocks[b]["kind"] == "type":
                names.append(blocks[b]["name"])
            elif blocks[b]["kind"] == "namespace":
                spaces.append(blocks[b]["name"])
            b = blocks[b]["parent"]
        ns = ".".join(reversed(spaces)) or file_ns
        return ".".join([x for x in [ns] + list(reversed(names)) if x])

    def add_method(sig, start, end, owner_block, is_ctor, body_open):
        mt = Member()
        tb = enclosing_type(owner_block)
        mt.file, mt.name, mt.start, mt.end = rel, sig[0], start, end
        mt.type_key = qualified_name(tb) if tb is not None else ""
        mt.type_name = blocks[tb]["name"] if tb is not None else ""
        mt.rtype, mt.params, mt.mods, mt.is_ctor, mt.body_open = sig[2], sig[3], sig[4], is_ctor, body_open
        methods.append(mt)
        declarations.add(start + sig[1])

    def member_signature(header, owner):
        sig = parse_signature(header)
        if not sig or owner is None:
            return None, False
        kind = blocks[owner]["kind"]
        if kind != "type" and kind not in CODE_BLOCKS:
            return None, False
        is_ctor = kind == "type" and sig[2] == "" and sig[0] == blocks[owner]["name"]
        if sig[2] == "" and not is_ctor:
            return None, False
        return sig, is_ctor

    for tok in TOKENS.finditer(S):
        t, i = tok.group(), tok.start()
        top = frames[-1]
        if t == "{":
            parent = current_block()
            block = {"kind": "other", "name": None, "parent": parent, "open": i, "info": None}
            if not top["paren"]:
                header, hstart = strip_attributes(S[top["hs"]:i], top["hs"])
                norm = " ".join(header.split())
                tm = TYPE_HEADER.match(norm)
                nm = NAMESPACE_HEADER.match(norm)
                if nm:
                    block["kind"], block["name"] = "namespace", nm.group(1)
                elif tm:
                    block["kind"], block["name"] = "type", tm.group("name").lstrip("@")
                    block["info"] = (tm.group("kind").split()[0], "static" in tm.group("mods").split(), hstart, header)
                elif norm.endswith("=>"):
                    block["kind"] = "lambda"
                else:
                    sig, is_ctor = member_signature(header, parent)
                    if sig:
                        block["kind"] = "method" if blocks[parent]["kind"] == "type" else "local"
                        block["name"] = sig[0]
                        block["info"] = (sig, hstart, is_ctor)
                    else:
                        fw = re.match(r"(@?\w+)", norm)
                        word = fw.group(1) if fw else ""
                        if word in ("get", "set", "init", "add", "remove") or re.search(r"\b(?:private|protected|internal)\s+(?:set|init)$", norm):
                            block["kind"] = "accessor"
                        elif word in NOT_A_MEMBER:
                            block["kind"] = "control"
            blocks.append(block)
            frames.append({"paren": False, "block": len(blocks) - 1, "hs": i + 1, "pending": None})
        elif t == "}":
            while len(frames) > 1 and frames[-1]["paren"]:
                frames.pop()
            if len(frames) == 1:
                continue
            b = frames.pop()["block"]
            block = blocks[b]
            if block["kind"] == "type":
                kind, is_static, hstart, header = block["info"]
                td = Member()
                td.file, td.name, td.kind, td.is_static = rel, block["name"], kind, is_static
                td.key, td.start, td.end, td.open = qualified_name(b), hstart, i, block["open"]
                td.primary_params = None
                pm = re.search(r"\b" + re.escape(block["name"]) + r"\s*(?:<[^()]*>)?\s*\(", header)
                if pm:
                    depth = 0
                    for k in range(pm.end() - 1, len(header)):
                        if header[k] == "(":
                            depth += 1
                        elif header[k] == ")":
                            depth -= 1
                            if depth == 0:
                                td.primary_params = header[pm.end():k]
                                break
                types.append(td)
            elif block["kind"] in ("method", "local"):
                sig, hstart, is_ctor = block["info"]
                add_method(sig, hstart, i, block["parent"], is_ctor, block["open"])
            if not frames[-1]["paren"]:
                frames[-1]["hs"] = i + 1
        elif t in "([":
            frames.append({"paren": True, "block": None, "hs": i + 1, "pending": None})
        elif t in ")]":
            if len(frames) > 1 and frames[-1]["paren"]:
                frames.pop()
        elif t == ",":
            if top["paren"]:
                top["hs"] = i + 1
        elif t == ";":
            if not top["paren"]:
                b = top["block"]
                if top["pending"] is not None:
                    sig, hstart, is_ctor = top["pending"]
                    add_method(sig, hstart, i, b, is_ctor, None)
                    top["pending"] = None
                elif b is not None and blocks[b]["kind"] == "type":
                    # Bodiless declaration: interface, abstract, partial, extern.
                    header, hstart = strip_attributes(S[top["hs"]:i], top["hs"])
                    sig = parse_signature(header)
                    if sig and sig[2] != "":
                        declarations.add(hstart + sig[1])
                top["hs"] = i + 1
        elif t == "=>":
            if not top["paren"] and top["pending"] is None:
                header, hstart = strip_attributes(S[top["hs"]:i], top["hs"])
                sig, is_ctor = member_signature(header, top["block"])
                if sig:
                    top["pending"] = (sig, hstart, is_ctor)
    return types, methods, declarations


# --------------------------------------------------------------------------
# Load sources
# --------------------------------------------------------------------------

FILES = []
for d, dirs, names in os.walk(SRC):
    dirs[:] = sorted(x for x in dirs if x not in ("bin", "obj"))
    for name in names:
        if name.endswith(".cs"):
            FILES.append(os.path.relpath(os.path.join(d, name), ROOT).replace(os.sep, "/"))
FILES.sort()

RAW, CODE, NEWLINES, DECLARATIONS = {}, {}, {}, {}
TYPES, METHODS = [], []
for rel in FILES:
    with open(os.path.join(ROOT, rel), encoding="utf-8-sig", errors="replace") as fh:
        text = fh.read().replace("\r\n", "\n")
    RAW[rel] = text
    CODE[rel] = sanitize(text)
    NEWLINES[rel] = [k for k, ch in enumerate(text) if ch == "\n"]
    t, m, d = parse_structure(rel, CODE[rel])
    TYPES.extend(t)
    METHODS.extend(m)
    DECLARATIONS[rel] = d


def line_of(rel, offset):
    return bisect.bisect_left(NEWLINES[rel], offset) + 1


def span_lines(rel, start, end):
    return line_of(rel, end) - line_of(rel, start) + 1


def count_sites(offsets_by_file):
    total = sum(len(v) for v in offsets_by_file.values())
    return total, sum(1 for v in offsets_by_file.values() if v)


RESULTS = []


def emit(key, value):
    RESULTS.append((key, value))


# Assignment operators: =, ??=, compound; never ==, =>, <=, >=, !=.
ASSIGN = r"\s*(?:\?\?=|[-+*/%&|^]=|=(?![=>]))"

# --------------------------------------------------------------------------
# Entities: every T in Entity<T> in *ModelConfiguration.cs. Settable property:
# public, non-static property with a set/init accessor that has no
# private/protected/internal modifier.
# --------------------------------------------------------------------------

entity_names = set()
for rel in FILES:
    if rel.endswith("ModelConfiguration.cs"):
        entity_names.update(re.findall(r"\bEntity<\s*(?:[\w.]+\.)?(\w+)\s*>", CODE[rel]))

PROPERTY_HEADER = re.compile(
    r"^(?P<mods>(?:(?:public|private|protected|internal|static|virtual|override|sealed|abstract|new|required|readonly)\s+)*)"
    r"[^=(){};]+?\s+(?P<name>@?[A-Za-z_]\w*)$"
)
SETTER = re.compile(r"((?:\b(?:private|protected|internal)\s+)*)\b(?:set|init)\b")


def settable_properties(td):
    S = CODE[td.file]
    body = S[td.open + 1:td.end]
    found, depth, hs, k = [], 0, 0, 0
    while k < len(body):
        ch = body[k]
        if ch == "{" and depth == 0:
            header = " ".join(strip_attributes(body[hs:k], 0)[0].split())
            close, d = k, 0
            for q in range(k, len(body)):
                if body[q] == "{":
                    d += 1
                elif body[q] == "}":
                    d -= 1
                    if d == 0:
                        close = q
                        break
            pm = PROPERTY_HEADER.match(header)
            if pm:
                mods = pm.group("mods").split()
                if "public" in mods and "static" not in mods and any(
                        s.group(1).strip() == "" for s in SETTER.finditer(body[k + 1:close])):
                    found.append(pm.group("name"))
            k = hs = close + 1
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        elif ch == ";" and depth == 0:
            hs = k + 1
        k += 1
    return found


entity_properties = {}
for td in TYPES:
    if td.kind == "class" and td.name in entity_names:
        entity_properties.setdefault(td.name, []).extend(settable_properties(td))
emit("entity_classes", len(entity_properties))
emit("entity_settable_properties", sum(len(v) for v in entity_properties.values()))

# --------------------------------------------------------------------------
# Lifecycle field write sites: member-access assignments (x.Field = / ??=)
# plus EF bulk updates (SetProperty(x => x.Field, ...)).
# --------------------------------------------------------------------------

for field, key in (("RevokedAt", "revoked_at"), ("IsActive", "is_active"),
                   ("ConsumedAt", "consumed_at"), ("IsVerified", "is_verified")):
    member = re.compile(r"\.\s*" + field + r"\b" + ASSIGN)
    bulk = re.compile(r"\bSetProperty\s*\(\s*(\w+)\s*=>\s*\1\s*\.\s*" + field + r"\s*,")
    sites = {rel: [m.start() for m in member.finditer(CODE[rel])] + [m.start() for m in bulk.finditer(CODE[rel])]
             for rel in FILES}
    total, files = count_sites(sites)
    emit(key + "_write_sites", total)
    emit(key + "_write_files", files)

# --------------------------------------------------------------------------
# SqlOSAuthorizationRequest field assignments: assignments to its settable
# properties through an identifier the same file types as one (declaration,
# `new`, a Set<SqlOSAuthorizationRequest>() single-row query, or a call to a
# method declared to return one).
# --------------------------------------------------------------------------

AR = "SqlOSAuthorizationRequest"
ar_properties = sorted(set(entity_properties.get(AR, [])))
returns_ar = set(m.name for m in METHODS
                 if re.fullmatch(r"(?:(?:Value)?Task<\s*)?" + AR + r"\s*\??(?:\s*>)?", m.rtype or ""))
SINGLE_ROW = re.compile(r"(?:First|Single|Last)(?:OrDefault)?(?:Async)?|Find(?:Async)?")


def strip_arguments(expr):
    out, depth = [], 0
    for ch in expr:
        if ch == "(":
            if depth == 0:
                out.append("(")
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                out.append(")")
        elif depth == 0:
            out.append(ch)
    return "".join(out)


ar_sites = {}
for rel in FILES:
    S = CODE[rel]
    receivers = set(re.findall(r"\b" + AR + r"\s*\??\s+(@?[A-Za-z_]\w*)\b(?!\s*[(<])", S))
    for vm in re.finditer(r"\bvar\s+(@?[A-Za-z_]\w*)\s*=\s*([^;]*);", S):
        expr = vm.group(2)
        if re.match(r"\s*new\s+" + AR + r"\b", expr):
            receivers.add(vm.group(1))
            continue
        tail = re.search(r"(\w+)\s*\(\s*\)\s*$", strip_arguments(expr))
        if tail and (tail.group(1) in returns_ar or (
                re.search(r"\bSet<\s*" + AR + r"\s*>", expr) and SINGLE_ROW.fullmatch(tail.group(1)))):
            receivers.add(vm.group(1))
    ar_sites[rel] = []
    if receivers and ar_properties:
        rx = re.compile(r"(?<![\w.])(?:" + "|".join(sorted(map(re.escape, receivers))) + r")\s*\.\s*(?:"
                        + "|".join(ar_properties) + r")\b" + ASSIGN)
        ar_sites[rel] = [m.start() for m in rx.finditer(S)]
total, files = count_sites(ar_sites)
emit("authorization_request_assignment_sites", total)
emit("authorization_request_assignment_files", files)

# --------------------------------------------------------------------------
# SaveChangesAsync call sites: invocations, not declarations.
# --------------------------------------------------------------------------

sc_sites = {rel: [m.start() for m in re.finditer(r"\bSaveChangesAsync\s*\(", CODE[rel])
                  if m.start() not in DECLARATIONS[rel]] for rel in FILES}
total, files = count_sites(sc_sites)
emit("save_changes_call_sites", total)
emit("save_changes_call_files", files)

# --------------------------------------------------------------------------
# Audit writes: product-code calls that record an audit event. Sinks are
# `new SqlOSAuditEvent` and RecordAsync on an *audit* receiver
# (SqlOSAuditLogService). Helpers are methods outside src/SqlOS/AuditLogs whose
# name or declaring type contains "Audit" and whose body reaches a sink or
# another helper. Calls inside helpers and inside AuditLogs are plumbing.
# --------------------------------------------------------------------------

AUDIT_MODULE = "src/SqlOS/AuditLogs/"
SINKS = [re.compile(r"\bnew\s+SqlOSAuditEvent\b"), re.compile(r"\b\w*[Aa]udit\w*\s*\.\s*RecordAsync\s*\(")]
candidates = {}
for m in METHODS:
    if not m.is_ctor and not m.file.startswith(AUDIT_MODULE) and ("Audit" in m.name or "Audit" in m.type_name):
        candidates.setdefault(m.name, []).append(m)


def calls_to(names):
    return re.compile(r"\b(?:" + "|".join(sorted(names)) + r")\s*\(") if names else None


helpers = set()
changed = True
while changed:
    changed = False
    helper_calls = calls_to(helpers)
    for name in sorted(candidates):
        if name in helpers:
            continue
        for m in candidates[name]:
            body = CODE[m.file][m.start:m.end + 1]
            if any(s.search(body) for s in SINKS) or (helper_calls and helper_calls.search(body)):
                helpers.add(name)
                changed = True
                break
helper_spans = {}
for m in METHODS:
    if m.name in helpers:
        helper_spans.setdefault(m.file, []).append((m.start, m.end))
helper_calls = calls_to(helpers)
audit_sites = {}
for rel in FILES:
    audit_sites[rel] = []
    if rel.startswith(AUDIT_MODULE):
        continue
    S = CODE[rel]
    offsets = [m.start() for s in SINKS for m in s.finditer(S)]
    if helper_calls:
        offsets += [m.start() for m in helper_calls.finditer(S) if m.start() not in DECLARATIONS[rel]]
    spans = helper_spans.get(rel, [])
    audit_sites[rel] = [o for o in offsets if not any(a <= o <= b for a, b in spans)]
total, files = count_sites(audit_sites)
emit("audit_write_sites", total)
emit("audit_write_files", files)

# --------------------------------------------------------------------------
# Clock reads, method length, class and file size, constructor width.
# --------------------------------------------------------------------------

emit("datetime_utcnow_uses", sum(len(re.findall(r"\bDateTime\s*\.\s*UtcNow\b", CODE[rel])) for rel in FILES))

emit("methods_over_100_lines", sum(1 for m in METHODS if span_lines(m.file, m.start, m.end) > 100))

class_lines = {}
for td in TYPES:
    if td.kind in ("class", "record", "struct") and not td.is_static:
        class_lines[td.key] = class_lines.get(td.key, 0) + span_lines(td.file, td.start, td.end)
largest = sorted(class_lines.items(), key=lambda kv: (-kv[1], kv[0])) or [("", 0)]
emit("largest_class_lines", largest[0][1])
emit("largest_class", largest[0][0].rsplit(".", 1)[-1])

largest_file = sorted(((RAW[rel].count("\n"), rel) for rel in FILES), key=lambda x: (-x[0], x[1])) or [(0, "")]
emit("largest_file_lines", largest_file[0][0])
emit("largest_file", largest_file[0][1])

constructors = [(len(split_top_level(m.params)), m.type_name) for m in METHODS if m.is_ctor]
constructors += [(len(split_top_level(td.primary_params)), td.name) for td in TYPES
                 if td.kind in ("class", "struct") and td.primary_params is not None]
widest = sorted(constructors, key=lambda x: (-x[0], x[1])) or [(0, "")]
emit("max_constructor_parameters", widest[0][0])
emit("max_constructor_parameters_class", widest[0][1])

# --------------------------------------------------------------------------
# Duplicated flows: distinct *Service classes declaring a public or internal
# method whose name matches the flow.
# --------------------------------------------------------------------------

FLOWS = (
    ("signup", r"SignUp\w*Async"),
    ("mfa_verify", r"(?:Verify|Complete)\w*Mfa\w*Async"),
    ("email_otp_verify", r"(?:Verify|Complete)\w*EmailOtp\w*Async"),
    ("magic_link_complete", r"(?:Verify|Complete)\w*MagicLink\w*Async"),
    ("organization_select", r"(?:Select|Complete)\w*Organization\w*Async"),
)
for key, pattern in FLOWS:
    owners = set(m.type_name for m in METHODS
                 if not m.is_ctor and m.type_name.endswith("Service")
                 and m.mods & {"public", "internal"} and re.fullmatch(pattern, m.name))
    emit("duplicated_flow_" + key + "_services", len(owners))

if FORMAT == "json":
    print(json.dumps(dict(RESULTS), indent=2))
else:
    for key, value in RESULTS:
        print("%s=%s" % (key, value))
PY
