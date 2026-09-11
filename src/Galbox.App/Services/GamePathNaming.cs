using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Galbox.App.Services;

/// <summary>
/// Result of turning one folder name into an ASCII-safe name.
/// </summary>
public sealed class AsciiNameSuggestion
{
    /// <summary>The suggested name. Always non-empty and always pure ASCII.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True when the source contained characters that were replaced, removed or escaped.</summary>
    public bool Changed { get; init; }

    /// <summary>Plain-Chinese explanation of what was done, for the "修复结果" area of the diagnosis page.</summary>
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// Turns a path segment that contains Chinese characters into the ASCII name a game executable can
/// survive, and decides which part of a path actually contains them.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the "路径含中文字符" diagnosis is only worth anything if it can be repaired. The
/// product spec (§3.5, §4.4) asks for "一键重命名目录", and its own example is
/// <c>游戏 → Game / youxi</c>.
/// </para>
/// <para>
/// Translation order, longest match first:
/// </para>
/// <list type="number">
///   <item>a word table of the vocabulary that actually appears in galgame folders
///         (游戏 → Game, 汉化 → Hanhua, 存档 → Save, ...), which is what makes the common case come
///         out readable rather than machine-generated;</item>
///   <item>a per-character pinyin table for the most frequent Chinese characters
///         (千恋万花 → QianLianWanHua);</item>
///   <item>a deterministic, reversible <c>u4e2d</c>-style escape for anything else. It is ugly, but it
///         is honest: inventing a reading for a character the table does not know would produce a
///         wrong name, and a wrong name is worse than a hex escape the user can rename.</item>
/// </list>
/// <para>
/// Nothing here touches the file system; the caller decides what to do with the suggestion.
/// </para>
/// </remarks>
public static class GamePathNaming
{
    /// <summary>CJK ideographs (the same range the diagnosis uses for "path contains Chinese").</summary>
    private static readonly Regex ChineseRegex =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf]", RegexOptions.Compiled);

    /// <summary>Maximum length of a generated name, so a pathological source cannot break MAX_PATH.</summary>
    private const int MaxNameLength = 64;

    /// <summary>Fallback name used when every character would have to be dropped.</summary>
    private const string FallbackName = "Game";

    /// <summary>True when the text contains at least one Chinese ideograph.</summary>
    public static bool ContainsChinese(string? text) =>
        !string.IsNullOrEmpty(text) && ChineseRegex.IsMatch(text);

    /// <summary>True when every character of the text is in the 7-bit ASCII range.</summary>
    public static bool IsPureAscii(string? text) =>
        text is not null && text.All(character => character < 128);

    /// <summary>
    /// Returns the distinct Chinese characters of the path, in order of appearance. Used for the
    /// "找到的中文字符" part of the diagnosis message.
    /// </summary>
    public static List<string> ChineseCharactersIn(string? path)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(path))
        {
            return result;
        }

        foreach (var character in path.Where(c => ChineseRegex.IsMatch(c.ToString())))
        {
            var text = character.ToString();
            if (!result.Contains(text, StringComparer.Ordinal))
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a path into segments and reports which of them contain Chinese characters. The last
    /// segment is the folder that can be renamed in place; a Chinese ancestor is a different (and
    /// much more invasive) problem, so the caller has to be able to tell them apart.
    /// </summary>
    public static (string? LeafWithChinese, List<string> AncestorsWithChinese) AnalysePath(string? path)
    {
        var ancestors = new List<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, ancestors);
        }

        var segments = path
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.EndsWith(':'))

            .ToList();

        if (segments.Count == 0)
        {
            return (null, ancestors);
        }

        var leaf = segments[^1];
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (ContainsChinese(segments[index]))
            {
                ancestors.Add(segments[index]);
            }
        }

        return (ContainsChinese(leaf) ? leaf : null, ancestors);
    }

    /// <summary>
    /// Suggests an ASCII replacement for one path segment.
    /// </summary>
    public static AsciiNameSuggestion SuggestAsciiName(string segment)
    {
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(segment))
        {
            return new AsciiNameSuggestion { Name = FallbackName, Changed = true, Notes = { "原名为空，已使用默认名。" } };
        }

        if (IsPureAscii(segment))
        {
            return new AsciiNameSuggestion { Name = segment, Changed = false };
        }

        var builder = new StringBuilder();
        var index = 0;
        var droppedCount = 0;
        var escapedCount = 0;
        var translatedByWord = 0;
        var translatedByPinyin = 0;

        while (index < segment.Length)
        {
            // 1. Longest word match at the current position.
            var word = MatchWord(segment, index);
            if (word is not null)
            {
                AppendPart(builder, word.Value.Ascii);
                index += word.Value.Length;
                translatedByWord++;
                continue;
            }

            var character = segment[index];

            // 2. Single Chinese character -> pinyin, or a deterministic escape.
            if (ChineseRegex.IsMatch(character.ToString()))
            {
                if (SyllableTable.TryGetValue(character, out var syllable))
                {
                    AppendPart(builder, Capitalize(syllable));
                    translatedByPinyin++;
                }
                else
                {
                    AppendPart(builder, "u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    escapedCount++;
                }

                index++;
                continue;
            }

            // 3. Full-width forms have an exact ASCII counterpart.
            if (character >= '\uff01' && character <= '\uff5e')
            {
                AppendPart(builder, ((char)(character - 0xfee0)).ToString());
                index++;
                continue;
            }

            if (character == '\u3000')
            {
                AppendPart(builder, "-");
                index++;
                continue;
            }

            // 4. Keep ASCII as it is.
            if (character < 128)
            {
                AppendPart(builder, character.ToString());
                index++;
                continue;
            }

            // 5. Anything else (kana, other scripts, emoji) cannot be romanised here; dropping it is
            //    the only honest option.
            droppedCount++;
            index++;
        }

        var sanitized = Sanitize(builder.ToString(), out var wasTruncated);

        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = FallbackName;
            notes.Add($"原目录名 \"{segment}\" 无法转写出可用的 ASCII 名，已使用默认名 \"{FallbackName}\"，请在界面上改成你喜欢的名字。");
        }
        else
        {
            if (translatedByWord > 0)
            {
                notes.Add($"按词表翻译了 {translatedByWord} 个词（如 游戏 → Game）。");
            }

            if (translatedByPinyin > 0)
            {
                notes.Add($"按拼音转写了 {translatedByPinyin} 个汉字。");
            }

            if (escapedCount > 0)
            {
                notes.Add($"有 {escapedCount} 个汉字不在拼音表中，已用 u+码点 形式转写（如 鑫 → u946b），请自行确认是否合适。");
            }

            if (droppedCount > 0)
            {
                notes.Add($"已移除 {droppedCount} 个无法转写的字符（假名、符号等）。");
            }

            if (wasTruncated)
            {
                notes.Add($"名称超过 {MaxNameLength} 个字符，已截断。");
            }
        }

        return new AsciiNameSuggestion
        {
            Name = sanitized,
            Changed = true,
            Notes = notes
        };
    }

    /// <summary>
    /// Finds a target name that is not used yet by appending <c>-2</c>, <c>-3</c>, ... .
    /// </summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{name}-{suffix}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        return $"{name}-{Guid.NewGuid():N}"[..Math.Min(MaxNameLength, name.Length + 9)];
    }

    /// <summary>Removes characters a Windows file name cannot contain and trims the result.</summary>
    private static string Sanitize(string raw, out bool wasTruncated)
    {
        wasTruncated = false;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(raw.Length);

        foreach (var character in raw)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var text = builder.ToString();

        // Collapse runs of separators and trim the ones at both ends: "a--b" and "-a-" are both
        // legal but read like machine noise.
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Replace('_', '-');
        text = text.Trim('-', '.', ' ');

        if (text.Length > MaxNameLength)
        {
            text = text[..MaxNameLength].Trim('-', '.', ' ');
            wasTruncated = true;
        }

        return text;
    }

    private static void AppendPart(StringBuilder builder, string part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return;
        }

        var needsSeparator = builder.Length > 0
                          && char.IsLetterOrDigit(builder[^1])
                          && char.IsLetterOrDigit(part[0]);

        if (needsSeparator)
        {
            builder.Append('-');
        }

        builder.Append(part);
    }

    private static string Capitalize(string syllable) =>
        syllable.Length == 0
            ? syllable
            : char.ToUpperInvariant(syllable[0]) + syllable[1..];

    private static (string Ascii, int Length)? MatchWord(string text, int index)
    {
        foreach (var (chinese, ascii) in WordTable)
        {
            if (index + chinese.Length <= text.Length
                && string.CompareOrdinal(text, index, chinese, 0, chinese.Length) == 0)
            {
                return (ascii, chinese.Length);
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------------------------
    // Word table: the vocabulary that actually shows up in galgame folder names. Every entry maps to
    // English (or to the conventional romanisation of the load word), which is what the spec asks for
    // ("改为英文或拼音").
    // -------------------------------------------------------------------------------------------
    private static readonly (string Chinese, string Ascii)[] WordTable = BuildWordTable(
        new[]
        {
            "游戏", "中文", "汉化", "补丁", "存档", "目录", "文件夹", "资料", "免安装", "绿色版", "硬盘版",
            "完整版", "完全版", "最终版", "体验版", "正式版", "简体", "繁体", "日语", "日文", "英文", "版本",
            "合集", "整合", "整合版", "收藏版", "典藏版", "豪华版", "珍藏版", "复刻版", "重制版", "修复版",
            "测试版", "试玩版", "全年龄", "无修正", "十八禁", "成人版", "汉化组", "原生版", "原版", "破解版",
            "安装包", "解压密码", "密码", "说明", "教程", "攻略", "修改器", "工具", "启动器", "运行库",
            "少女", "恋爱", "物语", "学园", "学院", "学校", "魔法", "天使", "恶魔", "公主", "骑士", "战争",
            "冒险", "模拟", "角色", "扮演", "恐怖", "悬疑", "推理", "校园", "青春", "后宫", "妹妹", "姐姐",
            "樱花", "星空", "幻想", "传说", "英雄", "王国", "世界", "时空", "轮回", "命运", "回忆", "记忆",
            "梦境", "虚拟", "真实", "电玩", "我的", "你的", "他的", "新的", "旧版", "备用", "备份", "临时",
            "下载", "音乐", "视频", "图片", "动画", "语音", "字幕", "电影", "剧情", "支线", "主线", "结局",
            "女主角", "男主角", "人物", "场景", "地图", "任务", "系统", "设置", "选项", "菜单", "标题",
            "夏日", "冬日", "春日", "秋日", "冬日", "夜空", "白昼", "黎明", "黄昏", "深夜", "午后"
        });

    private static (string Chinese, string Ascii)[] BuildWordTable(IEnumerable<string> words)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["游戏"] = "Game",
            ["中文"] = "Chinese",
            ["汉化"] = "Hanhua",
            ["补丁"] = "Patch",
            ["存档"] = "Save",
            ["目录"] = "Directory",
            ["文件夹"] = "Folder",
            ["资料"] = "Data",
            ["免安装"] = "Portable",
            ["绿色版"] = "Portable",
            ["硬盘版"] = "HDD",
            ["完整版"] = "Complete",
            ["完全版"] = "Complete",
            ["最终版"] = "Final",
            ["体验版"] = "Trial",
            ["正式版"] = "Release",
            ["简体"] = "Simplified",
            ["繁体"] = "Traditional",
            ["日语"] = "Japanese",
            ["日文"] = "Japanese",
            ["英文"] = "English",
            ["版本"] = "Version",
            ["合集"] = "Collection",
            ["整合"] = "Integrated",
            ["整合版"] = "Integrated",
            ["收藏版"] = "CollectorsEdition",
            ["典藏版"] = "CollectorsEdition",
            ["豪华版"] = "Deluxe",
            ["珍藏版"] = "Deluxe",
            ["复刻版"] = "Remaster",
            ["重制版"] = "Remake",
            ["修复版"] = "Fixed",
            ["测试版"] = "Beta",
            ["试玩版"] = "Demo",
            ["全年龄"] = "AllAges",
            ["无修正"] = "Uncensored",
            ["十八禁"] = "Adult",
            ["成人版"] = "Adult",
            ["汉化组"] = "HanhuaGroup",
            ["原生版"] = "Retail",
            ["原版"] = "Original",
            ["破解版"] = "Cracked",
            ["安装包"] = "Installer",
            ["解压密码"] = "Password",
            ["密码"] = "Password",
            ["说明"] = "Readme",
            ["教程"] = "Tutorial",
            ["攻略"] = "Walkthrough",
            ["修改器"] = "Trainer",
            ["工具"] = "Tools",
            ["启动器"] = "Launcher",
            ["运行库"] = "Runtime",
            ["少女"] = "Girl",
            ["恋爱"] = "Love",
            ["物语"] = "Story",
            ["学园"] = "Academy",
            ["学院"] = "Academy",
            ["学校"] = "School",
            ["魔法"] = "Magic",
            ["天使"] = "Angel",
            ["恶魔"] = "Demon",
            ["公主"] = "Princess",
            ["骑士"] = "Knight",
            ["战争"] = "War",
            ["冒险"] = "Adventure",
            ["模拟"] = "Simulation",
            ["角色"] = "Character",
            ["扮演"] = "Roleplay",
            ["恐怖"] = "Horror",
            ["悬疑"] = "Mystery",
            ["推理"] = "Detective",
            ["校园"] = "Campus",
            ["青春"] = "Youth",
            ["后宫"] = "Harem",
            ["妹妹"] = "Sister",
            ["姐姐"] = "Sister",
            ["樱花"] = "Sakura",
            ["星空"] = "StarrySky",
            ["幻想"] = "Fantasy",
            ["传说"] = "Legend",
            ["英雄"] = "Hero",
            ["王国"] = "Kingdom",
            ["世界"] = "World",
            ["时空"] = "TimeSpace",
            ["轮回"] = "Reincarnation",
            ["命运"] = "Fate",
            ["回忆"] = "Memories",
            ["记忆"] = "Memory",
            ["梦境"] = "Dream",
            ["虚拟"] = "Virtual",
            ["真实"] = "Real",
            ["电玩"] = "Game",
            ["我的"] = "My",
            ["你的"] = "Your",
            ["他的"] = "His",
            ["新的"] = "New",
            ["旧版"] = "Old",
            ["备用"] = "Backup",
            ["备份"] = "Backup",
            ["临时"] = "Temp",
            ["下载"] = "Download",
            ["音乐"] = "Music",
            ["视频"] = "Video",
            ["图片"] = "Image",
            ["动画"] = "Anime",
            ["语音"] = "Voice",
            ["字幕"] = "Subtitle",
            ["电影"] = "Movie",
            ["剧情"] = "Story",
            ["支线"] = "SideStory",
            ["主线"] = "MainStory",
            ["结局"] = "Ending",
            ["女主角"] = "Heroine",
            ["男主角"] = "Hero",
            ["人物"] = "Character",
            ["场景"] = "Scene",
            ["地图"] = "Map",
            ["任务"] = "Quest",
            ["系统"] = "System",
            ["设置"] = "Settings",
            ["选项"] = "Options",
            ["菜单"] = "Menu",
            ["标题"] = "Title",
            ["夏日"] = "Summer",
            ["冬日"] = "Winter",
            ["春日"] = "Spring",
            ["秋日"] = "Autumn",
            ["夜空"] = "NightSky",
            ["白昼"] = "Daytime",
            ["黎明"] = "Dawn",
            ["黄昏"] = "Dusk",
            ["深夜"] = "Midnight",
            ["午后"] = "Afternoon"
        };

        // Unknown words fall back to their pinyin, so a newly added entry is never more than
        // "readable on the next build".
        return words
            .Distinct(StringComparer.Ordinal)
            .Select(word => (Word: word, Ascii: translations.TryGetValue(word, out var ascii)
                ? ascii
                : string.Concat(word.Select(c => SyllableTable.TryGetValue(c, out var s) ? Capitalize(s) : string.Empty))))
            .Where(pair => !string.IsNullOrEmpty(pair.Ascii))
            .OrderByDescending(pair => pair.Word.Length)
            .ThenBy(pair => pair.Word, StringComparer.Ordinal)
            .ToArray();
    }

    // -------------------------------------------------------------------------------------------
    // Pinyin table for the most frequent Chinese characters. Format: one character followed by its
    // tone-less pinyin, entries separated by whitespace. A malformed pair is ignored instead of
    // producing a wrong reading.
    // -------------------------------------------------------------------------------------------
    private const string SyllableData =
        "的de 一yi 是shi 不bu 了le 在zai 人ren 有you 我wo 他ta 这zhe 个ge 们men 中zhong 来lai 上shang " +
        "大da 为wei 和he 国guo 地di 到dao 以yi 说shuo 时shi 要yao 就jiu 出chu 会hui 可ke 也ye 你ni 对dui " +
        "生sheng 能neng 而er 子zi 那na 得de 于yu 着zhe 下xia 自zi 之zhi 年nian 过guo 发fa 后hou 作zuo 里li " +
        "用yong 道dao 行xing 所suo 然ran 家jia 种zhong 事shi 成cheng 方fang 多duo 经jing 么me 去qu 法fa 学xue " +
        "如ru 都dou 同tong 现xian 当dang 没mei 动dong 面mian 起qi 看kan 定ding 天tian 分fen 还hai 进jin 好hao " +
        "小xiao 部bu 其qi 些xie 主zhu 样yang 理li 心xin 她ta 本ben 前qian 开kai 但dan 因yin 只zhi 从cong 想xiang " +
        "实shi 日ri 军jun 者zhe 意yi 无wu 力li 它ta 与yu 长chang 把ba 机ji 十shi 民min 第di 公gong 此ci 已yi " +
        "工gong 使shi 情qing 明ming 性xing 知zhi 全quan 三san 又you 关guan 点dian 正zheng 业ye 外wai 将jiang " +
        "两liang 高gao 间jian 由you 问wen 很hen 最zui 重zhong 并bing 物wu 手shou 应ying 战zhan 向xiang 头tou " +
        "文wen 体ti 政zheng 美mei 相xiang 见jian 被bei 利li 什shen 二er 等deng 产chan 或huo 新xin 己ji 制zhi " +
        "身shen 果guo 加jia 西xi 斯si 月yue 话hua 合he 回hui 特te 代dai 内nei 信xin 表biao 化hua 老lao 给gei " +
        "世shi 位wei 次ci 度du 门men 任ren 常chang 先xian 海hai 通tong 教jiao 儿er 原yuan 东dong 声sheng 提ti " +
        "立li 及ji 比bi 员yuan 解jie 水shui 名ming 真zhen 论lun 处chu 走zou 义yi 各ge 入ru 几ji 口kou 认ren " +
        "条tiao 平ping 系xi 气qi 题ti 活huo 尔er 更geng 别bie 打da 女nv 变bian 四si 神shen 总zong 何he 电dian " +
        "数shu 安an 少shao 报bao 才cai 结jie 反fan 受shou 目mu 太tai 量liang 再zai 感gan 建jian 务wu 做zuo " +
        "接jie 必bi 场chang 件jian 计ji 管guan 期qi 市shi 直zhi 德de 资zi 命ming 山shan 金jin 指zhi 克ke 许xu " +
        "统tong 区qu 保bao 至zhi 队dui 形xing 社she 便bian 空kong 决jue 治zhi 展zhan 马ma 科ke 司si 五wu 基ji " +
        "眼yan 书shu 非fei 则ze 听ting 白bai 却que 界jie 达da 光guang 放fang 强qiang 即ji 像xiang 难nan 且qie " +
        "权quan 思si 王wang 象xiang 完wan 设she 式shi 色se 路lu 记ji 南nan 品pin 住zhu 告gao 类lei 求qiu 据ju " +
        "程cheng 北bei 边bian 死si 张zhang 该gai 交jiao 规gui 万wan 取qu 拉la 格ge 望wang 觉jue 术shu 领ling " +
        "共gong 确que 传chuan 师shi 观guan 清qing 今jin 切qie 院yuan 让rang 识shi 候hou 带dai 导dao 争zheng " +
        "运yun 笑xiao 飞fei 风feng 步bu 改gai 收shou 根gen 干gan 造zao 言yan 联lian 持chi 组zu 每mei 济ji " +
        "车che 亲qin 极ji 林lin 服fu 快kuai 办ban 议yi 往wang 元yuan 英ying 士shi 证zheng 近jin 失shi 转zhuan " +
        "夫fu 令ling 准zhun 布bu 始shi 怎zen 呢ne 存cun 未wei 远yuan 叫jiao 台tai 单dan 影ying 具ju 罗luo 字zi " +
        "爱ai 击ji 流liu 备bei 兵bing 连lian 调diao 深shen 商shang 算suan 质zhi 团tuan 集ji 百bai 需xu 价jia " +
        "花hua 党dang 华hua 城cheng 石shi 级ji 整zheng 府fu 离li 况kuang 亚ya 请qing 技ji 际ji 约yue 示shi " +
        "复fu 病bing 息xi 究jiu 线xian 似si 官guan 火huo 断duan 精jing 满man 支zhi 视shi 消xiao 越yue 器qi " +
        "容rong 照zhao 须xu 九jiu 增zeng 研yan 写xie 称cheng 企qi 八ba 功gong 吗ma 包bao 片pian 史shi 委wei " +
        "乎hu 查cha 轻qing 易yi 早zao 曾ceng 除chu 农nong 找zhao 装zhuang 广guang 显xian 吧ba 阿a 李li 标biao " +
        "谈tan 吃chi 图tu 念nian 六liu 引yin 历li 首shou 医yi 局ju 突tu 专zhuan 费fei 号hao 尽jin 另ling " +
        "周zhou 较jiao 注zhu 语yu 仅jin 考kao 落luo 青qing 随sui 选xuan 列lie 武wu 红hong 响xiang 虽sui 推tui " +
        "势shi 参can 希xi 古gu 众zhong 构gou 房fang 半ban 节jie 土tu 投tou 某mou 案an 黑hei 维wei 革ge 划hua " +
        "敌di 致zhi 陈chen 律lv 足zu 态tai 护hu 七qi 兴xing 派pai 孩hai 验yan 责ze 营ying 星xing 够gou 章zhang " +
        "音yin 跟gen 志zhi 底di 站zhan 严yan 巴ba 例li 防fang 族zu 供gong 效xiao 续xu 施shi 留liu 讲jiang " +
        "型xing 料liao 州zhou 阳yang 否fou 纪ji 客ke 良liang 检jian 恋lian 妹mei 姐jie 娘niang 姬ji 樱ying " +
        "雪xue 雨yu 云yun 夜ye 昼zhou 春chun 夏xia 秋qiu 冬dong 梦meng 幻huan 雄xiong 轮lun 忆yi 忘wang 初chu " +
        "室shi 校xiao 园yuan 宫gong 魔mo 恶e 骑qi 冒mao 险xian 模mo 拟ni 角jiao 扮ban 演yan 恐kong 怖bu 悬xuan " +
        "疑yi 汉han 戏xi 夹jia 版ban 免mian 绿lv 硬ying 盘pan 终zhong 试shi 繁fan 甜tian 苦ku 笑xiao 泪lei " +
        "约yue 誓shi 愿yuan 望wang 祈qi 祷dao 祝zhu 福fu 幸xing 累lei 疲pi 倦juan 静jing 安an 宁ning 暖nuan " +
        "冷leng 热re 温wen 凉liang 冰bing 霜shuang 露lu 雾wu 雷lei 电dian 虹hong 霞xia 阳yang 阴yin 晴qing " +
        "光guang 暗an 灰hui 银yin 铜tong 铁tie 钢gang 玉yu 珠zhu 宝bao 石shi 晶jing 莹ying 彩cai 艳yan 丽li " +
        "秀xiu 雅ya 静jing 柔rou 刚gang 强qiang 弱ruo 勇yong 敢gan 怯qie 柔rou 情qing 义yi 理li 智zhi 慧hui " +
        "聪cong 明ming 愚yu 蠢chun 傻sha 呆dai 疯feng 狂kuang 野ye 蛮man 温wen 柔rou 贤xian 慧hui 淑shu 静jing";

    private static readonly Dictionary<char, string> SyllableTable = BuildSyllableTable(SyllableData);

    private static Dictionary<char, string> BuildSyllableTable(string data)
    {
        var table = new Dictionary<char, string>();

        var index = 0;
        while (index < data.Length)
        {
            // skip whitespace
            while (index < data.Length && char.IsWhiteSpace(data[index]))
            {
                index++;
            }

            if (index >= data.Length)
            {
                break;
            }

            var character = data[index];
            if (ChineseRegex.IsMatch(character.ToString()) is false)
            {
                // Defensive: a malformed entry must not shift the whole table.
                index++;
                continue;
            }

            index++;
            var start = index;
            while (index < data.Length && data[index] is >= 'a' and <= 'z')
            {
                index++;
            }

            var syllable = data[start..index];
            if (syllable.Length == 0)
            {
                continue;
            }

            table.TryAdd(character, syllable);
        }

        return table;
    }
}
