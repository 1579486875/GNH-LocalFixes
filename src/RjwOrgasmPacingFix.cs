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
    // 【常规·18】RJW 高潮节奏：兼容 Organic Sexuality + 兜底 Verse.Rand 污染
    // ==========================================================================
    //
    // ── 为什么要这一项（2026-11-14 实机取证，三轮对照）────────────────────
    //
    //   症状：性行为时「高潮值一直锁 0」或「几秒内 0→100 疯狂循环直到动画播完」，
    //         不分男女、不分种族、有无改造都一样。
    //
    //   取证（实装探针打出来的原文）：
    //     [真相] 苗阳飇 need_some_sex=1.00 CurLevel=0.5000 duration=1077 → num=2.00
    //     [止血] sex_ticks 2147483647 → 1077
    //     [真相] 孙雯 need_some_sex=0.00 CurLevel=1.0000 duration=3320
    //     [止血] sex_ticks 223 → 340
    //
    //   元凶 = `Organic Sexuality`（organic.sexuality）。它用 Prefix + return false
    //   把 RJW 的 `SetupOrgasmTicks` 整个接管，换成自己的公式：
    //
    //     float num = Math.Min(pawn.records.GetValue(xxx.CountOfSex) * 0.1f, 100f);
    //     if (num <= 0f) { sex_ticks = int.MaxValue; }              // ← bug ①
    //     else           { sex_ticks = 180 + duration / num; }      // ← bug ②
    //
    //   bug ①：`CountOfSex == 0`（新角色、第一次）时给 int.MaxValue，
    //           而 `Orgasm()` 的守卫是 `if (sex_ticks > orgasmstick) return;`
    //           → 永远 return → 永远不高潮；而 CountOfSex 只在 Orgasm() 里自增
    //           → **死锁**，一辈子涨不上去。这就是「锁 0」。
    //
    //   bug ②：`num` 封顶 100 → sex_ticks = 180 + duration/100 ≈ 192，
    //           而门槛是 180 → 每 12 tick 就高潮一次 → 0.2 秒一轮 → 「疯狂跳」。
    //           配上「每次高潮 CountOfSex+1」形成正反馈，越玩越疯。
    //
    //   对照实验（只关 organic.sexuality 一个变量）：
    //     [节奏] duration=1152 sex_ticks 516 → 521（本轮 5.7 秒）
    //     [节奏] duration=1191 sex_ticks 475 → 533（本轮 5.9 秒）
    //     [节奏] duration=1556 sex_ticks 600 → 614（本轮 7.2 秒）
    //     [止血] 0 条
    //   → 关掉后 RJW 原公式跑得完全正常，定案。
    //
    // ── 本项做什么 ────────────────────────────────────────────────────────
    //
    //   在 `SetupOrgasmTicks` 挂一个 Postfix（Postfix 在 Prefix return false 之后
    //   **仍会执行**，所以无论 RJW 原方法跑没跑，这里都能拿到最终结果并改写）。
    //
    //   ★ 分两种情况，**尽量保住原作者的设计意图**：
    //
    //   A. 检测到 Organic Sexuality 在场（第一次调时查 Harmony patch owner）
    //      → 按**它自己的公式**重算，只修两个 bug：
    //          · num = 0 时不再给 int.MaxValue，改成 1（等价于「新手最慢」）
    //          · num 上限从 100 降到 10，让「越熟练越快」的曲线走上限而不是撞死
    //          · 再钳一层 [orgasmstick+240, orgasmstick+duration] 兜底
    //        效果：新手约 22 秒一轮 → 熟练后约 4 秒一轮，曲线单调平滑、无正反馈暴走。
    //        **它的精液系统（SemenUtility / HediffComp_Chargeable / 高潮消耗 50%）
    //        一个都不碰**，玩法完整保留。
    //
    //   B. 没检测到（或检测失败）
    //      → 按 RJW 原文重算：`duration / (1 + need_some_sex(pawn)) * 随机(0.75~0.9)`，
    //        用 System.Random 而不是 Verse.Rand。
    //        这一条同时兜住「Verse.Rand 被别的模组弄成超范围值」的情况
    //        （实机抓到过 duration=3320 / sex_ticks=2147483647 这种值）。
    //
    //   两种情况都会把结果钳进 [orgasmstick+240, orgasmstick+duration]，
    //   保证「一轮不短于 4 秒、不长于总时长」。
    //   （原来那条独立的 `[GNH 止血]` 探针已并入本项，不再单独存在。）
    //
    // ── 自动让路 ──────────────────────────────────────────────────────────
    //
    //   · RJW 不在场 / 字段找不到 → 打一条日志，本项什么都不做；
    //   · 任何一步抛异常 → 吞掉、保留原值，绝不打断性行为；
    //   · `JobDriver_Masturbate` 有自己的 `SetupDurationTicks`，但 `SetupOrgasmTicks`
    //     是继承来的，因此本项对自慰同样生效（这是期望行为）。
    // ==========================================================================

    internal static class RjwOrgasmPacingFix
    {

        // ---- 异常留痕（模板 §3：不许空 catch 吞掉线索）------------------
        // 这两个文件都在游戏热路径上（每次高潮 / 每 150 tick），
        // 所以不能「每次都打」——限前 3 次，之后静默，只保留计数。
        private static readonly System.Collections.Generic.Dictionary<string, int> s_errSeen
            = new System.Collections.Generic.Dictionary<string, int>();
        private const int MaxLogPerSite = 3;

        private static void NoteError(string site, Exception ex)
        {
            try
            {
                int n;
                s_errSeen.TryGetValue(site, out n);
                s_errSeen[site] = n + 1;
                if (n < MaxLogPerSite)
                {
                    Log.Warning("[GNH " + "节奏" + "] " + site + " 出错（第 " + (n + 1)
                        + " 次，之后同类静默）：" + ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception)
            {
                // 留痕本身失败 —— 这是最后一道防线，只能真静默。
                // ⚠ 这里【绝对不能】再调 NoteError：那会自己调自己，
                //   一旦留痕持续失败就是无限递归 → 栈溢出崩溃。
                //   这是 2026-11-15 批量替换空 catch 时误伤的，已修。
            }
        }
        private const int MaxLogs = 15;

        private static int logs;
        private static readonly System.Random Rng = new System.Random();

        private static FieldInfo fDuration;
        private static FieldInfo fSexTicks;
        private static FieldInfo fOrgasmStartTick;
        private static FieldInfo fOrgasmStick;
        private static MethodInfo mNeedSomeSex;
        private static MethodInfo mIsHuman;
        private static FieldInfo fCountOfSex;
        // §1.3：JobDriver.pawn 的 FieldInfo 缓存。
        //   本类的 Postfix 每次高潮都跑，而 AccessTools.Field 要遍历继承链 + 加锁，
        //   属于「完全可以缓存」的开销 —— 缓存一次，之后只做 GetValue。
        private static FieldInfo s_pawnField;

        /// <summary>null = 还没查过；true/false = 已判定 Organic Sexuality 是否接管。</summary>
        private static bool? organicPresent;

                // 一次性安装守卫（模板 §四要求）：Harmony 补丁是叠加的，
        // 同一方法上重复 Patch 会套多层 prefix，表现为「越操作越卡」。
        // 正常情况下 TryInstall 只被 LocalFixesMod 的静态构造调用一次，
        // 但本守卫保证「哪怕将来多一条调用路径也不会叠加」。
        private static bool s_pacingInstalled;
        internal static void Install()
        {

            if (s_pacingInstalled) return;
            s_pacingInstalled = true;
            Type jobSex = AccessTools.TypeByName("rjw.JobDriver_Sex");
            Type xxx = AccessTools.TypeByName("rjw.xxx");
            if (jobSex == null)
            {
                Log.Warning("[GNH LocalFixes] 高潮节奏：RJW 不在场，本项跳过。");
                return;
            }

            fDuration = AccessTools.Field(jobSex, "duration");
            fSexTicks = AccessTools.Field(jobSex, "sex_ticks");
            fOrgasmStartTick = AccessTools.Field(jobSex, "orgasmStartTick");
            fOrgasmStick = AccessTools.Field(jobSex, "orgasmstick");

            if (fDuration == null || fSexTicks == null || fOrgasmStartTick == null || fOrgasmStick == null)
            {
                Log.Warning("[GNH LocalFixes] 高潮节奏：RJW 字段找不到（可能改版），本项跳过。");
                return;
            }

            s_pawnField = AccessTools.Field(typeof(Verse.AI.JobDriver), "pawn");

            if (xxx != null)
            {
                mNeedSomeSex = AccessTools.Method(xxx, "need_some_sex", new[] { typeof(Pawn) }, null);
                mIsHuman = AccessTools.Method(xxx, "is_human", new[] { typeof(Pawn) }, null);
                fCountOfSex = AccessTools.Field(xxx, "CountOfSex");
            }

            Harmony h = new Harmony("GNH.LocalFixes.RjwOrgasmPacing");
            int n = 0;

            MethodInfo setupOrg = AccessTools.Method(jobSex, "SetupOrgasmTicks");
            if (setupOrg != null)
            {
                h.Patch(setupOrg, postfix: new HarmonyMethod(
                    typeof(RjwOrgasmPacingFix), nameof(OrgasmTicksPostfix)));
                n++;
            }

            // SetupDurationTicks 只在「Organic Sexuality 不在场」时才有必要兜
            // （在场时它自己会写 3600，那是它的设计，不该被我们改回去）
            MethodInfo setupDur = AccessTools.Method(jobSex, "SetupDurationTicks");
            if (setupDur != null)
            {
                h.Patch(setupDur, postfix: new HarmonyMethod(
                    typeof(RjwOrgasmPacingFix), nameof(DurationPostfix)));
                n++;
            }

            Log.Message("[GNH LocalFixes] 【18】RJW 高潮节奏已安装 " + n + "/2"
                + "（SetupOrgasmTicks / SetupDurationTicks）。"
                + "检测到 Organic Sexuality 时按它的原公式重算、只修 num=0 与 num 封顶两个 bug"
                + "（精液系统完全保留）；未检测到时按 RJW 原文重算。"
                + "两种情况都会把一轮钳进 [门槛+240, 门槛+总时长]，即不短于 4 秒。");
        }

        // ---------------- 判 Organic Sexuality 在不在 ----------------
        private static bool OrganicPresent()
        {
            if (organicPresent.HasValue) return organicPresent.Value;

            bool found = false;
            try
            {
                if (ModLister.GetActiveModWithIdentifier("organic.sexuality", true) != null)
                {
                    found = true;
                }
                else
                {
                    // 退路：按名字/packageId 找
                    foreach (var mod in ModLister.AllInstalledMods)
                    {
                        if (mod == null || !mod.Active) continue;
                        var nm = mod.Name ?? "";
                        var pid = mod.PackageId ?? "";
                        if (nm.IndexOf("Organic Sexuality", StringComparison.OrdinalIgnoreCase) >= 0
                            || pid.IndexOf("organic", StringComparison.OrdinalIgnoreCase) >= 0
                               && pid.IndexOf("sexuality", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (!found)
                {
                    // 再退一步：查 SetupOrgasmTicks 上有没有 RJWLifeExpanded 的 patch
                    MethodInfo t = AccessTools.Method(AccessTools.TypeByName("rjw.JobDriver_Sex"), "SetupOrgasmTicks");
                    if (t != null)
                    {
                        Patches pi = Harmony.GetPatchInfo(t);
                        if (pi != null)
                        {
                            foreach (Patch p in pi.Prefixes)
                            {
                                if (p.owner != null
                                    && p.owner.IndexOf("RJWLifeExpanded", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception __ex2)
            {
                NoteError("site2", __ex2);
            }

            organicPresent = found;
            return found;
        }

        // ---------------- ① SetupDurationTicks 兜底（只在 Organic 不在场时动手）----------------
        private static void DurationPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                if (OrganicPresent()) return;   // 在场时 3600 是它的设计，别动

                int old = ReadInt(fDuration, __instance);
                if (old >= 1000 && old <= 1800) return;   // 本来就是合法值，不动

                int correct = (int)(2000f * NextFloat(0.5f, 0.9f));
                if (correct < 1000) correct = 1000;
                if (correct > 1800) correct = 1800;
                fDuration.SetValue(__instance, correct);

                FieldInfo tl = AccessTools.Field(typeof(Verse.AI.JobDriver), "ticksLeft");
                if (tl != null) { try { tl.SetValue(__instance, correct); } catch (Exception __ex3)
            {
                NoteError("site3", __ex3);
            } }

                if (logs < MaxLogs)
                {
                    logs++;
                    Log.Message("[GNH 节奏] duration " + old + " → " + correct + "（越界，按 RJW 原文重算）");
                }
            }
            catch (Exception __ex4)
            {
                NoteError("site4", __ex4);
            }
        }

        // ---------------- ② SetupOrgasmTicks 重算 ----------------
        private static void OrgasmTicksPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;

                Pawn pawn = s_pawnField?.GetValue(__instance) as Pawn;
                int duration = ReadInt(fDuration, __instance);
                if (duration < 1000 || duration > 1800)
                {
                    int fixedDur = (int)(2000f * NextFloat(0.5f, 0.9f));
                    if (fixedDur < 1000) fixedDur = 1000;
                    if (fixedDur > 1800) fixedDur = 1800;
                    duration = fixedDur;
                    fDuration.SetValue(__instance, duration);
                }

                int stick = 180;
                try { fOrgasmStick.SetValue(__instance, stick); } catch (Exception __ex5)
            {
                NoteError("site5", __ex5);
            }

                int old = ReadInt(fSexTicks, __instance);
                int correct;
                string mode;

                bool organic = OrganicPresent();
                if (organic)
                {
                    // ── A：按 Organic Sexuality 的原公式，只修两个 bug ──
                    //   num = min(CountOfSex * 0.1, 100)   →  改成 min(..., 10)
                    //   num <= 0 → int.MaxValue            →  改成 1
                    float exp = 1f;
                    long count = 0;
                    try
                    {
                        if (pawn != null && pawn.records != null && fCountOfSex != null)
                        {
                            object recDef = fCountOfSex.GetValue(null);
                            if (recDef != null)
                            {
                                count = (long)Convert.ToDouble(
                                    pawn.records.GetValue((RecordDef)recDef));
                            }
                        }
                        exp = Math.Min(count * 0.1f, 10f);
                    }
                    catch (Exception __exExp)
                    {
                        // 取不到 CountOfSex（RJW 未装 / records 异常）时退回 exp=1，
                        // 等价于「新手最慢」，而不是让异常穿出去打断性行为。
                        exp = 1f;
                        NoteError("site-exp", __exExp);
                    }
                    if (exp <= 0f) exp = 1f;      // ← 修 bug ①：不再给 int.MaxValue

                    float span = duration / exp;
                    correct = stick + (int)span;
                    mode = "有机性欲·修正";
                }
                else
                {
                    // ── B：RJW 原文 ──
                    float num = 1f;
                    if (pawn != null && mIsHuman != null && mNeedSomeSex != null)
                    {
                        try
                        {
                            if (mIsHuman.Invoke(null, new object[] { pawn }) is bool b && b)
                            {
                                object need = mNeedSomeSex.Invoke(null, new object[] { pawn });
                                num = 1f + (need == null ? 0f : Convert.ToSingle(need));
                            }
                        }
                        catch (Exception __ex6)
            {
                NoteError("site6", __ex6);
            }
                    }
                    if (num < 1f) num = 1f;
                    if (num > 4f) num = 4f;
                    correct = (int)((float)duration / num * NextFloat(0.75f, 0.9f));
                    mode = "RJW原文";
                }

                // 统一兜底：一轮至少 4 秒，且不超过总时长
                int minAllowed = stick + 240;
                int maxAllowed = stick + duration;
                if (correct < minAllowed) correct = minAllowed;
                if (correct > maxAllowed) correct = maxAllowed;

                fSexTicks.SetValue(__instance, correct);
                try { fOrgasmStartTick.SetValue(__instance, correct); } catch (Exception __ex7)
            {
                NoteError("site7", __ex7);
            }

                if (logs < MaxLogs)
                {
                    logs++;
                    Log.Message(string.Format(
                        "[GNH 节奏] {0} {1}｜duration={2} 原值={3} → {4}（本轮 {5:F1} 秒）",
                        pawn != null ? pawn.LabelShort : "?",
                        mode, duration, old, correct, (correct - stick) / 60f));
                }
            }
            catch (Exception __ex8)
            {
                NoteError("site8", __ex8);
            }
        }

        // ---------------- helpers ----------------
        private static float NextFloat(float min, float max)
        {
            return min + (float)Rng.NextDouble() * (max - min);
        }

        private static int ReadInt(FieldInfo f, object inst)
        {
            try
            {
                if (f == null) return -999;
                object v = f.GetValue(inst);
                return v == null ? -999 : Convert.ToInt32(v);
            }
            catch (Exception __exRead)
            {
                NoteError("site-read", __exRead);
                return -999;
            }
        }
    }
}
