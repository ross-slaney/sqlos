#!/usr/bin/env python3
"""Generate the IEEEtran LaTeX file from the markdown manuscript.

Usage: build-tex.py paper.md paper.tex

The markdown is the source. Its conventions:
  - line 1 is "# Title", line 3 the author, then a "**Abstract.**" paragraph;
  - the body runs from "## 1. Introduction" to "## References";
  - headings are numbered ("## 3. Title", "### 3.2 Title") and cross-referenced as "Section 3.2";
  - math is written in $...$ and $$...$$;
  - citations are [@key], and the reference list is "[@key] text" lines; numbers follow first citation;
  - a table is a "**Table N. Caption.**" paragraph followed by a pipe table, referenced as "Table N";
  - an image is "![Figure N. Caption](figures/name.svg)" and a chart "[Chart key, Figure N: Caption]",
    referenced as "Fig. N";
  - a code block preceded by "<!-- algorithm: Caption -->" becomes Algorithm 1.

Pandoc converts the body; this script then turns its longtables into booktabs floats, inserts the
figures, the charts and the algorithm, resolves cross-references and writes the bibliography.
"""
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

src = Path(sys.argv[1])
out = Path(sys.argv[2])
work = out.parent / "tex-work"
work.mkdir(exist_ok=True)
figures_dir = out.parent / "figures"

text = src.read_text(encoding="utf-8")
lines = text.split("\n")

title = lines[0].lstrip("# ").strip()
author = lines[2].strip()
abstract_line = next(l for l in lines if l.startswith("**Abstract.**"))
abstract = abstract_line[len("**Abstract.**"):].strip()

start = next(i for i, l in enumerate(lines) if l.startswith("## 1. Introduction"))
ref_start = next(i for i, l in enumerate(lines) if l.strip() == "## References")
body_lines = lines[start:ref_start]
ref_lines = [l for l in lines[ref_start + 1:] if l.strip()]

# ---- Citations: numbered in order of first citation in the body ----
order = []
for m in re.finditer(r"\[@([\w-]+)\]", "\n".join(body_lines)):
    if m.group(1) not in order:
        order.append(m.group(1))
refs = {}
for l in ref_lines:
    m = re.match(r"^\[@([\w-]+)\] (.*)$", l)
    if m:
        refs[m.group(1)] = m.group(2)
missing = [k for k in order if k not in refs]
assert not missing, f"cited but not in the reference list: {missing}"
unused = [k for k in refs if k not in order]
number = {k: i + 1 for i, k in enumerate(order)}


def cite(s: str) -> str:
    return re.sub(r"\[@([\w-]+)\]", lambda m: "\\cite{ref%d}" % number[m.group(1)], s)


def plain(s: str) -> str:
    """Dashes and similar punctuation in ASCII form; pandoc's smart extension restores them."""
    return s.replace("\u2013", "--").replace("\u2014", "---").replace("\u2026", "...").replace("\u2032", "'")


def code_ascii(s: str) -> str:
    for a, b in [("\u2190", "<-"), ("\u2264", "<="), ("\u2265", ">="), ("\u2014", "-"), ("\u2013", "-"),
                 ("\u2026", "..."), ("\u2192", "->")]:
        s = s.replace(a, b)
    return s


# ---- Body preprocessing ----
converted = []
in_code = False
algorithm = None          # (caption, lines) of the algorithm block
pending_algorithm = None  # caption waiting for its code block
in_algorithm = False
figure_numbers = {}       # placeholder key -> markdown figure number
table1_fixed = False
for l in body_lines:
    if l.startswith("```"):
        if in_algorithm:
            in_algorithm = False
            continue
        if pending_algorithm is not None and not in_code:
            algorithm = (pending_algorithm, [])
            pending_algorithm = None
            in_algorithm = True
            converted.append("ALGPLACEHOLDER")
            continue
        in_code = not in_code
        converted.append("```")  # drop language tags so pandoc emits plain verbatim
        continue
    if in_algorithm:
        algorithm[1].append(code_ascii(l))
        continue
    if in_code:
        converted.append(code_ascii(l))
        continue
    if l.strip() == "---":
        continue
    am = re.match(r"^<!-- algorithm: (.*) -->$", l.strip())
    if am:
        pending_algorithm = am.group(1)
        continue
    m = re.match(r"^(#{2,3}) (\d+(?:\.\d+)*)\.? (.*)$", l)
    if m:
        label = "sec-" + m.group(2).replace(".", "-")
        converted.append(f"{m.group(1)} {m.group(3)} {{#{label}}}")
        continue
    cm = re.match(r"^\[Chart ([\w-]+), Figure (\d+): (.*)\]$", l.strip())
    if cm:
        key = cm.group(1).replace("-", "")
        figure_numbers[key] = cm.group(2)
        converted += [f"CHARTPLACEHOLDER{key}", "", f"CHARTCAPTION{key}: {cite(plain(cm.group(3)))}"]
        continue
    im = re.match(r"^!\[Figure (\d+)\.\s*(.*?)\]\(figures/([\w-]+)\.svg\)$", l.strip())
    if im:
        key = im.group(3).replace("-", "")
        figure_numbers[key] = im.group(1)
        converted += [f"IMGPLACEHOLDER{key}", "", f"IMGCAPTION{key}: {cite(plain(im.group(2)))}"]
        continue
    # The Azure/SHRBAC comparison is the one prose table; give its columns proportional widths so pandoc wraps them.
    if l.strip() == "| --- | --- | --- |" and not table1_fixed and "| Concept | Azure RBAC | SHRBAC |" in converted[-1]:
        l = "| " + "-" * 16 + " | " + "-" * 42 + " | " + "-" * 42 + " |"
        table1_fixed = True
    converted.append(cite(plain(l)))

md_body = "\n".join(converted)
(work / "body.md").write_text(md_body, encoding="utf-8")

res = subprocess.run(
    ["pandoc", str(work / "body.md"), "-f", "markdown+smart", "-t", "latex", "--wrap=none", "--columns=150",
     "--shift-heading-level-by=-1"],
    capture_output=True, text=True, check=True,
)
tex = res.stdout
(work / "raw.tex").write_text(tex, encoding="utf-8")
# pandoc 3 computes column widths from \linewidth; use \columnwidth so wide tables can switch to \textwidth.
tex = tex.replace("(\\linewidth", "(\\columnwidth")
tex = re.sub(r"\\section\{AI Disclosure\}(\\label\{[^}]*\})?", r"\\section*{AI Disclosure}", tex)

# pandoc 3.9 emits every table as:
#   {\def\LTcaptype{none} % do not increment counter
#   \begin{longtable}[]{SPEC}
#   \toprule\noalign{} HEADER \\ \midrule\noalign{}
#   \endhead
#   \bottomrule\noalign{}
#   \endlastfoot
#   BODY
#   \end{longtable}
#   }
# IEEEtran is two-column, where longtable cannot run, so each becomes a table float holding a booktabs
# tabular, reduced to the available width only when it is wider, with the preceding bold "Table N."
# paragraph as its caption.
longtable = re.compile(
    r"(?:\\textbf\{(Table \d+\.[^\n]*?)\}\n\n)?"
    r"\{\\def\\LTcaptype\{none\} % do not increment counter\n"
    r"\\begin\{longtable\}\[\]\{(.*?@\{\}\})\n"
    r"(.*?)\\endhead\n"
    r"\\bottomrule\\noalign\{\}\n\\endlastfoot\n"
    r"(.*?)\\end\{longtable\}\n\}",
    re.S,
)
SINGLE_COLUMN_TABLES = {"1", "8", "9"}


def to_float(m):
    caption, spec, header, body = m.groups()
    spec = spec[:-1]  # the captured spec ends with the closing brace of the longtable argument
    number_ = re.search(r"Table (\d+)\.", caption).group(1) if caption else None
    wide = caption is not None and number_ not in SINGLE_COLUMN_TABLES
    width = "\\textwidth" if wide else "\\columnwidth"
    spec = spec.replace("\\columnwidth", width)
    tabular = ["\\scriptsize", "\\begin{adjustbox}{max width=" + width + "}", "\\begin{tabular}{" + spec + "}",
               header.rstrip("\n"), body.rstrip("\n"), "\\bottomrule", "\\end{tabular}", "\\end{adjustbox}"]
    if caption:
        # IEEEtran prints "TABLE I" itself, so the caption text drops the "Table 1." prefix.
        caption = re.sub(r"^Table \d+\.\s*", "", caption)
        env = "table*" if wide else "table"
        return "\n".join(["\\begin{" + env + "}[!tp]", "\\centering", "\\caption{" + caption + "}",
                          "\\label{tab:" + number_ + "}"] + tabular + ["\\end{" + env + "}"])
    return "\n".join(["\\begin{center}"] + tabular + ["\\end{center}"])


tex, n_tables = longtable.subn(to_float, tex)
assert "longtable" not in tex, "a table was not converted"
tex = tex.replace("\\begin{verbatim}", "\\begin{lstlisting}[style=pseudo]").replace("\\end{verbatim}", "\\end{lstlisting}")

# ---- Algorithm 1 ----
if algorithm:
    caption, code = algorithm
    block = "\n".join(["\\begin{algorithm}[!t]", "\\caption{" + caption + "}", "\\label{alg:1}",
                       "\\begin{lstlisting}[style=pseudo]"] + code + ["\\end{lstlisting}", "\\end{algorithm}"])
    tex = tex.replace("ALGPLACEHOLDER", block)

# ---- Charts (pgfplots), data from Tables 4, 5 and 6 ----
SERIES_STYLE = {
    "A": "fill=red!55!black, draw=red!55!black",
    "B": "fill=blue!60!black, draw=blue!60!black",
    "C": "fill=gray!55, draw=gray!70!black",
}
LEGEND = r"\legend{A: checking rows in order, B: projections, C: closure enumeration}"
charts = {
    "rowsread": r"""
\begin{figure}[!t]
\centering
\begin{tikzpicture}
\begin{axis}[
    ybar, bar width=3.2pt, ymode=log, log origin=infty, ymin=10, ymax=5000000,
    symbolic x coords={Administrator,Region manager,Store manager,100 stores,10k products,Far end,Scoped},
    xtick=data, x tick label style={rotate=30, anchor=east, font=\scriptsize},
    ylabel={Rows read per page}, width=0.97\columnwidth, height=5.4cm,
    enlarge x limits=0.1, legend style={font=\scriptsize, at={(0.5,1.03)}, anchor=south, legend columns=2},
    every axis label/.style={font=\small}, every tick label/.style={font=\scriptsize},
]
\addplot[STYLEA] coordinates {(Administrator,20) (Region manager,9306) (Store manager,184205) (100 stores,2233) (10k products,1452) (Far end,980020) (Scoped,20)};
\addplot[STYLEB] coordinates {(Administrator,40) (Region manager,80) (Store manager,80) (100 stores,4040) (10k products,20040) (Far end,80) (Scoped,40)};
\addplot[STYLEC] coordinates {(Administrator,40) (Region manager,13263) (Store manager,742) (100 stores,8549) (10k products,8544) (Far end,1054147) (Scoped,40)};
LEGEND
\end{axis}
\end{tikzpicture}
\caption{CAPTION}
\label{LABEL}
\end{figure}
""",
    "sweep": r"""
\begin{figure}[!t]
\centering
\begin{tikzpicture}
\begin{loglogaxis}[
    xlabel={Share of the table authorized for the caller}, ylabel={Rows read per page},
    width=0.92\columnwidth, height=5.4cm, grid=major, grid style={gray!20},
    ymin=30, ymax=600000,
    legend style={font=\scriptsize, at={(0.5,1.03)}, anchor=south, legend columns=2},
    every axis label/.style={font=\small}, every tick label/.style={font=\scriptsize},
    xtick={0.0001,0.001,0.01,0.1}, xticklabels={0.01\%,0.1\%,1\%,10\%}, xmax=0.1,
]
\addplot[color=red!55!black, thick, mark=square*] coordinates {(0.0001,193501) (0.001,19481) (0.0045,3825) (0.01,1928) (0.05,367)};
\addplot[color=blue!60!black, thick, mark=*] coordinates {(0.0001,80) (0.001,440) (0.0045,1840) (0.01,4040) (0.05,20040)};
\addplot[color=gray!70!black, thick, dashed, mark=triangle*] coordinates {(0.0001,742) (0.001,4142) (0.0045,8549) (0.01,8549) (0.05,2142)};
LEGEND
\end{loglogaxis}
\end{tikzpicture}
\caption{CAPTION}
\label{LABEL}
\end{figure}
""",
}

for key, fig in charts.items():
    cap = re.search(r"CHARTCAPTION" + key + r": ([^\n]*)", tex)
    caption = cap.group(1) if cap else ""
    tex = re.sub(r"CHARTCAPTION" + key + r": [^\n]*\n?", "", tex)
    fig = fig.replace("STYLEA", SERIES_STYLE["A"]).replace("STYLEB", SERIES_STYLE["B"]).replace("STYLEC", SERIES_STYLE["C"])
    fig = fig.replace("LEGEND", LEGEND).replace("CAPTION", caption).replace("LABEL", "fig:" + figure_numbers[key])
    tex = tex.replace("CHARTPLACEHOLDER" + key, fig)

# ---- Diagrams: full-width figures from the PDF rendering of each SVG ----
for key in re.findall(r"IMGPLACEHOLDER(\w+)", tex):
    name = {"sparseproblem": "sparse-problem", "storage": "storage", "retrieval": "retrieval", "maintenance": "maintenance"}[key]
    ext = "pdf" if (figures_dir / f"{name}.pdf").exists() else "png"
    cap = re.search(r"IMGCAPTION" + key + r": ([^\n]*)", tex)
    caption = cap.group(1) if cap else ""
    tex = re.sub(r"IMGCAPTION" + key + r": [^\n]*\n?", "", tex)
    fig = "\n".join([
        "\\begin{figure*}[!tp]", "\\centering",
        "\\includegraphics[width=\\textwidth]{figures/" + name + "." + ext + "}",
        "\\caption{" + caption + "}", "\\label{fig:" + figure_numbers[key] + "}", "\\end{figure*}"])
    tex = tex.replace("IMGPLACEHOLDER" + key, fig)

# ---- Cross-references ----
labels = set(re.findall(r"\\label\{([^}]*)\}", tex))


def ref(label: str) -> str:
    assert label in labels, f"no label {label}"
    return "\\ref{" + label + "}"


tex = re.sub(r"Section (\d+(?:\.\d+)?)", lambda m: "Section~" + ref("sec-" + m.group(1).replace(".", "-")), tex)
tex = re.sub(r"Tables (\d+) and (\d+)", lambda m: "Tables~" + ref("tab:" + m.group(1)) + " and~" + ref("tab:" + m.group(2)), tex)
tex = re.sub(r"Table (\d+)", lambda m: "Table~" + ref("tab:" + m.group(1)), tex)
tex = re.sub(r"Fig\.(?:~| |\\ )(\d+)", lambda m: "Fig.~" + ref("fig:" + m.group(1)), tex)
tex = re.sub(r"Algorithm (\d+)", lambda m: "Algorithm~" + ref("alg:" + m.group(1)), tex)


# ---- Bibliography ----
def esc(s: str) -> str:
    return s.replace("\\", r"\textbackslash{}").replace("&", r"\&").replace("#", r"\#").replace("_", r"\_").replace("%", r"\%").replace("$", r"\$")


bib = ["\\begin{thebibliography}{" + str(len(order)) + "}"]
for key in order:
    body = refs[key]
    urls = re.findall(r"https?://\S+", body)
    body_no_urls = re.sub(r"https?://\S+", "URLTOKEN", body)
    body_esc = esc(body_no_urls)
    for u in urls:
        u = u.rstrip(".,;")
        body_esc = body_esc.replace("URLTOKEN", "\\url{" + u + "}", 1)
    body_esc = plain(body_esc)
    body_esc = re.sub(r"\*([^*]+)\*", r"\\emph{\1}", body_esc)
    body_esc = re.sub(r'"([^"]*)"', r"``\1''", body_esc)
    bib.append(f"\\bibitem{{ref{number[key]}}} {body_esc}")
bib.append("\\end{thebibliography}")

preamble = r"""\documentclass[conference]{IEEEtran}

% ---- Packages ----
\usepackage{iftex}
\ifPDFTeX
  \usepackage[utf8]{inputenc}
\fi
\usepackage[T1]{fontenc}
\usepackage{amsmath,amssymb,amsthm}
\usepackage{algorithm}
\usepackage{booktabs}
\usepackage{array}
\usepackage{calc}
\usepackage{adjustbox}
\usepackage{graphicx}
\usepackage{tikz}
\usepackage{pgfplots}
\pgfplotsset{compat=1.18}
\usepackage{listings}
\usepackage[varqu]{zi4}
\usepackage{xcolor}
\usepackage{textcomp}
\usepackage{balance}
\usepackage{url}
\usepackage{cite}
\usepackage{hyperref}
\hypersetup{hidelinks}

% pandoc helpers
\providecommand{\tightlist}{\setlength{\itemsep}{0pt}\setlength{\parskip}{0pt}}

% ---- Float placement: the paper has many double-column floats, so let pages carry more of them ----
\setcounter{topnumber}{3}
\setcounter{dbltopnumber}{3}
\setcounter{totalnumber}{6}
\renewcommand{\topfraction}{0.95}
\renewcommand{\dbltopfraction}{0.95}
\renewcommand{\textfraction}{0.05}
\renewcommand{\floatpagefraction}{0.8}
\renewcommand{\dblfloatpagefraction}{0.8}
\makeatletter
\setlength{\@fptop}{0pt}
\setlength{\@fpsep}{14pt}
\setlength{\@fpbot}{0pt plus 1fil}
\setlength{\@dblfptop}{0pt}
\setlength{\@dblfpsep}{14pt}
\setlength{\@dblfpbot}{0pt plus 1fil}
\makeatother

% ---- Pseudocode and code listing style ----
\lstdefinestyle{pseudo}{
  basicstyle=\ttfamily\scriptsize,
  keywordstyle=\bfseries,
  showstringspaces=false,
  breaklines=true,
  breakatwhitespace=true,
  frame=single,
  framesep=2pt,
  xleftmargin=4pt,
  xrightmargin=4pt,
  aboveskip=6pt,
  belowskip=4pt,
  columns=fullflexible,
  morekeywords={ALGORITHM,LOOP,EXIT,RETURN,FOR,EACH,IN,IF,WHERE,EXISTS}
}

% Columns end where their text ends; stretching them left gaps inside columns next to wide floats.
\raggedbottom

\begin{document}
"""

tex_title = title.replace("&", "\\&")

abstract_tex = subprocess.run(
    ["pandoc", "-f", "markdown+smart", "-t", "latex", "--wrap=none"],
    input=plain(abstract), capture_output=True, text=True, check=True,
).stdout.strip()

doc = (
    preamble
    + "\\title{" + tex_title + "}\n"
    + "\\author{\\IEEEauthorblockN{" + author + "}}\n"
    + "\\maketitle\n\n"
    + "\\begin{abstract}\n" + abstract_tex + "\n\\end{abstract}\n\n"
    + tex
    + "\n\\balance\n\n"
    + "\n".join(bib)
    + "\n\n\\end{document}\n"
)
out.write_text(doc, encoding="utf-8")

# ---- Sanity checks ----
envs = re.findall(r"\\begin\{(\w+\*?)\}", doc)
ends = re.findall(r"\\end\{(\w+\*?)\}", doc)
cb, ce = Counter(envs), Counter(ends)
bad = {k: (cb[k], ce[k]) for k in set(cb) | set(ce) if cb[k] != ce[k]}
print("environments unbalanced:", bad or "none")
in_lst = False
stray = []
for i, l in enumerate(doc.split("\n"), 1):
    if "\\begin{lstlisting}" in l:
        in_lst = True
    if "\\end{lstlisting}" in l:
        in_lst = False
        continue
    if not in_lst and any(ord(c) > 127 for c in l):
        stray.append((i, "".join(c for c in l if ord(c) > 127)))
print("non-ASCII outside listings:", stray[:20] or "none")
print("tables:", doc.count("\\begin{table}") + doc.count("\\begin{table*}"), "figures:", doc.count("\\begin{figure}"),
      "wide figures:", doc.count("\\begin{figure*}"), "algorithms:", doc.count("\\begin{algorithm}"),
      "bibitems:", doc.count("\\bibitem"))
print("references in the list but never cited:", unused or "none")
print("leftover markers:", re.findall(r"(?:IMG|CHART|ALG)\w*PLACEHOLDER\w*|(?:IMG|CHART)CAPTION\w*|LTcaptype|longtable|\[@", doc) or "none")
print("sections:", re.findall(r"\\section\*?\{([^}]*)\}", doc))
print("lines:", doc.count("\n"))
