"""Build the optional cockpit texture pack (as GRID2VR's tools/build_cockpit_texture_pack.py).

Inputs, all local and never committed:
  <lab>/stock/          tools/Export-CockpitTextures.ps1 output: <car>.dds (512 BC3, 10 mips) and inventory.json
  <lab>/candidates/     the accepted redrawn colour atlas per car, <car>.png (any square size;
                        tools/redraw_cockpit_atlases.py makes them)

For each car the redrawn colour is resized to 2048 and joined with the stock alpha (the cockpit
shader's material mask, which the redraw does not touch) resized to 2048, then encoded as BC3 with
the full 12-level mip chain. The stock atlas is identified in game by the SHA-256 of the first eight
mips of its BC3 data, as the game uploads it (bridge/cockpit_textures.cpp).

Writes <lab>/pack/<version>/ (the installed folder) and <lab>/GRIDAutosportVR-CockpitTextures-<version>.zip,
and records the ZIP's size and hash in launcher/assets/cockpit-texture-pack.json, which the launcher
embeds. Only generated replacements enter the ZIP; the stock exports stay local.
"""
import argparse
import hashlib
import io
import json
import re
import struct
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SIZE, MIPS, STOCK_SIZE, STOCK_MIPS, GAME_MIPS = 2048, 12, 512, 10, 8


def sha(data):
    return hashlib.sha256(data).hexdigest()


def mip_bytes(size, mips):
    return [max(1, (max(1, size >> m) + 3) // 4) ** 2 * 16 for m in range(mips)]


# --- BC3 encoder ---------------------------------------------------------------------------------
# Colour: principal-axis endpoints refined by least squares (two passes), 4-colour palette.
# Alpha: min/max endpoints, 8-value palette. Vectorised over all 4x4 blocks of a level.

def _blocks(rgba):
    h, w = rgba.shape[:2]
    ph, pw = max(4, (h + 3) // 4 * 4), max(4, (w + 3) // 4 * 4)
    if (ph, pw) != (h, w):
        rgba = np.pad(rgba, ((0, ph - h), (0, pw - w), (0, 0)), mode='edge')
    b = rgba.reshape(ph // 4, 4, pw // 4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(-1, 16, 4)
    return b.astype(np.float32), ph // 4, pw // 4


def _to565(c):
    q = np.clip(np.rint(c * np.array([31, 63, 31], np.float32) / 255.0), 0, [31, 63, 31]).astype(np.int32)
    return q


def _from565(q):
    r, g, b = q[..., 0], q[..., 1], q[..., 2]
    return np.stack([(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)], -1).astype(np.float32)


WEIGHT = np.array([0.299, 0.587, 0.114], np.float32) * 3


def _palette_indices(px, q0, q1):
    c0, c1 = _from565(q0), _from565(q1)
    pal = np.stack([c0, c1, (2 * c0 + c1) / 3, (c0 + 2 * c1) / 3], 1)  # N,4,3
    d = (((px[:, :, None, :] - pal[:, None, :, :]) ** 2) * WEIGHT).sum(-1)  # N,16,4
    idx = d.argmin(-1)
    err = np.take_along_axis(d, idx[..., None], -1)[..., 0].sum(-1)
    return idx, err


def _encode_color(px):
    n = px.shape[0]
    mean = px.mean(1, keepdims=True)
    centered = px - mean
    cov = np.einsum('nki,nkj->nij', centered, centered)
    axis = np.ones((n, 3), np.float32)
    for _ in range(8):
        axis = np.einsum('nij,nj->ni', cov, axis)
        axis /= np.maximum(np.linalg.norm(axis, axis=1, keepdims=True), 1e-6)
    t = np.einsum('nki,ni->nk', centered, axis)
    lo, hi = t.min(1), t.max(1)
    e0 = mean[:, 0] + hi[:, None] * axis
    e1 = mean[:, 0] + lo[:, None] * axis
    q0, q1 = _to565(np.clip(e0, 0, 255)), _to565(np.clip(e1, 0, 255))
    idx, err = _palette_indices(px, q0, q1)
    weights = np.array([1.0, 0.0, 2 / 3, 1 / 3], np.float32)
    for _ in range(2):
        a = weights[idx]; b = 1 - a  # N,16
        aa, bb, ab = (a * a).sum(1), (b * b).sum(1), (a * b).sum(1)
        ax, bx = np.einsum('nk,nki->ni', a, px), np.einsum('nk,nki->ni', b, px)
        det = aa * bb - ab * ab
        ok = np.abs(det) > 1e-6
        safe = np.where(ok, det, 1)[:, None]
        n0 = np.clip((ax * bb[:, None] - bx * ab[:, None]) / safe, 0, 255)
        n1 = np.clip((bx * aa[:, None] - ax * ab[:, None]) / safe, 0, 255)
        nq0, nq1 = _to565(n0), _to565(n1)
        nidx, nerr = _palette_indices(px, nq0, nq1)
        better = ok & (nerr < err)
        q0[better], q1[better], idx[better], err[better] = nq0[better], nq1[better], nidx[better], nerr[better]
    v0 = (q0[:, 0] << 11) | (q0[:, 1] << 5) | q0[:, 2]
    v1 = (q1[:, 0] << 11) | (q1[:, 1] << 5) | q1[:, 2]
    # Keep c0 > c1 (4-colour order); swapping the endpoints swaps index pairs 0<->1 and 2<->3.
    swap = v0 < v1
    v0, v1 = np.where(swap, v1, v0), np.where(swap, v0, v1)
    idx = np.where(swap[:, None], idx ^ 1, idx)
    idx = np.where((v0 == v1)[:, None], 0, idx)
    bits = (idx.astype(np.uint32) << (2 * np.arange(16, dtype=np.uint32))).sum(1).astype(np.uint32)
    out = np.zeros((n, 8), np.uint8)
    out[:, 0:2] = v0.astype('<u2').view(np.uint8).reshape(n, 2)
    out[:, 2:4] = v1.astype('<u2').view(np.uint8).reshape(n, 2)
    out[:, 4:8] = bits.astype('<u4').view(np.uint8).reshape(n, 4)
    return out


def _encode_alpha(al):
    n = al.shape[0]
    a0, a1 = np.rint(al.max(1)).astype(np.int32), np.rint(al.min(1)).astype(np.int32)
    w0 = np.array([7, 0, 6, 5, 4, 3, 2, 1], np.float32) / 7
    pal = a0[:, None] * w0 + a1[:, None] * (1 - w0)  # 8-value mode (a0 > a1)
    idx = np.abs(al[:, :, None] - pal[:, None, :]).argmin(-1)
    idx = np.where((a0 == a1)[:, None], 0, idx)
    bits = (idx.astype(np.uint64) << (3 * np.arange(16, dtype=np.uint64))).sum(1).astype(np.uint64)
    out = np.zeros((n, 8), np.uint8)
    out[:, 0], out[:, 1] = a0, a1
    out[:, 2:8] = bits.astype('<u8').view(np.uint8).reshape(n, 8)[:, :6]
    return out


def encode_bc3(rgba):
    b, _, _ = _blocks(rgba)
    return np.concatenate([_encode_alpha(b[:, :, 3]), _encode_color(b[:, :, :3])], 1).tobytes()


def dds_bc3(levels):
    size = levels[0].shape[0]
    data = b''.join(encode_bc3(level) for level in levels)
    header = struct.pack('<4s7I44x2I4s5I5I', b'DDS ', 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000, size, size,
                         mip_bytes(size, 1)[0], 0, len(levels), 32, 0x4, b'DXT5', 0, 0, 0, 0, 0,
                         0x1000 | 0x8 | 0x400000, 0, 0, 0, 0)
    assert len(header) == 128
    return header + data


# --- Pack ----------------------------------------------------------------------------------------

LUMA = np.array([0.299, 0.587, 0.114], np.float32)


def match_tone(color, stock):
    """The redraws lift dark plastics and leather; map the redraw's luminance histogram onto the
    stock atlas's (monotonic, so detail is kept) and scale RGB by the same ratio per pixel."""
    c, s = np.asarray(color, np.float32), np.asarray(stock, np.float32)
    yc, ys = c @ LUMA, s @ LUMA
    order = np.argsort(yc, axis=None)
    target = np.sort(ys, axis=None)
    matched = np.empty(yc.size, np.float32)
    matched[order] = target
    matched = matched.reshape(yc.shape)
    ratio = np.clip((matched + 1) / (yc + 1), 0.5, 1.5)[..., None]
    return Image.fromarray(np.clip(np.rint(c * ratio), 0, 255).astype(np.uint8))


def replacement(stock_dds, candidate_png):
    color = Image.open(candidate_png).convert('RGB')
    if color.width != color.height:
        raise ValueError(f'{candidate_png.name}: not square')
    color = color.resize((SIZE, SIZE), Image.LANCZOS)
    stock = Image.open(stock_dds)
    color = match_tone(color, stock.convert('RGB').resize((SIZE, SIZE), Image.LANCZOS))
    alpha = stock.getchannel('A').resize((SIZE, SIZE), Image.BICUBIC)
    full = Image.merge('RGBA', (*color.split(), alpha))
    levels = [np.asarray(full, np.uint8)]
    for m in range(1, MIPS):
        side = max(1, SIZE >> m)
        levels.append(np.asarray(full.resize((side, side), Image.LANCZOS if side >= 4 else Image.BOX), np.uint8))
    return dds_bc3(levels), levels[0]


def build(lab, version, cars_only=None, keep_stock=()):
    if not re.fullmatch(r'\d+\.\d+\.\d+(-[a-z]+\.\d+)?', version):
        raise ValueError('Version must look like 1.0.0 or 1.0.0-preview.1')
    inventory = json.loads((lab / 'stock/inventory.json').read_text(encoding='utf-8-sig'))['cars']
    out = lab / 'pack' / version
    if out.exists():
        raise ValueError(f'{out} exists; choose a new version')
    out.mkdir(parents=True)
    rows, missing = [], []
    for row in sorted(inventory, key=lambda r: r['car']):
        car = row['car']
        if not re.fullmatch('[a-z0-9]{3}', car):
            raise ValueError('Invalid car ID')
        if cars_only and car not in cars_only:
            continue
        stock = (lab / f'stock/{car}.dds').read_bytes()
        if sha(stock) != row['ddsSha256'] or len(stock) != 128 + sum(mip_bytes(STOCK_SIZE, STOCK_MIPS)) or stock[84:88] != b'DXT5':
            raise ValueError(f'{car}: changed or unexpected stock export')
        source = sha(stock[128:128 + sum(mip_bytes(STOCK_SIZE, GAME_MIPS))])
        candidate = lab / f'candidates/{car}.png'
        if car in keep_stock:
            continue
        if not candidate.is_file():
            missing.append(car)
            continue
        dds, top = replacement(lab / f'stock/{car}.dds', candidate)
        decoded = np.asarray(Image.open(io.BytesIO(dds)).convert('RGBA'), np.float32)  # PIL's own BC3 decoder
        error = np.abs(decoded - top.astype(np.float32)).mean(axis=(0, 1))
        if error[:3].max() > 6 or error[3] > 4:
            raise ValueError(f'{car}: BC3 self-check error {error}')
        (out / f'{car}.dds').write_bytes(dds)
        rows.append(dict(car=car, source=source, dds=sha(dds), bytes=len(dds), candidate=sha(candidate.read_bytes())))
        print(f'{car}: {len(dds)} bytes, mean BC3 error {np.round(error, 2).tolist()}', flush=True)
    if missing and not cars_only:
        raise ValueError('Missing redrawn atlases: ' + ', '.join(missing))
    if not rows:
        raise ValueError('No replacements')
    (out / 'catalog.txt').write_text(''.join(f"{r['car']} {r['source']} {r['dds']}\n" for r in rows), encoding='ascii', newline='\n')
    (out / 'README.txt').write_text(
        f'GRID Autosport VR cockpit textures {version}\n\n'
        f'Optional artwork for {len(rows)} cars. AI-redrawn colour atlases based on the stock UV layout,\n'
        f'resized to {SIZE} square and joined with the stock material mask (alpha), BC3 with {MIPS} mips.\n'
        'Markings and materials can differ from the originals. Game files are not modified: the VR\n'
        'renderer swaps the textures in memory while GRID Autosport VR runs.\n'
        'Install and remove it from the launcher (Graphics -> Cockpit textures).\n' +
        (f'Kept original (no redraw): {", ".join(sorted(keep_stock))}.\n' if keep_stock else ''), encoding='utf-8', newline='\n')
    files = {p.name: sha(p.read_bytes()) for p in sorted(out.iterdir())}
    archive = lab / f'GRIDAutosportVR-CockpitTextures-{version}.zip'
    if archive.exists():
        raise ValueError(f'{archive} exists')
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for name in sorted(files):
            z.write(out / name, f'cockpit-textures/{version}/{name}')
    catalog = dict(schema=1, version=version, asset=archive.name, zipBytes=archive.stat().st_size,
                   zipSha256=sha(archive.read_bytes()), installedBytes=sum((out / n).stat().st_size for n in files),
                   cars=len(rows), stockCars=sorted(keep_stock), files=files)
    if not cars_only:
        (ROOT / 'launcher/assets/cockpit-texture-pack.json').write_text(json.dumps(catalog, indent=2) + '\n', encoding='utf-8', newline='\n')
    (lab / f'pack-{version}.json').write_text(json.dumps(dict(catalog, rows=rows), indent=2) + '\n', encoding='utf-8')
    return {k: catalog[k] for k in ('version', 'asset', 'zipBytes', 'zipSha256', 'installedBytes', 'cars')}


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--lab', type=Path, required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--car', action='append', help='build a local test pack of these cars only (launcher catalog untouched)')
    p.add_argument('--keep-stock', action='append', default=[], help='leave this car on its original texture (no redraw)')
    a = p.parse_args()
    print(json.dumps(build(a.lab.resolve(), a.version, a.car, set(a.keep_stock)), indent=2))
