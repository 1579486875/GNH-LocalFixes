// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【45】单个 HarmonyPatch 类解析失败时，不再连累同一批的其它补丁。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // Harmony 的 Harmony.PatchAll(Assembly) 长这样（2.3 / 2.4 都一样，已反编译核对）：
    //
    //     public void PatchAll(Assembly assembly)
    //     {
    //         AccessTools.GetTypesFromAssembly(assembly).DoIf(
    //             type => type.HasHarmonyAttribute(),
    //             delegate(Type type) { CreateClassProcessor(type).Patch(); });   // ← 没有 try/catch
    //     }
    //
    // 而 PatchClassProcessor.Patch() 的设计是「把内部异常收集起来，最后转成
    // HarmonyException 抛出去」：
    //
    //     catch (Exception ex) { exception = ex; }
    //     RunMethod<HarmonyCleanup>(ref exception, new object[] { exception });
    //     ReportException(exception, lastOriginal);
    //     return result;
    //
    //     private void ReportException(Exception exception, MethodBase original)
    //     {
    //         if (exception == null) return;
    //         ...
    //         if (exception is HarmonyException) throw exception;
    //         throw new HarmonyException("Patching exception in method " + ..., exception);
    //     }
    //
    // 也就是说：**只要有一个 patch 类解析不到目标方法，Patch() 必抛**，
    // 而 PatchAll 的 foreach 没有兜底 —— 整个方法立刻中断，
    // 排在它后面的所有 patch 类**一个都不会被处理**。
    //
    // 【后果有多严重】
    //
    // 模组作者通常把 PatchAll 写在 [StaticConstructorOnStartup] 的静态构造里：
    //
    //     [StaticConstructorOnStartup]
    //     internal static class PostLoadPatcher
    //     {
    //         static PostLoadPatcher() { new Harmony("xxx").PatchAll(); }
    //     }
    //
    // 于是「一个签名过期」会变成 TypeInitializationException，
    // 该模组 30~40 个补丁**全部失效**，玩家看到的功能缺失与日志里那一条红字
    // 完全不成比例 —— 红字只提了一个方法，实际死掉的是整个模组。
    //
    // 【本机实测到的两个例子（2026-11-07）】
    //
    //   · RimtalkContext（RimTalk Context Upgrade）：
    //       PawnUtil_DecorateText_Patch::Prefix 写死 2 参数，
    //       而 RimTalk 1.3 已把 DecorateText 改成 3 参数
    //       （string text, HashSet<Pawn> relevantPawns, Pawn selfPawn = null，已反编译核对）。
    //       结果：RCU 的 ContextBuilder / PromptService / RelationService / PromptManager /
    //             TalkRequest / TalkResponse 等 30 余个补丁集体消失。
    //
    //   · CQF-RT（Custom Quest Framework 的 RimTalk 扩展）：
    //       Patch_Dialog::postfix 要挂 QuestEditor_Library.CQFDialogTreeWindow，
    //       而那个类型根本不在（CQF 本体未启用）。同样整批失效。
    //
    // 两个模组、两种完全不同的原因，倒在同一个地方 —— 这说明它不是「某个作者的偶发失误」，
    // 而是 Harmony 使用方式上的**结构性缺陷**。
    //
    // 【我们怎么修】
    //
    // 给 PatchClassProcessor.Patch() 挂一个 Finalizer：
    //
    //   正常情况（不抛异常）—— 原方法照常返回，本补丁零开销、等于不存在。
    //
    //   出异常时 —— 打一条能定位到「哪个补丁类」的警告，
    //   把返回值补成空表，然后**吞掉异常**，让 PatchAll 的循环继续跑下一个类。
    //
    // 这样坏掉的那一个仍然不生效（它本来就不可能生效），
    // 但同一模组里其余几十个补丁能正常装上，模组功能从「全废」回到「少一个」。
    //
    // 【为什么不干脆让它静默】
    //
    // 因为「悄悄少装一个补丁」比「报错」更难查。玩家反馈「某功能不灵」时，
    // 日志里必须留一句能搜到的话 —— 所以这里用 Log.Warning 而不是吞掉了事，
    // 并且把**补丁类名**一起写进去（Harmony 自己那条红字只说 patch 方法名，
    // 不说它属于哪个类，排查时还得再翻一遍）。
    //
    // 【为什么不改 Harmony.DEBUG】
    //
    // 有人会想「关掉调试模式是不是就不抛了」。不是。
    // 反编译 ReportException 可见：`if (containerAttributes.debug || Harmony.DEBUG)`
    // 那一段只控制**是否写 FileLog**，两个 throw 是无条件的。
    // 关调试只会让日志更少，异常照抛。
    //
    // 【为什么挂在 Patch() 而不是 PatchAll()】
    //
    //   · 挂在 PatchAll 上 = 整个批次失败，比现在更糟；
    //   · 挂在 Patch() 上 = 粒度正好是「一个补丁类」，与 Harmony 自己的错误边界一致。
    [HarmonyPatch(typeof(PatchClassProcessor), "Patch", new Type[0])]
    public static class Patch_PatchClassProcessor_Isolate
    {
        // PatchClassProcessor.containerType 是 private 字段，取一次缓存起来。
        // 取不到也不影响功能 —— 只是日志里少个类名。
        private static readonly FieldInfo ContainerTypeField =
            AccessTools.Field(typeof(PatchClassProcessor), "containerType");

        /// <summary>
        /// 目标方法不存在时（Harmony 版本大变）直接让路，不要在启动时刷红字。
        /// </summary>
        [HarmonyPrepare]
        public static bool Prepare()
        {
            return AccessTools.Method(typeof(PatchClassProcessor), "Patch", Type.EmptyTypes) != null;
        }

        /// <summary>
        /// 只在 Patch() 抛异常时才执行。正常安装路径完全不经过这里。
        /// </summary>
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception,
                                          PatchClassProcessor __instance,
                                          ref List<MethodInfo> __result)
        {
            if (__exception == null)
            {
                return null; // 没异常：什么都不做，把结果原样放行。
            }

            // Patch() 抛的时候返回值还没赋给 __result，这里必须给个能用的空表，
            // 否则调用方（Harmony.PatchAll 之外，还有我们自己的 InstallPatchClass）
            // 拿到 null 再 .Count 就会二次崩。
            if (__result == null)
            {
                __result = new List<MethodInfo>();
            }

            // 日志本身绝不能再抛 —— 这是最后一道保险。
            try
            {
                string className = "(unknown patch class)";
                try
                {
                    if (ContainerTypeField != null && __instance != null)
                    {
                        Type t = ContainerTypeField.GetValue(__instance) as Type;
                        if (t != null)
                        {
                            className = t.FullName ?? t.Name;
                        }
                    }
                }
                catch (Exception)
                {
                    // 取不到类名就用占位符，不影响主流程。
                }

                // 内层异常往往比外层更有信息量（外层永远是 HarmonyException 包装）。
                Exception inner = __exception;
                if (inner is HarmonyException && inner.InnerException != null)
                {
                    inner = inner.InnerException;
                }

                Log.Warning("[GNH LocalFixes] 【45】有一个 Harmony 补丁类解析失败，已隔离。"
                    + "同一批次的其它补丁会继续安装，不受影响。\n"
                    + "  补丁类：" + className + "\n"
                    + "  原因：" + inner.GetType().Name + ": " + inner.Message + "\n"
                    + "  提示：这通常是「目标方法签名变了」或「目标所在模组没启用」。"
                    + "坏掉的这一个仍然不会生效，但不会再拖垮整个模组。");
            }
            catch (Exception)
            {
                // 连日志都写不出去，就只能放弃了 —— 静默吞掉，绝不向上抛。
            }

            return null; // 吞掉异常，让 PatchAll 的循环继续。
        }
    }
}
