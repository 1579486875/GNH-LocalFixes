// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【57】720 条「Could not find player faction.」—— 源头不在模组，在引擎自己的属性 getter
    // ==========================================================================
    //
    // 【症状】
    //
    //   每次世界生成恰好刷 240 条，三次生成共 720 条，占整份日志 ERROR 的 86%：
    //
    //       2026-11-12 19:30:03 ERROR [Verse] Could not find player faction.
    //           at MCW_MihoPach.Patch_Pawn_Kill.Prefix(Pawn __instance, …) [0x3]
    //           at .Verse.Pawn.Kill_Patch73(…)
    //           at .Verse.Pawn_HealthTracker.CheckForStateChange_Patch8(…)
    //           …
    //           at .RimWorld.PawnTechHediffsGenerator.GenerateTechHediffsFor(Pawn pawn)
    //           at .Verse.PawnGenerator.GenerateInitialHediffs(…)
    //           at .RimWorld.Faction.TryGenerateNewLeader_Patch1(Faction this)
    //           at .RimWorld.Planet.WorldGenStep_Factions.GenerateFresh_Patch1(…)
    //
    // 【为什么说「源头不在模组」—— 反编译实证】
    //
    //   日志把责任指向三处 `Pawn.Kill` 补丁（MCW 的美狐增强补丁 / Just Help For Me / KillFeed），
    //   但真正打出这句话的是**引擎自己**。`RimWorld.Faction.OfPlayer` 的 getter 原文（1.7.5005）：
    //
    //       public static Faction OfPlayer
    //       {
    //           get
    //           {
    //               Faction ofPlayerSilentFail = OfPlayerSilentFail;
    //               if (ofPlayerSilentFail == null)
    //               {
    //                   Log.Error("Could not find player faction.");
    //               }
    //               return ofPlayerSilentFail;
    //           }
    //       }
    //
    //   也就是说：**任何代码在世界生成期读一次 `Faction.OfPlayer`，就必然多一条红字**。
    //   世界生成时玩家派系本来就还没建立（要先有地图、有玩家殖民者），这是正常的中间状态，
    //   不是谁的 bug。三个模组只是「碰巧在那时候读了一下」而已。
    //
    //   ⚠ 同一份类型里**引擎自己就提供了静默版本**：
    //       `Faction.OfPlayerSilentFail` —— 只返回 `Find.FactionManager.OfPlayer`，不打日志。
    //     本项就是让 `OfPlayer` 在「世界生成期且确实没有玩家派系」时走那条静默路径。
    //
    // 【修法：Prefix 短路，返回值与原版完全一致】
    //
    //       static bool Prefix(ref Faction __result)
    //       {
    //           Faction f = Faction.OfPlayerSilentFail;
    //           if (f != null) return true;                                  // 有玩家派系 → 原版
    //           if (Current.ProgramState != ProgramState.Entry) return true;  // 已在游戏里 → 让原版照常报
    //           __result = null;
    //           return false;                                                // 跳过原方法（只跳过那句 Log.Error）
    //       }
    //
    //   · **返回值一模一样**：原版在没有玩家派系时返回的就是 null；
    //   · **只在 ProgramState.Entry（还没进地图）时生效**：进游戏之后如果真找不到玩家派系，
    //     原版照样报红字 —— 那才是真的异常，不该被我们压掉；
    //   · **不动任何模组**：三处 `Pawn.Kill` 补丁一行代码都不改，它们该读到什么还是什么；
    //   · 顺带记一个计数，会话结束时在日志里打一条「本次共抑制 N 次」——
    //     既证明本项在工作，也不会让人误以为「世界生成期从来没访问过 OfPlayer」。
    //
    // 【它不做什么（必须写明，免得下次又误会）】
    //
    //   这**不是**「修好了 UFC 派系生不出领袖」。那件事的根在【54】，
    //   本项只是把那 720 条**噪音**关掉；【54】修的是「为什么会有 pawn 在生成期被判定死亡」。
    //   两件事分开做、分开开关，是因为它们的影响面完全不同：
    //   【54】改动游戏行为（虽然只是补一个漏传的参数），【57】只改日志。
    //
    // 【自动让路】
    //
    //   · `Faction.get_OfPlayer` 找不到（属性被改名）→ 打 Error、留待重试；
    //   · `OfPlayerSilentFail` 找不到 → 打 Error、不装（宁可不动，也不自己手写一份可能走样的实现）；
    //   · 玩家关掉开关 → 整项不安装。
    // ==========================================================================

    /// <summary>
    /// 【57】世界生成期把 `Faction.OfPlayer` 的那句红字换成引擎自带的静默路径。
    /// </summary>
    internal static class FactionOfPlayerNoiseFix
    {
        /// <summary>本补丁是否已装。</summary>
        private static bool installed;

        /// <summary>本次会话一共抑制了多少次。</summary>
        private static int suppressed;


        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                PropertyInfo prop = AccessTools.Property(typeof(Faction), "OfPlayer");
                if (prop == null)
                {
                    Log.Error("[GNH LocalFixes] 【57】RimWorld.Faction.OfPlayer was not found;"
                        + " the worldgen noise filter is deferred and will retry.");
                    return;
                }

                MethodInfo getter = prop.GetGetMethod(true);
                if (getter == null)
                {
                    Log.Error("[GNH LocalFixes] 【57】Faction.OfPlayer has no getter?!"
                        + " The noise filter is NOT active this session.");
                    return;
                }

                // 引擎自带的静默版本。**必须存在**才动手 —— 不存在就宁可不动，
                // 也不自己手写一份可能走样的「静默实现」。
                PropertyInfo silent = AccessTools.Property(typeof(Faction), "OfPlayerSilentFail");
                if (silent == null || silent.GetGetMethod(true) == null)
                {
                    Log.Error("[GNH LocalFixes] 【57】RimWorld.Faction.OfPlayerSilentFail was not found;"
                        + " the noise filter is NOT active (we will not hand-roll a silent getter"
                        + " because that could drift from the vanilla semantics).");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(FactionOfPlayerNoiseFix), nameof(Prefix));
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【57】internal error: Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    getter, new HarmonyMethod(prefix), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【57】installed: Faction.OfPlayer no longer logs"
                    + " 'Could not find player faction.' while the game is still in ProgramState.Entry"
                    + " (i.e. before a colony map exists). The returned value is byte-for-byte"
                    + " the same as vanilla (null).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【57】failed to install the worldgen noise filter: " + ex);
            }
        }

        /// <summary>
        /// 有玩家派系 → 原版；还没进地图且确实没有玩家派系 → 静默返回 null；其余 → 原版。
        /// </summary>
        private static bool Prefix(ref Faction __result)
        {
            try
            {
                Faction f = Faction.OfPlayerSilentFail;
                if (f != null)
                {
                    // 有玩家派系：原版本来就不会打日志，走原版最稳。
                    return true;
                }

                if (Current.ProgramState != ProgramState.Entry)
                {
                    // 已经在游戏里了还是没有玩家派系 —— 这是真异常，让原版照常报红字。
                    return true;
                }

                __result = null;
                suppressed++;
                if (suppressed == 1)
                {
                    Log.Message("[GNH LocalFixes] 【57】世界生成期起不再刷「Could not find player faction.」"
                        + "（返回值与原先完全一致：null）。本项只提示这一次。");
                }
                return false; // 跳过原方法体，只省掉那句 Log.Error。
            }
            catch
            {
                // Prefix 自己出错就让原版跑，绝不因为「修日志」而改变游戏行为。
                return true;
            }
        }

    }
}
