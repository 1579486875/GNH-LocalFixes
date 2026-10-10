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
    // 整个补丁包会一个都装不上 —— 那正是「模组兼容修复补丁反被别的模组拖死」的情形。
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
            catch (Exception) { }
        }

        internal static void SafeLogError(string message)
        {
            try { Log.Error(message); }
            catch (Exception) { }
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

            // 再兜一层：把「逐类安装」这一步包起来。
            //
            // ⚠ 构造函数里其实有【两条】互相独立的安装路径，读注释时别把它们混成一条：
            //   (a) 下面这个 try 块里的 InstallAllPatchClasses() —— 只管带 [HarmonyPatch]
            //       特性的那 4 个补丁类，内部由 InstallPatchClass 逐类 try/catch；
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
            TryInstall("AssemblyGetTypesFallbackFix", AssemblyGetTypesFallbackFix.Install);

            TryInstall("VFEEInstrumentSpaceFix", VFEEInstrumentSpaceFix.Install);
            TryInstall("SteamWorkshopHookFix", SteamWorkshopHookFix.Install);
            TryInstall("CrossPromotionUnpatch", CrossPromotionUnpatch.Install);

            // 下面这个是 2026-10-04 根据游戏日志新增的修复，详见它的文件头注释。
            // 安装方式是「按类型名去找，找不到就置位跳过」。
            TryInstall("TechHediffsZeroBudgetFix", TechHediffsZeroBudgetFix.Install);

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
            TryInstall("MusicManagerFadeoutFix", MusicManagerFadeoutFix.Install);

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
            TryInstall("XmlExtensionsFindModFix", XmlExtensionsFindModFix.Install);

            // 2026-10-06 新增：NudityMattersMore 的 CoverBody.UpdateIdeo 会因为
            // 「调用方传 null + 它自己的静态字典里已有一条同号残留」而解引用空引用，
            // 把整条 Pawn 生成流程掀翻 —— 表现就是同一个进程里连续开新局会卡死在生成阶段
            // （实测第 8 次开新局连抛 4 次后彻底卡住、5 分钟零日志）。
            // 详见 NmmIdeoTrackerNullFix.cs 的文件头注释（含 IL 偏移与时间线证据）。
            // 目标类型来自第三方 dll，所以本步走反射：NMM 没装时只打一条 Message 就跳过。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("NmmIdeoTrackerNullFix", NmmIdeoTrackerNullFix.Install);

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
            TryInstall("BaseGenMimicSpawnerQuietFix", BaseGenMimicSpawnerQuietFix.Install);

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
            TryInstall("AlliesAreHelpfulCurJobNullFix", AlliesAreHelpfulCurJobNullFix.Install);

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
            TryInstall("RjwGenesSpamQuietFix", RjwGenesSpamQuietFix.Install);
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
        //      这正是「模组兼容修复补丁反而被某个没启用的模组拖死」的情形。
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

        // 装一个补丁类。成功安装返回 true。
        private static bool InstallPatchClass(Type type)
        {
            lock (installedPatchClasses)
            {
                if (installedPatchClasses.Contains(type))
                {
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
        private static void TryInstall(string name, Action install)
        {
            try
            {
                install();
            }
            catch (Exception ex)
            {
                SafeLogError("[GNH LocalFixes] " + name + ".Install() failed; that fix is NOT active this session: " + ex);
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
        public override string SettingsCategory()
        {
            // 这个名字会直接显示在「选项 → 模组设置」的列表里。
            //
            // 为什么这里写中文、而不是走 "Key".Translate() 那一套：
            //   语言包文件夹名是「ChineseSimplified (简体中文)」这种带括号的写法，
            //   把翻译放进 Languages\ 就得死死依赖这个名字，语言包一改名翻译就静默失效。
            //   而本包界面只有下面这一行控件 —— 直接写死中文最不容易出错。
            //
            // ⚠ 这个名字必须与 About\About.xml 的 <name> 保持一致。
            //   两者是分开的两处：游戏内【模组列表】读 About.xml 的 <name>，
            //   【模组设置】读这里的返回值。改名时漏掉这里，设置界面就会一直显示旧名。
            return "模组兼容修复补丁";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            GNHLocalFixesSettings settings = Settings;
            if (settings == null)
            {
                // 设置对象没取到（理论上不会发生）：给一句提示，别让窗口一片空白。
                Widgets.Label(inRect, "设置读取失败。本补丁按内置默认值（保护开启）继续工作。");
                return;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            // CheckboxLabeled 会**直接改写**传进去的那个 bool，
            // 所以先复制一份出来，点完再把结果同步回设置对象 ——
            // 这样「有没有变化」一目了然，也不会因为中途异常改坏设置。
            bool keepLeaderTitle = settings.keepLeaderTitle;
            listing.CheckboxLabeled(
                "保护自定义的领袖头衔",
                ref keepLeaderTitle,
                "开启时：已经定好的领袖头衔（主席、教宗……这类）不会再被游戏重新随机，"
                    + "改模因、点「随机符号」都不会动它。新建文化时的第一次生成不受影响。\n\n"
                    + "关闭后：完全恢复原版行为 —— 改模因或点「随机符号」会把领袖头衔重新掷一次。\n\n"
                    + "想给某个文化换一个头衔：把头衔清空（或临时关掉本开关）再点一次「随机符号」即可。");
            settings.keepLeaderTitle = keepLeaderTitle;

            listing.End();
        }
    }
}
