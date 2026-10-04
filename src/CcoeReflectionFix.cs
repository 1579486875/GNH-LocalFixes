using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：CCOE（RJW_补丁_CCOE，packageId keahuy.rjw.ccoe）
    // 的 CumOut 前缀在「目标 Pawn 没有 Comp_SealCum」时抛
    // TargetException: Non-static field requires a target，
    // 导致每次月经结算都失败并刷一条红字。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // CCOE 的源码（Patch/ArchotechVaginaEnhance.cs）只有不到 20 行，核心是这一句：
    //
    //     bool isSealed = (bool)(typeof(Comp_SealCum)
    //         .GetField("cumSealed", AccessTools.all)
    //         ?.GetValue(__instance.Pawn.GetComp<Comp_SealCum>()) ?? false);
    //
    // 作者想表达的意思很清楚：「取不到值就当 false」。
    // 但那个 ?. 保护错了对象 —— 它保护的是 GetField(...) 的返回值，
    // 而 GetField 只要字段名对就**永远不返回 null**，所以 ?. 形同虚设。
    // 真正可能为 null 的是 GetValue 的**参数**：
    // 当 Pawn 身上没有 Comp_SealCum 时，GetComp<Comp_SealCum>() 返回 null，
    // 而 FieldInfo.GetValue(null) 去读一个**实例字段**，运行库会直接抛
    //
    //     System.Reflection.TargetException: Non-static field requires a target
    //
    // 注意这里 ?? false 也救不了场：?? 只处理「值为 null」，
    // 而这里是**抛异常**，控制流根本走不到 ?? 那一步。
    //
    // 【什么时候 GetComp 会是 null】
    //
    // Comp_SealCum 由谁提供：
    //   * 人类等常规种族：Cumpilation 通过 XML 给它们的 ThingDef 挂上 CompProperties_SealCum；
    //   * 机械体：CCOE 自己的 MechanoidHasCompSealed（[StaticConstructorOnStartup]）
    //     遍历 race.IsMechanoid 的 ThingDef 补上。
    //
    // 而 Anomaly DLC 的肉兽（Fleshbeast，例如硬棘兽 Toughspike）、
    // 各类异星种族，两边都不沾 —— 它们既不是人类也不是机械体，
    // 于是 GetComp<Comp_SealCum>() 返回 null，正好踩中上面那条异常路径。
    //
    // 实测日志（2026-10-04）：
    //     Error processing womb of Toughspike1986: System.Reflection.TargetException:
    //     Non-static field requires a target
    //         at System.Reflection.RuntimeFieldInfo.GetValue (System.Object obj)
    //         at CCOE.Patch.ArchotechVaginaEnhance.Patch_CumOut (...)
    //         at RJW_Menstruation.HediffComp_Menstruation.CumOut ()
    //
    // 【为什么用 Finalizer，而不是把原方法整个顶掉】
    //
    // 最省事的写法是「Prefix 抢先执行 + 自己算一遍 + return false 顶掉原方法」，
    // 但那要求我们重新实现作者那段判断，还得引用 RJW / Cumpilation 的类型，
    // 会让本程序集对这些模组产生**编译期依赖**（它们没装就编不过、
    // 或者加载时类型解析失败）—— 这正是我们要避免的。
    //
    // Finalizer 只在**原方法已经抛异常**时才被调用，
    // 正常路径一行代码都不经过，所以对原作者逻辑的干扰是零。
    //
    // 【为什么固定返回 true】
    //
    // Patch_CumOut 是 CumOut 的 [HarmonyPrefix]，返回 true = 放行原方法。
    // 作者的原始表达式是：
    //
    //     return !有超凡阴道 || !isSealed;
    //
    // 我们接管的是「GetComp 为 null」这一条路径，而按作者的 ?? false 写法，
    // 这条路径下 isSealed 应当等于 false，于是：
    //
    //     !isSealed  =>  !false  =>  true
    //
    // 整个表达式因「或」运算恒为 true。也就是说：
    // 没有封堵布尔组件时，就当作「没有开启封堵」，正常放行 CumOut。
    // 这与作者本意完全一致，不需要重新计算任何东西。
    //
    // 【安装时机与探测】
    //
    // 在 LocalFixesMod 构造函数里通过 TryInstall 安装。
    // 没装 CCOE 时（AccessTools.TypeByName 返回 null）直接置位跳过，
    // 只留一条日志，不会报错、也不会反复重试 —— 不构成硬依赖。
    internal static class CcoeReflectionFix
    {
        private const string PatchTypeName = "CCOE.Patch.ArchotechVaginaEnhance";
        private const string TargetMethodName = "Patch_CumOut";

        private static bool installed;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type type = AccessTools.TypeByName(PatchTypeName);
                if (type == null)
                {
                    // 没装 CCOE：这是正常情况，标记为「已处理」以免每次构造都重试。
                    installed = true;
                    Log.Message("[GNH LocalFixes] CCOE not detected; its CumOut reflection fix was skipped.");
                    return;
                }

                MethodInfo target = AccessTools.Method(type, TargetMethodName, null, null);
                if (target == null)
                {
                    // 装了 CCOE 但方法名变了：**不置位**，留给下次再试，
                    // 并打 Error —— 静默失败是我们最不想要的。
                    Log.Error("[GNH LocalFixes] CCOE was found but "
                        + PatchTypeName + "." + TargetMethodName
                        + " is missing (mod updated?); fix deferred and will retry.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    null,
                    null,
                    null,
                    new HarmonyMethod(AccessTools.Method(typeof(CcoeReflectionFix), nameof(Finalizer), null, null)));

                installed = true;
                Log.Message("[GNH LocalFixes] Patched CCOE " + TargetMethodName
                    + " (swallows TargetException when the pawn has no Comp_SealCum).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the CCOE CumOut fix (will retry): " + ex);
            }
        }

        // Harmony 的 Finalizer 约定：
        //   * __exception 是原方法抛出的异常（没有异常时为 null）；
        //   * 返回 null 表示「异常已被处理，不要再往外抛」；
        //   * 返回非 null 表示「把异常原样继续抛出去」。
        //
        // __result 是原方法的返回值（这里就是 Patch_CumOut 的 bool）。
        // Finalizer 吞掉异常后，Harmony 会拿 __result 当作该方法的返回值，
        // 所以我们必须显式写进去 —— 否则 bool 的默认值是 false，
        // 那就变成「阻止 CumOut 执行」，与作者本意相反了。
        //
        // 只接管 TargetException：其他任何异常都原样抛回去，
        // 绝不掩盖 CCOE 或 RJW 将来出现的其它问题。
        // 只吞第一次，并留下一条日志。
        //
        // 为什么要打这条日志（2026-10-05 复审建议）：这个 Finalizer 生效与否，
        // 受 Mono 的 JIT **内联**影响 —— 也就是说「补丁表里明明装着，运行时却没被调用」
        // 这种事真的发生过（本项目的 CrossPromotion 那场事故就是这么来的）。
        // 而这个 Finalizer 一不生效就完全静默：红字继续刷，没人知道补丁到底有没有工作。
        // 打一条 Message 的成本是「一局一次」，换来的是实机可验证 —— 非常划算。
        private static bool swallowLogged;

        public static Exception Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception is TargetException)
            {
                // 见文件头推导：没有 Comp_SealCum => isSealed 视为 false => 放行。
                __result = true;

                if (!swallowLogged)
                {
                    swallowLogged = true;
                    Log.Message("[GNH LocalFixes] CCOE CumOut fix is working: swallowed a "
                        + "TargetException from Patch_CumOut (this message is only shown once per session).");
                }

                return null;
            }
            return __exception;
        }
    }
}
