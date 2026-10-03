using System;
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

        private static bool neutralized;
        private static bool uiHookInstalled;

        // 是否已经「安装」过（本方法可能被重复调用，比如模组类被重新构造时）。
        private static bool installCalled;

        internal static void Install()
        {
            if (installCalled)
            {
                // 已经安排过了，不用再来一遍 —— 否则会重复往 LongEventHandler 里排队。
                return;
            }
            installCalled = true;

            TryNeutralize("startup");

            if (!neutralized)
            {
                // 那个内嵌的小程序集此刻可能还没被加载出来，所以等游戏初始化流程跑完后再试一次。
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
        }

        internal static void TryNeutralize(string stage)
        {
            if (neutralized)
            {
                return;
            }

            try
            {
                MethodInfo prefix = FindPrefixMethod();
                if (prefix == null)
                {
                    return; // 内嵌程序集还没加载出来，留着等下一次机会
                }

                MethodBase target = AccessTools.Method(typeof(Page_ModsConfig), "DoModInfo");
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] Page_ModsConfig.DoModInfo not found; cannot remove the CrossPromotion prefix.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Unpatch(target, prefix);

                // 这一版 Harmony 的 Unpatch 返回 void，问不出「到底摘掉没有」。
                // 那就自己查：把目标方法上的补丁表读出来，看还剩几个前缀。
                // 还剩着就说明没摘干净，保持 neutralized = false 继续重试；
                // 一个都不剩才算真的办妥了。
                // 判定「摘干净了没有」，只能盯住**我们要摘的那一个补丁方法**，
                // 绝不能看 DoModInfo 上的前缀总数 —— 别的模组也可能给同一个方法挂前缀，
                // 那样总数永远不为 0，就会一直判定「没摘干净」：UI 钩子常驻下来，
                // 每帧遍历一次全部程序集、每帧写一条日志，白白掉帧还刷屏。
                Patches info = Harmony.GetPatchInfo(target);
                bool stillThere = false;
                if (info != null && info.Prefixes != null)
                {
                    for (int i = 0; i < info.Prefixes.Count; i++)
                    {
                        if (info.Prefixes[i].PatchMethod == prefix)
                        {
                            stillThere = true;
                            break;
                        }
                    }
                }
                neutralized = !stillThere;
                Log.Message("[GNH LocalFixes] Removed CrossPromotion's DoModInfo prefix (stage=" + stage + ")."
                    + " Still present: " + stillThere + ".");

                UninstallUiRetryHook();
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to remove the CrossPromotion prefix (will retry): " + ex);
            }
        }

        // 遍历当前已加载的所有程序集，找出那个真正带着补丁的类型。
        //
        // 为什么要挨个找：Visual Exceptions、Achtung!、Camera+ 各自都打包了一份
        // 同名的 CrossPromotion，而只有「运行时从资源里解出来」的那一份，
        // 才带有我们要找的那个方法。
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
                Log.Error("[GNH LocalFixes] Could not remove the UI retry hook: " + ex);
            }
            finally
            {
                uiHookInstalled = false;
            }
        }

        private static void UiRetryPostfix()
        {
            TryNeutralize("ui");
        }
    }
}
