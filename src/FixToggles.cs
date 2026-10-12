using System.Collections.Generic;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 本补丁包「全部可开关修复项」的元数据表。
    // ==========================================================================
    //
    // 【这张表是干什么的】
    //
    //   把「有哪些修复项、显示成什么名字、属于哪一类、对应哪个补丁类」集中在一处。
    //   设置界面（LocalFixesMod.DoSettingsWindowContents）直接遍历它生成控件，
    //   LocalFixesMod.IsFixEnabled() 也用它做校验。
    //
    // 【为什么不让每个补丁自己带开关信息】
    //
    //   因为界面在一个地方画、安装点在另一个地方查，
    //   两处必须看到**同一份**清单 —— 否则「界面上有、实际没接线」这种
    //   「开关是假的」的问题永远不会被发现。集中一处，加项时漏了立刻就看得出来。
    //
    // 【加一个新开关要动哪几处】（照这个顺序做，一处都不能少）
    //   1. 本文件的 All 数组加一条（Id / Group / Label / Desc，Harmony 补丁类还要填 PatchClassName）；
    //   2. 安装点：TryInstall 调用改成带 Id 的重载；若是 [StaticConstructorOnStartup] 装的，
    //      在那个类的静态构造里加一句 IsFixEnabled 判断；
    //   3. Languages\ChineseSimplified\Keyed\GNH_LocalFixes.xml 加 Label / Desc 两个键；
    //   4. Languages\English\Keyed\GNH_LocalFixes.xml 加同样两个键（英文）。
    //
    // 【Id 是配置文件里的键名，定下来就别改】
    //
    //   它会被写进 <配置目录>\Config\ModSettings\GNH.LocalFixes.xml。
    //   改 Id = 老玩家的那一条开关状态被丢弃（不会崩，会退回「默认开启」）。
    //
    // 【缺省值一律是「开启」】
    //
    //   查不到记录时按开启处理（见 GNHLocalFixesSettings.IsOn）。
    //   这样老配置升级上来、以及将来新增的项，默认都是修着的 ——
    //   本补丁包的定位就是「装上就别管」，默认关闭会让玩家莫名其妙地继续崩。
    internal static class FixToggles
    {
        // 界面上的分节标题。**值是语言键**，不是显示文本 ——
        //   界面上走 Translated(键, GroupFallback(键)) 取词。
        //
        // 为什么用键而不是直接写中文（2026-10-12 复审改正）：
        //   原先这四个常量直接写中文，而 About.xml / README 都声称「设置界面中英双语」。
        //   实际效果是：英文客户端里 22 个开关的标题与说明都是英文，
        //   四个分组小标题却仍是中文 —— 宣称与实现不符。
        //   现在四个标题各有一个语言键，取不到时才回退到 GroupFallback() 里的中文。
        //
        // 顺序有意义：越靠前越是「不修就会崩」的项，界面按这个顺序分节。
        internal const string GroupCrash = "GNH_LocalFixes_Group_Crash";
        internal const string GroupSpam = "GNH_LocalFixes_Group_Spam";
        internal const string GroupCompat = "GNH_LocalFixes_Group_Compat";
        internal const string GroupBehaviour = "GNH_LocalFixes_Group_Behaviour";

        /// <summary>
        /// 分组键 → 取不到翻译时显示的中文。
        /// 语言文件丢了、或将来新增分组忘了加键时，界面上至少是一句能看懂的中文，
        /// 而不是把 "GNH_LocalFixes_Group_Xxx" 这种机器串画出来。
        /// </summary>
        internal static string GroupFallback(string groupKey)
        {
            if (groupKey == GroupCrash) return "崩溃与卡死";
            if (groupKey == GroupSpam) return "日志刷屏";
            if (groupKey == GroupCompat) return "兼容与判定";
            if (groupKey == GroupBehaviour) return "行为与设置";
            return groupKey;
        }

        internal sealed class Entry
        {
            /// <summary>稳定的 Id，同时是配置文件里的键名与语言键的后缀。</summary>
            internal readonly string Id;

            /// <summary>界面分节标题。</summary>
            internal readonly string Group;

            /// <summary>界面上的标题（取不到翻译时的中文回退）。</summary>
            internal readonly string Label;

            /// <summary>界面上的说明文字（取不到翻译时的中文回退）。</summary>
            internal readonly string Desc;

            /// <summary>
            /// 若本项是带 [HarmonyPatch] 的补丁类，这里填它的完整类型名；
            /// 否则为 null。LocalFixesMod.InstallPatchClass 靠它反查开关。
            /// </summary>
            internal readonly string PatchClassName;

            internal Entry(string id, string group, string label, string desc,
                string patchClassName = null)
            {
                Id = id;
                Group = group;
                Label = label;
                Desc = desc;
                PatchClassName = patchClassName;
            }
        }

        // ======================================================================
        // 全部开关（顺序即界面顺序）
        // ======================================================================
        internal static readonly Entry[] All = new Entry[]
        {
            // ---------------- 崩溃与卡死 ----------------
            new Entry("assemblyGetTypesFallback", GroupCrash,
                "坏程序集不再拖垮整批补丁",
                "某个模组的 dll 引用了根本不存在的程序集时，游戏枚举「所有类」会整体失败，"
                + "排在后面的模组就全部扫不到。本项把「整份清单作废」降级成「少几个类，但清单还能用」。"),

            new Entry("techHediffsZeroBudget", GroupCrash,
                "植入体预算为 0 时的空引用崩溃",
                "原版在「植入体预算恰好为 0」时会以 0 权重抽到 null 然后解引用崩溃。本项补上空值保护。"),

            new Entry("musicManagerFadeout", GroupCrash,
                "音乐淡出每帧报错",
                "「乐器」分组里混进真实类型不是乐器的对象时，音乐淡出会每帧抛 InvalidCastException。"
                + "本项在源头拦截并附带一层兜底。"),

            new Entry("nmmIdeoTrackerNull", GroupCrash,
                "开新局卡死在生成阶段",
                "NudityMattersMore 的 CoverBody.UpdateIdeo 会在「静态表里残留同号记录」时解引用空引用，"
                + "把整条角色生成流程掀翻——表现就是同一个进程里连续开新局会卡死。本项在它之前清掉残留。"),

            new Entry("alliesAreHelpfulCurJobNull", GroupCrash,
                "Allies are Helpful 的角色空引用",
                "该模组在「角色当前没有工作」这个常见情形下没有拦住，会读空对象并中断 TickRare。"
                + "本项把那几处读 Job 字段的地方改成 null 安全写法。"),

            new Entry("srceLibConcurrency", GroupCrash,
                "雪兔公共库的并发崩溃",
                "SRCELib 的两个静态缓存全程没有锁，并行渲染下会同时扩容、写坏集合，"
                + "导致部分角色每次绘制都抛异常、干脆不显示。本项给它套一把锁。"),

            new Entry("gfmIdleCombatDeepCopy", GroupCrash,
                "Guards For Me 的行为树残缺",
                "该模组的 DeepCopy 是从原版复制来的，忘了把强转的目标类型一起改，必然抛 InvalidCastException，"
                + "导致三棵行为树没被复制、用到它们的种族 AI 行为缺失。本项只改那两条 IL 指令。"),

            new Entry("ccoeReflection", GroupCrash,
                "CCOE 的反射异常红字",
                "CCOE 有三个前缀方法在「角色没有对应组件」时会抛 System.Reflection.TargetException，"
                + "让月经结算流程失败并刷红字。本项把三处都改成安全写法。"),

            new Entry("fixVfeeInstrumentSpace", GroupCrash,
                "修复 VFE Empire「乐器空间」的空引用崩溃",
                "给 VFE Empire 那句会崩的判断补上空值保护：不补的话，九个用到「乐器空间」的爵位"
                + "（Archcount / Marquess / Archduke 等）的房间需求无法正常工作。"
                + "★ 多数情况下不必手动关：本项自带探测，一旦发现原版那句判断已经安全（多半是作者自己修好了），"
                + "会自动让路、什么都不做。"),

            new Entry("crossPromotionUnpatch", GroupCrash,
                "打开「模组」页面崩溃",
                "CrossPromotion 在模组页面里去取 Steam 信息，会调用 Steam 官方早已废弃的接口，"
                + "导致一打开「模组」页面就崩。本项把它那段前置处理摘掉。"),

            new Entry("steamWorkshopHook", GroupCrash,
                "Steam 工坊信息崩溃（第二道保险）",
                "与上一项是同一场崩溃的另一条路：把「取工坊信息」这条链路上的空值与异常接住。"
                + "两项都开着最稳。"),

            // ---------------- 日志刷屏 ----------------
            new Entry("baseGenMimicSpawnerQuiet", GroupSpam,
                "古代神殿警告刷屏",
                "生成古代神殿时，每个小格子都会刷一条「找不到 mimicSpawner 规则」的警告（实测一次开局 34 条）。"
                + "原版那条路径本来就是「拒绝 + 什么都不做」，所以本项只是不再刷警告，行为完全等价。"),

            new Entry("rjwGenesSpamQuiet", GroupSpam,
                "RJW-Genes 调试刷屏",
                "开发者模式下每帧都写一行 [RJW-Genes] multipreg checks（实测一局 3 万条、占整份日志 65%），"
                + "把真正要看的问题全埋掉。本项只拦这一句，该模组的报错与警告照常输出。"),

            new Entry("vehicleFrameworkDebug", GroupSpam,
                "Vehicle Framework 调试菜单报错",
                "打开开发者生成菜单后，该模组找不到对应的 PawnKind 会报错。"
                + "本项把「找不到就报错」改成「查不到就静默跳过」。"),

            // ---------------- 兼容与判定 ----------------
            new Entry("patchVerbPropertiesAdjustedRange", GroupCompat,
                "射程计算空引用",
                "原版算射程时有两处直接使用「攻击者」却没有先判空，特定情况下会刷空引用。本项补上判空。", "GNH.LocalFixes.Patch_VerbProperties_AdjustedRange"),

            new Entry("patchModListerHasActiveModWithName", GroupCompat,
                "XML 补丁按名字判定 DLC 失效",
                "中文环境里 DLC 的显示名会被语言包翻译（Royalty → 皇权），"
                + "于是补丁里写 <li>Royalty</li> 的判定会静默失败、整块补丁被跳过（而且一条日志都不打）。"
                + "本项让英文短名与中文显示名都能匹配。", "GNH.LocalFixes.Patch_ModLister_HasActiveModWithName"),

            new Entry("xmlExtensionsFindMod", GroupCompat,
                "XmlExtensions 的 FindMod 判定失效",
                "同样是「按名字判定 DLC」的毛病，但走的是 XmlExtensions 自己那条代码路径"
                + "（与本补丁包另一处修复互不重叠，两者都要开才完整）。"),

            new Entry("patchGenTypesPreferTelanda", GroupCompat,
                "RaceGeneDef 类型名解析到正确的一份",
                "本补丁包为了避免刷红字自带了一份同名兼容定义，当 telanda 的动物遗传模组在场时，"
                + "同一个类型名会同时存在于两个程序集。本项保证解析到 telanda 的那一份。", "GNH.LocalFixes.Patch_GenTypes_PreferTelanda"),

            new Entry("patchLoadModXMLFilterTelandaGenes", GroupCompat,
                "ElToro Patches 与 telanda 抢 defName",
                "两个模组各自定义了一整套 RaceGeneDef，而且 25 个完全重名、互相覆盖。"
                + "本项让它们不再互抢；telanda 缺席时还会顺手把无人消费的那批数据表安静地摘掉。", "GNH.LocalFixes.Patch_LoadModXML_FilterTelandaGenes"),

            new Entry("patchListerThingsRejectMislabeledInstrument", GroupCompat,
                "非乐器不许进「乐器」分组（源头拦截）",
                "在物品登记进分组的那一刻检查「def 说是乐器、真实类型却不是」的对象并挡回去，"
                + "于是根本不会有东西混进去。与「音乐淡出每帧报错」那一项配套，建议一起开。", "GNH.LocalFixes.Patch_ListerThings_Add_RejectMislabeledInstrument"),

            // ---------------- 行为与设置 ----------------
            new Entry("keepLeaderTitle", GroupBehaviour,
                "保护自定义的领袖头衔",
                "开启时：你已经定好的领袖头衔（主席、教宗……这类）不会再被游戏重新随机，"
                + "改模因、点「随机符号」都不会动它。关闭后：完全恢复原版行为。"
                + "想给某个文化换头衔，把头衔清空（或临时关掉本开关）再点一次「随机符号」即可。", "GNH.LocalFixes.Patch_IdeoFoundation_KeepLeaderTitle"),

            new Entry("cumpilationSettingsRestore", GroupBehaviour,
                "抵消「月经体液」强制关闭 Cumpilation 设置",
                "「RJW Menstruation Fluids」每次启动都会把 Cumpilation 的 9 个开关无条件写成关闭，"
                + "你在设置界面里打开也没用（下次启动又被按回去，而且它只改内存、从不写盘）。"
                + "本项在它之后按**你自己存过的值**恢复，不写死任何开关。"),
        };

        // ======================================================================
        // 查询
        // ======================================================================
        private static Dictionary<string, Entry> byId;
        private static Dictionary<string, string> idByPatchClass;

        private static void BuildIndex()
        {
            // 双检锁：设置界面与补丁安装可能在两个线程上同时走到这里。
            if (byId != null)
            {
                return;
            }
            lock (typeof(FixToggles))
            {
                if (byId != null)
                {
                    return;
                }
                Dictionary<string, Entry> ids = new Dictionary<string, Entry>();
                Dictionary<string, string> classes = new Dictionary<string, string>();
                for (int i = 0; i < All.Length; i++)
                {
                    Entry e = All[i];
                    ids[e.Id] = e;
                    if (!e.PatchClassName.NullOrEmpty())
                    {
                        classes[e.PatchClassName] = e.Id;
                    }
                }
                idByPatchClass = classes;
                byId = ids;
            }
        }

        internal static Entry Find(string id)
        {
            if (id.NullOrEmpty())
            {
                return null;
            }
            BuildIndex();
            Entry found;
            return byId.TryGetValue(id, out found) ? found : null;
        }

        /// <summary>本项在界面上显示的中文标题（取不到翻译时用）。</summary>
        internal static string LabelOf(string id)
        {
            Entry e = Find(id);
            return (e != null) ? e.Label : id;
        }

        /// <summary>语言键：标题。</summary>
        internal static string LabelKeyOf(string id)
        {
            return "GNH_LocalFixes_Toggle_" + id;
        }

        /// <summary>语言键：说明。</summary>
        internal static string DescKeyOf(string id)
        {
            return "GNH_LocalFixes_Toggle_" + id + "_Desc";
        }

        /// <summary>
        /// 某个 [HarmonyPatch] 类对应的开关 Id；没有对应项时返回 null
        /// （返回 null 表示「不受开关管辖，照常安装」—— 而不是「禁用」）。
        /// </summary>
        internal static string IdForPatchClass(string fullTypeName)
        {
            if (fullTypeName.NullOrEmpty())
            {
                return null;
            }
            BuildIndex();
            string id;
            return idByPatchClass.TryGetValue(fullTypeName, out id) ? id : null;
        }
    }
}
