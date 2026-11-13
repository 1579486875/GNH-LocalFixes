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
    // 【68】鼠族基因扩展把三条调试输出留在了 PostExposeData 里
    // ==========================================================================
    //
    // ── 症状（2026-11-13 第三轮全局日志诊断 P3-5）──
    //
    //   日志里每次读档刷：
    //     Debug:________isCustomSite: True
    //     Debug:________parentQuest: …
    //     Debug:________customQuests count: 1
    //
    //   `Debug:` 后面那串下划线本身就说明「这是开发者自己加的、准备事后删的」。
    //
    // ── 定位（本轮）──
    //
    //   全库扫 5,505 个 dll + 132,043 个 xml（按 UTF-16LE 搜字符串），
    //   三个 needle 只在**一个**文件里命中：
    //     RatkinGeneExpanded.dll（工坊 3043354134，鼠族基因扩展，1.6/Common/Assemblies）
    //
    //   反编译 `RatkinGeneExpanded.CustomSiteComp.PostExposeData()`：
    //
    //       public override void PostExposeData()
    //       {
    //           base.PostExposeData();
    //           Scribe_Collections.Look(ref customQuests, "customQuests", …);
    //           Scribe_References.Look(ref parentQuest, "quest", false);
    //           Scribe_Values.Look(ref isCustomSite, "isCustomSite", false, false);
    //           Log.Warning("Debug:________isCustomSite: " + isCustomSite);        // ★
    //           Log.Warning("Debug:________parentQuest: " + parentQuest);          // ★
    //           Log.Warning("Debug:________customQuests count: " + …);             // ★
    //           if (isCustomSite) TryInitializeCustomSite(parentQuest);
    //       }
    //
    //   三条 `Log.Warning` 是**纯调试输出**，夹在 `Scribe_*` 与 `TryInitializeCustomSite`
    //   之间。它们不做任何判断、不改变任何状态。
    //
    // ── 为什么用 Prefix 过滤而不是 Transpiler 改 IL ──
    //
    //   Transpiler 能精确只改这一个方法，但要写 IL 匹配（三条 `ldstr` + `call Log.Warning`），
    //   一旦作者改了字符串或调整了语句顺序就会失配 —— 那时代码会静默不生效。
    //
    //   而 `Log.Warning(string)` 的 **Prefix 按前缀过滤**：
    //     · 判据是 `text.StartsWith("Debug:________")` —— 那串下划线是作者自己打的标记，
    //       正常日志里不可能出现；
    //     · 它不依赖「哪个方法调用」、也不依赖语句顺序，作者怎么改都还能拦住；
    //     · 顺带把**任何模组**同类风格的调试输出也挡住（只要它也用这个前缀）。
    //
    //   ⚠ 唯一的代价：它是全局的。所以判据必须**极窄** —— 只认那 13 个字符的前缀，
    //     而不是「包含 Debug」这种宽判据（那样会误杀真正需要的诊断）。
    //
    // ── 自动让路 ──
    //
    //   · 作者哪天自己删掉这三行 → 前缀不再出现 → 这条 Prefix 每帧只多一次
    //     `StartsWith` 判断（纳秒级），等于空转；
    //   · `Log.Warning(string)` 这个重载找不到 → 整项跳过；
    //   · 出错就放行（绝不因为「修」而吞掉别人真正的警告）。
    // ==========================================================================

    /// <summary>
    /// 【68】过滤以 `Debug:________` 开头的调试输出。
    /// </summary>
    internal static class DebugSpamPrefixFix
    {
        /// <summary>作者自己打的调试前缀（13 个字符 + 8 个下划线）。</summary>
        private const string DebugPrefix = "Debug:________";

        private const int ErrorOnceKey = 0x474E486E;   // ASCII "GNHn"

        private static bool installed;
        private static int suppressed;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // 只 patch (string) 这个重载 —— Log.Warning 还有 (string, bool) 等签名。
                MethodInfo target = AccessTools.Method(
                    typeof(Log), "Warning", new[] { typeof(string) }, null);
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【68】Log.Warning(string) was not found"
                        + " (its API probably changed); the fix is deferred and will retry.");
                    return;
                }

                MethodInfo pre = AccessTools.Method(
                    typeof(DebugSpamPrefixFix), nameof(Prefix));
                if (pre == null)
                {
                    Log.Error("[GNH LocalFixes] 【68】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, new HarmonyMethod(pre), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【68】patched Log.Warning(string) with a narrow"
                    + " prefix filter. Lines starting with \"" + DebugPrefix + "\" are dropped —"
                    + " that marker comes from RatkinGeneExpanded.CustomSiteComp.PostExposeData(),"
                    + " where three debug Log.Warning calls were left in by the author."
                    + " Nothing else is filtered: the check is a 13-character prefix, not a fuzzy match.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【68】failed to install: " + ex);
            }
        }

        /// <summary>
        /// 只拦「以那 13 个字符开头」的警告；其余一律放行。
        /// </summary>
        private static bool Prefix(string text)
        {
            try
            {
                if (text == null || text.Length < DebugPrefix.Length)
                {
                    return true;
                }
                if (!text.StartsWith(DebugPrefix, StringComparison.Ordinal))
                {
                    return true;
                }

                suppressed++;
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] 【68】guard threw, allowing the warning: "
                    + ex.Message, ErrorOnceKey);
                return true;
            }
        }
    }
}
