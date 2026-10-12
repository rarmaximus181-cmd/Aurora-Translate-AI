from PIL import Image, ImageDraw
from pathlib import Path
size = 256
im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.rounded_rectangle((5, 5, 251, 251), radius=59, fill='#0d1229')
for n in range(165, 0, -1):
    a = n / 165
    color = (int(70 + a*48), int(95 + (1-a)*77), int(200 + (1-a)*50), 255)
    d.rounded_rectangle((int(128-n*.63), int(128-n*.63), int(128+n*.63), int(128+n*.63)), radius=int(n*.29), fill=color)
d.polygon([(128, 51), (193, 198), (161, 198), (128, 117), (95, 198), (63, 198)], fill='#f4faff')
d.polygon([(128, 153), (146, 195), (110, 195)], fill='#63efff')
d.ellipse((181, 48, 206, 73), fill='#8af5ef')
im.save(Path(__file__).with_name('aurora.ico'), sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
