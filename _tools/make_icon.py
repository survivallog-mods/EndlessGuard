"""把 Unity 渲染的透明底源图裁成物品图标：去透明边、等比缩放、居中留边。

源图是透明清屏的渲染结果，边缘像素的颜色已是按覆盖率乘过的（预乘形式）；所以缩放时 RGB 与 alpha
一起缩，最后再除回 alpha——直接缩会在边缘留黑边。
用法：python make_icon.py <源图.png> <输出.png> [尺寸=256] [边距比例=0.06]
"""
import sys

import numpy as np
from PIL import Image


def main():
    src, dst = sys.argv[1], sys.argv[2]
    size = int(sys.argv[3]) if len(sys.argv) > 3 else 256
    margin = float(sys.argv[4]) if len(sys.argv) > 4 else 0.06
    im = Image.open(src).convert('RGBA')
    box = im.getchannel('A').point(lambda a: 255 if a > 8 else 0).getbbox()
    if box is None:
        raise SystemExit('源图全透明: ' + src)
    im = im.crop(box)
    inner = size * (1 - 2 * margin)
    k = min(inner / im.width, inner / im.height)
    out_size = (max(1, round(im.width * k)), max(1, round(im.height * k)))

    a = np.asarray(im.getchannel('A').resize(out_size, Image.LANCZOS)).astype(np.float64) / 255.0
    channels = []
    for c in range(3):
        resized = np.asarray(im.getchannel(c).resize(out_size, Image.LANCZOS)).astype(np.float64)
        channels.append(np.where(a > 1e-4, np.clip(resized / np.maximum(a, 1e-4), 0, 255), 0))
    rgba = np.dstack(channels + [a * 255.0]).round().astype(np.uint8)
    out = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    out.paste(Image.fromarray(rgba, 'RGBA'), ((size - out_size[0]) // 2, (size - out_size[1]) // 2))
    out.save(dst, optimize=True)
    print(dst, out.size)


if __name__ == '__main__':
    main()
