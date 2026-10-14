#!/usr/bin/env python
"""
Downloads the open datasets used to build Reveal's offline lexicon into data/raw (about 3.4 GB, one time).
Run it, then run build_lexicon.py. Everything is free and openly licensed (see README, section "Datos").
"""
import sys
import urllib.request
from pathlib import Path

RAW = Path(__file__).resolve().parents[2] / "data" / "raw"
RAW.mkdir(parents=True, exist_ok=True)

SOURCES = {
    "english-wordnet-2025.xml.gz": "https://github.com/globalwordnet/english-wordnet/releases/download/2025-edition/english-wordnet-2025.xml.gz",
    "spa-eng.zip": "https://www.manythings.org/anki/spa-eng.zip",
    "en_full.txt": "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_full.txt",
    "es_full.txt": "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/es/es_full.txt",
    "kaikki-English.jsonl": "https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl",
}


def download(name: str, url: str) -> None:
    target = RAW / name
    if target.exists() and target.stat().st_size > 0:
        print(f"[skip] {name} already downloaded")
        return
    print(f"[get ] {name}  <-  {url}")
    tmp = target.with_suffix(target.suffix + ".part")
    with urllib.request.urlopen(url) as response, open(tmp, "wb") as out:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        while chunk := response.read(1 << 20):
            out.write(chunk)
            done += len(chunk)
            if total:
                print(f"\r       {done / 1e6:8.1f} / {total / 1e6:.1f} MB", end="", flush=True)
    tmp.replace(target)
    print()


if __name__ == "__main__":
    for n, u in SOURCES.items():
        download(n, u)
    print("done. Next: python tools/lexicon/build_lexicon.py")
    sys.exit(0)
