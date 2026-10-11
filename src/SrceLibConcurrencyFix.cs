using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 第 25 项：修掉「雪兔系列」公共库 SRCELib 的并发缺陷。
    // ==========================================================================
    //
    // 【它是谁】
    //
    //   SRCELib.dll 由模组「雪兔商品扩展——花花兔子公司」
    //   （Snow Rabbit Commodities Expansion，packageId = SnowRabbit.CommodityExpansionPB，
    //     工坊 3748316169）提供，放在它的 Common\Assemblies\ 下 —— 也就是说这个模组
    //   既卖兔子商品，又充当整个雪兔系列的公共代码库（另一个 SRCE_FALib.dll 在
    //   Patch_SRFacialAnimation\Assemblies\ 下，是面部动画补丁库）。
    //
    // 【问题：两个静态共享容器，一把锁都没有】
    //
    //   SRCELib.HediffComp_ChangeBodyRatio 里有两个「全游戏共享」（static readonly）的缓存：
    //
    //       private static readonly Dictionary<Pawn, HediffComp_ChangeBodyRatio> activeComps;
    //       private static readonly HashSet<Pawn> pawnsWithoutComp;
    //
    //   而 TryGetComp() 会在「第一次遇到某个角色」时往它们里【写】：
    //
    //       activeComps[pawn] = c;        // 命中组件时
    //       pawnsWithoutComp.Add(pawn);   // 确认这个角色没有该组件时  ← 崩溃点
    //
    //   全程没有任何锁。麻烦在于这个方法挂在【渲染路径】上（见下），而渲染可能是并行的：
    //   两个线程同时走到 HashSet.Add 时，一个线程正在 Resize()（换底层数组），
    //   另一个线程还拿着旧数组的索引去访问 —— 于是
    //
    //       System.IndexOutOfRangeException: Index was outside the bounds of the array.
    //
    //   .NET 还会主动把这件事点破：
    //
    //       InvalidOperationException: Operations that change non-concurrent collections
    //       must have exclusive access. A concurrent update was performed on this
    //       collection and corrupted its state.
    //
    // 【为什么它会污染渲染路径】
    //
    //   SRCELib 自己挂了两个 Harmony Postfix（反编译核对）：
    //
    //       [HarmonyPatch(typeof(PawnRenderNodeWorker), "OffsetFor")]
    //       public static class Harmony_OffsetFor_ChangeBodyRatio      -> Postfix 调 TryGetComp
    //
    //       [HarmonyPatch(typeof(PawnRenderNodeWorker), "ScaleFor")]
    //       public static class Harmony_ScaleFor_ChangeBodyRatio       -> Postfix 也调 TryGetComp
    //
    //   两个 Postfix 都只对 Body / Head 两类渲染节点动手（自带廉价预筛），
    //   但每个可见角色每帧仍会走「Body + Head」x 2 个补丁点，量级是每分钟几万次。
    //   调用链（实测堆栈）：
    //
    //       DynamicDrawManager.DrawDynamicThings
    //         -> PawnRenderer.RenderPawnAt
    //           -> PawnRenderTree.ParallelPreDraw
    //             -> PawnRenderNodeWorker.OffsetFor
    //               -> SRCELib.Harmony_OffsetFor_ChangeBodyRatio.Postfix
    //                 -> HediffComp_ChangeBodyRatio.TryGetComp          <- 在这里写共享集合
    //
    //   另一条 UI 头像链（PawnRenderer.RenderCache）走同一段代码，所以连「渲染某人的头像」
    //   都可能触发。
    //
    // 【实测后果（2026-10-11）】
    //
    //   一局游戏日志被刷到 12.8 MB / 199,045 行，其中
    //       System.IndexOutOfRangeException   42,987 次
    //       IndexOutOfRangeException          28,655 次
    //   直接表现是「部分角色每次绘制都抛异常、屏幕上不显示」，另有 1,531 条
    //   "Exception drawing <角色名>"。
    //
    //   它的触发是【概率性】的：只在「某角色第一次进入渲染管线」那一瞬间，
    //   两个线程恰好同时决定要写它时才会撞上；一旦写成功，后续都走只读路径。
    //   所以同一批角色会反复炸（异常抛出时 Add 没完成，下次还会重试），
    //   而换一局、换一批角色就可能完全安静 —— 这就是它看起来「时有时无」的原因。
    //
    // 【修法：在外面套一把锁，不动它的实现】
    //
    //   Prefix 进锁、Finalizer 出锁：
    //
    //     - 用 Finalizer 而不是 Postfix 释放锁 —— Harmony 会把 Finalizer 包进
    //       try/finally，所以【无论原方法是正常返回还是抛异常都一定会执行】。
    //       若用 Postfix，原方法一抛异常锁就永远不释放，会变成死锁，比原来的 bug 更糟。
    //     - Finalizer 里先判 Monitor.IsEntered 再释放，防止「Prefix 自身异常导致
    //       没进锁却要出锁」时抛 SynchronizationLockException 把真实异常盖掉。
    //     - 临界区极短（几次哈希查找），而且这段代码【读极多、写极少】，
    //       锁竞争接近于零，对帧率的影响远小于「每帧抛几千个异常」。
    //
    // 【为什么不用反射替换它的实现】
    //
    //   那样需要按类型操作 HediffComp_ChangeBodyRatio 的成员，等于要编译期引用
    //   SRCELib.dll —— 本补丁包会给 SRCELib 带来硬依赖，违背「谁不在场都不影响别人」
    //   的原则。套锁方案【零类型依赖】：只用 TypeByName 找方法，找不到就不装。
    //
    // 【安全设计】
    //
    //   1. 找不到 SRCELib（没装 / 改了名字 / 改了签名）-> 静默跳过，不报错、不影响其它修复；
    //   2. installed 静态守卫 —— 本工程的纪律：补丁安装必须幂等
    //      （LoadedModManager.CreateModClasses 一局内会被多次调用）；
    //   3. 全程 try/catch，失败只留一条 Warning，绝不因为本补丁让游戏启动失败；
    //   4. 只影响这一个方法，不碰它的任何其它行为 —— 组件功能（身体/头部偏移与缩放）
    //      完全不受影响，只是不再并发写坏集合。
    // ==========================================================================
    public static class SrceLibConcurrencyFix
    {
        /// <summary>补丁目标类型名（用字符串而不是 typeof：见文件头「为什么不用反射替换它的实现」）。</summary>
        private const string TargetTypeName = "SRCELib.HediffComp_ChangeBodyRatio";

        /// <summary>补丁目标方法名。</summary>
        private const string TargetMethodName = "TryGetComp";

        /// <summary>本补丁是否已经装过。Harmony 补丁是叠加的，装两次会对同一方法套两层锁。</summary>
        private static bool installed;

        /// <summary>
        /// 全局唯一的一把锁，只用来保护 SRCELib 那两个静态容器。
        ///
        /// 用 private static readonly object 而不是 lock(typeof(XXX)) / lock(this)：
        /// 后两者会跟别的模组（甚至原模组自己）共用同一把锁对象，容易变成互相等待。
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>
        /// 安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用
        ///（与自己写的 installed 守卫是双保险：外层保证不重复调用，内层保证即使被重复调用也安全）。
        /// </summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type type = AccessTools.TypeByName(TargetTypeName);
                if (type == null)
                {
                    // 没装 SRCELib —— 这是正常情况，不是错误。
                    // 置位 installed 可以省掉后续每次的 TypeByName 查找。
                    installed = true;
                    Log.Message("[GNH LocalFixes] SRCELib not present; its concurrency fix was skipped"
                        + " (nothing to do, other fixes are unaffected).");
                    return;
                }

                MethodInfo target = AccessTools.Method(type, TargetMethodName);
                if (target == null)
                {
                    // 装了 SRCELib 却没有这个方法 —— 多半是它改版了。
                    // 不置位 installed：留给下次重试的机会；同时必须留 Error 线索，
                    // 否则会变成「说好的修复呢」却查无实据。
                    Log.Error("[GNH LocalFixes] SRCELib is present but "
                        + TargetTypeName + "." + TargetMethodName
                        + " was not found (its API probably changed); the concurrency fix is deferred and will retry.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(SrceLibConcurrencyFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    // 收尾器负责出锁：它被 Harmony 包在 try/finally 里，
                    // 所以原方法正常返回或抛异常都一定会执行。
                    new HarmonyMethod(AccessTools.Method(typeof(SrceLibConcurrencyFix), nameof(Finalizer), null, null)));

                installed = true;

                // 装完读回补丁表：这是「装上了」与「装对了」之间的那次验证。
                // 本项目在别处吃过亏 —— 补丁表里有，不等于运行时真的被调用。
                Patches info = Harmony.GetPatchInfo(target);
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " with a lock (prefixes=" + (info != null ? info.Prefixes.Count : -1)
                    + ", finalizers=" + (info != null ? info.Finalizers.Count : -1) + ")."
                    + " This stops its static caches from being written concurrently during rendering,"
                    + " which used to flood the log with IndexOutOfRangeException.");
            }
            catch (Exception ex)
            {
                // 装不上不影响游戏本体，也不影响其它修复项，但必须留痕。
                Log.Warning("[GNH LocalFixes] Failed to install the SRCELib concurrency fix"
                    + " (that fix is NOT active this session; everything else is unaffected): " + ex);
            }
        }

        /// <summary>
        /// 进锁。不接触任何 SRCELib 类型，所以本类不会给补丁包引入对它的编译期依赖。
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix()
        {
            Monitor.Enter(Gate);
        }

        /// <summary>
        /// 出锁。
        ///
        /// 这里先判断 Monitor.IsEntered，而不是直接 Exit：万一 Prefix 在 Enter 之前
        /// 就抛了异常（理论上不会，但这是防御性写法），直接 Exit 会抛
        /// SynchronizationLockException，把真正的异常信息盖掉，反而更难排查。
        /// Monitor 是「线程亲和」的 —— 同一个线程进锁就必须由同一个线程出锁，
        /// 而 Harmony 保证 Prefix 与 Finalizer 跑在同一个线程上，所以这样配对是安全的。
        /// </summary>
        [HarmonyFinalizer]
        public static void Finalizer()
        {
            if (Monitor.IsEntered(Gate))
            {
                Monitor.Exit(Gate);
            }
        }
    }
}
