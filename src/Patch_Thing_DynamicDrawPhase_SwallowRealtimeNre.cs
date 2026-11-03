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
    // 【38】实时绘制阶段每帧抛空引用，日志被刷到几个 GB。
    // ==========================================================================
    //
    // 【① 真实存在，有日志为证】
    //
    // 2026-10-30 那局尾部 5 MB 日志里有 **2,148 条** `Exception drawing ...：NullReferenceException`：
    //   ReactorControl331370 / ReactorControl331372 / ResearchReactor330669 / TowerOfLight321575
    //   —— 各 537 条。整局 8.5 小时刷出 661,441 条 ERROR / 2.07 GB 日志 / 进程内存 28 GB。
    //
    // 【为什么偏偏是它们】
    //
    // 原版的 DynamicDrawManager 只把「需要实时绘制」的 Thing 放进这个循环。
    // 所以不是 Rimatomics 有毛病，而是地图上**只有它们走这条路** —— 换别的实时绘制建筑一样会炸。
    //
    // 【根因在哪一层】（DecompilerServer 逐层反编译核对）
    //
    //   Verse.Thing.DynamicDrawPhase      ← 两个模组各挂了一对 Prefix/Finalizer
    //     ├─ ZuoYao.RavenRace：Patch_SlimeOverlayThingDynamicDraw
    //     │    Prefix → SlimeThingOverlayCapture.BeginDynamic
    //     │             └ CaptureEnabled => RavenRaceMod.Settings.enableRavenLiquidCoveredVisuals
    //     │                ★ 这里裸读一个静态属性，可疑度最高
    //     └─ ChezhouLib：Patch_ClSceneThingDraw
    //          Prefix → new ClSceneThingDrawing.Scope(thing)
    //                   └ Supported / TryGet / Matrix / Dispose  ← 逐项核对都判了
    //
    // 【② 为什么现在仍然只能「吞」—— 诚实交代】
    //
    // ⚠ 2026-11-01 复查发现：这条链上**每一环我都核过判空，找不到确切的空值点**：
    //     · `TryGetThingOverlay`  → 完整判空（`target/ Spawned / Map / CanReceiveOverlay` 全查）
    //     · `GetCached(Map)`      → 开头就 `if (map == null) return null;`
    //     · `AppendPrintedSilhouetteRequests` → 四个参数全判空
    //     · `RavenRaceMod.Settings` → 在 `RavenRaceMod..ctor` 里就赋值了，且没有 `Error while instantiating`
    //   而日志里的堆栈**只到 `Thing.DynamicDrawPhase_Patch2`**，没有再往下一层 ——
    //   说明 NRE 抛在被 JIT 内联进来的某一层，靠静态阅读区分不出来。
    //
    //   **所以这一项从「猜根因去改上游」改成「先把证据留全」**：
    //   第一次吞掉时，把 `Exception.ToString()`（含完整堆栈与所有内层帧）打进日志。
    //   下次真出这个毛病时，就能看到具体是哪一层的哪个字段为空，再决定怎么真修。
    //
    //   在那之前不动上游 —— 上次【38】的教训就是：凭"理论上会崩"去改别人的代码，
    //   结果改掉的是一个本来正常的功能。
    //
    // 【③ 为什么用 Finalizer 而不是 Prefix 打断】
    //
    // 不直接改 `BeginDynamic` 里那一行 —— 那是别人的功能开关。改在 `Thing.DynamicDrawPhase`
    // 上加 Finalizer，能同时兜住两个模组的任意一层，而且**只在真的抛异常时才动作**。
    // 只吞 NullReferenceException；其它异常（越界、类型转换失败等）原样抛回，不把真正的 bug 一起埋掉。
    //
    // 【④ 什么情况下它自动失效 / 跳过】
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
    // 和 SafeLogWarning 调用 —— 前两个不可能抛，最后一个由 LocalFixesMod 保证不抛
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

        // Harmony 的 [HarmonyPrepare] 每局会被问不止一次（模组列表重建时会再来问），
        // 不加守卫的话「已启用」那条日志会打两遍（2026-11-01 实测确认）。
        private static bool preparedOnce;

        // 完整堆栈只打一次 —— 它可能很长，每次抛都打等于换个方式刷屏。
        private static bool stackDumped;

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
                LocalFixesMod.SafeLogMessage("[GNH LocalFixes] 【38】让路：没找到 RavenRace 的 "
                    + "SlimeThingOverlayCapture（该功能可能已被移除或改名），本项不安装。");
                return false;
            }

            if (!preparedOnce)
            {
                preparedOnce = true;
                LocalFixesMod.SafeLogMessage("[GNH LocalFixes] 【38】已启用：实时绘制抛空引用时拦截，"
                    + "每 " + ReportEvery + " 次记一条。**第一次吞的时候会把完整堆栈打进日志**"
                    + "（只打一次）—— 那一份堆栈是用来定位根因的，定位到之后就能改成真修。");
            }

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

            // ★ 第一次吞掉时把完整异常（含堆栈、InnerException、Data）写进日志。
            //
            //   为什么必须做：这是个「吞掉」补丁，没有堆栈就永远查不到是谁在抛 ——
            //   2026-10-30 那局刷了 2,148 条，日志里却只有一行
            //   「Exception drawing ReactorControl331370: NullReferenceException」，
            //   连是哪一层抛的都看不出。有了这份堆栈，下次就能定位到具体字段，改成真修。
            //   只打一次，不刷屏。
            if (!stackDumped)
            {
                stackDumped = true;
                LocalFixesMod.SafeLogWarning("[GNH LocalFixes] 【38】首次吞掉实时绘制空引用。"
                    + "下面这份完整堆栈是定位根因用的（只打一次，含所有内层帧）。"
                    + "确定是哪一层的空值之后，就可以把它改成真修而不是吞掉："
                    + Environment.NewLine + __exception);
            }

            if (swallowed >= nextReportAt)
            {
                nextReportAt = swallowed + ReportEvery;
                LocalFixesMod.SafeLogWarning("[GNH LocalFixes] 实时绘制空引用已吞掉 " + swallowed
                    + " 次（最近一次：" + lastType + "）。这是【38】的防护，可在设置里关掉。");
            }

            // 返回 null = 告诉 Harmony「这个异常已经处理」，不再向上抛。
            return null;
        }
    }
}
