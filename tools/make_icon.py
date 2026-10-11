"""Matinee icon (ViewInFFXIV/images/icon.png): a glowing TV under drawn-back theatre curtains, with a play mark.
Drawn at 4x and scaled down to 512x512. Run from the repo root: python tools/make_icon.py"""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 2048                      # working size (4x of 512)
out = sys.argv[1] if len(sys.argv) > 1 else "ViewInFFXIV/images/icon.png"


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def vertical_gradient(w, h, top, bottom):
    img = Image.new("RGBA", (w, h))
    px = img.load()
    for y in range(h):
        c = lerp(top, bottom, y / (h - 1))
        for x in range(w):
            px[x, y] = c
    return img


def radial_glow(size, center, radius, color):
    glow = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(glow)
    cx, cy = center
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=color)
    return glow.filter(ImageFilter.GaussianBlur(radius * 0.45))


def mask_rounded(size, box, r):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle(box, r, fill=255)
    return m


# Background: a deep evening plum, rounded like an app tile.
bg = vertical_gradient(S, S, (44, 30, 78, 255), (16, 12, 30, 255))
tile = mask_rounded((S, S), (0, 0, S - 1, S - 1), 420)
canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
canvas.paste(bg, (0, 0), tile)

# A warm light behind the TV, like a lit stage.
canvas = Image.alpha_composite(canvas, radial_glow((S, S), (S // 2, 1060), 700, (255, 170, 90, 110)))

# TV body.
tv = (330, 560, 1718, 1470)
d = ImageDraw.Draw(canvas)
shadow_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow_layer).rounded_rectangle((tv[0] + 20, tv[1] + 60, tv[2] - 20, tv[3] + 60), 120, fill=(0, 0, 0, 150))
canvas = Image.alpha_composite(canvas, shadow_layer.filter(ImageFilter.GaussianBlur(50)))
d = ImageDraw.Draw(canvas)
d.rounded_rectangle(tv, 110, fill=(30, 24, 44, 255))
d.rounded_rectangle(tv, 110, outline=(155, 123, 255, 255), width=26)                                     # accent rim

# Screen: a sunset glow, amber at the bottom fading to violet.
screen = (tv[0] + 70, tv[1] + 70, tv[2] - 70, tv[3] - 70)
sw, sh = screen[2] - screen[0], screen[3] - screen[1]
glass = vertical_gradient(sw, sh, (126, 92, 230, 255), (255, 176, 104, 255))
canvas.paste(glass, screen[:2], mask_rounded((sw, sh), (0, 0, sw - 1, sh - 1), 60))

# Soft highlight across the top of the glass.
shine = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shine).ellipse((screen[0] - 200, screen[1] - 520, screen[2] + 200, screen[1] + 260), fill=(255, 255, 255, 40))
shine_mask = Image.new("L", (S, S), 0)
shine_mask.paste(mask_rounded((sw, sh), (0, 0, sw - 1, sh - 1), 60), screen[:2])
shine.putalpha(ImageChops.multiply(shine.getchannel("A"), shine_mask))
canvas = Image.alpha_composite(canvas, shine)

# Curtains drawn back to the sides of the screen, with a valance across the top.
curtain = Image.new("RGBA", (S, S), (0, 0, 0, 0))
c = ImageDraw.Draw(curtain)
red, red_dark = (214, 52, 82, 255), (150, 28, 58, 255)
left = [(screen[0], screen[1]), (screen[0] + 330, screen[1]), (screen[0] + 150, screen[1] + 330),
        (screen[0] + 95, screen[3]), (screen[0], screen[3])]
right = [(screen[2], screen[1]), (screen[2] - 330, screen[1]), (screen[2] - 150, screen[1] + 330),
         (screen[2] - 95, screen[3]), (screen[2], screen[3])]
c.polygon(left, fill=red)
c.polygon(right, fill=red)
for i, x in enumerate((45, 115)):    # folds
    c.line([(screen[0] + x + 90 - i * 40, screen[1] + 30), (screen[0] + x, screen[3])], fill=red_dark, width=26)
    c.line([(screen[2] - x - 90 + i * 40, screen[1] + 30), (screen[2] - x, screen[3])], fill=red_dark, width=26)
# Tie-backs in gold.
gold = (255, 200, 92, 255)
c.ellipse((screen[0] + 95, screen[1] + 300, screen[0] + 205, screen[1] + 410), fill=gold)
c.ellipse((screen[2] - 205, screen[1] + 300, screen[2] - 95, screen[1] + 410), fill=gold)
# Valance: scalloped swags.
val_h = 120
c.rectangle((screen[0], screen[1], screen[2], screen[1] + val_h - 40), fill=red)
n = 5
w = sw / n
for i in range(n):
    x0 = screen[0] + i * w
    c.ellipse((x0, screen[1] + val_h - 130, x0 + w, screen[1] + val_h + 30), fill=red)
    c.arc((x0 + 10, screen[1] + val_h - 120, x0 + w - 10, screen[1] + val_h + 20), 20, 160, fill=red_dark, width=14)
curtain_mask = Image.new("L", (S, S), 0)
curtain_mask.paste(mask_rounded((sw, sh), (0, 0, sw - 1, sh - 1), 60), screen[:2])
curtain.putalpha(ImageChops.multiply(curtain.getchannel("A"), curtain_mask))
canvas = Image.alpha_composite(canvas, curtain)

# Play mark in the middle of the stage.
cx, cy = (screen[0] + screen[2]) // 2 + 30, (screen[1] + screen[3]) // 2 + 70
r = 210
play = [(cx - r * 0.55, cy - r * 0.95), (cx - r * 0.55, cy + r * 0.95), (cx + r * 1.05, cy)]
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).polygon([(x + 18, y + 26) for x, y in play], fill=(60, 20, 40, 120))
canvas = Image.alpha_composite(canvas, shadow.filter(ImageFilter.GaussianBlur(22)))
ImageDraw.Draw(canvas).polygon(play, fill=(255, 250, 244, 255))

# Stand.
d = ImageDraw.Draw(canvas)
d.rounded_rectangle((S // 2 - 330, tv[3] + 70, S // 2 + 330, tv[3] + 130), 30, fill=(155, 123, 255, 255))
d.polygon([(S // 2 - 70, tv[3]), (S // 2 + 70, tv[3]), (S // 2 + 110, tv[3] + 80), (S // 2 - 110, tv[3] + 80)], fill=(155, 123, 255, 255))

# Two little sparkles: the "magic" of a show.
def sparkle(x, y, r, color):
    pts = [(x, y - r), (x + r * 0.22, y - r * 0.22), (x + r, y), (x + r * 0.22, y + r * 0.22),
           (x, y + r), (x - r * 0.22, y + r * 0.22), (x - r, y), (x - r * 0.22, y - r * 0.22)]
    ImageDraw.Draw(canvas).polygon(pts, fill=color)

sparkle(1760, 380, 120, gold)
sparkle(1560, 250, 60, (255, 230, 170, 255))

canvas.resize((512, 512), Image.LANCZOS).save(out)
print("saved", out)
