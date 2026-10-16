"""Write the four diagrams of the paper as SVG (rendered to PDF and PNG with rsvg-convert)."""
from pathlib import Path

OUT = Path(__file__).parent / "figures"
FONT = 'font-family="Helvetica, Arial, sans-serif"'
INK, GRAY, LINE = "#111827", "#6b7280", "#374151"
BLUE_FILL, BLUE = "#dbeafe", "#1d4ed8"
RED = "#b91c1c"
GREEN_FILL, GREEN = "#dcfce7", "#15803d"
SHADE = "#d1d5db"
ARROW = ('<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
         'orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#374151"/></marker></defs>')


def svg(w, h, body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" {FONT}>\n'
            f'{ARROW}\n<rect width="{w}" height="{h}" fill="#ffffff"/>\n' + "\n".join(body) + "\n</svg>\n")


def text(x, y, s, size=15, fill=INK, anchor="start", weight="normal", style="normal"):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" '
            f'font-weight="{weight}" font-style="{style}">{s}</text>')


def rect(x, y, w, h, fill="#ffffff", stroke=LINE, sw=1.2, rx=0, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{rx}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"{d}/>'


def line(x1, y1, x2, y2, stroke=LINE, sw=1.3, arrow=False, dash=None):
    a = ' marker-end="url(#arrow)"' if arrow else ""
    d = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{sw}"{a}{d}/>'


def node(cx, y, label, w=96, h=28, fill="#f3f4f6", stroke=LINE, sw=1.2, size=14, dash=None):
    return [rect(cx - w / 2, y, w, h, fill=fill, stroke=stroke, sw=sw, rx=6, dash=dash), text(cx, y + h / 2 + 5, label, size=size, anchor="middle")]


def table(x, y, widths, header, rows, row_h=24, size=14, fills=None, colors=None):
    """A small table; fills/colors give per-row background and text color."""
    out = [rect(x, y, sum(widths), row_h, fill="#e5e7eb", stroke="#9ca3af", sw=1)]
    cx = x
    for wdt, hd in zip(widths, header):
        out.append(text(cx + 8, y + row_h - 7, hd, size=size, weight="bold"))
        cx += wdt
    for i, row in enumerate(rows):
        ry = y + row_h * (i + 1)
        fill = (fills[i] if fills else None) or "#ffffff"
        color = (colors[i] if colors else None) or INK
        out.append(rect(x, ry, sum(widths), row_h, fill=fill, stroke="#d1d5db", sw=1))
        cx = x
        for wdt, cell in zip(widths, row):
            out.append(text(cx + 8, ry + row_h - 7, cell, size=size, fill=color))
            cx += wdt
    return out


# ---------------------------------------------------------------- Figure 1
def fig_sparse():
    body = []
    x0, width = 90, 820

    def bar(y, label_letter, label):
        body.append(text(20, y - 10, label_letter, size=16, weight="bold"))
        body.append(text(52, y - 10, label, size=15))
        body.append(text(x0 - 8, y + 23, "1", size=14, fill=GRAY, anchor="end"))
        body.append(text(x0 + width + 8, y + 23, "1,000,000", size=14, fill=GRAY))

    # (a) 100 authorized rows spread through the order; each mark stands for 4 rows.
    ya = 40
    bar(ya, "(a)", "100 authorized rows spread through the order")
    examined_a = width * 184205 / 1_000_000
    body.append(rect(x0, ya, examined_a, 34, fill=SHADE, stroke="none", sw=0))
    body.append(rect(x0, ya, width, 34, fill="none", stroke=LINE, sw=1.2))
    for k in range(1, 26):
        x = x0 + width * (k - 0.4) / 25
        body.append(line(x, ya + 4, x, ya + 30, stroke=RED, sw=2.4))
    body.append(f'<path d="M{x0},{ya + 42} L{x0},{ya + 48} L{x0 + examined_a:.1f},{ya + 48} L{x0 + examined_a:.1f},{ya + 42}" fill="none" stroke="{INK}" stroke-width="1.2"/>')
    body.append(text(x0 + examined_a / 2, ya + 66, "R = 184,205", size=15, anchor="middle"))

    # (b) 20,000 authorized rows at the end of the order.
    yb = 150
    bar(yb, "(b)", "20,000 authorized rows at the end of the order")
    examined_b = width * 980020 / 1_000_000
    start_b = width * 980000 / 1_000_000
    body.append(rect(x0, yb, examined_b, 34, fill=SHADE, stroke="none", sw=0))
    body.append(rect(x0 + start_b, yb + 4, width - start_b, 26, fill=RED, stroke="none", sw=0))
    body.append(rect(x0, yb, width, 34, fill="none", stroke=LINE, sw=1.2))
    body.append(f'<path d="M{x0},{yb + 42} L{x0},{yb + 48} L{x0 + examined_b:.1f},{yb + 48} L{x0 + examined_b:.1f},{yb + 42}" fill="none" stroke="{INK}" stroke-width="1.2"/>')
    body.append(text(x0 + examined_b / 2, yb + 66, "R = 980,020", size=15, anchor="middle"))

    # legend
    yl = 252
    body.append(line(x0, yl - 12, x0, yl + 2, stroke=RED, sw=2.4))
    body.append(text(x0 + 10, yl, "authorized rows (in (a), each mark stands for 4 rows)", size=14, fill=GRAY))
    body.append(rect(x0 + 430, yl - 12, 22, 14, fill=SHADE, stroke="none", sw=0))
    body.append(text(x0 + 460, yl, "rows examined up to the 20th authorized row", size=14, fill=GRAY))
    return svg(1000, 266, body)


# ---------------------------------------------------------------- Figure 2
def fig_storage():
    body = []
    # (a) resource tree
    body.append(text(20, 26, "(a)", size=16, weight="bold"))
    body.append(text(52, 26, "Resource tree", size=15))
    cx = 165
    for i, (lab, hl) in enumerate([("root", False), ("D1", False), ("C1", False), ("R1", True)]):
        y = 42 + i * 46
        body += node(cx, y, lab, h=26, fill=BLUE_FILL if hl else "#f3f4f6", stroke=BLUE if hl else LINE, sw=2 if hl else 1.2)
        if i:
            body.append(line(cx, y - 20, cx, y))
    ys = 42 + 4 * 46 + 8
    for sx, lab in [(95, "S1"), (235, "S2")]:
        body += node(sx, ys, lab, h=26)
        body.append(line(cx, ys - 28, sx, ys))
    yp = ys + 56
    for px, sx, lab in [(55, 95, "p1"), (135, 95, "p2"), (195, 235, "p3"), (275, 235, "p4")]:
        body += node(px, yp, lab, w=56, h=26, fill="#ffffff", dash="4 3")
        body.append(line(sx, ys + 26, px, yp, dash="4 3", stroke=GRAY))

    # (b) application table
    bx = 350
    body.append(text(bx, 26, "(b)", size=16, weight="bold"))
    body.append(text(bx + 32, 26, "Products", size=15))
    body += table(bx, 40, [50, 66, 108], ["Id", "Price", "ResourceId"],
                  [["p1", "3", "S1"], ["p2", "9", "S1"], ["p3", "5", "S2"], ["p4", "7", "S2"]])

    # (c) projection
    px0 = 620
    body.append(text(px0, 26, "(c)", size=16, weight="bold"))
    body.append(text(px0 + 32, 26, "Projection on (Price, Id)", size=15))
    rows = [["S1", "3", "p1"], ["S1", "9", "p2"], ["S2", "5", "p3"], ["S2", "7", "p4"],
            ["R1", "3", "p1"], ["R1", "5", "p3"], ["R1", "7", "p4"], ["R1", "9", "p2"]]
    fills = [None] * 4 + [BLUE_FILL] * 4
    body += table(px0, 40, [70, 70, 80], ["Node", "Price", "Id"], rows, fills=fills)
    note_y = 40 + 24 * 9
    body.append(rect(px0, note_y, 220, 24, fill="#ffffff", stroke="#d1d5db", sw=1))
    body.append(text(px0 + 8, note_y + 17, "C1, D1, root: as R1", size=14, fill=GRAY, style="italic"))
    top, bottom = 40 + 24 * 5, 40 + 24 * 9
    bx2 = px0 + 220 + 10
    body.append(f'<path d="M{bx2 - 4},{top + 2} L{bx2 + 6},{top + 2} L{bx2 + 6},{bottom - 2} L{bx2 - 4},{bottom - 2}" fill="none" stroke="{BLUE}" stroke-width="2"/>')
    body.append(text(bx2 + 14, (top + bottom) / 2 - 3, "range", size=15, fill=BLUE))
    body.append(text(bx2 + 14, (top + bottom) / 2 + 15, "of R1", size=15, fill=BLUE))
    return svg(1000, 330, body)


# ---------------------------------------------------------------- Figure 3
def fig_retrieval():
    body = []
    w = 290
    xs = [20, 355, 690]
    heads = [("(a)", "Scan"), ("(b)", "Merge"), ("(c)", "Verification")]
    for x, (letter, head) in zip(xs, heads):
        body.append(text(x, 26, letter, size=16, weight="bold"))
        body.append(text(x + 32, 26, head, size=15))

    # (a) first 20 rows of Q after u in (Price, Id) order, each tagged by the access function
    x = xs[0]
    body.append(text(x, 54, "first 20 rows of Q after u, in (Price, Id) order", size=14, fill=GRAY))
    rows = [["row 1", "no"], ["row 2", "no"], ["row 3", "no"], ["...", "..."], ["row 20", "no"]]
    body += table(x, 66, [150, 140], ["Row", "authorized"], rows, colors=[RED, RED, RED, GRAY, RED])
    body.append(text(x, 66 + 24 * 6 + 24, "0 of 20 rows authorized", size=15))
    body.append(text(x, 66 + 24 * 6 + 46, "rows read: 20", size=15, weight="bold"))

    # (b) scope function and the store's range in the projection
    x = xs[1]
    body.append(text(x, 54, "scopes S(p, perm) = {S7}", size=14, fill=GRAY))
    rows = [["S6", "..."], ["S7", "1st entry after u"], ["S7", "2nd entry after u"], ["S7", "..."],
            ["S7", "20th entry after u"], ["S8", "..."]]
    fills = [None, BLUE_FILL, BLUE_FILL, BLUE_FILL, BLUE_FILL, None]
    colors = [GRAY, INK, INK, INK, INK, GRAY]
    body += table(x, 66, [70, 220], ["Node", "(Price, Id)"], rows, fills=fills, colors=colors)
    body.append(text(x, 66 + 24 * 7 + 24, "first 20 entries of the range of S7", size=15))
    body.append(text(x, 66 + 24 * 7 + 46, "rows read: 40 (20 entries, 20 rows)", size=15, weight="bold"))

    # (c) verification
    x = xs[2]
    body.append(text(x, 54, "the 20 selected rows, loaded by key", size=14, fill=GRAY))
    rows = [["row 1", "yes"], ["row 2", "yes"], ["...", "..."], ["row 20", "yes"]]
    body += table(x, 66, [150, 140], ["Row", "authorized"], rows, colors=[GREEN, GREEN, GRAY, GREEN])
    body.append(text(x, 66 + 24 * 5 + 24, "page of 20 rows; cursor = (Price, Id)", size=15))
    body.append(text(x, 66 + 24 * 5 + 44, "of the last selected entry", size=15))
    body.append(text(x, 66 + 24 * 5 + 68, "rows read: 20", size=15, weight="bold"))

    # arrows between panels
    body.append(line(xs[0] + w + 6, 120, xs[1] - 8, 120, arrow=True, sw=1.6))
    body.append(line(xs[1] + w + 6, 120, xs[2] - 8, 120, arrow=True, sw=1.6))
    return svg(1000, 286, body)


# ---------------------------------------------------------------- Figure 4
def fig_maintenance():
    body = []
    # (a) insertion
    body.append(text(20, 26, "(a)", size=16, weight="bold"))
    body.append(text(52, 26, "Insertion of p9 (Price 6) below S2", size=15))
    cx = 80
    chain = ["root", "D1", "C1", "R1", "S2"]
    for i, lab in enumerate(chain):
        y = 44 + i * 46
        body += node(cx, y, lab, w=80, h=26)
        if i:
            body.append(line(cx, y - 20, cx, y))
    yp = 44 + 5 * 46
    body += node(cx, yp, "p9", w=56, h=26, fill=GREEN_FILL, stroke=GREEN, sw=1.6)
    body.append(line(cx, yp - 20, cx, yp, stroke=GREEN, sw=1.6))
    body.append(text(190, 54, "entries inserted into the projection on (Price, Id)", size=14, fill=GRAY))
    rows = [["+", "S2", "6", "p9"], ["+", "R1", "6", "p9"], ["+", "C1", "6", "p9"], ["+", "D1", "6", "p9"], ["+", "root", "6", "p9"]]
    body += table(190, 66, [36, 80, 70, 70], ["", "Node", "Price", "Id"], rows,
                  fills=[GREEN_FILL] * 5, colors=[GREEN] * 5)

    # (b) move of a region
    x0 = 530
    body.append(text(x0, 26, "(b)", size=16, weight="bold"))
    body.append(text(x0 + 32, 26, "Move of R1 from C1 to C2", size=15))
    body += node(x0 + 120, 44, "root", w=80, h=26)
    for dx, d, c in [(60, "D1", "C1"), (180, "D2", "C2")]:
        body += node(x0 + dx, 92, d, w=70, h=26)
        body.append(line(x0 + 120, 70, x0 + dx, 92))
        body += node(x0 + dx, 140, c, w=70, h=26)
        body.append(line(x0 + dx, 118, x0 + dx, 140))
    body += node(x0 + 180, 196, "R1", w=70, h=26, fill=BLUE_FILL, stroke=BLUE, sw=2)
    body.append(line(x0 + 180, 166, x0 + 180, 196, stroke=BLUE, sw=2))
    body.append(line(x0 + 60, 166, x0 + 160, 196, dash="5 4", stroke=GRAY))
    body += node(x0 + 180, 246, "S1, S2", w=70, h=26)
    body.append(line(x0 + 180, 222, x0 + 180, 246))
    body += node(x0 + 180, 296, "p1 ... p4", w=84, h=26, fill="#ffffff", dash="4 3")
    body.append(line(x0 + 180, 272, x0 + 180, 296, dash="4 3", stroke=GRAY))

    tx = x0 + 280
    body.append(text(tx, 54, "entries of p1 (Price 3)", size=14, fill=GRAY))
    rows = [["−", "C1"], ["−", "D1"], ["+", "C2"], ["+", "D2"]]
    body += table(tx, 66, [36, 74], ["", "Node"], rows,
                  fills=["#fee2e2", "#fee2e2", GREEN_FILL, GREEN_FILL], colors=[RED, RED, GREEN, GREEN])
    body.append(text(tx, 66 + 24 * 5 + 22, "S1, R1, root:", size=14, fill=GRAY))
    body.append(text(tx, 66 + 24 * 5 + 40, "rewritten unchanged", size=14, fill=GRAY))
    body.append(text(tx, 66 + 24 * 5 + 66, "p2, p3, p4: likewise", size=14, fill=GRAY))
    return svg(1000, 336, body)


for name, fn in [("sparse-problem", fig_sparse), ("storage", fig_storage), ("retrieval", fig_retrieval),
                 ("maintenance", fig_maintenance)]:
    (OUT / f"{name}.svg").write_text(fn(), encoding="utf-8")
    print("wrote", name)
