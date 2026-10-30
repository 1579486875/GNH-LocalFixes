// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【39】实时绘制阶段每帧抛空引用，日志被刷到几个 GB。
    // ==========================================================================
    //
    // 【① 修的是什么问题】
    //
    // 日志里每秒几十条：
    //
    //     Exception drawing ReactorControl331370: System.NullReferenceException
    //        at Verse.Thing.DynamicDrawPhase_Patch2(Thing this, DrawPhase phase)
    //        at Verse.DynamicDrawManager.DynamicDrawThings(DynamicDrawManager this)
    //
    // 2026-10-30 实测一局：8.5 小时刷出 661,441 条 ERROR / 2.07 GB 日志 / 进程内存 28 GB。
    // 中招的不止一种建筑 —— 尾部 5 MB 里 ReactorControl 1074、TowerOfLight 537、
    // ResearchReactor 537，全是 Dubs Rimatomics 的。
    //
    // 【为什么偏偏是它们】
    //
    // 原版的 DynamicDrawManager 只把「需要实时绘制」的 Thing 放进这个循环
    // （def.drawerType 不是 MapMeshOnly 的那些）。所以不是 Rimatomics 有毛病，
    // 而是地图上**只有它们走这条路** —— 换别的实时绘制建筑一样会炸。
    //
    // 【根因在哪一层】（DecompilerServer 逐层反编译核对）
    //
    //   Verse.Thing.DynamicDrawPhase      ← 两个模组各挂了一对 Prefix/Finalizer
    //     ├─ ZuoYao.RavenRace：Patch_SlimeOverlayThingDynamicDraw
    //     │    Prefix → SlimeThingOverlayCapture.BeginDynamic
    //     │             └ CaptureEnabled => RavenRaceMod.Settings.enableRavenLiquidCoveredVisuals
    //     │                ★ 这里裸读一个静态属性，整条链上唯一没有判空的点
    //     └─ ChezhouLib：Patch_ClSceneThingDraw
    //          Prefix → new ClSceneThingDrawing.Scope(thing)
    //                   └ Supported / TryGet / Matrix / Dispose  ← 逐项核对都判了
    //
    // RavenRace 那一侧：`RavenRaceMod.Settings` 是个 `get; private set;` 的静态属性，
    // 绘制阶段如果它还是 null（模组类还没构造完 / 被别的东西重置），取 `.Settings` 就是裸 NRE。
    // 堆栈只到 `_Patch2` 而不到更深一层，也符合「属性 getter 被 JIT 内联进包装方法」的形态。
    //
    // 【② 为什么这样修】
    //
    // 不直接改 `BeginDynamic` 里那一行 —— 那是别人的功能开关，上游哪天补上判空，
    // 我们的改动就白做一遍。改在 `Thing.DynamicDrawPhase` 上加 Finalizer，
    // 能同时兜住两个模组的任意一层，而且**只在真的抛异常时才动作**。
    //
    // 只吞 NullReferenceException；其它异常（越界、类型转换失败等）原样抛回，
    // 不把真正的 bug 一起埋掉。
    //
    // 【③ 什么情况下它自动失效 / 跳过】
    //
    // 两层退场机制：
    //   1. 安装期（Prepare）：找不到 RavenRace 的 SlimeThingOverlayCapture 就不装 ——
    //      说明作者把这个功能删了或改名了，本项没有拦截对象。
    //   2. 运行期：上游补上判空之后就不再抛异常，Finalizer 会一直走第一个分支直接返回，
    //      等于什么都不做。此时日志里也不会再有「已吞掉」的计数增长。
    //
    // 【关于异常处理边界】
    //
    // Finalizer 里没有 try/catch，这是刻意的：整段只有 int 自增、GetType().Name
    // 和一个 SafeLogWarning 调用 —— 前两个不可能抛，最后一个由 LocalFixesMod 保证不抛
    // （它内部已经吞掉日志子系统自己的异常，2026-10-07 极端场景测试过的老问题）。
    // 所以这里不存在「需要保护的临界区」，也就不该用空 catch 把问题盖住。
    [HarmonyPatch(typeof(Thing), nameof(Thing.DynamicDrawPhase))]
    public static class Patch_Thing_DynamicDrawPhase_SwallowRealtimeNre
    {
        /// <summary>每吞掉多少次记一条 WARNING。取 500 是为了「不刷屏但也不失明」。</summary>
        private const int ReportEvery = 500;

        private static int swallowed;
        private static int nextReportAt = 1;   // 第一条立刻报，证明它真的在干活
        private static string lastType = "?";

        /// <summary>
        /// 安装前的让路判断。返回 false 时 Harmony 不会给方法挂 Finalizer。
        /// </summary>
        [HarmonyPrepare]
        public static bool Prepare()
        {
            Type capture = AccessTools.TypeByName(
                "RavenRace.Features.SlimeOverlay.SlimeThingOverlayCapture");

            if (capture == null)
            {
                // 目标功能不在了 → 让路。这条日志必须留：否则下次排查时
                // 没人知道这一项到底跑没跑过（模板 §3.1 的反向要求）。
                LocalFixesMod.SafeLogMessage("[GNH LocalFixes] 【39】让路：没找到 RavenRace 的 "
                    + "SlimeThingOverlayCapture（该功能可能已被移除或改名），本项不安装。");
                return false;
            }

            LocalFixesMod.SafeLogMessage("[GNH LocalFixes] 【39】已启用：实时绘制抛空引用时拦截，"
                + "每 " + ReportEvery + " 次记一条。");
            return true;
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, Thing __instance)
        {
            // 没有异常：原方法正常走完，本补丁等于不存在。
            if (__exception == null)
            {
                return null;
            }

            // 只处理空引用。别的异常类型原样抛回。
            if (!(__exception is NullReferenceException))
            {
                return __exception;
            }

            swallowed++;
            if (__instance != null)
            {
                lastType = __instance.GetType().Name;
            }

            if (swallowed >= nextReportAt)
            {
                nextReportAt = swallowed + ReportEvery;
                LocalFixesMod.SafeLogWarning("[GNH LocalFixes] 实时绘制空引用已吞掉 " + swallowed
                    + " 次（最近一次：" + lastType + "）。这是【39】的防护，可在设置里关掉。");
            }

            // 返回 null = 告诉 Harmony「这个异常已经处理」，不再向上抛。
            return null;
        }
    }
}
