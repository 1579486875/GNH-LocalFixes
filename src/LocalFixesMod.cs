// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这是整个修复补丁包的「入口」类。游戏会自动发现它，并在启动早期调用它的构造函数。
    // ==========================================================================
    //
    // 【为什么要放在这么早】
    //
    // 游戏通过 LoadedModManager.CreateModClasses() 来创建各个模组的对象。
    // 这个时机比「解析 Def 数据」更早，更比所有 [StaticConstructorOnStartup]
    // 类型的初始化早得多。
    //
    // 正是这个「早」的窗口，让我们来得及给一个本来会崩溃的静态构造函数打补丁，
    // 详见 VFEEInstrumentSpaceFix.cs。
    //
    // 【为什么要防重复】
    //
    // 先说清楚实际机制（已用反编译核对过，以免后来人照着错的印象改代码）：
    // 游戏在一局里确实会多次调用 LoadedModManager.LoadAllActiveMods —— 触发它的是
    // 开发者工具的热重载（PlayDataLoader.HotReloadDefs）和切换语言
    //（LanguageDatabase.SelectLanguage → PlayDataLoader.LoadAllPlayData），
    // **不含**打开游戏内的「模组」页面（反编译查证：LoadAllActiveMods 的调用点只有
    // PlayDataLoader.DoPlayLoad，以及 HotReloadDefs 里那个委托）。
    // 但其中的 CreateModClasses() 有一句 if (!runningModClasses.ContainsKey(type)) 去重 ——
    // runningModClasses 是个静态字段（在 LoadedModManager 的静态构造里创建）、
    // 条目由 CreateModClasses 逐类型写入，之后从不清空。
    // 所以正常路径下，同一个模组类的构造函数在一个进程里**只跑一次**。
    //
    // 那为什么还要立防重复的标记？它挡的是：
    //   首次构造时抛异常，运行库的去重表里没能写下这一项（见
    //   LoadedModManager.CreateModClasses 里的 runningModClasses），于是下次还会被构造一次。
    //
    // 要说明白的是：**程序集被重新加载这种情况它挡不住** —— 那样一来所有静态状态
    // 都是全新的，标记必然从空开始。真要防那一条得靠卸载旧补丁，不是靠这个标记。
    //
    // 而 Harmony 装补丁的方式是「叠加」而不是「替换」：
    // 同一个方法上装十次，就会有十层代码依次执行，越玩越卡直到卡死。
    // 所以每一个补丁类都必须立起自己的标记 —— 代价只有一次集合查找。
    //
    // ⚠ 「什么时候立标记」有两种做法，都成立，但别把它们搞混（2026-10-07 复核后统一口径）：
    //
    //   · **本文件这套（多数派）**：**装成功之后**才置位。
    //     好处是失败留待下次重试、能自愈；代价是「已成功」这件事必须当场记下来。
    //     本文件由 installedPatchClasses 这个 HashSet 负责，而 InstallPatchClass 里
    //     是「先 Add 再打日志」——顺序不能反（见那里的注释）。
    //
    //   · **各补丁类自己的 Install() 里的静态 bool（少数派）**：**在尝试之前**就置位，
    //     即「本次进程只试一次」。代表是 MusicManagerFadeoutFix 与 XmlExtensionsFindModFix。
    //     它们失败的唯一现实原因是「游戏改版把这个方法改名或删掉了」，
    //     那种情况下在同一个进程里重试一百次也还是找不到，只会把同一条 Error 多刷几遍。
    //
    //   两种做法的底线是同一条：**绝不允许对同一个方法 Patch 两次**。
    //   而只要「已经 Patch 成功」这一事实没有当场落纸，就有可能在日志抛异常时丢掉它
    //   —— 2026-10-07 复核就在 SteamWorkshopHookFix 里查出过这么一处，已修。
    //
    // 【2026-10-04 的重要变更：不再用 PatchAll】
    //
    // 这里原本是一句 HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly())，
    // 现已改成逐类安装，理由写在下文 InstallAllPatchClasses 的注释里。
    // 一句话：PatchAll 遇到任何一个补丁类出错就整体中止；更麻烦的是它内部那句
    // GetTypes() 一旦因为「某个模组没装、程序集里有类型解析不了」而失败，
    // 整个补丁包会一个都装不上 —— 那正是「多模组兼容修复补丁反被别的模组拖死」的情形。
    public class LocalFixesMod : Mod
    {
        public const string HarmonyId = "gnh.cn.cys.localfixes";

        internal static readonly Harmony HarmonyInstance = new Harmony(HarmonyId);

        // 模组设置对象（目前里面只有一个开关：「保护自定义的领袖头衔」）。
        //
        // 由构造函数里的 GetSettings<GNHLocalFixesSettings>() 填上。
        // 万一那一步失败，这里就是 null —— 所有用到它的地方都按**内置默认值**行事，
        // 不会因为「设置读不出来」而把功能一起弄丢（见 Patch_IdeoFoundation_KeepLeaderTitle）。
        internal static GNHLocalFixesSettings Settings;

        // 已经成功安装过的「补丁类」。
        //
        // 用「逐个类记录」而不是「一个全局 bool」，是为了让失败的类能单独重试，
        // 同时绝不重复安装已经装好的那些（Harmony 补丁叠加会造成越玩越卡）。
        private static readonly HashSet<Type> installedPatchClasses = new HashSet<Type>();

        // 设置界面那张长列表的滚动位置。做成静态字段：关掉窗口再打开还停在原处
        // （二十多项平铺下来要滚好几屏，每次重开都跳回顶部会很难用）。
        private static Vector2 settingsScroll;

        // ======================================================================
        // 安全日志：日志系统自己也会坏，坏的时候不许拖累补丁安装
        // ======================================================================
        //
        // 【为什么不能直接写 Log.Error(...)】
        //
        // 本文件里所有 Log 调用都落在「装补丁」的关键路径上。而 Log 本身是会抛异常的：
        //   · 日志文件被别的程序占着、磁盘写满 —— 写盘那一层会抛；
        //   · 另一个模组给 Verse.Log 打了有问题的补丁 —— 调用它就会抛；
        //   · 罕见的运行环境问题（实测：在非 Unity 宿主里 Log 会抛
        //     SecurityException: ECall methods must be packaged into a system module）。
        //
        // 一旦它抛出去，后果被放大得非常严重（这是 2026-10-07 的极端场景测试实测出来的）：
        //    Log.Error 抛 → InstallPatchClass / TryInstall 的 catch 块跟着抛
        //    → 异常一路穿到本类的构造函数
        //    → 构造函数抛异常，游戏那张「已经构造过哪些模组类」的去重表就留不下本类型
        //    → 下次（切语言 / 开发者热重载）会再构造一遍
        //    → Harmony 补丁是叠加的 → 越玩越卡直到卡死。
        //
        // 也就是说：**一个写日志失败，能把一个本来已经装好的补丁包变成性能炸弹。**
        // 所以这里统一走下面三个壳子：日志成功就正常打印，失败就静静地算了 ——
        // 「没打出日志」是可以接受的，「因为打不出日志而崩掉」不可以。
        //
        // ⚠ 2026-10-08 由 private 改成 internal：**别的文件也要用它们**。
        //   起因是 AssemblyGetTypesFallbackFix 的收尾器里有裸的 Log.Warning，
        //   而收尾器跑在「别人的方法」里 —— 它一抛，被修的模组反而挂得更惨
        //   （详见那个文件里 ReportOnce 上面的注释，含实测过程）。
        //   谁在「不在自己 try/catch 保护内」的位置打日志，就必须用这三个壳子。
        internal static void SafeLogMessage(string message)
        {
            try { Log.Message(message); }
            catch (Exception) { /* 日志失败只能算了，绝不能因此影响补丁安装 */ }
        }

        internal static void SafeLogWarning(string message)
        {
            try { Log.Warning(message); }
            catch (Exception)
            {
                // 必须吞：这里唯一现实的失败原因就是「日志子系统自己坏了」
                //（磁盘写满 / 日志文件被占用 / 被别的模组打坏了 Log）。
                // 本方法跑在补丁安装的关键路径上，让异常从这里漏出去 =
                // 把一次日志故障升级成「模组构造函数抛异常 → 下次重入 → 补丁叠层」。
                // 没打出这条日志可以接受，因此崩掉不可以。
            }
        }

        internal static void SafeLogError(string message)
        {
            try { Log.Error(message); }
            catch (Exception)
            {
                // 必须吞，理由同 SafeLogWarning：安装路径上的日志失败只能放弃这条日志。
                // 特别注意调用方那边：catch 块里之所以必须用 SafeLogError 而不是裸的
                // Log.Error，就是为了不让异常从这里二次抛出、穿到模组构造函数里去。
            }
        }

        public LocalFixesMod(ModContentPack content) : base(content)
        {
            // 先把模组设置读进来（也就是那个开关的当前状态），再装补丁。
            //
            // 这一步同样**不能抛异常**：本类的构造函数一旦抛出，游戏那张
            // 「已经构造过哪些模组类」的去重表里就留不下本类型（机制见下方长注释），
            // 下次热重载/切语言时会被重新构造一遍。所以这里单独兜一层。
            try
            {
                Settings = GetSettings<GNHLocalFixesSettings>();
            }
            catch (Exception ex)
            {
                SafeLogError("[GNH LocalFixes] Failed to load mod settings; the leader-title protection "
                    + "falls back to its built-in default (ON). Other fixes are unaffected: " + ex);
            }

            // 记下本模组的根目录：XmlPatchGate 判定「这条 XML 补丁是不是我们的」时，
            // 除了比文件名，还要比路径前缀 —— 只比文件名的话，别的模组只要也有个同名
            // 补丁文件（例如某个作者也放一份 ConfigErrorCleanup.xml），我们的开关就会
            // 去拦别人的补丁。本机 6543 个 patch 文件实测零碰撞，但那是运气，不是保证。
            try
            {
                Patch_PatchOperation_Apply.OwnRootDir = content.RootDir;
            }
            catch (Exception ex)
            {
                SafeLogWarning("[GNH LocalFixes] 取模组根目录失败；XML 开关退化为只按文件名匹配：" + ex);
            }

            // 再兜一层：把「逐类安装」这一步包起来。
            //
            // ⚠ 构造函数里其实有【两条】互相独立的安装路径，读注释时别把它们混成一条：
            //   (a) 下面这个 try 块里的 InstallAllPatchClasses() —— 只管带 [HarmonyPatch]
            //       特性的那 10 个补丁类（8 个功能类 + Apply / Complete 两个机制类），内部由 InstallPatchClass 逐类 try/catch；
            //   (b) 本 try 块【之后】平铺的那些 TryInstall(...) —— 它们不在这个 try 里，
            //       也不经过 InstallAllPatchClasses，而是各自由 TryInstall 逐项 try/catch。
            // 两条路径的每一步都已经自兜了，打日志也都走 SafeLog*（自己不会抛）。
            // 那这里为什么还要再包一层？因为**本构造函数绝不能抛**（原因见上面那段），
            // 而「绝不能抛」这种要求不能建立在「下游每一处都做对了」的假设上 ——
            // 将来有人在 InstallAllPatchClasses 里加了一行会抛的代码，
            // 有这一层就只是少装几个补丁，没这一层就是整包补丁叠层。
            try
            {
                InstallAllPatchClasses();
            }
            catch (Exception ex)
            {
                SafeLogError("[GNH LocalFixes] InstallAllPatchClasses threw; the patch pack is partially "
                    + "installed this session, but the mod class constructor stays intact: " + ex);
            }

            // 下面这些补丁各自隔离：任何一步失败都不许把整个构造函数掀翻。
            //
            // 为什么这么要紧（2026-10-02 事故的机制）：
            //   LoadedModManager.CreateModClasses() 里写的是
            //       runningModClasses[type] = (Mod)Activator.CreateInstance(type, modContentPack);
            //   —— **先构造、后登记**。构造函数一旦抛异常，去重表里就留不下这个类型，
            //   于是下次 CreateModClasses() 会再构造一遍（触发它的是**开发者工具热重载**与
            //   **切换语言**；**不含**打开游戏内的「模组」页面 —— 见本文件开头那段反编译核对），
            //   而 Harmony 补丁是叠加的 → 越玩越卡直到卡死。
            //   宁可某一步没装上，也不能让它带塌整个构造函数。
            //
            // 同时 catch 里**必须**打 Log.Error：那场事故的另一半教训是
            //   「异常被静默吞掉、补丁其实从没生效，却没人知道」。
            //   日志是排查者唯一能看出「这一步没装上」的线索。
            // 2026-10-08 新增：给 Assembly.GetTypes() 挂一层安全网。
            //
            // 现象：每次启动日志里都有
            //     Exception in post-load event 'Apply final patches':
            //     System.Reflection.ReflectionTypeLoadException
            //     Could not resolve type ... 'AchievementsExpanded.TrackerBase'
            //       at AM.Patches.Patch_Verb_MeleeAttack_ApplyMeleeDamageToTarget.PatchAll()
            // 近战动画（Melee Animation）会遍历所有已加载的 dll、逐个调 GetTypes()
            // 挑出「继承了 Verb_MeleeAttack」的类来打补丁，而那句 GetTypes()
            // **不在它的 try 块里** —— 一抛异常，整个循环当场中断，
            // 排在后面的 dll 全都不再扫。所以漏掉的不是「某一个补丁」而是一批。
            //
            // 根因：Geneva Checklist（工坊 3339044171）把它的成就联动组件
            // GenevaChecklistAchievements.dll 放进了**无条件加载**的 1.6\Assemblies\，
            // 而该组件引用的 AchievementsExpanded 程序集并没有装（它自己的
            // loadfolders.xml 里那一项是带 IfModActive 条件的，而条件目录里又没放 dll）。
            //
            // 修法：给 GetTypes() 挂收尾器 —— 正常时什么都不做，
            // 只在「类型加载失败」时把确认不了的类剔掉、把能用的交出去，
            // 于是「一整份清单作废」变成「少几个类但清单还能用」。
            // 完整推导与安全性论证见 AssemblyGetTypesFallbackFix.cs 的文件头注释。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("assemblyGetTypesFallback", "AssemblyGetTypesFallbackFix", AssemblyGetTypesFallbackFix.Install);

            TryInstall("fixVfeeInstrumentSpace", "VFEEInstrumentSpaceFix", VFEEInstrumentSpaceFix.Install);
            TryInstall("steamWorkshopHook", "SteamWorkshopHookFix", SteamWorkshopHookFix.Install);
            TryInstall("crossPromotionUnpatch", "CrossPromotionUnpatch", CrossPromotionUnpatch.Install);

            // ⚠ RimUnityPostFxUnpatch 曾经也在这里装（2026-10-16 加，2026-10-17 由用户指示移除）。
            //
            // 移除原因：那场崩溃（crash-20261016-132826.txt，开在主菜单约 21 分钟后被渲染线程
            // 的 MissingMethodException 终结）属于**本机引擎快照落后** —— D:\RimWorld-1.7 是
            // 2026-10-13 的构建，而 RimUnity 0.1.7 按**更新的** Waylith 引擎编译，CLR 因而找不到
            // PostEffectPass 的新重载。这不是模组间的兼容问题，引擎快照更新后自愈，
            // 不需要在兼容补丁里长期兜着。详见 FixToggles.cs 同类注记与移除前的源码备份。
            //
            // ⚠ 「不要停用 RimUnity」这条警告仍然有效：ZuoYao_RavenRace 在编译时引用了 RimUnity
            //    的类型，停用会让它整份程序集不可用，进而使大量 Def 解析失败（实测 3 分钟 5.7 万条
            //    ERROR）。快照更新前若又崩，先升级快照 —— 别去停用 RimUnity。
            //
            // ⚠ 保留：steamWorkshopHook / crossPromotionUnpatch 这对「打开模组页面即崩」的两道保险，
            //    它们修的是 Steam 官方废弃接口（RequestUGCDetails），与本地版本无关，2026-10-17 确认保留。

            // 下面这个是 2026-10-04 根据游戏日志新增的修复，详见它的文件头注释。
            // 安装方式是「按类型名去找，找不到就置位跳过」。
            TryInstall("techHediffsZeroBudget", "TechHediffsZeroBudgetFix", TechHediffsZeroBudgetFix.Install);

            // 2026-10-17 新增：RI-Furniture 的皇权房间要求自检刷屏静音。
            //
            // 那 54 条「皇权房间要求补丁未生效」的成因是 disroom.mashiro 把爵位的
            // throneRoomRequirements / bedroomRequirements 整片清空 —— 而那是玩家**故意**启用的
            // 效果（模组名就叫 disabledroomRequirements）。RI 想往空列表里加家具，必然加不进去。
            // 本项只静音它这一路的自检结论，其余日志照常。完整推导见 RIFurnitureRoyalCompatQuietFix.cs。
            TryInstall("riFurnitureRoyalCompatQuiet", "RIFurnitureRoyalCompatQuietFix",
                RIFurnitureRoyalCompatQuietFix.Install);

            // ── 2026-10-31 新增三项（1.7 移交清单的 A 类）──
            //    40：模组作者留在正式版里的调试输出（WeaponStats 炮塔名 / RimImmortal 的 ::Start）
            //    41：DeepProfiler 跟着 logVerbose 一起跑，打印整棵调用树并累积对象
            //    42：读档时 /fixPawn 的空引用登记
            TryInstall("logSpamFilter", "LogSpamFilterFix", LogSpamFilterFix.Install);
            TryInstall("deepProfilerQuiet", "DeepProfilerQuietFix", DeepProfilerQuietFix.Install);
            TryInstall("fixPawnScribeNull", "FixPawnScribeNullFix", FixPawnScribeNullFix.Install);

            // ⚠ CcoeReflectionFix 曾经也在这里装，2026-10-06 移走了。
            //
            // 原因：Harmony 打补丁这个动作会触发**目标程序集里其它类型**的静态构造，
            // 而 CCOE 那三个目标方法引用的 RJW_Menstruation.VariousDefOf 是个 [DefOf] 类，
            // 它的静态构造函数要查 Def 数据库 —— 本构造函数跑在「模组加载」阶段，
            // Def 还没建好，于是抛 NullReferenceException（实测 2026-10-06 14:22 的
            // TypeInitializationException）。现在它改由 [StaticConstructorOnStartup] 安装，
            // 完整推导见 CcoeReflectionFix.cs 末尾 CcoeReflectionFixLateInstaller 的注释。

            // 2026-10-04 新增：拦下游戏每帧刷的 InvalidCastException。
            // 那个崩溃来自 MusicManagerPlay.UpdateMusicFadeout —— 它去「乐器」分组里取东西、
            // 强行当成乐器建筑用，结果分组里混进了真实类型不是乐器的对象（def 与实际类型对不上）。
            // 详见 MusicManagerFadeoutFix.cs 的文件头注释（含原版源码与 IL 证据）。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("musicManagerFadeout", "MusicManagerFadeoutFix", MusicManagerFadeoutFix.Install);

            // 2026-10-04 新增：修 XmlExtensions 自己的 <FindMod> 判定。
            //
            // 这是和 FindModLanguageFix **不同的另一条代码路径**：
            //   · 原版 Verse.PatchOperationFindMod → ModLister.HasActiveModWithName → FindModLanguageFix 管
            //   · XmlExtensions 的 FindMod          → 直接遍历 LoadedModManager.RunningMods → 本步管
            // v1.3.12 只修了前者，所以日志里「Patch operation FindMod(Royalty) failed」
            // 那 4 条依旧存在（一次启动 4 个模组的整块补丁失败）。
            //
            // 根因：官方 DLC 的显示名会被语言包翻译（Royalty → 皇权），
            // 而模组作者写的是英文短名，字符串比对必然失败。
            // 详见 XmlExtensionsFindModFix.cs 的文件头注释（含反编译证据与安全设计）。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("xmlExtensionsFindMod", "XmlExtensionsFindModFix", XmlExtensionsFindModFix.Install);

            // 2026-10-28 新增：消掉 1.7 下「一进地图就每帧刷」的
            //     ERROR [Verse] System.MissingMethodException: Method not found:
            //         'Unity.Mathematics.float2 Unity.Mathematics.math.normalize(Unity.Mathematics.float2)'
            // 实测（2026-10-28 15:43 进图后 6 分钟）：12,671 行，日志约 4 MB/分钟。
            //
            // 来源是 RavenRace（渡鸦种族，ZuoYao.RavenRaceNSFW）的「蝗虫雨」天气组件：
            // 它在 MapComponentUpdate 里每帧调 Unity.Mathematics.math.normalize(float2)，
            // 而 1.7 换引擎后 UnityEngine.* 全部被重定向到 UnityStubs
            // （RimWorld.runtimeconfig.json 的 AssemblyNameRedirects），
            // UnityStubs 的 Unity.Mathematics.math 里**没有 normalize 这个重载**。
            //
            // ⚠ 不能直接给那个方法打 Prefix —— 第一版那么写，补丁自己装不上：
            //   Harmony 的 MethodBodyReader 读目标方法 IL 时会因为那个缺失的方法引用
            //   当场抛 MissingMethodException（已实测复现）。
            //   所以改成 patch 原版的遍历器 Verse.MapComponentUtility.MapComponentUpdate，
            //   在它的 Prefix 里把该组件从 map.components 摘掉。
            //   那个遍历器本来就是每个组件独立 try/catch，摘掉一个不影响其它组件。
            //
            // 该天气效果依赖的 Jobs/Burst 粒子在换引擎后本就跑不起来，摘掉功能损失为零。
            // 详见 RavenLocustWeatherQuietFix.cs 的文件头注释（含两次实测的证据）。
            // 目标类型来自第三方 dll，所以本步走反射：RavenRace 没装时只打一条 Message 就跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("ravenLocustWeatherQuiet", "RavenLocustWeatherQuietFix", RavenLocustWeatherQuietFix.Install);

            // 2026-10-06 新增：NudityMattersMore 的 CoverBody.UpdateIdeo 会因为
            // 「调用方传 null + 它自己的静态字典里已有一条同号残留」而解引用空引用，
            // 把整条 Pawn 生成流程掀翻 —— 表现就是同一个进程里连续开新局会卡死在生成阶段
            // （实测第 8 次开新局连抛 4 次后彻底卡住、5 分钟零日志）。
            // 详见 NmmIdeoTrackerNullFix.cs 的文件头注释（含 IL 偏移与时间线证据）。
            // 目标类型来自第三方 dll，所以本步走反射：NMM 没装时只打一条 Message 就跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("nmmIdeoTrackerNull", "NmmIdeoTrackerNullFix", NmmIdeoTrackerNullFix.Install);

            // 2026-10-07 新增：消掉古代神殿生成时固定刷的那几十条
            //     Could not find any RuleDef for symbol "mimicSpawner" with any resolver ...
            // 警告。
            //
            // 根因（已用反编译逐行核对）：模组「RimJobWorld - Onahole Extension」把自己
            // 的 symbol 推给了一个「1×1 的小格子四边各内缩 2 格」之后得到的**空矩形**，
            // 而 resolver 基类 SymbolResolver 要求宽高都 ≥ 1（minRectSize 默认 (1,1)），
            // 于是必然拒绝。原版这条路径的实际动作就是「拒绝 + 打印警告 + 什么都不做」，
            // 所以跳过它与原版**完全等价**，只是不再刷警告。
            //
            // 旧做法（在 Defs 下追加一条同 symbol 的 RuleDef 做兜底）已证明无效并已撤除：
            // 新规则的 resolver 还是同一个类、minRectSize 还是 (1,1)，对同一个空矩形照样拒绝。
            //
            // 详见 BaseGenMimicSpawnerQuietFix.cs 的文件头注释（含完整的证据链）。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("baseGenMimicSpawnerQuiet", "BaseGenMimicSpawnerQuietFix", BaseGenMimicSpawnerQuietFix.Install);

            // 2026-10-08 新增：修「Allies are Helpful」的一处空引用。
            //
            // 触发场景（快速测试地图实测抓到）：一场帝国派系袭击落地之后，
            // 日志里开始出现
            //     Exception ticking <角色名> (at (x, 0, z)): System.NullReferenceException
            //       at PawnTendAndRescuePatch.Postfix (Verse.Pawn __instance)
            // 实测 4 条 + 51 条重复堆栈折叠，涉及的角色全是刚进场的帝国成员。
            //
            // 根因（已用反编译把 IL 逐条读出来核对，完整证据链见补丁类的文件头注释）：
            // Postfix 里 `Job val = __instance.jobs?.curJob;` 之后，
            // 作者的守卫写的是 `if (val?.def != null && ...)` —— val 为 null 时它求值为 false，
            // **不会 return**，于是继续往下走，在第 130 / 146 行直接读 `val.def` 而炸。
            // 也就是说：真正的问题是「角色当前没有工作」这一常见情形没有被拦住。
            //
            // ⚠ 本步在 2026-10-08 之前叫 AlliesAreHelpfulNullCacheFix，当时的判断是
            //   「两个静态缓存字段 _cachedTendTargets / _cachedRescueTargets 没被初始化，
            //   初值是 null」。那个判断是**错的** —— .cctor 并非空方法（117 字节 / 29 条指令，
            //   明确包含 newobj + stsfld），且那两个字段唯一的赋值来源
            //   GetTendTargets / GetRescueTargets 都只有一个 return、永远返回新建的 List。
            //   两个事实合起来说明字段不可能是 null，旧修法是永不生效的空操作。
            //   推翻过程写在补丁类文件头的「一段被推翻的旧结论」一节。
            //
            // 修法与安全性：用转译器把 Postfix 里每一次「读 Job.def 字段」换成
            // null 安全的 SafeJobDef(job)。两者在求值栈上完全等价（[Job] → [JobDef]），
            // 只在 job 为 null 时把「抛异常」变成「返回 null」；而原代码本来就在那一步崩掉，
            // 所以不可能比原来更差。另挂一个收尾器兜底，防止将来模组改版让转译器失配。
            // 对第三方 dll 零编译期依赖（类型名走字符串 + 反射），
            // 模组没装时只打一条 Message 就直接跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("alliesAreHelpfulCurJobNull", "AlliesAreHelpfulCurJobNullFix", AlliesAreHelpfulCurJobNullFix.Install);

            // 2026-10-08 新增：消掉「RJW 基因扩展」在开发者模式下每帧刷的那句调试输出。
            //
            // 现象：只要用 -debug 启动，日志里就会被
            //     [RJW-Genes] multipreg checks
            // 刷爆。实测一局 30 分钟刷了 31415 条，占整份日志的 65%（约 2.1 MB 里的 1.4 MB），
            // 把真正要看的问题全埋掉了。
            //
            // 根因（该模组自带源码，直接读到，不用猜）：
            //     1.6\Source\Genes\Patches\MultiplePregnancies.cs 第 26 行
            //         if (RJWSettings.DevMode) RJW_Genes.ModLog.Message("multipreg checks");
            // 它挂在 PawnExtensions.IsPregnant(Pawn, bool) 这个**每帧都会被问很多次**的
            // 高频方法上，而这句调试输出既没有去重也没有节流 —— 于是问一次写一行。
            //
            // 注意：这句话外面包着 if (RJWSettings.DevMode)，所以**正常游玩不会出现**；
            // 但深度测试必须开 -debug，一开就被它淹没。而且它每写一行都要落盘一次，
            // 本身就在白白吃掉帧时间。
            //
            // 修法：patch 这句日志最终调用的 RJW_Genes.ModLog.Message(string)，
            // 只在这个字符串正好是 "multipreg checks" 时跳过原方法，
            // 其余参数一律放行。已用反编译确认该字符串在整个 Rjw-Genes.dll 里**只有一处**，
            // 所以不可能误伤其它日志；该类另有 Error / Warning / Debug 三个方法，本补丁一个都不碰。
            // 被拦掉的那行字不参与任何游戏逻辑，屏蔽它对游戏行为没有影响。
            // 对第三方 dll 零编译期依赖（类型名与字符串全部走反射 / 常量），
            // 模组没装时只打一条 Message 就直接跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("rjwGenesSpamQuiet", "RjwGenesSpamQuietFix", RjwGenesSpamQuietFix.Install);

            // 2026-10-11 新增：修掉「雪兔系列」公共库 SRCELib 的并发缺陷。
            //
            // 现象：一局日志被刷到 12.8 MB / 199,045 行，其中
            //     System.IndexOutOfRangeException   42,987 次
            //     IndexOutOfRangeException          28,655 次
            // 以及 1531 条 "Exception drawing <角色名>" —— 屏幕上部分角色（人和动物都有）
            // 每次绘制都抛异常、干脆不显示，而日志被彻底淹没。
            //
            // 根因：SRCELib.dll（由模组「雪兔商品扩展——花花兔子公司」提供，
            // 工坊 3748316169，packageId = SnowRabbit.CommodityExpansionPB）里，
            // HediffComp_ChangeBodyRatio 用两个**全游戏共享的静态容器**做缓存：
            //     static Dictionary<Pawn, HediffComp_ChangeBodyRatio> activeComps;
            //     static HashSet<Pawn> pawnsWithoutComp;
            // 而 TryGetComp() 会在「首次遇到某个角色」时往它们里写，全程没有任何锁。
            // 这个方法又被它自己的两个 Postfix 从**渲染路径**调用
            //（PawnRenderNodeWorker.OffsetFor / ScaleFor）—— 并行渲染下两个线程
            // 同时扩容同一个 HashSet，就会抛 IndexOutOfRangeException。
            // .NET 甚至直接报了 "A concurrent update was performed on this collection
            // and corrupted its state"，等于官方认定这是并发写坏了集合。
            //
            // 修法：在它外面套一把锁（Prefix 进锁 + Finalizer 出锁）。
            // 用 Finalizer 而不是 Postfix 释放锁，是为了让「原方法抛异常时也一定解锁」——
            // 否则会从「偶尔刷屏」恶化成「必然死锁」，比原来的 bug 更糟。
            // 对第三方 dll 零编译期依赖（类型名走字符串 + TypeByName），
            // SRCELib 不在场时只打一条 Message 就直接跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("srceLibConcurrency", "SrceLibConcurrencyFix", SrceLibConcurrencyFix.Install);

            // ------------------------------------------------------------------
            // 【26】Guards For Me（aRandomKiwi.GFM）的 DeepCopy 类型转换错误
            // ------------------------------------------------------------------
            // 现象：每次进游戏都留这几条红字 ——
            //     Error while resolving references for def Nivarian_ThinkTree:
            //         System.InvalidCastException: Specified cast is not valid.
            //     ... Nivarian_MechUnit_ThinkTree / PAS_PoweredApparelMaintenance 同样
            //
            // 根因：GuardsForMe.dll（工坊 1855885448，版本 3.0.10）里的
            // JobGiver_IdleCombat.DeepCopy 是从原版 JobGiver_Idle.DeepCopy 复制来的，
            // 复制时只把变量名 obj 改成了 val，**忘了把强转的目标类型一起改**。
            // 而它真正的直接基类是 ThinkNode_JobGiver（与原版 JobGiver_Idle 是兄弟关系，
            // 不是父子关系），所以 (JobGiver_Idle)base.DeepCopy(resolve) 必然抛异常。
            //
            // 影响：抛异常的位置在 ResolveSubnodes —— 它的职责是把 Subtree 引用的
            // 节点深拷贝进来。异常一抛，那整棵子树就没被复制，这三棵行为树是残缺的，
            // 用到它们的种族（涅瓦莲、PasserRace）的 AI 行为会实实在在地缺失。
            //
            // 修法：Transpiler 只替换两条 IL 指令（castclass 与 stfld 的目标类型）。
            // 采用「先整表扫描、两条都恰好命中一次才修改」的两段式写法，
            // 所以作者哪天真修好了，本补丁会自动退化为什么都不做。
            // 对 GuardsForMe.dll 零编译期依赖（类型 / 方法 / 字段全走反射查找）。
            // 本步幂等：Install() 内部有自己的静态守卫。
            TryInstall("gfmIdleCombatDeepCopy", "GfmIdleCombatDeepCopyFix", GfmIdleCombatDeepCopyFix.Install);

            // ------------------------------------------------------------------
            // 最后一步：修正汇总行的记账口径
            // ------------------------------------------------------------------
            // 「让 XML 补丁也能单独关闭」那个总控（xmlPatchGate）关掉时，
            // Patch_PatchOperation_Apply 根本不会被安装 —— 于是没有任何拦截，
            // Patches\ 下 10 个文件全部照常执行（这正是 About 里写的行为）。
            // 但它们不会再经过 FixReport.MarkLoaded，汇总行于是少 10 项：
            // 玩家看到「共 42 项」，可已装载/已关闭/未启用加起来不够 39，像是丢了东西。
            // 总控关掉 = 所有 XML 补丁都在执行，所以这里按「已装载」补记。
            MarkXmlTogglesWhenGateOff();
        }

        /// <summary>
        /// 总控（xmlPatchGate）被关掉时，把所有 XML 补丁项按「已装载」记账。
        ///
        /// 为什么要补：总控关闭 ⇒ 拦截器不安装 ⇒ 10 个文件全部执行，
        /// 但它们不会经过 XmlPatchGate 的 MarkLoaded。不补记，汇总行就会少 10 项。
        /// 总控开着时直接返回 —— 那种情况下各文件由 XmlPatchGate 按**实际执行**记账，
        /// 被单独关掉的文件不会被误记成「已装载」。
        /// </summary>
        private static void MarkXmlTogglesWhenGateOff()
        {
            try
            {
                if (IsFixEnabled("xmlPatchGate"))
                {
                    return;
                }
                FixToggles.Entry[] all = FixToggles.All;
                if (all == null)
                {
                    return;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    FixToggles.Entry e = all[i];
                    if (e != null && !e.XmlPatchFile.NullOrEmpty())
                    {
                        FixReport.MarkLoaded(e.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                // 记账出错不影响任何修复，但要让排查的人看得见。
                SafeLogError("[GNH LocalFixes] 汇总记账修正失败（不影响任何修复）：" + ex);
            }
        }

        // ======================================================================
        // 逐个补丁类地安装（替代 Harmony.PatchAll 那种一把梭的写法）
        // ======================================================================
        //
        // 【为什么不直接用 PatchAll】
        //
        // PatchAll 内部是「先把程序集里所有类型都取出来（GetTypes），
        // 再一类一类地装」。有两处要命的地方：
        //
        //   1) GetTypes() 有可能**整体失败**。
        //      Assembly.GetTypes() 要解析每个类型的「签名」—— 基类、接口、字段类型、
        //      方法参数与返回值类型。签名里任何一处引用了「当前加载不到的程序集」，
        //      这个类型就取不出来；失败的类型一多，GetTypes() 直接抛
        //      ReflectionTypeLoadException —— **整批类型都拿不到**，
        //      于是整个补丁包一个都装不上。
        //      这正是「多模组兼容修复补丁反而被某个没启用的模组拖死」的情形。
        //
        //      ⚠ 2026-10-08 用离线宿主实测，纠正了这里原先的一处错误说法：
        //        旧注释写的是「卸载 RJW / ElToro 就会让 GetTypes() 失败」，**实测并不成立**。
        //        本程序集里引用这两个模组的只有
        //        ElToro_BAddon.JobDriver_BestialityInvite_Watching 一个类，
        //        而它对那两个模组类型的引用**全部在方法体内部**；
        //        它自己的签名是 TryMakePreToilReservations(bool) 与 MakeNewToils()，
        //        用的都是游戏本体的类型。方法体要等真正调用时才解析，与 GetTypes() 无关。
        //        实测把 RJW.dll 与 ElToro_BAddon.dll 全部抽走：GetTypes() 照样成功，
        //        本程序集的类型一个不少（连 JobDriver 那个类本身也在）。
        //
        //        真正会让 GetTypes() 整体失败的是**游戏本体程序集**取不到：
        //        实测抽走 Assembly-CSharp.dll → 抛 ReflectionTypeLoadException，大部分类型取不出来。
        //        （2026-10-08 更正：原文写的是「24 个类型一个不少」与「24 个里只剩 20 个」。
        //          这种绝对数字会随源码增删立刻失效 —— 加一个类它就不对了。
        //          此后这类实测一律用相对表述，不写死个数。）
        //        那种情况下游戏压根没跑起来，轮不到本补丁操心。
        //        但「签名里引用可选模组的类型」这种写法将来完全可能被引入
        //        （只要有人给某个类加一个 ElToro 类型的字段或方法参数就会），
        //        所以这段兜底必须留着 —— 它挡的是「将来某一天」，不是「今天」。
        //
        //   2) 只要有一个类装失败，PatchAll 会直接中止，排在它后面的类全部不装。
        //
        // 【现在的做法】
        //
        //   * 取类型时兜住 ReflectionTypeLoadException，把**能加载的那部分**继续用作候选
        //    （取不到的那几个，签名里引用了当前加载不到的程序集，对应功能本来也用不上）；
        //   * 每个补丁类单独 try/catch：失败只记一条 Error，不影响别的类；
        //   * 每个类成功安装后记进 installedPatchClasses，
        //     下次（热重载 Def / 切换语言导致本模组对象被重新构造时）直接跳过 ——
        //     【2026-10-06 审计更正】这里原先写的是「游戏里每次打开模组管理器都会重新构造」，
        //     那与文件开头「为什么要防重复」那一节的反编译结论**直接矛盾**，也已复核证伪：
        //     LoadAllActiveMods 的调用点只有 PlayDataLoader.DoPlayLoad 与 HotReloadDefs 里的委托，
        //     **没有** UI 路径。打开模组页面不会触发这里。
        //     既不会漏装，也不会叠层。
        //
        // 一句话：**任何单个补丁的失败，都不再可能拖垮整个补丁包。**
        //
        // 【另外两个修复不从这里装】（新手容易找不到它们的安装点）
        //
        //   * VehicleFrameworkDebugFix —— 它自己带 [StaticConstructorOnStartup]，
        //     由【游戏】在「所有 Def 加载完毕之后」统一触发一次，**不经过本方法**、
        //     也不需要 TryInstall。
        //
        //     ⚠ 别写成「由 CLR 在程序集加载后自动跑」—— 那是错的。
        //     CLR 不会因为程序集被加载就去跑静态构造函数；真正来跑这一趟的是游戏：
        //       Verse.StaticConstructorOnStartupUtility.CallAll()
        //           → foreach (带该特性的每个类型) RuntimeHelpers.RunClassConstructor(...)
        //     而 CallAll 的唯一调用者是 Verse.PlayDataLoader（已反编译核对）。
        //     本文件另一处（CcoeReflectionFix.cs 末尾）把这套机制写对了，两处口径一致。
        //   * JobDriver_BestialityInvite_Watching —— 它根本不是 Harmony 补丁，
        //     只是补了一个缺失的 JobDriver **类型定义**（替换掉第三方 dll 里没有的那个类），
        //     所以既没有 [HarmonyPatch] 也不需要安装。
        private static void InstallAllPatchClasses()
        {
            Type[] candidates = LoadAllTypesTolerantly(Assembly.GetExecutingAssembly());
            if (candidates == null)
            {
                return;
            }

            int installedThisPass = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                Type type = candidates[i];
                if (type == null || !IsHarmonyPatchClass(type))
                {
                    continue;
                }
                if (InstallPatchClass(type))
                {
                    installedThisPass++;
                }
            }

            if (installedThisPass > 0)
            {
                SafeLogMessage("[GNH LocalFixes] Harmony patch classes installed this pass: " + installedThisPass + ".");
            }
        }

        // 取类型；遇到「部分类型加载不了」不放弃，把能用的挑出来继续用。
        private static Type[] LoadAllTypesTolerantly(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 含义：有一部分类型的「签名」引用了当前加载不到的程序集，所以取不出来。
                // 注意不是「装了但没启用」就会命中 —— 只有当某个类型的签名
                //（基类 / 字段类型 / 方法参数与返回值）直接用到那个模组的类型时才会。
                // 实测依据见本文件上方 InstallAllPatchClasses 那段说明。
                // 这属于**允许**的情况：取不到的那几个类型对应的功能本来也用不上，
                // 跳过它们即可，其余补丁照常工作。
                Type[] partial = ex.Types;
                List<Type> usable = new List<Type>();
                if (partial != null)
                {
                    for (int i = 0; i < partial.Length; i++)
                    {
                        if (partial[i] != null)
                        {
                            usable.Add(partial[i]);
                        }
                    }
                }
                SafeLogWarning("[GNH LocalFixes] Some types could not be loaded (a referenced mod assembly is probably missing). "
                    + usable.Count + " type(s) are still usable and the rest of the patch pack continues. First loader error: "
                    + FirstLoaderError(ex));
                return usable.ToArray();
            }
            catch (Exception ex)
            {
                // 理论上走不到这里：本程序集自己的类型枚举，唯一现实的失败模式是
                // ReflectionTypeLoadException，上面已经单独接住了。
                //
                // 但万一真的发生，**绝不能返回 null** —— 那会让下面 InstallAllPatchClasses
                // 直接 return，于是**一个补丁都装不上**，恰恰就是这段代码想避免的「被拖死」。
                // 退化成「按已知的补丁类名逐个取」：坏掉的那个跳过，其余照装。
                SafeLogError("[GNH LocalFixes] Could not enumerate types for patching: " + ex
                    + " -- falling back to per-name lookup so a single bad type cannot disable the whole pack.");
                return LoadKnownPatchClassesTolerantly(assembly);
            }
        }

        // 「GetTypes() 整体失败」时的退化路径：按名字一个一个取补丁类。
        //
        // ⚠ 维护提醒：新增一个带 [HarmonyPatch] 的补丁类时，**必须**把它的完整类型名
        //    加进下面这张表 —— 否则在这条（极少走到的）退化路径上，新类会被静默漏掉。
        //    正常路径（GetTypes 成功）不受这张表影响，新增补丁**不必**改别处。
        private static readonly string[] KnownPatchClassNames =
        {
            "GNH.LocalFixes.Patch_ModLister_HasActiveModWithName",
            "GNH.LocalFixes.Patch_ListerThings_Add_RejectMislabeledInstrument",
            "GNH.LocalFixes.Patch_IdeoFoundation_KeepLeaderTitle",
            "GNH.LocalFixes.Patch_VerbProperties_AdjustedRange",
            "GNH.LocalFixes.Patch_GenTypes_PreferTelanda",
            "GNH.LocalFixes.Patch_LoadModXML_FilterTelandaGenes",
            "GNH.LocalFixes.Patch_LoadModXML_FilterRjwpeStaleDefs",
            // 【39】实时绘制空引用：这条也是 [HarmonyPatch] 类（带 [HarmonyPrepare] 让路）。
            // 漏登记它不会在正常路径上出问题（GetTypes 成功时按特性扫得到），
            // 但在「GetTypes() 整体失败」的退化路径上，【39】会被静默漏装 —— 所以必须登记。
            "GNH.LocalFixes.Patch_Thing_DynamicDrawPhase_SwallowRealtimeNre",
            // 下面两个是 XML 补丁开关的机制类。漏了它们在退化路径上不会报错，
            // 只会让「XML 补丁也能单独关」这个功能静默消失 —— 所以必须登记。
            "GNH.LocalFixes.Patch_PatchOperation_Apply",
            "GNH.LocalFixes.Patch_PatchOperation_Complete",
        };

        private static Type[] LoadKnownPatchClassesTolerantly(Assembly assembly)
        {
            List<Type> usable = new List<Type>();
            for (int i = 0; i < KnownPatchClassNames.Length; i++)
            {
                try
                {
                    Type type = assembly.GetType(KnownPatchClassNames[i], throwOnError: false);
                    if (type != null)
                    {
                        usable.Add(type);
                    }
                }
                catch (Exception)
                {
                    // 单个类取不到就跳过它，绝不因此放弃其余的。
                }
            }
            SafeLogWarning("[GNH LocalFixes] Per-name fallback found " + usable.Count + " of "
                + KnownPatchClassNames.Length + " known patch classes.");
            return usable.ToArray();
        }

        // LoaderExceptions 里常常夹着一堆 null，挑第一条有内容的报出来就够了。
        private static string FirstLoaderError(ReflectionTypeLoadException ex)
        {
            Exception[] errors = ex.LoaderExceptions;
            if (errors == null || errors.Length == 0)
            {
                return "(none reported)";
            }
            for (int i = 0; i < errors.Length; i++)
            {
                if (errors[i] != null)
                {
                    return errors[i].GetType().Name + ": " + errors[i].Message;
                }
            }
            return "(all null)";
        }

        // 只有带 [HarmonyPatch] 的类才需要交给 Harmony 处理。
        // 不带还硬塞给 PatchClassProcessor 的话，它会直接抛异常（白记一条 Error）。
        private static bool IsHarmonyPatchClass(Type type)
        {
            try
            {
                object[] attributes = type.GetCustomAttributes(typeof(HarmonyPatch), true);
                return attributes != null && attributes.Length > 0;
            }
            catch (Exception)
            {
                // 连特性都读不到的类（依赖缺失等），当作「不是补丁类」跳过。
                return false;
            }
        }

        /// <summary>
        /// 机制类 → 它归哪个既有开关管。
        ///
        /// 有些补丁类玩家不需要（也不该）单独关，但它们必须跟着某个开关同生共死：
        ///   · Patch_PatchOperation_Apply   —— 「让 XML 补丁也能单独关闭」的执行端；
        ///   · Patch_PatchOperation_Complete —— 同一件事的收尾端（被关掉的补丁不许报 failed）。
        /// 前者在 FixToggles 里登记为 xmlPatchGate 的 PatchClassName；
        /// 后者没有自己的登记项，靠这张表挂到同一个开关名下 ——
        /// 否则 InstallPatchClass 会以为「新增补丁类忘了登记」，每次启动打一条黄字。
        /// </summary>
        private static readonly Dictionary<string, string> mechanismClassOwner =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "GNH.LocalFixes.Patch_PatchOperation_Complete", "xmlPatchGate" },
        };

        // 装一个补丁类。成功安装返回 true。
        private static bool InstallPatchClass(Type type)
        {
            lock (installedPatchClasses)
            {
                if (installedPatchClasses.Contains(type))
                {
                    return false;
                }

                // 玩家把这一项关掉了就跳过。
                // ⚠ 只有「本补丁类确实有对应开关项」时才跳过：IdForPatchClass 返回 null
                //   表示这一类不受开关管辖（将来新增但忘了登记的补丁类会落到这里），
                //   此时应当**照常安装** —— 漏装的后果是「修复静默消失」，比多装严重得多。
                // 跳过时不写 installedPatchClasses：玩家中途把开关打开、下一次（切语言 /
                //   热重载）触发本方法时，它就能立刻装上。
                string toggleId = FixToggles.IdForPatchClass(type.FullName);
                if (toggleId == null)
                {
                    // 没在 FixToggles 里登记？先看它是不是「机制类」——
                    // 那类东西跟着既有开关走，不该有自己的界面项（见 mechanismClassOwner）。
                    string owner;
                    if (type.FullName != null
                        && mechanismClassOwner.TryGetValue(type.FullName, out owner))
                    {
                        toggleId = owner;
                    }
                }
                if (toggleId == null)
                {
                    // 这个补丁类没在 FixToggles 里登记 —— 不受任何开关管辖，照常安装。
                    // 之所以不「拒绝安装」：漏装的后果（修复静默消失）比多装严重得多。
                    // 但必须留一条日志，否则将来新增补丁类时忘了登记，
                    // 玩家看到的现象会是「设置界面里没有这一项，可它一直生效」，
                    // 排查时完全找不到线索。
                    SafeLogWarning("[GNH LocalFixes] Patch class has no toggle entry in FixToggles; "
                        + "installing it unconditionally: " + type.Name);
                }
                if (toggleId != null && !IsFixEnabled(toggleId))
                {
                    SafeLogMessage("[GNH LocalFixes] Patch class skipped, turned off in mod settings: "
                        + type.Name + " [" + toggleId + "].");
                    return false;
                }

                try
                {
                    List<MethodInfo> patched = HarmonyInstance.CreateClassProcessor(type).Patch();

                    // 顺序是「先立标记、再打日志」，不能反过来：
                    // 万一打印这一步出了任何意外，标记也已经立住，
                    // 不会导致下次重复安装、把同一批补丁叠成两层。
                    // （下面用的是 SafeLogMessage，它自己已经不会抛了 —— 这里算双保险。）
                    installedPatchClasses.Add(type);

                    // 记账：toggleId 为 null 的类（未登记开关）MarkLoaded 会直接忽略，
                    // 那类项在设置界面上本来也不显示状态。
                    FixReport.MarkLoaded(toggleId);

                    SafeLogMessage("[GNH LocalFixes] Patch class installed: " + type.Name
                        + " (" + ((patched != null) ? patched.Count : 0) + " target method(s)).");
                    return true;
                }
                catch (Exception ex)
                {
                    // 不立标记 → 下次构造本模组对象时会再试一次；但不影响其它任何补丁类。
                    //
                    // ⚠ 这里**必须**用 SafeLogError，不能写成裸的 Log.Error。
                    // 2026-10-07 的极端场景测试实测过：当日志子系统本身正在抛异常
                    // （磁盘写满 / 日志文件被占用 / 被别的模组打坏了 Log），
                    // 裸的 Log.Error 会把异常从**这个 catch 块内部**再次扔出去 ——
                    // 等于 catch 白写：异常一路穿到 LocalFixesMod 的构造函数，
                    // 「某个补丁装不上」就升级成了「补丁叠层、越玩越卡」。
                    SafeLogError("[GNH LocalFixes] Patch class " + type.Name
                        + " failed to install; other patches are unaffected, and this one will be retried: " + ex);
                    FixReport.MarkFailed(toggleId);
                    return false;
                }
            }
        }

        // 把「装一步补丁」包起来：失败只记日志，绝不往外抛。
        // 理由见构造函数里那段注释（构造函数抛异常会破坏 mod 类的去重登记）。
        //
        // ⚠ catch 里必须是 SafeLogError：裸的 Log.Error 在「日志系统自己抛异常」时
        //    会让异常从这个 catch 里漏出去，把「这一步没装上」变成「构造函数抛异常」。
        //    （2026-10-07 极端场景测试实测确证，细节见 SafeLog* 那段注释。）
        /// <summary>
        /// 某一项修复是否被玩家开启。
        ///
        /// 【读不到设置时返回 true】
        ///   保守方向是「宁可多修」：本补丁包存在的意义就是兜住那些会让玩家崩掉的问题，
        ///   因为一次配置读取失败就把二十多项修复全关掉，比不装这个模组还糟。
        ///   （设置对象为空只在极端情况下出现，见构造函数里那段 try/catch。）
        /// </summary>
        internal static bool IsFixEnabled(string toggleId)
        {
            try
            {
                GNHLocalFixesSettings settings = Settings;
                if (settings == null)
                {
                    return true;
                }
                return settings.IsOn(toggleId);
            }
            catch (Exception)
            {
                // IsOn 本身只是几次字典查找，理论上不会抛。真抛了也按「开启」处理，理由同上。
                return true;
            }
        }

        // 装一步补丁。toggleId 对应 FixToggles 里的一项；玩家在设置界面里关掉它，
        // 这一步就整段跳过，并且**在日志里留一条记录** ——
        // 「明明装了这个模组却没修」是排查时最费解的情形，必须能从日志一眼看出来是玩家自己关的。
        private static void TryInstall(string toggleId, string name, Action install)
        {
            if (!IsFixEnabled(toggleId))
            {
                SafeLogMessage("[GNH LocalFixes] Skipped, turned off in mod settings: " + name
                    + " [" + toggleId + "].");
                return;
            }
            try
            {
                install();
                // 记账：这一项这次确实装上了。「装上了」与「玩家开着」是两件事 ——
                // 前者还可能因为目标方法签名变了而失败，设置界面读的就是这份账本。
                FixReport.MarkLoaded(toggleId);
            }
            catch (Exception ex)
            {
                SafeLogError("[GNH LocalFixes] " + name + ".Install() failed; that fix is NOT active this session: " + ex);
                FixReport.MarkFailed(toggleId);
            }
        }

        // ======================================================================
        // 模组设置界面
        // ======================================================================
        //
        // 游戏「选项 → 模组设置」里会多出一项叫 GNH LocalFixes 的分类，
        // 里面就是下面这些控件。
        //
        // 【为什么这里只管改值、不主动写盘】
        //
        // 游戏的 Dialog_ModSettings 在窗口关闭时会自己调用 WriteSettings()，
        // 我们不必操心。反过来，如果在 DoSettingsWindowContents 里顺手写一次，
        // 那就会变成**每帧写一次配置文件**（这个方法每帧都被调用），
        // 白白磨损磁盘还掉帧 —— 所以这里一行写盘代码都不放。
        /// <summary>
        /// 取翻译；取不到就回退到写死的中文。
        ///
        /// 【为什么用 TryTranslate 而不是 Translate】
        ///   无参的 "键名".Translate() 在**找不到这个键**的时候，
        ///   会把**键名本身**原样返回 —— 界面就会显示成
        ///   "GNH_LocalFixes_SettingsCategory" 这种给机器看的东西，比英文还难懂。
        ///   更麻烦的是：开发者模式下它还会把返回值交给 PseudoTranslated()
        ///   逐字母"变音化"（GNH_LòçàlFìxéš_…），于是**连「返回值和键名相同」
        ///   这个判断都会失效**（本机 Prefs.devMode = True，已按反编译确认）。
        ///
        ///   而 TryTranslate(out result) 给出的是一个明确的 bool：
        ///   找到了才是 true；没找到时 result 是键名，并且**不做变音处理**。
        ///   拿这个 bool 判断，既不依赖字符串比较，也不受开发者模式影响。
        ///
        /// 【为什么不直接抛异常】
        ///   本方法是在**绘制设置窗口**时被调用的。GUI 绘制流程里抛异常，
        ///   轻则这一帧画不出来，重则整个设置窗口报错关不掉。
        ///   宁可退化成一句能看懂的中文，也不能让玩家打不开设置。
        /// </summary>
        private static string Translated(string key, string chineseFallback)
        {
            try
            {
                TaggedString text;
                if (key.TryTranslate(out text) && !text.RawText.NullOrEmpty())
                {
                    return text.RawText;
                }
                return chineseFallback;
            }
            catch
            {
                return chineseFallback;
            }
        }

        public override string SettingsCategory()
        {
            // 这个名字会直接显示在「选项 → 模组设置」的列表里。
            //
            // 【为什么要走翻译】这样英文环境显示英文、中文环境显示中文。
            //   早先这里写死中文，理由写在注释里说是「放进 Languages\ 就得依赖
            //   语言包文件夹名（含括号那种），一改名翻译就静默失效」——
            //   那个顾虑**不成立**：RimWorld 认的是 ChineseSimplified / English
            //   这种**不带括号**的目录名，而且这份语言文件是**本模组自己提供**的，
            //   跟外部语言包改不改名毫无关系。（2026-10-11 审计后改正。）
            //
            // 取不到翻译时回退成写死的中文，见 Translated()。
            //
            // ⚠ 中文名必须与 About\About.xml 的 <name> 保持一致。
            //   两者是分开的两处：游戏内【模组列表】读 About.xml 的 <name>，
            //   【模组设置】读这里的返回值。改名时两处都要改。
            return Translated("GNH_LocalFixes_SettingsCategory", "多模组兼容修复补丁");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            GNHLocalFixesSettings settings = Settings;
            if (settings == null)
            {
                // 设置对象没取到（理论上不会发生）：给一句提示，别让窗口一片空白。
                Widgets.Label(inRect, Translated("GNH_LocalFixes_SettingsUnavailable",
                    "设置读取失败。本补丁按内置默认值（全部开启）继续工作。"));
                return;
            }

            // 先把字体钉死再测量。Text.CalcHeight 用的是**当前字体**，
            // 而下面测量与绘制之间隔着 Widgets.ButtonText / CheckboxLabeled 等控件；
            // 只要有一个改了 Text.Font 又不还原，两处算出的高度就会不一致
            // （表现是滚不到底或底部留白）。显式设一次最省心。
            Text.Font = GameFont.Small;

            // ---- 顶部：批量按钮 + 当前状态 + 重启提示 ----
            const float headerHeight = 86f;
            DrawSettingsHeader(new Rect(inRect.x, inRect.y, inRect.width, headerHeight), settings);

            // ---- 下面：可滚动的开关列表 ----
            //
            // 为什么用 ScrollView 而不是直接平铺：二十多项、每项还带一行说明，
            // 高度远超设置窗口，不滚动的话下面一大半永远看不见
            //（而且玩家不会知道"下面还有"，只会觉得"就这几项"）。
            float listTop = inRect.y + headerHeight + 6f;
            Rect outer = new Rect(inRect.x, listTop, inRect.width,
                Mathf.Max(120f, inRect.height - headerHeight - 6f));
            float innerWidth = outer.width - 22f;   // 给右侧滚动条留出位置
            Rect view = new Rect(0f, 0f, innerWidth, MeasureListHeight(innerWidth));

            Widgets.BeginScrollView(outer, ref settingsScroll, view);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(view);
            DrawToggleList(listing, settings, innerWidth);
            listing.End();
            Widgets.EndScrollView();
        }

        // 分组标题占的高度。测量与绘制共用同一个常数 ——
        // 两处不一致的后果是「滚不到底」或者「底部空一大片」，而且很难看出是哪边错了。
        private const float GroupHeaderHeight = 34f;

        /// <summary>
        /// 顶部那一行：三个批量按钮 + 当前状态 + 「要重启才生效」的提示。
        /// </summary>
        private static void DrawSettingsHeader(Rect rect, GNHLocalFixesSettings settings)
        {
            float buttonWidth = (rect.width - 16f) / 3f;
            Rect row = new Rect(rect.x, rect.y, rect.width, 30f);

            if (Widgets.ButtonText(new Rect(row.x, row.y, buttonWidth, 30f),
                    Translated("GNH_LocalFixes_EnableAll", "全部开启（默认）")))
            {
                settings.SetAll(true);
            }
            if (Widgets.ButtonText(new Rect(row.x + buttonWidth + 8f, row.y, buttonWidth, 30f),
                    Translated("GNH_LocalFixes_DisableAll", "全部关闭")))
            {
                settings.SetAll(false);
            }
            Rect onlyCrashRect = new Rect(row.x + (buttonWidth + 8f) * 2f, row.y,
                buttonWidth, 30f);
            // 悬停提示：光看按钮文字容易以为「行为与设置」也被关掉了，
            // 这里明说它不动 —— 那两项是玩家偏好（保不保护领袖头衔之类），不是故障修复，
            // 顺手替玩家改掉不合适。
            //
            // ⚠ v1.4.10 加了第五组（XML 补丁）之后，本按钮会连那 12 个 XML 开关一起关掉，
            //    而提示当时没跟着改 —— v1.4.12 补上「XML 补丁」这一类，别再漏。
            TooltipHandler.TipRegion(onlyCrashRect,
                Translated("GNH_LocalFixes_OnlyCrashes_Tip",
                    "把「日志刷屏」「兼容与判定」「XML 补丁」三类关掉，只留下不修就会崩的那些。\n"
                    + "「行为与设置」两项不动 —— 那是你自己的偏好，不是故障修复。"));
            if (Widgets.ButtonText(onlyCrashRect,
                    Translated("GNH_LocalFixes_OnlyCrashes", "只留「崩溃与卡死」")))
            {
                // 实用预设：除「行为与设置」外，只把「崩溃与卡死」这一类打开。
                // 也就是说「日志刷屏」「兼容与判定」「XML 补丁」三类都会关掉 ——
                // 提示文案必须与这里保持一致，否则玩家会以为 XML 补丁没被关。
                // 「行为与设置」两项是玩家偏好（不是修 bug），保持原样不动，免得顺手改掉别人的选择。
                for (int i = 0; i < FixToggles.All.Length; i++)
                {
                    FixToggles.Entry e = FixToggles.All[i];
                    if (e.Group != FixToggles.GroupBehaviour)
                    {
                        settings.SetOn(e.Id, e.Group == FixToggles.GroupCrash);
                    }
                }
            }

            int total, loadedCount, disabledCount, missingCount, failedCount, notRunCount;
            FixReport.Count(out total, out loadedCount, out disabledCount,
                out missingCount, out failedCount, out notRunCount);
            string status = string.Format(Translated("GNH_LocalFixes_StatusSummary",
                    "本次启动：共 {0} 项 —— 已装载 {1} 项 · 已关闭 {2} 项 · 目标模组未安装 {3} 项"),
                total, loadedCount, disabledCount, missingCount);
            if (failedCount > 0)
            {
                status += string.Format(Translated("GNH_LocalFixes_StatusFailed",
                    " · 安装失败 {0} 项"), failedCount);
            }
            // 「未运行」必须印出来。
            //
            // 以前只在「安装失败 > 0」时才追加，于是差额在没有报错的情况下**凭空消失**：
            // 玩家看到「共 42 项 · 已装载 26 项 · 已关闭 0 · 未启用 0」，
            // 剩下的 11 项在界面上无处交代（2026-10-19 玩家实际提出的疑问）。
            // 数字必须能加得起来，哪怕最后一项叫「未运行」。
            if (notRunCount > 0)
            {
                status += string.Format(Translated("GNH_LocalFixes_StatusNotRun",
                    " · 未运行 {0} 项"), notRunCount);
            }
            Rect statusRect = new Rect(rect.x, rect.y + 36f, rect.width, 22f);
            Widgets.Label(statusRect, status);
            if (FixReport.XmlPatchesServedFromCache)
            {
                TooltipHandler.TipRegion(statusRect,
                    Translated("GNH_LocalFixes_StatusCacheTip",
                        "12 个 XML 补丁项这次算作「已装载」，但本轮并没有重跑。\n"
                        + "原因：你装了 Def 加载缓存类模组（如 DefLoadCache），它命中了缓存，"
                        + "整个 XML 补丁阶段被跳过，Def 直接从上次「打完补丁」的快照载入。\n"
                        + "修复效果仍然在 —— 缓存里存的就是补丁执行后的结果。"
                        + "只要改动任何 Patches 文件或本模组的 dll，缓存会自动重建、补丁重新执行。"));
            }

            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0.45f);
            Widgets.Label(new Rect(rect.x, rect.y + 58f, rect.width, 26f),
                Translated("GNH_LocalFixes_RestartHint",
                    "★ 改动要重启游戏才生效 —— 补丁一旦挂上就没法中途摘掉，这里决定的是「下次启动装哪几项」。"));
            GUI.color = prevColor;
        }

        // ──────────────────────────────────────────────────────────────
        // 界面文本缓存
        // ──────────────────────────────────────────────────────────────
        //
        // 【为什么要有这一段】
        //   DoSettingsWindowContents 是 OnGUI 回调，**每帧都会跑**
        //   （一帧通常有 Layout 与 Repaint 两次）。而滚动区要先知道总高度，
        //   于是每帧都得对 22 项各做一次 Text.CalcHeight；绘制时又要再来一遍。
        //   不缓存就是每帧几十次文字度量 —— 纯白烧，而且随开关数量线性增长。
        //
        //   所以「翻译 + 度量」的结果算一次存下来，测量与绘制**共用同一份**，
        //   顺带也让两处不会因为各算各的而对不上（对不上的后果是滚不到底或底部留白）。
        //
        // 【什么时候必须重算】
        //   宽度变了（换分辨率、改 UI 缩放）、当前语言变了、开关项数变了。
        //   三者任一不同就整体重算；其余情况直接用旧的。
        private static float cacheWidth = -1f;
        private static string cacheLangFolder;
        private static int cacheCount = -1;
        private static string[] cacheLabel;
        private static string[] cacheDesc;
        private static string[] cacheGroup;
        private static float[] cacheDescHeight;
        private static float cacheTotalHeight;

        private static void BuildUiCacheIfNeeded(float width)
        {
            string langFolder = (LanguageDatabase.activeLanguage != null)
                ? LanguageDatabase.activeLanguage.folderName : "?";
            if (cacheCount == FixToggles.All.Length
                && cacheLangFolder == langFolder
                && Mathf.Abs(cacheWidth - width) < 0.01f
                && cacheTotalHeight > 0f)
            {
                return;
            }

            int n = FixToggles.All.Length;
            cacheLabel = new string[n];
            cacheDesc = new string[n];
            cacheGroup = new string[n];
            cacheDescHeight = new float[n];

            float total = 0f;
            string lastGroup = null;
            for (int i = 0; i < n; i++)
            {
                FixToggles.Entry e = FixToggles.All[i];
                cacheLabel[i] = Translated(FixToggles.LabelKeyOf(e.Id), e.Label);
                cacheDesc[i] = Translated(FixToggles.DescKeyOf(e.Id), e.Desc);
                cacheGroup[i] = Translated(e.Group, FixToggles.GroupFallback(e.Group));
                // 26f = 复选框那一行；说明文本按实际换行后的高度算；10f = 项与项之间的间隔。
                cacheDescHeight[i] = Text.CalcHeight(cacheDesc[i], width - 26f);

                if (e.Group != lastGroup)
                {
                    lastGroup = e.Group;
                    if (i > 0)
                    {
                        total += 8f;      // 与 DrawToggleList 里的 listing.Gap(8f) 对应
                    }
                    total += GroupHeaderHeight;
                }
                total += 26f + cacheDescHeight[i] + 10f;
            }

            cacheTotalHeight = total + 8f;    // 底部留一点余量，免得最后一行贴着边
            cacheWidth = width;
            cacheLangFolder = langFolder;
            cacheCount = n;
        }

        /// <summary>
        /// 列表总高度。与 DrawToggleList 共用同一份缓存，所以两边必然一致。
        /// </summary>
        private static float MeasureListHeight(float width)
        {
            BuildUiCacheIfNeeded(width);
            return cacheTotalHeight;
        }

        /// <summary>
        /// 把 FixToggles.All 画成一条条开关。顺序、分组、文案全部来自那张表 ——
        /// 界面上能看到的项，就是安装侧会去查的那些项，不存在「有界面没接线」的假开关。
        /// </summary>
        /// <summary>
        /// CheckboxLabeled 把复选框画在整行最右端、固定 24px 宽（rect.xMax - 24 ~ xMax）。
        /// 状态文字必须右对齐到它左边，否则会正好叠在勾 / 叉图标上 —— v1.4.12 修的就是这个重叠。
        /// 32f = 复选框 24 + 间距 8。
        /// </summary>
        private const float CheckboxReserve = 32f;

        private static void DrawToggleList(Listing_Standard listing, GNHLocalFixesSettings settings, float width)
        {
            // 文本与高度都取自缓存 —— 与 MeasureListHeight 用的是同一份，
            // 所以「测量的高度」与「实际画出来的高度」不可能对不上。
            BuildUiCacheIfNeeded(width);

            string lastGroup = null;
            for (int i = 0; i < FixToggles.All.Length; i++)
            {
                FixToggles.Entry entry = FixToggles.All[i];

                if (entry.Group != lastGroup)
                {
                    lastGroup = entry.Group;
                    if (i > 0)
                    {
                        listing.Gap(8f);
                    }
                    Rect headerRect = listing.GetRect(GroupHeaderHeight);
                    GameFont prevFont = Text.Font;
                    Text.Font = GameFont.Medium;
                    // 分组标题也走翻译：entry.Group 是语言键（见 FixToggles），
                    // 取不到就回退到写死的中文。
                    Widgets.Label(headerRect, cacheGroup[i]);
                    Text.Font = prevFont;
                    Widgets.DrawLineHorizontal(headerRect.x, headerRect.yMax - 6f, headerRect.width);
                }

                // CheckboxLabeled 会**直接改写**传进去的那个 bool，
                // 所以先复制一份、比完再写回设置对象 —— 这样「有没有变化」一目了然，
                // 也不会因为中途异常把设置改坏。
                bool value = settings.IsOn(entry.Id);
                bool before = value;
                Rect toggleRect = listing.GetRect(26f);
                Widgets.CheckboxLabeled(toggleRect, cacheLabel[i], ref value);
                if (value != before)
                {
                    settings.SetOn(entry.Id, value);
                }

                // 状态提示：只对**异常状态**标注，正常项不画（避免界面噪音）。
                // 判据见 FixToggles.StatusOf —— 只有两条能确定的：开关值、前置模组在不在场。
                // 画在标题行右端、但必须**给复选框让路**（v1.4.12）：右边界收在 CheckboxReserve 以内，
                // 否则会正好压在勾 / 叉图标上。空间不够（标题特别长）时宁可不标注，也不叠字。
                FixToggles.FixStatus status = FixToggles.StatusOf(entry);
                if (status != FixToggles.FixStatus.Active)
                {
                    string statusText = (status == FixToggles.FixStatus.Disabled)
                        ? "GNH_LocalFixes_Status_Disabled".Translate()
                        : "GNH_LocalFixes_Status_TargetMissing".Translate();

                    float labelWidth = Text.CalcSize(cacheLabel[i]).x;
                    GameFont prevFont = Text.Font;
                    Text.Font = GameFont.Tiny;
                    float statusWidth = Text.CalcSize(statusText).x;
                    float statusRight = toggleRect.width - CheckboxReserve;
                    if (statusRight - statusWidth > labelWidth + 12f)
                    {
                        Color prevColor = GUI.color;
                        TextAnchor prevAnchor = Text.Anchor;
                        GUI.color = (status == FixToggles.FixStatus.Disabled)
                            ? new Color(0.62f, 0.62f, 0.62f)
                            : new Color(0.85f, 0.72f, 0.30f);
                        Text.Anchor = TextAnchor.MiddleRight;
                        Widgets.Label(new Rect(toggleRect.x, toggleRect.y, statusRight, toggleRect.height),
                            statusText);
                        Text.Anchor = prevAnchor;
                        GUI.color = prevColor;
                    }
                    Text.Font = prevFont;
                }

                // 说明单独一行、缩进对齐到标题文字下面。
                float descHeight = cacheDescHeight[i];
                Rect descRect = listing.GetRect(descHeight);
                Widgets.Label(new Rect(descRect.x + 26f, descRect.y, descRect.width - 26f, descHeight),
                    cacheDesc[i]);

                listing.Gap(10f);
            }
        }
    }
}
