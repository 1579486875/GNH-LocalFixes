// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【54】世界生成期「装义体 → 被判死亡 → 领袖连生 120 次全败」
    // ==========================================================================
    //
    // 【症状（本机日志原文，2026-11-12 第二轮诊断）】
    //
    //   每次世界生成都会连续打出三段递进式报错：
    //       19:30:11  Could not generate a pawn after 70 tries.  Last error: Generated dead pawn.
    //       19:30:14  Could not generate a pawn after 100 tries. Last error: Generated dead pawn.
    //       19:30:17  Pawn generation error: ... Too many tries (120), returning null.
    //                 Generation request: kindDef=UFC_BOSS, context=NonPlayer, faction=联合水果优品公司
    //
    //   紧跟着是三处 Pawn.Kill 补丁各刷一条「Could not find player faction.」，
    //   三次世界生成 × 120 次重试 × 3 个日志源 = 720 条红字（占整份日志 ERROR 的 86%）。
    //
    // 【完整堆栈（决定了该修哪里）】
    //
    //       Verse.Pawn.Kill
    //     ← Pawn_HealthTracker.CheckForStateChange(dinfo, hediff)
    //     ← Pawn_HealthTracker.AddHediff(hediff, part, dinfo, result)        ← 死亡在这里被「发现」
    //     ← Pawn_HealthTracker.AddHediff(def, part, dinfo, result)
    //     ← Recipe_InstallArtificialBodyPart.ApplyOnPawn(pawn, part, null, …, null)
    //     ← PawnTechHediffsGenerator.InstallPart(pawn, partDef)              ← 这一次是在装义体
    //     ← PawnTechHediffsGenerator.GenerateTechHediffsFor(pawn)
    //     ← PawnGenerator.GenerateInitialHediffs(pawn, request)
    //     ← … TryGenerateNewPawnInternal → GeneratePawn → Faction.TryGenerateNewLeader
    //     ← FactionGenerator.NewGeneratedFaction → WorldGenStep_Factions.GenerateFresh
    //
    // 【为什么这不是「义体装到致命部位」】
    //
    //   本机实测排除过一遍（以下都是有据可查的，不是猜）：
    //     · 原版 InstallBionicLeg 的 appliedOnFixedBodyParts = Leg，只作用在腿；
    //     · PawnCapacityUtility.CalculatePartEfficiency 只沿 part.parent 向上，
    //       **不递归子部位** —— 少一条腿不会让躯干效率归零；
    //     · UFC 的两个 startingHediffs（UFC_banana_High / UFC_monster_High）都是
    //       Hediff_High、无 lethalSeverity，甚至还 +Consciousness —— 不致死；
    //     · Hediff_AddedPart.CauseDeathNow() 恒为 false（def.lethalSeverity < 0）；
    //     · ShouldBeDeadFromLethalDamageThreshold 只统计 Hediff_Injury，装假体不产生伤口。
    //   **剩下唯一能判死的入口是 ShouldBeDeadFromRequiredCapacity()**，也就是
    //   「某个 lethalFlesh 的 PawnCapacity 变成 0」。至于是哪一个、为什么是 0，
    //   必须有实机证据才能定论 —— 所以本文件的第一件事是**把证据打出来**，
    //   而不是先猜一个改法把它压下去。
    //
    // 【本文件做两件事，各自独立可关】
    //
    //   ① 诊断（diagnostics 组，默认关闭）：
    //      在 Pawn_HealthTracker.CheckForStateChange 之前，如果「pawn 正在生成」
    //      且「此刻的 ShouldBeDead() 已经成立」，就把这个 pawn 的全部 hediff、
    //      全部 identity、以及 ShouldBeDead 五个判据的逐条结果写进日志。
    //      它**不改变任何行为**（只读），跑一次世界生成就能把死因钉死。
    //
    //   ② 修复（crash 组，默认开启）：
    //      给 Pawn_HealthTracker.RestorePart 加一层守卫 ——
    //      **只在「pawn 正在生成」时**把 checkStateChange 降级成 false。
    //
    // 【为什么 ② 是「修」而不是「砍功能」】
    //
    //   看 Ludeon 自己在同一个文件里怎么写的（1.7.5005 反编译原文）：
    //
    //       // Hediff_AddedPart.PostAdd
    //       public override void PostAdd(DamageInfo? dinfo)
    //       {
    //           base.PostAdd(dinfo);
    //           pawn.health.RestorePart(base.Part, this, checkStateChange: false);   // ← 明确传 false
    //           …
    //       }
    //
    //   引擎作者在「装假体」这条路径上**自己写了 checkStateChange: false**，
    //   说明他认同「恢复一个刚被假体替代的部位，不该顺手做一次死亡判定」。
    //   而 Recipe_InstallArtificialBodyPart.ApplyOnPawn 的无图分支
    //   （`else if (part != null) { pawn.health.RestorePart(part); }`）漏了这个参数，
    //   于是同一条语义在两条路径上行为不一致 —— 这就是缺口本身。
    //
    //   加守卫后：
    //     · 玩家在手术台上给小人装假体（billDoer != null 或 pawn.Map != null）—— 完全不受影响；
    //     · 世界生成期装配 —— 不再在装配中途判死；生成结束后 PawnGenerator 自己的
    //       validator（allowDead / allowDowned）照常生效，该拦的照样拦。
    //   也就是说：**没有一处「作者做进去的功能」被拿掉，只是把漏掉的参数补齐。**
    //
    // 【自动让路】
    //
    //   · RestorePart 的签名若在将来版本变了（参数表对不上）→ 装不上，打一条 Error 并留待重试；
    //   · 玩家关掉对应开关 → 整项不安装，游戏回到原版行为；
    //   · 诊断项默认关闭，不干扰正常游玩。
    // ==========================================================================

    /// <summary>
    /// 【54】世界生成期装配义体被误判死亡 —— 诊断 + 守卫。
    /// </summary>
    internal static class PawnGenDeathCauseFix
    {
        /// <summary>诊断输出上限（条）。够看清一轮世界生成的真相，又不会把日志刷爆。</summary>
        private const int MaxDiagReports = 24;

        /// <summary>Log.ErrorOnce 的去重键（ASCII "GNHd"）。</summary>
        private const int DiagErrorKey = 0x474E4864;

        /// <summary>守卫是否已装。</summary>
        private static bool guardInstalled;

        /// <summary>诊断是否已装。</summary>
        private static bool diagInstalled;

        /// <summary>已经打过多少条诊断。</summary>
        private static int diagCount;

        // ------------------------------------------------------------------
        // Pawn_HealthTracker.pawn 是 public 字段（1.6 / 1.7 一致）。
        // 仍然走反射取 FieldInfo：这样即使将来它改成属性，也只是这里取不到、不影响编译。
        // ------------------------------------------------------------------
        private static FieldInfo pawnField;

        private static Pawn GetPawn(Pawn_HealthTracker tracker)
        {
            if (tracker == null)
            {
                return null;
            }

            if (pawnField == null)
            {
                pawnField = AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");
            }

            return pawnField == null ? null : pawnField.GetValue(tracker) as Pawn;
        }

        // ==================================================================
        // ① 诊断项（默认关闭）
        // ==================================================================

        /// <summary>安装「死因诊断」。</summary>
        internal static void InstallDiagnostics()
        {
            if (diagInstalled)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Pawn_HealthTracker),
                    "CheckForStateChange",
                    new[] { typeof(DamageInfo?), typeof(Hediff) });
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】Pawn_HealthTracker.CheckForStateChange"
                        + "(DamageInfo?, Hediff) was not found; the pawn-generation death-cause probe"
                        + " stays off (nothing else is affected).");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(PawnGenDeathCauseFix), nameof(DiagPrefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】internal error: DiagPrefix not found.");
                    return;
                }

                new Harmony("GNH.LocalFixes.PawnGenDeathCause.Diag")
                    .Patch(target, prefix: new HarmonyMethod(prefix));
                diagInstalled = true;
                Log.Message("[GNH LocalFixes] 【54】pawn-generation death-cause probe installed"
                    + " (read-only; it never changes behaviour).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【54】failed to install the death-cause probe: " + ex);
            }
        }

        /// <summary>
        /// 诊断：在「世界生成期」且「此刻已经满足死亡条件」时，把这个 pawn 的
        /// 全部状态写进日志。**只读**，不改任何变量。
        /// </summary>
        private static void DiagPrefix(Pawn_HealthTracker __instance)
        {
            if (diagCount >= MaxDiagReports)
            {
                return;
            }

            try
            {
                Pawn pawn = GetPawn(__instance);
                if (pawn == null || pawn.Dead)
                {
                    // Dead 已经为 true 说明这一轮是「死后再次判定」，没有新信息。
                    return;
                }

                if (!PawnGenerator.IsBeingGenerated(pawn))
                {
                    // 只在「pawn 正在被生成」这个窗口里取证 —— 那才是问题发生的场景。
                    return;
                }

                if (!__instance.ShouldBeDead())
                {
                    return;
                }

                diagCount++;
                Log.Error("[GNH LocalFixes] 【54】pawn-gen death probe #" + diagCount + ":\n"
                    + BuildDiagnosis(pawn, __instance));
            }
            catch (Exception ex)
            {
                // 诊断自己出错绝不能影响游戏流程。
                Log.ErrorOnce("[GNH LocalFixes] 【54】death probe itself threw: " + ex.Message,
                    DiagErrorKey);
            }
        }

        /// <summary>拼一份「为什么它会死」的报告。</summary>
        private static string BuildDiagnosis(Pawn pawn, Pawn_HealthTracker tracker)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("  kindDef=").Append(pawn.kindDef == null ? "(null)" : pawn.kindDef.defName)
              .Append("  race=").Append(pawn.def == null ? "(null)" : pawn.def.defName)
              .Append("  faction=").Append(pawn.Faction == null ? "(null)" : pawn.Faction.Name)
              .Append('\n');

            if (pawn.kindDef != null)
            {
                sb.Append("  techHediffsRequired=")
                  .Append(DescrTechRequired(pawn.kindDef))
                  .Append('\n');
            }

            // ---- hediff 全表 ----
            sb.Append("  hediffs (").Append(tracker.hediffSet.hediffs.Count).Append("):\n");
            for (int i = 0; i < tracker.hediffSet.hediffs.Count; i++)
            {
                Hediff h = tracker.hediffSet.hediffs[i];
                if (h == null)
                {
                    continue;
                }

                sb.Append("    [").Append(i).Append("] ")
                  .Append(h.def == null ? "(nulldef)" : h.def.defName)
                  .Append(" sev=").Append(h.Severity.ToString("0.###"))
                  .Append(" part=").Append(h.Part == null ? "(whole body)" : h.Part.Label)
                  .Append(" class=").Append(h.GetType().Name)
                  .Append('\n');
            }

            // ---- ShouldBeDead 的五条判据逐条列出 ----
            sb.Append("  verdicts:\n");

            bool preventsDeath = tracker.hediffSet.HasPreventsDeath;
            sb.Append("    HasPreventsDeath      = ").Append(preventsDeath).Append('\n');

            for (int i = 0; i < tracker.hediffSet.hediffs.Count; i++)
            {
                Hediff h = tracker.hediffSet.hediffs[i];
                if (h != null && h.CauseDeathNow())
                {
                    sb.Append("    CauseDeathNow         = ").Append(h.def.defName)
                      .Append(" (sev ").Append(h.Severity.ToString("0.###")).Append(")\n");
                }
            }

            PawnCapacityDef failing = null;
            try
            {
                failing = tracker.ShouldBeDeadFromRequiredCapacity();
            }
            catch (Exception ex)
            {
                sb.Append("    RequiredCapacity      = threw ").Append(ex.GetType().Name).Append('\n');
            }

            sb.Append("    RequiredCapacity      = ")
              .Append(failing == null ? "(none)" : failing.defName)
              .Append('\n');

            // 把每个 lethalFlesh 的产能实际值都打出来 —— 这才是「到底是哪一个归零」的答案。
            sb.Append("    capacity values:\n");
            List<PawnCapacityDef> allCaps = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            for (int i = 0; i < allCaps.Count; i++)
            {
                PawnCapacityDef cap = allCaps[i];
                if (cap == null)
                {
                    continue;
                }

                bool lethal = pawn.RaceProps != null && pawn.RaceProps.IsFlesh
                    ? cap.lethalFlesh
                    : cap.lethalMechanoids;
                if (!lethal)
                {
                    continue;
                }

                float v;
                try
                {
                    v = tracker.capacities.GetLevel(cap);
                }
                catch (Exception ex)
                {
                    sb.Append("      ").Append(cap.defName).Append(" = threw ")
                      .Append(ex.GetType().Name).Append('\n');
                    continue;
                }

                sb.Append("      ").Append(cap.defName).Append(" = ")
                  .Append(v.ToString("0.####")).Append('\n');
            }

            float coreEff;
            try
            {
                coreEff = PawnCapacityUtility.CalculatePartEfficiency(
                    tracker.hediffSet, pawn.RaceProps.body.corePart);
            }
            catch (Exception ex)
            {
                sb.Append("    CorePartEfficiency    = threw ").Append(ex.GetType().Name).Append('\n');
                coreEff = -1f;
            }

            sb.Append("    CorePartEfficiency    = ").Append(coreEff.ToString("0.####"))
              .Append(" (core=")
              .Append(pawn.RaceProps.body.corePart == null
                  ? "(null)" : pawn.RaceProps.body.corePart.Label)
              .Append(")\n");

            bool lethalDmg = false;
            try
            {
                lethalDmg = tracker.ShouldBeDeadFromLethalDamageThreshold();
            }
            catch (Exception ex)
            {
                sb.Append("    LethalDamageThreshold = threw ").Append(ex.GetType().Name).Append('\n');
            }

            sb.Append("    LethalDamageThreshold = ").Append(lethalDmg).Append('\n');

            return sb.ToString();
        }

        private static string DescrTechRequired(PawnKindDef kind)
        {
            if (kind.techHediffsRequired == null || kind.techHediffsRequired.Count == 0)
            {
                return "(none)";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < kind.techHediffsRequired.Count; i++)
            {
                ThingDef d = kind.techHediffsRequired[i];
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(d == null ? "(null)" : d.defName);
            }

            return sb.ToString();
        }

        // ==================================================================
        // ② 守卫（默认开启）
        // ==================================================================

        /// <summary>安装「生成期不因恢复部位而判死」的守卫。</summary>
        internal static void InstallGuard()
        {
            if (guardInstalled)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Pawn_HealthTracker),
                    "RestorePart",
                    new[] { typeof(BodyPartRecord), typeof(Hediff), typeof(bool) });
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】Pawn_HealthTracker.RestorePart"
                        + "(BodyPartRecord, Hediff, bool) was not found; the guard is deferred"
                        + " and will retry on the next start.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(PawnGenDeathCauseFix), nameof(RestorePartPrefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】internal error: RestorePartPrefix not found.");
                    return;
                }

                new Harmony("GNH.LocalFixes.PawnGenDeathCause.Guard")
                    .Patch(target, prefix: new HarmonyMethod(prefix));
                guardInstalled = true;
                Log.Message("[GNH LocalFixes] 【54】RestorePart no-death-check-during-generation"
                    + " guard installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【54】failed to install the RestorePart guard: " + ex);
            }
        }

        /// <summary>
        /// 只在「pawn 正在被生成」这个窗口里，把 checkStateChange 从 true 降级成 false。
        /// 其它一切场景（玩家手术、战斗、日常治疗）原样不动。
        /// </summary>
        private static void RestorePartPrefix(Pawn_HealthTracker __instance, ref bool checkStateChange)
        {
            if (!checkStateChange)
            {
                return; // 调用方本来就传了 false —— 不需要我们做什么。
            }

            try
            {
                Pawn pawn = GetPawn(__instance);
                if (pawn == null)
                {
                    return;
                }

                if (!PawnGenerator.IsBeingGenerated(pawn))
                {
                    return; // 不是生成期 —— 游戏内正常手术，保持原版行为。
                }

                // 与 Hediff_AddedPart.PostAdd 里 Ludeon 自己传的 checkStateChange: false 对齐。
                checkStateChange = false;
            }
            catch
            {
                // 守卫自己出错时保持原值，让游戏按原版行为走（宁可报错也不改变语义）。
            }
        }
    }
}
