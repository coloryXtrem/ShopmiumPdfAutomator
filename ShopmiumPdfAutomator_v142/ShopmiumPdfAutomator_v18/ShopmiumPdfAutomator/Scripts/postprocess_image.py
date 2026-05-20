#!/usr/bin/env python3
"""
Post-traitement image : transforme une image IA en photo réaliste iPhone.
Usage: python postprocess_image.py <input_path> <output_path>
"""
import sys, io, random, datetime, os
from PIL import Image

def process_as_iphone_photo(input_path: str, output_path: str):
    # Heure de prise "il y a quelques minutes" 
    capture_time = datetime.datetime.now() - datetime.timedelta(
        minutes=random.randint(8, 180))
    dt_str = capture_time.strftime("%Y:%m:%d %H:%M:%S")

    img = Image.open(input_path).convert('RGB')
    W, H = img.size

    # Micro-rotation (main qui tient le téléphone)
    angle = random.uniform(-0.5, 0.5)
    if abs(angle) > 0.08:
        img = img.rotate(angle, expand=False, fillcolor=(235,228,220),
                         resample=Image.BICUBIC)

    # Léger recadrage naturel
    crop_pct = random.uniform(0, 0.012)
    cx, cy = int(W*crop_pct/2), int(H*crop_pct/2)
    if cx > 0 or cy > 0:
        img = img.crop((cx, cy, W-cx, H-cy))
        img = img.resize((W, H), Image.LANCZOS)

    # EXIF iPhone 15 Pro
    exif = Image.Exif()
    exif[0x010f] = "Apple"
    exif[0x0110] = "iPhone 15 Pro"
    exif[0x0131] = "17.4.1"
    exif[0x0132] = dt_str
    exif[0x013b] = ""
    try:
        exif[0x8769] = {
            0x9003: dt_str, 0x9004: dt_str,
            0x829a: (1, 60), 0x829d: (9, 5),
            0x8827: 400, 0xa405: (26, 1),
            0xa002: W, 0xa003: H,
        }
    except Exception:
        pass

    # JPEG qualité 88 (standard iPhone)
    img.save(output_path, format='JPEG',
             quality=88, optimize=True,
             progressive=False, subsampling=2,
             exif=exif.tobytes())

    print(f"[POSTPROCESS] OK: {os.path.getsize(output_path)//1024} KB → {output_path}")

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: postprocess_image.py <input> <output>")
        sys.exit(1)
    process_as_iphone_photo(sys.argv[1], sys.argv[2])
