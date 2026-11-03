// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【42】同一台「可分配」建筑上挂了两个 CompAssignableToPawn → 读档刷 32 条红字
    //
    // ── 症状（2026-11-03 06:19 实机日志）──
    //
    //   读档时成对出现，每台器械 4 条：
    //     Tried to register the same list of load IDs twice.
    //         pathRelToParent=/assignedPawns, parent=FFE_AnimalMedTable434235
    //     Tried to register the same list of load IDs twice.
    //         pathRelToParent=/uninstalledAssignedPawns, parent=…
    //     Could not get load IDs list. We're asking for something
    //         which was never added during LoadingVars. （同上两条）
    //   一局 32 条（8 + 8 + 8 + 8），涉及 2 台床。
    //
    // ── 根因（存档里直接看到的，不是推测）──
    //
    //   把该 Thing 的 <thing> 块切出来数了一遍，`<assignedPawns>` 与
    //   `<uninstalledAssignedPawns>` **各出现两次**，中间夹着 `<quality>`：
    //
    //       <thing Class="Building_Bed">
    //         <def>FFE_AnimalMedTable</def>
    //         <id>FFE_AnimalMedTable434235</id>
    //         …
    //         <assignedPawns />                  ← 第 1 个 comp
    //         <uninstalledAssignedPawns />
    //         <quality>Legendary</quality>       ← CompQuality
    //         <assignedPawns />                  ← 第 2 个 comp
    //         <uninstalledAssignedPawns />
    //         …
    //
    //   即：**这台床身上挂了两个 CompAssignableToPawn 系组件。**
    //   正常建筑只会有一个（一个 Bed 就是 CompAssignableToPawn_Bed）。
    //
    // ── 为什么两个就会报错（Verse.LoadIDsWantedBank 的字典键）──
    //
    //   `Scribe_Collections.Look(ref list, "assignedPawns", LookMode.Reference)`
    //   登记时用的键是 `(Scribe.loader.curParent, Scribe.loader.curPathRelToParent)`。
    //   `ThingWithComps.ExposeData` 是**平铺**调用每个 comp 的 PostExposeData
    //   （中间没有 Scribe.EnterNode），所以两个 comp 的 curParent 是**同一个 Thing**、
    //   path 都是 `/assignedPawns` —— 键一模一样。
    //
    //   于是：
    //     · LoadingVars    → 第 2 个登记时键已存在 → `Log.Error("…register the same list…")`
    //                        （游戏自己的实现是报错后 return，**不覆盖**第一个）
    //     · PostLoadInit   → 第 1 个 TakeList 取走并 Remove
    //                        第 2 个再 TakeList → 查不到 → `Log.Error("…never added…")`
    //
    //   两条报错是同一个原因的两面，且**游戏本身已经选择了「用第一个、丢掉第二个」**。
    //   我们的补丁只是把这个既定选择提前说出来、并且不再刷红字。
    //
    // ── 为什么可以安全跳过第二个 ──
    //
    //   · 存档里那两个列表**都是空的**（`<assignedPawns />` 自闭合、无内容）；
    //   · 就算有内容，游戏自己的去重也只会保留第一个 —— 我们的行为与游戏一致；
    //   · 被跳过的是**序列化动作**，不动任何对象、不改存档格式；
    //   · 单 comp 的正常建筑：第一次就放行，本补丁对它零影响。
    //
    // ── 实现 ──
    //
    //   给 `CompAssignableToPawn.PostExposeData` 挂 Prefix，按
    //   「当前 Scribe 阶段 + 父 Thing」做一次性放行：
    //     · 阶段变化（LoadingVars ⇄ PostLoadInit）时清空记录，保证两个阶段各放行一次；
    //     · 同一 Thing 的第二次及以后直接 `return false`（整个 PostExposeData 不执行）。
    //   子类（_Bed / _Throne / _Grave / _MeditationSpot）的 `base.PostExposeData()`
    //   走的正是基类方法，所以一处补丁覆盖全部子类。
    //
    // ── 什么情况下自动失效 ──
    //
    //   · 上游修好了（def 只留一个 comp）→ HashSet 第一次就放行，补丁变成纯空转；
    //   · 玩家关掉这一项 → 什么都不装；
    //   · `PostExposeData` 改名/改签名 → 找不到目标，本局不生效并在日志里说明。
    //
    // ── 性能 ──
    //
    //   只在存读档时被调用（不在 tick / 渲染路径上）。每次是一个 HashSet.Add。
    // ==========================================================================
    internal static class DuplicateAssignableCompFix
    {
        private static bool installed;
        private static bool announced;
        private static int skipped;

        // Scribe 阶段变了就清空 —— LoadingVars 与 PostLoadInit 是两趟完整遍历，
        // 两趟都必须各放行一次，否则第二趟会把所有建筑都跳过（那才是真灾难）。
        private static LoadSaveMode lastMode = LoadSaveMode.Inactive;
        private static readonly HashSet<ThingWithComps> seen =
            new HashSet<ThingWithComps>(ReferenceComparer.Instance);

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(CompAssignableToPawn),
                    nameof(CompAssignableToPawn.PostExposeData),
                    Type.EmptyTypes,
                    null);

                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 找不到 CompAssignableToPawn.PostExposeData()；"
                        + "【42】本次未安装，下次会重试。");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(DuplicateAssignableCompFix), nameof(Prefix), null, null);
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] 【42】连自己的 Prefix 都找不到，放弃安装。");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(prefix),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【42】重复「可分配」组件去重"
                    + "（CompAssignableToPawn.PostExposeData 加一次性放行）。"
                    + "只在同一台建筑挂了两个以上可分配组件时生效 —— "
                    + "游戏自己遇到这种存档也是「用第一个、丢第二个」，"
                    + "本项只是不再把这件事报成红字。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【42】重复「可分配」组件去重失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // Prefix：同一 Scribe 阶段内，同一台建筑只放行第一次
        // ==================================================================
        //
        // 必须 public static：Harmony 通过反射拿它。
        public static bool Prefix(CompAssignableToPawn __instance)
        {
            try
            {
                LoadSaveMode mode = Scribe.mode;
                if (mode != lastMode)
                {
                    seen.Clear();
                    lastMode = mode;
                }

                // 只在真正读档的两趟里介入；保存（Saving）那一趟完全不动，
                // 免得把「存档里写几份」这件事改掉。
                if (mode != LoadSaveMode.LoadingVars && mode != LoadSaveMode.PostLoadInit)
                {
                    return true;
                }

                ThingWithComps parent = __instance?.parent;
                if (parent == null)
                {
                    return true;
                }

                if (seen.Add(parent))
                {
                    return true;   // 第一次（正常建筑永远走这里）
                }

                skipped++;
                if (!announced)
                {
                    announced = true;
                    Log.Message("[GNH LocalFixes] 【42】已开始跳过重复的可分配组件"
                        + "（成因：某台建筑身上挂了两个 CompAssignableToPawn，"
                        + "两者用同一个父对象登记 /assignedPawns，键冲突）。"
                        + "跳过的是重复那一份，游戏本来也只用第一份；之后不再重复打印。");
                }

                return false;
            }
            catch (Exception ex)
            {
                // 守卫本身绝不能影响读档：出错就放行，让原逻辑照常跑。
                Log.Warning("[GNH LocalFixes] 【42】去重守卫异常，本次放行：" + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// 按引用比较。Thing 没有重写 Equals/GetHashCode（写日志时实测依赖引用语义），
        /// 但这里显式用 RuntimeHelpers.GetHashCode 更稳妥 —— 万一将来上游重写了也不会串。
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<ThingWithComps>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(ThingWithComps x, ThingWithComps y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(ThingWithComps obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
