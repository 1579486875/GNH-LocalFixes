// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【40】两个模组把调试输出留在了正式版，日志被刷爆
    //
    // 修的两处（都是「作者自己的 Log.Message 忘了删」）：
    //
    //   ① WeaponStats（bodlosh.WeaponStats，工坊 974066449）
    //      Source\WeaponsTab\Turret.cs:74 里写着
    //          public new void fillFromThing(Thing th, bool ce = false)
    //          {
    //              Log.Message(th.def.defName);      ← 每开一次武器统计面板，每个炮塔刷一行
    //              base.fillFromThing(th);
    //      Source\WeaponsTab\WeaponStats.cs:1042 还有一句
    //          Log.Message("Update weapons list");
    //      实测 21 点一小时 2,737 条：Turret_MiniTurret 552 次、KT_Bunker 82 次……
    //
    //   ② RimImmortal.Core（RI.RimImmortal.Core，工坊 3296476341，[1.7] 边缘仙路）
    //      它有个前缀方法每次进来都写一行 `AnyActiveDeepScannersOnMapPreFix::Start`，
    //      实测 190 条。
    //
    // 两处都不是「出错」，是纯粹的调试噪音 —— 它们把真正的报错埋掉，也让日志文件白涨几十 MB。
    //
    // ── 为什么拦在 Log.Message 这一层，而不是去 patch 那两个模组 ──
    //
    //   ① 那两个模组都有自己的 dll，patch 它们的目标方法要跟着它们的版本走；
    //      Log.Message(string) 是原版方法，签名在 1.6/1.7 都是 `Void Message(String text)`（已反编译核对）。
    //   ② 真正要拦的只是「这一行」，不是「那个方法的所有输出」——
    //      WeaponStats 的 fillFromThing 还要算预热、射程、伤害，拦掉整个方法会让面板空掉。
    //
    // ── 三个判据，全都收紧到「只可能是这两处」 ──
    //
    //   判据 A：文本恰好等于 "Update weapons list"        —— 这是 WeaponStats 独有的固定串
    //   判据 B：文本以 "::Start" 结尾                        —— 这是 RimImmortal 那类前缀日志的形态
    //   判据 C：文本是一个**炮塔** ThingDef 的 defName      —— 对应 Turret.cs:74
    //           判据 C 收得很紧：还要求 `building.turretGunDef != null`，
    //           所以「某个模组恰好把某个普通 def 的名字打出来」不会被误伤。
    //
    // ── 什么情况下它会自动失效 ──
    //
    //   · 作者把调试输出删掉 → 这三条判据一条都命中不了，本项什么都不做（也一条日志不打）。
    //   · 用户关掉这一项 → 前缀根本不装，Log.Message 完全不受影响。
    //   · 原版 Log.Message(string) 改签名 → 找不到目标，本次跳过并留 Error，下次重试。
    //
    // ── 性能 ──
    //
    //   Log.Message 是相对低频的路径（正常游戏每分钟几条到几十条）。
    //   前缀里最贵的是判据 C 的 `DefDatabase.GetNamedSilentFail`，但先被「长度 ≤ 64」挡掉绝大多数；
    //   而判据 A / B 都是 `==` 与 `EndsWith`，代价可忽略。
    //   第一次拦下时打一条说明，之后完全静默 —— 否则等于换个方式继续刷屏。
    // ==========================================================================
    internal static class LogSpamFilterFix
    {
        private const string WeaponStatsFixedLine = "Update weapons list";
        private const string PrefixLogSuffix = "::Start";

        // defName 不会超过这个长度；超过的直接放行，避免对长文本做 DefDatabase 查询。
        private const int MaxDefNameLength = 64;

        private static bool installed;
        private static int suppressed;
        private static bool announced;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ⚠ AccessTools.Method 的第 4 个参数是「泛型类型参数」，非泛型方法必须传 null。
                //   Log.Message 有两个重载：Message(string) 与 Message(object)，
                //   这里用参数类型数组精确取 string 那个。
                MethodInfo target = AccessTools.Method(
                    typeof(Log), "Message", new[] { typeof(string) }, null);

                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 找不到 Verse.Log.Message(string)；"
                        + "【40】日志刷屏过滤本次未安装，下次会重试。");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(LogSpamFilterFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【40】日志刷屏过滤（Verse.Log.Message）。"
                    + "拦三类：WeaponStats 的「" + WeaponStatsFixedLine + "」与炮塔名、"
                    + "以及以「" + PrefixLogSuffix + "」结尾的前缀调试日志。"
                    + "其余日志一律照常输出。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【40】日志刷屏过滤失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 前缀：命中三类噪音就返回 false（原方法整个跳过）。
        // ==================================================================
        //
        // 用 `string __0` 而不是 `string text`：按位置绑定，
        // 万一原版把参数名从 text 改掉也不影响（Harmony 允许两种写法）。
        public static bool Prefix(string __0)
        {
            if (__0 == null || __0.Length == 0)
            {
                return true;
            }

            bool hit = false;
            string why = null;

            // 判据 A：WeaponStats 的固定串
            if (__0 == WeaponStatsFixedLine)
            {
                hit = true;
                why = "WeaponStats 的固定调试串";
            }
            // 判据 B：RimImmortal 那类「方法名::Start」
            else if (__0.Length >= PrefixLogSuffix.Length
                     && __0.EndsWith(PrefixLogSuffix, StringComparison.Ordinal))
            {
                hit = true;
                why = "前缀方法的 ::Start 调试日志";
            }
            // 判据 C：炮塔 defName（长度先筛，再查 def 并确认它真是炮塔）
            else if (__0.Length <= MaxDefNameLength)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(__0);
                if (def != null && def.building != null && def.building.turretGunDef != null)
                {
                    hit = true;
                    why = "WeaponStats 打印的炮塔 defName";
                }
            }

            if (!hit)
            {
                return true;
            }

            suppressed++;

            // 只在第一次拦下时说明一次，之后完全静默。
            if (!announced)
            {
                announced = true;
                Log.Message("[GNH LocalFixes] 已开始过滤日志刷屏（首次命中：" + why + "）。"
                    + "这些都是模组作者留在正式版里的调试输出，不是报错；"
                    + "过滤只针对上面三类，其它日志不受影响。之后不再重复打印。");
            }

            return false;
        }
    }
}
