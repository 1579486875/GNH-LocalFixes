using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个类要解决的问题：只要一打开游戏里的「模组」页面，整个游戏就崩掉。
    // ==========================================================================
    //
    // 【到底崩在哪一步】
    //
    // 游戏画「模组」页面时，会一层一层往下调用：
    //
    //   1. 游戏自己在画模组列表 .......... RimWorld.Page_ModsConfig.DoWindowContents
    //   2. 一个叫 CrossPromotion 的组件插了一脚
    //      （它给「显示某个模组的详细信息」这个功能装了自己的代码）
    //   3. 那段代码去问游戏：「这个模组的 Steam 信息是什么？」
    //   4. 游戏于是去新建一个「Steam 信息」对象
    //   5. 新建的过程中，它调用了一个 Steam 官方早就废弃的接口：
    //      RequestUGCDetails
    //   6. 这个废弃接口在现在这个版本的 steam_api64.dll 上会直接抛异常。
    //      异常被游戏的崩溃处理器抓到后，整个游戏进程就被杀掉了。
    //
    // 这四次崩溃的堆栈一模一样，所以这条调用链是确定的，不是猜的。
    //
    // 【CrossPromotion 到底藏在哪】
    //
    // 它并不在磁盘上那个 CrossPromotion.dll 里。Visual Exceptions、Achtung!、
    // Camera+ 这三个模组各自都带着一份 CrossPromotion.dll，而真正的代码是
    // 「压缩打包」在这份 dll 内部的资源里的，游戏运行起来之后才把它解出来、
    // 用 Assembly.Load 装进内存。
    //
    // 这就解释了为什么用工具直接看磁盘上那个 dll 时，根本找不到闯祸的那个方法，
    // 只能找到 Install 和构造函数。
    //
    // 【为什么不能「在下面几层打补丁」】
    //
    // 试过了，完全没用。原因有点反直觉，值得记下来：
    //
    //   Harmony 装补丁的方式，是把「方法的入口」换成自己的代码。
    //   但 Mono（游戏使用的 .NET 运行时）在编译时，会把短小的方法直接「抄写」
    //   到调用它的地方（这个动作叫「内联」），而且抄的是原始代码。
    //
    //   所以上面那条链被一层层抄完之后，真正在跑的代码里早就没有那三个方法了。
    //   我们替换入口的手脚再正确，也够不着已经抄进去的那份。
    //
    //   这是实际吃过亏才知道的：补丁表里明明写着「已安装 1 个前缀」，
    //   但游戏照崩不误，堆栈一字不差。
    //
    // 【那正确做法是什么】
    //
    // 既然下游够不着，就去动上游：把 CrossPromotion 装在 DoModInfo 上的
    // 那个补丁直接「摘掉」。Harmony 摘掉之后会重新生成一份调用代码，
    // 而新生成的代码里，那段会崩的调用压根就不存在了 ——
    // 不是「被跳过」，是「根本没有」。
    //
    // 这也是影响最小的做法。DoModInfo 是个两百行上下的大方法，负责画模组
    // 预览图、名称、作者、版本，以及最关键的「上传到创意工坊」按钮。
    // 把它整个禁用或替换掉是不能接受的。只摘掉这一个补丁的代价，
    // 仅仅是失去 brrainz 自带的那个「交叉推广」小面板。
    //
    // CrossPromotion 装的另外三个补丁（PostClose、
    // WorkshopItems_Notify_Subscribed、ModLister_RebuildModList）
    // 都不碰这条出事的路，所以原样留着不动。
    internal static class CrossPromotionUnpatch
    {
        private const string TypeName = "Brrainz.CrossPromotion";
        private const string PrefixName = "Page_ModsConfig_DoModInfo_Prefix";

        /// <summary>
        /// CrossPromotion 装在 DoModInfo 上的那个前缀是否已经摘干净。
        /// 摘干净之后（neutralized = true）所有重试路径都会立刻返回，不再做任何事。
        /// </summary>
        private static bool neutralized;

        /// <summary>
        /// 每帧那道的 UI 兜底钩子是否已经挂上去了。
        ///
        /// 它挂在 UIRoot_Entry.UIRootUpdate 与 UIRoot_Play.UIRootUpdate 上，
        /// 也就是**每帧各跑一次** —— 装重复了就是每帧多跑一遍，所以必须立这个标志。
        /// </summary>
        private static bool uiHookInstalled;

        /// <summary>
        /// UiRetryPostfix 出错时的日志去重键（Log.ErrorOnce 需要它）。
        /// 用它而不是 Log.Error：那里是每帧路径，真出事时不能每帧刷一条。
        /// 字母含义 "CPUR" = CrossPromotion Ui Retry。
        /// </summary>
        private const int UiRetryErrorKey = 0x43505552;

        /// <summary>
        /// TryNeutralize 已经试过几次了。
        ///
        /// 为什么必须设上限：下面的 UI 兜底钩子会一直尝试，而每次尝试都要遍历
        /// AppDomain 里全部已加载程序集。万一目标一直不出现（游戏版本变了、
        /// DoModInfo 改了名……），无限重试就是白白掉帧。
        ///
        /// 注意计数的粒度：UI 钩子是每帧调 UiRetryPostfix 的，但它每
        /// UiRetryIntervalFrames 帧才真正转调一次 TryNeutralize，
        /// 所以这个 60 对应的实际窗口是 60 × 30 帧 ≈ 30 秒 ——
        /// 够 CrossPromotion 那份运行时 Assembly.Load 出来的程序集从容挂上前缀。
        /// 试满这个次数就停手，只留一条说明。
        /// </summary>
        private static int neutralizeAttempts;

        /// <summary>重试上限。正常情况下一两次就成功了，这个数是给「一直失败」兜底的。</summary>
        private const int MaxNeutralizeAttempts = 60;

        /// <summary>
        /// UI 兜底钩子每隔多少帧才真正试一次。
        ///
        /// 为什么要隔：TryNeutralize 里那步「找 CrossPromotion」要遍历 AppDomain 里
        /// 全部已加载程序集、逐个 GetType，是重活，每帧都做会白白掉帧。
        /// 而可用的重试窗口又必须足够长（见 MaxNeutralizeAttempts），
        /// 所以改成「低频 + 多次」：30 帧一次 ≈ 0.5 秒一次。
        /// </summary>
        private const int UiRetryIntervalFrames = 30;

        /// <summary>UI 钩子的帧计数器（跨 UIRoot_Entry / UIRoot_Play 共用）。</summary>
        private static int uiRetryFrames;

        /// <summary>「已放弃」这件事只记一次，免得每帧都写。</summary>
        private static bool gaveUp;

        /// <summary>Log.ErrorOnce 的去重键：目标方法找不到。</summary>
        private const int ErrorKeyTargetMissing = 0x4C464301;

        /// <summary>Log.ErrorOnce 的去重键：摘除过程抛异常。</summary>
        private const int ErrorKeyUnpatchFailed = 0x4C464302;

        /// <summary>
        /// 是否已经「安排妥当」。
        ///
        /// 注意它不等于「已经发起过尝试」—— 只有「成功中立化」或者
        /// 「UI 兜底钩子确实装上了」之后才置位。否则首次尝试一旦恰好失败，
        /// 后面的重入路径就会被这个标记堵死，修复永久失效。
        /// （InstallUiRetryHook 内部自带 uiHookInstalled 守卫，重复调用本就幂等。）
        /// </summary>
        private static bool installDone;

        internal static void Install()
        {
            if (installDone)
            {
                // 已经安排妥当了，不用再来一遍 —— 否则会重复扫描程序集
                //（TryNeutralize 要遍历 AppDomain 里全部已加载程序集）、重复 Patch UI 钩子；
                // 若此刻正有长事件在跑，还会再往 LongEventHandler 里排一次队。
                return;
            }

            TryNeutralize("startup");

            if (!neutralized)
            {
                // 那个内嵌的小程序集此刻可能还没被加载出来，所以等游戏初始化流程跑完后再试一次。
                //
                // 注意：ExecuteWhenFinished 在没有长事件排队时是**同步立即执行**的
                //（它把委托塞进待执行列表后，若 currentEvent == null 就当场跑完）。
                // 所以这一次「重试」有可能立刻就执行结束了 —— 真正可靠的兜底是下面的 UI 钩子：
                // 它会在玩家点开模组页面之前，再给 TryNeutralize 一次机会。
                //
                // 外面套 try/catch 是必需的：如果这里抛出的异常漏了出去，
                // 会把整个修复补丁包的初始化一起搞挂。
                try
                {
                    LongEventHandler.ExecuteWhenFinished(delegate { TryNeutralize("whenFinished"); });
                }
                catch (Exception ex)
                {
                    Log.Error("[GNH LocalFixes] Could not queue the CrossPromotion retry: " + ex);
                }
            }

            if (!neutralized)
            {
                InstallUiRetryHook();
            }

            // 只有「确实有了出路」才算安排妥当：
            //   · 已经中立化（补丁摘掉了），或者
            //   · UI 兜底钩子装上了（它每帧都会给 TryNeutralize 一次机会）。
            // 两样都没成的话就把标记留着，下次重入时再安排一遍 ——
            // 这正是相比旧写法（一进来就置位）多出来的那点韧性。
            installDone = neutralized || uiHookInstalled;
        }

        internal static void TryNeutralize(string stage)
        {
            if (neutralized)
            {
                return;
            }

            // 试太多次还不行就停手（原因见 neutralizeAttempts 的说明）。
            // 这里用「先判断、后自增」，所以恰好允许试 MaxNeutralizeAttempts 次。
            if (neutralizeAttempts >= MaxNeutralizeAttempts)
            {
                if (!gaveUp)
                {
                    gaveUp = true;
                    Log.Error("[GNH LocalFixes] Gave up neutralizing the CrossPromotion prefix after "
                        + MaxNeutralizeAttempts + " attempts; the mods page may still crash. "
                        + "See the earlier log lines for the reason.");
                }
                return;
            }
            // 探针没找到（程序集还没解出来）也算一次尝试 —— 这没关系：
            // UI 侧已经是 30 帧一次的低频，不会像原来那样每帧烧掉一次机会。
            // 但**不要**把这行挪到探针之后：那样「程序集一直不出现」就会无限重试，
            // 正是要防的那个场景。
            neutralizeAttempts++;

            try
            {
                // 先用探针确认「那份内嵌的 CrossPromotion 已经被解出来」。
                // 返回 null 表示还没出来 —— 这时补丁表里当然也是空的，
                // 绝不能因此就判「已摘干净」，否则我们会提前收工、它稍后挂上就没人摘了。
                if (FindPrefixMethod() == null)
                {
                    return; // 内嵌程序集还没加载出来，留着等下一次机会
                }

                MethodBase target = AccessTools.Method(typeof(Page_ModsConfig), "DoModInfo");
                if (target == null)
                {
                    // 用 ErrorOnce：本方法可能被 UI 钩子每帧调用，普通 Error 会变成每秒几十条红字。
                    Log.ErrorOnce("[GNH LocalFixes] Page_ModsConfig.DoModInfo not found; "
                        + "cannot remove the CrossPromotion prefix.", ErrorKeyTargetMissing);
                    return;
                }

                // 反复「找 → 摘」，直到补丁表里再也数不到 CrossPromotion 的前缀为止。
                //
                // 为什么要循环：内存里可能同时存在多份 Brrainz.CrossPromotion
                //（Visual Exceptions / Achtung! / Camera+ 各自内嵌一份，运行时才 Assembly.Load 出来），
                // 每份都可能给 DoModInfo 装了前缀。只摘一个就判定成功的话，剩下的那个仍会让
                // 模组页面崩溃，而日志却显示「已摘除」—— 那比不修还糟。
                // 8 轮上限纯粹是防死循环。
                //
                // removed 记的是**实测差值**：每次摘之前、摘之后各数一遍补丁表，累加 before - after。
                // Harmony.Unpatch 不返回任何结果（内部就是 PatchInfo.RemovePatch，
                // 对不存在的补丁是静默 no-op），所以「这次到底摘掉几个」只能靠前后对比得出。
                // 早先写成 int removed = 1（凭假设 +1）会让日志虚高。
                int removed = 0;
                for (int round = 0; round < 8; round++)
                {
                    List<MethodInfo> victims = FindCrossPromotionPrefixes(target);
                    if (victims.Count == 0)
                    {
                        break; // 表里已经没有它了：摘干净了
                    }

                    int before = victims.Count;
                    for (int i = 0; i < victims.Count; i++)
                    {
                        // ⚠ 这一句是全项目最反直觉的地方：用**本模组自己的** Harmony 实例，
                        //    去摘掉**别人（CrossPromotion）**装上去的补丁。
                        //
                        //    它是可行的：Harmony 的 Unpatch **不检查补丁归属** —— 内部就是在
                        //    全局补丁表里按 PatchMethod 相等来删，跟调用者属于哪个 Harmony 实例
                        //    无关；对不存在的补丁则是静默 no-op。
                        //    换句话说，「谁装的」不影响我们摘，只要拿得到那个 Prefix 方法对象
                        //  （上面的 FindCrossPromotionPrefixes 就是从补丁表里把它捞出来的）。
                        LocalFixesMod.HarmonyInstance.Unpatch(target, victims[i]);
                    }
                    removed += before - FindCrossPromotionPrefixes(target).Count;
                }

                // 判定成功要同时满足两条，缺一不可：
                //   1. 这一次**真的摘掉过**至少一个（removed > 0）——
                //      否则可能是「程序集已加载、但它还没来得及给 DoModInfo 挂前缀」，
                //      此时表里本来就是空的，直接判成功会让我们提前收工，
                //      等它稍后挂上来就没人摘了。这种时候宁可保持 false，留到下次机会。
                //   2. 现在表里确实一个都不剩。
                neutralized = removed > 0 && FindCrossPromotionPrefixes(target).Count == 0;
                Log.Message("[GNH LocalFixes] Removed CrossPromotion's DoModInfo prefix (stage=" + stage
                    + "). Removed: " + removed + "; still present: " + (!neutralized) + ".");

                // 只在**真的成功**之后才撤钩。
                // 没摘到东西（上面的 removed == 0）时，这个钩子是唯一的重试机会，
                // 撤掉等于自断退路 —— 要等下一次 Mod 被重新构造才会再装上。
                if (neutralized)
                {
                    UninstallUiRetryHook();
                }
            }
            catch (Exception ex)
            {
                // 同样用 ErrorOnce：这是可能被每帧调用的路径。
                Log.ErrorOnce("[GNH LocalFixes] Failed to remove the CrossPromotion prefix (will retry): " + ex,
                    ErrorKeyUnpatchFailed);
            }
        }

        // 探针：内存里到底有没有「被解出来的那份」CrossPromotion。
        //
        // Visual Exceptions / Achtung! / Camera+ 各自内嵌一份 CrossPromotion.dll，
        // 真正的代码压在里面当资源，要等运行时 Assembly.Load 才存在 ——
        // 在那之前，任何程序集里都找不到 Brrainz.CrossPromotion 这个类型。
        // 所以「返回 null」的含义是「还没出来」，**不是**「补丁已经摘干净了」，
        // 绝不能拿它当成功判据（判据见 FindCrossPromotionPrefixes）。
        // 它只返回第一个命中的类型，因此也不能用来枚举「一共有几份」。
        //
        // 下面遍历所有已加载程序集、按类型全名找，就是为了回答这一个问题。
        private static MethodInfo FindPrefixMethod()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = null;
                try
                {
                    type = assemblies[i].GetType(TypeName, false);
                }
                catch (Exception)
                {
                    // 这个程序集加载不出来（比如是那种只有元数据的引用程序集），
                    // 直接跳过继续找下一个 —— 这里失败属于正常情况，不该刷日志。
                    continue;
                }

                if (type == null)
                {
                    continue;
                }

                MethodInfo method = type.GetMethod(PrefixName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        // 从 DoModInfo 的 Harmony 补丁表里，挑出**所有**「CrossPromotion 装上来的前缀」。
        //
        // 为什么不复用 FindPrefixMethod()：那个只会返回**第一份**同名类型的方法。
        // 内存里可能同时存在多份 Brrainz.CrossPromotion（Visual Exceptions / Achtung! /
        // Camera+ 各自内嵌一份，运行时才 Assembly.Load 出来），而 Unpatch 是按
        // PatchMethod 相等来删的 —— 拿第一份的方法去摘，第二份纹丝不动，
        // 判据却会认为「已经干净」，模组页面照样崩。
        //
        // 所以「到底还有几个」这件事，唯一可靠的真值来源就是补丁表本身：
        // 谁挂在 DoModInfo 上写得清清楚楚，按声明类型全名 + 方法名精确匹配即可。
        // 用 FullName 而不是 Name，避免误伤别的同名类型。
        private static List<MethodInfo> FindCrossPromotionPrefixes(MethodBase target)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null || info.Prefixes == null)
            {
                return found;
            }

            for (int i = 0; i < info.Prefixes.Count; i++)
            {
                Patch patch = info.Prefixes[i];
                MethodInfo method = patch?.PatchMethod;
                if (method == null)
                {
                    continue;
                }

                Type declaring = method.DeclaringType;
                if (declaring != null && declaring.FullName == TypeName && method.Name == PrefixName)
                {
                    found.Add(method);
                }
            }

            return found;
        }

        // 最后一道保险。
        //
        // 道理很朴素：玩家只能从界面上点开「模组」页面，
        // 所以我们只要挂在界面每帧的刷新上，就一定能在玩家点开之前再获得一次尝试机会。
        //
        // 一旦上面成功摘除了补丁，这个挂钩会被立刻撤掉（见 UninstallUiRetryHook）。
        private static void InstallUiRetryHook()
        {
            if (uiHookInstalled)
            {
                return;
            }

            try
            {
                HarmonyMethod postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(CrossPromotionUnpatch), nameof(UiRetryPostfix)));

                // 方法名是 UIRootUpdate，**不是** Update。
                //
                // 这两个类（Verse.UIRoot_Entry 和 RimWorld.UIRoot_Play）里都没有
                // 叫 Update 的方法 —— Unity 那个 Update 是「消息调用」，
                // 反射是找不到它的。写错了 AccessTools.Method 只会安静地返回 null，
                // 于是补丁一个都装不上，而下面还会照样把 uiHookInstalled 立起来，
                // 让这道保险永远不再重试。方法名已用反编译逐字核对。
                MethodInfo entry = AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate");
                MethodInfo play = AccessTools.Method(typeof(UIRoot_Play), "UIRootUpdate");

                if (entry == null && play == null)
                {
                    // 两个目标都没找到：把原因说清楚，并且**不置位** ——
                    // 留着下一次机会再试，总比悄悄放弃强。
                    Log.Error("[GNH LocalFixes] UI retry hook targets not found "
                        + "(UIRoot_Entry.UIRootUpdate / UIRoot_Play.UIRootUpdate); will retry later.");
                    return;
                }

                if (entry != null)
                {
                    LocalFixesMod.HarmonyInstance.Patch(entry, null, postfix, null, null);
                }
                if (play != null && play != entry)
                {
                    LocalFixesMod.HarmonyInstance.Patch(play, null, postfix, null, null);
                }

                uiHookInstalled = true;
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Could not install the UI retry hook: " + ex);
            }
        }

        private static void UninstallUiRetryHook()
        {
            if (!uiHookInstalled)
            {
                return;
            }

            // 只有**真的摘掉了**才允许把标记清掉。
            //
            // 为什么不能用 finally 无条件清（这是 2026-10-04 查出的一处隐患）：
            // 万一 Unpatch 抛异常，钩子其实还留在方法上，但标记已经被清成 false，
            // 于是下一次 InstallUiRetryHook 会**再装一个** postfix ——
            // Harmony 是叠加的，同一个方法上就变成了两层、三层，
            // 每次界面刷新都要多跑几遍。宁可标记一直留着（代价只是每帧一次
            // bool 判断），也不能让补丁层层叠加。
            bool removed = true;
            try
            {
                MethodInfo postfix = AccessTools.Method(typeof(CrossPromotionUnpatch), nameof(UiRetryPostfix));
                // 名字必须和安装时一致，都是 UIRootUpdate（原因见本文件上方那段说明：
                // 这两个界面根类根本没有 Update 方法，写错了反射只会安静地返回 null）。
                MethodInfo entry = AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate");
                MethodInfo play = AccessTools.Method(typeof(UIRoot_Play), "UIRootUpdate");
                if (entry != null)
                {
                    LocalFixesMod.HarmonyInstance.Unpatch(entry, postfix);
                }
                if (play != null && play != entry)
                {
                    LocalFixesMod.HarmonyInstance.Unpatch(play, postfix);
                }
            }
            catch (Exception ex)
            {
                removed = false;
                Log.Error("[GNH LocalFixes] Could not remove the UI retry hook; "
                    + "keeping the installed flag so we never stack a second one: " + ex);
            }
            finally
            {
                if (removed)
                {
                    uiHookInstalled = false;
                }
            }
        }

        /// <summary>
        /// 每帧的 UI 兜底钩子。
        ///
        /// 挂载点：Verse.UIRoot_Entry.UIRootUpdate 与 RimWorld.UIRoot_Play.UIRootUpdate
        /// —— 也就是说**每帧各执行一次**。
        ///
        /// ⚠ 本方法整体包着一层 try/catch，而且是本文件里唯一这么做的补丁入口。
        ///   原因就是「每帧」这两个字：一旦它抛异常，游戏的 UI 主循环每帧都会吃一条，
        ///   帧率会被日志直接拖垮 —— 而本补丁存在的意义恰恰是「别让游戏卡」。
        ///
        ///   稳定之后（neutralized = true）本方法只剩「读一个 bool 然后返回」，
        ///   但第一次成功中立化之前它会转调 TryNeutralize（要遍历 AppDomain 里的
        ///   全部程序集），那段路上任何意外都必须在这里被拦下。
        /// </summary>
        private static void UiRetryPostfix()
        {
            try
            {
                if (neutralized)
                {
                    return;
                }

                uiRetryFrames++;
                if (uiRetryFrames < UiRetryIntervalFrames)
                {
                    return;
                }
                uiRetryFrames = 0;
                TryNeutralize("ui");
            }
            catch (Exception ex)
            {
                // 吞掉，但绝不静默：用 ErrorOnce 保证「第一次出事」在日志里看得见，
                // 又不会因为每帧都出错而把日志刷爆。
                Log.ErrorOnce("[GNH LocalFixes] CrossPromotionUnpatch.UiRetryPostfix 出错"
                    + "（已忽略；这个方法每帧都会跑，绝不能让它往外抛）：" + ex, UiRetryErrorKey);
            }
        }
    }
}
