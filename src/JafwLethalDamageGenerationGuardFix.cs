// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【61】「Just A Flesh Wound」把「装义体造成的 MissingBodyPart」当成致命伤，
    //       导致 UFC 派系的领袖每次世界生成都必死 —— 720 条红字的**真正最后一环**
    // ==========================================================================
    //
    // 【症状回顾】
    //
    //   世界生成时 `Faction.TryGenerateNewLeader` 为「联合水果优品公司」（UFC）生成 `UFC_BOSS`，
    //   连生 120 次全部失败，最后 `return null` —— 那个派系开局就没有领袖。
    //   连带刷出：`Generated dead pawn` ×3、`Could not generate a pawn after N tries` ×2，
    //   以及三处 `Pawn.Kill` 补丁各打一行「Could not find player faction.」（合计 720 条）。
    //
    // 【取证过程（1.7 实机，2026-11-13，日志 Player.log.probe-v2-numbers）】
    //
    //   本项目自带的只读取证探头（【54】）在世界生成期抓到 UFC_BOSS 死亡瞬间的完整状态：
    //
    //       hediffs (40):
    //         BionicArm ×2（左肩/右肩）、BionicLeg ×2（左腿/右腿）—— Hediff_AddedPart
    //         其余 36 条全是 Hediff_MissingPart（锁骨/臂/肱骨/桡骨/手/五指/股骨/胫骨/足/五趾…）
    //       verdicts:
    //         HasPreventsDeath = False
    //         CauseDeathNow    = (none of 40 hediffs)
    //         RequiredCapacity = (none)
    //         capacity: Consciousness/Breathing/BloodFiltration/BloodPumping/Metabolism 全 = 1
    //         CorePartEfficiency = 1
    //         LethalDamageThreshold = True     ← 唯一成立的判据
    //         HealthScale = 1   LethalDamageThresholdV = 150   InjurySum = 0 (count=0)
    //
    //   **矛盾点**：原版 `ShouldBeDeadFromLethalDamageThreshold` 只累加 `is Hediff_Injury`
    //   的 Severity（反编译 IL 62 条指令逐条核对过），而 `Hediff_MissingPart` 的基类是
    //   `HediffWithComps`（不是 `Hediff_Injury`），InjurySum 实测 = 0、阈值 = 150 ——
    //   按原版逻辑必须返回 false，实测却是 true。
    //
    // 【真凶：JustAFleshWound 的 Postfix】
    //
    //   工坊 2260055393「[Kit] Just A Flesh Wound」（`kittahkhan.justafleshwound`），
    //   1.7 用 `Current/Assemblies/JustAFleshWound.dll`。它在这个方法上挂了一个
    //   **pass-through Postfix**（签名 `bool Postfix(bool value, Pawn_HealthTracker __instance)`）：
    //
    //       FatalDamage fatalDamage = JustAFleshWound.settings.fatalDamage;
    //       if (!fatalDamage.newSystem) return value;          // 旧系统就放行
    //       float healthScale = pawn.HealthScale;
    //       float num2 = fatalDamage.resilience * healthScale * GetResilienceModifier(hediffSet);
    //       foreach (Hediff current in hediffSet.getInjuries())
    //       {
    //           BodyPartRecord part = current.Part;
    //           // ★ MissingPart 按「该部位的最大生命值」折算成伤害
    //           float num3 = (current is Hediff_MissingPart)
    //                        ? (float)part.def.hitPoints * healthScale
    //                        : current.Severity;
    //           float num4 = fatalDamage.nonPlayer;
    //           if (part.IsLimb())  num4 *= fatalDamage.limb;      // ★ 四肢还有倍率
    //           if (part.IsVital()) num4 *= fatalDamage.vital;
    //           ...
    //           num2 -= num3 * num4;                            // 从「韧性」里扣
    //       }
    //       return num2 <= 0f;                                  // 韧性扣光 → 判死
    //
    //   也就是说：**它把「因为装义体而被标成缺失」的 36 个部位，当成 36 处致命伤累计**。
    //   UFC_BOSS 一次装 4 件义体 → 双臂双腿连指/趾全部变 MissingPart → 韧性归零 → 判死。
    //
    //   这不是任何一方的 bug，而是**两个模组的语义冲突**：
    //     · JustAFleshWound 的意图是「真的失去四肢会致命」，这很合理；
    //     · UFC 的意图是「打手全身义体化」，装 BionicLeg 必然让原腿变成 MissingPart —— 这也很合理；
    //     · 冲突点在**世界生成期**：那里没有战斗、没有受伤，只有「装配」。
    //
    // 【修法：只在「pawn 正在生成」时跳过它那一次重算】
    //
    //   Prefix 打在这个 Postfix 上：pawn 正在被 `PawnGenerator` 生成时返回 false（跳过 Postfix），
    //   其余任何时候都放行。于是：
    //     · 世界生成期：义体照装，`__result` 保持原版算法的 false → 领袖能生出来；
    //     · 游戏内真实受伤/断肢：JustAFleshWound 的逻辑**原样生效**，一个字节都不改。
    //
    // 【为什么不能改我们自己的【54】守卫】
    //
    //   【54】的守卫打在 `Pawn_HealthTracker.RestorePart` 上，只覆盖「恢复部位」那条路径；
    //   而这次死亡发生在 `ApplyOnPawn` 里 `AddHediff` 之后的 `CheckForStateChange`，
    //   与 RestorePart 无关。两条守卫各管一段，互不替代 —— 都留着。
    //
    // 【自动让路】
    //
    //   · 模组没装 → 找不到那个 Postfix → 跳过（打 Message，不是 Error）；
    //   · 作者改了 Postfix 的名字/签名 → 装不上，打 Error 并留待下次重试；
    //   · 玩家把 JustAFleshWound 的「新致命伤害系统」关掉 → 它的 Postfix 自己会 `return value`，
    //     本项即便装上也不产生任何影响（跳过一个空操作）。
    // ==========================================================================

    /// <summary>
    /// 【61】世界生成期跳过 Just A Flesh Wound 的致命伤害重算。
    /// </summary>
    internal static class JafwLethalDamageGenerationGuardFix
    {
        private const string TargetTypeName =
            "JustAFleshWound.Patches.Pawn_HealthTracker_ShouldBeDeadFromLethalDamageThreshold";

        private const string TargetMethodName = "Postfix";

        /// <summary>`Log.ErrorOnce` 的去重键（ASCII "GNHj"）。</summary>
        private const int ErrorOnceKey = 0x474E486A;

        private static bool installed;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type tp = AccessTools.TypeByName(TargetTypeName);
                if (tp == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【61】Just A Flesh Wound is not present;"
                        + " its lethal-damage generation guard was skipped (nothing to do).");
                    return;
                }

                // Postfix(bool value, Pawn_HealthTracker __instance) —— 按「第二个参数是
                // Pawn_HealthTracker」来找，比按参数个数更稳（作者可能加参数）。
                MethodInfo target = null;
                MethodInfo[] methods = tp.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != TargetMethodName)
                    {
                        continue;
                    }

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length >= 2 && ps[1].ParameterType == typeof(Pawn_HealthTracker))
                    {
                        target = m;
                        break;
                    }
                }

                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【61】" + TargetTypeName + "." + TargetMethodName
                        + "(…, Pawn_HealthTracker) was not found (its API probably changed);"
                        + " the guard is deferred and will retry on the next start.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(JafwLethalDamageGenerationGuardFix), nameof(Prefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【61】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(prefix), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【61】patched " + TargetTypeName + "." + TargetMethodName
                    + " (prefix only). Just A Flesh Wound's lethal-damage recount is skipped"
                    + " while a pawn is being generated — prosthetics still install, and in-game"
                    + " injuries behave exactly as the author designed.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【61】failed to install the Just A Flesh Wound guard: " + ex);
            }
        }

        /// <summary>
        /// 只在「pawn 正在被生成」时跳过这个 Postfix；其它情况一律放行。
        /// </summary>
        private static bool Prefix(Pawn_HealthTracker __instance)
        {
            try
            {
                if (__instance == null || __instance.hediffSet == null)
                {
                    return true;
                }

                Pawn pawn = __instance.hediffSet.pawn;
                if (pawn == null)
                {
                    return true;
                }

                if (!PawnGenerator.IsBeingGenerated(pawn))
                {
                    return true; // 游戏内正常受伤 —— 让作者的逻辑原样生效
                }

                return false; // 生成期：跳过这次重算，保留原版算法的 false
            }
            catch (Exception ex)
            {
                // 守卫自己出错时放行，绝不因为「修」而改变游戏行为
                Log.ErrorOnce("[GNH LocalFixes] 【61】guard threw, allowing the original postfix: "
                    + ex.Message, ErrorOnceKey);
                return true;
            }
        }
    }
}
