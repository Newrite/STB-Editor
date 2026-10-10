"""Interface translation helper for STB Editor (see docs/DEVELOPING.md, "Перевод интерфейса").

    python tools/loc.py missing            keys used in src/Editor.App that src/Editor.App/Ui/LangRu.cs lacks
    python tools/loc.py keys               every key (T("..."), TF($"...") composite formats, label tables)
    python tools/loc.py wrap <file.cs>...  wrap user-visible literals in T() / TF() (add --dry to only list)

Run from the repository root. The wrapper is a heuristic: review its diff, it skips lines that
look like logic (comparisons, logs, IDs, paths) and static tables (translate those where drawn).
"""
import glob
import os
import re
import sys

SKIP_LINE = re.compile(
    r'Log\.(Info|Warn|Error|Write)|throw |const |case "|\bis "|==|!=|\.Equals\(|StartsWith|EndsWith|Contains\(|'
    r'GetCommandLineArgs|DockBuilder|Name => |nameof|\[DllImport|LibraryImport|Path\.|GetID\(|PushID\(|'
    r'BeginChild\(|BeginTable\(|IndexOf\(|Split\(|Replace\(|Process\.Start|ProcessStartInfo|Environment\.|'
    r'\bT\(|\bTF\(|Title\(|Signature|signature|Regex|\.Parse\(|EditorId ==|"--|autotest|Autotest|"STB|NewRecords\.|Registration"|EqualityContract|static readonly'
)
HUMAN = re.compile(r'^[A-Z][a-z]|^[A-Z]{2,}[a-z ]|\s[a-z]{2,}|^%[^"]*\s\w|^\(|^[a-z]+ [a-z]')


def human(text):
    if not text or text.startswith('##') or text.startswith('#'):
        return False
    if '\\\\' in text or text.endswith(('.nif', '.dds', '.esp', '.esm', '.esl', '.pex', '.json', '.ini', '.txt', '.ttf')):
        return False
    if re.fullmatch(r'[A-Z0-9_]{4}', text):  # record signatures
        return False
    if re.fullmatch(r'[\W\d_]*', text):  # punctuation / numbers only
        return False
    return bool(HUMAN.search(text))


def literals(line):
    """Yields (start, end, kind, text) for regular and interpolated literals in one line."""
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if line.startswith('//', i):
            return
        if c == "'":
            j = i + 1
            while j < n and line[j] != "'":
                j += 2 if line[j] == '\\' else 1
            i = j + 1
            continue
        if line.startswith('"""', i):
            return  # raw string: leave the line alone
        verbatim = (c == '@' and i + 1 < n and line[i + 1] in '"$') or (c == '$' and i + 1 < n and line[i + 1] == '@')
        if verbatim:
            j = line.find('"', i)
            k = j + 1
            while k < n:
                if line[k] == '"' and (k + 1 >= n or line[k + 1] != '"'):
                    break
                k += 2 if line[k] == '"' else 1
            i = k + 1
            continue
        if c == '$' and i + 1 < n and line[i + 1] == '"':
            j = i + 2
            depth = 0
            while j < n:
                ch = line[j]
                if depth == 0:
                    if ch == '\\':
                        j += 2
                        continue
                    if ch == '"':
                        break
                    if ch == '{':
                        if j + 1 < n and line[j + 1] == '{':
                            j += 2
                            continue
                        depth = 1
                else:
                    if ch == '"':  # string inside a hole
                        j += 1
                        while j < n and line[j] != '"':
                            j += 2 if line[j] == '\\' else 1
                    elif ch == '{':
                        depth += 1
                    elif ch == '}':
                        depth -= 1
                j += 1
            yield i, j + 1, 'interp', line[i + 2:j]
            i = j + 1
            continue
        if c == '"':
            j = i + 1
            while j < n and line[j] != '"':
                j += 2 if line[j] == '\\' else 1
            yield i, j + 1, 'plain', line[i + 1:j]
            i = j + 1
            continue
        i += 1


def composite(body):
    """$"a {x:N0} b" body -> ("a {0:N0} b", [x])."""
    out, args, i, n = [], [], 0, len(body)
    while i < n:
        ch = body[i]
        if ch == '{' and i + 1 < n and body[i + 1] == '{':
            out.append('{{'); i += 2; continue
        if ch == '}' and i + 1 < n and body[i + 1] == '}':
            out.append('}}'); i += 2; continue
        if ch == '\\':
            out.append(body[i:i + 2]); i += 2; continue
        if ch == '{':
            depth, j = 1, i + 1
            while j < n and depth:
                if body[j] == '"':
                    j += 1
                    while j < n and body[j] != '"':
                        j += 2 if body[j] == '\\' else 1
                elif body[j] == '{':
                    depth += 1
                elif body[j] == '}':
                    depth -= 1
                j += 1
            hole = body[i + 1:j - 1]
            # format / alignment after the expression (a top-level ':' or ',' not inside parens/strings)
            expr, fmt, d, k = hole, '', 0, 0
            while k < len(hole):
                h = hole[k]
                if h == '"':
                    k += 1
                    while k < len(hole) and hole[k] != '"':
                        k += 2 if hole[k] == '\\' else 1
                elif h in '([':
                    d += 1
                elif h in ')]':
                    d -= 1
                elif h == '?' and d == 0:
                    d += 100  # ternary: its ':' is not a format
                elif h == ':' and d >= 100:
                    d -= 100
                elif h in ':,' and d == 0:
                    expr, fmt = hole[:k], hole[k:]
                    break
                k += 1
            out.append('{%d%s}' % (len(args), fmt))
            args.append(expr)
            i = j
            continue
        out.append(ch)
        i += 1
    return ''.join(out), args


def process(path, dry):
    src = open(path, encoding='utf-8').read()
    lines = src.split('\n')
    keys = []
    changed = False
    for li, line in enumerate(lines):
        if SKIP_LINE.search(line):
            continue
        stripped = line.lstrip()
        if stripped.startswith(('//', '///', '[', 'using ', 'namespace ')):
            continue
        new, last = [], 0
        for start, end, kind, text in literals(line):
            if kind == 'plain':
                if not human(text):
                    continue
                new.append(line[last:start]); new.append('T(' + line[start:end] + ')'); last = end
                keys.append(text)
            else:
                fmt, args = composite(text)
                visible = re.sub(r'\{\d+[^}]*\}', '', fmt)
                if not args or not human(visible.strip()) or len(visible.strip()) < 3:
                    continue
                new.append(line[last:start]); new.append('TF(' + line[start:end] + ')'); last = end
                keys.append(fmt)
        if new:
            new.append(line[last:])
            lines[li] = ''.join(new)
            changed = True
    if changed and not dry:
        out = '\n'.join(lines)
        if 'using static StbEditor.App.Ui.Loc;' not in out:
            # after the last using
            m = list(re.finditer(r'^using [^\n]+;\n', out, re.M))
            pos = m[-1].end() if m else 0
            out = out[:pos] + 'using static StbEditor.App.Ui.Loc;\n' + out[pos:]
        open(path, 'w', encoding='utf-8', newline='').write(out)
    return keys


APP = os.path.join('src', 'Editor.App')
DICTIONARY = os.path.join(APP, 'Ui', 'LangRu.cs')


def keys():
    """Every translation key in the app sources, in first-use order."""
    found = []
    for f in sorted(glob.glob(os.path.join(APP, '**', '*.cs'), recursive=True)):
        norm = f.replace('\\', '/')
        if '/obj/' in norm or '/bin/' in norm or norm.endswith('LangRu.cs'):
            continue
        src = open(f, encoding='utf-8').read()
        for line in src.split('\n'):
            for m in re.finditer(r'\b(T|TF|Title)\(', line):
                for start, end, kind, text in literals(line[m.end():]):
                    if start == 0:
                        key = text.split('##')[0] if kind == 'plain' else composite(text)[0]
                        if key:
                            found.append(key)
                    break
        # tables of labels translated where they are drawn (static readonly (string ...)[] = [ ... ];)
        for block in re.finditer(r'static readonly \(string[^=]*=\s*\[(.*?)\n    \];', src, re.S):
            for line in block.group(1).split('\n'):
                for start, end, kind, text in literals(line):
                    if kind == 'plain' and human(text):
                        found.append(text)
        # panel names are window titles
        for m in re.finditer(r'public override string Name => "([^"]+)";', src):
            found.append(m.group(1))
    unique = []
    for k in found:
        if k not in unique:
            unique.append(k)
    return unique


def translated():
    src = open(DICTIONARY, encoding='utf-8').read()
    return set(m.group(1) for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\]\s*=', src))


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
    command = sys.argv[1] if len(sys.argv) > 1 else 'missing'
    if command == 'keys':
        for k in keys():
            print(k)
    elif command == 'missing':
        have = translated()
        # shortcuts ("Ctrl+O") and names stay as they are
        missing = [k for k in keys() if k not in have and not ('+' in k and re.fullmatch(r'[A-Za-z0-9+ /]*', k))]
        for k in missing:
            print(k)
        print(f'{len(missing)} missing', file=sys.stderr)
        sys.exit(1 if missing else 0)
    elif command == 'wrap':
        dry = '--dry' in sys.argv
        for f in [a for a in sys.argv[2:] if not a.startswith('--')]:
            for k in process(f, dry):
                print(f'{os.path.basename(f)}\t{k}')
    else:
        print(__doc__)
