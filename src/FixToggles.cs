// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

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
        //   实际效果是：英文客户端里 35 个开关的标题与说明都是英文，
        //   四个分组小标题却仍是中文 —— 宣称与实现不符。
        //   现在四个标题各有一个语言键，取不到时才回退到 GroupFallback() 里的中文。
        //
        // 顺序有意义：越靠前越是「不修就会崩」的项，界面按这个顺序分节。
        internal const string GroupCrash = "GNH_LocalFixes_Group_Crash";
        internal const string GroupSpam = "GNH_LocalFixes_Group_Spam";
        internal const string GroupCompat = "GNH_LocalFixes_Group_Compat";
        internal const string GroupBehaviour = "GNH_LocalFixes_Group_Behaviour";
        internal const string GroupXmlPatches = "GNH_LocalFixes_Group_XmlPatches";

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
            if (groupKey == GroupXmlPatches) return "XML 补丁（可单独关闭）";
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

            /// <summary>
            /// 若本项对应 Patches\ 下的某个 XML 补丁文件，这里填**文件名**（含 .xml）。
            /// 由 XmlPatchGate 在运行时按 PatchOperation.sourceFile 的文件名匹配，
            /// 匹配到且本项被关掉时，跳过那一条补丁。为 null 表示不是 XML 补丁项。
            /// </summary>
            internal readonly string XmlPatchFile;

            internal Entry(string id, string group, string label, string desc,
                string patchClassName = null, string xmlPatchFile = null)
            {
                Id = id;
                Group = group;
                Label = label;
                Desc = desc;
                PatchClassName = patchClassName;
                XmlPatchFile = xmlPatchFile;
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

            // ⚠ RimUnityPostFxUnpatch 曾经也在这一组里，2026-10-17 由用户指示移除。
            //
            // 原因：那场崩溃（crash-20261016-132826.txt）的根因是**本机引擎快照落后** ——
            //      D:\RimWorld-1.7 是 2026-10-13 的构建，而 RimUnity 0.1.7 是按**更新的**
            //      Waylith 引擎编译的，于是 CLR 找不到 PostEffectPass 的新重载。
            //      这是本地环境版本落后，**不是模组之间的兼容问题** —— 引擎快照更新后自愈，
            //      不该在兼容补丁里长期兜着。
            //
            // ⚠ 但「**不要停用 RimUnity**」这条警告仍然有效：ZuoYao_RavenRace 在编译时引用了
            //    RimUnity 的类型，停用会让整份程序集不可用，进而使大量 Def 解析失败
            //    （实测 3 分钟 5.7 万条 ERROR）。快照更新前若又崩，先升级快照 —— 别去停用 RimUnity。
            //
            // 完整推导见备份 _backup/pre-rimunity-postfx-20261016-134657/ 与当时的 RimUnityPostFxUnpatch.cs。

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

            new Entry("filterRjwpeStaleDefs", GroupSpam,
                "PE 模组的残留 def 报错",
                "RJW PE 的 SoundDef_Replacement.xml 用的是已被作者改名的旧类型 rjwpe.SoundReplacementDef"
                + "（2025-09-04 起改叫 rjwpe.VoiceReplacementDef，新文件 ReplacementDef_Voice.xml 已完整实现该功能），"
                + "启动时必报「Type ... is not a Def type」。那批定义从来没生效过，"
                + "本项把它摘掉 —— 行为与实际完全等价，只是不再刷红字。"
                + "★ 作者若把类型加回来，本项自动失效、什么都不做。",
                "GNH.LocalFixes.Patch_LoadModXML_FilterRjwpeStaleDefs"),

            new Entry("riFurnitureRoyalCompatQuiet", GroupSpam,
                "RI-Furniture 皇权自检刷屏",
                "每次启动刷 54 条「皇权房间要求补丁未生效」。成因是 disroom.mashiro 把爵位的"
                + " throneRoomRequirements / bedroomRequirements 整片清空，而那是玩家故意启用的效果 ——"
                + " RI 的家具因此必然加不进去，属于预期中的设计冲突。"
                + "本项只静音这一类自检结论，该模组的其它日志一律照常输出。"),

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
                "当 telanda 的动物遗传模组与 ElToro Patches 同时在场时，同一个类型名会同时存在于两个程序集。"
                + "本项保证解析到 telanda 的那一份；telanda 不在场时什么都不做。", "GNH.LocalFixes.Patch_GenTypes_PreferTelanda"),

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
                + "本项在它之后按你自己存过的值恢复，不写死任何开关。"),

            // ---------------- XML 补丁（可单独关闭） ----------------
            //
            // 这一组解决的是「Patches\ 下的 XML 补丁以前只能靠删文件关掉」这个体验问题。
            // 做法：给 PatchOperation.Apply 挂一个前缀，按 sourceFile 的文件名判断
            // 这一条属于哪个文件、对应哪个开关，关掉就直接跳过（不执行）。
            //
            // ⚠ 总控那一项（xmlPatchGate）控制的是「这套开关机制本身是否生效」：
            //   关掉它 = 不跳过任何 XML 补丁 = 全部执行。它本身不修任何问题。
            new Entry("xmlPatchGate", GroupXmlPatches,
                "让 XML 补丁也能单独关闭",
                "开启时：下面每个 XML 补丁文件各有一个开关，关掉哪个就只有那个文件里的修复不生效。"
                + "关闭时：所有 XML 补丁照常全部执行（等于把它们全部打开）。"
                + "★ 这一项只决定「开关是否生效」，本身不修任何问题。",
                "GNH.LocalFixes.Patch_PatchOperation_Apply"),

            new Entry("xmlConfigErrorCleanup", GroupXmlPatches,
                "配置自检噪音 + 字段级清理",
                "一个文件里装着多条彼此独立的小修复：量子温控设备的射击/视线豁免、"
                + "Sex_MC_UAP 标签方括号豁免、softresin 当成装备的问题，"
                + "以及【33】LT 贞操带/胸罩的耐久与可燃字段、【34】两个基因分类排序撞号、"
                + "【35】OA 会计记录里的非法字段、【36】三条指向不存在 def 的交叉引用。",
                xmlPatchFile: "ConfigErrorCleanup.xml"),

            new Entry("xmlDamageDefPurificationBurn", GroupXmlPatches,
                "把 HediffDef 当 DamageDef 引用（【32】）",
                "Fortification Industrial - Citadel 在 damageMultipliers 里写了 PurificationBurn，"
                + "而它在 Monolyn Race 里是 HediffDef —— 每次都报「No Verse.DamageDef named」。"
                + "摘掉那条从来没生效过的倍率条目。",
                xmlPatchFile: "DamageDefPurificationBurnFix.xml"),

            new Entry("xmlElToroAnimsRottiRef", GroupXmlPatches,
                "通用动物动画的 Rotti 死引用（【7】）",
                "动画定义里还列着已经被删掉的 Rotti 种族，加载时刷悬空引用。",
                xmlPatchFile: "ElToroAnimsRottiRefFix.xml"),

            new Entry("xmlTY2ValleyArtillery", GroupXmlPatches,
                "溪谷联邦炮塔引用已删除的类型（【13】）",
                "该模组往炮塔里塞了 VFE Security 1.6 已删除的 CompProperties_LongRangeArtillery，"
                + "整个 Def 加载失败、炮塔消失。本项把这个节点摘掉。",
                xmlPatchFile: "FixTY2ValleyLongRangeArtillery.xml"),

            new Entry("xmlIncestuosIssueRestore", GroupXmlPatches,
                "补回被删掉的戒律 issue（【30】）",
                "「乱伦许可」把 Incestuos_Disapproved_CloseOnly 的 issue 节点删了，"
                + "而它自己的 preceptClass 是空的 —— 打开文化界面一次刷 350 条空引用。"
                + "本项只把 issue 补回来，不复活被一并删掉的 comps。",
                xmlPatchFile: "IncestuosCloseOnlyIssueRestore.xml"),

            new Entry("xmlMiliraNavigatorUniform", GroupXmlPatches,
                "米莉拉领航员制服的 layers / comps（【31】）",
                "那件制服把父类的 apparel/layers 与 comps 写成了含空白文本的空元素，"
                + "导致每次染色都报「Failed to get last layer」，还丢了「避免友伤」。",
                xmlPatchFile: "MiliraNavigatorUniformFix.xml"),

            new Entry("xmlOasfcTsarXenotype", GroupXmlPatches,
                "金鸢尾兰「不知名的沙皇」缺 xenotypeSet（【18】）",
                "该模板没有 xenotypeSet，生成出来会变成普通智人。本项补一个固定的。",
                xmlPatchFile: "OASFC_TsarXenotypeFix.xml"),

            new Entry("xmlSnowRabbitTattooIcon", GroupXmlPatches,
                "雪兔纹身图标大小写不符（【19】）",
                "iconPath 与磁盘文件名大小写不一致，文化界面刷红字。本项改回真实文件名。",
                xmlPatchFile: "SnowRabbitTattooIconFix.xml"),

            new Entry("xmlVfeeRoyalTitleExt", GroupXmlPatches,
                "VFE Empire 爵位扩展被清空（【3】）",
                "disroom.mashiro 把爵位的 modExtensions 整片换空，"
                + "而 WorldComponent_Hierarchy 取扩展时不判空 —— 会中断整轮世界组件初始化。"
                + "本项按「实际生效的 def」逐个补回。"
                + "⚠ 属于补丁打补丁：必须排在 disroom.mashiro 之后，见 About.xml 的 loadAfter。",
                xmlPatchFile: "VFEE_RoyalTitleExtFix.xml"),

            new Entry("xmlVfePianoFirepit", GroupXmlPatches,
                "VFE 钢琴/营火/石棺补丁没执行（【8】）",
                "VFE 自己的 PatchOperationFindMod 比的是显示名，中文下 Royalty 被译成「皇权」而匹配不上。"
                + "本项补做那 8 条操作。"
                + "★ 若「中文名判定」那一项（findModLanguage）开着，VFE 自己就会执行，本项会自动匹配 0 个节点、不动作。",
                xmlPatchFile: "VFEPianoFirepitRestore.xml"),
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

        // ======================================================================
        // 设置界面的状态提示
        // ======================================================================
        //
        // 【为什么需要这个】
        //
        // 玩家看不出「这一项现在到底还有没有活干」。两种情况尤其容易困惑：
        //   ① 自己关掉了，但界面只有一个没勾的框，翻半天才发现；
        //   ② **被修的模组根本没装** —— 这一项从头到尾都在空转，
        //      却和真正在干活的项长得一模一样。
        //
        // 所以界面上只对**异常状态**做提示，正常项一律不标（避免噪音）。
        //
        // 【为什么不用「作者修好了」作为状态】
        //
        // 那需要每项各自写一套「目标现在是好的还是坏的」检测逻辑，
        // 三十六项没有统一判据，猜错方向反而会骗人。
        // 这里只做**能确定**的两件事：开关值、前置模组在不在场。
        // ======================================================================
        internal enum FixStatus
        {
            /// <summary>开关开着、前置也在场 —— 正常，界面不标注。</summary>
            Active,

            /// <summary>玩家在设置里关掉了。</summary>
            Disabled,

            /// <summary>这一项的前置模组没装 / 没启用 —— 修了也没对象。</summary>
            TargetMissing,
        }

        /// <summary>
        /// 开关 Id → 前置模组的 packageId。
        /// 只登记**依赖单一模组**的项；依赖多个来源、或修的是原版行为的，
        /// 一律不登记（不登记就不会误报「目标未安装」）。
        /// </summary>
        private static readonly Dictionary<string, string> requiresModById =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "nmmIdeoTrackerNull", "dord.nuditymattersmore" },
            { "alliesAreHelpfulCurJobNull", "ninagoblin.alliesarehelpful" },
            { "srceLibConcurrency", "Gugustar.SnowRabbitRaceBeta" },
            { "gfmIdleCombatDeepCopy", "arandomkiwi.guardsforme" },
            { "ccoeReflection", "keahuy.rjw.ccoe" },
            { "fixVfeeInstrumentSpace", "OskarPotocki.VFE.Empire" },
            { "vehicleFrameworkDebug", "SmashPhil.VehicleFramework" },
            { "filterRjwpeStaleDefs", "readyforjeff.rjwpeculiarinstitution" },
            { "patchVerbPropertiesAdjustedRange", "TOT.CeleTech.MKIII" },
            { "patchGenTypesPreferTelanda", "telanda.rjw.animalgeneinheritance" },
            { "patchLoadModXMLFilterTelandaGenes", "telanda.rjw.animalgeneinheritance" },
            { "cumpilationSettingsRestore", "vegapnk.cumpilation" },
            { "rjwGenesSpamQuiet", "vegapnk.rjw.genes" },

            { "xmlDamageDefPurificationBurn", "Aoba.Fortress.Industrial.Citadel" },
            { "xmlElToroAnimsRottiRef", "ElToro.Anims" },
            { "xmlTY2ValleyArtillery", "TYll.valley" },
            { "xmlIncestuosIssueRestore", "worldie.allowconsensualincest" },
            { "xmlMiliraNavigatorUniform", "Ariandel.MiliraImperium" },
            { "xmlOasfcTsarXenotype", "WW.OberoniaAurea.SteppedFlowerCake" },
            { "xmlSnowRabbitTattooIcon", "Gugustar.SnowRabbitRaceBeta" },
            { "xmlVfeeRoyalTitleExt", "OskarPotocki.VFE.Empire" },
            { "xmlVfePianoFirepit", "VanillaExpanded.VFECore" },
        };

        /// <summary>
        /// 某一项当前的状态。判据只有两条，且都是**能确定**的：
        ///   ① 开关是不是关着；
        ///   ② 登记过的前置模组在不在场。
        /// 任何异常都当作 Active（不显示提示），宁可少标也不误标。
        /// </summary>
        internal static FixStatus StatusOf(Entry entry)
        {
            try
            {
                if (entry == null)
                {
                    return FixStatus.Active;
                }

                if (!LocalFixesMod.IsFixEnabled(entry.Id))
                {
                    return FixStatus.Disabled;
                }

                string modId;
                if (requiresModById.TryGetValue(entry.Id, out modId) && !modId.NullOrEmpty())
                {
                    if (!ModsConfig.IsActive(modId))
                    {
                        return FixStatus.TargetMissing;
                    }
                }
            }
            catch (System.Exception)
            {
                // 状态提示本身绝不能影响开关的读写，出错就当作正常。
            }
            return FixStatus.Active;
        }
    }
}
