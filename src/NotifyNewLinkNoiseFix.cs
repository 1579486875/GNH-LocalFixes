// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【67】「Notify_NewLink was called but the link is already here.」—— 纯噪音，静音
    // ==========================================================================
    //
    // ── 症状（2026-11-13 第三轮全局日志诊断 P2-1）──
    //
    //   读档时 12 条：
    //     ERROR Notify_NewLink was called but the link is already here.
    //     at Verse.ThingWithComps.SpawnSetup(Map, Boolean)
    //     at .Verse.Building.SpawnSetup_Patch8(...)
    //     at RimWorld.Building_WorkTable.SpawnSetup(...)
    //     at .Verse.GenSpawn.Spawn_Patch10(...)
    //     at .Verse.Map.FinalizeLoading_Patch4(Map this)
    //
    //   12 次全是 Building_WorkTable，全在 Map.FinalizeLoading 期间。
    //
    // ── 报告的一个推断需要订正 ──
    //
    //   报告 §6.4 写「Notify_NewLink 报错后，那次 link 登记可能只登记了一半 →
    //   工作台的设施加成不生效」。**这个推断不成立**。反编译原文：
    //
    //       public void Notify_NewLink(Thing facility)
    //       {
    //           for (int i = 0; i < linkedFacilities.Count; i++)
    //           {
    //               if (linkedFacilities[i] == facility)
    //               {
    //                   Log.Error("Notify_NewLink was called but the link is already here.");
    //                   return;                    // ← 直接返回，**不会再 Add 一次**
    //               }
    //           }
    //           …
    //           linkedFacilities.Add(facility);
    //       }
    //
    //   也就是说：发现重复时它**什么都不做就返回** —— `linkedFacilities` 里那份是**完整的**。
    //   那 12 个工作台的设施加成没有失效，只是**多了一次多余的调用**。
    //
    //   这条错误是**纯噪音**，不是功能缺陷。
    //
    // ── 修法：重复时由我们自己处理（静音 + 留一条诊断）──
    //
    //   Prefix 里先判 `linkedFacilities` 里有没有这个 facility：
    //     · 已有 → **我们自己 return false**（跳过原方法）：
    //         - 原方法本来也会「返回、不添加」，行为**完全等价**；
    //         - 少一条 Log.Error（噪音没了）；
    //         - 但我们打一条**信息量更大的 Warning**（带 parent 与 facility 的 Label），
    //           只打前几条 —— 这样既静音又能定位是哪个模组在重复调用。
    //     · 没有 → return true（走原逻辑，一点不改）。
    //
    //   ⚠ 为什么不用 Postfix：Log.Error 是在方法体里打的，Postfix 拦不住。
    //   ⚠ 为什么不直接 `success=Always` 之类：这是 C# 里的 Log.Error，不是 XML patch。
    //
    // ── 自动让路 ──
    //
    //   · 没有重复时 Prefix 立刻 return true，等于不存在；
    //   · `linkedFacilities` 是 private → 用 FieldRefAccess，找不到就整项跳过；
    //   · 出错就放行，绝不影响读档。
    // ==========================================================================

    /// <summary>
    /// 【67】把「Notify_NewLink ... already here」的重复告警换成一条带 Label 的诊断。
    /// </summary>
    internal static class NotifyNewLinkNoiseFix
    {
        private const int ErrorOnceKey = 0x474E486D;   // ASCII "GNHm"

        /// <summary>最多打几条诊断（超过就只汇总条数）。</summary>
        private const int MaxDetailLogs = 6;

        private static bool installed;
        private static int detailLogged;
        private static int totalSuppressed;

        private static FieldInfo linkedFacilitiesField;
        private static AccessTools.FieldRef<CompAffectedByFacilities, List<Thing>> linkedRef;

        // ── 2026-11-14 补漏：同一条 Error 还有第二个出处 ──────────────────
        //   反编译 1.7.5005（search_string_literals）查到两条同文 Error：
        //     · RimWorld.CompAffectedByFacilities.Notify_NewLink  ← 本项原本只 patch 了这个
        //     · RimWorld.CompFacility.Notify_NewLink              ← 漏了
        //   两个方法的实现逐字同构（发现重复 → Log.Error + return，不添加），
        //   所以同样可以静音、同样等价。
        //   ⚠ 实测证据：2026-11-14 10:33:23 载入旧存档时日志里 12 条这条 Error，
        //     而【67】的 suppressed 计数是 0 —— 说明那 12 条全来自 CompFacility 这一侧。
        private static FieldInfo linkedBuildingsField;
        private static AccessTools.FieldRef<CompFacility, List<Thing>> linkedBuildingRef;
        private static bool installedCompFacility;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(CompAffectedByFacilities), "Notify_NewLink",
                    new[] { typeof(Thing) }, null);
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【67】CompAffectedByFacilities.Notify_NewLink(Thing)"
                        + " was not found (its API probably changed);"
                        + " the fix is deferred and will retry on the next start.");
                    return;
                }

                linkedFacilitiesField = AccessTools.Field(
                    typeof(CompAffectedByFacilities), "linkedFacilities");
                if (linkedFacilitiesField == null)
                {
                    Log.Error("[GNH LocalFixes] 【67】field linkedFacilities was not found;"
                        + " the fix is deferred and will retry.");
                    return;
                }
                linkedRef = AccessTools.FieldRefAccess<CompAffectedByFacilities, List<Thing>>(
                    linkedFacilitiesField);

                MethodInfo pre = AccessTools.Method(
                    typeof(NotifyNewLinkNoiseFix), nameof(Prefix));
                if (pre == null)
                {
                    Log.Error("[GNH LocalFixes] 【67】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(pre), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【67】patched CompAffectedByFacilities.Notify_NewLink"
                    + " (prefix only). A duplicate link no longer logs the vanilla error line;"
                    + " instead we log one informative warning naming the parent and the facility,"
                    + " so the mod that calls it twice can be identified."
                    + " Note: the link list itself was always correct — that vanilla error is"
                    + " pure noise, not a broken workbench bonus.");

                InstallCompFacility();
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【67】failed to install: " + ex);
            }
        }

        /// <summary>
        /// 同一条 Error 的第二个出处：<see cref="CompFacility.Notify_NewLink"/>。
        /// 与主项完全独立 —— 它失败不影响主项，反之亦然。
        /// </summary>
        private static void InstallCompFacility()
        {
            if (installedCompFacility)
            {
                return;
            }
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(CompFacility), "Notify_NewLink",
                    new[] { typeof(Thing) }, null);
                if (target == null)
                {
                    Log.Warning("[GNH LocalFixes] 【67b】CompFacility.Notify_NewLink(Thing)"
                        + " was not found (its API probably changed); this half is skipped,"
                        + " the CompAffectedByFacilities half is unaffected.");
                    return;
                }

                linkedBuildingsField = AccessTools.Field(typeof(CompFacility), "linkedBuildings");
                if (linkedBuildingsField == null)
                {
                    Log.Warning("[GNH LocalFixes] 【67b】field linkedBuildings was not found;"
                        + " this half is skipped, the other half is unaffected.");
                    return;
                }
                linkedBuildingRef = AccessTools.FieldRefAccess<CompFacility, List<Thing>>(
                    linkedBuildingsField);

                MethodInfo pre = AccessTools.Method(
                    typeof(NotifyNewLinkNoiseFix), nameof(PrefixCompFacility));
                if (pre == null)
                {
                    Log.Error("[GNH LocalFixes] 【67b】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(pre), null, null, null);
                installedCompFacility = true;

                Log.Message("[GNH LocalFixes] 【67b】patched CompFacility.Notify_NewLink"
                    + " (prefix only). This is the *second* place that prints the same"
                    + " \"Notify_NewLink was called but the link is already here.\" error —"
                    + " the first one (CompAffectedByFacilities) was already handled above."
                    + " 实测依据：2026-11-14 10:33 载入旧存档时 12 条该错误全部来自这一侧。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【67b】failed to install: " + ex);
            }
        }

        /// <summary>
        /// <see cref="CompFacility.Notify_NewLink"/> 的同构守卫。
        /// 原方法发现重复时同样是「Log.Error + return，不 Add」，
        /// 所以这里 return false 与它完全等价，只是少一条红字。
        /// </summary>
        private static bool PrefixCompFacility(CompFacility __instance, Thing thing)
        {
            try
            {
                if (__instance == null || thing == null || linkedBuildingRef == null)
                {
                    return true;
                }

                List<Thing> list = linkedBuildingRef(__instance);
                if (list == null || list.Count == 0)
                {
                    return true;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == thing)
                    {
                        totalSuppressed++;
                        if (detailLogged < MaxDetailLogs)
                        {
                            detailLogged++;
                            ThingWithComps parent = __instance.parent;
                            Log.Warning("[GNH LocalFixes] 【67b】suppressed a duplicate"
                                + " CompFacility.Notify_NewLink: parent=\""
                                + (parent != null ? parent.LabelShort : "(null)")
                                + "\" (def=" + (parent != null && parent.def != null
                                    ? parent.def.defName : "?") + ")"
                                + ", facility=\"" + thing.LabelShort + "\""
                                + " (def=" + (thing.def != null ? thing.def.defName : "?") + ")."
                                + " The link list is already correct, so this is noise."
                                + " (" + detailLogged + "/" + MaxDetailLogs + ")");
                        }
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] 【67b】guard threw, allowing the original call: "
                    + ex.Message, ErrorOnceKey + 1);
                return true;
            }
        }

        /// <summary>
        /// 已有同一条 link 时由我们自己返回 false（等价于原方法的「返回、不添加」）。
        /// </summary>
        private static bool Prefix(CompAffectedByFacilities __instance, Thing facility)
        {
            try
            {
                if (__instance == null || facility == null || linkedRef == null)
                {
                    return true;
                }

                List<Thing> list = linkedRef(__instance);
                if (list == null || list.Count == 0)
                {
                    return true;
                }

                bool dup = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == facility)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                {
                    return true;   // 不重复：走原逻辑，一点不改
                }

                totalSuppressed++;

                if (detailLogged < MaxDetailLogs)
                {
                    detailLogged++;
                    ThingWithComps parent = __instance.parent;
                    Log.Warning("[GNH LocalFixes] 【67】suppressed a duplicate Notify_NewLink:"
                        + " parent=\"" + (parent != null ? parent.LabelShort : "(null)")
                        + "\" (def=" + (parent != null && parent.def != null ? parent.def.defName : "?") + ")"
                        + ", facility=\"" + facility.LabelShort + "\""
                        + " (def=" + (facility.def != null ? facility.def.defName : "?") + ")"
                        + ". The link list is already correct, so this is noise —"
                        + " but the caller is worth finding. (" + detailLogged + "/" + MaxDetailLogs + ")");
                }

                return false;   // 跳过原方法：它本来也只 return 不添加
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] 【67】guard threw, allowing the original call: "
                    + ex.Message, ErrorOnceKey);
                return true;
            }
        }
    }
}
