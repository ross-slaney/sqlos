// Renders the public surface of @sqlos/headless for the approval snapshot in
// contract.test.ts. For each entry point in package.json "exports" it lists
// the runtime and type names the entry exports. It then prints, once per name:
// every exported declaration in a normalized .d.ts-like form (no comments,
// bodies, or initializers), every package declaration an export reaches
// without exporting it (such as the input types of HeadlessFlow methods), and
// the runtime value of every exported constant.
//
// The output is deterministic across machines: names and union members are
// sorted by code unit, line endings are LF, and it holds no absolute paths or
// source locations, so moving a declaration between files is not a change.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import ts from "typescript";

type PackageJson = {
  main?: string;
  module?: string;
  types?: string;
  exports: Record<string, string | Record<string, string>>;
};

type EntryPoint = { subpath: string; source: string };

type Rendered = { text: string; nodes: ts.Node[] };

/** name → rendered text → entry subpaths that export that text. */
type Variants = Map<string, Map<string, string[]>>;

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourcePrefix = `${toPosix(path.join(packageRoot, "src"))}/`;

const printer = ts.createPrinter({ newLine: ts.NewLineKind.LineFeed, removeComments: true });
const typeNodeFlags =
  ts.NodeBuilderFlags.NoTruncation | ts.NodeBuilderFlags.MultilineObjectLiterals | ts.NodeBuilderFlags.IgnoreErrors;
const droppedModifiers = new Set<ts.SyntaxKind>([
  ts.SyntaxKind.ExportKeyword,
  ts.SyntaxKind.DefaultKeyword,
  ts.SyntaxKind.DeclareKeyword,
  ts.SyntaxKind.AsyncKeyword,
]);

export async function renderPublicSurface(): Promise<string> {
  const pkg = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8")) as PackageJson;
  const entryPoints = entryPointsOf(pkg);
  const program = ts.createProgram({
    rootNames: entryPoints.map((entry) => path.join(packageRoot, entry.source)),
    options: compilerOptions(),
  });
  const checker = program.getTypeChecker();
  const renderer = createDeclarationRenderer(checker);

  const exportedSymbols = new Set<ts.Symbol>();
  const declarations: Variants = new Map();
  const values: Variants = new Map();
  const entrySections: string[] = [];

  for (const entry of entryPoints) {
    const fileName = path.join(packageRoot, entry.source);
    const sourceFile = program.getSourceFile(fileName);
    const moduleSymbol = sourceFile && checker.getSymbolAtLocation(sourceFile);
    if (!moduleSymbol) {
      throw new Error(`${entry.source} (package.json exports["${entry.subpath}"]) is not a module`);
    }
    const runtime = (await import(pathToFileURL(fileName).href)) as Record<string, unknown>;
    const runtimeExports = new Map<string, Set<string>>();
    const typeExports = new Map<string, Set<string>>();

    for (const exportSymbol of checker.getExportsOfModule(moduleSymbol)) {
      const name = exportSymbol.name;
      const symbol = resolveAlias(checker, exportSymbol);
      const symbolDeclarations = declarationsOf(symbol);
      if (symbolDeclarations.length === 0) {
        throw new Error(`${entry.subpath}: cannot resolve the declaration of export ${name}`);
      }
      exportedSymbols.add(symbol);
      for (const declaration of symbolDeclarations) {
        const kind = kindOf(declaration);
        if (ts.isInterfaceDeclaration(declaration) || ts.isTypeAliasDeclaration(declaration)) {
          addKind(typeExports, name, kind);
        } else if (name in runtime) {
          addKind(runtimeExports, name, ts.isVariableDeclaration(declaration) ? `${kind} ${valueKindOf(runtime[name])}` : kind);
        } else {
          addKind(typeExports, name, `${kind} (type-only export)`);
        }
      }
      const exportedAs = name === symbol.name ? "" : `// exported as ${name}\n`;
      addVariant(declarations, name, exportedAs + renderer.render(symbol).text, entry.subpath);
      if (name in runtime && symbolDeclarations.some(ts.isVariableDeclaration)) {
        addVariant(values, name, `${name} = ${formatValue(runtime[name])}`, entry.subpath);
      }
    }

    const undeclared = Object.keys(runtime).filter((name) => !runtimeExports.has(name));
    if (undeclared.length > 0) {
      throw new Error(`${entry.subpath} exports ${undeclared.join(", ")} at runtime without a matching declaration`);
    }
    entrySections.push(
      [
        `## Entry point ${JSON.stringify(entry.subpath)} (${entry.source})`,
        "",
        ...kindList("Runtime exports", runtimeExports),
        "",
        ...kindList("Type exports", typeExports),
      ].join("\n"),
    );
  }

  // Package declarations that exported declarations reach by name without
  // exporting them are still public shape: consumers depend on their fields.
  const reachable = new Map<ts.Symbol, string>();
  const pending = [...exportedSymbols].flatMap((symbol) => renderer.render(symbol).nodes);
  for (let node = pending.pop(); node; node = pending.pop()) {
    for (const symbol of renderer.references(node)) {
      if (!exportedSymbols.has(symbol) && !reachable.has(symbol)) {
        const rendered = renderer.render(symbol);
        reachable.set(symbol, rendered.text);
        pending.push(...rendered.nodes);
      }
    }
  }
  const reachableTexts = [...reachable]
    .map(([symbol, text]) => ({ name: symbol.name, text }))
    .sort((left, right) => compare(left.name, right.name) || compare(left.text, right.text))
    .map((item) => item.text);

  const sections = [
    [
      "# @sqlos/headless public surface",
      "#",
      "# Generated by the \"public surface\" test in tests/contract.test.ts; do not edit.",
      "# Approve an intended change with `npx vitest run -u` in packages/headless.",
    ].join("\n"),
    `## package.json entry fields\n\n${formatValue(pick(pkg, ["main", "module", "types", "exports"]))}`,
    ...entrySections,
    `## Exported declarations\n\n${renderVariants(declarations)}`,
    `## Declarations reached from exports but not exported\n\n${reachableTexts.join("\n\n") || "(none)"}`,
    `## Runtime values of exported constants\n\n${renderVariants(values) || "(none)"}`,
  ];
  return `${sections.join("\n\n")}\n`.replace(/\r\n?/g, "\n");
}

function createDeclarationRenderer(checker: ts.TypeChecker) {
  const factory = ts.factory;
  const cache = new Map<ts.Symbol, Rendered>();

  function typeNode(type: ts.Type, enclosing: ts.Node, flags = typeNodeFlags): ts.TypeNode {
    const node = checker.typeToTypeNode(type, enclosing, flags);
    if (!node) {
      throw new Error(`cannot print type ${checker.typeToString(type)}`);
    }
    return node;
  }

  function inferredType(declaration: ts.Declaration): ts.TypeNode {
    return typeNode(checker.getTypeAtLocation(declaration), declaration);
  }

  function returnType(declaration: ts.SignatureDeclaration): ts.TypeNode | undefined {
    if (declaration.type) {
      return declaration.type;
    }
    const signature = checker.getSignatureFromDeclaration(declaration);
    return signature && typeNode(checker.getReturnTypeOfSignature(signature), declaration);
  }

  /** `x = 1` becomes `x?: number`, as in a .d.ts; the default value is behavior, not shape. */
  function parameters(list: readonly ts.ParameterDeclaration[]): ts.ParameterDeclaration[] {
    return list.map((parameter) =>
      parameter.initializer
        ? factory.updateParameterDeclaration(
            parameter,
            ts.getModifiers(parameter),
            parameter.dotDotDotToken,
            parameter.name,
            parameter.dotDotDotToken ? undefined : factory.createToken(ts.SyntaxKind.QuestionToken),
            parameter.type ?? inferredType(parameter),
            undefined,
          )
        : parameter,
    );
  }

  function classMembers(declaration: ts.ClassDeclaration): ts.ClassElement[] {
    const members: ts.ClassElement[] = [];
    let hasPrivateNames = false;
    for (const member of declaration.members) {
      if (member.name && ts.isPrivateIdentifier(member.name)) {
        hasPrivateNames = true;
        continue;
      }
      if (!ts.canHaveModifiers(member)) {
        continue; // static blocks and stray semicolons are not surface
      }
      const modifiers = modifiersOf(member);
      if (member.name && modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.PrivateKeyword)) {
        // As in a .d.ts: a private member keeps its name, which affects assignability, but not its type.
        members.push(factory.createPropertyDeclaration(modifiers, member.name, undefined, undefined, undefined));
      } else if (ts.isPropertyDeclaration(member)) {
        members.push(
          factory.updatePropertyDeclaration(member, modifiers, member.name, member.questionToken, member.type ?? inferredType(member), undefined),
        );
      } else if (ts.isMethodDeclaration(member)) {
        members.push(
          factory.updateMethodDeclaration(
            member,
            modifiers,
            undefined,
            member.name,
            member.questionToken,
            member.typeParameters,
            parameters(member.parameters),
            returnType(member),
            undefined,
          ),
        );
      } else if (ts.isConstructorDeclaration(member)) {
        members.push(factory.updateConstructorDeclaration(member, modifiers, parameters(member.parameters), undefined));
      } else if (ts.isGetAccessorDeclaration(member)) {
        members.push(factory.updateGetAccessorDeclaration(member, modifiers, member.name, member.parameters, returnType(member), undefined));
      } else if (ts.isSetAccessorDeclaration(member)) {
        members.push(factory.updateSetAccessorDeclaration(member, modifiers, member.name, parameters(member.parameters), undefined));
      } else if (ts.isIndexSignatureDeclaration(member)) {
        members.push(member);
      }
    }
    if (hasPrivateNames) {
      members.unshift(factory.createPropertyDeclaration(undefined, factory.createPrivateIdentifier("#private"), undefined, undefined, undefined));
    }
    return members;
  }

  /** The declaration as a .d.ts would state it, or undefined when there is no printable node. */
  function normalize(declaration: ts.Declaration): ts.Node | undefined {
    if (ts.isTypeAliasDeclaration(declaration)) {
      return factory.updateTypeAliasDeclaration(declaration, modifiersOf(declaration), declaration.name, declaration.typeParameters, declaration.type);
    }
    if (ts.isInterfaceDeclaration(declaration)) {
      return factory.updateInterfaceDeclaration(
        declaration,
        modifiersOf(declaration),
        declaration.name,
        declaration.typeParameters,
        declaration.heritageClauses,
        declaration.members,
      );
    }
    if (ts.isEnumDeclaration(declaration)) {
      return factory.updateEnumDeclaration(declaration, modifiersOf(declaration), declaration.name, declaration.members);
    }
    if (ts.isFunctionDeclaration(declaration)) {
      return factory.updateFunctionDeclaration(
        declaration,
        modifiersOf(declaration),
        undefined,
        declaration.name,
        declaration.typeParameters,
        parameters(declaration.parameters),
        returnType(declaration),
        undefined,
      );
    }
    if (ts.isClassDeclaration(declaration)) {
      return factory.updateClassDeclaration(
        declaration,
        modifiersOf(declaration),
        declaration.name,
        declaration.typeParameters,
        declaration.heritageClauses,
        classMembers(declaration),
      );
    }
    if (ts.isVariableDeclaration(declaration) && ts.isVariableDeclarationList(declaration.parent)) {
      const variable = factory.createVariableDeclaration(declaration.name, undefined, declaration.type ?? inferredType(declaration), undefined);
      const flags = declaration.parent.flags & (ts.NodeFlags.Const | ts.NodeFlags.Let);
      return factory.createVariableStatement(undefined, factory.createVariableDeclarationList([variable], flags));
    }
    return undefined;
  }

  /**
   * A type alias computed from other declarations (`(typeof X)[number]`,
   * `keyof T`, ...) also prints what it resolves to, so a changed union
   * member shows up next to the alias. Union members are sorted: their order
   * carries no meaning and follows internal type ids.
   */
  function resolvedForm(symbol: ts.Symbol, declaration: ts.Declaration): string | undefined {
    if (!ts.isTypeAliasDeclaration(declaration) || !isComputedType(declaration.type)) {
      return undefined;
    }
    const type = checker.getDeclaredTypeOfSymbol(symbol);
    const lines = type.isUnion()
      ? type.types.map((member) => `| ${checker.typeToString(member, declaration, ts.TypeFormatFlags.NoTruncation)}`).sort()
      : printer
          .printNode(ts.EmitHint.Unspecified, typeNode(type, declaration, typeNodeFlags | ts.NodeBuilderFlags.InTypeAlias), declaration.getSourceFile())
          .split("\n");
    return [`// ${symbol.name} resolves to:`, ...lines.map((line) => `//   ${line}`)].join("\n");
  }

  return {
    render(symbol: ts.Symbol): Rendered {
      const cached = cache.get(symbol);
      if (cached) {
        return cached;
      }
      const parts: string[] = [];
      const nodes: ts.Node[] = [];
      for (const declaration of declarationsOf(symbol)) {
        const node = normalize(declaration);
        if (node) {
          nodes.push(node);
          parts.push(printer.printNode(ts.EmitHint.Unspecified, node, declaration.getSourceFile()));
        } else {
          const type = checker.getTypeOfSymbolAtLocation(symbol, declaration);
          parts.push(`${kindOf(declaration)} ${symbol.name}: ${checker.typeToString(type, declaration, ts.TypeFormatFlags.NoTruncation)};`);
        }
        const resolved = resolvedForm(symbol, declaration);
        if (resolved) {
          parts.push(resolved);
        }
      }
      const rendered = { text: parts.join("\n"), nodes };
      cache.set(symbol, rendered);
      return rendered;
    },

    /** Top-level package declarations a normalized node names (bodies and initializers are already gone). */
    references(node: ts.Node): ts.Symbol[] {
      const found: ts.Symbol[] = [];
      const visit = (child: ts.Node): void => {
        if (ts.isIdentifier(child)) {
          const symbol = child.pos >= 0 ? resolveAlias(checker, checker.getSymbolAtLocation(child)) : undefined;
          if (symbol && isPackageTopLevel(symbol)) {
            found.push(symbol);
          }
        } else {
          ts.forEachChild(child, visit);
        }
      };
      visit(node);
      return found;
    },
  };
}

function entryPointsOf(pkg: PackageJson): EntryPoint[] {
  return Object.entries(pkg.exports).flatMap(([subpath, target]) => {
    if (typeof target === "string") {
      return []; // a non-code subpath such as ./package.json; listed with the entry fields
    }
    const entry = target.types && /^\.\/dist\/(.+)\.d\.ts$/.exec(target.types)?.[1];
    if (!entry) {
      throw new Error(`package.json exports["${subpath}"] needs a "types" condition of the form ./dist/<entry>.d.ts`);
    }
    const source = `src/${entry}.ts`;
    if (!fs.existsSync(path.join(packageRoot, source))) {
      throw new Error(`package.json exports["${subpath}"] maps to ${source}, which does not exist`);
    }
    return [{ subpath, source }];
  });
}

function compilerOptions(): ts.CompilerOptions {
  const { config, error } = ts.readConfigFile(path.join(packageRoot, "tsconfig.json"), ts.sys.readFile);
  if (error) {
    throw new Error(ts.flattenDiagnosticMessageText(error.messageText, "\n"));
  }
  return ts.parseJsonConfigFileContent(config, ts.sys, packageRoot).options;
}

function resolveAlias(checker: ts.TypeChecker, symbol: ts.Symbol): ts.Symbol;
function resolveAlias(checker: ts.TypeChecker, symbol: ts.Symbol | undefined): ts.Symbol | undefined;
function resolveAlias(checker: ts.TypeChecker, symbol: ts.Symbol | undefined): ts.Symbol | undefined {
  return symbol && symbol.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(symbol) : symbol;
}

/** Source order within a file; files by name, so merged declarations print in a stable order. */
function declarationsOf(symbol: ts.Symbol): ts.Declaration[] {
  return [...(symbol.declarations ?? [])].sort(
    (left, right) => compare(left.getSourceFile().fileName, right.getSourceFile().fileName) || left.pos - right.pos,
  );
}

function isPackageTopLevel(symbol: ts.Symbol): boolean {
  return (symbol.declarations ?? []).some((declaration) => {
    if (!toPosix(declaration.getSourceFile().fileName).startsWith(sourcePrefix)) {
      return false;
    }
    const statement = ts.isVariableDeclaration(declaration) ? declaration.parent.parent : declaration;
    return ts.isSourceFile(statement.parent);
  });
}

/**
 * Whether the alias itself is computed. Object and function type literals are
 * not searched: their members already print as written (`fetch?: typeof fetch`
 * is a member shape, not a computed alias).
 */
function isComputedType(node: ts.TypeNode): boolean {
  if (ts.isParenthesizedTypeNode(node)) {
    return isComputedType(node.type);
  }
  if (ts.isUnionTypeNode(node) || ts.isIntersectionTypeNode(node)) {
    return node.types.some(isComputedType);
  }
  if (ts.isTypeReferenceNode(node)) {
    return node.typeArguments?.some(isComputedType) ?? false;
  }
  if (ts.isArrayTypeNode(node)) {
    return isComputedType(node.elementType);
  }
  return (
    ts.isTypeQueryNode(node) ||
    ts.isIndexedAccessTypeNode(node) ||
    ts.isMappedTypeNode(node) ||
    ts.isConditionalTypeNode(node) ||
    (ts.isTypeOperatorNode(node) && node.operator === ts.SyntaxKind.KeyOfKeyword)
  );
}

function modifiersOf(node: ts.HasModifiers): ts.Modifier[] | undefined {
  const kept = ts.getModifiers(node)?.filter((modifier) => !droppedModifiers.has(modifier.kind));
  return kept && kept.length > 0 ? kept : undefined;
}

function kindOf(declaration: ts.Declaration): string {
  if (ts.isVariableDeclaration(declaration)) {
    const flags = declaration.parent.flags;
    return flags & ts.NodeFlags.Const ? "const" : flags & ts.NodeFlags.Let ? "let" : "var";
  }
  if (ts.isFunctionDeclaration(declaration)) {
    return "function";
  }
  if (ts.isClassDeclaration(declaration)) {
    return "class";
  }
  if (ts.isInterfaceDeclaration(declaration)) {
    return "interface";
  }
  if (ts.isTypeAliasDeclaration(declaration)) {
    return "type";
  }
  if (ts.isEnumDeclaration(declaration)) {
    return "enum";
  }
  if (ts.isModuleDeclaration(declaration)) {
    return "namespace";
  }
  return ts.SyntaxKind[declaration.kind];
}

function valueKindOf(value: unknown): string {
  return Array.isArray(value) ? "array" : value === null ? "null" : typeof value;
}

function addKind(target: Map<string, Set<string>>, name: string, kind: string): void {
  const kinds = target.get(name) ?? new Set<string>();
  kinds.add(kind);
  target.set(name, kinds);
}

function kindList(title: string, byName: Map<string, Set<string>>): string[] {
  const names = [...byName.keys()].sort(compare);
  return [`${title} (${names.length}):`, ...names.map((name) => `  ${name}: ${[...byName.get(name)!].sort(compare).join(", ")}`)];
}

function addVariant(target: Variants, name: string, text: string, subpath: string): void {
  const variants = target.get(name) ?? new Map<string, string[]>();
  variants.set(text, [...(variants.get(text) ?? []), subpath]);
  target.set(name, variants);
}

/** One block per name; a name whose shape differs between entry points prints once per variant. */
function renderVariants(byName: Variants): string {
  return [...byName.keys()]
    .sort(compare)
    .flatMap((name) => {
      const variants = [...byName.get(name)!];
      return variants.length === 1
        ? variants.map(([text]) => text)
        : variants.map(([text, subpaths]) => `// as exported by ${subpaths.map((subpath) => JSON.stringify(subpath)).join(", ")}\n${text}`);
    })
    .join("\n\n");
}

/**
 * Deterministic value printer. Key order is insertion order (source order for
 * these constants, and meaningful to anyone iterating them); arrays nested in
 * a container print on one line, like the literals in contract.ts.
 */
function formatValue(value: unknown, indent = "", ancestors: object[] = []): string {
  if (typeof value === "string") {
    return JSON.stringify(value);
  }
  if (typeof value === "function") {
    return `[function ${value.name}]`;
  }
  if (typeof value === "bigint") {
    return `${value}n`;
  }
  if (typeof value !== "object" || value === null) {
    return String(value);
  }
  if (ancestors.includes(value)) {
    return "[circular]";
  }
  const inner = `${indent}  `;
  const nested = [...ancestors, value];
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return "[]";
    }
    if (indent !== "" && value.every((item) => item === null || (typeof item !== "object" && typeof item !== "function"))) {
      return `[${value.map((item) => formatValue(item)).join(", ")}]`;
    }
    return `[\n${value.map((item) => inner + formatValue(item, inner, nested)).join(",\n")}\n${indent}]`;
  }
  const entries = Object.entries(value);
  if (entries.length === 0) {
    return "{}";
  }
  return `{\n${entries.map(([key, item]) => `${inner}${JSON.stringify(key)}: ${formatValue(item, inner, nested)}`).join(",\n")}\n${indent}}`;
}

function pick<T extends object, K extends keyof T>(source: T, keys: readonly K[]): Partial<Pick<T, K>> {
  return Object.fromEntries(keys.filter((key) => source[key] !== undefined).map((key) => [key, source[key]])) as Partial<Pick<T, K>>;
}

/** Code-unit order: identical on every machine, unlike localeCompare. */
function compare(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function toPosix(fileName: string): string {
  return fileName.split(path.sep).join("/");
}
