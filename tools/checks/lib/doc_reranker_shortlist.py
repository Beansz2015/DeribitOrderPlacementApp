"""tools/checks/lib/doc_reranker_shortlist.py -- the Jev doc re-ranker's BM25 shortlist CLI.

PORTED 2026-10-06 from C:/Dev/DeribitVerdictEngine, tools/checks/lib/doc_reranker_shortlist.py
at engine f3455d0. LIVE MODE ONLY: the engine's `score` subcommand (hit@k against a
measured query set) is not ported, because this repo has no query set.

Owns CODE facts only (the shortlist and each candidate's commit date / archive label);
it never calls Jev. The engine measured BM25 as the better of its two shortlist methods
(22/26 vs grep 21/26 at k=30; 17/26 vs 12/26 at k=10), so BM25 is the only live path.

Usage:
  python doc_reranker_shortlist.py shortlist --rev REV --query TEXT --k 30 --out FILE
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import doc_sections as DS  # noqa: E402


def main(argv):
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('shortlist')
    s.add_argument('--rev', default='HEAD')
    s.add_argument('--query', required=True)
    s.add_argument('--k', type=int, default=30)
    s.add_argument('--out', required=True)
    a = ap.parse_args(argv)

    rev = DS.rev_full(a.rev)
    rev7 = rev[:7]
    sections = DS.flat_sections(rev)
    bm25 = DS.BM25Index(sections)
    ranked = bm25.rank(a.query, a.k)

    out = []
    for s_, score in ranked:
        out.append({
            'id': s_['id'], 'path': s_['path'], 'heading_chain': s_['heading_chain'],
            'heading_text': s_['heading_text'], 'text': s_['text'],
            'is_archive': s_['is_archive'], 'truncated': s_['truncated'],
            'start_line': s_['start_line'], 'end_line': s_['end_line'],
            'bm25_score': score,
            'commit_date_epoch': DS.commit_date_epoch(rev, s_['path']),
        })
    doc = {'rev': rev, 'rev7': rev7, 'query': a.query, 'k': a.k,
           'doc_scope_count': len(DS.doc_scope(rev)), 'section_count': len(sections),
           'shortlist': out}
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
    print(f'SHORTLIST_OK rev={rev7} k={a.k} candidates={len(out)}')


if __name__ == '__main__':
    main(sys.argv[1:])
