#!/usr/bin/env python3
"""
Bulk-update SG test files: in IMPL baselines (inline @"..." strings starting
with `#pragma checksum`), strip the `#nullable restore / class : global::Y /
#nullable disable` block down to just `class X`. Mirrors what
DefaultRazorCSharpLoweringPhase.TryWriteImplDocument now produces post-fix.

Decl baselines (which don't start with `#pragma checksum` after the decl-
checksum-suppress fix) are left alone -- decl half still emits the full
class header.
"""
import re
import sys
from pathlib import Path

# The block we're collapsing inside an impl baseline. Anchored on leading
# 4-space indent (the namespace-member indent in generated code). Captures
# the class name so we can preserve it.
IMPL_CLASS_HEADER = re.compile(
    r"    #nullable restore\r?\n"
    r"    public partial class (\w+) : global::[\w.<>,?\s]+\r?\n"
    r"    #nullable disable\r?\n",
    re.MULTILINE,
)


def transform_impl_baseline(body: str) -> tuple[str, int]:
    """Apply the class-header collapse to an impl baseline body. Returns
    (new_body, replacements_made)."""
    count = 0

    def repl(m):
        nonlocal count
        count += 1
        return f"    public partial class {m.group(1)}\n"

    return IMPL_CLASS_HEADER.sub(repl, body), count


# Match a verbatim string literal: `@"..."`. The body is everything between
# the opening @" and the closing " (with "" being an escaped quote inside).
# We use a non-greedy match plus a lookahead to make sure we don't swallow
# escaped quotes.
VERBATIM_STRING = re.compile(
    r'@"((?:[^"]|"")*?)"(?!")',
    re.DOTALL,
)


def process_file(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    total_replacements = 0

    def replace_string(m):
        nonlocal total_replacements
        body = m.group(1)
        # Impl baselines contain `BuildRenderTree` (the primary method body).
        # Decl baselines never do -- the split moves the render method to impl.
        # Using #pragma-checksum-presence to discriminate doesn't work because
        # test inline baselines for decl often still carry the (now-stripped)
        # checksum line and VerifyPageOutput tolerates it.
        if "BuildRenderTree" not in body:
            return m.group(0)
        new_body, count = transform_impl_baseline(body)
        total_replacements += count
        if count == 0:
            return m.group(0)
        return f'@"{new_body}"'

    new_text = VERBATIM_STRING.sub(replace_string, text)
    if new_text != text:
        path.write_text(new_text, encoding="utf-8")
    return total_replacements


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
    total = 0
    files_changed = 0
    for cs_file in root.rglob("*.cs"):
        before = total
        total += process_file(cs_file)
        if total > before:
            files_changed += 1
            print(f"  {cs_file.relative_to(root)}: +{total - before} impl class headers stripped")
    print(f"\nTotal: {total} class headers stripped across {files_changed} files")


if __name__ == "__main__":
    main()
