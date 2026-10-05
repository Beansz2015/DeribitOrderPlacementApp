"""tools/checks/lib/doc_sections.py -- the Jev doc re-ranker's section splitter
and the two M2 shortlist methods (BM25, grep-on-distinctive-terms).

PORTED 2026-10-06 from C:/Dev/DeribitVerdictEngine, tools/checks/lib/doc_sections.py at
engine f3455d0 (owner ruling: port, do not reach into the read-only engine repo).
The only behavioural change is SUPERSEDED below. Every "docs/..." citation in the rest of
this header names an ENGINE doc, not one in this repo.

SUPERSEDED (this repo only): the numbered handovers before docs/HANDOVER-6.md are
EXCLUDED from scope, not ranked. CLAUDE.md says do not read HANDOVER-3/-4/-5 because
docs/HANDOVER-4.md section 5 carries two runtime instructions that were later reversed;
HANDOVER.md and HANDOVER-2.md predate them. Ranking them would send a seat straight to
text it is told not to read. Archive docs (path contains 'archive') stay ranked and labelled.

Scope (docs/doc-reranker-measurement-plan.md section 3): all tracked docs/**/*.md
plus CLAUDE.md, read at a PINNED revision with `git show`, never the working tree
(same discipline as tools/checks/lib/doc_scanner_candidates.py's DS-D7 -- a doc
scanned from a moving working tree cannot be matched back to a query set that was
checked at a fixed rev).

SECTION ID: `path#heading-chain@rev7`. Split at markdown headings (H1-H6),
fence-aware -- a `#` inside a ``` or ~~~ fence is never a heading (same fence
tracking as doc_scanner_candidates.heading_chains). Each heading starts a new
section; the section's body is every line up to (not including) the next
heading, at any level. heading_chain is the ' > '-joined stack of headings in
force AT that section (doc_scanner's own construction), so a leaf section still
carries its ancestors' titles for matching and for the Jev state. The bytes
before the first heading form a preamble section with heading_chain ''.

`#` and `@` inside a heading chain are escaped in the ID string (they are the
ID's own field separators) -- display and matching only, not a URL.

ARCHIVE LABELLING: a path containing 'archive' (case-insensitive, matched against
the path only, e.g. docs/trader-tick-queue-archive.md, docs/history-archive.md)
is labelled is_archive=True. Rulings get archived verbatim (measurement plan
section 3), so archive sections are RANKED, never excluded -- only labelled, so
a re-ranker report can show the reader which hits are archival.

JEV STATE CAP: a state is the query plus ONE section (measurement plan section
3 -- never a whole doc). Some sections are still huge on their own (one
DeribitIndicatorProject.md section 15 table row alone runs past 9,000 tokens per
CLAUDE.md's own session-start protocol note). MAX_SECTION_CHARS caps the section
body handed to Jev; a cut is windowed around the START of the section (the
heading and its opening lines matter most for "does this answer the query") and
marked with an explicit "[...cut, N chars omitted...]" tail so a truncated
section is never silently passed off as whole.
"""
import re
import subprocess
import math
from collections import Counter
from functools import lru_cache

HERE = __import__('os').path.dirname(__import__('os').path.abspath(__file__))
REPO = __import__('os').path.normpath(__import__('os').path.join(HERE, '..', '..', '..'))

MAX_SECTION_CHARS = 20000  # ~8-9k tokens at the ~2.29 B/token rate measured in
                           # docs/doc-scanner-measurement-2026-09-22.md section 1;
                           # safely under Jev's 32k-token state limit alone.

STOPWORDS = set("""
a an the of to in on for is are was were be been being and or not no nor
this that these those it its it's as at by from with without into onto
what where when why how which who whom whose does did do doing done can
could would should will shall may might must than then so if but never
ever always about over under between across per via vs versus
""".split())


def git(*args):
    out = subprocess.run(['git', *args], cwd=REPO, capture_output=True, text=True,
                          encoding='utf-8', errors='replace')
    if out.returncode != 0:
        raise RuntimeError(f'git {list(args)} failed (exit {out.returncode}): {out.stderr}')
    return out.stdout


@lru_cache(None)
def rev_full(rev):
    out = git('rev-parse', '--verify', rev + '^{commit}').strip()
    if not re.fullmatch(r'[0-9a-f]{40}', out):
        raise SystemExit(f'REV_UNRESOLVED: {rev!r}')
    return out


@lru_cache(None)
def tree_files(rev):
    return set(git('ls-tree', '-r', '--name-only', rev).split('\n'))


@lru_cache(None)
def show(rev, path):
    out = subprocess.run(['git', 'show', f'{rev}:{path}'], cwd=REPO, capture_output=True,
                          text=True, encoding='utf-8', errors='replace')
    if out.returncode != 0:
        return ''
    return out.stdout


@lru_cache(None)
def commit_date_epoch(rev, path):
    """Last commit's committer-date (epoch seconds, UTC-derived) that touched `path`,
    reachable from `rev`. Used for 'newest ruling wins' ordering -- CODE orders by
    date, never Jev (measurement plan section 3: 'dates are text to Jev')."""
    out = git('log', '-1', '--format=%ct', rev, '--', path).strip()
    return int(out) if out else 0


SUPERSEDED = {'docs/HANDOVER.md', 'docs/HANDOVER-2.md', 'docs/HANDOVER-3.md',
              'docs/HANDOVER-4.md', 'docs/HANDOVER-5.md'}


def doc_scope(rev):
    """docs/**/*.md plus CLAUDE.md, tracked at `rev`, minus SUPERSEDED."""
    files = tree_files(rev)
    out = sorted(p for p in files if p not in SUPERSEDED and (p == 'CLAUDE.md' or
                 (p.startswith('docs/') and p.endswith('.md'))))
    return out


def is_archive(path):
    return 'archive' in path.lower()


HEADING_RX = re.compile(r'^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$')
FENCE_RX = re.compile(r'^\s{0,3}(```|~~~)')


def _esc(s):
    return s.replace('#', '＃').replace('@', '＠')


def split_sections(path, rev):
    """[{id, path, heading_chain, heading_text, level, start_line, end_line,
    text, is_archive}], 1-based inclusive start_line/end_line over the doc's
    lines. text is capped at MAX_SECTION_CHARS with a marked cut."""
    text = show(rev, path)
    if not text:
        return []
    rev7 = rev[:7]
    lines = text.splitlines()
    archive = is_archive(path)

    # Pass 1: find heading line indices (0-based) and the chain in force at each,
    # fence-aware -- identical tracking to doc_scanner_candidates.heading_chains,
    # duplicated here (not imported) because that module lives under
    # tools/checks/lib and is the doc-SCANNER's own candidate builder, a
    # different harness; a shared low-level fence/heading walk is small enough
    # that importing across harnesses would trade a few lines for a cross-harness
    # coupling neither spec asks for.
    fenced = False
    stack = []
    heading_at = {}  # 0-based line idx -> (level, title, chain_str)
    for i, ln in enumerate(lines):
        if FENCE_RX.match(ln):
            fenced = not fenced
            continue
        if fenced:
            continue
        m = HEADING_RX.match(ln)
        if m:
            lvl = len(m.group(1))
            title = m.group(2).strip()
            stack = [s for s in stack if s[0] < lvl] + [(lvl, title)]
            chain = ' > '.join(t for _, t in stack)
            heading_at[i] = (lvl, title, chain)

    heading_idxs = sorted(heading_at)
    sections = []

    def make(start_i, end_i, chain, title, level):
        # start_i, end_i are 0-based, end_i EXCLUSIVE
        body_lines = lines[start_i:end_i]
        body = '\n'.join(body_lines)
        truncated = False
        if len(body) > MAX_SECTION_CHARS:
            cut = len(body) - MAX_SECTION_CHARS
            body = body[:MAX_SECTION_CHARS] + f'\n[...cut, {cut} chars omitted...]'
            truncated = True
        sid = f'{path}#{_esc(chain)}@{rev7}'
        sections.append({
            'id': sid, 'path': path, 'heading_chain': chain, 'heading_text': title,
            'level': level, 'start_line': start_i + 1, 'end_line': end_i,
            'text': body, 'is_archive': archive, 'truncated': truncated,
        })

    if not heading_idxs:
        make(0, len(lines), '', '(preamble/no headings)', 0)
        return sections

    if heading_idxs[0] > 0:
        make(0, heading_idxs[0], '', '(preamble)', 0)

    for k, hi in enumerate(heading_idxs):
        lvl, title, chain = heading_at[hi]
        end = heading_idxs[k + 1] if k + 1 < len(heading_idxs) else len(lines)
        make(hi, end, chain, title, lvl)

    return sections


@lru_cache(None)
def all_sections(rev):
    """[(path, [sections])] over the whole doc scope, at rev."""
    r = rev_full(rev)
    out = []
    for p in doc_scope(r):
        out.append((p, split_sections(p, r)))
    return out


def flat_sections(rev):
    out = []
    for _, secs in all_sections(rev):
        out.extend(secs)
    return out


# -------------------------------------------------------------------------------------
# Tokenising, shared by both shortlist methods.
# -------------------------------------------------------------------------------------
TOKEN_RX = re.compile(r"[A-Za-z][A-Za-z0-9_'-]*|\d+(?:\.\d+)?")


def tokenize(s):
    return [t.lower() for t in TOKEN_RX.findall(s)]


def distinctive_terms(query):
    """The 'seat's own habit' input: query tokens with stopwords and 1-2 char
    tokens dropped, de-duplicated, order preserved."""
    seen = []
    for t in tokenize(query):
        if t in STOPWORDS or len(t) <= 2:
            continue
        if t not in seen:
            seen.append(t)
    return seen


# -------------------------------------------------------------------------------------
# Method 1: BM25 over section text (+ heading chain, weighted in -- a heading like
# "ATR thresholds" is exactly the signal a keyword search should catch even when
# the body prose paraphrases it).
# -------------------------------------------------------------------------------------
class BM25Index:
    def __init__(self, sections, k1=1.5, b=0.75):
        self.sections = sections
        self.k1, self.b = k1, b
        self.doc_tokens = []
        self.doc_len = []
        df = Counter()
        for s in sections:
            corpus_text = (s['heading_chain'] + ' ' + s['heading_chain'] + ' ' + s['text'])
            toks = tokenize(corpus_text)
            self.doc_tokens.append(toks)
            self.doc_len.append(len(toks))
            for t in set(toks):
                df[t] += 1
        n = len(sections)
        self.avgdl = (sum(self.doc_len) / n) if n else 0.0
        self.idf = {t: math.log(1 + (n - c + 0.5) / (c + 0.5)) for t, c in df.items()}
        self.tf = [Counter(toks) for toks in self.doc_tokens]

    def score(self, query_tokens):
        scores = [0.0] * len(self.sections)
        for i in range(len(self.sections)):
            dl = self.doc_len[i] or 1
            tf = self.tf[i]
            s = 0.0
            for t in query_tokens:
                if t not in tf:
                    continue
                idf = self.idf.get(t, 0.0)
                f = tf[t]
                s += idf * (f * (self.k1 + 1)) / (f + self.k1 * (1 - self.b + self.b * dl / (self.avgdl or 1)))
            scores[i] = s
        return scores

    def rank(self, query, k):
        q = tokenize(query)
        scores = self.score(q)
        order = sorted(range(len(self.sections)), key=lambda i: -scores[i])
        return [(self.sections[i], scores[i]) for i in order[:k]]


# -------------------------------------------------------------------------------------
# Method 2: grep-style rank on the query's distinctive terms -- score = count of
# DISTINCT distinctive terms present (case-insensitive whole-word) in heading_chain+
# text; tie-break by total occurrence count. This is the plain "grep and eyeball
# the hit count" habit, not a keyword-frequency model.
# -------------------------------------------------------------------------------------
def grep_rank(sections, query, k):
    terms = distinctive_terms(query)
    scored = []
    for s in sections:
        hay = (s['heading_chain'] + ' ' + s['text']).lower()
        distinct_hits = 0
        total_hits = 0
        for t in terms:
            c = len(re.findall(r'(?<![a-z0-9_])' + re.escape(t) + r'(?![a-z0-9_])', hay))
            if c:
                distinct_hits += 1
                total_hits += c
        scored.append((s, distinct_hits, total_hits))
    scored.sort(key=lambda x: (-x[1], -x[2]))
    return [(s, float(d)) for (s, d, _t) in scored[:k]]
