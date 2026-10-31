// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【41】DeepProfiler 全程在跑，把日志和内存一起拖垮
    //
    // 症状（同一局 14.5 小时实测）：
    //   "--- Main thread ---"   7,105 次
    //   "Hotspot analysis"      8,001 次
    //   20:56:04 → 20:56:12 的 8 秒里，日志从 L741763 跳到 L795857（**54,094 行**）
    //   内存：11.5 小时 9,178 MB → 14.5 小时 **15,730 MB**（+6.5 GB / 3 小时）
    //
    // ── 根因：不是谁误开了性能分析器，是 `Prefs.LogVerbose` ──
    //
    //   反编译 Verse.DeepProfiler（1.7，Assembly-CSharp）：
    //
    //       public static volatile bool enabled = true;          ← 默认就是 true
    //
    //       public static void Start(string label = null) {
    //           if (enabled && Prefs.LogVerbose) { Get().Start(label); }
    //       }
    //       public static void End() {
    //           if (enabled && Prefs.LogVerbose) { Get().End(); }
    //       }
    //
    //   **两个条件都成立才会跑**。`enabled` 默认 true，所以真正决定它开不开的是 `Prefs.LogVerbose`。
    //   本机 Prefs.xml 里 `<logVerbose>True</logVerbose>` —— 玩家自己开的（想看得细一点）。
    //
    //   于是每一次 LongEvent（读档、生成地图、模组初始化……）都走一遍
    //   `ExecuteToExecuteWhenFinished → DeepProfiler.End() → 打印整棵调用树 + Hotspot analysis`。
    //
    // ── 为什么不建议「把 logVerbose 关掉」──
    //
    //   那是玩家主动开的：他要的是「出问题时日志写详细点」。
    //   DeepProfiler 的输出对他毫无用处（几千行调用树，只有开发者做性能剖析时才看），
    //   两者本来就不该绑在一起。所以这里**只关 DeepProfiler，保留 logVerbose**。
    //
    // ── 为什么光写字段不够（v1.4.18 第一次实机就撞了）──
    //
    //   原版 `Verse.PlayDataLoader.DoPlayLoad` 里有这样一对模式（反编译 IL 实证，
    //   偏移 446/449 与 461/464，另有一组 487/490 与 502/505）：
    //
    //       ldc.i4.0 ; volatile. ; stsfld DeepProfiler.enabled     ← 临时关
    //       … ResolveAllReferences(…) …                            ← 干活不记
    //       ldc.i4.1 ; volatile. ; stsfld DeepProfiler.enabled     ← 又开回来
    //
    //   也就是说**读档会把 enabled 设回 true**。只置一次 false 的话，
    //   22:16:46 装上、23:01 读档就又开始了（实测 1,178 次调用树）。
    //
    //   所以本项**两层**一起上：
    //     ① 仍然把 enabled 置 false（连 Get() 都不进，省得白建对象）；
    //     ② 给 Start / End 各挂一个返回 false 的前缀 —— 无论 enabled 被谁改成什么值，
    //        这两个方法都直接返回。
    //
    // ── 为什么直接写字段，而不是只 patch --Start/End ──
    //
    //   `enabled` 是 `public static volatile bool`，直接置 false 等价于「官方开关」：
    //   Start 和 End 在**第一道判断**就返回，连 `Get()` 都不会调用。
    //   配上 ② 之后，即使原版中途把它开回来，也不会真的去记。
    //
    //   代价：游戏内开发者的「性能分析」功能会失效（MeasureTool 的耗时统计也会一起没）。
    //   那是开发用途，普通玩家用不到；需要时把本项开关关掉重开游戏即可恢复。
    //
    // ── 什么情况下它会自动失效 ──
    //
    //   · 玩家关掉本项 → Install 根本不会被调用，enabled 与 Start/End 都保持原样。
    //   · 原版删掉/改名 `enabled` → ① 跳过并留 Message，但 ② 仍然装上（照样能静音）。
    //   · 原版把 DeepProfiler 整个移除 → 类型找不到，打一条 Message 说明后跳过。
    //
    // ── 性能 ──
    //
    //   两个前缀各只做一次 `return false`；没有反射、没有分配。
    //   它省下的是「每次 LongEvent 都遍历整棵调用树」和一份持续增长的对象列表。
    // ==========================================================================
    internal static class DeepProfilerQuietFix
    {
        private const string ProfilerTypeName = "Verse.DeepProfiler";

        private static bool installed;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type t = AccessTools.TypeByName(ProfilerTypeName);
                if (t == null)
                {
                    // 类型都没有 = 原版改结构了。重试没意义，置位跳过。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + ProfilerTypeName
                        + "；【41】DeepProfiler 关闭本次跳过（原版可能已移除或改名）。");
                    return;
                }

                // ── 第一层：把 enabled 置 false ──
                string stateNote;
                FieldInfo f = AccessTools.Field(t, "enabled");
                if (f == null)
                {
                    stateNote = "找不到 enabled 字段（只靠第二层静音）";
                }
                else
                {
                    object before = f.GetValue(null);
                    f.SetValue(null, false);
                    object after = f.GetValue(null);
                    stateNote = "enabled " + before + " → " + after;
                }

                // ── 第二层：Start / End 各挂一个直接返回 false 的前缀 ──
                //   这一层才是真正的保险 —— 原版 DoPlayLoad 会把 enabled 改回 true。
                int patched = 0;
                patched += PatchNoOp(t, "Start");
                patched += PatchNoOp(t, "End");

                if (patched == 0)
                {
                    Log.Error("[GNH LocalFixes] 【41】在 " + ProfilerTypeName
                        + " 上没能给 Start/End 挂上前缀；本次未安装，下次会重试。");
                    return;
                }

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【41】DeepProfiler 关闭（" + stateNote
                    + "，Start/End 前缀 " + patched + "/2）。"
                    + "它跟着 Prefs.logVerbose 一起跑，每次 LongEvent 都打印整棵调用树并累积对象 ——"
                    + "实测一局 14.5 小时涨了 6.5 GB 内存。"
                    + "★ 光把 enabled 置 false 不够：原版 PlayDataLoader.DoPlayLoad 读档时会把它设回 true，"
                    + "所以另外给 Start/End 挂了直接返回的前缀。"
                    + "logVerbose 本身保留，其它日志一条不少；"
                    + "需要游戏内的性能剖析时，把本项开关关掉重开游戏即可。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【41】DeepProfiler 关闭失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 给 DeepProfiler 上的某个无参静态方法挂一个「直接返回」的前缀。
        // ==================================================================
        //
        // 成功返回 1，找不到方法返回 0（调用方据此判断要不要重试）。
        private static int PatchNoOp(Type profilerType, string methodName)
        {
            MethodInfo target = AccessTools.Method(profilerType, methodName, Type.EmptyTypes, null);
            if (target == null)
            {
                // 有的版本可能带默认参数（Start(string label = null)），补一次「一个 string」的尝试。
                target = AccessTools.Method(profilerType, methodName, new[] { typeof(string) }, null);
            }

            if (target == null)
            {
                return 0;
            }

            LocalFixesMod.HarmonyInstance.Patch(
                target,
                new HarmonyMethod(AccessTools.Method(
                    typeof(DeepProfilerQuietFix), nameof(NoOpPrefix), null, null)),
                null,
                null,
                null);

            return 1;
        }

        // 前缀：永远返回 false → 原方法整个跳过（不打印、不累积）。
        public static bool NoOpPrefix()
        {
            return false;
        }
    }
}
