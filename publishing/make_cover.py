from PIL import Image, ImageDraw, ImageFont
from pathlib import Path

out = Path(__file__).resolve().parent
W = H = 1024
im = Image.new("RGB", (W, H), "#0d1417")
d = ImageDraw.Draw(im)

font_reg = "C:/Windows/Fonts/msyh.ttc"
font_bold = "C:/Windows/Fonts/msyhbd.ttc"


def f(size, bold=False):
    return ImageFont.truetype(font_bold if bold else font_reg, size)


def text(x, y, s, size=26, color="#dce7e5", bold=False):
    d.text((x, y), s, font=f(size, bold), fill=color)


for y in range(H):
    c = (12 + int(y / 140), 20 + int(y / 150), 24 + int(y / 110))
    d.line((0, y, W, y), fill=c)

for x in range(40, 1024, 48):
    d.line((x, 0, x, 1024), fill="#172429")
for y in range(40, 1024, 48):
    d.line((0, y, 1024, y), fill="#172429")

d.rounded_rectangle((46, 46, 978, 978), 28, fill="#101b1f", outline="#3c585c", width=2)
d.rectangle((46, 86, 58, 286), fill="#9ae265")

text(88, 76, "ZED ZONE  ·  官方 10 语言支持  ·  MIT 开源", 23, "#96b5b4")
text(86, 118, "AI伴侣", 88, "#f3f7f1", True)
text(406, 148, "测试版 v0.3", 40, "#9ae265", True)
text(90, 232, "API 直连大模型  ·  DSH 本地 Agent 联动", 30, "#cbe0be")

cards = [
    ("01  聊天陪伴模式", "单人 / 联机共用左侧官方聊天栏 · 动态防复读提醒 · 背包 / 制造 / 拾取辅助", "#20332b", "#648f52"),
    ("02  接管雇佣 NPC", "按游戏内置机制精准协同 · 跟随 / 驻守 / 开火 / 搜集 / 手雷与治疗烟雾", "#1d2d34", "#4f7c8a"),
    ("03  联机玩家模式", "AI 专属独立联机角色 · 作为正常联机玩家入房参战与全房聊天同步", "#282834", "#6e6aa0"),
]
for idx, (title, desc, fill, outline) in enumerate(cards):
    y0 = 318 + idx * 124
    d.rounded_rectangle((86, y0, 938, y0 + 104), 16, fill=fill, outline=outline, width=2)
    text(114, y0 + 18, title, 29, "#f0f5ef", True)
    text(114, y0 + 60, desc, 20, "#c8d6d4")

d.rounded_rectangle((86, 706, 938, 822), 14, fill="#162126", outline="#3a4f54", width=2)
text(114, 722, "公平游戏边界 · 仅基于已知信息与现有材料行动", 24, "#e6c77a", True)
text(114, 764, "绝不剧透未解锁配方、未检视箱子或视野外目标｜所有写操作权限默认关闭", 20, "#b8c7c5")

pills = [("ESC 游戏选项进设置", 86), ("单人 Enter 官方聊天", 374), ("MIT 开源 · 欢迎反馈", 662)]
for label, x in pills:
    d.rounded_rectangle((x, 846, x + 276, 902), 10, fill="#22362a", outline="#5c7e48", width=2)
    text(x + 22, 860, label, 22, "#dcf0c2", True)

text(88, 924, "需订阅前置工坊 3834614258（ZED ZONE 代码模组加载器） · GitHub 开源", 20, "#8fa6a6")

target = out / "preview.png"
im.save(target, optimize=True)
print(target, target.stat().st_size)
