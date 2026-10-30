"""Writes FluentSystemIcons-Regular.ttf next to this script: the glyphs ../Glyphs.cs lists, from the original font.

Tool: fonttools 4.66.1 (pip install fonttools==4.66.1)
Source: Fluent UI System Icons 1.1.344, fonts/FluentSystemIcons-Regular.ttf
    https://raw.githubusercontent.com/microsoft/fluentui-system-icons/1.1.344/fonts/FluentSystemIcons-Regular.ttf
    SHA-256 f977ae5bfb46661e7ba94b098e0d7720e27af888e57b9b14e239933ac4c0e6f4

Usage: python -I subset-icons.py <the original FluentSystemIcons-Regular.ttf>
To add an icon, add its code point to Glyphs.cs and run this again.
"""
import hashlib
import re
import sys
from pathlib import Path

import fontTools
from fontTools import subset
from fontTools.ttLib import TTFont

FONTTOOLS = "4.66.1"
SOURCE_SHA256 = "f977ae5bfb46661e7ba94b098e0d7720e27af888e57b9b14e239933ac4c0e6f4"
HERE = Path(__file__).resolve().parent
GLYPHS = HERE.parent / "Glyphs.cs"
OUT = HERE / "FluentSystemIcons-Regular.ttf"


def code_points():
    """Glyphs.cs's constants: one character each, written as itself or as a \\u or \\U escape."""
    text = GLYPHS.read_text(encoding="utf-8")
    consts = re.findall(r"public const string (\w+) = \"([^\"]*)\";", text)
    if not consts or len(consts) != text.count("public const string"):
        sys.exit(f"{GLYPHS}: a constant isn't of the form: public const string Name = \"<one character>\";")
    points = []
    for name, value in consts:
        if re.fullmatch(r"\\u[0-9A-Fa-f]{4}|\\U[0-9A-Fa-f]{8}", value):
            value = chr(int(value[2:], 16))
        if len(value) != 1:
            sys.exit(f"{GLYPHS}: {name} isn't one character")
        points.append((name, ord(value)))
    return points


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    if fontTools.version != FONTTOOLS:
        sys.exit(f"fonttools {fontTools.version}: this script is pinned to {FONTTOOLS}")
    source = Path(sys.argv[1])
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    if digest != SOURCE_SHA256:
        sys.exit(f"{source}: SHA-256 {digest}, not the pinned release's {SOURCE_SHA256}")

    points = code_points()
    # the source's glyph headers say xMin 0 and FreeType shifts an outline by xMin - lsb: recalculated bounds move the icons
    font = TTFont(source, recalcBBoxes=False, recalcTimestamp=False)
    cmap = font.getBestCmap()
    missing = [f"{name} U+{cp:04X}" for name, cp in points if cp not in cmap]
    if missing:
        sys.exit("not in the source font: " + ", ".join(missing))

    options = subset.Options()
    options.name_IDs = ["*"]   # every name record as it is: the copyright, licence and URLs
    options.name_languages = ["*"]
    options.name_legacy = True
    options.notdef_outline = True
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=sorted({cp for _, cp in points}))
    subsetter.subset(font)
    font.save(OUT)
    print(f"{OUT.name}: {len(points)} code points, {len(font.getGlyphOrder())} glyphs, {OUT.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
