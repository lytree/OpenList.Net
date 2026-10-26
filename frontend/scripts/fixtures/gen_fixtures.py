#!/usr/bin/env python
"""生成 A–H 回归用的三套装置。

为什么不放在 `.e2e/`（那里是 gitignore 的运行现场）：装置一旦只存在于某台机器上，
「验收跑过」就成了无法复现的传闻 —— 本轮 M5 全量回归正好踩到这一点（§26 遗留 4 记录过）。
因此生成脚本入库，装置本身仍然落在仓库外的临时目录。

用法：
    python frontend/scripts/fixtures/gen_fixtures.py preview  /g/Share/ol-v2-prev
    python frontend/scripts/fixtures/gen_fixtures.py torrent  /g/Share/ol-v2-torrent
    python frontend/scripts/fixtures/gen_fixtures.py media    /g/Share/ol-v2-movies
    python frontend/scripts/fixtures/gen_fixtures.py media-manage /g/Share/ol-v2-media
    python frontend/scripts/fixtures/gen_fixtures.py ops      /g/Share/ol-v2-ops

依赖：openpyxl（xlsx）、python-docx（docx）、Pillow（海报/封面）、ffmpeg（真实可播的 mp4）。
后两者在 M5 的 runner 里由 `.e2e/venv` 提供，见 scripts/run-regression.sh。
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

# --------------------------------------------------------------------------- preview

def gen_preview(root: Path) -> None:
    """verify-preview.mjs 的装置（§23）。

    结构照脚本断言倒推：
      - 归档根层 **4 项**（3 目录 + hello.txt），目录排在文件前，`pics` 在最前
        → 目录名必须都排在 hello.txt 之前，且 pics 字典序最小；
      - `deep/a/b/c.txt` 里 a、b 是**隐式目录**（zip 里没有目录条目），
        用来验证「逐层补出隐式目录」；
      - `pics/cover.png` 走 `/ae?inner` 内联渲染，必须是真 PNG。
    """
    from PIL import Image  # noqa: PLC0415

    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)

    # --- bundle.zip ---
    png = root / "_cover.png"
    Image.new("RGB", (48, 32), (32, 122, 214)).save(png, format="PNG")

    with zipfile.ZipFile(root / "bundle.zip", "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("hello.txt", "hello from the archive fixture\n")
        # 只写文件、不写目录条目：a、b 必须由前端补出来
        zf.writestr("deep/a/b/c.txt", "deeply nested\n")
        zf.write(png, "pics/cover.png")
        zf.writestr("docs/readme.txt", "docs dir\n")
    png.unlink()

    # --- .url ---
    (root / "shortcut.url").write_text(
        "[InternetShortcut]\r\nURL=https://example.com/landing?x=1\r\n", encoding="utf-8"
    )

    # --- book.xlsx：两个工作表，第二个用来验切换 ---
    from openpyxl import Workbook  # noqa: PLC0415

    wb = Workbook()
    sheet = wb.active
    sheet.title = "Data"
    sheet["A1"] = "apple"
    sheet["B1"] = 42
    sheet["A2"] = "banana"
    sheet["B2"] = 7
    extra = wb.create_sheet("Extra")
    extra["A1"] = "second sheet"
    wb.save(root / "book.xlsx")

    # --- notes.docx ---
    from docx import Document  # noqa: PLC0415

    doc = Document()
    doc.add_heading("Fixture notes", level=1)
    doc.add_paragraph("docx preview smoke test")
    doc.add_paragraph("这条段落只用来确认正文被渲染出来。")
    doc.save(root / "notes.docx")

    # --- demo.cast（asciinema v2）---
    header = {"version": 2, "width": 80, "height": 24, "timestamp": 1730000000, "title": "M5 fixture"}
    events = [
        [0.1, "o", "$ echo hello\r\n"],
        [0.6, "o", "hello\r\n"],
        [1.0, "o", "$ "],
    ]
    lines = [json.dumps(header)] + [json.dumps(e, ensure_ascii=False) for e in events]
    (root / "demo.cast").write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"[preview] {root}: {sorted(p.name for p in root.iterdir())}")


# --------------------------------------------------------------------------- torrent

def bencode(value) -> bytes:
    if isinstance(value, int):
        return b"i" + str(value).encode() + b"e"
    if isinstance(value, str):
        value = value.encode()
    if isinstance(value, bytes):
        return str(len(value)).encode() + b":" + value
    if isinstance(value, list):
        return b"l" + b"".join(bencode(v) for v in value) + b"e"
    if isinstance(value, dict):
        out = [b"d"]
        for key in sorted(value, key=lambda k: k.encode() if isinstance(k, str) else k):
            out.append(bencode(key))
            out.append(bencode(value[key]))
        out.append(b"e")
        return b"".join(out)
    raise TypeError(f"cannot bencode {type(value)!r}")


PIECE = 16  # 必须小到让 80 B 的总量切出 5 片（断言里写死了 "5 × 16"）


def _pieces(payload: bytes) -> bytes:
    return b"".join(
        hashlib.sha1(payload[i : i + PIECE]).digest() for i in range(0, len(payload), PIECE)
    )


def _write_torrent(path: Path, info: dict, extra: dict) -> str:
    """写种子并返回 info_hash（对 info 字典的 *原始 bencoded 字节* 取 SHA-1）。"""
    meta = {"announce": "http://tracker.example.com/announce", **extra, "info": info}
    path.write_bytes(bencode(meta))
    return hashlib.sha1(bencode(info)).hexdigest()


def gen_torrent(root: Path) -> None:
    """verify-torrent.mjs 的装置（§26）。

    断言里写死了几个数字，装置必须精确对上：
      - 3 files / 5 pieces × 16 B（总量 80 B）/ Private / 2 trackers；
      - 树形「根层 2 个目录 + 展开 = 5 行」，且折叠 `pics` 只少 1 行
        → `pics` 里放 **1 个**文件、`sub` 里放 2 个，`pics` 字典序在前（断言要求首行是 pics）。
    """
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)

    # 80 字节：pics/cover.png 40 + sub/b.txt 20 + sub/c.txt 20
    cover = b"\x89PNG\r\n\x1a\n" + b"c" * 32
    text_b = b"torrent fixture body"
    text_c = b"another body........"
    assert len(cover) == 40 and len(text_b) == 20 and len(text_c) == 20, "长度与片数断言绑定"

    multi_name = "ol-v2-fixture"
    multi_files = [
        {"length": len(cover), "path": ["pics", "cover.png"]},
        {"length": len(text_b), "path": ["sub", "b.txt"]},
        {"length": len(text_c), "path": ["sub", "c.txt"]},
    ]
    multi_info = {
        "name": multi_name,
        "piece length": PIECE,
        "pieces": _pieces(cover + text_b + text_c),
        "private": 1,
        "files": multi_files,
    }
    multi_hash = _write_torrent(
        root / "bundle.torrent",
        multi_info,
        {
            "announce-list": [
                ["http://tracker.example.com/announce"],
                ["udp://tracker2.example.com:6969/announce"],
            ],
            "comment": "ol 种子预览装置",
            "created by": "ol-v2-fixture",
            "creation date": 1730000000,
        },
    )

    single_payload = b"single file payload.."
    single_info = {
        "name": "single-file.txt",
        "piece length": PIECE,
        "pieces": _pieces(single_payload),
        "length": len(single_payload),
    }
    single_hash = _write_torrent(
        root / "single.torrent",
        single_info,
        {"comment": "单文件装置", "created by": "ol-v2-fixture"},
    )

    (root / "notes.txt").write_text("not a torrent\n", encoding="utf-8")

    expected = {
        "multi": {
            "name": multi_name,
            "total": len(cover) + len(text_b) + len(text_c),
            "hash": multi_hash,
            "rows": 5,  # 2 个目录 + 各自展开的子文件
        },
        "single": {"name": "single-file.txt", "hash": single_hash, "rows": 1},
    }
    # expected.json 与装置分开放：装置要挂进 VFS，期望值不该出现在用户可见目录里
    out = Path(os.environ.get("EXPECT_OUT", str(root.parent / ".e2e-torrent")))
    out.mkdir(parents=True, exist_ok=True)
    (out / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print(f"[torrent] {root}: multi={multi_hash[:12]}… single={single_hash[:12]}… expected={out}")


# --------------------------------------------------------------------------- media

NFO = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>The Grand Budapest Hotel</title>
  <originaltitle>The Grand Budapest Hotel</originaltitle>
  <year>2014</year>
  <rating>8.1</rating>
  <plot>A legendary concierge at a famous European hotel between the wars.</plot>
  <genre>Comedy</genre>
  <genre>Drama</genre>
  <studio>Fox Searchlight</studio>
  <director>Wes Anderson</director>
  <actor><name>Ralph Fiennes</name><role>M. Gustave</role><order>0</order></actor>
</movie>
"""


def _ffmpeg_mp4(dest: Path, seconds: int = 30) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "ffmpeg", "-y", "-v", "error",
            "-f", "lavfi", "-i", f"testsrc=size=320x180:rate=15:duration={seconds}",
            "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", str(dest),
        ],
        check=True,
    )


def gen_media(root: Path) -> None:
    """verify-media.mjs 的装置（§27）。

    两个条目刻意不对称：
      - `Budapest/`：Kodi NFO + 海报 + 背景图 → 有标题/年份/评分/题材，图片端点 200；
      - `NoSidecar/`：只有一个 mp4 → 无海报（图片端点必须 404）、状态 Pending。
    目录名按字典序 Buddy → NoSidecar，对应脚本里写死的 item id 1 / 2。
    """
    from PIL import Image  # noqa: PLC0415

    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)

    buddy = root / "Budapest"
    buddy.mkdir()
    (buddy / "movie.nfo").write_text(NFO, encoding="utf-8")
    Image.new("RGB", (200, 300), (148, 96, 42)).save(buddy / "poster.jpg", format="JPEG")
    Image.new("RGB", (480, 270), (24, 24, 32)).save(buddy / "fanart.jpg", format="JPEG")
    # 30 秒是硬要求：验收会把 currentTime 拨到 15s 再断言进度落库，
    # 视频短于 15s 时浏览器会把 currentTime 夹到片尾，进度断言永远拿不到 10–20 的区间。
    _ffmpeg_mp4(buddy / "movie.mp4", seconds=30)

    no_sidecar = root / "NoSidecar"
    no_sidecar.mkdir()
    _ffmpeg_mp4(no_sidecar / "other.mp4", seconds=30)

    print(f"[media] {root}: {sorted(p.name for p in root.iterdir())}")


def gen_media_manage(root: Path) -> None:
    """verify-media-manage.mjs 的装置（§28）。

    脚本自己会在 `E2E Movie Night/fillers` 里铺 2000 个空 mp4，用来让 running 状态
    可被观测到；这里只补它不管的两件事：
      - 库根下**放一个真视频**（否则扫描只会聚合出 1 个条目，而断言要 2 个）；
      - 先清空目录，保证上一轮的 filler 不残留在计数里。
    """
    # 不清目录：这里迟早会有 2000 个 filler，整目录删除在受保护环境里会撞「批量删除需确认」，
    # 而脚本本身对残留是幂等的（filler 只补不足的部分）。
    (root / "E2E Movie Night").mkdir(parents=True, exist_ok=True)
    if not (root / "root-movie.mp4").exists():
        _ffmpeg_mp4(root / "root-movie.mp4", seconds=1)
    print(f"[media-manage] {root}: root video + empty filler dir")


# --------------------------------------------------------------------------- ops

def gen_ops(root: Path) -> None:
    """verify-ops.mjs 的装置（§22），逐字对齐它内联的 python 构造。

    为什么在这里再写一遍：`verify-ops` 会**重建并随后改写**这套装置，而 `verify-v2`
    要求的是「干净状态」。文档 §22.5 因此定了「先 v2 后 ops」的顺序 —— 那就必须先有一个
    能把装置恢复成干净状态的地方，否则第二条命令只能对着被改过的目录跑。
    """
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)
    for i in range(1, 41):
        (root / f"file-{i:02d}.txt").write_text(f"demo {i}\n", encoding="utf-8")
    (root / "子目录A").mkdir()
    (root / "子目录A" / "inner.txt").write_text("inner\n", encoding="utf-8")
    (root / "空目录B").mkdir()
    with zipfile.ZipFile(root / "bundle.zip", "w") as zf:
        zf.writestr("hello.txt", "hello from zip\n")
        zf.writestr("sub/second.txt", "nested\n")
    (root / "README.md").write_text("# ol-v2-ops\n\n操作集 e2e 的临时目录。\n", encoding="utf-8")
    print(f"[ops] {root}: {len(list(root.iterdir()))} entries")


# --------------------------------------------------------------------------- main

def main(argv: list[str]) -> int:
    if len(argv) != 3 or argv[1] not in {"preview", "torrent", "media", "media-manage", "ops"}:
        print(__doc__)
        return 2
    kind, target = argv[1], Path(argv[2])
    {
        "preview": gen_preview,
        "torrent": gen_torrent,
        "media": gen_media,
        "media-manage": gen_media_manage,
        "ops": gen_ops,
    }[kind](target)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
