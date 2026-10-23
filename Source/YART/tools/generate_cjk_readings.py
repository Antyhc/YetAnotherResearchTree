#!/usr/bin/env python3
"""
Unihan_Readings.txt -> 검색용 CJK 읽기 테이블

  - 병음: kMandarin 첫 항목에서 성조 제거. 예) 電 -> dian
  - 음독: kJapaneseOn 첫 항목을 소문자로. 예) 電 -> den

런타임은 활성 언어 쪽만 로드하므로 같은 한자가 언어별로 충돌하지 않음

출력: Resources/cjk_readings.txt(.gz)
  형식(탭 구분, 헤더 1줄): "<hexCodepoint>\t<pinyin or '-'>\t<on or '-'>"
  둘 다 비면 줄 생략

사용:
  python generate_cjk_readings.py <Unihan_Readings.txt> <out_dir>
"""
import sys, os, unicodedata, gzip

def strip_tones(s: str) -> str:
    # NFD로 성조 표시 제거. ü도 u가 되지만 검색용 근사라 괜찮음
    nfd = unicodedata.normalize("NFD", s)
    base = "".join(c for c in nfd if not unicodedata.combining(c))
    return base.lower()

def is_ascii_latin(s: str) -> bool:
    return all(("a" <= c <= "z") for c in s)

def main():
    if len(sys.argv) != 3:
        print("usage: generate_cjk_readings.py <Unihan_Readings.txt> <out_dir>")
        sys.exit(1)
    src, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    pinyin = {}  # cp(int) -> str
    on = {}      # cp(int) -> str

    with open(src, encoding="utf-8") as f:
        for line in f:
            if not line.startswith("U+"):
                continue
            parts = line.rstrip("\n").split("\t")
            if len(parts) != 3:
                continue
            cp_hex, field, val = parts
            cp = int(cp_hex[2:], 16)
            first = val.split()[0] if val.split() else ""
            if not first:
                continue
            if field == "kMandarin":
                p = strip_tones(first)
                if p and is_ascii_latin(p):
                    pinyin[cp] = p
            elif field == "kJapaneseOn":
                o = first.lower()
                if o and is_ascii_latin(o):
                    on[cp] = o

    cps = sorted(set(pinyin) | set(on))
    out_txt = os.path.join(out_dir, "cjk_readings.txt")
    lines = ["# YART CJK readings (Unihan kMandarin/kJapaneseOn). cp<TAB>pinyin<TAB>on  ('-'=none)"]
    for cp in cps:
        lines.append(f"{cp:X}\t{pinyin.get(cp, '-')}\t{on.get(cp, '-')}")
    data = ("\n".join(lines) + "\n").encode("utf-8")

    with open(out_txt, "wb") as f:
        f.write(data)
    with gzip.open(out_txt + ".gz", "wb", compresslevel=9) as f:
        f.write(data)

    print(f"pinyin entries: {len(pinyin)}")
    print(f"on entries:     {len(on)}")
    print(f"rows:           {len(cps)}")
    print(f"txt bytes:      {len(data)}")
    print(f"gz bytes:       {os.path.getsize(out_txt + '.gz')}")
    # 샘플 출력. 콘솔이 CJK를 못 찍어도 죽지 않게
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    for ch in "電気电气研究機械":
        cp = ord(ch)
        print(f"  U+{cp:04X}  pinyin={pinyin.get(cp,'-')}  on={on.get(cp,'-')}")

if __name__ == "__main__":
    main()
