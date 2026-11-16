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
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【】RavenRace 自己那层「总权重 0」—— 绕过 TechHediffsZeroBudgetFix 的兜底
    // ==========================================================================
    //
    // 【症状】（2026-11-16 11:59 那一局，日志原文）
    //
    //     12:46:27.010 ERROR RandomElementByWeight with totalWeight=0 - use TryRandomElementByWeight.
    //         at RavenRace.Features.Bionics.Patch_FusangFluidImplantPreference.ChooseImplant(
    //              IEnumerable`1 source, Func`2 weight, Pawn pawn, Single money)
    //         at .RimWorld.PawnTechHediffsGenerator.GenerateTechHediffsFor_Patch2(Pawn pawn)
    //         at .Verse.PawnGenerator.GenerateInitialHediffs_Patch2(…)
    //         at VFEEmpire.WorldComponent_Hierarchy.MakePawnFor(RoyalTitleDef title)
    //
    // 【为什么 TechHediffsZeroBudgetFix 没拦住】
    //
    //   那一项只把 Finalizer 挂在 `PawnTechHediffsGenerator.GenerateTechHediffsFor` 上。
    //   但 RavenRace 用 Transpiler 把原版那一行
    //
    //       ThingDef thingDef = source.RandomElementByWeight(w => w.BaseMarketValue);
    //
    //   换成了自己的 `ChooseImplant(source, weight, pawn, money)`。
    //   而 `ChooseImplant` 的**最后一行**仍然是
    //
    //       return GenCollection.RandomElementByWeight<ThingDef>(source, weight);
    //
    //   （反编译 `ZuoYao_RavenRace.dll` 1.7 版实证，方法 ID 06002E91）
    //
    //   ⇒ 原版那个「总权重 0 时打 ERROR 返回 null」的行为被**完整保留进了 ChooseImplant**。
    //     异常不再从 GenerateTechHediffsFor 里抛，所以我们的 Finalizer 看不到它；
    //     而 ChooseImplant 是 `return` 直接返回，**它自己不解引用**，因此这里不会崩，
    //     只会每次帝国头衔生成时打一条 ERROR，并让那只 pawn 一个植入体都不装。
    //
    // 【修法：在 ChooseImplant 前面拦一次「总权重为 0」】
    //
    //   给 `ChooseImplant` 加 Prefix：
    //     · 先遍历一遍 source，把 weight 累加；
    //     · 总和 <= 0 → `__result = null; return false;`（跳过原方法）。
    //
    //   **语义完全等价**：原版 `RandomElementByWeight` 在总和为 0 时的返回值就是
    //   `default(T)`（引用类型即 null），我们提前返回 null，调用方拿到的东西一模一样 ——
    //   区别只是不再打那条 ERROR。
    //
    // 【为什么 Prefix 里多遍历一次 source 是安全的】
    //
    //   `source` 在调用点是一个 LINQ 查询（`DefDatabase<ThingDef>.AllDefs.Where(...)`），
    //   每次枚举都会重新求值。它只读 Def 数据库、不消耗 Rand、没有副作用，
    //   所以多枚举一次的结果与第一次完全相同。
    //   （⚠ 如果将来作者把 source 改成「一次性迭代器」，那也只会在「该放行时」多跑一次空枚举，
    //     仍然不会出错 —— 因为我们在总和 > 0 时**原样放行**，原方法还会自己枚举一遍。）
    //
    //   代价：每次调用多一次全表遍历。这个调用只发生在 pawn 生成时，
    //   且 TechHediffsZeroBudgetFix 里已经缓存了「正市值 tech hediff 清单」，
    //   本项没有再做缓存 —— 因为 source 是调用方给的、带过滤条件的，无法预先算。
    //   实测这一局的 ERROR 只有 1 条，说明调用频次极低。
    //
    // 【为什么用 Prefix 而不是 Finalizer】
    //
    //   Finalizer 只能「事后吞异常」，而这里**没有异常** —— `RandomElementByWeight`
    //   是打 ERROR 后正常返回 null。想让那条 ERROR 不出现，只能在它**被调用之前**拦住。
    //
    // 【自动让路】
    //
    //   · 模组没装（或作者改了实现、方法没了）→ 装不上 → 打一行 Message，不是 Error；
    //   · 作者将来自己改用 TryRandomElementByWeight → 我们的 Prefix 仍会先跑，
    //     总和 > 0 时照常放行，行为不变；
    //   · weight 为 null（不该发生）→ 放行原方法，交给它自己报错；
    //   · 累加过程中抛异常 → 放行原方法，绝不因为我们的判断改变游戏行为；
    //   · 玩家关掉开关 → 整项不安装。
    // ==========================================================================

    /// <summary>
    /// 给 `RavenRace.Features.Bionics.Patch_FusangFluidImplantPreference.ChooseImplant`
    /// 加一道「总权重为 0 就提前返回 null」的 Prefix，消掉那条每次头衔生成都会刷的 ERROR。
    /// </summary>
    internal static class RavenChooseImplantZeroWeightFix
    {
        /// <summary>补丁类完整名（工坊模组内部类型，只能按字符串找）。</summary>
        private const string TargetTypeName =
            "RavenRace.Features.Bionics.Patch_FusangFluidImplantPreference";

        /// <summary>目标方法名。</summary>
        private const string TargetMethodName = "ChooseImplant";

        /// <summary>本补丁是否已装。</summary>
        private static bool installed;

        /// <summary>目标 Type 缓存。</summary>
        private static Type targetType;

        /// <summary>目标 Type 是否解析过。</summary>
        private static bool typeResolved;

        /// <summary>跳过次数（只记日志用，不刷屏）。</summary>
        private static int skippedCount;

        /// <summary>是否已经为「跳过」打过一次说明日志。</summary>
        private static bool skipLogged;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type t = ResolveTargetType();
                if (t == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] RavenRace's Fusang implant patch is not present;"
                        + " its zero-weight guard was skipped (nothing to do).");
                    return;
                }

                // 签名：ThingDef ChooseImplant(IEnumerable<ThingDef>, Func<ThingDef,float>, Pawn, float)
                MethodInfo target = AccessTools.Method(
                    t, TargetMethodName,
                    new[]
                    {
                        typeof(IEnumerable<ThingDef>),
                        typeof(Func<ThingDef, float>),
                        typeof(Pawn),
                        typeof(float),
                    },
                    null);

                if (target == null)
                {
                    // 方法在但签名变了：作者改过实现。不置位，留待下次重试。
                    Log.Message("[GNH LocalFixes] " + TargetTypeName + "." + TargetMethodName
                        + " exists but its signature changed; the zero-weight guard was skipped"
                        + " (this only removes one redundant error line).");
                    installed = true;
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(RavenChooseImplantZeroWeightFix), nameof(Prefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] internal error: Raven zero-weight Prefix not found.");
                    return;
                }

                new Harmony("GNH.LocalFixes.RavenChooseImplantZeroWeight")
                    .Patch(target, prefix: new HarmonyMethod(prefix));

                installed = true;
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " (zero-total-weight now returns null quietly instead of logging an error).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the Raven zero-weight guard: " + ex);
            }
        }

        /// <summary>解析目标类型；找不到就是模组没装。</summary>
        private static Type ResolveTargetType()
        {
            if (typeResolved)
            {
                return targetType;
            }

            typeResolved = true;
            try
            {
                targetType = AccessTools.TypeByName(TargetTypeName);
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] looking up " + TargetTypeName + " threw: " + ex.Message);
                targetType = null;
            }

            return targetType;
        }

        /// <summary>
        /// 返回 false 表示跳过原方法（此时 <paramref name="__result"/> 已被我们设成 null）。
        /// 返回 true 表示放行，原方法照常跑。
        ///
        /// 任何意外都返回 true —— 我们的判断绝不该改变游戏行为。
        /// </summary>
        public static bool Prefix(
            IEnumerable<ThingDef> source,
            Func<ThingDef, float> weight,
            ref ThingDef __result)
        {
            try
            {
                // 参数不完整就放行，让原方法自己去报错（那是它该做的）。
                if (source == null || weight == null)
                {
                    return true;
                }

                // 判据其实只有一句：**有没有任何一个候选的权重大于 0**。
                //
                // 为什么这一个条件就够了：GenCollection.RandomElementByWeight 的行为是
                //   · 先累加所有「正权重」得到 total；
                //   · total <= 0f → Log.Error + return default(T)；
                //   · 否则按权重抽签。
                // 所以「存在一个正权重」与「total > 0」完全等价，而这个判断可以**遇到第一个就返回**，
                // 不必把整个 source 走完 —— 大列表（本机上万条 ThingDef）时能省下绝大部分开销。
                //
                // 负数/NaN 权重一律不算：RandomElementByWeight 本身也只累加正数，
                // 而 NaN > 0f 恒为 false，正好被这条比下去。
                foreach (ThingDef def in source)
                {
                    if (def == null)
                    {
                        continue;
                    }
                    if (weight(def) > 0f)
                    {
                        return true;   // 有能抽中的候选 → 原方法照常跑
                    }
                }

                // 走到这里：一个正权重都没有。原方法这一趟必然打 ERROR 并返回 null。
                // 我们直接返回 null，等价于「这次抽不中，不装这个植入体」。
                __result = null;
                skippedCount++;
                if (!skipLogged)
                {
                    skipLogged = true;
                    Log.Message("[GNH LocalFixes] RavenRace's implant chooser had a zero total weight"
                        + " (no candidate with a positive market value fit the budget);"
                        + " returned null quietly — this is the same outcome as the engine's"
                        + " TryRandomElementByWeight failure, minus the error line."
                        + " Further occurrences are counted silently.");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Raven zero-weight check failed; letting the original"
                    + " chooser run: " + ex.Message);
                return true;
            }
        }

        /// <summary>累计跳过次数（供自检/文档引用）。</summary>
        internal static int SkippedCount
        {
            get { return skippedCount; }
        }
    }
}
