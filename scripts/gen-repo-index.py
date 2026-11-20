#!/usr/bin/env python3
"""Regenerate docs/repo-index.md from the working tree.

The index is a navigation aid, not a design doc: it answers "which file holds this
type" and "what is in this project". It goes stale the moment a file is added, so it
is generated, never hand-edited -- run this after any change that adds or renames a
source file.

    python3 scripts/gen-repo-index.py

Reads the tree it is run against, writes docs/repo-index.md, and stamps the branch and
commit it was generated from so a reader can tell how old it is.
"""
import os, re, subprocess, sys, io

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules', 'dist', '.vs', '.idea',
             '.hermes', 'packages', 'TestResults', '.cache'}

# Project roots in the order they appear in the index.
PROJECTS = [
    ('Factions.Core',       'src/Factions.Core',   'netstandard2.0 engine -- no SE/Torch references'),
    ('Factions (Torch host)', 'src/Factions',      'net48 Torch plugin -- the server-side host boundary'),
    ('FactionsUI (client plugin)', 'src/FactionsUI', 'net48 SE client plugin, loaded by Pulsar'),
    ('Factions.Wire',       'src/Factions.Wire',   'source-only, compiled into BOTH the host and the client'),
    ('Factions.Shared',     'src/Factions.Shared', 'source-only, compiled into both halves'),
    ('Factions.Api',        'src/Factions.Api',    'net9 dashboard backend'),
    ('Factions.Sim',        'src/Factions.Sim',    'net9 console balancing tool -- references Core only'),
    ('Factions.Dashboard',  'src/Factions.Dashboard', 'React/TypeScript web UI'),
    ('Factions.Core.Tests', 'tests/Factions.Core.Tests', 'xUnit on Core'),
]

TYPE_RE = re.compile(
    r'^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*'
    r'(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)', re.M)
PS_FUNC_RE = re.compile(r'^\s*function\s+([A-Za-z][A-Za-z0-9_-]*)', re.M | re.I)
TS_EXPORT_RE = re.compile(
    r'^\s*export\s+(?:default\s+)?(?:async\s+)?(?:function|const|class|interface|type|enum)\s+'
    r'([A-Za-z_][A-Za-z0-9_]*)', re.M)


def walk(rel):
    base = os.path.join(ROOT, rel)
    out = []
    if not os.path.isdir(base):
        return out
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for fn in sorted(filenames):
            out.append(os.path.relpath(os.path.join(dirpath, fn), ROOT).replace(os.sep, '/'))
    return out


def symbols(path):
    ext = os.path.splitext(path)[1].lower()
    if ext not in ('.cs', '.ps1', '.ts', '.tsx'):
        return []
    try:
        text = io.open(os.path.join(ROOT, path), encoding='utf-8', errors='replace').read()
    except OSError:
        return []
    if ext == '.cs':
        names = [m.group(2) for m in TYPE_RE.finditer(text)]
    elif ext == '.ps1':
        names = PS_FUNC_RE.findall(text)
    else:
        names = TS_EXPORT_RE.findall(text)
    seen, uniq = set(), []
    for n in names:
        if n not in seen:
            seen.add(n)
            uniq.append(n)
    return uniq


def git(*args):
    try:
        return subprocess.check_output(['git', '-C', ROOT] + list(args),
                                       stderr=subprocess.DEVNULL).decode().strip()
    except Exception:
        return '(unknown)'


def main():
    lines = []
    w = lines.append
    w('# Factions repository index')
    w('')
    w('**Generated — do not hand-edit.** Regenerate with `python3 scripts/gen-repo-index.py`')
    w('after any change that adds, renames or removes a source file.')
    w('')
    w('## Snapshot')
    w('- Branch: `%s`' % git('rev-parse', '--abbrev-ref', 'HEAD'))
    w('- Commit: `%s` (%s)' % (git('rev-parse', '--short', 'HEAD'),
                               git('log', '-1', '--format=%ad', '--date=short')))
    w('')
    w('## How to search')
    w('- **File names:** ripgrep or editor search over the paths below.')
    w('- **Types:** the inverted symbol index at the end maps a type name to its file.')
    w('- **Relationships** ("what calls this", "what else implements this") — use the Scirix')
    w('  skill (`scirix rm`), not this file. This index lists what exists, not how it connects.')
    w('- **Runtime data:** `index.json` in the live data directory is the faction index, not this.')
    w('')

    index = {}   # symbol -> [paths]
    for title, rel, blurb in PROJECTS:
        files = walk(rel)
        if not files:
            continue
        code = [f for f in files if os.path.splitext(f)[1].lower()
                in ('.cs', '.ts', '.tsx', '.csproj', '.ps1', '.json', '.xml')]
        w('## %s' % title)
        w('')
        w('*%s — %d files.*' % (blurb, len(code)))
        w('')
        for f in code:
            syms = symbols(f)
            for s in syms:
                index.setdefault(s, []).append(f)
            if syms:
                shown = ', '.join(syms[:8]) + (', …' if len(syms) > 8 else '')
                w('- `%s` — %s' % (f, shown))
            else:
                w('- `%s`' % f)
        w('')

    w('## Scripts')
    w('')
    for f in walk('scripts'):
        syms = symbols(f)
        for s in syms:
            index.setdefault(s, []).append(f)
        w('- `%s`%s' % (f, ' — ' + ', '.join(syms) if syms else ''))
    w('')

    w('## Docs')
    w('')
    docs = [f for f in walk('docs') if f.endswith('.md') and '/issues/' not in f]
    for f in docs:
        w('- `%s`' % f)
    w('')
    w('The issue queue (`docs/issues/FAC-###-*.md`, %d files) is deliberately not listed here —'
      % len([f for f in walk('docs') if '/issues/FAC-' in f]))
    w('it turns over too fast. See [issues/README.md](issues/README.md).')
    w('')

    w('## Root')
    w('')
    for f in sorted(os.listdir(ROOT)):
        p = os.path.join(ROOT, f)
        if os.path.isdir(p) or f.startswith('.'):
            continue
        syms = symbols(f)
        for s in syms:
            index.setdefault(s, []).append(f)
        w('- `%s`%s' % (f, ' — ' + ', '.join(syms) if syms else ''))
    w('')

    w('## Symbol index')
    w('')
    w('Type, interface, enum and top-level function names, mapped to the file that declares them.')
    w('A name listed against more than one path is declared in more than one place — usually a')
    w('`partial`, occasionally a genuine collision worth looking at.')
    w('')
    for s in sorted(index, key=lambda x: x.lower()):
        paths = sorted(set(index[s]))
        w('- `%s` — %s' % (s, ', '.join('`%s`' % p for p in paths)))
    w('')

    out = os.path.join(ROOT, 'docs', 'repo-index.md')
    io.open(out, 'w', encoding='utf-8').write('\n'.join(lines))
    sys.stderr.write('wrote %s (%d lines, %d symbols)\n' % (out, len(lines), len(index)))


if __name__ == '__main__':
    main()
