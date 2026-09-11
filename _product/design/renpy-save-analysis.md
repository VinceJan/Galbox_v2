# Ren'Py 存档可提取信息 —— 实测分析报告

> **结论一句话**：从真实 Ren'Py 存档里，**"这个存档在哪段剧情"和"CG 解锁率"都能准确读出来**（不是猜，是有 label 级实测命中）；"章节进度"和"分支分组"只能近似，因为这个游戏本身**没有章节概念、也没有可用的路线进度变量**。
>
> 测试对象：`D:\GAME\Dreamin'_Her`（Ren'Py **7.4.11.2266** / Python 2.7）
> 测试日期：本次会话 ｜ 全部结论均基于**真实存档文件**实测，无伪造样本

---

## 1. 结论速览表

| # | 能力 | 判定 | 一句话依据（实测） |
|---|---|---|---|
| 1 | **存档节点标记**<br>（知道这个存档停在剧本哪一段） | ✅ **能准确做到** | 存档 pickle 内 `_history_list[N].voice.tlid` 的前缀**就是剧本 label 名**：对 `main_scenario.rpy` 的 262 个 label 实测 **22/22 命中**（如 `孤独感`、`だーれだ`、`七瀬家から帰宅`）。`Context.current` 为字符串时更是直接给出 label 名（`face`、`_call_characg_78`）。 |
| 2 | **章节进度** | 🟡 **能近似做到** | 游戏**根本没有"章节"概念**：`main_scenario.rpy`（21072 行）是 **262 个扁平 label**，全文搜不到 `chapter` 字段/变量。只能用「label 序号」或「已读语句数 / 总语句数」近似。实测已读 `main_scenario.rpy` **1373 / 全部** 语句，行区间 **550–4709 / 21073**。 |
| 3 | **CG 解锁率** | ✅ **能准确做到** | `gallery.rpy` 明确声明 **27 个 CG 槽位**（`gallery.image("NN01")`）；解锁状态存在 `persistent._seen_images`。实测**已解锁 6 / 27 = 22.2%**，可列出具体 ID：`0101, 0301, 0401, 0501, 0801, 2401`。游戏另有 3 个影片解锁位（`persistent.opmv / kakoed / granded`）。 |
| 4 | **分支分组** | 🟡 **能近似做到** | ⚠️ 游戏**确实定义了**路线标志（`hensu.rpy` 里的 `kakoroute`/`risounomirai`/`yumenomirai`…），且 `main_scenario.rpy` 有 `#現実ルート` `#夢現ルート` `### grandルート` 等分支注释；<br>**但这 12 个存档里一个都没有**（12/12 缺失）。原因见 §7.2：Ren'Py 只序列化「本局被改动过」的变量，而这些标志被赋值的行号（11461–17687）远超玩家实际到达的位置。可用的替代信号只有 `persistent._chosen`（仅 **4 条**菜单选择）和 `store.grand`（12/12 存在，值均为 `1`，但它在第 7 行就被赋值，**没有区分度**）。 |

> **给决策者的直接结论**：核心卖点 4 项里，**2 项可以做到产品级精度**（存档节点标记、CG 解锁率），**2 项只能做"近似 + 明确标注不确定性"**（章节进度、分支分组）。分支分组要靠**静态解析剧本**补，不能只靠存档。

---

## 2. 测试素材与环境

### 2.1 环境

| 项 | 值 | 来源 |
|---|---|---|
| Ren'Py 版本 | **7.4.11.2266** | `renpy/__init__.py:84` `version_tuple = (7, 4, 11, vc_version)`；`renpy/vc_version.py` `vc_version = 2266` |
| 版本代号 | Lucky Beckoning Cat | `renpy/__init__.py:87` |
| Python 运行时 | Python 2.7（`lib/python2.7/`） | 目录结构 |
| 存档格式版本 | `script_version = 5003000` | `renpy/__init__.py:109` |
| 补丁搜索路径 | `LCHH_RENPY_PATCH_SEARCHPATH="00patch"` | `environment.txt` |
| 本机分析工具 | Python 3.12.7（仅用标准库 `zipfile`/`pickle`/`zlib`/`pickletools`） | `python --version` |

### 2.2 存档位置：**两处都存在，且内容逐字节相同**

| 位置 | 内容 |
|---|---|
| `D:\GAME\Dreamin'_Her\game\saves\` | 12 个 `.save` + `persistent`（游戏本地存档目录） |
| `%APPDATA%\RenPy\dreamin_her-1631775296\` | 同样的 13 个文件 |

SHA256 逐一比对：**13/13 完全相同**（例：`1-1-LT1.save` → `4EC47ED469C1A920...`，`persistent` → `18A84224588D2BC6...`）。

**这不是"未解之谜"——原因已定位。** Ren'Py 7.4 的 `MultiLocation` 在每次保存时**同时写入两个位置**：

```python
# renpy/savelocation.py:426-435  （MultiLocation.save）
def save(self, slotname, record):
    saved = False
    for l in self.active_locations():      # ← 所有活跃位置都写一遍
        l.save(slotname, record)
        saved = True
```

两个位置在 `savelocation.py:556-570` 注册：

```python
# 1. User savedir.        → %APPDATA%\RenPy\<config.save_directory>
location.add(FileLocation(renpy.config.savedir))
# 2. Game-local savedir.  → <gamedir>/saves
if (not renpy.mobile) and (not renpy.macapp):
    path = os.path.join(renpy.config.gamedir, "saves")
    location.add(FileLocation(path))
```

而 `%APPDATA%\RenPy\` 下那个目录名，**由游戏自己的配置决定**，可确定性推导：

```renpy
# options.rpy:147
define config.save_directory = "dreamin_her-1631775296"
```

`config.save_directory` = `dreamin_her-1631775296`，与 APPDATA 下的目录名**逐字符一致**。
（该值可从 `archive.rpa` 里的 `options.rpy` 读出，见 §9 附录。）

> 同机 `%APPDATA%\RenPy\` 下还有 11 个其它游戏的目录（`DDLC-1454445547`、`the_question-7`、`peace-1760711164` …），**游戏本体目录不在里面**。所以"哪个 APPDATA 目录属于当前游戏"必须靠 `config.save_directory` 匹配，或退而用「`persistent` 里的 `_seen_ever` 文件名包含该游戏剧本路径」反查（实测有效：文件名是 `00patch/scenario/main_scenario.rpy`）。

### 2.3 `game\saves\` 实际内容（问题 1）

```
1-1-LT1.save        165,345 B   2026-04-09 00:18:58
1-5-LT1.save        165,336 B   2026-04-09 00:19:13
auto-1-LT1.save     192,215 B   2026-06-21 11:08:37   ← 最新（含手动存档之后的进度）
auto-10-LT1.save    170,982 B   2026-04-08 23:57:49   ← 最早
auto-2-LT1.save     165,184 B   2026-04-09 00:19:16
auto-3-LT1.save     164,319 B   2026-04-09 00:18:04
auto-4-LT1.save     183,050 B   2026-04-09 00:15:23
auto-5-LT1.save      54,183 B   2026-04-09 00:10:51   ← 最小（截图近乎纯色）
auto-6-LT1.save     148,130 B   2026-04-09 00:07:29
auto-7-LT1.save     181,367 B   2026-04-09 00:04:58
auto-8-LT1.save     181,448 B   2026-04-09 00:01:57
auto-9-LT1.save     167,085 B   2026-04-08 23:59:57
persistent           48,623 B   2026-06-21 11:08:39   ← 跨存档持久数据
```

格式判断（读文件头 8 字节）：**12 个 `.save` 全部是 ZIP**（`50 4B 03 04 14 00 00 00`）；`persistent` 是 **裸 zlib 流**（`78 5E ...`）。

---

## 3. 存档格式结构（实测，非推测）

### 3.1 ⚠️ 重要纠正：`.save` **不是**「zlib 压缩的 pickle」

任务书里的假设（以及一些网络资料）说 `.save` 是 zlib+pickle。**在这个版本里是错的。**

`renpy/loadsave.py:338-368`（`SaveRecord.write_file`）是权威依据：

```python
    def write_file(self, filename):
        with zipfile.ZipFile(filename_new, "w", zipfile.ZIP_DEFLATED) as zf:
            # Screenshot.
            if self.screenshot is not None:
                zf.writestr("screenshot.png", self.screenshot)
            # Extra info.
            zf.writestr("extra_info", self.extra_info.encode("utf-8"))
            # Json
            zf.writestr("json", self.json)
            # Version.
            zf.writestr("renpy_version", renpy.version)
            # The actual game.
            zf.writestr("log", self.log)
```

所以正确的分层是 **ZIP 容器 + 内部各自独立的 5 个条目**：

```
┌─ <slot>.save ── ZIP (ZIP_DEFLATED, magic "PK\x03\x04") ────────────────┐
│                                                                        │
│  screenshot.png    deflate   204,556 B(原始) / 1,098–139,547 B(压缩)   │  → 存档缩略图（PNG）
│  extra_info        deflate         0 B(原始)                           │  → UTF-8 字符串，= 玩家给的存档名
│  json              deflate        75 B(原始)                           │  → 元数据（JSON 文本，明文！）
│  renpy_version     deflate        18 B(原始)                           │  → "Ren'Py 7.4.11.2266"
│  log               deflate  267,000–290,000 B(原始)                    │  → ★ 游戏状态 = Python2 pickle
└────────────────────────────────────────────────────────────────────────┘

┌─ persistent ── 裸 zlib 流（magic 78 5E）→ 48,623 B → 解压 175,157 B ──┐
│  Python2 pickle，protocol 2，顶层对象 = renpy.persistent.Persistent     │
└────────────────────────────────────────────────────────────────────────┘
```

**实测 ZIP 目录（`1-1-LT1.save`）**：

```
ZIP entry: screenshot.png   compress_type=8  size=204556  compressed=104594
ZIP entry: extra_info       compress_type=8  size=     0  compressed=     2
ZIP entry: json             compress_type=8  size=    75  compressed=    65
ZIP entry: renpy_version    compress_type=8  size=    18  compressed=    20
ZIP entry: log              compress_type=8  size=290362  compressed= 60174
```

**`json` 条目原文**（12 个存档全部一字不差）：

```json
{"_save_name": "", "_renpy_version": [7, 4, 11, 2266], "_version": "1.0.2"}
```

**`renpy_version` 条目原文**：`Ren'Py 7.4.11.2266`
**`extra_info` 条目原文**：`b''`（0 字节）

### 3.2 `log`（pickle）内部结构

Pickle 头部：`80 02 7d 71 01 28 58 11 00 00 00 "store._side_image"...`
→ protocol 2，一个 `dict`，key 形如 `"store.<变量名>"`，value 是变量值。

对 `log` 做**受限反序列化**（`find_class` 全部返回惰性桩对象，不执行任何游戏代码）得到：

```
log pickle  =  (roots: dict, log: RollbackLog)
                │            │
                │            └─ RollbackLog
                │                 ├─ log      : list[129]  ← 129 个 Rollback 检查点
                │                 ├─ current  : Rollback     ← 当前状态
                │                 ├─ rollback_limit, forward, did_interaction ...
                │                 └─ current / log[i]
                │                       ├─ context   : renpy.execution.Context  ★执行位置
                │                       ├─ stores    : dict[15]                  ★变量命名空间快照
                │                       ├─ identifier, checkpoint, purged, random ...
                │
                └─ roots : dict[44]   key = "store.<var>"，只含"本局被改动过"的变量
                       ├─ "store._history_list"      : list[90]  ★对白历史（含 label 线索）
                       ├─ "store.nvl_list"           : list       NVL 模式对白
                       ├─ "store._version"           : "1.0.2"
                       ├─ "store.grand"              : 1          ★游戏自己的变量（仅此一类）
                       ├─ "store._last_say_what"     : "「诶？」"
                       ├─ "store._side_image", "store.quick_menu", ...
```

**`Context`（执行位置）字段全表（实测 `1-1-LT1.save`）**：

```
current              = '_call_characg_78'          ★ 当前语句名（字符串=label名 / 3元组=普通语句）
runtime              = 2363.117180585861           ★ 本 context 累计游玩时长（秒）
call_location_stack  = []                           调用位置栈
return_stack         = []                           返回栈
dynamic_stack        = [len=2]
translate_identifier = None                         本存档为 None（未启用翻译）
images / scene_lists / music / modes / say_attributes / line_log ...
```

**任意 pickle 里出现的 34 个类**（说明对象图非常浅，解析成本低）：

```
renpy.python.RevertableDict/List/Set/Object/CompressedList/RollbackLog/Rollback
renpy.execution.Context / Delete
renpy.character.HistoryEntry
renpy.display.*（layout.Null, core.SceneLists, transform.*, matrix.Matrix2D, image.*）
renpy.audio.audio.MusicContext ｜ renpy.styledata.styleclass.Style
store.VoiceInfo ｜ store.Shaker ｜ store._Shake ｜ store._console.TracedExpressionsList
collections.defaultdict ｜ __builtin__.dict ｜ __builtin__.set
```

**可读文本片段原样摘录**（`_history_list` 前几条，直接来自 pickle）：

```
[00] who='未 来' what='「嗯……」'      voice=audio/voice/mir10501.ogg tlid=face_62a63158
[01] who='未 来' what='「那就学校再见啦」' voice=audio/voice/mir10502.ogg tlid=face_072128f2
[02] who='苍'    what='「嗯，回见」'     voice=                          tlid=七瀬家から帰宅_354bf51c
[03] who='俊 之' what='「回家路上小心哟」' voice=audio/voice/tos10013.ogg tlid=face_1d61a575
[04] who=None    what='　背对着目送我的七濑一家，我踏上了回家的路。'  tlid=七瀬家から帰宅_31587ad0
...
[48] who='苍'    what='「……还是放弃比较好吧……」'  tlid=孤独感_fd1b87bf
[65] who='未 来' what='「猜——猜——我——是——谁！！」' voice=audio/voice/mir10503.ogg tlid=characg_98564bb1_1
```

**`_history_list` 每个 entry 的完整字段**（`renpy.character.HistoryEntry`）：

```
kind='adv'  image_tag='mirai'  who='未 来'  what='「嗯……」'
voice.tag='mirai'  voice.filename='audio/voice/mir10501.ogg'  voice.tlid='face_62a63158'
who_args={style,substitute}  what_args={style,slow_abortable,substitute}
window_args={style}  show_args={}  rollback_identifier=(1775662617.334479, 7922)
```

---

## 4. 逐条问题实测证据

### 问题 1：`game\saves\` 里有什么？

见 §2.3。**12 个真实 `.save` + 1 个 `persistent`**，全部可解析。

### 问题 2：能否解压？解压后是什么结构？

**能。** 但结构是 **ZIP**（不是 zlib）。见 §3。

解压方法（Python 标准库即可）：

```python
import zipfile, json
with zipfile.ZipFile(save_path) as zf:
    meta     = json.loads(zf.read("json").decode("utf-8"))   # 明文 JSON
    ver      = zf.read("renpy_version")                       # b"Ren'Py 7.4.11.2266"
    extra    = zf.read("extra_info")                          # b"" 
    pickle_b = zf.read("log")                                 # 真正的游戏状态
    png      = zf.read("screenshot.png")                       # 缩略图
```

`persistent`：

```python
import zlib
body = zlib.decompress(open(persistent_path, "rb").read())    # 175,157 B
```

### 问题 3：能否提取 `_save_name`（槽位名）？实际值是什么？

**能提取，但实际值是空字符串。**

12 个存档的 `json` **完全相同**：

```json
{"_save_name": "", "_renpy_version": [7, 4, 11, 2266], "_version": "1.0.2"}
```

`extra_info` 条目也全是 **0 字节**。

**为什么是空的？** 因为 `_save_name` 来自 `save_name` 全局变量，而这个游戏只在**一处**设置过它：

```renpy
# script.rpy:408
$ save_name =  "   プロローグ\n     テスト表示です"
```

这是测试代码，且**当前播放位置不在那段**，所以 12 个存档全部为空。

**游戏自己也没有做"存档节点命名"**。`utils/save_load_slot_name.rpy` 全文只有 25 行，功能仅是从**文件名**里切出页码：

```renpy
# utils/save_load_slot_name.rpy（全文）
init python:
    class SaveLoadSlotMethod(object):
        def __init__(self, slotName):
            self.slotName = slotName

        def getPage(self):
            if self.slotName == None:
                return None
            sp = self.slotName.split('-')
            if len(sp) <= 0:
                return None
            # 数字でない
            if sp[0].isdecimal() == False:
                return None
            return sp[0]
```

即 `1-1-LT1` → 第 1 页；`auto-1-LT1` → `auto` 不是数字 → `None`（自动存档不进分页）。
**结论：槽位名不能指望游戏提供，必须由 Galbox 自己从存档内容推导。**

### 问题 4：能否提取存档时间、游玩时长？

| 字段 | 能否提取 | 实际值 / 来源 |
|---|---|---|
| **存档时间** | ✅ 能 | **不在 `json` 里**（没有 `_save_time` 字段）。必须用**文件 mtime** —— 这正是 Ren'Py 自己的做法：`savelocation.py:176-184` `FileLocation.mtime()` → `self.mtimes.get(slotname)` → `os.path.getmtime()`。实测 `auto-1-LT1.save` = 2026-06-21 11:08:37。 |
| **游玩时长** | ✅ 能，但字段名不是 `_game_runtime` | `json` 里**没有** `_game_runtime`（该字段只在游戏主动注册 `config.save_json_callbacks` 时才存在；本游戏没注册）。真实来源是 **`log → current.context.runtime`**，float，秒。 |

`renpy/display/core.py:4246` 是它的累加点：

```python
renpy.game.context().runtime += end_time - start_time
```

**12 个存档实测 `runtime`（秒），与剧情推进严格单调对应**：

| 存档 | runtime (s) | 换算 | 存档 mtime |
|---|---|---|---|
| auto-10-LT1 | 1194.19 | ≈ 19.9 min | 04-08 23:57 |
| auto-9-LT1 | 1320.15 | ≈ 22.0 min | 04-08 23:59 |
| auto-8-LT1 | 1436.44 | ≈ 23.9 min | 04-09 00:01 |
| auto-7-LT1 | 1592.30 | ≈ 26.5 min | 04-09 00:04 |
| auto-6-LT1 | 1741.55 | ≈ 29.0 min | 04-09 00:07 |
| auto-5-LT1 | 1921.99 | ≈ 32.0 min | 04-09 00:10 |
| auto-4-LT1 | 2163.35 | ≈ 36.1 min | 04-09 00:15 |
| auto-3-LT1 | 2315.79 | ≈ 38.6 min | 04-09 00:18 |
| auto-2-LT1 | 2363.12 | ≈ 39.4 min | 04-09 00:19 |
| 1-1-LT1 | 2363.12 | ≈ 39.4 min | 04-09 00:18 |
| 1-5-LT1 | 2363.12 | ≈ 39.4 min | 04-09 00:19 |
| auto-1-LT1 | 2415.06 | ≈ 40.3 min | 06-21 11:08 |

> 注意：`renpy/execution.py:133` 的注释写的是 "in milliseconds"，但实测量级（1194–2415）只有按**秒**解释才合理（约 20–40 分钟，与 12 个 autosave 跨越 71 分钟、剧本推进到 21073 行中的 4709 行相符）。**单位以实测为准 = 秒；文档注释不可信。**

### 问题 5：能否推断"这个存档对应哪段剧情"？（★ 最关键）

**能，而且是 label 级精度。** 三个独立信号源，互相验证：

#### 信号源 A（最强）：`_history_list[N].voice.tlid` 的前缀 = 剧本 label 名

`tlid` 形如 `<label名>_<8位hash>` 或 `<label名>_<8位hash>_<序号>`。剥离尾部后**就是 label 名**。

**对 `main_scenario.rpy` 里的 label 做验证，22/22 全部命中**：

| tlid 前缀 | 是 `main_scenario.rpy` 的 label 吗？ | label 定义行 |
|---|---|---|
| `孤独感` | ✅ | 5757 |
| `だーれだ` | ✅ | 5896 |
| `七瀬家から帰宅` | ✅ | 5712 |
| `もうちょっとで夏休み` | ✅ | 4838 |
| `起こしに来る未来` | ✅ | 4674 |
| `俺結婚した？？` | ✅ | 4494 |
| `朝ごはんそれだけ？` | ✅ | 4387 |
| `付き合ってます？` | ✅ | 3281 |
| `紀伊国屋の疑問` | ✅ | 3610 |
| `絶妙なトロッコ問題` | ✅ | 3774 |
| `紅茶：お砂糖ふたつ` | ✅ | 5416 |
| `俊之帰宅オフ` | ✅ | 5616 |
| `漫画を詰めに蒼の部屋` | ✅ | 3990 |
| `合鍵` | ✅ | 4105 |
| `七瀬邸へ` | ✅ | 5170 |
| `昔から優しかった` | ✅ | 5053 |
| `漫画を返したい` | ✅ | 5125 |
| `念の為お掃除しておく` | ✅ | 3466 |
| `一緒に帰る` | ✅ | 2984 |
| `未来の部活漬け` | ✅ | 3005 |
| `パターンB` | ✅ | 3036 |
| `昔もこういう話してたよね` | ✅ | 3080 |

（另有 2 个**不是** `main_scenario.rpy` 的 label：`face`（在 `face.rpy`）、`characg_98564bb1`（屏幕参数生成的名字）。）

**真实 label 源码原样摘录**：

```renpy
label 孤独感:                          # main_scenario.rpy:5757
    call se("door") from _call_se_35
    call bg('lvnt') from _call_bg_74   # リビング夜
    aoi "「………」"

label だーれだ:                         # main_scenario.rpy:5896
    call date("yoku") from _call_date_4 #日付：●
    call kuro from _call_kuro_12
    call characg(mirai,"mir10503","「だー……れ、だっ！！」") from _call_characg_71
    call m('mirai') from _call_m_42

label 七瀬家から帰宅:                     # main_scenario.rpy:5712
    call bg('nanayu') from _call_bg_73  # 七瀬家前夕方
    call chara(mirai,"mir10499",'h0sc1sm02',"「また、漫画読み終わったら言うね」") from _call_chara_780
    aoi "「うん、早くネタバレ感想言いたいから」"
```

#### 信号源 B：`Context.current` —— 字符串时**直接就是 label 名**

`execution.py:508`：`self.current = node.name`（`node` 是即将执行的语句节点）。

- `node` 是 **label 语句** → `name` = label 名字符串（`ast.py:841` `Label.__init__: self.name = name`）
- `node` 是**普通语句** → `name` = 3 元组，由 `script.py:320-330` 分配：

```python
# renpy/script.py:320
def assign_names(self, stmts, fn):
    all_stmts = collapse_stmts(stmts)
    version = int(time.time())            # ← 编译时刻的时间戳
    for s in all_stmts:
        if s.name is None:
            s.name = (fn, version, self.serial)   # ← (文件名, 编译时间, 全局语句序号)
            self.serial += 1
```

> ⚠️ **重要细节（我一开始判断错了，靠交叉验证纠正过来）**：3 元组的第 3 个元素是**全局语句递增序号**，**不是行号**！它不是 `lineno`。
> 我最初把它当行号映射到 label，得到的结果"看起来合理"（5/5 都落在真实 label 内），但那是**巧合**——因为序号和行号都单调递增。后来发现「历史记录里出现的 label 定义行号（5712+）比 `Context.current` 里的数字（2631）还大」这个矛盾，才回溯到 `script.py:329` 找到真相。**不要用这个数字做行号映射。**

**实测 12 个存档的 `Context.current`**：

| 存档 | `Context.current` | 类型 | 解析 |
|---|---|---|---|
| 1-1-LT1 / 1-5-LT1 / auto-2-LT1 | `'_call_characg_78'` | 字符串 | `from` 生成标签 → 所属 label = **`だーれだ`** |
| auto-3-LT1 | `('00patch/scenario/main_scenario.rpy', 1654702878, 4509)` | 3 元组 | 普通语句（序号，非行号）→ 用信号源 A |
| auto-4-LT1 | `'_call_chara_694'` | 字符串 | → 所属 label = **`七瀬邸へ`** |
| auto-6-LT1 | `'face'` | 字符串 | **直接就是 label 名** `face` |
| auto-7-LT1 | `'_call_chara_550'` | 字符串 | → 所属 label = **`合鍵`** |
| auto-9-LT1 | `'_call_ss_20'` | 字符串 | `from` 标签 |
| auto-1 / auto-5 | `('game/macro/macro_bg.rpy', 1650991685, 716)` | 3 元组 | 在宏文件里，用信号源 A |
| auto-8 | `('00patch/scenario/main_scenario.rpy', 1654702878, 3090)` | 3 元组 | 用信号源 A |
| auto-10 | `('00patch/scenario/main_scenario.rpy', 1654702878, 2631)` | 3 元组 | 用信号源 A |

`from _call_xxx_N` → 所属真实 label 的映射可以**静态建立**：扫描剧本全文的 `... from _call_xxx_N`，取其外层 label。实测从 `main_scenario.rpy` 解析出 **3995 条** 映射，抽样验证正确：

```
_call_mk0_2   -> label act00 (line 5)
_call_anten_7 -> label act00 (line 5)
_call_siro    -> label プロローグ：無垢な世界 (line 12)
_call_m_7     -> label プロローグ：無垢な世界 (line 12)
```

#### 信号源 C：`_history_list` 最近条目 → 当前位置

每条历史都带 `tlid`，**最后一条**最接近当前位置。实测 12/12 存档都能解出可读场景名：

| 存档 | 该存档最后 3 条对白（label 名 + 原文） |
|---|---|
| **1-1-LT1** | `だーれだ` →「　为什么突然害羞了啊！？」；`characg_98564bb1` → 未 来「「……然后呢？」」 |
| **auto-1-LT1** | `進路希望について` →「　在教室的角落一边看书一边吃买来的便当的我，被班主任海老原老师叫出去了。」；`進路希望について` → 苍「「……？」」 |
| **auto-10-LT1** | `昔もこういう話してたよね` →「　我们曾是关系很好，可以毫无顾忌地在彼此家里玩的伙伴。」；「　真是不可思议。」 |
| **auto-3-LT1** | `孤独感` →「　感觉有点脸上发烧，不过还是继续往下翻着评论。」；「　这种负面的评论并不多。」 |
| **auto-4-LT1** | `face` → 未 来「「妈妈，苍君最近学习很忙的」」；`face` → 爱「「呀，原来是这样吗？」」 |
| **auto-5-LT1** | `たまにからかわれるんだ` → 苍「「于是对方就老老实实地认同了这个说法吗」」；`face` → 未 来「「嗯，因为对方也都是好孩子啦」」 |
| **auto-6-LT1** | `face` → 未 来「「特别简单的哦？　还比外边卖的更健康」」；`俺結婚した？？` → 苍「「你都在哪学的？」」 |
| **auto-7-LT1** | `合鍵_e3cb1b97` →「　………」；`face` → 未 来「「啊……」」 |
| **auto-8-LT1** | `face` → 未 来「「所以呢？」」；`絶妙なトロッコ問題` → 苍「「唔……」」 |
| **auto-9-LT1** | `念の為お掃除しておく` →「　装模作样得有些过了。」；「　母亲真是一直在某些地方过于敏锐呢……」 |

**每个存档的完整场景 label 集合**（去重后）：

```
1-1-LT1.save     : だーれだ, 七瀬家から帰宅, 孤独感
auto-1-LT1.save  : だーれだ, よくやってたっけ？, 孤独感, 進路希望について
auto-3-LT1.save  : ヒバリとナイチンゲール, 七瀬家から帰宅, 俊之帰宅オフ, 孤独感, 紅茶：お砂糖ふたつ
auto-4-LT1.save  : 七瀬邸へ, 昔から優しかった, 漫画を返したい
auto-5-LT1.save  : たまにからかわれるんだ, もうちょっとで夏休み, 起こしに来る未来
auto-6-LT1.save  : 俺結婚した？？, 朝ごはんそれだけ？, 相変わらず朝弱いんだね
auto-7-LT1.save  : 合鍵, 漫画を詰めに蒼の部屋
auto-8-LT1.save  : 何巻まで出てるの？, 紀伊国屋の疑問, 絶妙なトロッコ問題
auto-9-LT1.save  : まだ漫画持ってる？, 付き合うなんてこと, 付き合ってます？, 念の為お掃除しておく
auto-10-LT1.save : パターンB, 一緒に帰る, 昔もこういう話してたよね, 未来の部活漬け
```

**有 `scene`/`label`/`chapter` 之类专用字段吗？**
**没有专用的"当前章节"字段**，但**有可解析的位置信息**（上表）。`Context` 里没有叫 `scene`/`chapter`/`_last_scene` 的字段；有效信息全在 `current` 和 `_history_list[].voice.tlid` 里。

**游戏自定义变量有没有指示剧情位置？**
有 1 个能读到：`store.grand`（12/12 存档均为 `1`，但它在 `main_scenario.rpy:7` 就被赋值，**没有区分度**）。其余路线变量见 §4 问题 7。

### 问题 6：能否提取 CG 解锁率 / 路线解锁？

**CG 解锁率：能准确算出。** 数据在 **`persistent`**，不在存档里。

**分母**来自 `gallery.rpy`（从 `archive.rpa` 抽出的原文）：

```renpy
# gallery.rpy（节选）
init python:
    gallery = Gallery()
    gallery.locked_background = "gui/gallery/cover.png"
    gallery.unlocked_advance = True

    gallery.button("gui/gallery/01unlock")
    gallery.image("0101")          # ← 槽位
    gallery.unlock("0101")
    gallery.button("0101")
    gallery.unlock_image("0101")   # ← 该槽位下的具体 CG
    ...
```

实测统计：

```
gallery.button() calls     : 54   （27 个槽位按钮 + 27 个 CG 按钮）
gallery.image()  calls     : 27   → ★ 分母 = 27 个 CG 槽位
gallery.unlock_image()     : 132 distinct  （槽位下的具体立绘/CG 变体）
```

**分子**来自 `persistent._seen_images`。链路已从引擎源码确认：

```
renpy/common/00gallery.rpy:45   if not renpy.seen_image(i):    ← Gallery 判断解锁
        ↓
renpy/exports.py:2427           return name in renpy.game.persistent._seen_images
renpy/exports.py:2440           renpy.game.persistent._seen_images[name] = True
        ↓
persistent.py:292               register_persistent("_seen_images", dictset_merge)
```

**实测结果**：

```
unlocked in persistent : 6 / 27  (22.2%)
unlocked CG ids        : ['0101', '0301', '0401', '0501', '0801', '2401']
still locked           : ['0201','0601','0701','0901','1001','1101','1201','1301','1401','1501',
                          '1601','1701','1801','1901','2001','2101','2201','2301','2501','2601','2701']
```

`persistent._seen_images` 共 **283 个 key**，构成：

- **27 个** CG 槽位 ID（形如 `0101`、`2401`、`150407`、`17shoka`、`3haru`）
- **约 217 个** 角色立绘合成 key（元组形式，如 `('mirai','hf1','sc01','ma02','ey01','mo05','ase0','hf1')`）
- 其余为背景 bg01–bg203、`black`、`cl` 等

> ⚠️ 陷阱：**不能**直接拿 `len(_seen_images)` 当 CG 解锁数（283 ≠ 已解锁 CG 数），必须**用 `gallery.image()` 声明的 27 个 ID 做交集**。

**影片解锁**（`moviegallery.rpy` 原文）：

```renpy
if persistent.opmv:      hotspot (217, 319, 395, 224) action Play("movie", "opmv.webm") ...
if persistent.kakoed:    hotspot (749, 321, 394, 222) action Play("movie", "kakoed.webm") ...
if persistent.granded:   hotspot (1290, 321, 393, 222) action Play("movie", "granded.webm") ...
```

对应赋值点（剧本原文）：

```
main_scenario.rpy:9228   $ persistent.opmv = True
main_scenario.rpy:19822  $ persistent.granded = True
scenario/kakoend.rpy:771 $ persistent.kakoed = True
```

**路线解锁**：`persistent._chosen` 记录了玩家选过的菜单项，只有 **4 条**：

```
(('00patch/scenario/main_scenario.rpy', 1654702878, 1054), '保持沉默')            -> True
(('00patch/scenario/main_scenario.rpy', 1654702878, 3091), '最爱的妻子一个人')     -> True
(('00patch/scenario/main_scenario.rpy', 1654702878, 1815), '盐烤青花鱼')          -> True
(('00patch/scenario/main_scenario.rpy', 1654702878, 1546), '调查一下看看吧')       -> True
```

即：`((剧本文件, 编译时间戳, 语句序号), 选项文本) -> True`。**这是唯一可直接读到的"玩家做过什么选择"记录。**

### 问题 7：游戏脚本里有"章节"概念吗？

**没有 `chapter`。** 但有 262 个 label 和一套路线标志。

**（a）剧本结构**：`main_scenario.rpy` = **21,073 行 / 262 个 label**，扁平结构。

```
act00
プロローグ：無垢な世界
幼馴染なんて脆いもの
回想・サンドバッグ
彼女と話すこともなくなった
桜の再会
夢世界の断片
２年夏・紀伊国屋のパソコン部勧誘
通りすがる未来
やっぱり陰キャじゃないスか
未練たっぷり
夢１・幼稚園
朝がめちゃ弱い蒼
３年・春
言い返してみる
黙っておく
小説は書かないんですか
未来と同じクラス
一緒に帰ろう
...
```

label 名是**场景标题式日语**，天然适合做"剧情节点"显示名。全文搜索 `chapter` / `章` 只命中台词文本（如「文章を書くのは好きだ」，是「文章」不是「章」），**没有章节机制**。

**（b）但游戏确实有"路线/结局"标志** —— `scenario/hensu.rpy` 全文：

```renpy
label hensu:

    define isdebag = 0
    define miraipt = 0
    define kakopt = 0
    define gacha = 0
    define moguwani = 0
    define kakoroute = 0      # 過去ルート = 过去线
    define grand = 0          # グランドルート
    define lie = 0
    define know_yokkyuuhuman = 0
    define know_flashback = 0
    define know_mondai = 0
    define know_senzai = 0
    define erosion = 0
    define risounomirai = 0   # 理想の未来 = 理想未来
    define realyou = 0
    define yumenomirai = 0    # 夢の未来 = 梦之未来
    define henshin = 0
    define tadanoyume = 0
    define ganbou = 0
    define ijigen = 0
    define kokkai = 0
    define move_size = 32

    return
```

剧本里的分支注释与赋值点：

```
main_scenario.rpy:7       $ grand = 1
main_scenario.rpy:11461   $ know_flashback = 1
main_scenario.rpy:11824   $ risounomirai = 1
main_scenario.rpy:15219   $ grand = -1 #g以外
main_scenario.rpy:16160   $ grand = -1 #grand以外+1
main_scenario.rpy:16808   $ grand = -1
main_scenario.rpy:17687   $ kakoroute = 1

main_scenario.rpy:17828   #架子ルート
main_scenario.rpy:17829   if kakoroute == 1:
main_scenario.rpy:17832   #現実ルート #0401
main_scenario.rpy:17843   ### grandルート
main_scenario.rpy:17968   #夢現ルート
main_scenario.rpy:18413   #グランドルート
```

**所以"路线"这个概念在游戏里是存在的，但只以 store 变量形式存在，而这些变量在本批存档里全部读不到 —— 见下。**

### ★ 关键实测：这批存档里**没有**任何路线进度变量

对 12 个存档做**受限反序列化 + 原始字节双重扫描**，21 个 `hensu.rpy` 变量的命中情况：

```
file               isde mira kako gach mogu kako gran lie  know know know know eros riso real yume hens tada ganb ijig kokk move
1-1-LT1.save       .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .
1-5-LT1.save       .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .
auto-1-LT1.save    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .
auto-10-LT1.save   .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .    .
（全部 12 行同此）

独立原始字节扫描（不依赖反序列化）：
   kakoroute        in  0/12 saves
   risounomirai     in  0/12 saves
   erosion          in  0/12 saves
   know_flashback   in  0/12 saves
   yumenomirai      in  0/12 saves
   grand            in 12/12 saves    ← 唯一存在的
```

**原因已从引擎源码确认** —— `renpy/python.py:1757`（`RollbackLog.get_roots`）：

```python
def get_roots(self):
    """
    Return a map giving the current roots of the store. ...
    A variable is only in this map if it has ever been changed
    since the init phase finished.          ← ★★ 关键
    """
    rv = { }
    for store_name, sd in store_dicts.items():
        for name in sd.ever_been_changed:     # ← 只导出"被改动过"的变量
            ...
```

**推论（重要）**：
1. Ren'Py **不会**把没动过的变量写进存档。`define X = 0` 且全程没赋值的变量，存档里**根本不存在**——不等于它是 0，而是"读不到"。
2. 这批存档没读到 `kakoroute` 等，**不等于游戏没有路线系统**，而是**玩家没走到那些分支**。交叉验证：`persistent._seen_ever` 显示玩家在 `main_scenario.rpy` 只读过 **1373 句、行区间 550–4709**，而路线标志赋值点在 **11461–17687** —— 确实没到。
3. `grand` 之所以存在，是因为它在 **第 7 行**（游戏一开始）就被 `$ grand = 1` 赋值了。

**总进度实测**（`persistent._seen_ever`，按文件分组）：

```
00patch/scenario/main_scenario.rpy     seen= 1373 语句   行区间    550 .. 4709
game/macro/macro_face_pattern.rpy      seen=  174 语句   行区间   1086 .. 19896
00patch/macro/macro_screen.rpy         seen=   46 语句   行区间     19 .. 216
game/macro/macro_bg.rpy                seen=   43 语句   行区间    656 .. 966
game/macro/macro_se.rpy                seen=   12 语句
game/macro/macro_date.rpy              seen=   10 语句
game/scenario/splash.rpy               seen=    8 语句
game/macro/macro_face.rpy              seen=    2 语句
renpy/common/00gamemenu.rpy            seen=    1 语句
renpy/common/00gltest.rpy              seen=    1 语句
（main_scenario.rpy 全文 21073 行）
```

`_seen_ever` 共 **2887 个 key**，两种形态：

- **1217 个字符串 key**（label 名 / `_call_*` 自动名）—— 由 `execution.py:623` 写入：
  ```python
  if self.seen:
      renpy.game.persistent._seen_ever[self.current] = True
  ```
  抽样验证：**83 个**是 `main_scenario.rpy` 的精确 label 名（`TALKの続き`、`あおくん！`、`だーれだ`、`もうちょっとで夏休み` …），其余是 `_call_anten_7`、`_after_load` 这类自动名。
- **1670 个 3 元组 key** `(文件名, 编译时间戳, 全局语句序号)` —— 语句级"已读"记录（序号语义见 §4 问题 5 的警告）。

### 问题 8 见 §1 表格与 §5 完整论述。

---

## 5. 问题 8 完整回答：四项能力各自能做到什么程度

### ✅ 能准确做到

#### (1) 存档节点标记 —— 准确

**依据**：`log` pickle → `_history_list[N].voice.tlid` 前缀 = 剧本 label 名，对 `main_scenario.rpy` 的 262 个 label 实测 **22/22 命中**；`Context.current` 为字符串时**本身就是 label 名或 `from` 标签名**（`face`、`_call_characg_78` → `だーれだ`）。

**能做到的产品形态**：
- 每个存档显示「**当前场景名**」：如 `auto-3` → `孤独感`（「孤独感」，主角深夜刷差评后自我否定的那段）
- 显示「**最近经过的场景序列**」：如 `auto-8` → `紀伊国屋の疑問 → 絶妙なトロッコ問題 → 何巻まで出てるの？`
- 显示「**这条路线上读到的最后一句对白**」作为节点描述（原文可读，见 §4 问题 5 表格）

**精度边界**：能精确到 **label（场景）**级，不能精确到"第几行/第几句话"。

#### (3) CG 解锁率 —— 准确

**依据**：`gallery.rpy` 硬编码 27 个 CG 槽位作分母；`persistent._seen_images` 作分子；引擎链路 `00gallery.rpy:45 → exports.py:2427 → persistent._seen_images` 已确认。实测 6/27 = 22.2%。

**能做到的产品形态**：CG 图鉴页显示「6 / 27 (22.2%)」，并高亮未解锁槽位。

### 🟡 能近似做到

#### (2) 章节进度 —— 近似

**依据**：游戏**没有 `chapter` 概念**（262 个扁平 label，全文无 `chapter` 字段）。但可以做三种近似，精度递增：

| 近似方案 | 依据 | 精度 | 代价 |
|---|---|---|---|
| A. **场景 label 序号** | 262 个 label 按定义顺序编号，存档所在 label 的序号 / 262 | 低（label 长度不均，会严重失真） | 低 |
| B. **已读语句数 / 总语句数** | `persistent._seen_ever` 中 `main_scenario.rpy` 的条目数 ÷ 编译产物的语句总数 | 中（实测 1373 句 / 总语句数） | 中（需解析 `.rpyc` 取总语句数） |
| C. **已读行区间 / 总行数** | `_seen_ever` 行号 max ÷ 剧本总行数 | 中（实测 4709 / 21073 ≈ 22%） | 低 |

⚠️ **方案 C 有坑**：`_seen_ever` 的 3 元组第 3 个元素是**全局语句序号，不是行号**（见 §4 问题 5 警告）。若用方案 C 必须重新建立"序号 → 行号"映射，或改用字符串 label key 的数量做分母。

**推荐**：不做"章节百分比"，改做「**已解锁场景数 / 总场景数**」——即拿 `persistent._seen_ever` 的字符串 key 与剧本 262 个 label 求交集。实测 83+ 个已解锁场景，语义比"章节进度"更诚实、更可解释，也不会因为 label 长短不均而失真。

#### (4) 分支分组 —— 近似

**依据**：游戏有路线标志（`kakoroute`/`risounomirai`/`yumenomirai`…）和分支注释（`#現実ルート` `#夢現ルート` `### grandルート`），**但这批存档里 12/12 读不到**（玩家没走到赋值点 11461–17687）。唯一可用的运行时信号是 `persistent._chosen`（仅 4 条）。

**能做到的产品形态**（按可靠性排序）：
1. **按"经过的场景集合"聚类**（可靠）：每个存档的 label 集合是实测得到的，重合度高的存档自然归为一组。如上表 `1-1/1-5/auto-2` 三个存档的场景集合完全一致（`だーれだ, 七瀬家から帰宅, 孤独感`）→ 可判定为同一分支/同一段剧情。
2. **显示已选选项**（可靠但稀疏）：`persistent._chosen` 的 4 条选择，可列出「你选过：保持沉默 / 调查一下看看吧 / 盐烤青花鱼 / 最爱的妻子一个人」。
3. **静态解析剧本建立分支树**（可靠但需额外工作）：从 `.rpy` 里解析 `menu:`、`if kakoroute == 1:`、`#○○ルート` 注释，建出分支图 —— **这是唯一能得到"真正的分支结构"的途径**。
4. **读路线变量**（**不可靠，不要依赖**）：只有当玩家真的走到赋值点、且 Ren'Py 把它写进存档时才有值。本批存档 0/12。

### ❌ 做不到

**没有严格"做不到"的项**，但有两项**不能承诺**：

- ❌ **不能承诺"从任意 Ren'Py 游戏的存档里通用地读出章节/路线"** —— 本报告全部结论基于这一个游戏 + 一个引擎版本实测。见 §7。
- ❌ **不能读出存档里不存在的变量** —— `get_roots()` 的"只导出改动过的变量"机制决定了：没动过的路线标志在存档里**不存在**，不是 0。任何"读变量判断路线"的方案都必须自带 fallback。

---

## 6. 给实现方的建议

### 6.1 推荐读取的字段（按优先级）

| 优先级 | 字段 | 位置 | 用途 | 实测可用性 |
|---|---|---|---|---|
| ★★★ | `_history_list[N].voice.tlid` | `log` pickle → `roots["store._history_list"]` | **当前场景 label 名 + 场景序列** | 12/12 ✅ |
| ★★★ | `persistent._seen_images` ∩ `gallery.image()` 列表 | `persistent` + 剧本静态解析 | **CG 解锁率** | ✅ 6/27 |
| ★★★ | 存档文件 mtime | 文件系统 | **存档时间**（Ren'Py 自己就这么用） | ✅ |
| ★★☆ | `log → current.context.runtime` | `log` pickle | **游玩时长（秒）** | 12/12 ✅ |
| ★★☆ | `Context.current` | `log` pickle | 当前语句名 → 字符串时直接是 label | 5/12 直接可用，7/12 需 fallback |
| ★★☆ | `persistent._seen_ever` 的**字符串 key** | `persistent` | **已解锁场景集合 / 总进度** | ✅ 1217 keys |
| ★★☆ | `persistent._chosen` | `persistent` | **玩家选过的选项**（分支证据） | ✅ 4 条 |
| ★☆☆ | `json` 条目的 `_save_name` / `_version` / `_renpy_version` | ZIP 内 | 槽位名（常为空）、游戏版本 | ✅（`_save_name` 全空） |
| ★☆☆ | `screenshot.png` | ZIP 内 | 缩略图 | ✅ 204,556 B |
| ☆☆☆ | `store.grand` 等游戏变量 | `log` pickle → `roots["store.*"]` | 路线标志（**本批全缺**） | ⚠️ 不可依赖 |

### 6.2 分步实施 + 验收判据

**Step 0 — 定位存档目录**（0.5 天）
- 从 `options.rpy` 读 `config.save_directory` → 拼 `%APPDATA%\RenPy\<值>`
- 同时扫 `<gamedir>\saves\`
- **验收**：两个路径都能列出 12 个 `.save` + `persistent`；文件大小/哈希一致（本作 13/13 相同）

**Step 1 — 读元数据，不碰 pickle**（1 天）
- 用 ZIP 读 `json` / `renpy_version` / `extra_info` / `screenshot.png` / 文件 mtime
- **验收**：12/12 存档输出 `{槽位名:"", 版本:"1.0.2", Ren'Py:"7.4.11.2266", mtime, 缩略图}`；**此步零风险，可先上线**

**Step 2 — 受限反序列化 `log`**（3–5 天）
- 实现 `find_class` 白名单 + 桩对象（**绝不能裸 `pickle.loads`**，见 §7.4）
- 取 `roots["store._history_list"]`、`current.context.runtime`、`current.context.current`
- **验收**：
  - 12/12 存档能取出 90 条历史 + runtime（1194.19–2415.06 区间内）
  - runtime 与存档时间单调一致（本批完全吻合，见 §4 问题 4 表）
  - **不执行任何游戏代码**（用 `pickletools.genops` 先做静态审计，确认无反序列化副作用）

**Step 3 — 建立 label 词典**（3–5 天）
- 从 `.rpy`（RPA 内）或 `.rpyc` 提取全部 label 名 + 行号 + `from _call_*` 映射
- **验收**：能复现本报告 §4 问题 5 的 **22/22** 命中；能从 `_call_characg_78` 解出 `だーれだ`
- **注意**：**必须用补丁版脚本**（见 §7.1）

**Step 4 — 存档节点标记**（3 天）
- `tlid 前缀 → label 名 → 显示名`；`Context.current` 字符串直接当 label，3 元组时用历史 fallback
- **验收**：12/12 存档都能显示一个**日语场景名**（对照 §4 问题 5 的表格，应完全一致）

**Step 5 — CG 解锁率**（2 天）
- 从 `gallery.rpy` 解析 `gallery.image(...)` 得 N=27；`persistent._seen_images` 求交
- **验收**：输出 `6/27 = 22.2%`，且 ID 集合 = `{0101,0301,0401,0501,0801,2401}`

**Step 6 — 分支分组**（5–8 天，**风险最高**）
- 优先做「按场景集合聚类」+「显示 `_chosen` 选项」；「静态解析分支树」作为增强
- **验收**：`1-1/1-5/auto-2` 三个场景集合完全相同的存档被归为同一组
- **不要在验收标准里写"读出路线名"** —— 本批存档给不出

### 6.3 性能预期（实测数据）

| 项 | 实测 |
|---|---|
| `.save` 文件大小 | 54 KB – 192 KB |
| `log` pickle 解压后 | 267 KB – 290 KB |
| pickle opcode 数 | 80,841 |
| pickle 里出现的类 | 仅 **34** 个 |
| 字符串常量 | 977 个（791 distinct） |
| 解析单存档总耗时 | 亚秒级（纯 Python 标准库，无外部依赖） |

> **结论：性能完全不是问题**，可以直接对全部存档做全量扫描、实时解析，无需缓存层。

---

## 7. 坑与边界（★ 重点）

### 7.1 ⚠️ 同一个剧本有**两份**，行号会漂移 —— **必须以补丁版为准**

这是本游戏最隐蔽的坑。同一个 `scenario/main_scenario.rpy` 在**两个 RPA 里各有一份**：

| 来源 | 大小 | 行数 | label 数 | SHA1 |
|---|---|---|---|---|
| `game/archive.rpa` → `scenario/main_scenario.rpy` | 886,973 B | 21,073 | 262 | `61ec11dcf2fe3167…` |
| **`00patch/00patch.rpa` → `scenario/main_scenario.rpy`** | **802,484 B** | **21,072** | 262 | `ad6709b417adff97…` |

**字节不同**（连 UTF-8 都解不干净，尾部有 `0xa3` 字节）。label 集合完全一致（262 个，交集 262、差集 0），但**行号整体漂移 ±1 行**：

```
act00                     archive:5       active:5       drift=+0
プロローグ：無垢な世界           archive:12      active:12      drift=+0
幼馴染なんて脆いもの             archive:76      active:75      drift=+1
回想・サンドバッグ              archive:106     active:105     drift=+1
桜の再会                    archive:215     active:214     drift=+1
孤独感                     archive:5757    active:5756    drift=+1
（全部 262 个 label 的最大漂移 = 1 行）
```

**哪一份是生效的？—— 补丁版（`00patch`）。** 三重证据：

1. `environment.txt`：`LCHH_RENPY_PATCH_SEARCHPATH="00patch"` —— 补丁目录在搜索路径上
2. 存档里 `Context.current` 记录的文件名是 **`00patch/scenario/main_scenario.rpy`**（不是 `scenario/main_scenario.rpy`）
3. `persistent._seen_ever` 的 key 里，主线剧本文件名是 **`00patch/scenario/main_scenario.rpy`**（1373 条语句）

**影响与对策**：
- 如果按 `archive.rpa` 那份建 label 行号表，**行号会错 1 行**。做 label 归属判断时通常仍落在同一 label 内（我实测了两种映射，5/5 结果**相同**，因为 label 通常几百行长），但**边界行会错**。
- ✅ **正确做法**：建立「**文件名 + 编译时间戳**」为主键的 label 词典，并**优先取补丁目录（`00patch/`）下的副本**。绝不能假设"一个游戏只有一份剧本"。

### 7.2 ⚠️ 存档里**读不到没被改动过的变量**

`renpy/python.py:1757` `get_roots()` 的文档字符串写得很明确：

> *"A variable is only in this map if it has ever been changed since the init phase finished."*

实测后果：`hensu.rpy` 的 21 个变量里，只有 `grand`（第 7 行就被赋值）出现在存档里；`kakoroute`/`risounomirai`/`erosion`/`know_flashback`/`yumenomirai` **0/12 存在**。

**这不是 bug，是引擎设计。** 影响：
- 「读变量判断路线」的方案**必须有 fallback**，不能假设变量一定存在
- 「变量不存在」≠「变量为 0」—— 前者是"没有信息"，后者是"确定没走过那条线"
- 存档体积因此很小（290 KB），但代价是**稀疏**

### 7.3 ⚠️ `Context.current` 3 元组的第 3 个元素**不是行号**

`renpy/script.py:320-330`：

```python
def assign_names(self, stmts, fn):
    all_stmts = collapse_stmts(stmts)
    version = int(time.time())
    for s in all_stmts:
        if s.name is None:
            s.name = (fn, version, self.serial)   # serial = 全局递增语句序号
            self.serial += 1
```

**我踩过这个坑**：最初把 `2631` 当行号映射到 label，得到 `無事に校門まで辿り着いた`（label @ 2600），"看起来对"。但交叉验证发现矛盾——同一存档的历史记录里有 label 定义在 **5712 行之后**，比 `2631` 还大，说明"当前位置"不可能同时是 2631。回溯源码才确认它是**语句序号**。

**影响**：任何"行号 → 章节/进度百分比"的映射都必须**重新建立序号↔行号对照表**，不能直接当行号用。`_seen_ever` 的 1670 个 3 元组 key 有同样问题。

### 7.4 🔒 安全：**绝不能裸 `pickle.loads` 别人的存档**

Ren'Py 存档是 **Python pickle**，而 pickle 的 `REDUCE`/`GLOBAL` opcode **可以执行任意代码**。GAL 存档是用户从各处下载的，属于**不可信输入**。

**本报告全部实验使用的安全做法**：

```python
class RestrictedUnpickler(pickle.Unpickler):
    def find_class(self, module, name):
        # 绝不导入真实模块；返回惰性桩对象
        return stub_class(module, name)
    def persistent_load(self, pid):
        return ("<PERSISTENT>", pid)
```

配套措施：
1. **先做 opcode 静态审计**（`pickletools.genops`，零执行风险），确认对象图里有哪些类
2. 只对**已知安全的内建类型**（`dict`/`list`/`tuple`/`set`/`str`/`int`）返回真实类型，其余一律桩对象
3. 桩对象用 `__setstate__` 捕获状态即可，**不需要真正构造游戏对象**
4. 如需更高安全性，可考虑子进程 + 资源限制隔离

实测：12/12 存档在受限 unpickler 下**全部成功**，拿到需要的全部字段，**且无任何代码执行**。

### 7.5 ⚠️ 版本差异：不要过度承诺通用性

本报告的全部结论基于 **Ren'Py 7.4.11.2266 / Python 2.7**。已知风险点：

| 风险 | 说明 | 状态 |
|---|---|---|
| **存档容器格式** | 7.4.11 是 **ZIP**。更早的 Ren'Py（6.x）历史上用过 **zlib+pickle 裸流**；不同版本的条目名/顺序可能有差异 | ⚠️ 未验证其它版本 |
| **`_game_runtime` 字段** | 任务书预期存档里有 `_game_runtime`。**7.4.11 里没有**——`loadsave.py:427` 只写 `_save_name`/`_renpy_version`/`_version` 三个键，其余靠游戏注册 `config.save_json_callbacks`。本游戏没注册 | ✅ 已确认 |
| **存档加密** | 本游戏**无加密**（明文 ZIP + 明文 JSON）。但本机 `%APPDATA%\RenPy\tokens\security_keys.txt` 存在（来自 Ren'Py 8.x launcher），说明**新版本引擎引入了存档安全令牌机制**。本游戏的 `renpy/` 目录里**没有 `savetoken.py`**，所以不受影响 | ⚠️ 8.x 未验证 |
| **`persistent` 格式** | 7.4.11 是裸 zlib。新版本可能改为 ZIP | ⚠️ 未验证 |
| **RPA 索引结构** | 本作是 **RPA-3.0**，索引条目是 **3 元组 `(offset, length, prefix)`**，且 **offset 和 length 都异或了 key**（详见 §8）；RPA-2.0/3.2 结构不同 | ✅ 已用 400/400 magic 校验确认 |
| **`_seen_images` 语义** | 283 个 key 里只有 27 个是 CG 槽位，其余 217 个是立绘合成元组 —— **不同游戏的 key 结构可能完全不同** | ⚠️ 需逐游戏适配 |

### 7.6 ⚠️ 版本漂移：存档里的 `_version` 与当前游戏版本不一致

- 存档 `json` 里的 `_version` = **`1.0.2`**
- 当前 `options.rpy:27` 的 `config.version` = **`1.0.3`**

说明**这 12 个存档是在游戏 1.0.2 时期存的，游戏之后更新到了 1.0.3**。Galbox 应把 `_version` 与当前版本比对，**对旧版本存档标注"可能不兼容"**。

### 7.7 ⚠️ 两个存档位置，选哪个？

`MultiLocation.newest()`（`savelocation.py:394-414`）按 **mtime 取最新**。实测两者哈希完全一致（因为每次保存都写两份），所以随便读哪个都行。但如果用户手动复制过文件、或游戏在 U 盘/只读目录运行，两份可能不一致 —— **应始终按 mtime 取最新，而不是固定读某一个**。

---

## 8. 附录：RPA 解包的坑（供实现方参考）

本次为了读到剧本 label 表，需要解包 `.rpa`。这里踩了两个坑，记录如下：

**坑 1：索引条目是 3 元组，不是 2 元组。**

```
'aac/xxx.ogg' -> [(1229097093, 1111627400, '')]     ← 注意第 3 个元素
```

直接 `for off, ln in index[name]` 会报 `ValueError: too many values to unpack (expected 2)`。

**坑 2：`offset` 和 `length` **都**异或了 key，不是只异或 offset。**

header：`RPA-3.0 000000002850b9b7 42424242` → index_off = `0x2850b9b7`，key = `0x42424242`。

症状：`length` 字段读出来全是 `0x4242xxxx` 之类（如 `1111627400`），明显是 key 泄漏。

**用 400 个带 magic 的二进制资源（PNG/Ogg/WebM/JPG）做四假设对比，结论无歧义：**

```
both^key       magic-validated 400 / 400     ← ✅ 正确
off^key only   magic-validated   0 / 400
len^key only   magic-validated   0 / 400
none           magic-validated   0 / 400
```

**正确读法**：

```python
parts = f.readline().decode().strip().split()   # b"RPA-3.0 <hex_off> <hex_key>"
index_off, key = int(parts[1], 16), int(parts[2], 16)
f.seek(index_off)
index = pickle.loads(zlib.decompress(f.read()))

for entry in index[name]:
    off, length = entry[0] ^ key, entry[1] ^ key      # ★ 两个字段都要异或
    f.seek(off); data = f.read(length)
```

**验证方法（务必做）**：读出来先查 magic —— `.png` 应以 `89 50 4E 47` 开头，`.ogg` 应以 `4F 67 67 53`（`OggS`）开头。对不上就是解错了。

**两个 RPA 的规模**：

```
game/archive.rpa      645.1 MB   4517 个条目   34 个 .rpy + 34 个 .rpyc
00patch/00patch.rpa    29.9 MB     77 个条目   10 个 .rpy + 10 个 .rpyc
```

**环境里没有 `game/*.rpy` 散文件** —— 剧本全在 RPA 里。`game/` 目录下只有 `archive.rpa`、字体、`script_version.txt`、`script.rpy.rej`（一个 patch 失败留下的 reject 文件）和 `cache/*.rpyb`。

---

## 9. 未验证清单

明确标注以下内容**本次没有验证**，实现时不要假设：

| # | 未验证项 | 原因 |
|---|---|---|
| 1 | **Ren'Py 8.x 的存档格式** | 本机只有 7.4.11 引擎 + 7.4.11 存档。`%APPDATA%\RenPy\tokens\security_keys.txt` 提示新版有安全令牌机制，但其 `savetoken.py` 在本游戏引擎目录里**不存在**，无法验证 |
| 2 | **Ren'Py 6.x / 5.x 的 zlib 裸流存档** | 无样本。任务书假设的 "zlib+pickle" 结构在 7.4.11 上**不成立**（是 ZIP），但旧版本可能确实如此 |
| 3 | **其它游戏的 `_seen_images` / `gallery.image()` 对应关系** | 只测了这一个游戏。不同游戏的 CG 命名、gallery 实现、是否存在 gallery 都可能不同 |
| 4 | **`config.save_json_callbacks` 生效时的 `json` 结构** | 本游戏**没有**注册该回调，所以只看到 3 个默认键。若游戏注册了（常见于显示 `_save_time`/`_game_runtime` 的游戏），`json` 会多出字段 —— 但**没有样本可测** |
| 5 | **加密/混淆存档** | 本游戏无加密。未测任何加密存档（8.x 的 save token 机制） |
| 6 | **`_history_list` 的长度上限** | 12 个存档**全都是 90 条**（可疑地一致）。`renpy/config.py:718` `history_length = None`，而 `character.py:1365` 有 `while len(history) > history_length` 的裁剪逻辑。究竟是"恰好每次会话都是 90"还是"被配置裁到 90"，**未验证** |
| 7 | **`runtime` 的精确计时语义** | 实测量级符合"秒"，且 `core.py:4246` 用 `end_time - start_time` 累加。但 `execution.py:133` 的注释写 "in milliseconds"，两者矛盾。**"是否包含玩家挂机时间"未验证** |
| 8 | **`_seen_images` 里 217 个元组 key 的确切语义** | 形如 `('mirai','hf1','sc01','ma02','ey01','mo05','ase0','hf1')`，看起来是「角色+服装+姿势+表情」的立绘属性组合，但**未从引擎源码确认** |
| 9 | **`00patch` / `archive` 两份剧本的差异是否只影响行号** | 实测 label 集合完全相同、行号漂移 ±1。但**差异的具体内容**（那 84 KB 差在哪）未逐行 diff |
| 10 | **存档与游戏的绑定关系（同一存档能否被另一版本加载）** | 超出本次范围 |
| 11 | **`%APPDATA%\RenPy\` 下 11 个其它游戏目录** | 只确认了本游戏目录可由 `config.save_directory` 推导，未逐个验证其它游戏 |
| 12 | **`_chosen` 为何只有 4 条** | 存档覆盖 20–40 分钟游玩，但只记录 4 个选择。是因为分支本来就少，还是有裁剪/合并机制，**未验证** |

---

## 10. 附：本次实验的复现命令

```bash
# 环境
python --version                 # Python 3.12.7（仅用标准库）

# 实验脚本（临时目录，未改动任何项目文件）
E:\tmp\_galbox_scratch\
  01_zip_probe.py       ZIP 结构 + json 元数据 dump
  02_pickle_walk.py     pickletools 安全 opcode 审计
  03_unpickle.py        受限 unpickler 基础设施（find_class 桩化）
  04_deep.py            RollbackLog 结构 + 对话历史
  05_contexts.py        Context 字段 + 变量命名空间
  06_persistent.py      persistent 全量分析
  07_vars.py            跨存档汇总表
  08b_rpa.py / 10_rpa_fix.py   RPA 索引解析 + XOR 假设验证（400/400）
  09b_extract.py        从 RPA 抽取 .rpy 源码
  13_final.py           gallery/persistent 交叉验证
  14_seenever.py        _seen_ever key 构成分析
  18_remap.py / 19_algo.py / 20_final.py   label 解析算法验证

# 产物（只读，未修改任何项目文件）
E:\tmp\Galbox_v2\_product\design\renpy-save-analysis.md   ← 本报告
E:\tmp\_galbox_scratch\rpa_extract\                        ← 抽出的剧本源码
E:\tmp\_galbox_scratch\out*.txt                            ← 全部原始输出
```

**硬约束遵守情况**：未修改 `E:\tmp\Galbox_v2` 里任何现有文件（本报告为新建文件）；未执行 git 提交；未访问 GitHub；未安装任何第三方包（全部使用 Python 标准库）。
