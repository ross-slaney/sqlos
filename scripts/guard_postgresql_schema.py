#!/usr/bin/env python3
"""Make PostgreSQL schema scripts skip missing tables the way SQL Server OBJECT_ID guards do."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOLLAR = re.compile(r"\$[A-Za-z0-9_]*\$")
CREATE_INDEX = re.compile(
    r'CREATE\s+(UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+"[^"]+"\s+'
    r'ON\s+"\{Schema\}"\."([A-Za-z0-9_]+)"\s*\((.*?)\)'
    r'(\s+WHERE\s+.*?)?\s*;',
    re.I | re.S,
)
CREATE_TABLE = re.compile(
    r'CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+"\{Schema\}"\."([A-Za-z0-9_]+)"',
    re.I,
)
ALTER_TABLE = re.compile(
    r'ALTER\s+TABLE\s+(?!IF\s+EXISTS\s)"(\{Schema\})"\."([A-Za-z0-9_]+)"',
    re.I,
)
UPDATE = re.compile(
    r'UPDATE\s+"\{Schema\}"\."([A-Za-z0-9_]+)"(?:\s+AS\s+[A-Za-z0-9_]+)?',
    re.I,
)
DELETE = re.compile(
    r'DELETE\s+FROM\s+"\{Schema\}"\."([A-Za-z0-9_]+)"',
    re.I,
)
INSERT = re.compile(
    r'INSERT\s+INTO\s+"\{Schema\}"\."([A-Za-z0-9_]+)"',
    re.I,
)
SCHEMA_TABLES = {"SqlOSSchema", "SqlOSFgaSchema", "SqlOSAppliedMigrations"}
REFERENCES = re.compile(r'REFERENCES\s+"\{Schema\}"\."([A-Za-z0-9_]+)"', re.I)
IDENT = re.compile(r'"([A-Za-z0-9_]+)"')


def table_exists(table: str) -> str:
    return f"to_regclass(format('%I.%I', '{{Schema}}', '{table}')) IS NOT NULL"


def column_exists(table: str, column: str) -> str:
    return (
        "EXISTS (SELECT 1 FROM information_schema.columns "
        f"WHERE table_schema = '{{Schema}}' AND table_name = '{table}' "
        f"AND column_name = '{column}')"
    )


def wrap(condition: str, body: str) -> str:
    body = body.strip()
    if not body.endswith(";"):
        body += ";"
    return (
        "DO $sqlos_guard$\n"
        "BEGIN\n"
        f"  IF {condition} THEN\n"
        f"    {body}\n"
        "  END IF;\n"
        "END\n"
        "$sqlos_guard$;"
    )


def find_matching_paren(sql: str, open_at: int) -> int:
    depth = 0
    for index in range(open_at, len(sql)):
        char = sql[index]
        if char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return index
    return -1


def statement_end(sql: str, start: int) -> int:
    index = start
    while index < len(sql):
        if sql[index] == "$":
            match = DOLLAR.match(sql, index)
            if match:
                tag = match.group(0)
                close = sql.find(tag, match.end())
                if close < 0:
                    return len(sql)
                index = close + len(tag)
                continue
        if sql[index] == "'":
            index += 1
            while index < len(sql):
                if sql[index] == "'":
                    if index + 1 < len(sql) and sql[index + 1] == "'":
                        index += 2
                        continue
                    index += 1
                    break
                index += 1
            continue
        if sql[index] == "(":
            close = find_matching_paren(sql, index)
            if close < 0:
                return len(sql)
            index = close + 1
            continue
        if sql[index] == ";":
            return index + 1
        index += 1
    return len(sql)


def split_dollar_segments(sql: str) -> list[tuple[str, str]]:
    parts: list[tuple[str, str]] = []
    index = 0
    while index < len(sql):
        match = DOLLAR.search(sql, index)
        if not match:
            parts.append(("sql", sql[index:]))
            break
        if match.start() > index:
            parts.append(("sql", sql[index : match.start()]))
        tag = match.group(0)
        close = sql.find(tag, match.end())
        if close < 0:
            parts.append(("sql", sql[match.start() :]))
            break
        parts.append(("do", sql[match.start() : close + len(tag)]))
        index = close + len(tag)
    return parts


def index_columns(column_sql: str) -> list[str]:
    columns: list[str] = []
    for raw in re.split(r",(?![^()]*\))", column_sql):
        match = IDENT.search(raw)
        if match:
            columns.append(match.group(1))
    return columns


def already_guarded(prefix: str) -> bool:
    tail = prefix[-240:]
    return "sqlos_guard" in tail or "to_regclass(" in tail


def rewrite_sql_segment(sql: str) -> str:
    sql = ALTER_TABLE.sub(r'ALTER TABLE IF EXISTS "\1"."\2"', sql)
    pieces: list[str] = []
    index = 0
    while index < len(sql):
        create_table = CREATE_TABLE.search(sql, index)
        create_index = CREATE_INDEX.search(sql, index)
        update = UPDATE.search(sql, index)
        delete = DELETE.search(sql, index)
        insert = INSERT.search(sql, index)
        candidates = [match for match in (create_table, create_index, update, delete, insert) if match]
        if not candidates:
            pieces.append(sql[index:])
            break
        match = min(candidates, key=lambda item: item.start())
        pieces.append(sql[index : match.start()])
        if already_guarded(sql[max(0, match.start() - 240) : match.start()]):
            end = statement_end(sql, match.start())
            pieces.append(sql[match.start() : end])
            index = end
            continue

        if match is create_table:
            paren = sql.find("(", match.end())
            close = find_matching_paren(sql, paren) if paren >= 0 else -1
            end = statement_end(sql, close + 1 if close >= 0 else match.start())
            body = sql[match.start() : end]
            referenced = REFERENCES.findall(body)
            if referenced:
                condition = " AND ".join(table_exists(table) for table in dict.fromkeys(referenced))
                pieces.append(wrap(condition, body))
            else:
                pieces.append(body)
            index = end
            continue

        if match is create_index:
            table = match.group(2)
            columns = index_columns(match.group(3))
            end = match.end()
            body = sql[match.start() : end]
            checks = [table_exists(table), *[column_exists(table, column) for column in columns]]
            pieces.append(wrap(" AND ".join(checks), body))
            index = end
            continue

        table = match.group(1)
        end = statement_end(sql, match.start())
        body = sql[match.start() : end]
        if table in SCHEMA_TABLES:
            pieces.append(body)
            index = end
            continue
        extras = REFERENCES.findall(body)
        extras += re.findall(r'FROM\s+"\{Schema\}"\."([A-Za-z0-9_]+)"', body, flags=re.I)
        extras += re.findall(r'JOIN\s+"\{Schema\}"\."([A-Za-z0-9_]+)"', body, flags=re.I)
        tables = [table, *[item for item in extras if item != table]]
        condition = " AND ".join(table_exists(name) for name in dict.fromkeys(tables))
        pieces.append(wrap(condition, body))
        index = end
    return "".join(pieces)


def guard_missing_relations(sql: str) -> str:
    guarded: list[str] = []
    for kind, segment in split_dollar_segments(sql):
        if kind == "do":
            guarded.append(ALTER_TABLE.sub(r'ALTER TABLE IF EXISTS "\1"."\2"', segment))
            continue
        guarded.append(rewrite_sql_segment(segment))
    return "".join(guarded)


def guard_tree(directory: Path) -> int:
    count = 0
    for path in sorted(directory.glob("*.sql")):
        original = path.read_text(encoding="utf-8")
        updated = guard_missing_relations(original)
        if updated != original:
            path.write_text(updated, encoding="utf-8")
            count += 1
    return count


def main() -> None:
    auth = ROOT / "src/SqlOS/AuthServer/Schema/PostgreSql"
    fga = ROOT / "src/SqlOS/Fga/Schema/PostgreSql"
    print("guarded", guard_tree(auth), "auth scripts")
    print("guarded", guard_tree(fga), "fga scripts")


if __name__ == "__main__":
    main()
