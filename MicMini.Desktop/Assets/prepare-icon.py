"""Extract the manifest-referenced official APK icon and create a Windows ICO.

No artwork is generated or redrawn. Requires Pillow; takes --apk optionally.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
from zipfile import ZipFile

import PIL
from PIL import Image


APK_NAME = "1790237230756-DJI-Mimo-store-official-release-10001997_sec.apk"
APK_SHA256 = "877cce18fb21cbdb6e025965e7eab4b06d823675428f61b14f641b719454c2a2"
ICON_ENTRY = "res/kQ-.png"
ICON_SHA256 = "7d09f3e553695c8be9ab0046777c2e9406f0d83443279ac42bc69ebaae1dbf73"
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    assets = Path(__file__).resolve().parent
    repository = assets.parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, default=repository / APK_NAME)
    args = parser.parse_args()

    apk_hash = sha256_file(args.apk)
    if apk_hash != APK_SHA256:
        raise ValueError("APK SHA-256 does not match the analyzed official artifact.")
    with ZipFile(args.apk) as archive:
        source = archive.read(ICON_ENTRY)
        binary_manifest_hash = hashlib.sha256(archive.read("AndroidManifest.xml")).hexdigest()
    if hashlib.sha256(source).hexdigest() != ICON_SHA256:
        raise ValueError("APK icon entry does not match the traced official image.")

    png_path = assets / "dji-mimo-official-round.png"
    ico_path = assets / "dji-mimo.ico"
    png_path.write_bytes(source)
    image = Image.open(io.BytesIO(source)).convert("RGBA")
    image.save(ico_path, format="ICO", sizes=[(size, size) for size in SIZES])
    actual_sizes = sorted(Image.open(ico_path).ico.sizes())
    if actual_sizes != [(size, size) for size in SIZES]:
        raise ValueError(f"Unexpected generated icon sizes: {actual_sizes!r}")

    provenance = {
        "source_apk": APK_NAME,
        "source_apk_sha256": apk_hash,
        "binary_android_manifest_sha256": binary_manifest_hash,
        "manifest_application_icon": "@mipmap/ic_launcher_new",
        "manifest_application_round_icon": "@drawable/ic_launcher_round",
        "selected_resource": "@drawable/ic_launcher_round (xxxhdpi)",
        "decoded_resource_path": "analysis/extracted/resources-decoded/res/drawable-xxxhdpi/ic_launcher_round.png",
        "apk_entry": ICON_ENTRY,
        "source_size": list(image.size),
        "source_png_sha256": ICON_SHA256,
        "windows_ico": ico_path.name,
        "windows_ico_sha256": sha256_file(ico_path),
        "windows_ico_sizes": SIZES,
        "sidebar_png": png_path.name,
        "sidebar_png_sha256": sha256_file(png_path),
        "conversion": "Original PNG bytes retained; RGBA conversion and Lanczos resampling into ICO frames only. No redrawing, recoloring, cropping, or AI generation.",
        "tool": f"Pillow {PIL.__version__}",
    }
    (assets / "icon-provenance.json").write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps({"ico": str(ico_path), "sizes": SIZES, "sha256": provenance["windows_ico_sha256"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
