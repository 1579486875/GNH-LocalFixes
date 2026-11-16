// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）完成。
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
    // 【69】「裸体评价更多看法」的页签，在一批「自己写死页签列表」的种族上不出现
    // ==========================================================================
    //
    // ── 症状（2026-11-16 玩家实测，两轮会话 + 截图 + 改年龄对照）──
    //
    //   · 原版人类（24 岁）页签栏：幻化 | 裸体 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康
    //   · 某「人工种族」（16 岁，改成 20 岁并存读档后）：
    //                              幻化 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康
    //   · 但机制完全正常：全裸照常遮体、走光照常掉心情、评语照常生成。
    //   · **改年龄无效** —— 与年龄无关。
    //
    // ── 根因：那个种族**根本不继承 `BasePawn`**，所以拿不到挂在 `BasePawn` 上的那一项 ──
    //
    //   「裸体评价更多看法」用一条 XML patch 把页签挂到原版 Pawn 的公共父节点上
    //   （`Common/Patches/ReplaceNMMTab.xml`）：
    //
    //       <xpath>Defs/ThingDef[@Name="BasePawn"]/inspectorTabs</xpath>
    //       <value><li>NudityMattersMore_opinions.ITab_Pawn_NMMOpinions</li></value>
    //
    //   原版 `Human` 是 `<ThingDef Name="Human" ParentName="BasePawn">`，
    //   自己没写 `inspectorTabs`，所以**继承**到 `BasePawn` 那一份（含本页签）—— 原版人类没问题。
    //
    //   而「人工种族」那一系列（Aya 系列，13 个种族逐一核过）走的是另一条路：
    //   它们在自己的 XML 里**另起一个根**，并**手抄了一份完整的页签列表**：
    //
    //       <ThingDef Name="HAR_Nearmaere_BasePawn" Abstract="True">
    //         …
    //         <inspectorTabs>
    //           <li>ITab_Pawn_Health</li>
    //           <li>ITab_Pawn_Needs</li>
    //           … 共 15 项，与原版 BasePawn 一模一样 …
    //         </inspectorTabs>
    //
    //   **唯独没有本页签** —— 因为作者抄这份列表的时候，「裸体评价」还没把它加进去；
    //   而且这条链路根本不经过 `BasePawn`，之后任何人往 `BasePawn` 上打补丁都影响不到它。
    //
    //   已核实的 13 个 Aya 种族（Nearmare / Xenoorca / Idhale / Silkiera / Neclose /
    //   Littluna / Solark / Saclean / Nexaga / Qualeela / Idearn / Chaoura / Outerm）：
    //   **全部自写 `inspectorTabs`，全部不含本页签**。
    //
    //   ⇒ 页签压根没进 `ThingDef.inspectorTabs`，`ResolveReferences` 自然不会把它解析进
    //     `inspectorTabsResolved`，`GetInspectTabs()` 返回的列表里就没有它。
    //     这跟 `IsVisible`、跟年龄、跟裸体逻辑**都没有关系** ——
    //     所以「机制一切正常、只有这个页签没了」。
    //
    //   ⚠ 这里记录一条走错过的路，避免以后再踩：
    //     最初怀疑是 `IsVisible` 里 `can_do_loving` 把人工种族误判成机械体
    //     （RJW 用 `FleshType.defName.Contains("Android")` 判机械体）。
    //     **实测推翻**：13 个 Aya 种族的 FleshType 是继承默认值（`Normal`）或
    //     `HAR_xx_Flesh`，**没有一个含 `Android`**；`intelligence` 也是 `Humanlike`
    //     （`ToolUser` 只出现在 `_NPC` 变体上，那是敌方单位，不是殖民者）。
    //     所以那条推断不成立，已废弃。
    //
    // ── 修法：给「自己写了页签列表、却漏了这一项」的 ThingDef 补上 ──
    //
    //   时机：`LoadedLanguage.InjectIntoData_BeforeImpliedDefs` 的 **Postfix**。
    //   反编译 `PlayDataLoader.DoPlayLoad()` 得到的分步是：
    //
    //       ② DefDatabase<T>.AddAllInMods()            ← Def 进全局库，inspectorTabs 已是 List<Type>
    //       ⑦ InjectIntoData_BeforeImpliedDefs()       ← **我们在这里补**
    //       ⑨ DefDatabase<ThingDef>.ResolveAllReferences()
    //             └─ ThingDef.ResolveReferences() 里：
    //                  if (inspectorTabs != null && inspectorTabs.Any()) {
    //                      inspectorTabsResolved = new List<InspectTabBase>();
    //                      … 逐个 GetSharedInstance …
    //                  }
    //      ⑪ if (Prefs.DevMode) ErrorCheckAllDefs()
    //
    //   ⑨ 会**无条件重建** `inspectorTabsResolved`，所以在 ⑦ 补进去的类型能正常生效，
    //   不需要自己去碰 `inspectorTabsResolved`。
    //
    //   补的条件（四条同时成立，缺一不动）：
    //     ① 本页签类型存在（没装「裸体评价更多看法」就直接退出）；
    //     ② `def.race != null` —— 是个 pawn，不是物品/建筑；
    //     ③ `def.inspectorTabs` 非空 **且含 `ITab_Pawn_Health`** ——
    //        说明这确实是一份「人形 pawn 的页签列表」，而不是随便什么列表；
    //     ④ 目前不含本页签 —— 防重复，也保证「本来就有的种族」一点不受影响。
    //
    //   ⇒ 原版人类、鼠族这类正常继承 `BasePawn` 的种族，第 ④ 条直接挡掉；
    //     动物没有 `ITab_Pawn_Health`，第 ③ 条挡掉；
    //     物品/建筑没有 `race`，第 ② 条挡掉。
    //
    // ── 为什么不是「把页签加给所有 pawn」 ──
    //
    //   那会给动物、机械体也塞一份。虽然本页签自己的 `IsVisible` 会判断 `Humanlike`
    //   从而不显示，但没必要让它们的 `inspectorTabsResolved` 里多一个永远不显示的实例。
    //   第 ③ 条用 `ITab_Pawn_Health` 当判据，等价于「这个 def 的页签栏是给人形 pawn 用的」。
    //
    // ── 自动让路 ──
    //
    //   · 没装「裸体评价更多看法」→ 拿不到类型，打一行 Message 跳过；
    //   · 作者以后把这些种族改成继承 `BasePawn`，或自己补了这一项 → 第 ④ 条挡掉，本项不做事；
    //   · 玩家关掉本项 → 整项不安装，回到原状。
    // ==========================================================================

    /// <summary>
    /// 【69】给「自己写死 inspectorTabs、但漏了裸体评价页签」的人形种族补上那一项。
    /// 只动这类 def，继承 BasePawn 的正常种族与动物、物品一概不碰。
    /// </summary>
    internal static class NmmTabCustomRaceFix
    {
        private const string TabTypeName = "NudityMattersMore_opinions.ITab_Pawn_NMMOpinions";

        private static bool installed;

        /// <summary>累计给多少个 ThingDef 补过页签。</summary>
        private static int patchedDefCount;

        /// <summary>补过页签的 defName 样例（日志里点名用）。</summary>
        private static readonly List<string> samples = new List<string>();

        private const int MaxSamples = 6;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // 目标方法在 NMM 那边；这里只是借它的 Postfix 当时机锚点，
                // 真正要改的是 Def 数据而不是它的方法体。
                Type langType = AccessTools.TypeByName("Verse.LoadedLanguage");
                if (langType == null)
                {
                    Log.Message("[GNH LocalFixes] 【69】Verse.LoadedLanguage not found;"
                        + " the custom-race tab patch was skipped.");
                    installed = true;
                    return;
                }

                MethodInfo target = AccessTools.Method(
                    langType, "InjectIntoData_BeforeImpliedDefs", null, null);
                if (target == null)
                {
                    Log.Message("[GNH LocalFixes] 【69】InjectIntoData_BeforeImpliedDefs not found;"
                        + " the custom-race tab patch was skipped.");
                    installed = true;
                    return;
                }

                // 没装 NMM 就没必要挂 —— 挂了也只是空转。
                if (AccessTools.TypeByName(TabTypeName) == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【69】Nudity Matters More (opinions) is not present;"
                        + " the custom-race tab patch was skipped (nothing to do).");
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(NmmTabCustomRaceFix), nameof(Postfix));
                if (postfix == null)
                {
                    Log.Error("[GNH LocalFixes] 【69】internal error: Postfix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, null, new HarmonyMethod(postfix), null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【69】custom-race inspector-tab patch installed"
                    + " (adds the nudity tab to pawn races that hard-code their own"
                    + " inspectorTabs list instead of inheriting it from BasePawn).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【69】failed to install the custom-race tab patch: " + ex);
            }
        }

        /// <summary>
        /// 语言包注入完、ResolveReferences 之前补页签。
        /// 这个 Postfix 与【68】挂在同一个方法上，Harmony 允许多个 Postfix 并存，互不影响。
        /// </summary>
        public static void Postfix()
        {
            try
            {
                int now = PatchAllDefs();
                if (now <= 0)
                {
                    return;
                }
                patchedDefCount += now;

                Log.Message("[GNH LocalFixes] 【69】已给 " + now
                    + " 个「自己写死页签列表」的人形种族补上「裸体评价」页签（"
                    + DescribeSamples() + "）。"
                    + "原因：这些种族在自己 XML 里另起了一个根、手抄了一份 15 项的 inspectorTabs，"
                    + "既不继承原版的 BasePawn，那一份里也没有本页签；"
                    + "而本页签是靠 patch 加到 BasePawn 上的，所以它们天然拿不到。"
                    + "本项只补「含 ITab_Pawn_Health 但缺本页签」的 pawn def，"
                    + "原版人类与鼠族这类正常继承的种族、动物、物品一概不碰。"
                    + "（本条及后续同类只计数，不再逐条打印。）");
            }
            catch (Exception ex)
            {
                // 兜底：补页签失败绝不能连累加载流程。
                Log.Error("[GNH LocalFixes] 【69】custom-race tab patch threw: " + ex);
            }
        }

        /// <summary>遍历全部 ThingDef 并按条件补页签。返回补了几个。</summary>
        private static int PatchAllDefs()
        {
            Type tabType = AccessTools.TypeByName(TabTypeName);
            if (tabType == null)
            {
                return 0;
            }

            int count = 0;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def == null || def.race == null)
                {
                    // 不是 pawn：物品、建筑、地形都不该有 pawn 页签。
                    continue;
                }

                List<Type> tabs = def.inspectorTabs;
                if (tabs == null || tabs.Count == 0)
                {
                    // 它自己没写页签列表 → 走的是继承，本来就会拿到 BasePawn 那份（含本页签），
                    // 或者压根不需要页签。两种情况都不该由我们插手。
                    continue;
                }

                // 只认「这是一份人形 pawn 的页签列表」——用原版必有的健康页签当判据。
                if (!tabs.Contains(typeof(ITab_Pawn_Health)))
                {
                    continue;
                }

                // 已经有本页签（正常继承来的，或别的补丁加的）→ 不动。
                if (tabs.Contains(tabType))
                {
                    continue;
                }

                try
                {
                    tabs.Add(tabType);
                    count++;

                    if (samples.Count < MaxSamples)
                    {
                        samples.Add(def.defName);
                    }
                }
                catch (Exception)
                {
                    // 某个 def 的列表不让改（理论上不会），跳过它继续，不影响其它种族。
                }
            }

            return count;
        }

        /// <summary>把样例拼成「a、b、c 等」。</summary>
        private static string DescribeSamples()
        {
            if (samples.Count == 0)
            {
                return "样本未记录";
            }
            string joined = string.Join("、", samples.ToArray());
            return patchedDefCount > samples.Count ? joined + " 等" : joined;
        }

        /// <summary>累计补过页签的 def 个数（供自检/文档引用）。</summary>
        internal static int PatchedDefCount
        {
            get { return patchedDefCount; }
        }
    }
}
