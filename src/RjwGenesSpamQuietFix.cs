using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：只要开着开发者模式（-debug），日志就会被同一句话刷爆：
    //
    //     [RJW-Genes] multipreg checks
    //     [RJW-Genes] multipreg checks
    //     ...（每帧若干条，实测一局 30 分钟刷了 31415 条，占整份日志的 65%）
    //
    // 刷屏的模组是「RJW 基因扩展 / RJW Genes」
    // （packageId = vegapnk.rjw.genes，本机路径 Mods\RJW_基因_核心，v2.7.0）。
    // ==========================================================================
    //
    // 【第一件事：它到底在哪一行打的】
    //
    // 该模组**自带源码**，直接读就能定位（不用猜）：
    //
    //     1.6\Source\Genes\Patches\MultiplePregnancies.cs 第 26 行
    //
    //     [HarmonyPostfix]
    //     public static void Postfix(ref bool __result, Pawn pawn, bool mustBeVisible)
    //     {
    //         bool isPregnant = __result;
    //         if (MultiPregnancy != null)
    //         {
    //             if (RJWSettings.DevMode) RJW_Genes.ModLog.Message("multipreg checks");   // ← 就是这一句
    //             if (isPregnant) { ... }
    //             __result = isPregnant;
    //         }
    //     }
    //
    // 它挂在 PawnExtensions.IsPregnant(Pawn, bool) 这个**高频方法**上 ——
    // 「这个角色怀孕了吗」在游戏里每帧都会被问很多次（每个角色、每个相关系统都问），
    // 而作者在这里留了一句**没有任何去重、没有任何节流**的调试输出，
    // 于是每问一次就往日志里写一行。这就是刷屏的全部原因。
    //
    // 【第二件事：它只在开发者模式下出现 —— 这一点很重要】
    //
    // 那句话外面包着 if (RJWSettings.DevMode)。
    // 也就是说：**正常游玩（不加 -debug 启动参数）时它一行都不会打。**
    //
    // 那为什么还要修？因为做深度测试必须开 -debug（否则看不到开发者面板、看不到
    // 异常堆栈），而一开 -debug 日志就被这 3 万行淹没 —— 真正要看的问题全被埋掉。
    // 另外它每写一行都要落盘一次 I/O，本身就是白白吃掉帧时间。
    //
    // 【第三件事：为什么改在这里，而不是改模组自己的 dll】
    //
    // 直接改别人的 dll 是不行的（会被创意工坊更新覆盖，也会破坏文件校验）。
    // 本补丁包一贯的做法是：用 Harmony 在**运行时**挂一层，不改磁盘上的任何文件。
    //
    // 【第四件事：本补丁做什么】
    //
    // 它 patch 的是这句日志最终要调用的那个方法：
    //
    //     RJW_Genes.ModLog.Message(string message)   ← 该模组自己的日志小工具
    //     {
    //         Log.Message($"[{ModId}] {message}");    // ModId 就是 "RJW-Genes"
    //     }
    //
    // 我们给它挂一个前缀：**当且仅当**传进来的字符串正好是 "multipreg checks" 时，
    // 返回 false 让原方法整个跳过 —— 那行字就不会被写出来。
    // 参数是别的任何内容时一律放行，原方法照常执行。
    //
    // 【为什么这是安全的】
    //
    // · 已用反编译器在整个 Rjw-Genes.dll 里搜过 "multipreg checks" 这个字符串常量：
    //   **全库只有一处**（就在上面那个 Postfix 里）。所以"正好匹配这个字符串"
    //   等价于"正好命中那一句调试输出"，不可能误伤别的日志。
    // · ModLog 类还有 Error / Warning / Debug 三个方法，本补丁**一个都不碰** ——
    //   该模组真正的报错和警告照常出现在日志里，一条都不会少。
    // · 同一个类里的 Message 方法也只拦这一个字符串，
    //   将来作者用 Message 输出别的内容，照样正常打印。
    // · 被拦掉的这句话**对游戏没有任何功能作用** —— 它不改变任何变量、
    //   不参与任何判断，纯粹是给开发者看的打印。少这一行，游戏行为一模一样。
    //
    // 【性能说明】
    //
    // 前缀里做的事只有一件：把传进来的字符串和 "multipreg checks" 比一下。
    // .NET 比较两个字符串时先比长度，长度不同立刻返回 —— 本机所有非刷屏调用
    // 都在这一步就结束了，代价是纳秒级的。
    //
    // 而它换掉的是**每条一次磁盘写入**（Log.Message 最终要落盘）。
    // 实测这一项一局就能省掉三万多次写日志，是"少花钱"而不是"多花钱"。
    //
    // 首次成功拦下时会打一条说明日志（只打一次，之后不再重复），
    // 这样你在日志里能一眼确认补丁确实生效了。
    // ==========================================================================
    internal static class RjwGenesSpamQuietFix
    {
        // 目标：RJW_Genes.ModLog（注意它是个 internal 类，所以只能靠反射拿）。
        private const string ModLogTypeName = "RJW_Genes.ModLog";

        // 目标方法：public static void Message(string message)
        private const string TargetMethodName = "Message";

        // 要拦下的那一句（必须与模组源码里的字面量**完全一致**，一个字符都不能差）。
        private const string SpamText = "multipreg checks";

        private static bool installed;

        // 一共拦掉多少条（只用来写日志，不参与逻辑判断）。
        private static int suppressedCount;

        // 是否已经打过"开始静默"的说明日志。
        // 这个方法每帧都会被调用，每次都打反而把日志刷满，所以只打第一条。
        private static bool announced;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type modLogType = AccessTools.TypeByName(ModLogTypeName);
                if (modLogType == null)
                {
                    // 找不到类型 = 模组没装，或作者改了命名空间。
                    // 这两种情况重试也没用，所以置位跳过（用 Message 而不是 Error：
                    // 没装这个模组是完全正常的状态，不该报红）。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + ModLogTypeName
                        + "（RJW 基因扩展未安装或已改版）；"
                        + "RJW-Genes 调试刷屏消音补丁已跳过。");
                    return;
                }

                MethodInfo target = FindMessageMethod(modLogType);
                if (target == null)
                {
                    // 类型在、方法找不到 → 多半是作者改了结构。不置位，留给下次重试；并且留 Error 线索。
                    Log.Error("[GNH LocalFixes] 在 " + ModLogTypeName + " 上找不到 "
                        + TargetMethodName + "(string) 方法；"
                        + "RJW-Genes 调试刷屏消音补丁本次未安装，下次会重试。");
                    return;
                }

                // Patch 的参数顺序是 (原方法, 前缀, 后缀, 转译器, 收尾器)。
                // 这里只需要前缀，其余三个传 null。
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(RjwGenesSpamQuietFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装 RJW-Genes 调试刷屏消音补丁"
                    + "（RJW_Genes.ModLog.Message）。"
                    + "该模组在「这个角色怀孕了吗」这个每帧都会问的高频方法里留了一句"
                    + "没有去重、没有节流的调试输出（开发者模式下每问一次写一行，"
                    + "实测一局刷了三万多行、占整份日志的 65%）。"
                    + "本补丁只拦【" + SpamText + "】这一个字符串，"
                    + "该模组的报错 / 警告 / 其它日志全部照常输出。"
                    + "那行字不参与任何游戏逻辑，屏蔽它对游戏行为毫无影响。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装 RJW-Genes 调试刷屏消音补丁失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 真正干活的前缀：发现是那句刷屏就返回 false，让原方法整个跳过。
        // ==================================================================
        //
        // 【为什么参数写成 __0 而不是 message】
        // Harmony 允许两种写法：
        //   · 按**参数名**绑定 —— 写 string message；
        //   · 按**位置**绑定 —— 写 string __0（第 0 个参数）。
        // 这里用位置绑定：万一作者哪天把参数名从 message 改成 msg，
        // 按名字写的前缀会直接挂不上（Harmony 会报错），按位置写的照常工作。
        //
        // 【返回值 false 的含义】
        // 前缀返回 false = 跳过原方法，什么也不做。原方法只是往日志写一行字，
        // 跳掉它没有任何副作用。
        public static bool Prefix(string __0)
        {
            // 绝大多数调用在这一行就结束了（长度不同直接返回 true 放行）。
            if (__0 != SpamText)
            {
                return true;
            }

            suppressedCount++;

            // 只在第一次拦截时说明一下，之后完全静默 —— 否则等于换个方式继续刷屏。
            if (!announced)
            {
                announced = true;
                Log.Message("[GNH LocalFixes] 已开始静默 RJW-Genes 的调试刷屏"
                    + "（RJW_Genes.ModLog.Message 收到【" + SpamText + "】时不再写日志）。"
                    + "这只影响开发者模式下的那一句调试输出，"
                    + "该模组的报错与警告一个都不会少。之后不再重复打印。");
            }

            return false;
        }

        // ==================================================================
        // 找到 public static void Message(string) 这个方法。
        // ==================================================================
        //
        // 不写死参数类型数组，而是按「名字叫 Message、且只收一个参数」去找：
        // 这样即使将来作者把参数类型从 string 换成别的（比如重载了一个带前缀的版本），
        // 也不会因为签名对不上而漏装。
        private static MethodInfo FindMessageMethod(Type modLogType)
        {
            MethodInfo[] methods = modLogType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != TargetMethodName)
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1)
                {
                    continue;
                }

                return m;
            }

            return null;
        }
    }
}
