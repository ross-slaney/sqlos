#!/usr/bin/env python3
"""Port the markdown paper to an IEEEtran LaTeX file, following paper/paper-build-process.md.

Steps: split title/author/abstract/body/references; convert Unicode math to LaTeX math;
run pandoc on the body; turn pandoc's longtables into booktabs tabulars inside table floats;
replace image embeds with full-width figures and chart markers with pgfplots figures; emit
thebibliography; assemble with the first paper's preamble.
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


def big_o_to_math(s: str) -> str:
    def conv(m):
        inner = m.group(1)
        inner = inner.replace(" · ", r" \cdot ").replace("·", r"\cdot")
        inner = inner.replace("σ", r"\sigma").replace("G_max", r"G_{\max}")
        inner = re.sub(r"\blog\b", r"\\log", inner)
        return "$O(" + inner + ")$"
    return re.sub(r"O\(([^()]*(?:\([^()]*\)[^()]*)*)\)", conv, s)


def prose_math(s: str) -> str:
    s = big_o_to_math(s)
    s = re.sub(r"√\(([^)]*)\)", lambda m: "$\\sqrt{" + m.group(1).replace(" · ", r" \cdot ").replace("σ", r"\sigma") + "}$", s)
    s = s.replace("Σ_r", r"$\sum_r$").replace("Σ", r"$\sum$")
    s = s.replace("G_max", r"$G_{\max}$")
    s = s.replace(" · ", r" $\cdot$ ").replace("·", r"$\cdot$")
    s = s.replace("σ", r"$\sigma$").replace("ℓ", r"$\ell$")
    s = s.replace("≤", r"$\leq$").replace("≥", r"$\geq$").replace("≈", r"$\approx$")
    s = s.replace("→", r"$\rightarrow$").replace("∃", r"$\exists$").replace("∈", r"$\in$").replace("⋃", r"$\bigcup$")
    s = s.replace("×", r"$\times$").replace("−", "-").replace("–", "--").replace("—", "---")
    s = s.replace("′", "'").replace("…", "...").replace("□", r"$\square$").replace("§", "Section ")
    s = s.replace("≠", r"$\neq$")
    return s


def code_ascii(s: str) -> str:
    for a, b in [("←", "<-"), ("∃", "exists"), ("∈", "in"), ("⋃", "UNION"), ("≥", ">="), ("≤", "<="),
                 ("·", "*"), ("σ", "sigma"), ("—", "-"), ("–", "-"), ("′", "'"), ("…", "..."), ("→", "->")]:
        s = s.replace(a, b)
    return s


converted = []
in_code = False
table2_fixed = False
for l in body_lines:
    if l.startswith("```"):
        in_code = not in_code
        converted.append("```")  # drop language tags so pandoc emits plain verbatim
        continue
    if in_code:
        converted.append(code_ascii(l))
        continue
    if l.strip() == "---":
        continue
    m = re.match(r"^(#{2,3}) \d+(?:\.\d+)*\.? (.*)$", l)
    if m:
        l = f"{m.group(1)} {m.group(2)}"
    cm = re.match(r"^\[Chart ([\w-]+), Figure \d+: (.*)\]$", l.strip())
    if cm:
        key = cm.group(1).replace("-", "")
        converted += [f"CHARTPLACEHOLDER{key}", "", f"CHARTCAPTION{key}: {prose_math(cm.group(2))}"]
        continue
    im = re.match(r"^!\[(.*?)\]\(figures/([\w-]+)\.svg\)$", l.strip())
    if im:
        key = im.group(2).replace("-", "")
        caption = re.sub(r"^Figure \d+\.\s*", "", im.group(1))
        converted += [f"IMGPLACEHOLDER{key}", "", f"IMGCAPTION{key}: {prose_math(caption)}"]
        continue
    # Table 2 is the one prose table; give its columns proportional widths for wrapping.
    if l.startswith("| --- | --- | --- | --- |") and not table2_fixed:
        l = "| " + "-" * 30 + " | " + "-" * 24 + " | " + "-" * 26 + " | " + "-" * 20 + " |"
        table2_fixed = True
    converted.append(prose_math(l))

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
tex = tex.replace("\\section{AI Disclosure}", "\\section*{AI Disclosure}")

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
# IEEEtran is two-column, where longtable cannot run, so each becomes a table float holding a
# booktabs tabular, scaled to the column, with the preceding bold "Table N." paragraph as its caption.
longtable = re.compile(
    r"(?:\\textbf\{(Table \d+\.[^\n]*?)\}\n\n)?"
    r"\{\\def\\LTcaptype\{none\} % do not increment counter\n"
    r"\\begin\{longtable\}\[\]\{(.*?@\{\}\})\n"
    r"(.*?)\\endhead\n"
    r"\\bottomrule\\noalign\{\}\n\\endlastfoot\n"
    r"(.*?)\\end\{longtable\}\n\}",
    re.S,
)


def to_float(m):
    caption, spec, header, body = m.groups()
    spec = spec[:-1]  # the captured spec ends with the closing brace of the longtable argument
    header_row = header.split("\\\\")[0]
    columns = header_row.count("&") + 1
    # Every captioned table spans both columns of the page; only the inline callers table stays in the column.
    wide = caption is not None
    width = "\\textwidth" if wide else "\\columnwidth"
    spec = spec.replace("\\columnwidth", width)
    tabular = ["\\scriptsize", "\\resizebox{" + width + "}{!}{%", "\\begin{tabular}{" + spec + "}",
               header.rstrip("\n"), body.rstrip("\n"), "\\bottomrule", "\\end{tabular}}"]
    if caption:
        # IEEEtran prints "TABLE I" itself, so the caption text drops the "Table 1." prefix.
        number = re.search(r"Table (\d+)\.", caption).group(1)
        caption = re.sub(r"^Table \d+\.\s*", "", caption)
        env = "table*" if wide else "table"
        return "\n".join(["\\begin{" + env + "}[!tp]", "\\centering", "\\caption{" + caption + "}",
                          "\\label{tab:" + number + "}"] + tabular + ["\\end{" + env + "}"])
    # A table with no "Table N." paragraph stays in the text flow.
    return "\n".join(["\\begin{center}"] + tabular + ["\\end{center}"])


tex, n_tables = longtable.subn(to_float, tex)
assert "longtable" not in tex, "a table was not converted"
tex = tex.replace("\\begin{verbatim}", "\\begin{lstlisting}[style=pseudo]").replace("\\end{verbatim}", "\\end{lstlisting}")

charts = {
    "rowsread": r"""
\begin{figure}[t]
\centering
\begin{tikzpicture}
\begin{axis}[
    ybar, bar width=5pt, ymode=log, log origin=infty, ymin=10, ymax=3000000,
    symbolic x coords={Admin,Region,Store,100 stores,10k grants,Far end,Scoped},
    xtick=data, x tick label style={rotate=30, anchor=east, font=\scriptsize},
    ylabel={Rows read per page}, width=\columnwidth, height=5.2cm,
    enlarge x limits=0.12, legend style={font=\scriptsize, at={(0.5,1.02)}, anchor=south, legend columns=2},
    every axis label/.style={font=\small}, every tick label/.style={font=\scriptsize},
]
\addplot[fill=red!60!black, draw=red!60!black] coordinates {(Admin,20) (Region,9306) (Store,184205) (100 stores,2233) (10k grants,1452) (Far end,980020) (Scoped,20)};
\addplot[fill=blue!60!black, draw=blue!60!black] coordinates {(Admin,40) (Region,80) (Store,80) (100 stores,4040) (10k grants,20040) (Far end,80) (Scoped,40)};
\legend{Method A (per-row predicate), Method B (this paper)}
\end{axis}
\end{tikzpicture}
\caption{CAPTION}
\label{fig:rows}
\end{figure}
""",
    "sweep": r"""
\begin{figure}[t]
\centering
\begin{tikzpicture}
\begin{loglogaxis}[
    xlabel={Share of the table the caller may see}, ylabel={Rows read per page},
    width=\columnwidth, height=5.2cm, grid=major, grid style={gray!20},
    legend style={font=\scriptsize, at={(0.03,0.03)}, anchor=south west, draw=gray!50},
    every axis label/.style={font=\small}, every tick label/.style={font=\scriptsize},
    xtick={0.0001,0.001,0.01,0.1}, xticklabels={0.01\%,0.1\%,1\%,10\%},
]
\addplot[color=red!60!black, thick, mark=square*] coordinates {(0.0001,193501) (0.001,19481) (0.0045,3825) (0.01,1928) (0.05,367)};
\addplot[color=blue!60!black, thick, mark=*] coordinates {(0.0001,80) (0.001,440) (0.0045,1840) (0.01,4040) (0.05,20040)};
\legend{Method A (per-row predicate), Method B (this paper)}
\end{loglogaxis}
\end{tikzpicture}
\caption{CAPTION}
\label{fig:sweep}
\end{figure}
""",
}

for key, fig in charts.items():
    cap = re.search(r"CHARTCAPTION" + key + r": ([^\n]*)", tex)
    caption = cap.group(1) if cap else ""
    tex = re.sub(r"CHARTCAPTION" + key + r": [^\n]*\n?", "", tex)
    tex = tex.replace("CHARTPLACEHOLDER" + key, fig.replace("CAPTION", caption))

# Image embeds become full-width figures. The PDF rendering of the SVG is preferred; the PNG is the fallback.
for key in re.findall(r"IMGPLACEHOLDER(\w+)", tex):
    name = {"sparseproblem": "sparse-problem", "storage": "storage", "retrieval": "retrieval", "maintenance": "maintenance"}[key]
    ext = "pdf" if (figures_dir / f"{name}.pdf").exists() else "png"
    cap = re.search(r"IMGCAPTION" + key + r": ([^\n]*)", tex)
    caption = cap.group(1) if cap else ""
    tex = re.sub(r"IMGCAPTION" + key + r": [^\n]*\n?", "", tex)
    fig = "\n".join([
        "\\begin{figure*}[!tp]", "\\centering",
        "\\includegraphics[width=\\textwidth]{figures/" + name + "." + ext + "}",
        "\\caption{" + caption + "}", "\\label{fig:" + key + "}", "\\end{figure*}"])
    tex = tex.replace("IMGPLACEHOLDER" + key, fig)


def esc(s: str) -> str:
    return s.replace("\\", r"\textbackslash{}").replace("&", r"\&").replace("#", r"\#").replace("_", r"\_").replace("%", r"\%").replace("$", r"\$")


bib = ["\\begin{thebibliography}{49}"]
for l in ref_lines:
    m = re.match(r"^\[(\d+)\] (.*)$", l)
    if not m:
        continue
    num, body = m.groups()
    urls = re.findall(r"https?://\S+", body)
    body_no_urls = re.sub(r"https?://\S+", "URLTOKEN", body)
    body_esc = esc(body_no_urls)
    for u in urls:
        u = u.rstrip(".,;")
        body_esc = body_esc.replace("URLTOKEN", "\\url{" + u + "}", 1)
    body_esc = body_esc.replace("–", "--").replace("—", "---").replace("′", "'")
    body_esc = re.sub(r"\*([^*]+)\*", r"\\emph{\1}", body_esc)
    bib.append(f"\\bibitem[{num}]{{ref{num}}} {body_esc}")
bib.append("\\end{thebibliography}")

preamble = r"""\documentclass[conference]{IEEEtran}

% ---- Packages ----
\usepackage{iftex}
\ifPDFTeX
  \usepackage[utf8]{inputenc}
\fi
\usepackage[T1]{fontenc}
\usepackage{amsmath,amssymb,amsthm}
\usepackage{algorithmic}
\usepackage{algorithm}
\usepackage{booktabs}
\usepackage{multirow}
\usepackage{array}
\usepackage{calc}
\usepackage{graphicx}
\usepackage{tikz}
\usetikzlibrary{matrix,positioning,fit,backgrounds,calc}
\usepackage{pgfplots}
\pgfplotsset{compat=1.18}
\usepackage{listings}
\usepackage{xcolor}
\usepackage{textcomp}
\usepackage{balance}
\usepackage{url}
\usepackage{cite}
\usepackage{hyperref}
\hypersetup{hidelinks}

% pandoc helpers
\providecommand{\tightlist}{\setlength{\itemsep}{0pt}\setlength{\parskip}{0pt}}

% ---- Float placement: the paper has ten double-column floats, so let pages carry more of them ----
\setcounter{topnumber}{3}
\setcounter{dbltopnumber}{3}
\setcounter{totalnumber}{6}
\renewcommand{\topfraction}{0.95}
\renewcommand{\dbltopfraction}{0.95}
\renewcommand{\textfraction}{0.05}
\renewcommand{\floatpagefraction}{0.6}
\renewcommand{\dblfloatpagefraction}{0.6}

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
  morekeywords={ALGORITHM,LOOP,EXIT,RETURN,FOR,EACH,IF,ELSE,WHERE,EXISTS}
}

\begin{document}
"""

tex_title = title.replace("&", "\\&")

abstract_tex = subprocess.run(
    ["pandoc", "-f", "markdown+smart", "-t", "latex", "--wrap=none"],
    input=prose_math(abstract), capture_output=True, text=True, check=True,
).stdout.strip()

doc = (
    preamble
    + "\\title{" + tex_title + "}\n"
    + "\\author{\\IEEEauthorblockN{" + author + "}}\n"
    + "\\maketitle\n\n"
    + "\\begin{abstract}\n" + abstract_tex + "\n\\end{abstract}\n\n"
    + tex
    + "\n\\clearpage\n\\balance\n\n"
    + "\n".join(bib)
    + "\n\n\\end{document}\n"
)
out.write_text(doc, encoding="utf-8")

# Sanity checks: environment balance and stray non-ASCII outside listings.
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
print("tables:", doc.count("\\begin{table}"), "figures:", doc.count("\\begin{figure}"), "wide figures:", doc.count("\\begin{figure*}"), "bibitems:", doc.count("\\bibitem"))
print("leftover markers:", re.findall(r"(?:IMG|CHART)\w*PLACEHOLDER\w*|(?:IMG|CHART)CAPTION\w*|LTcaptype|longtable", doc) or "none")
print("sections:", re.findall(r"\\section\{([^}]*)\}", doc))
print("lines:", doc.count("\n"))
