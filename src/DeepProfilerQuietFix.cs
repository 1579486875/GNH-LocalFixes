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
    // ── 为什么直接写字段，而不是 patch --Start/End ──
    //
    //   `enabled` 是 `public static volatile bool`，直接置 false 等价于「官方开关」：
    //   Start 和 End 在**第一道判断**就返回，连 `Get()` 都不会调用 ——
    //   比 patch 两个方法更彻底，也不会在栈里留 Harmony 的痕迹。
    //
    //   代价：游戏内开发者的「性能分析」功能会失效（MeasureTool 的耗时统计也会一起没）。
    //   那是开发用途，普通玩家用不到；需要时把本项开关关掉重开游戏即可恢复。
    //
    // ── 什么情况下它会自动失效 ──
    //
    //   · 玩家关掉本项 → 这个 Install 根本不会被调用，enabled 保持原值。
    //   · 原版把 `enabled` 改名或改成非静态 → 找不到字段，本次跳过并留 Error，下次重试。
    //   · 原版把 DeepProfiler 整个移除 → 类型找不到，打一条 Message 说明后跳过。
    //
    // ── 性能 ──
    //
    //   安装时反射读一次、写一次，之后零开销（连 Harmony 补丁都没挂）。
    //   它省下的是「每次 LongEvent 都遍历整棵调用树」和一份持续增长的对象列表。
    // ==========================================================================
    internal static class DeepProfilerQuietFix
    {
        private const string ProfilerTypeName = "Verse.DeepProfiler";
        private const string EnabledFieldName = "enabled";

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

                FieldInfo f = AccessTools.Field(t, EnabledFieldName);
                if (f == null)
                {
                    // 字段在、名字变了 → 留给下次重试，并留一条 Error 线索。
                    Log.Error("[GNH LocalFixes] 在 " + ProfilerTypeName + " 上找不到 "
                        + EnabledFieldName + " 字段；【41】本次未安装，下次会重试。");
                    return;
                }

                object before = f.GetValue(null);
                f.SetValue(null, false);
                object after = f.GetValue(null);

                // 复核：写进去了才算装好（volatile 字段在个别运行时会拒绝写入）。
                if (after is bool b && b == false)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 已安装【41】DeepProfiler 关闭（原值 " + before + "）。"
                        + "它跟着 Prefs.logVerbose 一起跑，每次 LongEvent 都打印整棵调用树并累积对象 ——"
                        + "实测一局 14.5 小时涨了 6.5 GB 内存。"
                        + "logVerbose 本身保留，其它日志一条不少；"
                        + "需要游戏内的性能剖析时，把本项开关关掉重开游戏即可。");
                }
                else
                {
                    Log.Error("[GNH LocalFixes] 【41】写入 " + ProfilerTypeName + "." + EnabledFieldName
                        + " 失败（写后读回 = " + after + "），下次会重试。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【41】DeepProfiler 关闭失败（下次会重试）：" + ex);
            }
        }
    }
}
