"""Redraw each stock cockpit atlas with Codex's built-in image generation, as GRID2VR did
(one `codex exec` image edit per car, prompt in tools/data/cockpit-redraw-prompt.txt).

  <lab>/input/<car>.png        the stock colour atlas, decoded and resized to 2048 (written here from <lab>/stock)
  <lab>/candidates/<car>.png   the redraw, copied from Codex's generated_images folder
  <lab>/generation-log.jsonl   car, source image path, prompt and timing per redraw

Cars that already have a candidate are skipped, so a run can be resumed. A request the image
service refuses is reported as FAILED and left for a person to decide (keep the original).
Then check the layout with --check and build with tools/build_cockpit_texture_pack.py.
"""
import argparse, json, os, re, shutil, subprocess, threading, time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
PREFIX = ('Use your built-in image generation tool exactly once to edit the attached image (image 1) with the prompt below. '
          'Do not run shell commands or write files. Reply only with the path of the generated image.\n\n')
TEMPLATE = (ROOT / 'tools/data/cockpit-redraw-prompt.txt').read_text(encoding='utf-8')
log_lock = threading.Lock()


def prepare(lab):
    (lab / 'input').mkdir(exist_ok=True)
    for dds in sorted((lab / 'stock').glob('*.dds')):
        target = lab / 'input' / f'{dds.stem}.png'
        if not target.exists():
            Image.open(dds).convert('RGB').resize((2048, 2048), Image.LANCZOS).save(target)


def redraw(lab, codex, car):
    target = lab / 'candidates' / f'{car}.png'
    if target.exists():
        return car, 'exists'
    prompt = PREFIX + TEMPLATE.replace('CAR_ID', car)
    for attempt in range(2):
        start = time.time()
        r = subprocess.run([codex, 'exec', '--skip-git-repo-check', '-s', 'read-only', f'--image={lab / "input" / f"{car}.png"}'],
                           input=prompt, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=900)
        (lab / 'logs' / f'{car}.out').write_text(r.stdout, encoding='utf-8')
        (lab / 'logs' / f'{car}.err').write_text(r.stderr, encoding='utf-8')
        paths = re.findall(r'[A-Za-z]:\\[^\s"]*generated_images\\[^\s"]+\.png', r.stdout)
        if r.returncode == 0 and paths and Path(paths[-1]).is_file():
            shutil.copyfile(paths[-1], target)
            with log_lock, open(lab / 'generation-log.jsonl', 'a', encoding='utf-8') as f:
                f.write(json.dumps(dict(car=car, path=paths[-1], prompt=prompt, mode='built-in imagegen (codex exec)',
                                        seconds=round(time.time() - start), attempt=attempt + 1)) + '\n')
            return car, 'ok'
        if 'moderation_blocked' in r.stderr:
            break
    return car, 'FAILED'


def check(lab):
    """Shift (phase correlation) and structure correlation of each redraw against its stock atlas."""
    def gray(path):
        return np.asarray(Image.open(path).convert('L').resize((512, 512), Image.LANCZOS), np.float32)
    flagged = []
    for cand in sorted((lab / 'candidates').glob('*.png')):
        a, b = gray(lab / 'input' / cand.name), gray(cand)
        r = np.fft.fft2(a - a.mean()) * np.conj(np.fft.fft2(b - b.mean()))
        c = np.abs(np.fft.ifft2(r / (np.abs(r) + 1e-6)))
        y, x = np.unravel_index(c.argmax(), c.shape)
        dx, dy = (x if x < 256 else x - 512), (y if y < 256 else y - 512)
        corr = float(np.corrcoef(a.ravel(), b.ravel())[0, 1])
        bad = abs(dx) > 2 or abs(dy) > 2 or corr < 0.8
        flagged += [cand.stem] if bad else []
        print(f'{"FLAG " if bad else "     "}{cand.stem} shift=({dx},{dy}) corr={corr:.3f}')
    return flagged


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--lab', type=Path, required=True)
    p.add_argument('--codex', default=os.environ.get('CODEX_EXE', 'codex'), help='Codex CLI with image generation (the desktop app bundles one)')
    p.add_argument('--parallel', type=int, default=3)
    p.add_argument('--check', action='store_true', help='only check the existing redraws')
    p.add_argument('cars', nargs='*')
    a = p.parse_args()
    lab = a.lab.resolve()
    if a.check:
        raise SystemExit(1 if check(lab) else 0)
    for folder in ('candidates', 'logs'):
        (lab / folder).mkdir(exist_ok=True)
    prepare(lab)
    cars = [c for c in sorted(p.stem for p in (lab / 'input').glob('*.png')) if not a.cars or c in a.cars]
    with ThreadPoolExecutor(a.parallel) as pool:
        for car, status in pool.map(lambda car: redraw(lab, a.codex, car), cars):
            print(f'{car} {status}', flush=True)
