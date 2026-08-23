"""Regenerates assets/app.ico: a monitor with capture-viewfinder corner brackets.

Run with: python scripts/generate_app_icon.py
Requires Pillow (pip install pillow). Not part of the build; run manually when the
icon needs to change, then commit the regenerated assets/app.ico.
"""

from pathlib import Path

from PIL import Image, ImageDraw

CANVAS = 1024  # supersampled, then downscaled per icon size for clean anti-aliasing
SCREEN_FILL = (30, 34, 42, 255)  # dark slate, matches a "device" rather than pure black
SCREEN_BORDER = (210, 214, 222, 255)  # light bezel line
SCREEN_INNER = (58, 64, 76, 255)  # lit-panel shade inside the bezel
ACCENT = (50, 205, 90, 255)  # same green TrayIconFactory uses for TrayIconState.Capturing


def draw_base(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Monitor body: rounded rect, roughly centered, leaving room for a stand below.
    body = (size * 0.10, size * 0.10, size * 0.90, size * 0.68)
    d.rounded_rectangle(body, radius=size * 0.06, fill=SCREEN_FILL, outline=SCREEN_BORDER, width=int(size * 0.018))

    inset = size * 0.045
    inner = (body[0] + inset, body[1] + inset, body[2] - inset, body[3] - inset)
    d.rounded_rectangle(inner, radius=size * 0.045, fill=SCREEN_INNER)

    # Stand: neck + foot beneath the monitor body.
    neck_w = size * 0.10
    neck = ((size - neck_w) / 2, body[3], (size + neck_w) / 2, body[3] + size * 0.09)
    d.rectangle(neck, fill=SCREEN_FILL)
    foot = (size * 0.32, neck[3], size * 0.68, neck[3] + size * 0.035)
    d.rounded_rectangle(foot, radius=size * 0.015, fill=SCREEN_FILL)

    # Viewfinder corner brackets over the screen, suggesting an active capture region.
    bracket_len = (inner[2] - inner[0]) * 0.22
    bracket_w = size * 0.028
    pad = size * 0.03
    corners = [
        (inner[0] + pad, inner[1] + pad, 1, 1),   # top-left
        (inner[2] - pad, inner[1] + pad, -1, 1),  # top-right
        (inner[0] + pad, inner[3] - pad, 1, -1),  # bottom-left
        (inner[2] - pad, inner[3] - pad, -1, -1), # bottom-right
    ]
    for x, y, dx, dy in corners:
        d.line([(x, y), (x + dx * bracket_len, y)], fill=ACCENT, width=int(bracket_w))
        d.line([(x, y), (x, y + dy * bracket_len)], fill=ACCENT, width=int(bracket_w))

    return img


def main() -> None:
    base = draw_base(CANVAS)
    out = Path(__file__).resolve().parent.parent / "assets" / "app.ico"
    out.parent.mkdir(parents=True, exist_ok=True)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    resized = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    resized[0].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=resized[1:])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
