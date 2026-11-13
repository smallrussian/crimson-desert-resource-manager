"""Compare a new reader snapshot with a baseline, for checking a game patch (docs/patch-update.md).

    python tools/re/compare_snapshots.py <new.json> <baseline.json | snapshots.jsonl> [--version <old version>]

With a .jsonl log as the baseline, the last save snapshot of --version (or of any version other than the new one)
is used. Prints every difference in save state, static mission data and comrade skill slots. Exit code 1 if any.
Reads only the two files given.
"""
import json
import sys

STATIC = ('name', 'type', 'location', 'region', 'category', 'workersMin', 'workersMax', 'role',
          'durationHours', 'cost', 'reward')


def load_baseline(path, new_version, want_version):
    if not path.endswith('.jsonl'):
        return json.load(open(path, encoding='utf-8'))
    rows = [json.loads(l) for l in open(path, encoding='utf-8') if l.strip()]
    rows = [r for r in rows if r.get('source') == 'save'
            and (r.get('gameVersion') == want_version if want_version else r.get('gameVersion') != new_version)]
    if not rows:
        sys.exit('no matching save snapshot in ' + path)
    return rows[-1]


def main(argv):
    if len(argv) < 2:
        sys.exit(__doc__)
    want = argv[argv.index('--version') + 1] if '--version' in argv else None
    new = json.load(open(argv[0], encoding='utf-8'))
    old = load_baseline(argv[1], new.get('gameVersion'), want)
    print(f"new {new.get('gameVersion')} {new.get('capturedAt')}  vs  baseline {old.get('gameVersion')} {old.get('capturedAt')}")
    diffs = 0

    def report(what, a, b):
        nonlocal diffs
        diffs += 1
        print(f'  {what}: {a!r} -> {b!r}')

    print('save state:')
    for k in ('reserves', 'workers', 'campLevel'):
        if old.get(k) != new.get(k):
            report(k, old.get(k), new.get(k))
    oa = {m.get('internalName'): m for m in old.get('missions') or []}
    na = {m.get('internalName'): m for m in new.get('missions') or []}
    for k in sorted(set(oa) | set(na), key=str):
        if k not in oa or k not in na:
            report('active mission ' + str(k), 'present' if k in oa else None, 'present' if k in na else None)

    print('static mission data (active + available):')
    om = {m['internalName']: m for m in (old.get('missions') or []) + (old.get('availableMissions') or [])}
    nm = {m['internalName']: m for m in (new.get('missions') or []) + (new.get('availableMissions') or [])}
    for k in sorted(set(om) | set(nm), key=str):
        if k not in om or k not in nm:
            report(str(k), 'present' if k in om else None, 'present' if k in nm else None)
            continue
        for f in STATIC:
            if om[k].get(f) != nm[k].get(f):
                report(f'{k}.{f}', om[k].get(f), nm[k].get(f))
    print(f'  ({len(set(om) & set(nm))} missions compared)')

    print('comrade skill slots:')
    oc = {c['characterKey']: c for c in old.get('comrades') or []}
    nc = {c['characterKey']: c for c in new.get('comrades') or []}
    for k in sorted(set(oc) | set(nc)):
        a, b = oc.get(k, {}).get('slots'), nc.get(k, {}).get('slots')
        if a != b:
            report(f"{(oc.get(k) or nc.get(k)).get('name')} ({k})", a, b)
    print(f'  ({len(set(oc) & set(nc))} comrades compared)')

    new_unknowns = [u for u in new.get('unknowns') or [] if u not in (old.get('unknowns') or [])]
    if new_unknowns:
        print('new unknowns:')
        for u in new_unknowns:
            report('unknown', None, u)
    print(f'{diffs} difference(s)')
    return 1 if diffs else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
