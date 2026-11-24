"""Draws Navigator's original logo: a folder tab shape with a compass needle (designed for this project)."""
import sys
from PIL import Image, ImageDraw

def draw(size):
    s = size * 4  # supersample
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = s * 0.06
    # gradient rounded square background
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / s
        r = int(0x1E + (0x00 - 0x1E) * t); g = int(0x90 + (0x5A - 0x90) * t); b = int(0xFF + (0xC8 - 0xFF) * t)
        gd.line([(0, y), (s, y)], fill=(r, g, b, 255))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.22, fill=255)
    img.paste(grad, (0, 0), mask)
    # folder silhouette
    fx0, fy0, fx1, fy1 = s * 0.22, s * 0.30, s * 0.78, s * 0.74
    d.rounded_rectangle([fx0, fy0, fx0 + (fx1 - fx0) * 0.42, fy0 + s * 0.12], radius=s * 0.035, fill=(255, 214, 102, 255))
    d.rounded_rectangle([fx0, fy0 + s * 0.06, fx1, fy1], radius=s * 0.05, fill=(255, 196, 64, 255))
    # compass needle
    cx, cy = s * 0.5, s * 0.53
    d.polygon([(cx, cy - s * 0.16), (cx + s * 0.06, cy), (cx, cy + s * 0.16), (cx - s * 0.06, cy)], fill=(255, 255, 255, 255))
    d.polygon([(cx, cy - s * 0.16), (cx + s * 0.06, cy), (cx - s * 0.06, cy)], fill=(232, 72, 85, 255))
    d.ellipse([cx - s * 0.022, cy - s * 0.022, cx + s * 0.022, cy + s * 0.022], fill=(60, 60, 60, 255))
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
big = draw(256)
big.save(f"{out}/navigator-256.png")
draw(512).save(f"{out}/navigator-512.png")
big.save(f"{out}/navigator.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
