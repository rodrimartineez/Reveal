#!/usr/bin/env python
"""
Builds Reveal's offline lexicon (data/build/lexicon.db) and the language-id model (data/build/langid.json)
from free, openly licensed sources:

  * Wiktionary (via kaikki.org/wiktextract)        CC BY-SA 4.0   translations, synonyms, examples, IPA, inflections
  * Open English WordNet 2025                       CC BY 4.0      synonyms, examples
  * Tatoeba sentence pairs (manythings.org mirror)  CC BY 2.0 FR   example sentences, language-id training data
  * FrequencyWords (OpenSubtitles 2018)             CC BY-SA 4.0   word frequencies used for ranking / language detection

Run `python tools/lexicon/fetch_sources.py` first, then `python tools/lexicon/build_lexicon.py`.
The output is bundled with the application; nothing here runs on the user's machine.
"""
from __future__ import annotations

import collections
import gzip
import json
import math
import re
import sqlite3
import sys
import time
import unicodedata
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "data" / "raw"
OUT = ROOT / "data" / "build"
OUT.mkdir(parents=True, exist_ok=True)

SKIP_SENSE_TAGS = {"obsolete", "archaic", "dialectal", "nonstandard", "rare", "dated", "historical", "slang",
                   "vulgar", "offensive", "derogatory", "ethnic-slur", "error-lemma", "misspelling"}
SKIP_POS = {"character", "symbol", "punct", "suffix", "prefix", "infix", "circumfix", "interfix", "affix",
            "combining_form", "contraction", "num"}
SKIP_EXAMPLE_TYPES = {"quotation"}


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def strip_diacritics(s: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFD", s) if unicodedata.category(c) != "Mn")


def tokens(text: str):
    return re.findall(r"[A-Za-zÀ-ÿ]+(?:['’][A-Za-z]+)?", text)


# --------------------------------------------------------------------------------------------------
# Frequencies
# --------------------------------------------------------------------------------------------------
def load_freq(path: Path, limit: int):
    ppm = {}
    total = 0
    rows = []
    with open(path, encoding="utf8") as f:
        for line in f:
            parts = line.split()
            if len(parts) != 2:
                continue
            w, c = parts[0], int(parts[1])
            rows.append((w, c))
            total += c
    for w, c in rows[:limit]:
        ppm[w] = c * 1_000_000 / total
    return ppm


# --------------------------------------------------------------------------------------------------
# Kaikki (Wiktionary)
# --------------------------------------------------------------------------------------------------
class Entry:
    __slots__ = ("pos", "senses", "tr_top", "ipa_us", "ipa_uk", "syn_top")

    def __init__(self, pos):
        self.pos = pos
        self.senses = []     # list of dicts {tr, syn, ex}
        self.tr_top = []
        self.syn_top = []
        self.ipa_us = None
        self.ipa_uk = None


def clean_tr(w: str):
    w = w.strip()
    w = re.sub(r"\s*\(.*?\)\s*", " ", w).strip()
    if not w or len(w) > 40 or any(ch.isdigit() for ch in w) or "[" in w or "/" in w:
        return None
    if len(w.split()) > 4:
        return None
    return w


def valid_example(text: str) -> bool:
    t = text.strip()
    n = len(t.split())
    if n < 3 or n > 22 or len(t) > 150:
        return False
    if not t[0].isupper() or t[-1] not in ".!?\"'":
        return False
    if "…" in t or "[" in t or "\n" in t:
        return False
    return True


def parse_kaikki(path: Path):
    entries = collections.defaultdict(list)   # word -> [Entry]
    form2lemma = {}
    lemma_forms = collections.defaultdict(set)
    n = 0
    t0 = time.time()
    with open(path, encoding="utf8") as f:
        for line in f:
            n += 1
            if n % 200000 == 0:
                log(f"kaikki {n:,} lines ({time.time() - t0:.0f}s)")
            d = json.loads(line)
            word = d.get("word")
            pos = d.get("pos", "")
            if not word or pos in SKIP_POS:
                continue
            if len(word) > 40 or any(ch.isdigit() for ch in word):
                continue

            senses = d.get("senses", [])
            # inflection / alternative form entries -> lemma mapping only
            form_senses = [s for s in senses if s.get("form_of") or s.get("alt_of")]
            if senses and len(form_senses) == len(senses):
                tgt = None
                for s in form_senses:
                    lst = s.get("form_of") or s.get("alt_of")
                    if lst:
                        tgt = lst[0].get("word")
                        break
                if tgt and tgt != word and not any(
                        t in ("misspelling", "obsolete", "archaic", "nonstandard", "dialectal", "eye-dialect")
                        for s in form_senses for t in (s.get("tags") or [])):
                    form2lemma.setdefault(word, tgt)
                continue

            e = Entry(pos)
            for s in senses:
                if s.get("form_of") or s.get("alt_of"):
                    continue
                tags = set(s.get("tags") or []) | set(s.get("raw_tags") or [])
                if tags & SKIP_SENSE_TAGS:
                    continue
                tr = []
                for t in s.get("translations", []):
                    if t.get("lang_code") == "es":
                        c = clean_tr(t.get("word", ""))
                        if c:
                            tr.append(c)
                syn = [x["word"] for x in s.get("synonyms", []) if x.get("word")]
                ex = []
                for x in s.get("examples", []):
                    if x.get("type") in SKIP_EXAMPLE_TYPES or "ref" in x:
                        continue
                    txt = (x.get("text") or "").strip()
                    if txt:
                        ex.append(txt)
                e.senses.append({"tr": tr, "syn": syn, "ex": ex})
            for t in d.get("translations", []):
                if t.get("lang_code") == "es":
                    c = clean_tr(t.get("word", ""))
                    if c:
                        e.tr_top.append((c, t.get("sense", "")))
            e.syn_top = [x["word"] for x in d.get("synonyms", []) if x.get("word")]
            for snd in d.get("sounds", []):
                ipa = snd.get("ipa")
                if not ipa:
                    continue
                tags = set(snd.get("tags") or [])
                if ("US" in tags or "General-American" in tags) and e.ipa_us is None:
                    e.ipa_us = ipa
                elif ("UK" in tags or "Received-Pronunciation" in tags) and e.ipa_uk is None:
                    e.ipa_uk = ipa
                elif not tags:
                    if e.ipa_us is None:
                        e.ipa_us = ipa
                    if e.ipa_uk is None:
                        e.ipa_uk = ipa
            for fm in d.get("forms", []):
                form = fm.get("form")
                tags = set(fm.get("tags") or [])
                if not form or form == word or form == "-" or len(form) > 40:
                    continue
                if tags & {"table-tags", "inflection-template", "class", "romanization", "obsolete", "archaic"}:
                    continue
                if form.startswith(("more ", "most ")):
                    continue
                form2lemma.setdefault(form, word)
                lemma_forms[word].add(form)
            if e.senses or e.tr_top:
                entries[word].append(e)
    log(f"kaikki done: {n:,} lines, {len(entries):,} headwords, {len(form2lemma):,} forms")
    return entries, form2lemma, lemma_forms


# --------------------------------------------------------------------------------------------------
# WordNet
# --------------------------------------------------------------------------------------------------
def parse_wordnet(path: Path):
    log("parsing WordNet")
    syn_members = {}      # synset id -> [lemma]
    syn_examples = {}
    syn_similar = collections.defaultdict(list)
    word_synsets = collections.defaultdict(list)    # (lemma, pos) -> [synset ids] in sense order
    with gzip.open(path, "rb") as f:
        for _, el in ET.iterparse(f, events=("end",)):
            tag = el.tag
            if tag == "LexicalEntry":
                lemma_el = el.find("Lemma")
                lemma = lemma_el.get("writtenForm")
                pos = lemma_el.get("partOfSpeech")
                for s in el.findall("Sense"):
                    sid = s.get("synset")
                    word_synsets[(lemma, pos)].append(sid)
                    syn_members.setdefault(sid, []).append(lemma)
                el.clear()
            elif tag == "Synset":
                sid = el.get("id")
                ex = [e.text.strip() for e in el.findall("Example") if e.text]
                if ex:
                    syn_examples[sid] = ex
                for r in el.findall("SynsetRelation"):
                    if r.get("relType") in ("similar", "also"):
                        syn_similar[sid].append(r.get("target"))
                el.clear()
    log(f"WordNet: {len(word_synsets):,} lemma/pos pairs")
    return syn_members, syn_examples, syn_similar, word_synsets


# --------------------------------------------------------------------------------------------------
# Tatoeba
# --------------------------------------------------------------------------------------------------
def load_tatoeba():
    z = zipfile.ZipFile(RAW / "spa-eng.zip")
    pairs = []
    for line in z.read("spa.txt").decode("utf8").splitlines():
        parts = line.split("\t")
        if len(parts) >= 2:
            pairs.append((parts[0].strip(), parts[1].strip()))
    log(f"tatoeba pairs: {len(pairs):,}")
    return pairs


def score_sentence(s: str) -> float:
    words = s.split()
    n = len(words)
    if n < 5 or n > 16 or len(s) > 110:
        return -1
    if any(ch.isdigit() for ch in s) or '"' in s or "(" in s or ":" in s or ";" in s:
        return -1
    if not s[0].isupper() or s[-1] not in ".?!":
        return -1
    score = 10 - abs(n - 9) * 0.7
    if re.search(r"\b(Tom|Mary|John|Tom's|Mary's)\b", s):
        score -= 1.5
    if s.endswith("?"):
        score -= 1
    return score


def build_langid(pairs):
    log("training language id")
    counts = {"en": collections.Counter(), "es": collections.Counter()}
    for en, es in pairs:
        for lang, s in (("en", en), ("es", es)):
            for w in tokens(s):
                w = "_" + w.lower() + "_"
                for n in (1, 2, 3):
                    for i in range(len(w) - n + 1):
                        counts[lang][w[i:i + n]] += 1
    model = {}
    vocab = set()
    for lang, c in counts.items():
        for g, v in c.items():
            if v >= 8:
                vocab.add(g)
    for lang, c in counts.items():
        total = sum(v for g, v in c.items() if g in vocab) + len(vocab)
        model[lang] = {g: round(math.log((c.get(g, 0) + 1) / total), 3) for g in vocab if c.get(g, 0) > 0}
        model[lang]["_unseen"] = round(math.log(1 / total), 3)
    (OUT / "langid.json").write_text(json.dumps(model, ensure_ascii=False, separators=(",", ":")), encoding="utf8")
    log(f"langid grams: {len(vocab):,}")


# --------------------------------------------------------------------------------------------------
# Build
# --------------------------------------------------------------------------------------------------
def main():
    ppm_en = load_freq(RAW / "en_full.txt", 400_000)
    ppm_es = load_freq(RAW / "es_full.txt", 400_000)
    log(f"freq: en {len(ppm_en):,} es {len(ppm_es):,}")

    def logf(table, w):
        w = w.lower()
        if w in table:
            return max(-1.0, math.log10(table[w] + 0.001))
        parts = w.split()
        if len(parts) > 1:   # phrases: average of the words, slightly penalised
            return sum(max(-1.0, math.log10(table.get(p, 0) + 0.001)) for p in parts) / len(parts) - 0.5
        return -1.0

    def fe(w):   # english log-frequency
        return logf(ppm_en, w)

    def fs(w):
        return logf(ppm_es, w)

    entries, form2lemma, lemma_forms = parse_kaikki(RAW / "kaikki-English.jsonl")
    syn_members, syn_examples, syn_similar, word_synsets = parse_wordnet(RAW / "english-wordnet-2025.xml.gz")
    pairs = load_tatoeba()
    build_langid(pairs)

    # ---- example sentence index from Tatoeba --------------------------------------------------
    multiword = {}   # lowercase surface form -> headword (for phrases / phrasal verbs)
    for w, es in entries.items():
        if " " in w and len(w.split()) <= 4:
            multiword[w.lower()] = w
            for f in lemma_forms.get(w, ()):
                if " " in f:
                    multiword.setdefault(f.lower(), w)
    cand = collections.defaultdict(list)   # headword (lower) -> [(score, sentence)]
    for en, _es in pairs:
        sc = score_sentence(en)
        if sc < 0:
            continue
        toks = [t.lower() for t in tokens(en)]
        seen = set()
        for i, t in enumerate(toks):
            lemma = form2lemma.get(t, t)
            for key in {t, lemma}:
                if key not in seen:
                    seen.add(key)
                    cand[key].append((sc, en))
            for n in (2, 3, 4):
                if i + n <= len(toks):
                    ph = " ".join(toks[i:i + n])
                    hw = multiword.get(ph)
                    if hw and hw.lower() not in seen:
                        seen.add(hw.lower())
                        cand[hw.lower()].append((sc + 1, en))
    tatoeba_best = {}
    for k, lst in cand.items():
        lst.sort(key=lambda x: -x[0])
        tatoeba_best[k] = lst[0][1]
    log(f"tatoeba examples for {len(tatoeba_best):,} keys")

    # ---- WordNet synonym candidates per lemma ---------------------------------------------------
    pos_map = {"noun": "n", "verb": "v", "adj": "a", "adv": "r"}

    def wordnet_for(word: str, pos: str | None):
        poses = [pos_map[pos]] if pos in pos_map else ["n", "v", "a", "r"]
        syns = []   # (score, lemma)
        exs = []
        lowered = word.lower()
        for p in poses:
            sids = word_synsets.get((word, p)) or word_synsets.get((lowered, p)) or []
            for rank, sid in enumerate(sids[:2]):
                for m in syn_members.get(sid, []):
                    if m.lower() != lowered:
                        syns.append((1.0 - rank * 0.6, m))
                if rank == 0:
                    for tgt in syn_similar.get(sid, [])[:3]:
                        for m in syn_members.get(tgt, [])[:3]:
                            if m.lower() != lowered:
                                syns.append((0.4, m))
                for e in syn_examples.get(sid, [])[:2]:
                    exs.append(e)
        return syns, exs

    # ---- assemble English rows ------------------------------------------------------------------
    db_path = OUT / "lexicon.db"
    if db_path.exists():
        db_path.unlink()
    con = sqlite3.connect(db_path)
    con.executescript("""
        PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;
        CREATE TABLE en(word TEXT PRIMARY KEY, pos TEXT, ipa_us TEXT, ipa_uk TEXT, tr TEXT, syn TEXT, ex TEXT, def TEXT) WITHOUT ROWID;
        CREATE TABLE forms(form TEXT PRIMARY KEY, lemma TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE es(key TEXT PRIMARY KEY, nd TEXT, en TEXT) WITHOUT ROWID;
        CREATE TABLE freq(lang TEXT NOT NULL, word TEXT NOT NULL, ppm REAL NOT NULL, PRIMARY KEY(lang, word)) WITHOUT ROWID;
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
    """)

    rows = []
    reverse = collections.defaultdict(dict)  # es -> en -> (best contribution, sources)
    all_words = set(entries.keys())
    # words only present in WordNet also get a row (synonyms / examples without Wiktionary data)
    for (lemma, p), _ in word_synsets.items():
        if lemma not in all_words and lemma.lower() not in all_words and len(lemma) <= 40:
            all_words.add(lemma)

    for word in all_words:
        es_entries = entries.get(word, [])
        primary = next((e for e in es_entries if any(s["tr"] for s in e.senses) or e.tr_top), None) \
            or (es_entries[0] if es_entries else None)
        pos = primary.pos if primary else None

        # translations ----------------------------------------------------------------------------
        # Score = Spanish frequency, minus a penalty that grows with how far down the Wiktionary entry the
        # sense is (the first senses are the core meanings), minus a penalty for other etymologies / POS.
        tr_scores = {}

        def add_tr(t, order, foreign):
            sc = 0.5 * fs(t) - 2.0 * order - (1.5 if foreign else 0)
            if sc > tr_scores.get(t, -99):
                tr_scores[t] = sc

        primary_has_tr = primary is not None and (any(sd["tr"] for sd in primary.senses) or primary.tr_top)
        for e in es_entries:
            foreign = e is not primary
            if foreign and primary_has_tr and e.pos != primary.pos:
                continue    # other parts of speech would pollute ("book" -> "sacar")
            # Entry-level translation tables are the curated, comprehensive lists: their groups come first,
            # sense-level lists (often idiosyncratic) rank after them. Order = position among the lists that exist.
            groups = {}
            for t, sense in e.tr_top:
                add_tr(t, groups.setdefault(sense, len(groups)), foreign)
            k = len(groups)
            for sd in e.senses:
                if sd["tr"]:
                    for t in sd["tr"]:
                        add_tr(t, k, foreign)
                    k += 1
        ranked_tr = [t for t, _ in sorted(tr_scores.items(), key=lambda kv: -kv[1])]
        # very rare / regional leftovers ("casalicio", "rezongón") only survive when they're among the first two
        tr = [t for i, t in enumerate(ranked_tr) if i == 0 or ppm_es.get(t.lower(), 0) >= 0.02 or " " in t][:6]

        # reverse index (Spanish -> English): a word that is the first translation of an English entry is a strong hit
        is_name = primary is not None and primary.pos == "name"
        for pos_i, t in enumerate(tr):
            if is_name and t.lower() != word.lower():
                continue
            contribution = 1.0 / (1.0 + 0.6 * pos_i) * (0.5 if " " in t else 1.0)
            cur = reverse[t.lower()].get(word, (0.0, 0))
            reverse[t.lower()][word] = (max(cur[0], contribution), cur[1] + 1)

        # synonyms ----------------------------------------------------------------------------------
        # Only the primary entry's first senses count: synonyms of rare senses ("bank" -> "row", "grid") are noise.
        cand_syn = collections.defaultdict(lambda: [0.0, 0])   # word -> [weight, number of sources]

        def add_syn(x, weight):
            c = cand_syn[x]
            c[0] = max(c[0], weight)
            c[1] += 1

        if primary:
            for si, sd in enumerate(primary.senses[:2]):
                for x in sd["syn"]:
                    add_syn(x, 1.0 if si == 0 else 0.5)
            for x in primary.syn_top:
                add_syn(x, 0.6)
        wn_syns, wn_ex = wordnet_for(word, pos)
        for wsc, x in wn_syns:
            add_syn(x, wsc)

        syns = []
        scored = []
        for x, (weight, nsrc) in cand_syn.items():
            xl = x.lower()
            if xl == word.lower() or len(x) > 30 or len(x.split()) > 3 or any(ch.isdigit() for ch in x):
                continue
            if word[:1].islower() and x[:1].isupper():
                continue
            if "'" in x and "'" not in word:
                continue
            f = ppm_en.get(xl, 0)
            if f < 0.03 and " " not in x:
                continue
            scored.append((max(-1.5, math.log10(f + 0.001)) * 1.0 + weight * 1.6 + (0.9 if nsrc > 1 else 0), x))
        for _, x in sorted(scored, reverse=True):
            if x.lower() not in {y.lower() for y in syns}:
                syns.append(x)
            if len(syns) >= 6:
                break

        # example -----------------------------------------------------------------------------------
        example = None
        for e in ([primary] if primary else []) + [x for x in es_entries if x is not primary]:
            for s in e.senses:
                for x in s["ex"]:
                    if valid_example(x):
                        example = x
                        break
                if example:
                    break
            if example:
                break
        if not example:
            example = tatoeba_best.get(word.lower())
        if not example:
            for x in wn_ex:
                if 3 <= len(x.split()) <= 22 and x[0].isalpha():
                    example = x[0].upper() + x[1:]
                    break

        definition = None
        if not (tr or syns or example) and not primary:
            continue
        if not (tr or syns or example):
            continue
        rows.append((word, pos, primary.ipa_us if primary else None, primary.ipa_uk if primary else None,
                     "|".join(tr), "|".join(syns), example, definition))

    con.executemany("INSERT INTO en VALUES (?,?,?,?,?,?,?,?)", rows)
    log(f"en rows: {len(rows):,}")

    rows_f = [(f, l) for f, l in form2lemma.items() if f != l and f.lower() != l.lower()]
    con.executemany("INSERT OR IGNORE INTO forms VALUES (?,?)", rows_f)
    log(f"forms: {len(rows_f):,}")

    rev_rows = []
    for es_word, ens in reverse.items():
        ranked = sorted(ens.items(), key=lambda kv: -(fe(kv[0]) * 0.8 + 1.5 * kv[1][0] + 0.3 * (kv[1][1] - 1)))
        rev_rows.append((es_word, strip_diacritics(es_word), "|".join(w for w, _ in ranked[:6])))
    con.executemany("INSERT OR IGNORE INTO es VALUES (?,?,?)", rev_rows)
    con.execute("CREATE INDEX es_nd ON es(nd)")
    log(f"es rows: {len(rev_rows):,}")

    fr = [("en", w, v) for w, v in list(ppm_en.items())[:250_000]] + [("es", w, v) for w, v in list(ppm_es.items())[:250_000]]
    con.executemany("INSERT OR IGNORE INTO freq VALUES (?,?,?)", fr)
    con.execute("INSERT INTO meta VALUES ('version','1')")
    con.execute("INSERT INTO meta VALUES ('built', ?)", (time.strftime("%Y-%m-%d"),))
    con.commit()
    con.execute("VACUUM")
    con.close()
    log(f"done -> {db_path} ({db_path.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    sys.exit(main())
