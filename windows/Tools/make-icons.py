#!/usr/bin/env python3
"""Regenerate the Windows icon assets from the Mac build's icon.

The mark is drawn once, in code, by Tools/make-icon.swift, and baked into
Resources/AppIcon.icns at release. macOS ships that single file; Windows
packaging wants every size on disk as its own PNG, and a tray and window icon
as a multi-resolution .ico. Rather than draw the mark a second time in a second
language — two drawings to keep looking alike, which is the thing the Mac tool
went out of its way to avoid — this scales all of them from the 1024 master the
Swift already produced. Run it whenever that icon changes, so the two builds
never drift into wearing different faces.

    python windows/Tools/make-icons.py

Needs Pillow (`pip install Pillow`).
"""

from pathlib import Path
from PIL import Image, ImageFilter

REPO = Path(__file__).resolve().parents[2]
MASTER = REPO / "Resources" / "AppIcon.icns"
ASSETS = REPO / "windows" / "ClaudeGraft" / "Assets"

# The taskbar button and Start entry of a packaged app take their icon from the
# Square44x44 logos, never from the exe's embedded .ico, so these want the mark
# filling the frame the way the .ico does — scaled from the cropped master, not
# the padded one, or the mark sits inset and reads smaller than Claude's beside
# it in the taskbar. The Store logo is a bare icon too and gets the same.
CROPPED = {
    "Square44x44Logo.scale-200.png": 88,
    "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
    "Square44x44Logo.targetsize-48_altform-lightunplated.png": 48,
    "StoreLogo.png": 50,
}

# The larger Start tiles are meant to hold the mark centred with a margin around
# it, so these keep the padding the macOS master carries.
PADDED = {
    "Square150x150Logo.scale-200.png": 300,
    "LockScreenLogo.scale-200.png": 48,
}

# The wide tiles are not square; the mark sits at their height, centred, on the
# transparency Windows fills with the accent colour behind it.
WIDE = {
    "Wide310x150Logo.scale-200.png": (620, 300),
    "SplashScreen.scale-200.png": (1240, 600),
}

# The sizes a Windows .ico carries, from the taskbar's largest down to the 16
# the notification area shows. The mark still reads at 16, which is the whole
# reason it is a single stroke and not a scene.
ICO_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 64, 128, 256, 512]


def master_1024() -> Image.Image:
    icon = Image.open(MASTER)
    icon.size = (1024, 1024)  # the largest the .icns carries
    icon.load()
    return icon.convert("RGBA")


def main() -> None:
    if not MASTER.exists():
        raise SystemExit(f"no master icon at {MASTER} — draw it on the Mac first")

    master = master_1024()

    # The master carries transparent padding for the macOS menu bar, which adds
    # its own inset. The taskbar, tray and small logos want the mark filling the
    # frame, so they scale from the content bbox with the padding cropped away.
    bbox = master.getbbox()
    cropped = master.crop(bbox) if bbox else master
    # Make it square again, centred, in case the bbox was not.
    side = max(cropped.size)
    if cropped.size != (side, side):
        sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        sq.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2), cropped)
        cropped = sq

    def scale(source: Image.Image, size: int) -> Image.Image:
        img = source.resize((size, size), Image.LANCZOS)
        # A light sharpen keeps small icons crisp rather than smeared.
        if size <= 64:
            img = img.filter(ImageFilter.SHARPEN)
        return img

    for name, size in CROPPED.items():
        scale(cropped, size).save(ASSETS / name)

    for name, size in PADDED.items():
        scale(master, size).save(ASSETS / name)

    for name, (width, height) in WIDE.items():
        canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        mark = scale(master, min(width, height))
        canvas.paste(mark, ((width - mark.width) // 2, (height - mark.height) // 2), mark)
        canvas.save(ASSETS / name)

    # Pillow's ICO writer takes one image and a sizes list; it rescales
    # internally, so hand it the largest and let it downsample. The sharpen
    # at small sizes is lost this way, but the cropped bbox is what matters
    # for the size match against Claude's icon.
    ico_master = cropped.resize((max(ICO_SIZES), max(ICO_SIZES)), Image.LANCZOS)
    ico_master.save(
        ASSETS / "AppIcon.ico", format="ICO",
        sizes=[(s, s) for s in ICO_SIZES])

    print(f"wrote {len(CROPPED) + len(PADDED) + len(WIDE) + 1} assets to {ASSETS}")


if __name__ == "__main__":
    main()
