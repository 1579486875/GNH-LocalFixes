// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
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
        // 诊断工具单独成组，并且排在**最前面**（2026-11-05 用户要求）。
        //
        // 为什么单独一组：它不是修复项，默认关闭，玩家没有排查需求时本不该被它打扰；
        // 但真要排查时，位置必须一眼能找到 —— 埋在一长串「我修了什么」的末尾，
        // 等于让人翻三次才找到那个「帮我看看哪里坏了」的开关。
        internal const string GroupDiagnostics = "GNH_LocalFixes_Group_Diagnostics";
        internal const string GroupCrash = "GNH_LocalFixes_Group_Crash";
        internal const string GroupSpam = "GNH_LocalFixes_Group_Spam";
        internal const string GroupCompat = "GNH_LocalFixes_Group_Compat";
        internal const string GroupBehaviour = "GNH_LocalFixes_Group_Behaviour";
        internal const string GroupAdvanced = "GNH_LocalFixes_Group_Advanced";
        internal const string GroupXmlPatches = "GNH_LocalFixes_Group_XmlPatches";

        /// <summary>
        /// 分组键 → 取不到翻译时显示的中文。
        /// 语言文件丢了、或将来新增分组忘了加键时，界面上至少是一句能看懂的中文，
        /// 而不是把 "GNH_LocalFixes_Group_Xxx" 这种机器串画出来。
        /// </summary>
        internal static string GroupFallback(string groupKey)
        {
            if (groupKey == GroupDiagnostics) return "诊断工具（默认关闭）";
            if (groupKey == GroupCrash) return "崩溃与卡死";
            if (groupKey == GroupSpam) return "日志刷屏";
            if (groupKey == GroupCompat) return "兼容与判定";
            if (groupKey == GroupBehaviour) return "行为与设置";
            if (groupKey == GroupAdvanced) return "性能剖析（默认关闭）";
            if (groupKey == GroupXmlPatches) return "XML 补丁（可单独关闭）";
            return groupKey;
        }

        /// <summary>
        /// 这一项的**出厂默认值是不是「关」**。
        ///
        /// <para>
        /// 默认关的只有两类：一类关掉的是**游戏自带的功能**
        /// （deepProfilerQuiet 的性能剖析），值不值得关该由玩家自己决定；
        /// 另一类是**诊断工具**（ew2TranspilerProbe），它不修任何东西、只多打几段日志，
        /// 没有排查需求的人没必要替这事儿付日志噪声。
        /// 其余各项修的都是「模组写错导致的毛病」，默认开着才是对的。
        /// </para>
        ///
        /// <para>
        /// ⚠ 这个判断**只有这一处**：`GNHLocalFixesSettings.IsOn()`（玩家没存过这一项时）
        /// 与 `LocalFixesMod.IsFixEnabled()`（设置还没读出来时）都调它。
        /// 千万别在别的文件里再写一份 id 字符串 —— 一旦两边写岔，
        /// 现象是「界面上显示关着，实际却装上了」，而日志里看不出任何异常。
        /// </para>
        /// </summary>
        /// <summary>
        /// 这一项是不是「诊断/取证」类 —— 供 `allDiagnostics` 总控用。
        ///
        /// <para>
        /// **`allDiagnostics` 自己不在这个集合里**，否则它会把自己也算进去，变成自引用。
        /// </para>
        /// </summary>
        internal static bool IsDiagnostic(string id)
        {
            return string.Equals(id, "ew2TranspilerProbe", StringComparison.Ordinal)
                || string.Equals(id, "pawnGenDeathProbe", StringComparison.Ordinal)
                || string.Equals(id, "parsedIntLocator", StringComparison.Ordinal)
                || string.Equals(id, "allDiagnostics", StringComparison.Ordinal);
        }

        internal static bool IsDefaultOff(string id)
        {
            return string.Equals(id, "deepProfilerQuiet", StringComparison.Ordinal)
                || string.Equals(id, "ew2TranspilerProbe", StringComparison.Ordinal)
                // 【54】的取证探头：2026-11-12 加它时漏登记，于是默认是**开着**的，
                // 每局往日志里最多写 24 条 ERROR，而 About 与界面都写着「默认关闭」——
                // 文档与实现不一致。它与 ew2TranspilerProbe 同属「只读取证」，
                // 没有排查需求时不该打扰任何人。
                || string.Equals(id, "pawnGenDeathProbe", StringComparison.Ordinal)
                // 【69】与上面几个同属「只读取证」：它给日志加行、
                // 不改变任何加载结果，没有排查需求时不该打扰任何人。
                || string.Equals(id, "parsedIntLocator", StringComparison.Ordinal);
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
            // ---------------- 诊断工具（默认关闭） ----------------
            //
            // 这一组**排在最前面**（用户 2026-11-05 明确要求）：它不是修复项，平时默认关着、
            // 不该打扰任何人；但真要排查疑难时，位置必须一眼能找到 —— 埋在一长串
            // 「我修了什么」的末尾，等于让人翻三次才找到「帮我看看哪里坏了」那个开关。
            //
            // 同样因为它不是修复，**不占修复清单的编号**（编号只给真正修东西的项）。            // 【2026-11-13】总控：开了它 = 下面三个诊断一起开。它们各自仍可单控。
            new Entry("allDiagnostics", GroupDiagnostics,
                "全部诊断一起开（总控，默认关闭）",
                "本组下面那三个都是**只读取证**工具："
                + "「详细错误勘探」（查 transpiler 撞车）、"
                + "「世界生成期死亡取证」（查领袖生不出来）、"
                + "「定位 Parsed X as int」（查哪个 XML 把小数值写进整数字段）。"
                + "排查疑难时常常要同时开两三个，一个个点很麻烦 —— 这个总控就是为了省那几下。"
                + "★ 它**不替代**那三个各自的开关：关掉它，那三个回到「各看各的」；"
                + "**它们原来的勾选状态不会被改动**，所以你之前单独开的那个还是开的。"
                + "★ 它自己也默认关闭 —— 不排查时不该让日志多出任何一行。",
                null),

            new Entry("ew2TranspilerProbe", GroupDiagnostics,
                "详细错误勘探（默认关闭）",
                "专门给「游戏一闪就没了、日志里一个字都没有」这类疑难用的只读勘探工具。"
                + "ExtraWeapon2 是用 transpiler 直接改游戏方法内部指令的，而且它决定"
                + "「改哪一处」的办法是数某个调用第几次出现。只要前面还有别的模组也改过同一个方法，"
                + "这个「第几次」就会数偏 —— 补丁报告安装成功，实际却改错位置，"
                + "表现就是武器行为不正常，或者一点开火进程瞬间消失、连一行报错都留不下。"
                + "开启本项后重开游戏，它会在进到界面时扫一遍 ExtraWeapon2 动过的那几个方法，"
                + "把每个方法上挂着几个补丁、分别来自哪个 Harmony ID 全部写进日志；"
                + "同一方法上有两个以上 transpiler 就标星点名。"
                + "它只读：不改任何指令、不卸载别人的补丁，最坏只是多几行日志。"
                + "用法：开本项 → 重启游戏 → 日志里搜 [GNH LocalFixes] 看那几行。"
                + "没有排查需求时建议一直关着。"),

            // ---------------- 崩溃与卡死 ----------------
            //
            // 【45】放在本组第一条：它修的不是某一个模组，而是「一个补丁类坏掉、
            //   整批补丁陪葬」这个结构性问题。影响面比下面任何一条都大。
            new Entry("harmonyPatchClassIsolate", GroupCrash,
                "单个补丁失败不再连累整批（【45】）",
                "Harmony 批量安装补丁时没有逐个兜底：只要有一个补丁类找不到目标方法，"
                + "整批安装立刻中断，排在后面的全部不生效。而模组通常把安装写在静态构造里，"
                + "于是「一个方法改了签名」会变成「整个模组几十个功能集体消失」，"
                + "日志里却只有一条红字。本项在 Harmony 的错误边界上加一层拦截："
                + "坏掉的补丁类仍然不会生效（它本来就不可能生效），但会被单独点名报出来，"
                + "同批其余补丁照常安装。本机实测的受害者是 RimTalk Context Upgrade"
                + "（RimTalk 1.3 改了 DecorateText 签名，导致它 30 多个补丁全灭）"
                + "与 CQF-RT（目标类型不存在，同样整批失效）。",
                "GNH.LocalFixes.Patch_PatchClassProcessor_Isolate"),

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

            // 【50】放在本组最前面：它修的不是「某处报红字」，而是
            //   「一个派系的 BOSS 生不出来」被放大成「整个世界生成步骤失败」。
            //   影响面是**开新档**——轻则该派系没有领袖，重则档根本建不出来。
            new Entry("rjwPawnGenPostfixNull", GroupCrash,
                "RJW 生成失败被放大成世界生成崩溃（【50】）",
                "RJW 给「生成小人」挂的那段后处理没有判空：一旦上游重试了上百次之后"
                + "只能返回空（例：某个派系的 BOSS 基因组合必死），它还是会对空对象做后处理，"
                + "当场抛空引用。这一抛不要紧，它位于世界生成的派系步骤里，"
                + "于是「某个领袖生不出来」被放大成「WorldGenStep_Factions 失败」。"
                + "本项在那个后处理的最前面加一句「结果为空就直接返回」，"
                + "再用一层只认「结果为空 + 空引用」的兜底把漏网的情形吞掉并记一条日志。"
                + "★ 结果非空时完全走原逻辑，一个字节都不改；"
                + "★ 它只管「不要让一个生成失败拖垮整步」，**不负责**修那个本身生不出小人的派系。",
                "GNH.LocalFixes.RjwPawnGenPostfixNullFix"),

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

            // 2026-10-30 新增：原版只让「需要实时绘制」的建筑进 DynamicDrawPhase 循环，
            // RavenRace 的史莱姆覆盖层在那里读一个没有判空的静态设置，取不到就每帧抛 NRE。
            new Entry("realtimeDrawNreSwallow", GroupSpam,
                "实时绘制空引用刷屏（【38】）",
                "某些建筑（如 Dubs Rimatomics 的反应堆控制台）需要逐帧绘制，"
                + "而 RavenRace 的史莱姆覆盖层在这条路上读一个没有判空的静态设置 —— "
                + "取不到就每帧抛一次 NullReferenceException。实测 8.5 小时刷出 66 万条 ERROR、"
                + "2.07 GB 日志、进程内存涨到 28 GB。"
                + "本项只吞空引用并按 500 次一条记录，其它异常照常抛出。"
                + "★ 找不到 RavenRace 的史莱姆覆盖层时会自动让路、什么都不做；"
                + "上游补上判空后异常不再出现，本项也就自然不再动作。",
                "GNH.LocalFixes.Patch_Thing_DynamicDrawPhase_SwallowRealtimeNre"),

            // 【51】放在本组第一条之后：它的危害不是「报错」，而是**把日志打满**。
            //   Verse.Log 累计 10,000 条之后会执行 Debug.unityLogger.logEnabled = false，
            //   于是从那以后所有模组的报错都写不进日志 —— 比崩溃更难查。
            //   实测一次 4 小时的会话被这一条刷了 57,137 条。
            // 【53】紧跟【50】：两者都属于「加载期一个异常把整局拖垮」这一类。
            //   区别是【50】修 RJW 的锅，本项修边缘仙路自己的锅 —— 而且它的 bug 更隐蔽：
            //   `try/catch` 把 AddCopyBookRecipes 的异常吞了，但 `finally` 里又解引用同一个
            //   还没初始化的 DefOf 字段，**finally 不归 catch 管**，异常直接逃出去。
            new Entry("xiuxianCopyBookGuard", GroupCrash,
                "边缘仙路配方补丁在 DefOf 就绪前必崩（【53】）",
                "边缘仙路（RimImmortal-Core）在「配方生成结果」那一步挂了一个 postfix，"
                + "里面用 try/catch 包住自己的逻辑，但**收尾的 finally 里又去解引用同一个 DefOf 字段** ——"
                + "而 DefOf 要等所有 Def 加载完才注入，那时它还是 null。"
                + "结果就是：catch 把第一处空引用吞了、只打一行日志，"
                + "紧接着 finally 又炸一次，这次没人接，"
                + "异常直接逃出整个流程，把 play data 加载打断 ——"
                + "游戏随后会重置模组配置并放弃加载。"
                + "本项在它前面加一道守卫：那个 DefOf 字段还是 null 时，整个 postfix 跳过。"
                + "★ 字段就绪之后自动放行，不是永久停用；"
                + "★ 1.7 侧通常根本不会触发（那边时序不同，DefOf 已就绪），这正是预期。",
                "GNH.LocalFixes.XiuXianCopyBookPostfixGuardFix"),

            // 【61】是「UFC 领袖生不出来」这条链的**最后一环**：
            //   前因（【54】）是装配义体时多做了一次死亡判定；
            //   真正把 pawn 判死的是 Just A Flesh Wound 对致命伤害的重算。
            //   两者各管一段，缺一都不算修完。
            new Entry("jafwLethalDamageGenGuard", GroupCrash,
                "世界生成期被「Just A Flesh Wound」判死（【61】）",
                "「[Kit] Just A Flesh Wound」重做了致命伤害：它把每一条 MissingBodyPart "
                + "按「该部位的最大生命值」折算成伤害、四肢还有倍率，从一份「韧性」里扣，扣光就判死。"
                + "问题在于——**装义体本身就会产生一批 MissingBodyPart**"
                + "（BionicLeg 替代原腿，引擎会把原腿及其子部位标成缺失）。"
                + "于是 UFC 那种「全身义体化打手」在世界生成时一次装 4 件义体，"
                + "双臂双腿连指带趾 36 个部位全变缺失，韧性当场扣光、pawn 被判死，"
                + "领袖连生 120 次全败、那个派系开局没有领袖，顺带刷出 720 条红字。"
                + "本项只在「这只小人正在被生成」时跳过它那一次重算，"
                + "**游戏内真实受伤与断肢时 Just A Flesh Wound 的逻辑一个字节都不改**。"
                + "★ 实测依据：取证探头抓到 InjurySum=0、阈值 150，原版算法本该返回 false，"
                + "而它返回 true —— 顺着这条矛盾反编译到 JustAFleshWound 的 Postfix，"
                + "里面写着 `current is Hediff_MissingPart ? part.def.hitPoints * healthScale : Severity`。"
                + "★ 把它的「新致命伤害系统」关掉后本项即便装上也不产生任何影响（跳过一个空操作）。",
                "GNH.LocalFixes.JafwLethalDamageGenerationGuardFix"),

            // 【65】第三轮日志诊断 P2-2：雪兔的分层建筑渲染在非主线程加载贴图。
            new Entry("snowRabbitGraphicThread", GroupCrash,
                "雪兔的分层建筑上层显示成紫块（【65】）",
                "「SnowRabbit」（工坊 3643594275）的 `Comp_LayeredBuildingRenderer` 把贴图加载"
                + "放在了 `Initialize()` 里，而 `Initialize` 会被 `ThingWithComps.ExposeData` 触发 ——"
                + "**`ExposeData` 读档时跑在 ScribeLoader 的异步线程上，不是主线程**。"
                + "Unity 的 `Texture2D` 加载必须在主线程，所以这条必然失败："
                + "日志里就是「Attempted to load a graphic off the main thread」，"
                + "而那栋建筑的上层从此显示成紫块。"
                + "★ 更麻烦的是它的 `PostSpawnSetup` 里那句补加载写成 `if (!respawningAfterLoad)`，"
                + "读档时这个值是 true，所以**永远不会补**。"
                + "★ 本项把「加载贴图」挪到第一次 `PostDraw`（一定在主线程）："
                + "非主线程时跳过原调用、主线程里发现 `upperGraphic` 还是 null 就补一次。"
                + "**作者的代码一行没改**，只是把加载时机挪到了它本来就该在的线程上。",
                "GNH.LocalFixes.SnowRabbitGraphicThreadFix"),

            // 【66】第三轮日志诊断 P1-2：RitualObligation 的 ID 恒为 0，注册时撞车。
            new Entry("ritualObligationId", GroupCrash,
                "读档时「仪式义务」的 ID 撞车（【66】）",
                "读档刷三条 `Cannot register RimWorld.RitualObligation, (id=RitualObligation_0 …)`，"
                + "其中 `_0` 说明那个对象的 load id 是 **0**。"
                + "反编译 `RimWorld.RitualObligation` 确认：`public int ID;` 默认就是 0，"
                + "而**只有带参数的构造函数**才调 `Find.UniqueIDsManager.GetNextRitualObligationID()`；"
                + "无参构造函数是空的 —— 任何代码只要走它，ID 就恒为 0。"
                + "于是两个这样的对象同时存在时，`GetUniqueLoadID()` 都返回 `RitualObligation_0`，"
                + "`CrossRefHandler` 注册第二个时撞 ID、报错、拿不到引用。"
                + "★ 后果：仪式义务列表里可能有一条被顶掉（玩家看不到某个待办仪式）。"
                + "★ 本项在 `CrossRefHandler.ResolveAllCrossReferences` 的 Prefix 里扫一遍"
                + "所有 Ideo 的所有 `Precept_Ritual.activeObligations` / `completedObligations`，"
                + "把 `ID <= 0` 的补成唯一 ID。"
                + "★ 这是**注册前**的最后一站：更早拿不到完整的 IdeoManager，更晚报错已经发生。"
                + "★ `ID` 是唯一标识、本来就不该是 0，补它不改变任何游戏逻辑。"
                + "★ 另外两条 `Cannot register`（`IdeoBuildingPresenceDemand_197` 与 CE 的 `Verb_…`）"
                + "成因不同、未定位，本项不动它们（按 §0.2：没定位到根因就不动）。",
                "GNH.LocalFixes.RitualObligationIdFix"),

            // 【67】第三轮日志诊断 P2-1：Notify_NewLink 重复告警，纯噪音。
            new Entry("notifyNewLinkNoise", GroupSpam,
                "读档时「同一条设施链接被重复登记」的告警（【67】）",
                "读档刷 12 条 `Notify_NewLink was called but the link is already here.`，"
                + "全在 `Map.FinalizeLoading` 期间、全是 `Building_WorkTable`。"
                + "★ **报告里说的「设施加成失效」不成立** —— 反编译原文显示："
                + "发现重复时它 `Log.Error(...)` 然后 `return`，"
                + "**不会再把同一个 facility 加进 linkedFacilities**。"
                + "所以那份列表一直是完整的，那 12 个工作台的加成没有失效，"
                + "这只是**多了一次多余调用**产生的噪音。"
                + "★ 本项在重复时由我们自己返回（等价于原方法的「返回、不添加」），"
                + "少掉那条 Error，同时打一条**信息量更大的 Warning**"
                + "（带 parent 与 facility 的 Label），方便定位是哪个模组在重复调用。"
                + "★ 最多打 6 条明细，之后只静音不刷屏。",
                "GNH.LocalFixes.NotifyNewLinkNoiseFix"),

            // 【68】第三轮日志诊断 P3-5：鼠族基因扩展留了三条调试输出。
            new Entry("debugSpamPrefix", GroupSpam,
                "读档刷「Debug:________isCustomSite」等调试输出（【68】）",
                "每次读档刷三条 `Debug:________isCustomSite / parentQuest / customQuests count`。"
                + "`Debug:` 后面那串下划线本身就说明「这是开发者准备事后删的」。"
                + "★ 本轮定位：全库扫 5,505 个 dll + 132,043 个 xml（按 UTF-16LE 搜字符串），"
                + "三个 needle **只在一个文件**里命中 —— "
                + "`RatkinGeneExpanded.dll`（鼠族基因扩展，工坊 3043354134）。"
                + "反编译 `RatkinGeneExpanded.CustomSiteComp.PostExposeData()` 看到："
                + "三条 `Log.Warning(\"Debug:________…\")` 夹在 `Scribe_*` 与 "
                + "`TryInitializeCustomSite` 之间，不做任何判断、不改变任何状态。"
                + "★ 修法：给 `Log.Warning(string)` 挂 Prefix，**按那 13 个字符的前缀过滤**。"
                + "判据极窄（`StartsWith(\"Debug:________\")`），正常日志里不可能出现；"
                + "而且不依赖「哪个方法调用」，作者怎么改都还能拦住。"
                + "★ 顺带把其它模组同风格的调试输出也挡住。",
                "GNH.LocalFixes.DebugSpamPrefixFix"),

            // 【69】诊断：把「Parsed X as int」定位到具体 XML 文件。默认关闭。
            new Entry("parsedIntLocator", GroupDiagnostics,
                "定位「Parsed X as int」的具体来源文件（【69】，默认关闭）",
                "第三轮日志诊断里那 181 条 `Parsed <小数> as int.`，141 条已由【64】修掉，"
                + "**还剩约 40 条没定位** —— 堆栈是 `ParseAndSetComplexTypeField_System_Int32`，"
                + "日志里既没有 defName 也没有文件名，光看日志找不出来；"
                + "全库搜那些值也没用（0.1 / 1.5 / 14.9 这类在小数字段里太常见）。"
                + "★ 本项是一条**纯诊断**：给 `DirectXmlToObjectNew.DefFromNodeNew` 与 "
                + "`ParseHelper.ParseIntPermissive` 各挂一个 Prefix —— 前者记下"
                + "「现在在解析哪个 XML 文件」（`LoadableXmlAsset.name` / `fullFolderPath`），"
                + "后者只在小数时把这条信息一起打出来。"
                + "★ **它不改变任何加载结果**，开着关着都不影响存档。"
                + "★ **默认关闭**（因为它会给日志加行）：要排查那 40 条时在设置里打开，查完关掉。",
                "GNH.LocalFixes.ParsedIntLocatorDiagnostic"),

            // 【60】是一条「int 字段被写成小数」的修正：值本身没变（0.5 早被截断成 0），
            //   只是把同一个值写成引擎认得的写法，警告随之消失。
            new Entry("xmlAncientThreatIntField", GroupXmlPatches,
                "一个整数字段被写成了小数，每次加载刷 38 条（【60】）",
                "「Ancient amorphous threat」用一条补丁往所有叙事者的父类里加了个组件，"
                + "其中「第几天触发」那一项写成了 0.5 —— 而它在引擎里是**整数**字段。"
                + "引擎不会报错，而是把 0.5 截断成 0，再打一条「Parsed 0.5 as int.」；"
                + "因为那条补丁加在「所有叙事者的父类」上，每个叙事者解析时各报一次，"
                + "于是 1 处写错被放大成每次加载 38 条。"
                + "本项把那个值写成显式整数 0。"
                + "★ 行为一点没变：原来那 0.5 本来就被截断成 0，这个事件一直就是「第 0 天触发」；"
                + "★ 没有改成 1，是因为那会把触发时间往后推一天，属于改作者的数值设计；"
                + "★ 作者哪天自己改成整数，本项自动让路（xpath 只匹配文本为 0.5 的那一处）。",
                xmlPatchFile: "Patch_AncientThreatIntFieldFix.xml"),

            // 【59】纯美术：texPath 少了一层子目录。图其实就在同族主包里。
            new Entry("xmlNivarianStarfallBeaconTexPath", GroupXmlPatches,
                "涅瓦莲星陨信标贴图路径少一层目录（【59】）",
                "涅瓦莲的附加包给「星陨信标」写的贴图路径是"
                + "「Nivarian/Building/starfall_beacon」，"
                + "而主包 keeptpa.NivarianRace 里那张图的实际位置是"
                + "「Nivarian/Building/StarfallBeacon/starfall_beacon.png」——"
                + "中间少了 StarfallBeacon 这一层。于是加载期报两条"
                + "「Could not load Texture2D」，游戏里那个建筑显示成紫白方块。"
                + "本项只把路径改成真正存在的那一条。"
                + "★ 图是同一套涅瓦莲美术、文件名一致，视觉呈现与作者原意完全一致；"
                + "★ 主包没启用时整段跳过（路径指向的是主包的 Textures）。"
                + "★ 「翠绿祝福」引用 lotus 的那条同样是少一层目录（正确位置是 "
                + "Nivarian/Building/VerdantLocus/lotus），已一并修好；"
                + "两条都只是把路径补全，用的都是本体自己那份原件，视觉与作者原意一致。",
                xmlPatchFile: "Patch_NivarianStarfallBeaconTexPath.xml"),

            // 【64】第三轮日志诊断 P1-1：LDR（边缘弹道导弹:【地球科技】）把 141 个
            //   Int32 字段写成了小数 —— 全被静默截断成 0。
            //   ★ 第三轮报告把这条标成「写入方未定位（C 级）」，本轮定位到了：
            //     IRBM 本体那份 XML 是干净的，写小数的是 LDR 的 Buildings_MissileSilo_LDR.xml。
            new Entry("xmlSiloTubeIntField", GroupXmlPatches,
                "发射井的管子尺寸与开盖动画被截断成 0（【64】）",
                "「边缘弹道导弹:【地球科技】」（nekotee.irbmldr）那份 1,157 行的 XML 里，"
                + "把 141 个值写成了小数：`size` 有 0.1/0.2/0.3 共 70 处，"
                + "`coverMovementTicks` 有 0.5/0.1 共 71 处 ——"
                + "而这两个字段在 `IRBM.SiloTube` 里都是 **Int32**"
                + "（反编译确认：整类里只有这两个不是 float/Vector2/String）。"
                + "引擎不报错，用 `ParseIntPermissive` 悄悄截断成 0，再各打一条"
                + "「Parsed 0.5 as int.」—— 于是 141 个值全变成 0，日志里 158 条警告。"
                + "★ 后果不是噪音：`size` 的默认值是 1，变成 0 后那 10 个 ThingDef 的井盖可能根本不显示；"
                + "`coverMovementTicks` 的默认值是 60（1 秒），变成 0 后开盖没有过渡动画。"
                + "★ 修法按「作者想表达的意思」折算：`size` 一律取 1"
                + "（Int32 的最小有效值，也是它的默认值；作者写 0.1 多半是「想很小」）；"
                + "`coverMovementTicks` 按秒折算成 tick —— 0.5 秒 → 30、0.1 秒 → 6。"
                + "★ 每条 xpath 都带值匹配（`[text()=\"0.1\"]`），"
                + "所以作者哪天自己改成整数，本项自动让路，不需要额外判据。"
                + "★ 一条 xpath 能改全部（反编译确认 `PatchOperationReplace.ApplyWorker` 会遍历所有匹配节点）。",
                xmlPatchFile: "SiloTubeIntFieldFix.xml"),

            // 【58】是 XML 层的：上游把 8 种寿司合并成 4 种，VOE 那个前哨没跟上。
            //   放在 XML 组，因为它由 Patches\Patch_VoeSushiMigration.xml 执行。
            new Entry("xmlVoeSushiMigration", GroupXmlPatches,
                "餐厅前哨还在产已被合并掉的 8 种寿司（【58】）",
                "Vanilla Cooking Expanded - Sushi 在 1.6/1.7 把原来的 8 种寿司合并成 4 种，"
                + "旧 defName 全部删掉，并提供了官方的迁移定义。"
                + "但那份迁移只管**存档**里的引用，管不到模组 XML 之间的引用 ——"
                + "而 VOE 附加前哨的「餐厅」子模块正好是把 8 个旧名字直接写进产出列表里的，"
                + "于是加载期会连打 8 条「找不到 ThingDef」的 cross-reference 错误，"
                + "那个前哨的寿司产出也一并失效。"
                + "本项按上游给出的合并关系把 8 条换成 4 条，数值照抄原文件。"
                + "★ 如实写明让步：能产的寿司品种从 8 种变成 4 种 —— "
                + "这不是我们砍的，是上游自己合并的，我们只是让前哨跟上；产出数值一个都没动。"
                + "★ 上游哪天自己修好、或把新 def 改名，本项会自动让路（每条操作都设了「无论成败都继续」）。",
                xmlPatchFile: "Patch_VoeSushiMigration.xml"),

            // 【57】是那 720 条红字的真正出口：源头是引擎自己的 Faction.OfPlayer 属性 getter，
            //   不是某三个模组的补丁。所以修法也不是"去改三个模组"，而是让属性在
            //   「还没进地图且确实没有玩家派系」时走引擎自带的静默版本。
            new Entry("factionOfPlayerNoise", GroupSpam,
                "世界生成期刷 720 条「找不到玩家派系」（【57】）",
                "每次世界生成都会刷满 240 条「Could not find player faction.」，"
                + "三次生成就是 720 条，占整份日志 ERROR 的八成以上。"
                + "日志把责任指向三个模组的「击杀提示」补丁，但真正打出这句话的是引擎自己："
                + "`Faction.OfPlayer` 这个属性的 getter 在找不到玩家派系时会打一条 Error。"
                + "而世界生成时玩家派系本来就还没建立（得先有地图、有殖民者），"
                + "这是正常的中间状态，不是谁的 bug。"
                + "本项在「还没进地图、且确实没有玩家派系」时改走引擎自带的静默版本。"
                + "★ 返回值与原先**一模一样**（都是 null），三个模组一行代码都不改；"
                + "★ 进游戏之后如果真找不到玩家派系，原版照常报红字 —— 那才是真异常；"
                + "★ 只提示一次，日志里能查到本项在工作。"),

            // 【56】与【55】同一类：日志里只有一行红字，实际是一整块功能没跑起来。
            //   区别是它自己能 fail-soft（上层有 try/catch），所以排在本组末位。
            new Entry("rqpFactionLeaderGuard", GroupCompat,
                "帝国头衔生成时的集合改动异常（【56】）",
                "「Real Faction Guest」在给派系领袖校正背景故事时，"
                + "会在遍历某个集合的过程中改动它 —— 于是抛"
                + "「Collection was modified」，被它自己的上层 catch 掉、"
                + "记成一条「FactionLeaderValidator Failed」加整段堆栈。"
                + "这条异常只在世界生成「初始化帝国头衔」那一步出现一次，"
                + "它自己的 try/catch 本来就会接住，所以**游戏不会中断**；"
                + "真正的影响是那一次背景修正没做成，外加日志里多一段吓人的红字。"
                + "本项只把这一种异常单独摘掉并合并成一条提示。"
                + "★ 如实说明：这属于「让日志说实话」，不是「把功能修好了」——"
                + "要真修得改那个模组的遍历写法，风险远高于收益，故不做；"
                + "★ 别的异常（含真正的空引用）原样上抛，一个都不替别人兜。",
                "GNH.LocalFixes.RqpFactionLeaderValidatorGuardFix"),

            // ---------------- 2026-11-12 第二轮全局日志诊断新增 ----------------
            //
            // 【54】是那 720 条红字的根：世界生成期给派系领袖装义体，装到一半被判死亡，
            //   于是同一个领袖连生 120 次全败，每次失败又触发三处「击杀提示」补丁各打一行。
            //   本组放两条：一条只取证（默认关闭、只读），一条按取证结论动手（默认开启）。
            //   分开的理由很实际 —— 死因的最后一环必须由实机日志说了算，
            //   先取证再加刀，比先猜一个改法把它压下去要靠谱得多。
            new Entry("pawnGenDeathProbe", GroupDiagnostics,
                "世界生成期死亡取证（默认关闭）",
                "专门给「某个派系的领袖怎么都生不出来」这类疑难用的只读勘探工具。"
                + "症状是日志里反复出现「Generated dead pawn」加一串"
                + "「Could not generate a pawn after N tries」，最后该派系没有领袖。"
                + "本项在「引擎准备判定某只小人死亡」的那一刻之前插一脚："
                + "只有当这只小人正在被生成、且此刻确实已满足死亡条件时才动手，"
                + "把它的种族、派系、身上每一条 Hediff（含所在部位与严重度）、"
                + "五项死亡判据的逐条结果、以及全部致命产能的实际数值写进日志。"
                + "★ 它只读：一个变量都不改，最坏只是多几行日志；"
                + "★ 每个进程最多输出 24 条，不会把日志刷爆；"
                + "★ 没有排查需求时建议一直关着。"
                + "用法：开本项 → 重启游戏 → 开一局新档 → 日志里搜「【54】pawn-gen death probe」。"),

            new Entry("pawnGenRestorePartGuard", GroupCrash,
                "世界生成期装义体被误判死亡（【54】）",
                "世界生成时会先给派系成员装配义体。原版装义体的代码里有一句"
                + "「把这个部位恢复成原样」，而这句话默认会顺手做一次死亡判定 ——"
                + "可装配本身还没做完，判定自然是错的，于是小人当场被判定死亡，"
                + "派系领袖连生 120 次全败、每次刷三行红字，累计 720 条。"
                + "本项只在「这只小人正在被生成」这个窗口里，把那次多余的死亡判定关掉。"
                + "★ 依据是引擎作者自己：他在「装假体」那条路径上写的是"
                + " RestorePart(…, checkStateChange: false)，说明他本人也认为这条路径不该判死；"
                + "本项只是把另一条路径补成同样的语义。"
                + "★ 玩家在手术台上装义体、战斗中受伤、日常治疗 —— 全部不受影响；"
                + "★ 生成结束后引擎自己的校验（允许死亡 / 允许倒地）照常生效，"
                + "该拦的照样拦，没有一处功能被拿掉。"),

            new Entry("twilightGameComponentRebuild", GroupCrash,
                "贞操科技的核心组件从来没被创建（【55】）",
                "「缇瓦莱特贞操科技」把「高潮许可挑战」的全部状态放在一个游戏组件里，"
                + "但那个组件的构造函数写成了无参形式，而引擎统一按"
                + "「给你一个 Game 对象」的方式去创建它 —— 参数对不上，创建失败，"
                + "引擎 catch 掉、打一行红字就过去了。"
                + "后果是这套系统的每一个功能都不工作：挑战计时不跑、进度永远为空、"
                + "旧存档迁移逻辑不执行，而日志里从头到尾只有那三行红字。"
                + "本项在引擎建完所有组件之后，用作者写的那种无参构造把它补进去，"
                + "并做幂等检查（引擎在读档时也会重走一遍这条路径）。"
                + "★ 作者写的 GameComponentTick / GetHandler 等逻辑一行不改、全部照跑，"
                + "唯一的变化是「它终于会跑了」；"
                + "★ 模组没装就直接跳过；作者哪天把构造函数改对了，本项会自动让位、什么都不做；"
                + "★ 旧存档里没有这个节点，首次加载按空列表起步（正是它本该有的起点），不崩不丢数据。"),

            new Entry("mechanitorBandwidthGizmo", GroupSpam,
                "机械师带宽面板每帧刷屏（【51】）",
                "选中单个机械师小人时，右上角那个「带宽」小面板会摆一排小方块表示已用带宽。"
                + "它的排版循环有个除零隐患：行数一旦加过头，格子边长被算成 0，"
                + "随后「每行几个」变成无穷大，循环条件永远成立 —— 跑满 1000 次后报一条错、"
                + "整个面板不画，而且**每帧都重来一次**。"
                + "真正要紧的是后半句：Verse.Log 累计 10,000 条之后会把自己整个关掉，"
                + "于是从那以后别的模组报什么错你都看不到。实测一次会话被它刷了 57,137 条。"
                + "本项给「要摆几个格子」加一个上限，让循环一定能收敛；"
                + "再把那条每帧重复的报错降级成「整局只报一次」兜底。"
                + "★ 带宽真超过上限时，面板只画前一部分方块 —— 那么小的区域本来就分辨不出来；"
                + "★ 官方哪天自己修好了，本项会自动让路、什么都不做。",
                "GNH.LocalFixes.MechanitorBandwidthGizmoFix"),

            new Entry("rjwGenesSpamQuiet", GroupSpam,
                "RJW-Genes 调试刷屏",
                "开发者模式下每帧都写一行 [RJW-Genes] multipreg checks（实测一局 3 万条、占整份日志 65%），"
                + "把真正要看的问题全埋掉。本项只拦这一句，该模组的报错与警告照常输出。"),

            new Entry("vehicleFrameworkDebug", GroupSpam,
                "Vehicle Framework 调试菜单报错",
                "打开开发者生成菜单后，该模组找不到对应的 PawnKind 会报错。"
                + "本项把「找不到就报错」改成「查不到就静默跳过」。"),

            // ⚠ 2026-11-05 复审：本项**目前已是空转**（目标文件已不存在），保留是因为它自带
            //   让路判据、留着绝对无害；真要清理请先经作者点头（模板：判为冗余 ≠ 立刻删）。
            //   实测：三根模组目录（游戏 Mods / 1.6 副本 Mods / 工坊 294100）里
            //   SoundDef_Replacement.xml **0 个**，全库 grep `SoundReplacementDef` **0 命中** ——
            //   作者后来的版本已经把那个文件删了，症状不可能再现。
            new Entry("filterRjwpeStaleDefs", GroupSpam,
                "PE 模组的残留 def 报错",
                "RJW PE 的 SoundDef_Replacement.xml 用的是已被作者改名的旧类型 rjwpe.SoundReplacementDef"
                + "（2025-09-04 起改叫 rjwpe.VoiceReplacementDef，新文件 ReplacementDef_Voice.xml 已完整实现该功能），"
                + "旧版启动时必报「Type ... is not a Def type」。那批定义从来没生效过，"
                + "本项把它摘掉 —— 行为与实际完全等价，只是不再刷红字。"
                + "★ 作者若把类型加回来，本项自动失效、什么都不做。"
                + "（2026-11-05 复审：作者后续版本已把那个 XML 删掉，所以本项现在通常什么都不做。）",
                "GNH.LocalFixes.Patch_LoadModXML_FilterRjwpeStaleDefs"),

            new Entry("riFurnitureRoyalCompatQuiet", GroupSpam,
                "RI-Furniture 皇权自检刷屏",
                "每次启动刷 54 条「皇权房间要求补丁未生效」。成因是 disroom.mashiro 把爵位的"
                + " throneRoomRequirements / bedroomRequirements 整片清空，而那是玩家故意启用的效果 ——"
                + " RI 的家具因此必然加不进去，属于预期中的设计冲突。"
                + "本项只静音这一类自检结论，该模组的其它日志一律照常输出。"),

            // ── 2026-10-31 新增三项（来自 1.7 移交清单，都是日志/内存层的噪音）──

            new Entry("logSpamFilter", GroupSpam,
                "两处调试输出刷屏（【39】）",
                "WeaponStats（工坊 974066449）在 Source\\WeaponsTab\\Turret.cs:74 里写着 "
                + "Log.Message(th.def.defName) —— 每开一次武器统计面板，每个炮塔就刷一行"
                + "（实测 21 点一小时 2,737 条，Turret_MiniTurret 552 次）；同文件还有固定串「Update weapons list」。"
                + "RimImmortal.Core（工坊 3296476341）有个前缀方法每次进来都写一行「…::Start」（实测 190 条）。"
                + "两处都是作者留在正式版里的调试输出，不是报错。"
                + "本项在 Verse.Log.Message 上按三个收紧的判据拦下它们，其它日志一律照常输出。"),

            new Entry("fixPawnScribeNull", GroupSpam,
                "读档刷上百条 /fixPawn 报错（【41】）",
                "「裸体评价」的 Fixation 组件在 PostExposeData 里**无条件**执行 "
                + "Scribe_References.Look(ref fixPawn, \"fixPawn\")，而这些存档里的 fixPawn "
                + "在 1.6→1.7 迁移后解析不出来（null）—— 于是登记了一个 null loadID，"
                + "每次读档固定刷「Tried to register the same load ID twice」与"
                + "「never added during LoadingVars」各 66 条（实测涉及 100 个 Pawn）。"
                + "本项只在「label 是 fixPawn 且引用本身是 null」时跳过登记；"
                + "有效引用一个字节都不动，存档序列化格式完全不变。"),

            new Entry("assignableCompDupeGuard", GroupSpam,
                "同一台建筑挂了两个「可分配」组件（【42】）",
                "有些存档里的床 / 座椅身上被塞进了两个 CompAssignableToPawn 系组件，"
                + "两者用同一个父对象登记 /assignedPawns，键完全相同 —— 读档时固定刷"
                + "「Tried to register the same list of load IDs twice」与"
                + "「Could not get load IDs list … never added during LoadingVars」"
                + "（实测 8 + 8 + 8 + 8 = 32 条/局，涉及 2 台）。"
                + "查存档可见该 Thing 块里 assignedPawns 与 uninstalledAssignedPawns 各写了两遍，中间夹着 quality。"
                + "游戏自己遇到这种情况也是「用第一份、丢第二份」，本项只是把这件既定的事提前做完、不再报红字；"
                + "不动任何对象，不改存档格式，单组件的正常建筑完全不受影响。"),

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

            // ── 2026-11-05 新增（ExtraWeapon2，来自玩家求助的复现线索）──
            //    【43】死亡路径护栏 —— 给 EW2 的 Notify_KilledPawn postfix 加 Finalizer，
            //         让它自己的异常不再中断排在后面的死亡处理。
            //
            //  ⚠ 编号说明：同批的「详细错误勘探」是**诊断工具、不是修复**（用户 2026-11-05 明确要求），
            //    已挪到列表最前面的「诊断工具」分组、且不占修复编号。
            //    因此这一项由 44 顶成 43，修复清单保持连续、不留空号。
            //
            //  ⚠ 2026-11-05 复审订正：本条最初的说明写成「EW2 的遍历既没判空、也会撞上集合
            //    被改动」，**与反编译事实不符**。实际 EW2 那层遍历是
            //      for (i…) if (weapons[i]?.Weapon is ThingWithComps x) yield return x;
            //    —— 索引遍历、逐项判空，比原版自己的 foreach 还稳。
            //    真正的风险在被调的 ThingWithComps.Notify_KilledPawn：它读 pawn.Ideo、
            //    再遍历这武器身上所有 comp，第三方 comp 一抛就中断整条 postfix 链。
            //    文案已按事实改写；护栏本身仍然成立，只是理由换了。
            new Entry("ew2DeathPathRepair", GroupCompat,
                "ExtraWeapon2 额外武器击杀回调兜底（【43】）",
                "ExtraWeapon2 在「角色死亡」的统一通知点（Pawn_EquipmentTracker.Notify_KilledPawn）挂了"
                + "一个 postfix，遍历额外武器、逐个调原版的 Notify_KilledPawn。"
                + "它自己那层遍历已经做了防护（按索引走、每项判空），真正的风险落在被调的那个原版方法上："
                + "它会读 pawn.Ideo，再遍历这件武器的所有 comp —— 任何一个第三方 comp 抛异常，"
                + "都会中断整条 postfix 链，排在它后面的死亡处理"
                + "（掉装备、尸体生成、记录与关系更新）就全部跳过，一次死亡会停在半截。"
                + "本项给那层 postfix 加一层 Finalizer：捕获、留日志、吞掉。"
                + "★ 不改 ExtraWeapon2 的任何逻辑，只保证它的异常不外溢；"
                + "前 3 次打完整堆栈，之后只计数，不会刷屏。"),

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

            // ---------------- 性能剖析（默认关闭） ----------------
            //
            // 这一项**单独成段，而且默认是关的**（用户 2026-11-01 明确要求）。
            //
            // 为什么它跟「日志刷屏」那组不一样：那八项修的都是「模组写错了、日志被刷爆」，
            // 开着只有好处；而这一项关掉的是**游戏自带的功能** —— 关掉之后，
            // 开发者模式里的性能剖析就用不了了。它值不值得开，取决于玩家自己要不要那个功能，
            // 所以默认站在「不动原版功能」那一边，想省日志/内存的人自己去打开。
            //
            // 默认值由 FixToggles.IsDefaultOff() 统一给出（设置类与 IsFixEnabled 都读它），
            // 不要在各处各写一份，否则「界面上显示关、实际却装了」这类错会很难查。
            new Entry("deepProfilerQuiet", GroupAdvanced,
                "DeepProfiler 拖日志与内存（【40】，默认关闭）",
                "Verse.DeepProfiler 的开关条件是两个：`enabled && Prefs.LogVerbose`。"
                + "enabled 默认就是 true，所以真正决定它跑不跑的是玩家自己的「详细日志」——"
                + "只要那个开着，它每次 LongEvent 都打印整棵调用树 + Hotspot analysis"
                + "（实测一局 14.5 小时：7,105 次调用树、8,001 次 hotspot，20:56 那 8 秒写了 54,094 行），"
                + "而它内部维护的调用列表会持续累积对象（11.5h 9.2 GB → 14.5h 15.7 GB）。"
                + "★ 本项**默认关闭**：只把 DeepProfiler.enabled 置 false，"
                + "logVerbose 保留、其它日志一条不少，代价是游戏内的性能剖析功能失效。"
                + "你不看那个剖析、又嫌日志大或内存涨，再来把它打开。"),

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
                + "【35】三条指向不存在 def 的交叉引用。",
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
                "该模板没有 xenotypeSet，生成出来会变成普通智人（没有鼠耳尾巴、脸还发黑）。"
                + "本项补一个固定的 Ratkin_OA —— 所以除了金鸢尾兰本体，还需要 "
                + "「[OA]Ratkin Faction: Oberonia aurea」这个框架模组在场（Ratkin_OA 由它提供）。"
                + "两者缺一，本项什么都不会做（补丁外层就是用那个模组名做守卫的）。",
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

            // 2026-10-29 补登记：下面两项原先与「HediffStage.visible」那条一起挤在
            // RJW17_XmlFieldCleanup.xml 一个文件里。而一个 Entry 只能挂一个文件名
            // （Entry.XmlPatchFile 是单个字符串），于是两项都进不了设置界面、也进不了统计。
            // 已把那个文件拆成两个（各留原注释），这里各建一个开关。
            new Entry("xmlRjw17SraeLibField", GroupXmlPatches,
                "1.7 下 SRAELib 字段缺失刷红字（【36】）",
                "雪兔服装扩展（工坊 3668025052）的 1.6\\Assemblies 里躺着两个程序集名都是 "
                + "SRAELib 1.0.0.0 的 dll —— SRAELib.dll（19,456 字节，有 targetNullifyingHediffs）"
                + "与 SRAELib_old.dll（18,944 字节，没有），游戏加载到旧的那个，"
                + "于是 ThoughtDef_HediffAttraction 报「doesn't correspond to any field」。"
                + "本项在 1.7+ 删掉那个字段；1.6 下**不执行**（守卫用 Verse.MaterialDef 判版本）—— "
                + "因为 1.6 的 SRAELib 里该字段有效，无条件删会把「认知护盾免疫」一起删掉。",
                xmlPatchFile: "RJW17_SraeLibFieldCleanup.xml"),

            new Entry("xmlRjw17HediffVisible", GroupXmlPatches,
                "HediffStage.visible 字段不存在（【37】）",
                "某些模组给 HediffStage 写了 visible 字段，而 1.6 与 1.7 的 Verse.HediffStage 都没有这个字段"
                + "（只有 becomeVisible）→ 每次加载刷「doesn't correspond to any field in type HediffStage」。"
                + "删掉不改变任何行为，只消日志。两个版本都成立，无需版本守卫。",
                xmlPatchFile: "RJW17_HediffStageVisibleRemoval.xml"),

            // 2026-11-07 新增（来自全局日志排错，完整报告见
            //   docs\RimWorld-日志排错报告-20261107.md）
            //
            // ⚠ 这一项**必须**排在 dord.nuditymattersmore 之后执行 ——
            //   它删的是那个模组自己 XML patch 加进去的东西（补丁打补丁，硬约束）。
            //   目前已写进 About.xml 的 loadAfter；改顺序会让它变成空转。
            new Entry("xmlNmmStaleCompCleanup", GroupXmlPatches,
                "裸体评价 1.7 重构后的过期 comp（【44】）",
                "「[OWL] NMM Combined Edition」的 1.7 patch 仍在给 BasePawn/comps 追加 10 个旧架构的"
                + " CompProperties_*（BugsTracker / CoverBody / BraStatus / ExhibScore / NipSlip /"
                + " PrudeScore / VoyScore / WetShirt / NakedTime / Fixation），"
                + "而 1.7 的 dll 里那 10 个类型一个都不存在 —— 作者已重构成单一 CompProperties_NMM。"
                + "BasePawn 是 Human、全部动物、全部机械族的公共基类，而 comps 是整表解析："
                + "任意一项解析失败就整只 def 被丢弃，不是跳过那一项。"
                + "实测代价：344 个 ThingDef 消失（含 Human 本人）、8,304 条 cross-reference 报错、"
                + "连带 [DefOf] 绑定中断、主菜单每秒 1 次 NRE 持续 35 分钟。"
                + "本项只删掉那 10 个死引用，绝不补任何类型 —— "
                + "2026-11-09 就是因为补了一个「只有部分版本才有的」CompProperties_NMM，"
                + "把用旧版 NMM 的玩家整片炸掉（工坊评论区成片报找不到该类型）。"
                + "只有 success=Always：作者日后自己修好了会安静空转。"
                + "★ 不改那个模组的任何原始文件 —— 一切都在本补丁里做。",
                xmlPatchFile: "NMM_StaleCompCleanup.xml"),

            // ── 2026-11-10 第二轮新增（【46】【47】【48】）──
            //
            // 这三条原本是「直接改第三方模组文件」做的（见交接报告 2.1/2.2/3.4.2），
            // 本轮把它们**全部还原**并改成补丁。理由只有一条，而且是铁律：
            //   绝不去改别人模组目录里的东西，一切改动都写进本补丁包。
            // 直改 `Mods\` 下第三方 XML 的三个后果：玩家重装/模组管理器更新会静默覆盖、
            // GitHub 上没有记录、出问题时没有可追溯的 diff。
            new Entry("xmlHotwifeInverse", GroupXmlPatches,
                "淫妻与公牛：10 个关系 def 写了不存在的字段（【46】）",
                "「淫妻与公牛」（HotwifeRJW）的 10 个 PawnRelationDef 都写了一行 "
                + "<inverse>…</inverse>，而 RimWorld 的 PawnRelationDef **从来没有这个字段** —— "
                + "1.6 与 1.7 两份程序集都是 21 个字段、都没有它，原版数据 0 处引用。"
                + "所以这是作者写错，1.6 上一样报 10 条「doesn't correspond to any field」。"
                + "关系反向逻辑走 PawnRelationWorker、不读 def 上的字段，删掉这 10 个节点"
                + "不改变任何游戏行为，只是不再刷红字。",
                xmlPatchFile: "Hotwife_InverseFieldCleanup.xml"),

            new Entry("xmlHotwifeJobDriver", GroupXmlPatches,
                "淫妻与公牛：一个 JobDef 指向不存在的类（【47】）",
                "该模组的 FWQ_FamilyCheckIn 把 driverClass 写成了 "
                + "FamilyWitnessQuirks.JobDriver_FamilyCheckIn，而它的 dll 里根本没有这个命名空间 —— "
                + "真实存在的 JobDriver 只有 HotwifeRJW.Relationships.JobDriver_RelationshipConversation。"
                + "本项把它改指向那个真实类：def 保留、存档安全、日志干净。"
                + "（不删 def 是因为存档里若有小人在跑这个 job，删了会让 Scribe 找不到定义。）",
                xmlPatchFile: "Hotwife_JobDriverFix.xml"),

            new Entry("xmlRjwSkillSexOrder", GroupXmlPatches,
                "RJW 的 Sex 技能缺排序扩展（【48】）",
                "「角色编辑器 - 技能排序修复」（Character Editor - Skill Order Fix）要求每个 SkillDef "
                + "都带 SkillOrderExtension，否则那条技能**根本不进它的排序表**、编辑器的技能列表会错位。"
                + "RJW 的 Sex 技能没有这个扩展，于是每次启动刷一条 "
                + "「Skill Sex missing SkillOrderExtension」。本项按它自己的格式补一条 order=5 —— "
                + "原版技能是 120 到 10 每档 10，取 5 表示排在所有原版技能之后。"
                + "★ 三层守卫：目标修复模组不在场 / RJW 没装 / 扩展已存在，都不动作。",
                xmlPatchFile: "RJW_SkillSexOrderFix.xml"),

            new Entry("xmlCumworksBadCompRef", GroupCompat,
                "无限精液工坊：两个储液桶写了不存在的组件（【49】）",
                "它的两个储液桶（CumworksNet_CumBucket / _Advanced_）各写了一行 "
                + "Class=\"RJW_Menstruation_Fluids.CompProperties_CumGatherer\"，"
                + "而那个类型在 ELT 月经体液模组的三个 dll 里都不存在 —— "
                + "MayRequire 只查模组是否启用、不查类型是否存在，所以这两行一路走到解析阶段才炸。"
                + "comps 是整表解析，一失效连宿主 ThingDef 一起丢，也就是说这两个桶本体里根本不存在。"
                + "本项把这两行删掉，宿主与其它组件都不动。"
                + "★ 按类型存在性让路：作者哪天把该类型加回来，本项自动不动作。",
                xmlPatchFile: "CumworksBadCompRefFix.xml"),

            new Entry("xmlMiliraApparelPaths", GroupXmlPatches,
                "米莉拉服装贴图表里有重复条目（【52】）",
                "米莉拉的「穿这件衣服就换成自家贴图」表里，BodyStrap 与 TribalA 这两条各出现了两次。"
                + "游戏往字典里塞第二条时会抛重复键异常，被 catch 吞掉 —— 表面上只是两条红字，"
                + "实际后果是**这张表的内容与作者预期不一致，该种族整套服装贴图替换会退回人类默认贴图**。"
                + "本项只在「真的重复了」的时候动手：先删掉该键的全部条目，再加回恰好一条。"
                + "★ 正常只有一条时 xpath 不命中，整段不执行，一个字节都不改 —— 所以上游修好后本项自动让路。"
                + "★ 本模组必须排在外星人SA纹理适配（adk.sar.alien）之后才有意义，About 里已写 loadAfter。",
                xmlPatchFile: "MiliraApparelPathsDedupe.xml"),

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
            // RimJobWorld 本体；它挂的 Postfix 就在 rim.job.world 里
            { "rjwPawnGenPostfixNull", "rim.job.world" },
            // 边缘仙路本体（工坊 3296476341）；被守卫的 Postfix 在这个模组的 MOD.dll 里
            { "xiuxianCopyBookGuard", "RI.RimImmortal.Core" },
            // 米莉拉种族；被去重的 individualPaths 在它的 alienRace 定义里
            { "xmlMiliraApparelPaths", "Ancot.MiliraRace" },
            { "alliesAreHelpfulCurJobNull", "ninagoblin.alliesarehelpful" },
            // ⚠ 这一项修的是 SRCELib.dll 的并发缺陷，而 SRCELib.dll 由
            //   「Snow Rabbit Commodities Expansion——Playbunny Corporation」
            //   （工坊 3748316169，packageId SnowRabbit.CommodityExpansionPB）提供，
            //   不是雪兔种族本体。全库实测只有它一家带这个 dll。
            //   早先这里误填了 Gugustar.SnowRabbitRaceBeta，会让界面误报「目标模组未启用」。
            { "srceLibConcurrency", "SnowRabbit.CommodityExpansionPB" },
            { "gfmIdleCombatDeepCopy", "arandomkiwi.guardsforme" },
            { "ccoeReflection", "keahuy.rjw.ccoe" },
            { "fixVfeeInstrumentSpace", "OskarPotocki.VFE.Empire" },
            { "vehicleFrameworkDebug", "SmashPhil.VehicleFramework" },            // Ancient amorphous threat：写错字段的那条补丁由它提供            // Just A Flesh Wound：被守卫的那个 Postfix 在它的 dll 里
            { "jafwLethalDamageGenGuard", "kittahkhan.justafleshwound" },
            // 【64】改的是 LDR（边缘弹道导弹:【地球科技】）自己写错的小数
            { "xmlSiloTubeIntField", "nekotee.irbmldr" },

            { "xmlAncientThreatIntField", "XMB.AncientThreat.MO" },
            // 【65】改的是 SnowRabbit 的贴图加载时机
            { "snowRabbitGraphicThread", "Gugustar.SnowRabbitRaceBeta" },

            // ---- 2026-11-12 第二轮全局日志诊断新增 ----
            // 贞操科技：被补建的那个 GameComponent 在这个模组的 dll 里
            { "twilightGameComponentRebuild", "LT.TwilightChastityTechnology" },
            // Real Faction Guest：背景校验器在这个模组的 dll 里
            { "rqpFactionLeaderGuard", "rtxyd.RealFactionGuest" },
            // 涅瓦莲附加包：写错 texPath 的那个 def 由它提供
            { "xmlNivarianStarfallBeaconTexPath", "LeatherRingFox.RimSMFramework.NivarianRaceAddon" },
            // VOE 附加前哨：餐厅前哨的产出列表由它提供
            { "xmlVoeSushiMigration", "MrHydralisk.VOEAdditionalOutposts" },
            // ⚠ 2026-11-05 复审订正：这一项原来填的是 readyforjeff.rjwpeculiarinstitution
            //   （Peculiar Institution / 小妾项圈）—— **填错了模组**。
            //   本项修的 `rjwpe.SoundReplacementDef` 来自「RimJobWorld Pedophilia Extension」
            //   （packageId rim.job.world.pe，本机目录 Mods\RJW_功能_重金属冶炼核心），
            //   与 Peculiar Institution 毫无关系。填错的后果是：PE 装着、Peculiar 没装时，
            //   界面会一直标暗黄「目标模组未启用」，而补丁其实正常在干活 —— 纯误报。
            //   判据与 Patch_LoadModXML_FilterRjwpeStaleDefs.cs 里的 PePackageId 保持一致。
            { "filterRjwpeStaleDefs", "rim.job.world.pe" },
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

            // ── 2026-10-29 补登记：10-28 加的三项修复此前没登记前置，
            //    所以「目标模组未启用」这个状态对它们永远不会出现。
            //    【37】不登记：它删的是「任何模组写错的 HediffStage.visible」，
            //    没有单一目标模组，硬填一个会误报。
            { "xmlRjw17SraeLibField", "HahaSnowRabbitPB.ApparelPatch" },

            // ── 2026-11-09 补登记：用户报「【44】明明没装 NMM，界面却不标『目标模组未启用』」。
            //    原因是这张表是手工登记的，新加修复项如果忘了登记，
            //    界面根本不知道该去查哪个 packageId，那个状态就**永远不会出现**。
            //    （2026-10-29 已经因为同一原因补过三项，这是第二次。）
            //    以下 packageId 全部从 RimCrow 的 modasset 表查得，不凭印象 ——
            //    填错会造成反向误报（目标装着却一直标暗黄），比不登记更糟。
            { "xmlNmmStaleCompCleanup", "dord.nuditymattersmore" },   // 【44】NMM Combined Edition
            // ── 2026-11-10 补登记：【41】fixPawnScribeNull 修的正是 NMM 的 Fixation 组件
            //    （PostExposeData 里无条件 Scribe_References.Look(ref fixPawn, "fixPawn")），
            //    目标模组只有 dord.nuditymattersmore 一个。此前漏登记，界面就永远显示不出
            //    「目标模组未启用」这一档 —— 玩家装了别的模组时看不出本项在空转。
            { "fixPawnScribeNull", "dord.nuditymattersmore" },
            // 【2026-11-13 撤销】原先这里登记了 { "ew2TranspilerProbe", "mrk.extraweapon2" }，
            // 那是**错的**：`Ew2HarmonyId` 在 Ew2CompatProbe.Collect 里只用来
            // 「数出外来 transpiler 的个数」（owner != "EW2_Patch" 就 foreignTranspilers++），
            // **不是用来筛选要扫哪些方法**。它扫的是固定的那几个目标方法（EW2 本来会改的），
            // 所以 **EW2 没装时它照样能干活** —— 那些方法上依然可能有别的模组在 patch，
            // 正是要查的东西。登记 requiresModById 会让界面上挂一句
            // 「目标模组未启用」，玩家打开后以为白开了。
            { "ew2DeathPathRepair", "mrk.extraweapon2" },             // 【43】同上
            { "xmlExtensionsFindMod", "imranfish.xmlextensions" },    // 工坊 2574315206
            { "realtimeDrawNreSwallow", "zuoyao.ravenrace" },              // 【38】Raven Race - Embers in Shadow
            { "riFurnitureRoyalCompatQuiet", "RI.RimImmortal.Furniture" }, // 工坊 3478098371（源码注释里写着）

            // ── 2026-11-10 第二轮补登记（【46】【47】）──
            //    【48】不登记：它的真正目标是「CharacterEditorSkillFix + RJW 两个模组」，
            //    单填一个会误报（填 SkillOrderFix 则 RJW 没装时也显示正常）。
            { "xmlHotwifeInverse", "betaforher.hotwiferjw" },
            { "xmlHotwifeJobDriver", "betaforher.hotwiferjw" },
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
