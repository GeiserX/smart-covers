#!/usr/bin/env python3
"""Regenerates the comic-archive test fixtures in this directory.

Run from this directory:  python3 make-fixtures.py

Fixture contract (tests assert on these exact byte sizes):
  page-2.jpg   = 6144 bytes, JPEG magic  -> the expected cover (natural sort winner)
  page-10.jpg  = 8192 bytes, JPEG magic  -> the ordinal-sort trap (sorts before page-2 ordinally)
  zpage-3.png  = 7000 bytes, PNG magic
  ComicInfo.xml / Thumbs.db              -> non-image junk that must be skipped

Archives produced:
  first-page-rar4.cbr  hand-crafted stored RAR4 (rar 7.x cannot author RAR4 anymore;
                       the writer below emits spec-conformant RAR 1.5-format blocks,
                       verified with `unrar t`) - entries: page-2.jpg, page-10.jpg
  first-page-rar5.cbr  RAR5 via `rar a -ep -m5`  - page-2.jpg, page-10.jpg, zpage-3.png, Thumbs.db
  solid-rar5.cbr       RAR5 solid via `rar a -s` - ComicInfo.xml, page-2.jpg, page-10.jpg
  zip-as-cbr.cbr       ZIP renamed to .cbr (mislabeling is common in the wild)
  rar-as-cbz.cbz       RAR5 renamed to .cbz

Tooling: `rar` CLI (Homebrew cask `rar`; any WinRAR >= 5 works for the RAR5 set)
and `zip`. The RAR4 fixture needs no external tool.
"""

import os
import shutil
import struct
import subprocess
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))


def make_jpeg(path: str, size: int) -> None:
    header = bytes([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]) + b"JFIF\x00"
    body = os.urandom(size - len(header) - 2)
    with open(path, "wb") as f:
        f.write(header + body + bytes([0xFF, 0xD9]))


def make_png(path: str, size: int) -> None:
    header = bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
    with open(path, "wb") as f:
        f.write(header + os.urandom(size - len(header)))


# --- minimal RAR4 writer (stored entries only) -------------------------------

def _hdr_crc(body: bytes) -> bytes:
    return struct.pack("<H", zlib.crc32(body) & 0xFFFF)


def _rar4_file_block(name: bytes, data: bytes) -> bytes:
    head_size = 32 + len(name)
    dos_date = ((2026 - 1980) << 9) | (7 << 5) | 3   # 2026-07-03
    ftime = (dos_date << 16) | (12 << 11)             # 12:00:00
    body = struct.pack(
        "<BHH IIB IIBB H I",
        0x74,                              # HEAD_TYPE: file
        0x8000,                            # HEAD_FLAGS: long block (data follows)
        head_size,
        len(data),                         # PACK_SIZE (stored => == UNP_SIZE)
        len(data),                         # UNP_SIZE
        2,                                 # HOST_OS: Windows
        zlib.crc32(data) & 0xFFFFFFFF,     # FILE_CRC
        ftime,
        20,                                # UNP_VER: 2.0
        0x30,                              # METHOD: stored
        len(name),
        0x20,                              # ATTR: archive
    ) + name
    return _hdr_crc(body) + body + data


def write_rar4(archive_path: str, files: list[str]) -> None:
    marker = b"Rar!\x1a\x07\x00"
    main_body = struct.pack("<BHH HI", 0x73, 0x0000, 13, 0, 0)
    term_body = struct.pack("<BHH", 0x7B, 0x4000, 7)
    out = marker + _hdr_crc(main_body) + main_body
    for fpath in files:
        with open(fpath, "rb") as f:
            out += _rar4_file_block(os.path.basename(fpath).encode(), f.read())
    out += _hdr_crc(term_body) + term_body
    with open(archive_path, "wb") as f:
        f.write(out)


# --- build everything ---------------------------------------------------------

def main() -> None:
    src = tempfile.mkdtemp(prefix="cbr-fixture-src-")
    try:
        page2 = os.path.join(src, "page-2.jpg")
        page10 = os.path.join(src, "page-10.jpg")
        zpage3 = os.path.join(src, "zpage-3.png")
        comicinfo = os.path.join(src, "ComicInfo.xml")
        thumbs = os.path.join(src, "Thumbs.db")

        make_jpeg(page2, 6144)
        make_jpeg(page10, 8192)
        make_png(zpage3, 7000)
        with open(comicinfo, "w") as f:
            f.write("<?xml version='1.0'?><ComicInfo><Title>Fixture</Title></ComicInfo>")
        with open(thumbs, "wb") as f:
            f.write(b"\x00" * 128)

        def rar(out_name: str, files: list[str], switches: list[str] | None = None) -> None:
            out = os.path.join(HERE, out_name)
            if os.path.exists(out):
                os.remove(out)
            # rar syntax: rar a <switches> <archive> <files...>
            subprocess.run(["rar", "a", "-ep", "-m5", "-idq", *(switches or []), out, *files],
                           cwd=src, check=True)

        rar("first-page-rar5.cbr", ["page-2.jpg", "page-10.jpg", "zpage-3.png", "Thumbs.db"])
        rar("solid-rar5.cbr", ["ComicInfo.xml", "page-2.jpg", "page-10.jpg"], switches=["-s"])
        rar("rar-as-cbz.cbz", ["page-2.jpg", "page-10.jpg"])

        zip_out = os.path.join(HERE, "zip-as-cbr.cbr")
        if os.path.exists(zip_out):
            os.remove(zip_out)
        subprocess.run(["zip", "-q", "-X", zip_out,
                        "page-2.jpg", "page-10.jpg", "ComicInfo.xml"],
                       cwd=src, check=True)

        write_rar4(os.path.join(HERE, "first-page-rar4.cbr"), [page2, page10])

        subprocess.run(["unrar", "t", os.path.join(HERE, "first-page-rar4.cbr")], check=True)
        print("fixtures regenerated OK")
    finally:
        shutil.rmtree(src, ignore_errors=True)


if __name__ == "__main__":
    main()
