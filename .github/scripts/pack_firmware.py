import argparse
import csv
import datetime
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENVS = {"cyd": "ILI9341", "cyd2usb": "ST7789"}
BOOTLOADER_OFFSET = 0x1000
PARTITIONS_OFFSET = 0x8000


def app_offset(partitions_csv: Path) -> int:
    with partitions_csv.open(encoding="ascii") as f:
        for row in csv.reader(line for line in f if line.strip() and not line.lstrip().startswith("#")):
            cells = [c.strip() for c in row]
            if len(cells) >= 4 and cells[1] == "app":
                return int(cells[3], 0)
    raise SystemExit(f"Không thấy phân vùng app trong {partitions_csv}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="ví dụ 2.0.0 (không có chữ v)")
    ap.add_argument("--build-dir", default=str(ROOT / "firmware" / ".pio" / "build"))
    ap.add_argument("--out", default=str(ROOT / "dist" / "firmware"))
    args = ap.parse_args()

    build_dir = Path(args.build_dir)
    out = Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    offsets = {
        "bootloader.bin": BOOTLOADER_OFFSET,
        "partitions.bin": PARTITIONS_OFFSET,
        "firmware.bin": app_offset(ROOT / "firmware" / "partitions.csv"),
    }

    builds = {}
    for env, panel in ENVS.items():
        (out / env).mkdir(parents=True)
        parts = []
        for name, offset in offsets.items():
            src = build_dir / env / name
            if not src.is_file():
                raise SystemExit(f"Thiếu {src}: chạy 'pio run -e {env}' trước")
            data = src.read_bytes()
            shutil.copyfile(src, out / env / name)
            parts.append({"path": f"{env}/{name}", "offset": offset, "size": len(data),
                          "md5": hashlib.md5(data).hexdigest()})
        builds[env] = {"panel": panel, "parts": parts}

    manifest = {
        "name": "CYD Deck",
        "version": args.version.lstrip("v"),
        "date": datetime.date.today().isoformat(),
        "builds": builds,
    }
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"[OK] {out} ({', '.join(builds)})")


if __name__ == "__main__":
    main()
