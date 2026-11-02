#!/usr/bin/env python3
"""Pre-render the official profile light and radial avatar fills without a UWP shader.

Sources in Silo-Server/silo-android:
  android-shared/src/androidMain/kotlin/org/siloserver/silo/common/ui/marquee/
    MarqueeBackdrop.kt (BrandLight, grainTile), MarqueeProfileAvatar.kt
  androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/
    navigation/TvAppNavigation.kt (mirrored), screens/profiles/TvProfileSelectionScreen.kt

Use the Profiles mesh (stage 2), frozen at phase 0: also the official reduced-motion
and low-RAM pose. The Ambient scrim remains declarative in Themes/Skyline.xaml.
The old TvAuroraBackdrop.kt is no longer used by the official profile picker.
Only the Python standard library is needed. Run with --check to detect stale assets.
"""

import argparse
import math
from pathlib import Path
import re
import struct
import zlib


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/Bale.Xbox/Assets"


def lerp(a, b, t):
    return a + (b - a) * t


def byte(value):
    return max(0, min(255, int(value * 255 + 0.5)))


def rgb(argb):
    return tuple(((argb >> shift) & 255) / 255 for shift in (16, 8, 0))


def half(value):
    # Compose Color stores Oklab components as IEEE half floats.
    return struct.unpack("e", struct.pack("e", value))[0]


def oklab(color):
    linear = tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in color)
    r, g, b = linear
    l = math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
    m = math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
    s = math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
    return tuple(half(c) for c in (
        0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s,
    ))


def srgb(color):
    lightness, a, b = color
    l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3
    m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3
    s = (lightness - 0.0894841775 * a - 1.2914855480 * b) ** 3
    linear = (
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
    )
    return tuple(byte(12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055) / 255 for c in linear)


def color_lerp(start, end, amount):
    # androidx.compose.ui.graphics.Color.lerp works in Oklab, NOT sRGB. Using
    # ordinary RGB multiplication for dimmed() makes the brand light far too bright.
    return srgb(tuple(half(lerp(a, b, amount)) for a, b in zip(oklab(start), oklab(end))))


def png(width, height, pixels):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    stride = width * 3
    rows = b"".join(b"\0" + pixels[y * stride:(y + 1) * stride] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"sRGB", b"\0")
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def avatar(argb):
    size = 256  # 110dp at 2x Xbox output, including the 1.12 focus lift.
    tint = rgb(argb)
    stops = (color_lerp(tint, (1, 1, 1), 0.35), tint, color_lerp(tint, (0, 0, 0), 0.45))
    pixels = bytearray()
    for y in range(size):
        for x in range(size):
            # MarqueeProfileAvatar.kt: center (0.30w, 0.25h), radius 0.80w,
            # three equally spaced radial stops, clamped beyond the radius.
            distance = min(1, math.hypot((x + 0.5) / size - 0.3, (y + 0.5) / size - 0.25) / 0.8)
            index = min(1, int(distance * 2))
            amount = distance * 2 - index
            pixels.extend(byte(lerp(a, b, amount)) for a, b in zip(stops[index], stops[index + 1]))
    return png(size, size, pixels)


def brand_mesh():
    blue, red, orange = (rgb(c) for c in (0xFF0034FB, 0xFFF50B4F, 0xFFFD7403))
    # MarqueeBackdrop.kt BrandLight.PALETTES[2], in row-major order.
    mesh = [color_lerp(c, (0, 0, 0), amount) for c, amount in (
        (red, 0.56), (orange, 0.52), (blue, 0.56),
        (orange, 0.70), (red, 0.66), (blue, 0.72),
        (blue, 0.90), (red, 0.90), (orange, 0.92),
    )]

    def sample(u, v):
        gx, gy = min(u * 2, 1.9999), min(v * 2, 1.9999)
        cx, cy = int(gx), int(gy)
        fx, fy = gx - cx, gy - cy
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        top_left = cy * 3 + cx
        return tuple(byte(lerp(
            lerp(mesh[top_left][c], mesh[top_left + 1][c], fx),
            lerp(mesh[top_left + 3][c], mesh[top_left + 4][c], fx), fy)) for c in range(3))

    image = []
    # BrandLight.render(stage=2, phase=0): a=sin(0)=0, b=cos(0)=1.
    top_x, bottom_x, left_y, right_y, center_x, center_y = 0.5, 0.4, 0.66, 0.6, 0.6, 0.6
    for py in range(32):
        row = []
        y = (py + 0.5) / 32
        for px in range(48):
            x = (px + 0.5) / 48
            row_y = (lerp(left_y, center_y, x / center_x) if x < center_x
                     else lerp(center_y, right_y, (x - center_x) / (1 - center_x)))
            col_x = (lerp(top_x, center_x, y / row_y) if y < row_y
                     else lerp(center_x, bottom_x, (y - row_y) / (1 - row_y)))
            u = 0.5 * x / col_x if x < col_x else 0.5 + 0.5 * (x - col_x) / (1 - col_x)
            v = 0.5 * y / row_y if y < row_y else 0.5 + 0.5 * (y - row_y) / (1 - row_y)
            row.append(sample(max(0, min(1, u)), max(0, min(1, v))))
        image.append(row)
    return image


def backdrop():
    # Match the official 48x32 bitmap's bilinear upscaling, mirrored for TV.
    # Bake at the Xbox's 1920x1080 output so the 96x96 grain stays pixel-fine.
    width, height = 1920, 1080
    image = brand_mesh()
    state = 0x9E3779B9
    noise = []
    for _ in range(96 * 96):
        state = (state * 1_664_525 + 1_013_904_223) & 0xFFFFFFFF
        noise.append((state >> 24) / 255 * 0.045)

    pixels = bytearray()
    for y in range(height):
        sy = max(0, min(31, (y + 0.5) / height * 32 - 0.5))
        y0, y1 = int(sy), min(31, int(sy) + 1)
        for x in range(width):
            sx = max(0, min(47, (1 - (x + 0.5) / width) * 48 - 0.5))
            x0, x1 = int(sx), min(47, int(sx) + 1)
            alpha = noise[(y % 96) * 96 + x % 96]
            for c in range(3):
                top = lerp(image[y0][x0][c], image[y0][x1][c], sx - x0)
                bottom = lerp(image[y1][x0][c], image[y1][x1][c], sx - x0)
                value = lerp(top, bottom, sy - y0) / 255
                pixels.append(byte(lerp(value, 1, alpha)))
    return png(width, height, pixels)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="compare with committed PNGs without writing")
    args = parser.parse_args()
    # Keep the tested Core palette as the single source of tint/asset identities.
    source = (ROOT / "src/Bale.Core/Auth/ProfileTilePalette.cs").read_text(encoding="utf-8")
    colors = [int(value, 16) for value in re.findall(r"0xFF[0-9A-F]{6}\b", source)]
    if len(colors) != 8:
        raise SystemExit("Expected eight colors in ProfileTilePalette.cs")
    assets = {f"ProfileAvatar-{color:08X}.png": avatar(color) for color in colors}
    assets["ProfileBackdrop.png"] = backdrop()
    stale = []
    for name, data in assets.items():
        path = ASSETS / name
        if args.check:
            if not path.exists() or path.read_bytes() != data:
                stale.append(str(path))
        else:
            path.write_bytes(data)
    if stale:
        raise SystemExit("Stale profile artwork: " + ", ".join(stale))
    print(f"{'Checked' if args.check else 'Generated'} {len(assets)} profile artwork assets")


if __name__ == "__main__":
    main()
