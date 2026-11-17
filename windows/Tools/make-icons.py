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


SMALL = 32
SUPER = 16


def bezier(p0, p1, p2, p3, steps=64):
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        yield (u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
               u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1])


def draw_small(size: int) -> Image.Image:
    # Scaled down, a two-pixel stroke straddles three pixels and smears; this
    # is make-icon.swift's mark with the trunk's width and centre on the grid.
    k = SUPER
    big = size * k

    stops = [(0, (0xEE, 0x93, 0x66)), (0.52, (0xD1, 0x66, 0x3C)), (1, (0xA4, 0x3C, 0x19))]

    def ramp(t):
        for (a, ca), (b, cb) in zip(stops, stops[1:]):
            if t <= b:
                f = (t - a) / (b - a)
                return tuple(round(ca[i] + (cb[i] - ca[i]) * f) for i in range(3))
        return stops[-1][1]

    small_grad = Image.new("RGB", (size * 4, size * 4))
    n = size * 4
    small_grad.putdata([
        ramp((i + j) / (2 * (n - 1))) for j in range(n) for i in range(n)])
    body = small_grad.resize((big, big), Image.BILINEAR).convert("RGBA")

    sheen = Image.new("L", (n, n))
    sheen.putdata([
        round(255 * 0.18 * max(0.0, 1 - ((i - n / 2) ** 2 + j ** 2) ** 0.5 / (n * 0.8)))
        for j in range(n) for i in range(n)])
    white = Image.new("RGBA", (big, big), (255, 255, 255, 255))
    body = Image.composite(white, body, sheen.resize((big, big), Image.BILINEAR))

    # The superellipse the Mac draws, filling the frame as the cropped icons do.
    mask = Image.new("L", (big, big))
    a = big / 2
    mask.putdata([
        255 if abs((i + 0.5 - a) / a) ** 5 + abs((j + 0.5 - a) / a) ** 5 <= 1 else 0
        for j in range(big) for i in range(big)])

    width = max(2, round(size * 0.122))
    cx = size / 2 if width % 2 == 0 else (size // 2) + 0.5

    # The Swift draws with y up.
    def pt(px, py):
        return (px * size, (1 - py) * size)

    union = (cx, round((1 - 0.475) * size * 2) / 2)
    top = (cx, round((1 - 0.82) * size) + width / 2 - 0.5)

    ink = Image.new("L", (big, big))
    borrowed = Image.new("L", (big, big))
    from PIL import ImageDraw
    r = width * k / 2

    def stroke(layer, points):
        d = ImageDraw.Draw(layer)
        for px, py in points:
            d.ellipse((px * k - r, py * k - r, px * k + r, py * k + r), fill=255)

    stroke(borrowed, bezier(pt(0.765, 0.215), pt(0.745, 0.365), pt(0.64, 0.425), union))
    stroke(ink, bezier(pt(0.235, 0.215), pt(0.255, 0.365), pt(0.36, 0.425), union))
    ImageDraw.Draw(ink).rectangle(
        ((cx - width / 2) * k, top[1] * k, (cx + width / 2) * k - 1, union[1] * k), fill=255)
    stroke(ink, [top])

    cream = Image.new("RGBA", (big, big), (255, 248, 241, 255))
    body = Image.composite(cream, body, borrowed.point(lambda v: round(v * 0.52)))
    body = Image.composite(cream, body, ink.point(lambda v: round(v * 0.97)))
    body.putalpha(mask)
    return body.resize((size, size), Image.BOX)


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
        if source is cropped and size <= SMALL:
            return draw_small(size)
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

    # Left to itself Pillow's ICO writer resamples every size from one image
    # without the sharpen, which is what made the tray and taskbar icon soft.
    frames = [scale(cropped, s) for s in ICO_SIZES if s <= 256]
    frames[-1].save(
        ASSETS / "AppIcon.ico", format="ICO",
        sizes=[f.size for f in frames], append_images=frames[:-1])

    print(f"wrote {len(CROPPED) + len(PADDED) + len(WIDE) + 1} assets to {ASSETS}")


if __name__ == "__main__":
    main()
