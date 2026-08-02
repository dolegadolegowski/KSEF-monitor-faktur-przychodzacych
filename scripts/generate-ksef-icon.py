#!/usr/bin/env python3
"""Generate the deterministic multi-resolution Windows icon used by KSeF Monitor."""

from pathlib import Path
import struct


SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
GLYPHS = {
    "K": ("101", "110", "100", "110", "101"),
    "S": ("111", "100", "111", "001", "111"),
    "E": ("111", "100", "111", "100", "111"),
    "F": ("111", "100", "111", "100", "100"),
}


def inside_rounded_square(x: int, y: int, size: int, inset: int, radius: int) -> bool:
    left = inset
    top = inset
    right = size - inset - 1
    bottom = size - inset - 1
    if x < left or x > right or y < top or y > bottom:
        return False
    if left + radius <= x <= right - radius or top + radius <= y <= bottom - radius:
        return True
    center_x = left + radius if x < left + radius else right - radius
    center_y = top + radius if y < top + radius else bottom - radius
    return (x - center_x) ** 2 + (y - center_y) ** 2 <= radius**2


def render_pixels(size: int) -> list[list[tuple[int, int, int, int]]]:
    transparent = (0, 0, 0, 0)
    border = (10, 43, 75, 255)
    blue = (20, 73, 122, 255)
    white = (255, 255, 255, 255)
    pixels = [[transparent for _ in range(size)] for _ in range(size)]
    inset = 0 if size <= 20 else max(1, size // 64)
    radius = max(2, size // 5)
    border_width = max(1, size // 32)

    for y in range(size):
        for x in range(size):
            if not inside_rounded_square(x, y, size, inset, radius):
                continue
            is_inner = inside_rounded_square(
                x,
                y,
                size,
                inset + border_width,
                max(1, radius - border_width),
            )
            pixels[y][x] = blue if is_inner else border

    scale = max(1, (size - 2) // 15)
    text_width = 15 * scale
    text_height = 5 * scale
    start_x = (size - text_width) // 2
    start_y = (size - text_height) // 2
    for glyph_index, character in enumerate("KSEF"):
        glyph_x = start_x + glyph_index * 4 * scale
        for row_index, row in enumerate(GLYPHS[character]):
            for column_index, value in enumerate(row):
                if value != "1":
                    continue
                for dy in range(scale):
                    for dx in range(scale):
                        x = glyph_x + column_index * scale + dx
                        y = start_y + row_index * scale + dy
                        if 0 <= x < size and 0 <= y < size:
                            pixels[y][x] = white
    return pixels


def make_dib(size: int) -> bytes:
    pixels = render_pixels(size)
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        for red, green, blue, alpha in pixels[y]:
            xor.extend((blue, green, red, alpha))

    mask_stride = ((size + 31) // 32) * 4
    mask = bytearray(mask_stride * size)
    for output_row, y in enumerate(range(size - 1, -1, -1)):
        for x in range(size):
            if pixels[y][x][3] == 0:
                mask[output_row * mask_stride + x // 8] |= 0x80 >> (x % 8)

    header = struct.pack(
        "<IIIHHIIIIII",
        40,
        size,
        size * 2,
        1,
        32,
        0,
        len(xor),
        0,
        0,
        0,
        0,
    )
    return header + bytes(xor) + bytes(mask)


def build_ico() -> bytes:
    images = [(size, make_dib(size)) for size in SIZES]
    directory_size = 6 + 16 * len(images)
    result = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = directory_size
    for size, data in images:
        encoded_size = 0 if size == 256 else size
        result.extend(
            struct.pack(
                "<BBBBHHII",
                encoded_size,
                encoded_size,
                0,
                0,
                1,
                32,
                len(data),
                offset,
            )
        )
        offset += len(data)
    for _, data in images:
        result.extend(data)
    return bytes(result)


def main() -> None:
    project_root = Path(__file__).resolve().parents[1]
    output = project_root / "src" / "KsefMonitor" / "Assets" / "KSeFMonitor.ico"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(build_ico())
    print(f"Generated {output} ({output.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
