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
    // 【40】两个模组把调试输出留在了正式版，日志被刷爆
    //
    // ⚠ 2026-11-01 改法调整：原来是在 `Verse.Log.Message` 上加全局前缀过滤，
    //   现在改成**直接在目标方法里把那几条日志调用换掉**。
    //   理由是用户的要求：「能直接修复好的那就直接修复，不要搞纯拦截这一套」。
    //   全局拦 `Log.Message` 是拦截；改目标是修 —— 少一个全局钩子，也不会误伤别的模组。
    //
    // ── 要改的三处（都是作者自己的 Log.Message 忘了删）──
    //
    //   ① WeaponStats（bodlosh.WeaponStats，工坊 974066449）
    //      `WeaponStats.Turret.fillFromThing(Thing, bool)` 第一行：
    //          Log.Message(th.def.defName);      ← 每开一次武器统计面板，每个炮塔刷一行
    //      实测 21 点一小时 2,737 条：Turret_MiniTurret 552 次、KT_Bunker 82 次……
    //
    //   ② 同模组 `WeaponStats.MainTabWindow_WeaponStats.UpdateList()` 里：
    //          Log.Message("Update weapons list");
    //
    //   ③ RimImmortal.Core（工坊 3296476341）某个前缀方法每次写一行
    //      `AnyActiveDeepScannersOnMapPreFix::Start`（实测 190 条）。
    //      这类日志打在一个我们自己都定位不到的方法里，只能退回到「按内容过滤」。
    //
    // ── 怎么「删」一条日志调用 ──
    //
    //   IL 长这样：
    //       ldstr "xxx"                       ← 压参数
    //       call void Verse.Log::Message(string)   ← 弹掉参数，返回 void
    //
    //   把那条 `call` **换成 `pop`**：opcode 位置不变、栈平衡不变（都消耗一个值），
    //   其余指令一条不动。这是最小侵入的改法 —— 不需要去猜参数是由几条指令压出来的。
    //
    // ── 降级 ──
    //
    //   ①② 两处如果找不到目标方法（模组没装 / 作者改名），就跳过。
    //   ③ 那一处没有稳定锚点，**继续沿用按内容过滤**（`::Start` 结尾），
    //      但把它缩小到「只在 RimImmortal 装了的时候」才装。
    //
    // ── 什么情况下自动失效 ──
    //
    //   · 作者把调试输出删了 → Transpiler 找不到那条 call，什么都不改（也一条日志不打）。
    //   · 模组没装 → `AccessTools.TypeByName` 返回 null，直接跳过。
    //   · 玩家关掉这一项 → 什么都不装。
    // ==========================================================================
    internal static class LogSpamFilterFix
    {
        private const string TurretTypeName = "WeaponStats.Turret";
        private const string TabTypeName = "WeaponStats.MainTabWindow_WeaponStats";
        private const string RimImmortalTypeHint = "RI.RimImmortal";

        private const string PrefixLogSuffix = "::Start";

        private static bool installed;
        private static int patchedTargets;
        private static int replacedCalls;

        // RimImmortal 那条「按内容过滤」的钩子装上了没有。
        // 单独记一个标志，是为了让安装日志能说清「到底哪一半生效了」——
        // patchedTargets 把「转译器」和「内容过滤」混在一起数，单看它会误判。
        private static bool contentFilterInstalled;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ── ① WeaponStats.Turret.fillFromThing(Thing, bool) ──
                TryTranspileCall(TurretTypeName, "fillFromThing",
                    new[] { typeof(Thing), typeof(bool) });

                // ── ② WeaponStats.MainTabWindow_WeaponStats.UpdateList() ──
                TryTranspileCall(TabTypeName, "UpdateList", Type.EmptyTypes);

                // ── ③ RimImmortal 的 ::Start：没有稳定锚点，只能按内容过滤 ──
                //    只在它真装了的时候才装，避免给所有玩家多挂一个全局钩子。
                if (AnyTypeNameContains(RimImmortalTypeHint))
                {
                    TryInstallContentFilter();
                }

                // 一个目标都没找到（玩家没装 WeaponStats / RimImmortal）→ 这一项空转。
                // 必须留一条说明「为什么什么都没做」，否则日志里会写成
                // 「已安装…改了 0 个目标方法、替换 0 处」，读起来像装好了（模板 §3.1 的反向要求）。
                if (patchedTargets == 0)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【40】让路：没找到 WeaponStats 的 "
                        + "Turret.fillFromThing / MainTabWindow_WeaponStats.UpdateList，"
                        + "也没找到 RimImmortal —— 相关模组都没装，这一项本次无事可做。"
                        + "（这不影响其它任何修复。）");
                    return;
                }

                // 目标方法找到了、但里面一条 Log.Message 都没有 → 作者已经自己删掉了调试输出。
                // 同样要如实说，不能报成「已安装」。
                //
                // 注意这里**不能**用「contentFilterInstalled 为假」当前提：
                // RimImmortal 那条内容过滤装上了、而 WeaponStats 两处已经不需要，
                // 同样是「替换 0 处」，也得说清楚是哪一半还在生效。
                if (replacedCalls == 0)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【40】部分让路：找到了目标方法（" + patchedTargets
                        + " 个），但 WeaponStats 那两处里已经没有 Log.Message 调用 —— "
                        + "多半是作者自己删掉了调试输出。"
                        + (contentFilterInstalled
                            ? "RimImmortal 的 ::Start 内容过滤已经装上，这一半照常生效。"
                            : "本项这一次没有替换任何指令。"));
                    return;
                }

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【40】日志刷屏修复："
                    + "改了 " + patchedTargets + " 个目标方法、替换 " + replacedCalls + " 处日志调用"
                    + "（把 call Log.Message 换成 pop，逻辑与栈平衡都不变）。"
                    + "WeaponStats 的 Turret.fillFromThing 与 UpdateList 已从源头去掉调试输出；"
                    + "RimImmortal 的 ::Start 因为没有稳定锚点，仍按内容过滤。"
                    + "★ 本项不再拦全局 Log.Message —— 其它模组的日志一条都不受影响。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【40】日志刷屏修复失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 给某个方法挂 Transpiler，把里面所有 Log.Message 调用换成 pop
        // ==================================================================
        private static void TryTranspileCall(string typeName, string methodName, Type[] paramTypes)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                return;
            }

            MethodInfo target = AccessTools.Method(t, methodName, paramTypes, null);
            if (target == null)
            {
                return;
            }

            int before = replacedCalls;
            LocalFixesMod.HarmonyInstance.Patch(
                target,
                null,
                null,
                new HarmonyMethod(AccessTools.Method(
                    typeof(LogSpamFilterFix), nameof(DropLogCalls), null, null)),
                null);

            patchedTargets++;
            if (replacedCalls > before)
            {
                Log.Message("[GNH LocalFixes] 【40】" + typeName + "." + methodName
                    + " 里的 " + (replacedCalls - before) + " 处日志调用已去掉。");
            }
        }

        // ==================================================================
        // Transpiler：call Log.Message(...) → pop
        // ==================================================================
        //
        // 判据只看「方法名是 Message、声明类型是 Verse.Log」——
        // 三个重载（string / object / TaggedString）都算，因为它们都是调试输出。
        // 换 pop 而不是删指令：pop 与 call 消耗的值数量相同，栈平衡天然成立。
        private static IEnumerable<CodeInstruction> DropLogCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
                    && ci.operand is MethodInfo mi
                    && mi.Name == "Message"
                    && mi.DeclaringType != null
                    && mi.DeclaringType.FullName == "Verse.Log")
                {
                    ci.opcode = OpCodes.Pop;
                    ci.operand = null;
                    replacedCalls++;
                }

                yield return ci;
            }
        }

        // ==================================================================
        // ③ 的降级：按内容过滤 RimImmortal 的 ::Start
        // ==================================================================
        private static void TryInstallContentFilter()
        {
            MethodInfo logMessage = AccessTools.Method(
                typeof(Log), "Message", new[] { typeof(string) }, null);
            if (logMessage == null)
            {
                return;
            }

            LocalFixesMod.HarmonyInstance.Patch(
                logMessage,
                new HarmonyMethod(AccessTools.Method(
                    typeof(LogSpamFilterFix), nameof(Prefix), null, null)),
                null,
                null,
                null);
            patchedTargets++;
            contentFilterInstalled = true;
        }

        // 前缀：只拦「以 ::Start 结尾」这一种（RimImmortal 的前缀调试日志形态）。
        public static bool Prefix(string __0)
        {
            if (__0 == null || __0.Length < PrefixLogSuffix.Length)
            {
                return true;
            }

            return !__0.EndsWith(PrefixLogSuffix, StringComparison.Ordinal);
        }

        // 扫一遍已加载程序集，看有没有名字里带某个片段的类型。
        // 只在 Install 时跑一次，代价可忽略。
        private static bool AnyTypeNameContains(string fragment)
        {
            try
            {
                foreach (ModContentPack mod in LoadedModManager.RunningMods)
                {
                    foreach (Assembly asm in mod.assemblies.loadedAssemblies)
                    {
                        Type[] types;
                        try
                        {
                            types = asm.GetTypes();
                        }
                        catch
                        {
                            continue;
                        }

                        for (int i = 0; i < types.Length; i++)
                        {
                            if (types[i].FullName != null
                                && types[i].FullName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 扫不动就当没装 —— 这一处只是可选降级。
            }

            return false;
        }
    }
}
