# texfix.py — paints the real-brand marks out of the Sketchfab race-driver textures (the game uses made-up names only):
# every listed rectangle's pixels that differ from the rectangle's own median colour are filled by inpainting.
import numpy as np, cv2
from PIL import Image

MARKS = {  # texture file -> rectangles (x0, y0, x1, y1) in its own pixels
    'RT_DriverSuit_baseColor.png': [(225, 330, 435, 375), (150, 425, 220, 475), (440, 415, 510, 465), (770, 70, 820, 250), (770, 750, 820, 930)],
    'RT_Helemt_trasparent_baseColor.png': [(1290, 50, 1390, 100), (900, 130, 990, 210), (1630, 130, 1720, 210)],
    'RT_Gloves_baseColor.png': [(315, 395, 405, 475), (619, 395, 709, 475), (415, 355, 485, 445), (539, 355, 609, 445), (55, 205, 115, 255),
                                (909, 205, 969, 255), (180, 365, 240, 425), (784, 365, 844, 425), (125, 150, 195, 190), (829, 150, 899, 190)],
}


def clean(img, name):
    rects = MARKS.get(name)
    if not rects: return img
    a = np.asarray(img.convert('RGB')).copy(); mask = np.zeros(a.shape[:2], np.uint8)
    for x0, y0, x1, y1 in rects:
        sub = a[y0:y1, x0:x1].astype(float); med = np.median(sub.reshape(-1, 3), 0)
        dev = np.abs(sub - med).max(2) > 38
        mask[y0:y1, x0:x1][dev] = 255
    mask = cv2.dilate(mask, np.ones((5, 5), np.uint8))
    out = cv2.inpaint(a, mask, 6, cv2.INPAINT_TELEA)
    return Image.fromarray(out).convert(img.mode if img.mode in ('RGB', 'RGBA') else 'RGB')
