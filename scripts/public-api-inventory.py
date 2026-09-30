#!/usr/bin/env python3
"""Classify every public SqlOS type and write docs/architecture/public-api-inventory.md.

The types come from the approved PublicApiGenerator snapshot of the SqlOS assembly
(tests/SqlOS.BehaviorLock/PublicApi/SqlOS.verified.txt). The evidence comes from the
documentation and the examples. The generated page states the rules; change both together.

    python3 scripts/public-api-inventory.py              # write the page
    python3 scripts/public-api-inventory.py --check      # exit 1 when the committed page is stale
    python3 scripts/public-api-inventory.py --root DIR   # inventory another checkout

Standard library only. The output depends only on the files in the checkout: it is sorted,
uses LF line endings and contains no absolute paths or timestamps.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple

APPROVAL = "tests/SqlOS.BehaviorLock/PublicApi/SqlOS.verified.txt"
OUTPUT = "docs/architecture/public-api-inventory.md"
LEDGER = "docs/architecture/8.0-behavior-ledger.md"
SCRIPT = "scripts/public-api-inventory.py"

HOST_API = "Host API"
EXTENSION_POINT = "Extension point"
INCIDENTAL = "Incidental"
CLASSIFICATIONS = (HOST_API, EXTENSION_POINT, INCIDENTAL)

CORPUS_ROOTS = ("README.md", "docs", "examples", "web/content/docs")
EXCLUDED_DIRECTORIES = frozenset({"bin", "obj", "node_modules"})
EXAMPLE_SUFFIXES = (".cs", ".cshtml", ".razor")
GENERATED_MIGRATION_SUFFIXES = (".Designer.cs", "ModelSnapshot.cs")
CONFIGURATION_SUFFIXES = ("Options", "Builder")
MAX_FILES = 5
MAX_EXPOSURES = 3

# Issue #435, "Current SqlOS context > Public surface", measured on SqlOS 7.2.0 (d608257).
ISSUE_COMMIT = "d608257"
ISSUE_PUBLIC_TYPES = 539
ISSUE_SERVICE_CLASSES = 47
ISSUE_UNREFERENCED_SERVICES = 29
# Verified with `git grep` on d608257: 539 = 512 visible types + 25 extra partial declarations + 2 records
# nested in internal classes (SqlOSMigrationManifest.Script, SqlOSSignupOrchestration.PasswordSignupInput).
PUBLIC_TYPES_REASON = (
    "The issue counted `public` type declarations in `src/SqlOS` on 7.2.0, including 25 extra `partial` "
    "declarations (`EndpointRouteBuilderExtensions` 21, `SqlOSAdminService` 4) and 2 records nested in internal "
    "classes: 512 visible types. 7.2.1 added `SqlOSScimGrantBoundaryErrors` and `SqlOSScimGrantBoundaryException`."
)

DECLARATION = re.compile(
    r"^(?P<indent>(?: {4})+)"
    r"(?P<access>public|protected(?: internal)?)\s+"
    r"(?P<modifiers>(?:(?:new|static|sealed|abstract|readonly|ref|unsafe|partial)\s+)*)"
    r"(?P<keyword>record\s+struct|record\s+class|record|class|struct|interface|enum|delegate)\s+"
    r"(?P<rest>\S.*)$"
)
IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
TRAILING_IDENTIFIER = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)$")
WORD = re.compile(r"[A-Za-z0-9_]+")
QUALIFIED_NAME = re.compile(r"(?<![A-Za-z0-9_.])[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+")
GENERIC_BEFORE_DOT = re.compile(r"<[^<>]*>(?=\.)")
EQUALS_MEMBER = re.compile(r"\bEquals\s*\(")
THIS_PARAMETER = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*this\s")


class InventoryError(Exception):
    pass


class PublicType:
    def __init__(
        self,
        namespace: str,
        parent: Optional["PublicType"],
        access: str,
        modifiers: List[str],
        keyword: str,
        simple: str,
        type_parameters: List[str],
        bases: List[str],
    ) -> None:
        self.namespace = namespace
        self.parent = parent
        self.access = access
        self.modifiers = modifiers
        self.keyword = keyword
        self.simple = simple
        self.type_parameters = type_parameters
        self.bases = bases
        self.members: List[str] = []

    @property
    def display(self) -> str:
        own = self.simple
        if self.type_parameters:
            own += "<" + ", ".join(self.type_parameters) + ">"
        return f"{self.parent.display}.{own}" if self.parent else own

    @property
    def qualified_display(self) -> str:
        return f"{self.namespace}.{self.display}"

    @property
    def path(self) -> str:
        """The dotted name member signatures use for the type, without type parameters."""
        return f"{self.parent.path}.{self.simple}" if self.parent else f"{self.namespace}.{self.simple}"

    @property
    def nested_name(self) -> str:
        """`Outer.Inner`, without namespace or type parameters."""
        return f"{self.parent.nested_name}.{self.simple}" if self.parent else self.simple

    @property
    def is_class(self) -> bool:
        return self.keyword in ("class", "record", "record class")

    @property
    def is_static(self) -> bool:
        return "static" in self.modifiers

    @property
    def is_record(self) -> bool:
        if self.keyword.startswith("record"):
            return True
        if self.keyword not in ("class", "struct"):
            return False
        implements_own_equality = f"System.IEquatable<{self.qualified_display}>" in self.bases
        return implements_own_equality and not any(EQUALS_MEMBER.search(member) for member in self.members)

    @property
    def kind(self) -> str:
        keyword = self.keyword
        if keyword == "class" and self.is_record:
            keyword = "record"
        elif keyword == "struct" and self.is_record:
            keyword = "record struct"
        access = [] if self.access == "public" else [self.access]
        return " ".join(access + self.modifiers + [keyword])

    @property
    def has_accessible_constructor(self) -> bool:
        constructor = re.compile(r"^(?:public|protected)(?:\s+internal)?\s+" + re.escape(self.simple) + r"\s*\(")
        return any(constructor.match(member) for member in self.members)

    @property
    def extension_methods(self) -> List[str]:
        if not (self.is_class and self.is_static):
            return []
        names = set()
        for member in self.members:
            name, parameters = split_at_parameters(member)
            if name and THIS_PARAMETER.match(parameters):
                names.add(name)
        return sorted(names)

    @property
    def configuration_suffix(self) -> Optional[str]:
        if self.keyword == "delegate":
            return None
        for suffix in CONFIGURATION_SUFFIXES:
            if self.simple.endswith(suffix):
                return suffix
        return None

    @property
    def is_service_class(self) -> bool:
        return self.is_class and not self.is_record and self.simple.endswith("Service")


def is_compound(name: str) -> bool:
    """True for names with two or more capitals, such as `GrantRoleAsync`; false for `Allows`."""
    return sum(1 for char in name if "A" <= char <= "Z") >= 2


def strip_trailing_generic(text: str) -> str:
    if not text.endswith(">"):
        return text
    depth = 0
    for index in range(len(text) - 1, -1, -1):
        if text[index] == ">":
            depth += 1
        elif text[index] == "<":
            depth -= 1
            if depth == 0:
                return text[:index].rstrip()
    return text


def name_before(text: str) -> str:
    match = TRAILING_IDENTIFIER.search(strip_trailing_generic(text.rstrip()))
    return match.group(1) if match else ""


def split_at_parameters(member: str) -> Tuple[str, str]:
    """Return (method name, text after its opening parenthesis), or ("", "") for other members."""
    depth = 0
    for index, char in enumerate(member):
        if char == "<":
            depth += 1
        elif char == ">":
            depth -= 1
        elif depth == 0 and char in "{=;":
            return "", ""
        elif depth == 0 and char == "(":
            return name_before(member[:index]), member[index + 1 :]
    return "", ""


def member_name(member: str) -> str:
    depth = 0
    for index, char in enumerate(member):
        if char == "<":
            depth += 1
        elif char == ">":
            depth -= 1
        elif depth == 0 and char in "({=;":
            return name_before(member[:index])
    return ""


def split_top_level(text: str, separator: str = ",") -> List[str]:
    parts: List[str] = []
    depth = 0
    current: List[str] = []
    for char in text:
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth -= 1
        if char == separator and depth == 0:
            parts.append("".join(current).strip())
            current = []
        else:
            current.append(char)
    tail = "".join(current).strip()
    if tail:
        parts.append(tail)
    return [part for part in parts if part]


def parse_type_name(text: str) -> Tuple[str, List[str], str]:
    """Split `Name<in T1, T2> : Bases` into ("Name", ["T1", "T2"], " : Bases")."""
    match = IDENTIFIER.match(text)
    if not match:
        raise InventoryError(f"cannot read a type name from '{text}'")
    name = match.group(0)
    index = match.end()
    parameters: List[str] = []
    if index < len(text) and text[index] == "<":
        depth = 0
        for end in range(index, len(text)):
            if text[end] == "<":
                depth += 1
            elif text[end] == ">":
                depth -= 1
                if depth == 0:
                    break
        else:
            raise InventoryError(f"unbalanced type parameters in '{text}'")
        parameters = [part.split()[-1] for part in split_top_level(text[index + 1 : end])]
        index = end + 1
    return name, parameters, text[index:]


def parse_declaration(
    match: "re.Match[str]", namespace: str, parent: Optional[PublicType]
) -> Tuple[PublicType, bool]:
    """Return the declared type and whether the declaration line already ends its body."""
    access = match.group("access")
    modifiers = match.group("modifiers").split()
    keyword = " ".join(match.group("keyword").split())
    rest = match.group("rest").strip()
    if keyword == "delegate":
        depth = 0
        for index, char in enumerate(rest):
            if char == "<":
                depth += 1
            elif char == ">":
                depth -= 1
            elif char == "(" and depth == 0:
                signature = rest[:index].rstrip()
                break
        else:
            raise InventoryError(f"cannot read the delegate '{rest}'")
        name, parameters, _ = parse_type_name(split_top_level(signature, " ")[-1])
        declared = PublicType(namespace, parent, access, modifiers, keyword, name, parameters, [])
        declared.members.append(rest)
        return declared, True
    closed = rest.endswith("{ }")
    if closed:
        rest = rest[: -len("{ }")].rstrip()
    name, parameters, remainder = parse_type_name(rest)
    remainder = remainder.strip()
    if remainder and not remainder.startswith(":"):
        raise InventoryError(f"unexpected text after the type name in '{rest}'")
    bases = split_top_level(remainder[1:]) if remainder else []
    return PublicType(namespace, parent, access, modifiers, keyword, name, parameters, bases), closed


class OpenType:
    def __init__(self, declared: PublicType, indent: int) -> None:
        self.type = declared
        self.indent = indent
        self.body_open = False


def parse_approval(text: str) -> List[PublicType]:
    """Read PublicApiGenerator output: namespace blocks of 4-space indented declarations."""
    types: List[PublicType] = []
    namespace: Optional[str] = None
    stack: List[OpenType] = []
    member: Optional[List[str]] = None  # lines of the member being read, or None
    for number, raw in enumerate(text.split("\n"), 1):
        line = raw.rstrip()
        if not line.strip():
            continue
        try:
            if line.startswith("namespace ") or line in ("{", "}"):
                if stack:
                    raise InventoryError("namespace line inside a type")
                if line.startswith("namespace "):
                    namespace = line[len("namespace ") :].strip()
                member = None
                continue
            if namespace is None:
                raise InventoryError("declaration outside a namespace")
            indent = len(line) - len(line.lstrip(" "))
            stripped = line.strip()
            top = stack[-1] if stack else None
            if top is not None and indent == top.indent and stripped in ("{", "}"):
                if stripped == "{":
                    top.body_open = True
                else:
                    stack.pop()
                member = None
                continue
            declaration = DECLARATION.match(line)
            nested_level = top is None or top.body_open
            expected_indent = top.indent + 4 if top is not None else 4
            if declaration and nested_level and indent == expected_indent:
                declared, closed = parse_declaration(declaration, namespace, top.type if top else None)
                types.append(declared)
                if not closed:
                    stack.append(OpenType(declared, indent))
                member = None
                continue
            if top is None:
                # Attributes on the next top-level type, and their continuation lines.
                if (indent == 4 and stripped.startswith("[")) or (indent > 4 and member is None):
                    continue
                raise InventoryError(f"unexpected line outside a type: '{stripped}'")
            if not top.body_open:
                if indent == top.indent + 4 and stripped.startswith("where "):
                    if stripped.endswith("{ }"):
                        stack.pop()
                    continue
                raise InventoryError(f"unexpected line before the body of {top.type.display}: '{stripped}'")
            if indent == top.indent + 4:
                if stripped.startswith("["):
                    member = None  # an attribute of the next member or nested type
                else:
                    member = [stripped]
                    top.type.members.append(stripped)
                continue
            if indent > top.indent + 4:
                if member is not None:  # a wrapped parameter list or constraint
                    member.append(stripped)
                    top.type.members[-1] = " ".join(member)
                continue
            raise InventoryError(f"unexpected indentation: '{stripped}'")
        except InventoryError as error:
            raise InventoryError(f"{APPROVAL}:{number}: {error}") from None
    if stack:
        raise InventoryError(f"{APPROVAL}: {stack[-1].type.display} is not closed")
    return types


def git_listed_files(root: Path) -> Optional[List[str]]:
    command = ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--"]
    try:
        result = subprocess.run(
            command + list(CORPUS_ROOTS), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True
        )
    except (OSError, subprocess.CalledProcessError):
        return None
    return [path for path in result.stdout.decode("utf-8", errors="surrogateescape").split("\0") if path]


def walked_files(root: Path) -> List[str]:
    found: List[str] = []
    for corpus_root in CORPUS_ROOTS:
        base = root / corpus_root
        if base.is_file():
            found.append(corpus_root)
            continue
        for directory, subdirectories, files in os.walk(base):
            subdirectories[:] = [d for d in subdirectories if d not in EXCLUDED_DIRECTORIES and not d.startswith(".")]
            relative = Path(directory).relative_to(root).as_posix()
            found.extend(f"{relative}/{name}" for name in files)
    return found


def corpus_kind(path: str) -> Optional[str]:
    parts = path.split("/")
    directories, name = parts[:-1], parts[-1]
    if any(part in EXCLUDED_DIRECTORIES or part.startswith(".") for part in directories):
        return None
    if path == "README.md":
        return "doc"
    if parts[0] == "docs" and name.endswith(".md") and not path.startswith("docs/architecture/"):
        return "doc"
    if path.startswith("web/content/docs/") and name.endswith(".mdx"):
        return "doc"
    if parts[0] == "examples" and name.endswith(EXAMPLE_SUFFIXES):
        if "Migrations" in directories and name.endswith(GENERATED_MIGRATION_SUFFIXES):
            return None
        return "example"
    return None


def corpus_files(root: Path) -> List[str]:
    listed = git_listed_files(root)
    if listed is None:
        listed = walked_files(root)
    return sorted({path for path in listed if corpus_kind(path) and (root / path).is_file()})


def alternation(names: Set[str]) -> str:
    return "|".join(re.escape(name) for name in sorted(names, key=lambda name: (-len(name), name)))


class Evidence:
    """Per-name reference counts by file."""

    def __init__(self, root: Path, files: Sequence[str], words: Set[str], dotted: Set[str], calls: Set[str]) -> None:
        self.words: Dict[str, Counter] = defaultdict(Counter)
        self.dotted: Dict[str, Counter] = defaultdict(Counter)
        self.calls: Dict[str, Counter] = defaultdict(Counter)
        dotted_pattern = None
        if dotted:
            dotted_pattern = re.compile(r"(?<![A-Za-z0-9_])(" + alternation(dotted) + r")(?![A-Za-z0-9_])")
        call_pattern = re.compile(r"\.\s*(" + alternation(calls) + r")\s*[<(]") if calls else None
        for path in files:
            text = (root / path).read_bytes().decode("utf-8", errors="replace")
            for word, count in Counter(WORD.findall(text)).items():
                if word in words:
                    self.words[word][path] += count
            if dotted_pattern is not None:
                for match in dotted_pattern.finditer(text):
                    self.dotted[match.group(1)][path] += 1
            if call_pattern is not None:
                for match in call_pattern.finditer(text):
                    self.calls[match.group(1)][path] += 1


class Row:
    def __init__(self, public_type: PublicType) -> None:
        self.type = public_type
        self.references: Counter = Counter()
        self.name_referenced = False
        self.shared_name_count = 1
        self.matched_methods: List[str] = []
        self.reasons: List[str] = []
        self.exposures: List[str] = []
        self.classification = INCIDENTAL

    @property
    def evidence(self) -> str:
        if self.classification == HOST_API:
            items = sorted(self.references.items())
            text = ", ".join(f"`{path}` ({count})" for path, count in items[:MAX_FILES])
            if len(items) > MAX_FILES:
                text += f", +{len(items) - MAX_FILES} more"
            if self.matched_methods:
                noun = "extension method" if len(self.matched_methods) == 1 else "extension methods"
                text += f"; via {noun} " + ", ".join(f"`{name}`" for name in self.matched_methods)
            if self.name_referenced and self.shared_name_count > 1:
                text += f"; name shared by {self.shared_name_count} public types"
            return text
        if self.classification == EXTENSION_POINT:
            reasons = list(self.reasons)
            if self.exposures:
                shown = ", ".join(f"`{name}`" for name in self.exposures[:MAX_EXPOSURES])
                if len(self.exposures) > MAX_EXPOSURES:
                    shown += f", +{len(self.exposures) - MAX_EXPOSURES} more"
                reasons.append("exposed by " + shown)
            return "; ".join(reasons)
        return "none"


def structural_reasons(public_type: PublicType) -> List[str]:
    reasons = []
    if public_type.keyword == "interface":
        reasons.append("interface")
    elif public_type.keyword == "delegate":
        reasons.append("delegate")
    elif public_type.is_class and not public_type.is_static:
        noun = "record" if public_type.is_record else "class"
        if "abstract" in public_type.modifiers:
            reasons.append(f"abstract {noun}")
        elif "sealed" not in public_type.modifiers and public_type.has_accessible_constructor:
            reasons.append(f"unsealed {noun}")
    suffix = public_type.configuration_suffix
    if suffix == "Options":
        reasons.append("options type")
    elif suffix == "Builder":
        reasons.append("builder type")
    return reasons


def configuration_exposures(types: Sequence[PublicType]) -> Dict[int, List[str]]:
    """Map id(type) to the `Owner.Member` signatures of options and builder types that mention it."""
    by_path: Dict[str, List[PublicType]] = defaultdict(list)
    for public_type in types:
        by_path[public_type.path].append(public_type)
    exposures: Dict[int, Set[str]] = defaultdict(set)
    for owner in types:
        if owner.configuration_suffix is None:
            continue
        for member in owner.members:
            label = f"{owner.display}.{member_name(member)}"
            text = member
            while True:
                reduced = GENERIC_BEFORE_DOT.sub("", text)
                if reduced == text:
                    break
                text = reduced
            for match in QUALIFIED_NAME.finditer(text):
                segments = match.group(0).split(".")
                while len(segments) > 1:
                    candidates = by_path.get(".".join(segments))
                    if candidates:
                        for candidate in candidates:
                            if candidate is not owner:
                                exposures[id(candidate)].add(label)
                        break
                    segments.pop()
    return {key: sorted(labels) for key, labels in exposures.items()}


def classify(root: Path, types: Sequence[PublicType], files: Sequence[str]) -> List[Row]:
    simple_counts = Counter(public_type.simple for public_type in types)

    def matches_simple_name(public_type: PublicType) -> bool:
        if public_type.parent is None:
            return True
        return "SqlOS" in public_type.simple and simple_counts[public_type.simple] == 1

    words: Set[str] = set()
    dotted: Set[str] = set()
    calls: Set[str] = set()
    for public_type in types:
        if matches_simple_name(public_type):
            words.add(public_type.simple)
        else:
            dotted.add(public_type.nested_name)
        for method in public_type.extension_methods:
            (words if is_compound(method) else calls).add(method)
    evidence = Evidence(root, files, words, dotted, calls)
    exposures = configuration_exposures(types)

    rows = []
    for public_type in types:
        row = Row(public_type)
        if matches_simple_name(public_type):
            found = evidence.words.get(public_type.simple, Counter())
            row.shared_name_count = simple_counts[public_type.simple]
        else:
            found = evidence.dotted.get(public_type.nested_name, Counter())
        row.references.update(found)
        row.name_referenced = bool(found)
        for method in public_type.extension_methods:
            found = (evidence.words if is_compound(method) else evidence.calls).get(method, Counter())
            if found:
                row.references.update(found)
                row.matched_methods.append(method)
        row.reasons = structural_reasons(public_type)
        row.exposures = exposures.get(id(public_type), [])
        if row.references:
            row.classification = HOST_API
        elif row.reasons or row.exposures:
            row.classification = EXTENSION_POINT
        rows.append(row)
    rows.sort(key=lambda row: (row.type.namespace, row.type.display))
    return rows


def cell(text: str) -> str:
    return text.replace("|", "\\|")


def paragraph(*sentences: str) -> str:
    return " ".join(sentences)


def render(rows: Sequence[Row], files: Sequence[str]) -> str:
    counts = Counter(row.classification for row in rows)
    services = [row for row in rows if row.type.is_service_class]
    unreferenced_services = [row for row in services if not row.references]
    doc_files = sum(1 for path in files if corpus_kind(path) == "doc")
    example_files = len(files) - doc_files

    lines: List[str] = []
    add = lines.append
    add("# SqlOS public API inventory")
    add("")
    add(
        paragraph(
            "This page classifies every public type of the `SqlOS` assembly as Host API, Extension point or",
            "Incidental, with the evidence for each. Tasks 2 to 5 of the 8.0.0 refactor (#436 to #439) use it to",
            "justify any public API change. A change to a Host API type needs a behavior-ledger entry in",
            f"`{LEDGER}`, and the approval [`{APPROVAL}`](../../{APPROVAL}) changes with it.",
        )
    )
    add("")
    add(f"[`{SCRIPT}`](../../{SCRIPT}) generates this page. Do not edit it by hand.")
    add("")
    add("## Regenerate")
    add("")
    add("```bash")
    add(f"python3 {SCRIPT}              # rewrite this page")
    add(f"python3 {SCRIPT} --check      # exit 1 when this page is stale")
    add(f"python3 {SCRIPT} --root <dir> # inventory another checkout")
    add("```")
    add("")
    add(
        paragraph(
            "The script needs only python3, reads the approval, the documentation and the examples without building",
            "anything, and writes the same bytes for the same files. `--check` exits 1 when the page is stale, and",
            "the script exits 2 when it cannot read the approval, for example after a PublicApiGenerator format",
            "change. Regenerate the page in the change that edits the approval, a documentation page or an example.",
        )
    )
    add("")
    add("## Rules")
    add("")
    add(
        paragraph(
            "**Types.** Each type declaration in the approval is one row: classes, records, structs, interfaces,",
            "enums and delegates, including static classes and nested types, which are named `Outer.Inner`. A",
            "generic type shows its type parameters, as in `SqlOSCursorPage<T>`, and matches by its name without",
            "them. Kind repeats the declaration's modifiers and keyword. PublicApiGenerator renders a record as a",
            "class or struct that implements `System.IEquatable<T>` of itself, so such a type with no `Equals`",
            "member in the approval is shown as a record.",
        )
    )
    add("")
    add("**Evidence files.**")
    add("")
    add(
        "- Documentation: `web/content/docs/**/*.mdx`, `docs/**/*.md` except `docs/architecture/**`, and the root "
        "`README.md`."
    )
    add(
        paragraph(
            "- Examples: `examples/**/*.cs`, `*.cshtml` and `*.razor`, except the EF Core files generated in",
            "`Migrations/` (`*.Designer.cs` and `*ModelSnapshot.cs`).",
        )
    )
    add(
        paragraph(
            "- The script lists files with `git ls-files --cached --others --exclude-standard`, or walks the",
            "directories outside git. A path under `bin/`, `obj/`, `node_modules/` or a directory whose name starts",
            "with `.` never counts.",
        )
    )
    add("")
    add("**Matching.**")
    add("")
    add(
        paragraph(
            "- A reference is a whole, case-sensitive identifier, where identifier characters are ASCII letters,",
            "digits and `_`: `SqlOSUser` matches neither `SqlOSUserEmail` nor `ISqlOSUser`.",
        )
    )
    add(
        "- Every occurrence counts: prose, code, comments, link targets and file names such as "
        "`SqlOSCryptoService.cs`."
    )
    add(
        paragraph(
            "- A type matches by its simple name, without namespace or type parameters. Types that share a simple",
            "name share its references, and the evidence says so.",
        )
    )
    add(
        paragraph(
            "- A nested type matches as `Outer.Inner`, or by its simple name alone when that name contains `SqlOS`",
            "and no other public type uses it.",
        )
    )
    add(
        paragraph(
            "- Hosts call extension methods without naming their static class, so a static class also matches its",
            "extension method names: a name with two or more capitals, such as `GrantRoleAsync`, as a whole",
            "identifier, and a one-word name, such as `Allows`, only as a call (`.Allows(` or `.Allows<`). Classes",
            "that declare the same method name share its references, as `ServiceCollectionExtensions` and",
            "`WebApplicationBuilderExtensions` do for `AddSqlOS`.",
        )
    )
    add("")
    add("**Classification.** Each type gets the first class that applies.")
    add("")
    add(
        paragraph(
            "1. **Host API**: at least one reference in the evidence files. The evidence lists the referencing files",
            "in path order with the number of references in each, the first",
            f"{MAX_FILES} and then the count of the rest, and names the extension methods that matched.",
        )
    )
    add(
        paragraph(
            "2. **Extension point**: no reference, but hosts can implement, inherit or configure the type. The",
            "evidence lists every reason that applies:",
        )
    )
    add("   - `interface` or `delegate`.")
    add(
        paragraph(
            "   - `abstract class`, or `unsealed class`: a class that is neither `sealed` nor `static` and has a",
            "public or protected constructor in the approval. Records read `abstract record` and `unsealed record`.",
        )
    )
    add("   - `options type` or `builder type`: the simple name ends in `Options` or `Builder`.")
    add(
        paragraph(
            "   - `exposed by Owner.Member`: the type appears in the signature of a public member of an options or",
            "builder type, as a property or field type, a parameter or return type, or a generic argument of one,",
            "such as the `T` of an `Action<T>` configure callback or the arguments of a `Func<...>` hook. Only",
            "direct signatures count, not the members of the exposed type. The first",
            f"{MAX_EXPOSURES} members are listed.",
        )
    )
    add("3. **Incidental**: neither. The evidence is `none`.")
    add("")
    add(
        paragraph(
            "The classification records use and shape, not intent. A Host API type may be an implementation detail",
            "that a page names, and an Incidental type may still appear in the signature of a Host API member.",
        )
    )
    add("")
    add("## Summary")
    add("")
    add(f"The evidence files are {doc_files} documentation pages and {example_files} example source files.")
    add("")
    add("| Classification | Types |")
    add("|---|---|")
    for classification in CLASSIFICATIONS:
        add(f"| {classification} | {counts[classification]} |")
    add(f"| Total public types | {len(rows)} |")
    add("")
    add("### Issue #435 metrics")
    add("")
    add(
        paragraph(
            f"Issue #435 measured the public surface on SqlOS 7.2.0 (`{ISSUE_COMMIT}`). The recomputed column applies",
            "this page's rules to the approval.",
        )
    )
    add("")
    add("| Metric | Issue #435 | Recomputed | Why the issue value differs |")
    add("|---|---|---|---|")
    add(f"| Public types | {ISSUE_PUBLIC_TYPES} | {len(rows)} | {PUBLIC_TYPES_REASON} |")
    add(
        f"| Public `*Service` classes | {ISSUE_SERVICE_CLASSES} | {len(services)} | "
        + ("None." if len(services) == ISSUE_SERVICE_CLASSES else "The approval differs from 7.2.0.")
        + " |"
    )
    add(
        f"| `*Service` classes in no doc and no example | {ISSUE_UNREFERENCED_SERVICES} | "
        f"{len(unreferenced_services)} | "
        + (
            "None."
            if len(unreferenced_services) == ISSUE_UNREFERENCED_SERVICES
            else "The approval, documentation or examples differ from 7.2.0."
        )
        + " |"
    )
    add("")
    unreferenced_names = sorted(row.type.display for row in unreferenced_services)
    add(
        "A `*Service` class is a class, not an interface or record, whose name ends in `Service`. "
        + (
            f"The {len(unreferenced_names)} with no reference are "
            + ", ".join(f"`{name}`" for name in unreferenced_names)
            + "."
            if unreferenced_names
            else "Every one has a reference."
        )
    )
    for namespace in sorted({row.type.namespace for row in rows}):
        namespace_rows = [row for row in rows if row.type.namespace == namespace]
        namespace_counts = Counter(row.classification for row in namespace_rows)
        add("")
        add(f"## {namespace}")
        add("")
        add(
            f"{len(namespace_rows)} {'type' if len(namespace_rows) == 1 else 'types'}: "
            + ", ".join(f"{namespace_counts[classification]} {classification}" for classification in CLASSIFICATIONS)
            + "."
        )
        add("")
        add("| Type | Kind | Classification | Evidence |")
        add("|---|---|---|---|")
        for row in namespace_rows:
            add(f"| `{cell(row.type.display)}` | {cell(row.type.kind)} | {row.classification} | {cell(row.evidence)} |")
    return "\n".join(lines) + "\n"


def generate(root: Path) -> str:
    approval = root / APPROVAL
    if not approval.is_file():
        raise InventoryError(f"missing {APPROVAL}")
    types = parse_approval(approval.read_bytes().decode("utf-8"))
    if not types:
        raise InventoryError(f"{APPROVAL}: no public types")
    files = corpus_files(root)
    return render(classify(root, types, files), files)


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=f"Write {OUTPUT} from the approved SqlOS public API.")
    parser.add_argument("--check", action="store_true", help=f"exit 1 if {OUTPUT} differs from the generated page")
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
        help="repository root (default: the checkout that contains this script)",
    )
    arguments = parser.parse_args(argv)
    root = arguments.root.resolve()
    try:
        content = generate(root).encode("utf-8")
    except InventoryError as error:
        print(f"public-api-inventory: {error}", file=sys.stderr)
        return 2
    output = root / OUTPUT
    if arguments.check:
        if not output.is_file() or output.read_bytes() != content:
            print(f"public-api-inventory: {OUTPUT} is stale. Run: python3 {SCRIPT}", file=sys.stderr)
            return 1
        print(f"public-api-inventory: {OUTPUT} is up to date.")
        return 0
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(content)
    print(f"public-api-inventory: wrote {OUTPUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
