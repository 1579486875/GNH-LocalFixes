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
    // 【66】RitualObligation 的 ID 为 0 → 读档刷「Cannot register ... Id already used」
    // ==========================================================================
    //
    // ── 症状（2026-11-13 第三轮全局日志诊断 P1-2）──
    //
    //   读档时三条：
    //     Cannot register RimWorld.RitualObligation, (id=RitualObligation_0 …)
    //                               Id already used by RimWorld.RitualObligation.
    //     Cannot register RimWorld.IdeoBuildingPresenceDemand, (id=IdeoBuildingPresenceDemand_197 …)
    //     Cannot register CeleTech.Base.Verb_MeleeSectorDamage, (id=Verb_…2956_0_CMC_AOE_Slash …)
    //     at .Verse.CrossRefHandler.ResolveAllCrossReferences_Patch2(CrossRefHandler this)
    //
    //   本项只处理第一条（`RitualObligation_0`）—— 后两条的成因不同，见文末。
    //
    // ── 根因（反编译 RimWorld.RitualObligation 逐字确认）──
    //
    //   public class RitualObligation : IExposable, ILoadReferenceable
    //   {
    //       public int ID;                                   // ★ 默认 0
    //
    //       public RitualObligation() { }                     // ★ 无参构造，ID 保持 0
    //
    //       public RitualObligation(Precept_Ritual precept, TargetInfo targetA, …)
    //       {
    //           …
    //           ID = Find.UniqueIDsManager.GetNextRitualObligationID();   // ← 只有带参构造才分配
    //       }
    //
    //       public string GetUniqueLoadID() => "RitualObligation_" + ID;  // ← ID=0 → "RitualObligation_0"
    //   }
    //
    //   也就是说：只要**有任何代码走无参构造函数**创建一个 RitualObligation，
    //   它的 ID 就恒为 0，`GetUniqueLoadID()` 恒为 `"RitualObligation_0"`。
    //   两个这样的对象同时存在 → CrossRefHandler 注册时第二个撞 ID → 报错 → 拿不到引用。
    //
    //   `ExposeData` 里是 `Scribe_Values.Look(ref ID, "ID", -1)` ——
    //   读档时若存档里没有 `ID` 字段，`ref ID` 保持原值（构造时的 0），**不会变成 -1**，
    //   所以坏数据会一直传下去。
    //
    // ── 修法：注册之前把 ID ≤ 0 的补成唯一 ID ──
    //
    //   在 `CrossRefHandler.ResolveAllCrossReferences()` 的 **Prefix** 里扫一遍：
    //       Find.IdeoManager.IdeosListForReading
    //         → 每个 Ideo 的 PreceptsListForReading
    //           → 每个 Precept_Ritual 的 activeObligations / completedObligations
    //             → ID <= 0 的调 Find.UniqueIDsManager.GetNextRitualObligationID()
    //
    //   为什么选在这个时机：那是**注册前**的最后一站。更早（LoadingVars 期间）拿不到完整的
    //   IdeoManager；更晚（注册中）报错已经发生了。
    //
    // ── 为什么这是保意图修法 ──
    //
    //   `ID` 是「唯一标识」，它本来就不该是 0。补一个唯一 ID **不改变任何游戏逻辑** ——
    //   那个仪式义务还是那个仪式义务，只是它终于能被正确登记与引用。
    //   不修的话：重复登记 → 后一个对象拿不到引用 → 仪式义务列表里可能有一条被顶掉
    //   （玩家看不到某个待办仪式），属于「静默的错误状态」。
    //
    // ── 自动让路 ──
    //
    //   · 一局只跑一次（`doneThisLoad` 静态标志，读档/新开局都会重置）；
    //   · 没有 ID ≤ 0 的对象时什么都不做、也不打日志（正常存档不会出现）；
    //   · 出错就放行，绝不影响读档本身。
    //
    // ── 另外两条 `Cannot register` 为什么不在本项处理 ──
    //
    //   · `IdeoBuildingPresenceDemand_197` —— 那个 ID 是**正常的**（197 不是 0），
    //     说明是「同一个 ID 被两个对象用了」，成因在别的模组（重复生成 demand），
    //     本项不猜、不动它。
    //   · `Verb_…2956_0_CMC_AOE_Slash` —— 那是 CE 系 `Verb_MeleeSectorDamage`
    //     在唐刀人格武器上的重复登记，与 RitualObligation 无关。
    //   ⚠ 按模板 §0.2：**没定位到根因就不动**，宁可留一条红字。
    // ==========================================================================

    /// <summary>
    /// 【66】读档前把 ID ≤ 0 的 RitualObligation 补成唯一 ID。
    /// </summary>
    internal static class RitualObligationIdFix
    {
        /// <summary>`Log.ErrorOnce` 的去重键（ASCII "GNHl"）。</summary>
        private const int ErrorOnceKey = 0x474E486C;

        private static bool installed;

        /// <summary>本次加载是否已经跑过（读档/新开局会重置）。</summary>
        private static bool doneThisLoad;

        private static int lastGameId = -1;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(CrossRefHandler), "ResolveAllCrossReferences", Type.EmptyTypes, null);
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【66】CrossRefHandler.ResolveAllCrossReferences"
                        + " was not found (its API probably changed);"
                        + " the fix is deferred and will retry on the next start.");
                    return;
                }

                MethodInfo pre = AccessTools.Method(
                    typeof(RitualObligationIdFix), nameof(Prefix));
                if (pre == null)
                {
                    Log.Error("[GNH LocalFixes] 【66】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(pre), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【66】patched CrossRefHandler.ResolveAllCrossReferences"
                    + " (prefix only). RitualObligations whose ID is 0 now get a real unique ID"
                    + " before cross-reference registration, instead of colliding on"
                    + " \"RitualObligation_0\".");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【66】failed to install: " + ex);
            }
        }

        /// <summary>
        /// 注册前扫一遍，把 ID ≤ 0 的补成唯一 ID。
        /// </summary>
        private static void Prefix()
        {
            try
            {
                // 每次读档 / 新开局只跑一次：Game 换了就重置。
                Game game = Current.Game;
                if (game == null)
                {
                    return;
                }

                int gid = game.GetHashCode();
                if (gid != lastGameId)
                {
                    lastGameId = gid;
                    doneThisLoad = false;
                }

                if (doneThisLoad)
                {
                    return;
                }
                doneThisLoad = true;

                IdeoManager im = Find.IdeoManager;
                if (im == null)
                {
                    return;
                }

                UniqueIDsManager uim = Find.UniqueIDsManager;
                if (uim == null)
                {
                    return;
                }

                int fixedCount = 0;
                List<Ideo> ideos = im.IdeosListForReading;
                if (ideos == null)
                {
                    return;
                }

                for (int i = 0; i < ideos.Count; i++)
                {
                    Ideo ideo = ideos[i];
                    if (ideo == null)
                    {
                        continue;
                    }

                    List<Precept> precepts = ideo.PreceptsListForReading;
                    if (precepts == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < precepts.Count; j++)
                    {
                        Precept_Ritual ritual = precepts[j] as Precept_Ritual;
                        if (ritual == null)
                        {
                            continue;
                        }

                        fixedCount += FixList(ritual.activeObligations, uim);
                        fixedCount += FixList(ritual.completedObligations, uim);
                    }
                }

                if (fixedCount > 0)
                {
                    Log.Warning("[GNH LocalFixes] 【66】reassigned " + fixedCount
                        + " RitualObligation(s) whose ID was <= 0."
                        + " They all shared the load id \"RitualObligation_0\", which made"
                        + " CrossRefHandler report \"Id already used\" and drop their references."
                        + " The obligations themselves are unchanged — only their identifiers are now unique.");
                }
            }
            catch (Exception ex)
            {
                // 守卫绝不能影响读档：出错就放行，让原逻辑照常跑。
                Log.ErrorOnce("[GNH LocalFixes] 【66】guard threw, continuing with the original flow: "
                    + ex.Message, ErrorOnceKey);
            }
        }

        private static int FixList(List<RitualObligation> list, UniqueIDsManager uim)
        {
            if (list == null || list.Count == 0)
            {
                return 0;
            }

            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                RitualObligation ob = list[i];
                if (ob == null)
                {
                    continue;
                }
                if (ob.ID > 0)
                {
                    continue;
                }
                ob.ID = uim.GetNextRitualObligationID();
                n++;
            }
            return n;
        }
    }
}
