#!/usr/bin/env python3
"""One-shot migration: add `NANOCAD` using-branches to every C# source file
that imports the AutoCAD .NET API.

nanoCAD 24 exposes an AutoCAD-compatible API under different namespaces:
  Autodesk.AutoCAD.ApplicationServices -> HostMgd.ApplicationServices
  Autodesk.AutoCAD.EditorInput         -> HostMgd.EditorInput
  Autodesk.AutoCAD.PlottingServices    -> HostMgd.PlottingServices
  Autodesk.AutoCAD.<anything else>     -> Teigha.<anything else>

The nanoCAD project defines both AUTOCAD and NANOCAD, so the NANOCAD branch
must be checked *before* the AUTOCAD / ACAD_CORE branches.

Usage: python3 add_nanocad_usings.py <src-dir> [--check]
"""
import re
import sys
from pathlib import Path

HOST_NAMESPACES = ("ApplicationServices", "EditorInput", "PlottingServices")


def map_line(line: str) -> str:
    line = line.replace(
        "Autodesk.AutoCAD.ApplicationServices.Core.Application",
        "HostMgd.ApplicationServices.Application",
    )

    def repl(match: re.Match) -> str:
        ns = match.group(1)
        root = ns.split(".")[0]
        return ("HostMgd." if root in HOST_NAMESPACES else "Teigha.") + ns

    return re.sub(r"Autodesk\.AutoCAD\.([A-Za-z0-9_.]+)", repl, line)


def header_end(lines):
    for i, line in enumerate(lines):
        if line.startswith("namespace "):
            return i
    raise ValueError("namespace declaration not found")


def autocad_branch_lines(block):
    """Return the using lines of the plain AutoCAD (non-Core) branch."""
    result = []
    stack = []  # list of (symbol, active)
    for line in block:
        s = line.strip()
        if s.startswith("#if "):
            stack.append(s[4:].strip())
            continue
        if s.startswith("#elif "):
            stack[-1] = s[6:].strip()
            continue
        if s == "#else":
            stack[-1] = "!" + stack[-1]
            continue
        if s == "#endif":
            stack.pop()
            continue
        if not s.startswith("using "):
            continue
        active = True
        for cond in stack:
            # evaluate for the plain AutoCAD (net48) configuration
            symbols = {"AUTOCAD": True, "ACAD_CORE": False, "ZWCAD": False}
            if cond.startswith("!") and cond[1:] in symbols:
                ok = not symbols[cond[1:]]
            elif cond.startswith("!!"):
                ok = symbols.get(cond[2:], False)
            else:
                # "!X" produced by #else of a simple symbol, or a plain symbol
                neg = cond.startswith("!")
                name = cond[1:] if neg else cond
                ok = symbols.get(name, False)
                ok = not ok if neg else ok
            active = active and ok
        if active and "Autodesk.AutoCAD" in line:
            result.append(map_line(line))
    return result


def transform(text: str):
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.split(newline)
    end = header_end(lines)
    header = lines[:end]
    if any("NANOCAD" in l for l in header):
        return None
    if not any("Autodesk.AutoCAD" in l for l in header):
        return None

    nano = autocad_branch_lines(header)
    if not nano:
        raise ValueError("no AutoCAD usings found in header")

    # locate the first top-level preprocessor block that contains the usings
    first_if = next((i for i, l in enumerate(header) if l.startswith("#if ")), None)
    first_using = next(i for i, l in enumerate(header) if "Autodesk.AutoCAD" in l)

    if first_if is None or first_if > first_using:
        # unconditional AutoCAD usings: wrap the whole contiguous using region
        start = first_using
        while start > 0 and header[start - 1].startswith("using "):
            start -= 1
        stop = start
        depth = 0
        while stop < len(header):
            s = header[stop].strip()
            if s.startswith("#if "):
                depth += 1
            elif s == "#endif":
                depth -= 1
            elif not (s.startswith("using ") or s.startswith("#")) and depth == 0:
                break
            stop += 1
        while stop > start and header[stop - 1].strip() == "":
            stop -= 1
        # keep usings unrelated to the CAD API outside the conditional block
        region = header[start:stop]
        cad_from = next(i for i, l in enumerate(region)
                        if "Autodesk.AutoCAD" in l or l.startswith("#"))
        plain = [l for l in region[:cad_from]]
        cad_lines = region[cad_from:]
        plain += [l for l in cad_lines if l.startswith("using ") and "Autodesk.AutoCAD" not in l
                  and not any(c.startswith("#") for c in cad_lines[:cad_lines.index(l)])]
        cad_lines = [l for l in cad_lines if l not in plain]
        block = plain + ["#if NANOCAD"] + nano + ["#else"] + cad_lines + ["#endif"]
        header = header[:start] + block + header[stop:]
    else:
        cond = header[first_if][4:].strip()
        if cond == "AUTOCAD":
            header = (header[:first_if] + ["#if NANOCAD"] + nano
                      + ["#elif AUTOCAD"] + header[first_if + 1:])
        elif cond == "ZWCAD":
            # insert after the ZWCAD branch, i.e. before the first top-level #elif/#else
            depth = 0
            for j in range(first_if + 1, len(header)):
                s = header[j].strip()
                if s.startswith("#if "):
                    depth += 1
                elif s == "#endif":
                    if depth == 0:
                        raise ValueError("unexpected #endif")
                    depth -= 1
                elif depth == 0 and (s.startswith("#elif ") or s == "#else"):
                    header = header[:j] + ["#elif NANOCAD"] + nano + header[j:]
                    break
        else:
            raise ValueError("unsupported header condition: " + cond)

    return newline.join(header + lines[end:])


def main():
    root = Path(sys.argv[1])
    check = "--check" in sys.argv
    changed = 0
    for path in sorted(root.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        raw = path.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        try:
            result = transform(text)
        except ValueError as exc:
            print(f"ERROR {path}: {exc}")
            sys.exit(1)
        if result is None:
            continue
        changed += 1
        print(f"updated {path}")
        if not check:
            data = result.encode("utf-8")
            path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + data)
    print(f"{changed} file(s) {'would be ' if check else ''}updated")


if __name__ == "__main__":
    main()
