"""Match visible landmarks between an Autosport frame's two native viewports."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image


def match(source, target, x, y):
    # Search along a rectified epipolar line, allowing two pixels of vertical error.
    r = 7
    block = source[y-r:y+r+1, x-r:x+r+1]
    if block.shape != (2*r+1, 2*r+1) or block.std() < 8:
        raise ValueError(f"Landmark at {x},{y} has insufficient texture")
    template = block - block.mean()
    candidates = []
    for cy in range(max(r, y-2), min(target.shape[0]-r, y+3)):
        for cx in range(max(r, x-48), min(target.shape[1]-r, x+49)):
            patch = target[cy-r:cy+r+1, cx-r:cx+r+1]
            patch = patch - patch.mean()
            denom = np.linalg.norm(template) * np.linalg.norm(patch)
            if denom:
                candidates.append((float((template * patch).sum() / denom), cx, cy))
    score, tx, ty = max(candidates)
    runner_up = max((s for s, xx, yy in candidates if abs(xx-tx) > 2), default=0)
    return tx, ty, score, runner_up


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("frame", type=Path)
    parser.add_argument("landmark", nargs="+", help="Descriptive label@x,y in the left eye")
    args = parser.parse_args()
    meta = json.loads((args.frame / "frame.json").read_text())
    left, right = [Image.open(args.frame / f"{name}.bmp").convert("RGB") for name in ("left","right")]
    a, b = [np.asarray(im.convert("L"), dtype=np.float32) for im in (left, right)]
    measurements = []
    for item in args.landmark:
        label, coords = item.rsplit("@", 1)
        x, y = map(int, coords.split(","))
        tx, ty, score, runner_up = match(a, b, x, y)
        bx, by, _, _ = match(b, a, tx, ty)
        measurements.append(dict(landmark=label, left=[x,y], right=[tx,ty], disparityPx=x-tx,
                                 verticalOffsetPx=y-ty, correlation=round(score,4),
                                 alternateCorrelation=round(runner_up,4), reverseErrorPx=max(abs(bx-x),abs(by-y))))
    sheet = Image.new("RGB", (left.width+right.width,max(left.height,right.height)))
    sheet.paste(left, (0,0))
    sheet.paste(right, (left.width,0))
    sheet.save(args.frame / "cockpit-pair.png")
    result = dict(nativeFrame=meta["frame"], measurements=measurements)
    (args.frame / "parallax.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf8")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
