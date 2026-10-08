"""Generates src/DeskPilot/Assets/DeskPilot.ico and DeskPilot.png.

Python standard library only. Run from the repository root:

    python tools/make_icon.py

Design: a rounded square with a dark-to-accent diagonal gradient and a white
mouse pointer. Small sizes (<= 48 px) are stored as 32-bit DIBs, larger ones as
PNG, which every Windows version since Vista reads.
"""

import math
import os
import struct
import sys
import zlib

SIZES = [16, 24, 32, 48, 64, 128, 256]

# Gradient stops along the top-left to bottom-right diagonal.
STOPS = [
    (0.00, (24, 28, 52)),
    (0.55, (78, 92, 226)),
    (1.00, (142, 96, 248)),
]

# Classic arrow pointer, in its own units (tip at 0,0).
POINTER = [(0.0, 0.0), (0.0, 17.0), (4.3, 13.1), (7.2, 19.6), (9.9, 18.4), (7.0, 12.1), (12.4, 12.1)]
POINTER_W, POINTER_H = 12.4, 19.6


def lerp(a, b, t):
    return a + (b - a) * t


def gradient(t):
    t = max(0.0, min(1.0, t))
    for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
        if t <= t1:
            k = (t - t0) / (t1 - t0)
            return tuple(lerp(c0[i], c1[i], k) for i in range(3))
    return STOPS[-1][1]


def inside_rounded_rect(x, y, size, radius):
    # Distance-free test of a point against a rounded square [0,size]^2.
    if x < 0 or y < 0 or x > size or y > size:
        return False
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def inside_polygon(x, y, pts):
    inside = False
    n = len(pts)
    j = n - 1
    for i in range(n):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y):
            xcross = (xj - xi) * (y - yi) / (yj - yi) + xi
            if x < xcross:
                inside = not inside
        j = i
    return inside


def seg_distance(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy
    t = 0.0 if length2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length2))
    qx, qy = ax + t * dx, ay + t * dy
    return math.hypot(px - qx, py - qy)


def poly_distance(x, y, pts):
    best = 1e9
    n = len(pts)
    for i in range(n):
        ax, ay = pts[i]
        bx, by = pts[(i + 1) % n]
        best = min(best, seg_distance(x, y, ax, ay, bx, by))
    return best


def blend(dst, src, alpha):
    # dst, src: (r, g, b, a) with a in 0..1, straight alpha; "src over dst".
    if alpha <= 0:
        return dst
    dr, dg, db, da = dst
    sr, sg, sb = src
    out_a = alpha + da * (1 - alpha)
    if out_a <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    r = (sr * alpha + dr * da * (1 - alpha)) / out_a
    g = (sg * alpha + dg * da * (1 - alpha)) / out_a
    b = (sb * alpha + db * da * (1 - alpha)) / out_a
    return (r, g, b, out_a)


def render(size):
    ss = 4 if size <= 64 else 3
    radius = size * 0.225
    # Small icons get a larger pointer without an outline so it stays legible.
    small = size <= 24
    scale = size * (0.70 if small else 0.62 if size <= 32 else 0.56) / POINTER_H
    ox = size * 0.5 - POINTER_W * scale * 0.5 + size * 0.035
    oy = size * 0.5 - POINTER_H * scale * 0.5 + size * 0.01
    pointer = [(ox + px * scale, oy + py * scale) for px, py in POINTER]
    shadow_dx, shadow_dy = size * 0.018, size * 0.03
    shadow = [(x + shadow_dx, y + shadow_dy) for x, y in pointer]
    outline = 0.0 if small else max(0.6, size * 0.017)
    blur = max(1.0, size * 0.05)
    draw_shadow = size >= 32

    pixels = []
    for py in range(size):
        row = []
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    x = px + (sx + 0.5) / ss
                    y = py + (sy + 0.5) / ss
                    if not inside_rounded_rect(x, y, size, radius):
                        continue
                    t = (x + y) / (2.0 * size)
                    r, g, b = gradient(t)
                    # Soft highlight towards the top edge.
                    hl = max(0.0, 1.0 - y / (size * 0.55)) * 0.10
                    r, g, b = lerp(r, 255, hl), lerp(g, 255, hl), lerp(b, 255, hl)
                    c = (r, g, b, 1.0)
                    if draw_shadow:
                        if inside_polygon(x, y, shadow):
                            sa = 0.35
                        else:
                            d = poly_distance(x, y, shadow)
                            sa = 0.35 * max(0.0, 1.0 - d / blur)
                        c = blend(c, (8, 10, 24), sa)
                    if inside_polygon(x, y, pointer):
                        d = poly_distance(x, y, pointer)
                        if d < outline:
                            c = blend(c, (16, 20, 40), 0.85)
                        else:
                            c = blend(c, (255, 255, 255), 1.0)
                    acc[0] += c[0] * c[3]
                    acc[1] += c[1] * c[3]
                    acc[2] += c[2] * c[3]
                    acc[3] += c[3]
            n = ss * ss
            a = acc[3] / n
            if acc[3] > 0:
                r, g, b = acc[0] / acc[3], acc[1] / acc[3], acc[2] / acc[3]
            else:
                r = g = b = 0.0
            row.append((int(round(r)), int(round(g)), int(round(b)), int(round(a * 255))))
        pixels.append(row)
    return pixels


def png_bytes(pixels):
    size = len(pixels)
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw.extend((r, g, b, a))

    def chunk(kind, data):
        c = struct.pack(">I", len(data)) + kind + data
        return c + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def dib_bytes(pixels):
    size = len(pixels)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for row in reversed(pixels):
        for r, g, b, a in row:
            xor.extend((b, g, r, a))
    mask_stride = ((size + 31) // 32) * 4
    and_mask = bytearray()
    for row in reversed(pixels):
        bits = bytearray(mask_stride)
        for x, (_, _, _, a) in enumerate(row):
            if a == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        and_mask.extend(bits)
    return header + bytes(xor) + bytes(and_mask)


def ico_bytes(images):
    count = len(images)
    out = bytearray(struct.pack("<HHH", 0, 1, count))
    offset = 6 + 16 * count
    blobs = []
    for size, data in images:
        dim = 0 if size >= 256 else size
        out.extend(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
        blobs.append(data)
    for b in blobs:
        out.extend(b)
    return bytes(out)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "src", "DeskPilot", "Assets")
    os.makedirs(assets, exist_ok=True)
    images = []
    big = None
    for size in SIZES:
        sys.stdout.write(f"rendering {size}px\n")
        sys.stdout.flush()
        px = render(size)
        if size == 256:
            big = px
        data = dib_bytes(px) if size <= 48 else png_bytes(px)
        images.append((size, data))
    with open(os.path.join(assets, "DeskPilot.ico"), "wb") as f:
        f.write(ico_bytes(images))
    with open(os.path.join(assets, "DeskPilot.png"), "wb") as f:
        f.write(png_bytes(big))
    sys.stdout.write("wrote DeskPilot.ico and DeskPilot.png\n")


if __name__ == "__main__":
    main()
