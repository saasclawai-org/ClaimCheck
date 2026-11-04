#!/usr/bin/env python3
"""Export a client master (SQLite) into ClaimCheck's client-gazetteer files.

Reads a companies table and merges names + digit-normalized FEINs into
~/.sanitize-mcp/custom_clients.json and ~/.sanitize-mcp/custom_feins.json.
Existing manual entries are preserved — the export is additive (set union).
ClaimCheck hot-reloads both files on mtime change, so no restart is needed.

Usage:
  python3 tools/export-clients.py --db <sqlite path> [--table companies]
      [--name-col legal_name] [--fein-col fein] [--out-dir ~/.sanitize-mcp]
"""
import argparse, json, os, re, sqlite3, sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--db', required=True)
    ap.add_argument('--table', default='companies')
    ap.add_argument('--name-col', default='legal_name')
    ap.add_argument('--fein-col', default='fein')
    ap.add_argument('--out-dir', default=os.path.join(os.path.expanduser('~'), '.sanitize-mcp'))
    a = ap.parse_args()

    db = sqlite3.connect(a.db)
    cols = {r[1] for r in db.execute(f'PRAGMA table_info({a.table})')}
    for c in (a.name_col, a.fein_col):
        if c not in cols:
            sys.exit(f'column {c!r} not in table {a.table!r}: {sorted(cols)}')
    rows = db.execute(f'SELECT {a.name_col}, {a.fein_col} FROM {a.table}').fetchall()

    names, feins = set(), set()
    for name, fein in rows:
        if name and str(name).strip():
            names.add(str(name).strip())
        if fein:
            d = re.sub(r'\D', '', str(fein))
            if 6 <= len(d) <= 11:
                feins.add(d)

    def merge(path, add):
        old = []
        if os.path.exists(path):
            try:
                with open(path) as f:
                    old = json.load(f)
            except Exception:
                old = []
        merged = sorted(set(old) | add)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w') as f:
            json.dump(merged, f, indent=1)
        return len(merged), len(merged) - len(set(old))

    names_path = os.path.join(a.out_dir, 'custom_clients.json')
    feins_path = os.path.join(a.out_dir, 'custom_feins.json')
    n_tot, n_new = merge(names_path, names)
    f_tot, f_new = merge(feins_path, feins)
    print(f'{len(rows)} rows from {a.db} ({a.table})')
    print(f'names: {n_tot} total (+{n_new} new) -> {names_path}')
    print(f'feins: {f_tot} total (+{f_new} new) -> {feins_path}')


if __name__ == '__main__':
    main()
