// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
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
    // 【67】TSIND Garage Doors QA 的两条 XPath 一条都命中不了 —— 每次启动刷 4 条 ERROR
    // ==========================================================================
    //
    // ── 症状（2026-11-16 第四轮全局日志诊断 §6）──
    //
    //   12:00:23.288 ERROR [Verse] [TSIND Garage Doors QA - Start of stack trace]
    //     XmlExtensions.PatchOperationAddOrReplace(
    //        xpath='Defs/ThingDef[contains(@Name,"Garage") and contains(@Name,"Door")
    //               and not (contains(@Name,"Open"))]'): Failed to find a node referenced by <xpath>
    //     XmlExtensions.PatchByCase(value='StuffAirtight'): Error in <StuffAirtight> …
    //     XmlExtensions.UseSetting(key='TSIND_PAQA_GarageDoorInsulationLevel', …): Error in <apply> …
    //   [End of stack trace]
    //   Source file: …\294100\3547665095\1.7\Patches\TSIND_GDQA_AirtightGarageDoors.xml
    //
    //   第二条同源：
    //     XmlExtensions.PatchOperationAddOrReplace(
    //        xpath='Defs/ThingDef[defName[contains(text(),"Garage") and contains(text(),"Door")
    //               and contains(text(),"Open")] and fillPercent=0]'): Failed to find a node …
    //
    //   `grep -c "TSIND Garage Doors"` 本局 = **4**（12:00 两条、12:25 两条，各是一轮的开始与结尾）。
    //
    // ── 根因：那个 QA 补丁的本体模组根本没启用（已核实）──
    //
    //   · `Config\ModsConfig.xml` 的 1,178 条 activeMods 里**有** `tsind.garagedoors.qa`；
    //   · 但**没有任何**车库门本体（含 door 的只有 owlchemist.doorclearance /
    //     zal.personaldoors / jecrell.doorsexpanded / morph.advanceddoors 这几个，都不是它）；
    //   · 于是那两条「给车库门加气密保温」的 XPath 自然一条都命中不了。
    //
    //   换句话说：**这是个「可选增强补丁」在目标模组缺席时没有守卫** ——
    //   而那条守卫本该由作者用 `PatchOperationFindMod` 或 `<applyIf>` 写。
    //
    // ── 为什么我们不直接改它的 XPath / 也不替它加守卫 ──
    //
    //   那两处都是**作者的工坊文件**。改它会在作者更新时被覆盖，而且
    //   「给所有车库门加保温」是他的功能设计 —— 报告 §6.4 也明确写了
    //   「不要直接去改作者的 XPath」。**⛔ 那是破坏意图。**
    //
    // ── 我们怎么修：只在日志层静音，并留下可追溯的说明 ──
    //
    //   与【65】`debugSpamPrefix` 同一套写法 —— 给 `Log.Error(string)` 加 Prefix，
    //   消息里带 `[TSIND Garage Doors QA` 时吞掉。
    //
    //   为什么这个判据是安全的：
    //     · `[TSIND Garage Doors QA` 是**作者自己打的日志前缀**（格式是 `[模组名] 说明`），
    //       正常日志里不会有别的来源用它；
    //     · 只在**确实命中不了**时才会出现（XmlExtensions 的 XPath 失败路径），
    //       所以静音它等于「这条可选增强没生效」，与现状等价；
    //     · **吞掉的是 ERROR 不是异常** —— 游戏流程完全不受影响（XmlExtensions 自己已经接住了）。
    //
    //   ⚠ 副作用（必须在文档里写清）：如果哪天玩家真的装了车库门本体，
    //     而作者的 XPath 仍然写错，这条也会被一并静音 —— 那时玩家看到的是
    //     「保温没生效但没有报错」。**所以我们不静默**：第一次过滤时打一条 Message，
    //     写明「这条是被 GNH 静音的、原因是本体缺席、若你装了本体仍无效请找作者」。
    //
    // ── 自动让路 ──
    //
    //   · `Log.Error(string)` 找不到（引擎改名）→ 打 Message 跳过；
    //   · 玩家关掉本项 → 整项不安装，回到每次启动刷 4 条 ERROR 的原状；
    //   · 作者将来自己加了守卫 → 那条 ERROR 不再出现，本项自然什么都不做。
    // ==========================================================================

    /// <summary>
    /// 【67】把 TSIND Garage Doors QA 在「本体模组缺席」时刷的 4 条 ERROR 静音，
    /// 并在第一次过滤时留一条可追溯的说明。
    /// </summary>
    internal static class TsindGarageDoorsQuietFix
    {
        /// <summary>作者的日志前缀（含方括号，正常日志里不会有第二家用它）。</summary>
        private const string LogPrefix = "[TSIND Garage Doors QA";

        /// <summary>本补丁是否已装。</summary>
        private static bool installed;

        /// <summary>被静音的条数。</summary>
        private static int mutedCount;

        /// <summary>是否已经为「静音」打过一次说明。</summary>
        private static bool muteLogged;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Log), "Error", new[] { typeof(string) }, null);
                if (target == null)
                {
                    Log.Message("[GNH LocalFixes] 【67】Log.Error(string) not found;"
                        + " the TSIND Garage Doors noise filter was skipped.");
                    installed = true;
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(TsindGarageDoorsQuietFix), nameof(Prefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【67】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(prefix), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【67】TSIND Garage Doors QA noise filter installed"
                    + " (silences its \"Failed to find a node\" errors when the garage-door"
                    + " mod itself is absent; the first silenced line is explained in the log).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【67】failed to install the TSIND noise filter: " + ex);
            }
        }

        /// <summary>
        /// 返回 false 表示吞掉这条日志。
        /// 判据只有一条：消息以作者的前缀开头。
        /// </summary>
        public static bool Prefix(string text)
        {
            try
            {
                if (text == null)
                {
                    return true;
                }
                if (text.IndexOf(LogPrefix, StringComparison.Ordinal) < 0)
                {
                    return true;
                }

                mutedCount++;
                if (!muteLogged)
                {
                    muteLogged = true;
                    // 这条 Message 本身不能被上面那个 Prefix 拦住（前缀不同），所以一定打得出来。
                    Log.Message("[GNH LocalFixes] 【67】TSIND Garage Doors QA 的「找不到节点」错误已被静音。"
                        + "原因：那个补丁是「给车库门加气密保温」的可选增强，"
                        + "而本机 ModsConfig 里没有它的车库门本体模组（只有它的 QA 补丁）。"
                        + "若你确实装了车库门本体、保温仍不生效，那说明作者的 XPath 需要更新 ——"
                        + "关掉本项即可让错误重新出现，方便你向作者反馈。"
                        + "（本条及后续同类只计数，不再逐条打印。）");
                }
                return false;
            }
            catch (Exception)
            {
                // 过滤日志的代码绝不能自己抛异常。
                return true;
            }
        }

        /// <summary>被静音的条数（供自检/文档引用）。</summary>
        internal static int MutedCount
        {
            get { return mutedCount; }
        }
    }
}
