"""磁暴线圈特效审核图：把离线自测的帧（_candidates/tesla-coil/review）整理成可直接看的成图。

- compare_idle.png / compare_fire.png：上排 RA2 原版帧、下排本作帧，按建筑等高对齐
- review_chain.png：链电放电宽景 4 帧
- idle.gif / fire.gif / chain.gif：按游戏内节拍回放（待机 20Hz，放电 25Hz）
- compare_full.png：整屏缩到 1080p（实机 4K 的一半）看待机与放电全景
用法：python fx_review.py [ra2ref 目录]（默认 D:/temp/ra2ref，需有 active.gif / firing.gif）
"""
import os
import sys

from PIL import Image, ImageSequence

Review = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '_candidates', 'tesla-coil', 'review')
Ref = sys.argv[1] if len(sys.argv) > 1 else 'D:/temp/ra2ref'
Coil = (250, 80, 650, 680)          # 自测近景裁剪里线圈的框（400x600）
N = 6                              # 对比图每排帧数


def frame(name):
    return Image.open(os.path.join(Review, name)).convert('RGB')


def coil(name):
    return frame(name).crop(Coil)


def ref_frames(gif, k):
    im = Image.open(gif)
    bb = im.convert('RGBA').getbbox()
    box = (max(bb[0] - 4, 0), max(bb[1] - 2, 0), bb[2] + 4, bb[3])
    size = (round((box[2] - box[0]) * k), round((box[3] - box[1]) * k))
    return [f.convert('RGBA').crop(box).resize(size, Image.NEAREST) for f in ImageSequence.Iterator(im)]


def row(images, w, h, gap=8):
    out = Image.new('RGB', (len(images) * w + (len(images) - 1) * gap, h), (8, 8, 10))
    for i, im in enumerate(images):
        out.paste(im, (i * (w + gap) + (w - im.size[0]) // 2, 0), im if im.mode == 'RGBA' else None)
    return out


def compare(ref, mine, path, w, h):
    sheet = Image.new('RGB', (N * w + (N - 1) * 8, 2 * h + 24), (8, 8, 10))
    sheet.paste(row(ref, w, h), (0, 0))
    sheet.paste(row(mine, w, h), (0, h + 24))
    sheet.save(path)


def main():
    idle = [coil('idle_%02d.png' % i) for i in range(1, 13)]
    fire = [coil('fire_%d.png' % i) for i in range(1, 9)]
    w, h = idle[0].size
    ra2 = ref_frames(os.path.join(Ref, 'active.gif'), h / 87)      # RA2 建筑帧高 87px（active.gif 的 bbox）
    ra2f = ref_frames(os.path.join(Ref, 'firing.gif'), h / 87)
    compare(ra2[:N], idle[:N], os.path.join(Review, 'compare_idle.png'), w, h)
    compare(ra2f[:N], fire[:N], os.path.join(Review, 'compare_fire.png'), w, h)

    idle[0].save(os.path.join(Review, 'idle.gif'), save_all=True, append_images=idle[1:], duration=50, loop=0)
    fire[0].save(os.path.join(Review, 'fire.gif'), save_all=True, append_images=fire[1:], duration=40, loop=0)
    chain = [frame('chain_%d.png' % i).resize((750, 500), Image.LANCZOS) for i in range(1, 9)]
    chain[0].save(os.path.join(Review, 'chain.gif'), save_all=True, append_images=chain[1:], duration=40, loop=0)
    row(chain[:4], 750, 500).save(os.path.join(Review, 'review_chain.png'))

    full = Image.new('RGB', (1920, 1080 * 2), (0, 0, 0))
    for i, name in enumerate(('idle_full.png', 'fire_full.png')):
        full.paste(frame(name).resize((1920, 1080), Image.LANCZOS), (0, i * 1080))
    full.save(os.path.join(Review, 'compare_full.png'))
    print('ok ->', os.path.normpath(Review))


if __name__ == '__main__':
    main()
