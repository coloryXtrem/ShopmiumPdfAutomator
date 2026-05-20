#!/usr/bin/env python3
import sys, math, random, os
from PIL import Image, ImageFilter, ImageEnhance, ImageDraw
import numpy as np

def log(msg): print(f"[COMPOSITE] {msg}", flush=True)

def remove_background(img_path):
    img = Image.open(img_path).convert("RGBA")
    data = np.array(img)
    r, g, b, a = data[:,:,0], data[:,:,1], data[:,:,2], data[:,:,3]
    lum = r.astype(np.float32)*0.299 + g*0.587 + b*0.114
    H, W = lum.shape
    corner_lum = np.mean([lum[0,0], lum[0,W-1], lum[H-1,0], lum[H-1,W-1]])
    if corner_lum < 30:
        is_bg = lum < 20
    elif corner_lum > 220:
        is_bg = lum > 235
    else:
        is_bg = lum < 15
    mask = Image.fromarray((~is_bg).astype(np.uint8)*255, mode="L")
    mask = mask.filter(ImageFilter.MaxFilter(3))
    mask = mask.filter(ImageFilter.GaussianBlur(1.5))
    data[:,:,3] = np.array(mask)
    return Image.fromarray(data, "RGBA")

def make_background(W, H, style="wood"):
    np.random.seed(42)
    bg_data = np.zeros((H, W, 4), dtype=np.uint8)
    for y in range(H):
        base = 185 + 28*math.sin(y*0.025) + 12*math.sin(y*0.07+1.2)
        grain_row = np.random.normal(0, 3.5, W)
        bg_data[y,:,0] = np.clip(base*1.06+grain_row, 145, 255).astype(np.uint8)
        bg_data[y,:,1] = np.clip(base*0.84+grain_row*0.7, 100, 210).astype(np.uint8)
        bg_data[y,:,2] = np.clip(base*0.58+grain_row*0.4, 55, 155).astype(np.uint8)
        bg_data[y,:,3] = 255
    bg = Image.fromarray(bg_data, "RGBA")
    vig = Image.new("RGBA", (W,H), (0,0,0,0))
    d = ImageDraw.Draw(vig)
    for i in range(60):
        alpha = int(70*(1-i/60)**2)
        d.rectangle([i,i,W-1-i,H-1-i], outline=(0,0,0,alpha))
    return Image.alpha_composite(bg, vig)

def apply_effects(img):
    result = img.convert("RGB")
    result = ImageEnhance.Color(result).enhance(1.15)
    result = ImageEnhance.Contrast(result).enhance(1.07)
    result = ImageEnhance.Brightness(result).enhance(1.03)
    result = result.filter(ImageFilter.UnsharpMask(radius=1.0, percent=70, threshold=3))
    W, H = result.size
    arr = np.array(result, dtype=np.float32)
    xs = (np.arange(W)-W/2)/(W/2); ys = (np.arange(H)-H/2)/(H/2)
    xx, yy = np.meshgrid(xs, ys)
    f = np.clip(1-0.18*(xx**2+yy**2)**0.9, 0.72, 1.0)[:,:,np.newaxis]
    return Image.fromarray(np.clip(arr*f,0,255).astype(np.uint8))

def place_products(bg, product, qty, OUT):
    W_p, H_p = product.size
    tw = {1:440,2:360,3:300,4:260,5:230}.get(qty,220)
    th = int(tw*H_p/W_p)
    configs = {
        1:[(OUT*0.5-tw//2,OUT*0.27)],
        2:[(OUT*0.13,OUT*0.24),(OUT*0.50,OUT*0.28)],
        3:[(OUT*0.04,OUT*0.23),(OUT*0.35,OUT*0.18),(OUT*0.64,OUT*0.22)],
    }
    positions = configs.get(min(qty,3), configs[3])
    scales = [1.0,0.96,0.92,0.89,0.86]
    angles = [0,-2.5,2,-1.5,1]
    random.seed(99)
    for i,(px,py) in enumerate(positions[:qty]):
        sc = scales[i%len(scales)]*random.uniform(0.97,1.03)
        ang = angles[i%len(angles)]+random.uniform(-0.5,0.5)
        pw,ph = int(tw*sc),int(th*sc)
        prod = product.resize((pw,ph),Image.LANCZOS)
        if abs(ang)>0.3:
            prod = prod.rotate(ang,expand=True,fillcolor=(0,0,0,0),resample=Image.BICUBIC)
            pw,ph = prod.size
        shw = Image.new("RGBA",(pw+30,int(ph*0.06)+30),(0,0,0,0))
        sd = ImageDraw.Draw(shw)
        sd.ellipse([15,10,pw+15,int(ph*0.06)+10],fill=(0,0,0,55))
        shw = shw.filter(ImageFilter.GaussianBlur(9))
        bg.paste(shw,(int(px)-15,int(py+ph-ph*0.03)),shw)
        bg.paste(prod,(int(px),int(py)),prod)
    return bg

def generate(img_path, qty, proof_type, output_path, ean=""):
    OUT = 1024
    product = remove_background(img_path)
    log(f"Produit detectoure: {product.size}")
    bg = make_background(OUT, OUT)
    if proof_type == "barcode":
        W_p,H_p = product.size
        sc = min(OUT*0.82/W_p, OUT*0.82/H_p)
        pw,ph = int(W_p*sc),int(H_p*sc)
        prod = product.resize((pw,ph),Image.LANCZOS).rotate(-4,expand=True,fillcolor=(0,0,0,0))
        pw,ph = prod.size
        bg.paste(prod,((OUT-pw)//2-20,(OUT-ph)//2+15),prod)
        result = bg.convert("RGB")
        draw = ImageDraw.Draw(result)
        bx1,by1 = (OUT-pw)//2-20+int(pw*0.38),(OUT-ph)//2+15+int(ph*0.65)
        bx2,by2 = bx1+int(pw*0.42),by1+int(ph*0.24)
        cx,cy = (bx1+bx2)//2,(by1+by2)//2
        bw,bh = bx2-bx1,by2-by1
        random.seed(77)
        for t in range(random.randint(2,3)):
            ang = math.radians(38+random.uniform(-12,12)+t*48)
            L = int(math.sqrt(bw**2+bh**2)*0.88)
            dx,dy = int(math.cos(ang)*L//2),int(math.sin(ang)*L//2)
            sx,sy = cx+random.randint(-6,6)-dx,cy+random.randint(-5,5)-dy
            ex,ey = cx+random.randint(-6,6)+dx,cy+random.randint(-5,5)+dy
            for w in range(8,0,-2):
                g = int(10+18*(1-w/8))
                draw.line([(sx,sy),(ex,ey)],fill=(g,g,g),width=w)
            draw.line([(sx+1,sy-1),(ex+1,ey-1)],fill=(25,22,20),width=2)
        margin = 35
        crop = (max(0,bx1-margin),max(0,by1-margin*2),min(OUT,bx2+margin),min(OUT,by2+margin))
        result = Image.open(output_path).crop(crop) if False else result.crop(crop)
        result = result.resize((OUT,OUT),Image.LANCZOS)
        result = apply_effects(result)
    else:
        bg = place_products(bg, product, qty, OUT)
        result = apply_effects(bg)
    result.save(output_path, quality=95)
    log(f"Sauvegarde: {output_path} ({os.path.getsize(output_path)//1024}KB)")

if __name__ == "__main__":
    if len(sys.argv) < 5:
        print("Usage: composite_image.py <img> <qty> <type> <out> [ean]"); sys.exit(1)
    generate(sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4],
             sys.argv[5] if len(sys.argv)>5 else "")
