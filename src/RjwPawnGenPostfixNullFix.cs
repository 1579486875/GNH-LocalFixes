// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // RJW（RimJobWorld）本体：生成失败时没判空，把「某个派系 BOSS 生不出来」
    // 放大成「整个世界生成步骤失败」
    // ==========================================================================
    //
    // 【症状（本机日志原文）】
    //
    //   2x  Error while generating pawn. Rethrowing. Exception: System.NullReferenceException
    //   2x  Error in WorldGenStep: System.NullReferenceException
    //
    //   堆栈首帧：
    //
    //       at rjw.Patch_PawnGenerator_GenerateNewPawnInternal.Postfix(
    //              PawnGenerationRequest request, Pawn __result)
    //          in /rjw/Harmony/Patch_PawnGenerator.cs:line 33
    //
    //   完整的上游链条（同一次堆栈里）：
    //
    //       WorldGenStep_Factions.GenerateFresh
    //         -> FactionGenerator.CreateFactionAndAddToManager
    //         -> Faction.TryGenerateNewLeader
    //         -> PawnGenerator.GeneratePawn
    //         -> PawnGenerator.GenerateOrRedressPawnInternal
    //              "Could not generate a pawn after 70 / 100 / 120 tries.
    //               Last error: Generated dead pawn."          <-- 生成失败，返回 null
    //         -> PawnGenerator.GenerateNewPawnInternal            <-- __result = null
    //         -> rjw.Patch_PawnGenerator_GenerateNewPawnInternal.Postfix   <-- 在这里炸
    //
    // 【根因：反编译原文】
    //
    //       [HarmonyPatch(typeof(PawnGenerator), "GenerateNewPawnInternal")]
    //       internal static class Patch_PawnGenerator_GenerateNewPawnInternal
    //       {
    //           public static void Postfix(PawnGenerationRequest request, Pawn __result)
    //           {
    //               __result.GetCompRJW()?.Sexualize();     // <-- 第 33 行，__result 为 null 时炸
    //               FixNymph(request, __result);
    //               FixNewbornStyles(request, __result);
    //           }
    //       }
    //
    //   三处都直接解引用 `__result`：
    //     · `__result.GetCompRJW()` —— 扩展方法的第一个参数就是 pawn，null 进去内部就 NRE；
    //       `?.` 只保护了后面那次 `Sexualize()`，保护不到 `__result` 自己。
    //     · `FixNymph` 里 `Nymph_Generator.set_story(pawn)` 同样吃 null。
    //     · `FixNewbornStyles` 里 `pawn.style != null` —— **`pawn` 为 null 时读字段就已经炸了**。
    //
    //   `__result` 为什么会是 null？上游 `GenerateOrRedressPawnInternal` 重试 70 / 100 / 120 次
    //   都拿到 `Generated dead pawn`，最后 `return null`。触发者是 `kindDef = UFC_BOSS`
    //   （联合水果优品公司）自己的基因/hediff 组合 —— **那不是 RJW 的锅**。
    //   RJW 的锅只有一件：**它没判空**，于是「某个派系领袖生不出来」被放大成
    //   `Error in WorldGenStep` —— 轻则该派系没有领袖，重则新档建不出来。
    //
    // ==========================================================================
    // 【修法：Finalizer 兜底 —— 以及为什么不用 transpiler】
    // ==========================================================================
    //
    //   ⚠⚠ 这一项在 2026-11-12 一天里被推翻过两次，两次都记在下面，
    //      免得下一个人再走一遍同样的弯路。
    //
    //   ── 第一次尝试：在方法开头插「__result 为 null 就 return」（transpiler）──
    //
    //   想法很直接，但**在 Harmony 3.x 下装不上**。实测两条错误：
    //
    //     ① 先试 `new CodeInstruction(OpCodes.Brtrue, list[0])`（把另一条指令当跳转目标）：
    //          System.InvalidOperationException: Unexpected unemittable operand type HarmonyLib.CodeInstruction
    //        Harmony 2.x 接受这种写法、内部帮你转成 label；**3.x 不再接受**，
    //        必须自己把一个 `Label` 塞进目标指令的 `labels` 列表、再把同一个 `Label`
    //        当 operand。本机 1.7 用的是 Harmony 3.0，所以这条直接失败。
    //
    //     ② 更早一版还试过「transpiler + finalizer（带 `Pawn __result` 参数）」：
    //          System.ArgumentException: A void operation has no result:
    //            … parameter __result bound to __result, supplied System.Void, requested Verse.Pawn
    //        原因是 **RJW 的 Postfix 返回 void**，Harmony 在 finalizer 里不接受
    //        把 `__result` 绑到一个没有返回值的方法上。
    //
    //     两次失败还有个共同的坏副作用：`Patch()` 整体抛异常 → `installed` 不置位 →
    //     日志写「本项未激活」，**而 IL 其实已经被改过**（transpiler 的改写发生在
    //     参数绑定校验之前）。「说没生效、实际生效」是最难排查的状态。
    //
    //   ── 最终方案：只用 Finalizer，而且**不声明 `__result` 参数** ──
    //
    //          [HarmonyFinalizer]
    //          static Exception Finalizer(Exception __exception)
    //          {
    //              if (__exception is NullReferenceException) { 记一条; return null; }   // 吞
    //              return __exception;                                                    // 放行
    //          }
    //
    //   · 没有 IL 改写 → 不碰 Harmony 3 的分支/label 限制；
    //   · finalizer 不声明 `__result` → 不碰「void 方法没有返回值」的限制；
    //   · 语义上正好：一旦这个 Postfix 抛 NRE，`GenerateNewPawnInternal` 立刻返回 null，
    //     `WorldGenStep_Factions` 拿到 null 就当「这个领袖没生成出来」继续往下走 ——
    //     这正是我们要的结果。
    //
    //   代价（如实写明）：**吞的是整个方法（含其它模组挂在同一方法上的 postfix）的 NRE**，
    //   不只是 RJW 那一条。所以：
    //     · 只在 `NullReferenceException` 上动手，别的异常原样放行；
    //     · 吞掉时用 `Log.ErrorOnce` 记一条（每局最多一条），
    //       日志里能查到「这次吞了一次、原因大概率是某个 pawnkind 生不出活人」；
    //     · 这条修复**不负责**修那个生不出小人的派系 —— 它只负责别让一个失败拖垮整步。
    //
    // 【自动让路（本工程纪律 §3.1）】
    //
    //   · 类型 `rjw.Patch_PawnGenerator_GenerateNewPawnInternal` 找不到 -> 没装 RJW，
    //     打一行 Message 跳过，不影响任何其它修复项；
    //   · `Postfix` 方法找不到 / 签名变了 -> 打 Error 并**不置位 installed**，留给下次重试。
    //
    // 【对 1.6 的兼容】
    //
    //   全程字符串反射找类型与方法，不写 `typeof(rjw.…)`；
    //   `PawnGenerator.GenerateNewPawnInternal` 在 1.6 与 1.7 都是
    //   `Pawn GenerateNewPawnInternal(ref PawnGenerationRequest)`（已反编译核对，两版一致）。
    //   `[HarmonyFinalizer]` 本身在 Harmony 2.x / 3.x 都有，签名 `Exception(Exception)` 两版通用。
    // ==========================================================================
    public static class RjwPawnGenPostfixNullFix
    {
        /// <summary>RJW 那个补丁类的完整类型名（`internal static class`，只能反射取）。</summary>
        private const string TargetTypeName = "rjw.Patch_PawnGenerator_GenerateNewPawnInternal";

        /// <summary>它的方法名。</summary>
        private const string TargetMethodName = "Postfix";

        /// <summary>`Log.ErrorOnce` 的去重键（ASCII "GNHW"）。</summary>
        private const int ErrorOnceKey = 0x474E4857;

        /// <summary>本补丁是否已经装过。</summary>
        private static bool installed;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type targetType = AccessTools.TypeByName(TargetTypeName);
                if (targetType == null)
                {
                    // 没装 RJW —— 正常情况，不是错误。
                    installed = true;
                    Log.Message("[GNH LocalFixes] RJW is not present; its pawn-generation null guard was skipped"
                        + " (nothing to do, other fixes are unaffected).");
                    return;
                }

                MethodInfo target = targetType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == TargetMethodName
                                         && m.GetParameters().Length == 2
                                         && m.GetParameters()[1].ParameterType == typeof(Pawn));
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] RJW is present but " + TargetTypeName + "." + TargetMethodName
                        + "(…, Pawn) was not found (its API probably changed);"
                        + " the null guard is deferred and will retry.");
                    return;
                }

                MethodInfo finalizer = AccessTools.Method(typeof(RjwPawnGenPostfixNullFix), nameof(Finalizer), null, null);
                if (finalizer == null)
                {
                    Log.Error("[GNH LocalFixes] Internal error: RjwPawnGenPostfixNullFix.Finalizer not found."
                        + " The RJW null guard is NOT active this session.");
                    return;
                }

                // 只装 finalizer。理由见文件头「修法」那一大段。
                LocalFixesMod.HarmonyInstance.Patch(target, null, null, null, new HarmonyMethod(finalizer));

                installed = true;

                Patches info = Harmony.GetPatchInfo(target);
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " (finalizers=" + (info != null ? info.Finalizers.Count : -1) + ")."
                    + " A NullReferenceException from that postfix (pawn generation gave up and returned null)"
                    + " is now swallowed once per session instead of aborting WorldGenStep_Factions.");
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Failed to install the RJW pawn-generation null guard"
                    + " (that fix is NOT active this session; everything else is unaffected): " + ex);
            }
        }

        /// <summary>
        /// 兜底：RJW 那个 Postfix 在「生成失败（返回 null）」时必然抛 NRE，这里吞掉它。
        ///
        /// ⚠ 刻意**不声明 `Pawn __result` 参数**：RJW 的 Postfix 返回 void，
        ///   Harmony 会拒绝把 `__result` 绑到没有返回值的方法上
        ///   （实测 `ArgumentException: A void operation has no result`）。
        /// </summary>
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!(__exception is NullReferenceException))
            {
                // 别的异常（含别的模组 postfix 抛的）原样放行，绝不替它们兜。
                return __exception;
            }

            Log.ErrorOnce(
                "[GNH LocalFixes] A NullReferenceException from RJW's pawn-generation postfix was swallowed so"
                    + " that WorldGenStep_Factions can continue. This almost always means pawn generation itself"
                    + " failed upstream (a pawnkind whose genes/hediffs always produce a dead pawn) —"
                    + " that part is NOT fixed by this patch; it only stops one failure from aborting a whole"
                    + " worldgen step. (reported once per session)",
                ErrorOnceKey);
            return null;
        }
    }
}
