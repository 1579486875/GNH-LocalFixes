// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【41】读档刷 138 条 `/fixPawn` 的 loadID 报错
    //
    // ── 症状（用户 2026-11-01 实机复现，v1.4.19 没拦住）──
    //
    //   读档瞬间（13:33:22 起）一口气刷出：
    //     Tried to register the same load ID twice: null, pathRelToParent=/fixPawn, parent=…
    //     Could not get load ID. We're asking for something which was never added during LoadingVars…
    //   共 138 条，涉及 NivarianRace / Axolotl / Milira / Yuran / SnowRabbit 等 ~69 个 Pawn。
    //
    // ── 根因 ──
    //
    //   RJW「裸体评价」（`dord.nuditymattersmore`）的 `Fixation.PostExposeData`：
    //
    //       private Pawn fixPawn;
    //       Scribe_References.Look(ref fixPawn, "fixPawn");     // ← 无条件
    //       …
    //       if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
    //       if (!hasFixation || fixPawn == null || fixPawn.Dead) { hasFixation = false; fixPawn = null; }
    //
    //   `Scribe_References.Look<T>` 在**两个阶段各跑一次**：
    //     · LoadingVars        → `RegisterLoadIDReadFromXml(targetLoadID, typeof(T), label)`
    //                            `targetLoadID` 是 null → 登记了一个 **null loadID** → 「register the same load ID twice」
    //     · ResolvingCrossRefs → `TakeResolvedRef<T>(label)` → 内部 `Take<T>` 查 `idsRead`
    //                            查不到 → 「never added during LoadingVars」
    //
    //   也就是说：**LightingVars 阶段登记的那个 null，正是 ResolvingCrossRefs 阶段报错的来源。**
    //   只要把 null 那一次跳过，两条报错就同时消失。
    //
    // ── 为什么 v1.4.19 没拦住（实测教训）──
    //
    //   原来 patch 的是 `Scribe_References.Look<Pawn>`（**封闭泛型实例**）。
    //   补丁报「已安装」，但用户读档时 138 条照刷，而「已开始过滤」一次都没打印 ——
    //   说明前缀**从没被调用**。**Harmony 对封闭泛型方法实例的 patch 不可靠。**
    //   而 `Take<T>` / `TakeResolvedRef<T>` 同样是泛型，也靠不住。
    //
    // ── 现在的做法：改调用方，不改被调方 ──
    //
    //   用 Transpiler 进 `Fixation.PostExposeData` 的 IL，把那一条
    //   `call Scribe_References::Look` **换成我们自己的** `SafeLook(ref Pawn, string, bool)`：
    //
    //       static void SafeLook(ref Pawn p, string label, bool save)
    //       {
    //           if (label == "fixPawn" && p == null) return;   // ← 只跳这一个
    //           Scribe_References.Look(ref p, label, save);    // 其余原样转发
    //       }
    //
    //   好处：
    //     · **两个阶段一起解决**（不登记 → 也就不会去查）
    //     · 签名完全一致（`ref Pawn, string, bool`），栈平衡不变，只换 call 目标
    //     · **有效引用一个字节都不动**，存档格式完全不变
    //     · 不用碰泛型方法，绕开 Harmony 那个坑
    //
    // ── 什么情况下自动失效 ──
    //
    //   · RJW「裸体评价」没装 → 找不到 `Fixation` 类型 → 跳过
    //   · 作者改了字段名（不叫 `fixPawn`）→ Transpiler 找不到那条 call → 什么都不改
    //   · 作者自己补上判空 → 那条 call 的形态变了，Transpiler 不命中 → 自然让路
    //   · 玩家关掉这一项 → 什么都不装
    //
    // ── 性能 ──
    //
    //   `SafeLook` 只在**存读档**时被调用，不在 tick / 渲染路径上。
    //   Transpiler 只在安装期跑一次。
    // ==========================================================================
    internal static class FixPawnScribeNullFix
    {
        private const string FixationTypeName = "NudityMattersMore.Fixation";
        // 1.6 版叫 PostExposeData；1.7 重构后 Fixation 变成 NMMPawnModule 的子类，
        // 方法名改成 ExposeData()（public override void ExposeData()），
        // 由 CompNMM.PostExposeData() 统一调用。两个名字都要试。
        private const string ExposeMethodName = "PostExposeData";
        private const string ExposeMethodName17 = "ExposeData";
        private const string TargetLabel = "fixPawn";
        private const string ScribeRefsTypeName = "Verse.Scribe_References";

        private static bool installed;
        private static int replacedCalls;
        private static bool announced;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type fixationType = AccessTools.TypeByName(FixationTypeName);
                if (fixationType == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + FixationTypeName
                        + "（裸体评价未安装或已改版）；【41】/fixPawn 过滤已跳过。");
                    return;
                }

                // 先试旧名（1.6 / 早期 1.7），再试新名（1.7 重构后）。
                // 只换 call 目标的 Transpiler 对两个版本都适用 —— 两个方法体里
                // 都是 `Scribe_References.Look(ref fixPawn, "fixPawn")`。
                MethodInfo target = AccessTools.Method(fixationType, ExposeMethodName, Type.EmptyTypes, null);
                string usedName = ExposeMethodName;
                if (target == null)
                {
                    target = AccessTools.Method(fixationType, ExposeMethodName17, Type.EmptyTypes, null);
                    usedName = ExposeMethodName17;
                }

                if (target == null)
                {
                    // ⚠ 这里**故意用 Log.Message 而不是 Log.Error**，也**故意不设 installed**：
                    //   ① 用 Message：目标换名是「这一项本版本不适用」，不是故障 ——
                    //      每次启动打一条 ERROR 会把真正的红字埋掉（2026-11-07 实测：
                    //      那一整局里这条 ERROR 混在 10,879 条里，反而增加了排错噪音）。
                    //   ② 不设 installed：留着下次重试。成本只是一次反射，换来的是
                    //      「玩家换回旧版 / 作者改回旧名」时能自动自愈。
                    Log.Message("[GNH LocalFixes] 【41】目标不适用：" + FixationTypeName
                        + " 上既没有 " + ExposeMethodName + "() 也没有 " + ExposeMethodName17
                        + "()（该模组改了实现）；本项本局不生效，不影响其它修复。");
                    return;
                }

                replacedCalls = 0;
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    null,
                    null,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(FixPawnScribeNullFix), nameof(Transpiler), null, null)),
                    null);

                if (replacedCalls == 0)
                {
                    Log.Warning("[GNH LocalFixes] 【41】没能在 " + FixationTypeName + "." + usedName
                        + " 里找到 Scribe_References.Look 调用（作者可能改了写法）；本项本局不生效，下次会重试。");
                    return;
                }

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【41】/fixPawn 脏数据过滤"
                    + "（Transpiler 改 " + FixationTypeName + "." + usedName
                    + " 里的 Scribe_References.Look 调用，替换 " + replacedCalls + " 处）。"
                    + "只在「label = " + TargetLabel + " 且引用本身是 null」时跳过，"
                    + "有效引用一个字节都不动，存档格式不变。"
                    + "★ 这版换了拦法：v1.4.19 patch 的是 Look<Pawn>（封闭泛型实例），"
                    + "Harmony 对它不可靠 —— 补丁报「已安装」但一次都没拦到，138 条照刷。"
                    + "现在改成改调用方，绕开泛型。"
                    + "★ 1.7 兼容：目标方法名由 " + ExposeMethodName + " 变成 " + ExposeMethodName17
                    + "，两个都试过，这次用的是 " + usedName + "。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【41】/fixPawn 脏数据过滤失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // Transpiler：call Scribe_References::Look  →  call SafeLook
        // ==================================================================
        //
        // 只换 call 的目标，不动任何其它指令 —— 签名完全一致，栈平衡天然成立。
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(
                typeof(FixPawnScribeNullFix), nameof(SafeLook),
                new[] { typeof(Pawn).MakeByRefType(), typeof(string), typeof(bool) }, null);

            if (replacement == null)
            {
                Log.Error("[GNH LocalFixes] 【41】连自己的 SafeLook 都找不到，无法替换。");
                foreach (CodeInstruction ci in instructions)
                {
                    yield return ci;
                }
                yield break;
            }

            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
                    && ci.operand is MethodInfo mi
                    && mi.Name == "Look"
                    && mi.DeclaringType != null
                    && mi.DeclaringType.FullName == ScribeRefsTypeName)
                {
                    // 只换「第一个参数是 ref Pawn」的那个重载（即 Look<Pawn>(ref Pawn, string, bool)）
                    ParameterInfo[] ps = mi.GetParameters();
                    if (ps.Length == 3
                        && ps[0].ParameterType.IsByRef
                        && ps[0].ParameterType.GetElementType() == typeof(Pawn))
                    {
                        ci.operand = replacement;
                        replacedCalls++;
                    }
                }

                yield return ci;
            }
        }

        // ==================================================================
        // 安全版 Look：null 的 fixPawn 直接不登记（于是也不会去查）
        // ==================================================================
        //
        // 必须 public static：Transpiler 生成的是跨类 call。
        // 参数签名要与被替换的完全一致：ref Pawn / string / bool。
        public static void SafeLook(ref Pawn refee, string label, bool saveDestroyedThings)
        {
            if (label == TargetLabel && refee == null)
            {
                // 这一条正是 138 条报错的来源：登记一个 null loadID，然后在解析阶段又去查它。
                // 跳过它 = 既不登记也不查询，两条报错同时消失；有效引用不受影响。
                if (!announced)
                {
                    announced = true;
                    Log.Message("[GNH LocalFixes] 【41】已开始跳过 /fixPawn 的空引用登记"
                        + "（成因：1.6→1.7 迁移后这些引用解析不出来，模组又无条件交给了 Scribe）。"
                        + "跳过的是「登记一个 null」，有效引用不受影响。之后不再重复打印。");
                }

                return;
            }

            Scribe_References.Look(ref refee, label, saveDestroyedThings);
        }
    }
}
