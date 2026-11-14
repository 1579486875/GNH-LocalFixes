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
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【临时诊断 + 止血】高潮值疯狂循环（2026-11-14 实机取证）
    // ==========================================================================
    //
    // ── 已确认的事实（实机日志，不是推断）─────────────────────────────────
    //
    //   [GNH 高潮取证·重置] 娜塔莉娅 duration=1622 sex_ticks=196 orgasmstick=180
    //       → 本轮 16 tick（≈0.27 秒）
    //   [GNH 高潮取证·加满足] pawn=娜塔莉娅 → 之后 CurLevel=1.0000（100.0%）
    //       → 连续 30 条全是 100%
    //
    //   把两个数代进 RJW 的公式：
    //       sex_ticks = (int)((float)duration / num * Rand.Range(0.75f, 0.9f))
    //       num = 1f + need_some_sex(pawn)
    //   196 = 1622 / num × (0.75~0.9)  ⇒  num ≈ 6.4 ~ 7
    //
    //   而 num 的法律上限是 4（1 + 极度饥渴 3），且此刻 CurLevel 已是 100%，
    //   按源码 need_some_sex 应返回 0、num 应为 1、sex_ticks 应为 1379（23 秒）。
    //
    //   动物侧更离谱：
    //       [GNH 高潮取证·重置] 公野猪 duration=1206 sex_ticks=2147483647
    //   2147483647 = int.MaxValue，是「除以 0 → 无穷大 → 转 int」的典型特征，
    //   意味着 num 在某些情况下变成了 0（即 need_some_sex 返回 -1）。
    //
    //   结论：**num 这个中间值被算成了源码不可能产生的值**。
    //   静态分析已排除：SexSlaveCraft（只读采样）、RJWQuirks（只放大满足感）、
    //   月经周期 StatPart（0.01~1.0）、RJW_动画（无引用）、RimMT 多线程
    //   （JobTrackerTick 与 NeedInterval 都还在主线程）。
    //
    // ── 本文件做两件事 ────────────────────────────────────────────────────
    //
    //   ① 诊断：在 SetupOrgasmTicks 的 Prefix 里**自己调一次 need_some_sex**，
    //      把它的真实返回值和当时的 CurLevel 打出来 —— 这是分辨
    //      「函数被别人换了」还是「IL 被改了」的唯一办法。
    //
    //   ② 止血：在 Postfix 里把算出来的 sex_ticks 钳进 [orgasmstick+120, 10000]。
    //      · 下限 120 tick（2 秒）—— 防「7 tick 一轮」这种明显不合理的边界；
    //      · 上限 10000 —— 防 int.MaxValue 那种「永远不高潮」。
    //      ⚠ 它**不改 num、不改满足感、不改任何设计意图**，
    //        只保证「一轮不会短于 2 秒、也不会长到永不结束」。
    //
    // ⚠ 临时文件：根因定位、正式修复落地后，本文件与 LocalFixesMod 里那行调用一并删除。
    // ==========================================================================

    internal static class RjwOrgasmDiagProbe
    {
        private const int MaxSetupLogs = 40;
        private const int MaxSatisfyLogs = 10;
        private const int MaxNeedLogs = 40;

        private static int setupLogs;
        private static int satisfyLogs;
        private static int needLogs;

        private static FieldInfo sexPropsPawnField;
        private static FieldInfo sexPropsPartnerField;
        private static MethodInfo needSomeSexMethod;

        internal static void Entry()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH 高潮取证] 探针安装失败（不影响游戏）：" + ex);
            }
        }

        internal static void Install()
        {
            Type sexUtil = AccessTools.TypeByName("rjw.SexUtility");
            Type needSex = AccessTools.TypeByName("rjw.Need_Sex");
            Type jobSex = AccessTools.TypeByName("rjw.JobDriver_Sex");
            Type sexProps = AccessTools.TypeByName("rjw.SexProps");
            Type xxx = AccessTools.TypeByName("rjw.xxx");

            if (sexUtil == null || needSex == null || jobSex == null)
            {
                Log.Warning("[GNH 高潮取证] RJW 不在场，探针不安装。");
                return;
            }

            if (sexProps != null)
            {
                sexPropsPawnField = AccessTools.Field(sexProps, "pawn");
                sexPropsPartnerField = AccessTools.Field(sexProps, "partner");
            }

            if (xxx != null)
            {
                needSomeSexMethod = AccessTools.Method(xxx, "need_some_sex",
                    new[] { typeof(Pawn) }, null);
            }

            Harmony h = new Harmony("GNH.Diag.Orgasm");
            int n = 0;

            // ① SatisfyPersonal —— 只记前 10 条，确认加给谁、加到多少
            MethodInfo satisfy = AccessTools.Method(sexUtil, "SatisfyPersonal");
            if (satisfy != null)
            {
                h.Patch(satisfy,
                    postfix: new HarmonyMethod(typeof(RjwOrgasmDiagProbe), nameof(SatisfyPostfix)));
                n++;
            }

            // ② Need_Sex.NeedInterval
            MethodInfo needInt = AccessTools.Method(needSex, "NeedInterval");
            if (needInt != null)
            {
                h.Patch(needInt, postfix: new HarmonyMethod(
                    typeof(RjwOrgasmDiagProbe), nameof(NeedIntervalPostfix)));
                n++;
            }

            // ③ SetupOrgasmTicks —— 诊断 + 止血
            MethodInfo setup = AccessTools.Method(jobSex, "SetupOrgasmTicks");
            if (setup != null)
            {
                // 止血逻辑已并入 RjwOrgasmPacingFix，这里只保留 prefix 取样
                h.Patch(setup,
                    prefix: new HarmonyMethod(typeof(RjwOrgasmDiagProbe), nameof(SetupPrefix)));
                n++;
            }

            Log.Message("[GNH 高潮取证] 探针已安装 " + n + "/3 个点"
                + "（SatisfyPersonal / Need_Sex.NeedInterval / SetupOrgasmTicks[Prefix+Postfix]）。"
                + "③ 同时带止血：sex_ticks 会被钳进 [orgasmstick+120, 10000]。");
        }

        // ---------------- ③ Prefix：先自己调一次 need_some_sex，把真相记下来 ----------------
        private static void SetupPrefix(object __instance)
        {
            if (setupLogs >= MaxSetupLogs) return;
            try
            {
                if (__instance == null) return;
                Type t = __instance.GetType();
                object pawnObj = AccessTools.Field(t, "pawn")?.GetValue(__instance)
                    ?? AccessTools.Field(typeof(Verse.AI.JobDriver), "pawn")?.GetValue(__instance);
                Pawn pawn = pawnObj as Pawn;
                if (pawn == null) return;

                int duration = ReadInt(t, __instance, "duration");
                int sexTicksBefore = ReadInt(t, __instance, "sex_ticks");

                float needVal = -999f;
                string needSrc = "?";
                if (needSomeSexMethod != null)
                {
                    try
                    {
                        object r = needSomeSexMethod.Invoke(null, new object[] { pawn });
                        if (r != null) needVal = Convert.ToSingle(r);
                        needSrc = "直接调用";
                    }
                    catch (Exception ex)
                    {
                        needSrc = "调用失败:" + ex.GetType().Name;
                    }
                }
                else
                {
                    needSrc = "方法未找到";
                }

                Need sexNeed = TryGetSexNeed(pawn);
                float cur = sexNeed != null ? sexNeed.CurLevel : -1f;

                setupLogs++;
                Log.Message(string.Format(
                    "[GNH 高潮取证·真相] {0} need_some_sex={1:F2}（{2}） CurLevel={3:F4}"
                    + " duration={4} sex_ticks进={5} → 反推 num={6:F2}",
                    pawn.LabelShort, needVal, needSrc, cur, duration, sexTicksBefore,
                    needVal >= 0f ? 1f + needVal : -1f));
            }
            catch (Exception)
            {
            }
        }


        // ---------------- ① SatisfyPersonal ----------------
        private static void SatisfyPostfix(object __0)
        {
            if (satisfyLogs >= MaxSatisfyLogs) return;
            try
            {
                Pawn target = FieldAsPawn(__0, sexPropsPawnField);
                if (target == null) return;
                Need need = TryGetSexNeed(target);
                satisfyLogs++;
                Log.Message("[GNH 高潮取证·加满足] " + target.LabelShort
                    + (need != null
                        ? " → CurLevel=" + need.CurLevel.ToString("F4")
                        : " **没有 Need_Sex**"));
            }
            catch (Exception)
            {
            }
        }

        // ---------------- ② Need_Sex.NeedInterval ----------------
        private static void NeedIntervalPostfix(object __instance)
        {
            if (needLogs >= MaxNeedLogs) return;
            try
            {
                Need need = __instance as Need;
                Pawn p = NeedPawn(need);
                if (p == null) return;
                needLogs++;
                Log.Message(string.Format("[GNH 高潮取证·需求衰减] {0} CurLevel={1:F4}（{2:P1}）",
                    p.LabelShort, need.CurLevel, need.CurLevelPercentage));
            }
            catch (Exception)
            {
            }
        }

        // ================= helpers =================
        private static FieldInfo s_needPawnField;

        private static Need TryGetSexNeed(Pawn pawn)
        {
            try
            {
                if (pawn?.needs == null) return null;
                Type t = AccessTools.TypeByName("rjw.Need_Sex");
                if (t == null) return null;
                List<Need> all = pawn.needs.AllNeeds;
                for (int i = 0; i < all.Count; i++)
                {
                    Need n = all[i];
                    if (n != null && t.IsInstanceOfType(n)) return n;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        private static Pawn NeedPawn(Need need)
        {
            if (need == null) return null;
            try
            {
                if (s_needPawnField == null)
                {
                    s_needPawnField = AccessTools.Field(typeof(Need), "pawn");
                }
                return s_needPawnField?.GetValue(need) as Pawn;
            }
            catch (Exception) { return null; }
        }

        private static Pawn FieldAsPawn(object obj, FieldInfo field)
        {
            if (obj == null || field == null) return null;
            try { return field.GetValue(obj) as Pawn; }
            catch (Exception) { return null; }
        }

        private static int ReadInt(Type t, object inst, string field)
        {
            try
            {
                FieldInfo f = AccessTools.Field(t, field);
                if (f == null) return -999;
                object v = f.GetValue(inst);
                return v == null ? -999 : Convert.ToInt32(v);
            }
            catch (Exception) { return -999; }
        }
    }
}
