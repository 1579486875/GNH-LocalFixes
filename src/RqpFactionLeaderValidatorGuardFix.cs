// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【56】「Real Faction Guest」的背景校验器在遍历集合时改了集合 —— 帝国头衔那一批全废
    // ==========================================================================
    //
    // 【症状（本机日志原文）】
    //
    //   2026-11-12 19:30:17 ERROR [Verse] Real Faction Guest: InternalGen
    //   FactionLeaderValidator Failed.
    //   System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
    //   [Ref 14FE7878]
    //       at EventController_rQP.EventController_Work.Prefix_GenerateSkills(Pawn& pawn) [0x27]
    //       at .Verse.PawnGenerator.GenerateSkills_Patch5(Pawn pawn, PawnGenerationRequest request)
    //       …
    //       at VFEEmpire.WorldComponent_Hierarchy.MakePawnFor(RoyalTitleDef title) [0xA4]
    //       at VFEEmpire.WorldComponent_Hierarchy.InitializeTitles() [0x26]
    //       at VFEEmpire.WorldComponent_Hierarchy.FinalizeInit(Boolean fromLoad) [0x32]
    //
    //   紧跟着日志里还有三条（同一个下游）：
    //       [Miracle] Cannot arm imperial combatant 米兰达: violence/manipulation unavailable.
    //
    // 【模组定位】
    //
    //   工坊 3302093855（Real Faction Guest / EventController_rQP）。
    //   本机同时躺着 1.2 / 1.3 / 1.4 / 1.5 / 1.6 五个目录 —— 1.7 下走版本回退，
    //   实际加载的是 `1.6\Assemblies\EventController_rQP.dll`（1.6 目录确实存在，
    //   不要被「只有 1.5」这种旧说法带偏）。
    //
    // 【根因链（反编译实证，1.6 的 dll）】
    //
    //   `EventController_Work.Prefix_GenerateSkills(ref Pawn pawn)` 本体长这样：
    //
    //       public static void Prefix_GenerateSkills(ref Pawn pawn)
    //       {
    //           OngoingEvent ongoingEvent = OngoingEvent.FactionLeaderValidator;
    //           try
    //           {
    //               ongoingEvents |= ongoingEvent;
    //               if (RealFactionGuestSettings.factionLeaderValidator && pawn.kindDef.factionLeader)
    //                   PawnValidator_CrossWork.FactionLeaderValidator(ref pawn);
    //           }
    //           catch (Exception ex)
    //           {
    //               Tools.HandleEventControllerError(ex, ongoingEvent);   // ← 记成 ERROR 就完了
    //           }
    //       }
    //
    //   ⚠ 这里有一个**必须点名的判断错误**：这份「处方」最早写的是
    //   「给 Prefix_GenerateSkills 加 Finalizer 吞掉这个异常」—— **那条路走不通**。
    //   因为它自己就有 try/catch 把异常全接住了，异常**根本逃不出这个方法**，
    //   Finalizer 永远等不到它。真正该挂的地方是**再往里一层**：
    //   `PawnValidator_CrossWork.FactionLeaderValidator(ref Pawn)`。
    //
    //   那一层的代码是：
    //
    //       public static void FactionLeaderValidator(ref Pawn pawn)
    //       {
    //           if (pawn.WorkTagIsDisabled(WorkTags.Violent))
    //           {
    //               if (pawn.story.Childhood != null && …) FactionLeaderValidatorInner(ref pawn, Childhood);
    //               if (pawn.story.Adulthood != null && …) FactionLeaderValidatorInner(ref pawn, Adulthood);
    //               if (pawn.WorkTagIsDisabled(WorkTags.Violent)) PostFactionLeaderValidatorInner(ref pawn);
    //           }
    //       }
    //
    //   而 `FactionLeaderValidatorInner` 要做的事是「随机挑一个不禁暴力的背景故事，
    //   再赋给 pawn.story.Childhood / Adulthood」。赋值动作会触发 `Pawn_StoryTracker`
    //   的 setter，setter 又会牵动 traits / workSettings 这些集合 —— 一旦其中有谁
    //   正在被外层 `foreach` 遍历，就抛 `Collection was modified`。
    //
    // 【这条修复做了什么、没做什么】
    //
    //   做：给 `PawnValidator_CrossWork.FactionLeaderValidator(ref Pawn)` 挂一个 Finalizer，
    //       **只吞 `InvalidOperationException` 且消息里带 "Collection was modified" 的那一种**，
    //       并且每局最多打一条 Warning 说明「这一次背景修正被跳过了」。
    //
    //   不做：不碰那个「遍历时改集合」的源头。
    //       原因很实在 —— 要改它得写 transpiler 精确命中 `FactionLeaderValidatorInner`
    //       里某一条 `foreach`，改错的后果（把背景故事随机逻辑打乱）比现在这个症状严重得多。
    //       而现在这个症状的实际后果是：**这一次背景修正没做成**，异常被上层 catch 掉，
    //       游戏继续跑。也就是说，吞与不吞，**功能上都一样是失败**，
    //       区别只在于「日志里有没有一条把整段堆栈刷出来的红字」。
    //       所以这条属于「让日志说实话」，不是「把功能修好了」——
    //       **如实写在设置界面的说明里，不含糊。**
    //
    // 【自动让路】
    //
    //   · 模组没装 → `AccessTools.TypeByName` 返回 null → 跳过（打 Message，不是 Error）；
    //   · 方法签名变了 → 装不上，打 Error；
    //   · 别的异常（含真正的空引用）→ 原样上抛，一个都不替别人兜。
    // ==========================================================================

    /// <summary>
    /// 【56】把「Real Faction Guest」背景校验器里那一种「遍历时改集合」的异常单独摘掉。
    /// </summary>
    internal static class RqpFactionLeaderValidatorGuardFix
    {
        /// <summary>目标类型（模组自己的类，只能按字符串找）。</summary>
        private const string TargetTypeName = "EventController_rQP.PawnValidator_CrossWork";

        /// <summary>目标方法名。</summary>
        private const string TargetMethodName = "FactionLeaderValidator";

        /// <summary>`Log.WarningOnce` 的去重键（ASCII "GNHq"）。</summary>
        private const int WarnOnceKey = 0x474E4871;

        /// <summary>本补丁是否已装。</summary>
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
                    // 没装 Real Faction Guest —— 正常情况，不是错误。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【56】Real Faction Guest is not present; its"
                        + " background-validator guard was skipped (nothing to do).");
                    return;
                }

                // 目标签名是 `void FactionLeaderValidator(ref Pawn)`。
                // 反射里 ref 参数就是「按引用传递的 Pawn」，参数类型仍然是 Verse.Pawn。
                MethodInfo target = null;
                MethodInfo[] methods = targetType.GetMethods(
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
                    // ⚠ `ref Pawn` 在反射里的 ParameterType 是 `Pawn&`（ByRef），
                    //   直接跟 typeof(Pawn) 比会永远 false —— 2026-11-12 第一次实机
                    //   就栽在这里（日志原文：FactionLeaderValidator(…, Pawn) was not found）。
                    //   两种都接受，才能同时覆盖 ref 与非 ref 两种写法。
                    if (ps.Length == 1
                        && (ps[0].ParameterType == typeof(Pawn)
                            || ps[0].ParameterType == typeof(Pawn).MakeByRefType()))
                    {
                        target = m;
                        break;
                    }
                }

                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 【56】" + TargetTypeName + "." + TargetMethodName
                        + "(…, Pawn) was not found (its API probably changed); the guard is"
                        + " deferred and will retry on the next start.");
                    return;
                }

                MethodInfo finalizer = AccessTools.Method(
                    typeof(RqpFactionLeaderValidatorGuardFix), nameof(Finalizer));
                if (finalizer == null)
                {
                    Log.Error("[GNH LocalFixes] 【56】internal error: Finalizer not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, null, null, null, new HarmonyMethod(finalizer));

                installed = true;
                Log.Message("[GNH LocalFixes] 【56】patched " + TargetTypeName + "."
                    + TargetMethodName + " (finalizer only). A 'Collection was modified' from the"
                    + " backstory shuffler is now reported once instead of dumping a full stack"
                    + " every worldgen.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【56】failed to install the Real Faction Guest guard: " + ex);
            }
        }

        /// <summary>
        /// 只吞「遍历时改集合」这一种；别的异常原样返回给 Harmony 继续上抛。
        /// </summary>
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }

            // 精确到类型 + 消息，避免把别的 InvalidOperationException 也一起吞掉。
            if (!(__exception is InvalidOperationException))
            {
                return __exception;
            }

            string msg = __exception.Message ?? string.Empty;
            if (msg.IndexOf("Collection was modified", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return __exception;
            }

            Log.WarningOnce(
                "[GNH LocalFixes] 【56】Real Faction Guest 的背景故事校验器在遍历集合时改了集合，"
                + "这一次背景修正被跳过（上层本来也会 catch 掉它，故功能结果不变，只是不再把整段堆栈刷进日志）。"
                + " 根因未修：需要改那个模组的遍历写法，风险远高于收益。"
                + " (reported once per session)",
                WarnOnceKey);
            return null;
        }
    }
}
