using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：CCOE（RJW_补丁_CCOE，packageId keahuy.rjw.ccoe）
    // 有**三个** Harmony 前缀方法各自写了同一个有缺陷的表达式，在「目标 Pawn
    // 没有 Comp_SealCum」时抛 System.Reflection.TargetException:
    // Non-static field requires a target，导致月经结算相关流程失败并刷红字。
    //
    // ★ 2026-10-06 审计补齐。此前本补丁只修了第一个，另外两个照旧刷红字 ——
    //   实测日志（2026-10-06 12:49）里，本补丁自述「fix is working」的同一秒，
    //   紧接着就是一条来自 Patch_BeforeCumOut 的 TargetException。
    //
    // 三个方法（方法体各不相同，但开头取 cumSealed 那一句逐字相同，所以崩法一样）：
    //   * CCOE.Patch.ArchotechVaginaEnhance.Patch_CumOut
    //   * CCOE.Patch.MenstruationCycleNotSealedA.Patch_BeforeCumOut
    //   * CCOE.Patch.MenstruationCycleNotSealedC.Patch_AfterCumOut
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
    // ⚠ 本补丁**不在** LocalFixesMod 的构造函数里安装。
    //   （2026-10-07 订正：这里原先写着「在 LocalFixesMod 构造函数里通过 TryInstall 安装」，
    //     那是 2026-10-06 之前的旧路径，已经废弃。）
    //
    //   现在由本文件末尾的 CcoeReflectionFixLateInstaller 安装 —— 它带
    //   [StaticConstructorOnStartup]，执行时机是「所有 Def 加载完毕之后」。
    //
    //   为什么非挪不可：Harmony 打补丁这个动作会触发目标类型的静态构造，而这三个目标
    //   引用的 RJW_Menstruation.VariousDefOf 是个 [DefOf] 类，它的静态构造要查 Def 数据库。
    //   在「模组加载」阶段装（那时 Def 还没建好）会抛 NullReferenceException，
    //   实测 2026-10-06 14:22 的 TypeInitializationException 就是这么来的。
    //   完整推导见本文件末尾那段。
    //   （LocalFixesMod.cs 里也留了一处同样的说明，两处口径一致。）
    //
    // 没装 CCOE 时（AccessTools.TypeByName 返回 null）直接置位跳过，
    // 只留一条日志，不会报错、也不会反复重试 —— 不构成硬依赖。
    internal static class CcoeReflectionFix
    {
        // 三个目标的「类型全名 + 方法名」。
        private static readonly (string TypeName, string MethodName)[] Targets = new[]
        {
            ("CCOE.Patch.ArchotechVaginaEnhance", "Patch_CumOut"),
            ("CCOE.Patch.MenstruationCycleNotSealedA", "Patch_BeforeCumOut"),
            ("CCOE.Patch.MenstruationCycleNotSealedC", "Patch_AfterCumOut"),
        };

        private static bool installed;

        // 逐个目标记录「已经装好了」。
        //
        // 为什么不用一个总的 installed 就算完：CCOE 升级后可能只有个别方法改了名，
        // 那时我们希望「装好的不重装、没装上的下次再试」。Harmony 的补丁是**叠加**的，
        // 同一个方法装两次就会跑两遍 Finalizer —— 所以必须精确到单个目标。
        private static readonly bool[] targetDone = new bool[3];

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo finalizer = AccessTools.Method(typeof(CcoeReflectionFix), nameof(Finalizer), null, null);
                if (finalizer == null)
                {
                    Log.Error("[GNH LocalFixes] CCOE fix: could not find our own Finalizer method; skipped.");
                    return;
                }

                int typesFound = 0;
                int justPatched = 0;
                int missingMethods = 0;
                int failedTargets = 0;

                for (int i = 0; i < Targets.Length; i++)
                {
                    if (targetDone[i])
                    {
                        continue;                       // 这个目标上一轮就装好了，别重复装
                    }

                    Type type = AccessTools.TypeByName(Targets[i].TypeName);
                    if (type == null)
                    {
                        continue;                       // 整个类型都不在 -> CCOE 没装或版本不同
                    }
                    typesFound++;

                    MethodInfo target = AccessTools.Method(type, Targets[i].MethodName, null, null);
                    if (target == null)
                    {
                        // 类型在、方法却没了：多半是 CCOE 升级改了名。
                        // 不置 targetDone，留给下次重试；并且必须留 Error，绝不静默失败。
                        missingMethods++;
                        Log.Error("[GNH LocalFixes] CCOE was found but " + Targets[i].TypeName + "."
                            + Targets[i].MethodName + " is missing (mod updated?); that one is deferred and will retry.");
                        continue;
                    }

                    // ★ 每个目标各自 try/catch。
                    //
                    // 实测（2026-10-06 14:22，v1.3.17 上线当次）：给 Patch_BeforeCumOut 打补丁时，
                    // Harmony 会抛
                    //     TypeInitializationException: The type initializer for
                    //     'RJW_Menstruation.VariousDefOf' threw an exception.
                    //         ---> NullReferenceException  at RJW_Menstruation.VariousDefOf..cctor()
                    //         at HarmonyLib.PatchFunctions.UpdateWrapper
                    //         at HarmonyLib.Harmony.Patch
                    //         at GNH.LocalFixes.CcoeReflectionFix.Install
                    // 也就是说：**打补丁这个动作本身，会触发目标方法所在程序集里其它类型的静态构造**。
                    // VariousDefOf 是个 [DefOf] 类，它的 .cctor 要查 Def 数据库；
                    // 而当时还在「模组加载」阶段，Def 根本没建好，于是抛 NRE。
                    //
                    // 修法有两层，这里是第一层：**一个目标炸了不能连累其它目标** ——
                    // 尤其不能连累 Patch_CumOut（它本来一直装得好好的）。
                    // 第二层是安装时机，见文件末尾 CcoeReflectionFixLateInstaller。
                    try
                    {
                        LocalFixesMod.HarmonyInstance.Patch(
                            target,
                            null,
                            null,
                            null,
                            new HarmonyMethod(finalizer));

                        targetDone[i] = true;
                        justPatched++;
                    }
                    catch (Exception ex)
                    {
                        failedTargets++;
                        Log.Error("[GNH LocalFixes] CCOE fix: patching " + Targets[i].TypeName + "."
                            + Targets[i].MethodName + " failed; the other targets are unaffected: " + ex.Message);
                    }
                }

                if (typesFound == 0)
                {
                    // 一个类型都没找到：CCOE 没装。这是正常情况，
                    // 标记成「已处理」以免每次构造本模组对象都白找一遍。
                    installed = true;
                    Log.Message("[GNH LocalFixes] CCOE not detected; its cumSealed reflection fixes were skipped.");
                    return;
                }

                // 只有三个目标全部就位才算彻底完成；否则下次（热重载/切语言）还会再来补一次。
                installed = true;
                for (int i = 0; i < targetDone.Length; i++)
                {
                    if (!targetDone[i]) { installed = false; break; }
                }

                if (justPatched > 0)
                {
                    Log.Message("[GNH LocalFixes] Patched CCOE cumSealed reflection prefixes: "
                        + justPatched + " newly installed this pass, "
                        + (installed ? "all 3 targets are now covered. "
                                    : (missingMethods + " missing, " + failedTargets + " failed. "))
                        + "(they swallow TargetException when the pawn has no Comp_SealCum).");
                }
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

    // ==========================================================================
    // 安装时机：为什么这个补丁**不能**在 LocalFixesMod 的构造函数里装
    // ==========================================================================
    //
    // 实测日志（2026-10-06 14:22，v1.3.17 上线当次启动）：
    //
    //     ERROR [GNH LocalFixes] Failed to install the CCOE CumOut fix (will retry):
    //       System.TypeInitializationException: The type initializer for
    //       'RJW_Menstruation.VariousDefOf' threw an exception.
    //       ---> System.NullReferenceException
    //         at RJW_Menstruation.VariousDefOf..cctor()
    //         at HarmonyLib.PatchFunctions.UpdateWrapper (...)
    //         at HarmonyLib.Harmony.Patch (...)
    //         at GNH.LocalFixes.CcoeReflectionFix.Install ()
    //
    // 两个要点：
    //
    //   1. **Harmony 打补丁会触发目标程序集里其它类型的静态构造。**
    //      我们 patch 的是 CCOE 的方法，但被触发的是 RJW_Menstruation 里的 VariousDefOf。
    //
    //   2. **VariousDefOf 是个 [DefOf] 类**，它的静态构造函数要查 Def 数据库。
    //      而 `LocalFixesMod` 的构造函数跑在「模组加载」阶段 —— 那时 Def 还没建好，
    //      查不到就抛空引用。
    //
    // 顺带解释了一个看起来矛盾的现象：同样是 CCOE 的方法，Patch_CumOut 从构造函数里装
    // 一直没出事。区别在方法体 —— Patch_CumOut 用的是方法内的 DefDatabase 查询，
    // 而 Patch_BeforeCumOut 的方法体里有一句静态字段访问 `VariousDefOf.Hediff_ASA`。
    //
    // 修法：把安装推迟到 [StaticConstructorOnStartup]。
    // 该特性的执行时机是「所有 Def 加载完毕之后」（`Verse.StaticConstructorOnStartupUtility.CallAll`，
    // 已反编译确认它只被 PlayDataLoader 调用一次），那时 VariousDefOf 能正常初始化。
    // 本工程已有先例：VehicleFrameworkDebugFix 也是用这个特性装的。
    //
    // 注意：这个特性由 CLR 保证只跑一次，所以本类**不需要**额外守卫；
    // 而 CcoeReflectionFix.Install() 内部那个 `installed` 标记继续保留，
    // 是为了万一将来又从别处调它时不至于重复打补丁。
    [StaticConstructorOnStartup]
    internal static class CcoeReflectionFixLateInstaller
    {
        static CcoeReflectionFixLateInstaller()
        {
            CcoeReflectionFix.Install();
        }
    }
}
