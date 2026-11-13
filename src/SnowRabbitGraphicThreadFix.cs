// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【65】雪兔的「分层建筑渲染」在非主线程加载贴图 → 建筑显示成紫块
    // ==========================================================================
    //
    // ── 症状（2026-11-13 第三轮全局日志诊断 P2-2）──
    //
    //   读档时两条：
    //     Attempted to load a graphic off the main thread:
    //       Verse.Graphic_Single: SR_Buildings/SR_MILKMAKE/MILKMAKEUP
    //     at SnowRabbit.Comp_LayeredBuildingRenderer.LoadGraphics() [0x62]
    //     at SnowRabbit.Comp_LayeredBuildingRenderer.Initialize(CompProperties) [0x9]
    //     at Verse.ThingWithComps.InitializeComps_Patch6(...)
    //     at Verse.ThingWithComps.ExposeData_Patch2(...)
    //
    //   还有一条同类（Raven 的烛台 Candlestand_Flame），走的也是 ExposeData 路径。
    //   Unity 的 Texture2D 加载必须发生在主线程，所以这条必然报错，贴图拿不到 →
    //   那栋建筑的上层显示成紫块 / 直接不显示。
    //
    // ── 根因（反编译 SnowRabbit.Comp_LayeredBuildingRenderer 逐行确认）──
    //
    //   public override void Initialize(CompProperties compProps)
    //   {
    //       base.Initialize(compProps);
    //       LoadGraphics();          // ★ 读档时这在 ScribeLoader 的异步线程里跑
    //   }
    //
    //   private void LoadGraphics()
    //   {
    //       if (!string.IsNullOrEmpty(Props.upperGraphicPath))
    //           upperGraphic = GraphicDatabase.Get<Graphic_Single>(Props.upperGraphicPath, ...);
    //   }
    //
    //   public override void PostSpawnSetup(bool respawningAfterLoad)
    //   {
    //       base.PostSpawnSetup(respawningAfterLoad);
    //       if (!respawningAfterLoad)  // ★ 读档时这里是 true，所以不会补加载
    //           LoadGraphics();
    //   }
    //
    //   public override void PostDraw()
    //   {
    //       DrawContainedPawn();
    //       DrawUpperLayer();          // 只有 upperGraphic != null 才画
    //   }
    //
    //   也就是说：读档时 Initialize 在后台线程调用 → 贴图加载失败 → upperGraphic 为 null
    //   → PostSpawnSetup 因为 respawningAfterLoad == true 又不补 → PostDraw 永远画不出上层。
    //
    // ── 修法：把「加载贴图」从 Initialize 挪到第一次 PostDraw（懒加载）──
    //
    //   ① Prefix `LoadGraphics`：**不在主线程时直接跳过**（return false）。
    //      主线程时照常执行 —— 新放置建筑走的仍是原路径，行为一点不改。
    //
    //   ② Postfix `PostDraw`：**在主线程里补一次**。
    //      若 `upperGraphic` 仍为 null 且 `Props.upperGraphicPath` 非空，就调一次原 `LoadGraphics`。
    //      第一次绘制一定在主线程，所以这一次必然成功。
    //
    //   ③ 用 `UnityData.IsInMainThread` 判断（反编译确认：`mainThreadId == Thread.CurrentThread.ManagedThreadId`），
    //      比 `Thread.CurrentThread.IsBackground` 之类的启发式可靠。
    //
    //   ④ 不必担心每帧都判 null：`upperGraphic` 一旦加载成功就不再为 null，
    //      每帧只剩一次 `== null` 判断（纳秒级）。
    //
    // ── 为什么不去改 SnowRabbit 的源码 ──
    //
    //   那是工坊模组（3643594275）。改源码意味着「本地分支」，要跟着上游每一次更新合并；
    //   而这个缺陷只是一个「加载时机」问题，用一行 Prefix 就能在**不改它任何代码**的前提下修好。
    //   按 §0.2 保意图：**作者的功能一样没少**，只是把贴图加载挪到了它本来就该在的线程上。
    //
    // ── 自动让路 ──
    //
    //   · 没装 SnowRabbit → `TypeByName` 返回 null → 跳过（打 Message，不是 Error）；
    //   · 作者自己把 LoadGraphics 改成懒加载（或挪出 Initialize）→
    //     我们的 Prefix 仍然只在「非主线程」时才拦截，那时根本不会走到这里，等于空转；
    //   · 作者改了方法名/字段名 → 找不到 → 打 Error 并留待下次重试。
    // ==========================================================================

    /// <summary>
    /// 【65】雪兔分层建筑渲染的贴图懒加载。
    /// </summary>
    internal static class SnowRabbitGraphicThreadFix
    {
        private const string TargetTypeName = "SnowRabbit.Comp_LayeredBuildingRenderer";
        private const string LoadGraphicsName = "LoadGraphics";
        private const string PostDrawName = "PostDraw";
        private const string UpperGraphicField = "upperGraphic";

        private const int ErrorOnceKey = 0x474E486B;   // ASCII "GNHk"

        private static bool installed;

        /// <summary>原版的 LoadGraphics（Postfix 里补调它）。</summary>
        private static MethodInfo loadGraphicsMethod;

        /// <summary>对私有字段 upperGraphic 的引用访问器（读 + 写）。</summary>
        private static AccessTools.FieldRef<ThingComp, Graphic> upperGraphicRef;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type tp = AccessTools.TypeByName(TargetTypeName);
                if (tp == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【65】SnowRabbit is not present;"
                        + " its graphic-thread fix was skipped (nothing to do).");
                    return;
                }

                loadGraphicsMethod = AccessTools.Method(tp, LoadGraphicsName, Type.EmptyTypes, null);
                if (loadGraphicsMethod == null)
                {
                    Log.Error("[GNH LocalFixes] 【65】" + TargetTypeName + "." + LoadGraphicsName
                        + "() was not found (its API probably changed);"
                        + " the fix is deferred and will retry on the next start.");
                    return;
                }

                MethodInfo postDraw = AccessTools.Method(tp, PostDrawName, Type.EmptyTypes, null);
                if (postDraw == null)
                {
                    Log.Error("[GNH LocalFixes] 【65】" + TargetTypeName + "." + PostDrawName
                        + "() was not found; the fix is deferred and will retry.");
                    return;
                }

                // 私有字段 upperGraphic：用 FieldRef 读写，避免每次反射。
                FieldInfo fi = AccessTools.Field(tp, UpperGraphicField);
                if (fi == null)
                {
                    Log.Error("[GNH LocalFixes] 【65】field " + UpperGraphicField
                        + " was not found; the fix is deferred and will retry.");
                    return;
                }
                upperGraphicRef = AccessTools.FieldRefAccess<ThingComp, Graphic>(fi);

                MethodInfo pre = AccessTools.Method(
                    typeof(SnowRabbitGraphicThreadFix), nameof(LoadGraphicsPrefix));
                MethodInfo post = AccessTools.Method(
                    typeof(SnowRabbitGraphicThreadFix), nameof(PostDrawPostfix));

                if (pre == null || post == null)
                {
                    Log.Error("[GNH LocalFixes] 【65】internal error: prefix/postfix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    loadGraphicsMethod, new HarmonyMethod(pre), null, null, null);
                LocalFixesMod.HarmonyInstance.Patch(
                    postDraw, null, new HarmonyMethod(post), null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【65】patched " + TargetTypeName
                    + " (LoadGraphics prefix + PostDraw postfix)."
                    + " Its upper-layer graphic is now loaded on the main thread at first draw,"
                    + " instead of off-thread during ExposeData — which used to fail and leave"
                    + " the building showing a purple box.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【65】failed to install: " + ex);
            }
        }

        /// <summary>
        /// 不在主线程时跳过贴图加载（那一定失败）；主线程时照常执行。
        /// </summary>
        private static bool LoadGraphicsPrefix()
        {
            try
            {
                return UnityData.IsInMainThread;
            }
            catch
            {
                // 守卫出错就放行，绝不因为「修」而改变原行为
                return true;
            }
        }

        /// <summary>
        /// 第一次在主线程绘制时补一次加载。
        /// </summary>
        private static void PostDrawPostfix(ThingComp __instance)
        {
            try
            {
                if (__instance == null || upperGraphicRef == null || loadGraphicsMethod == null)
                {
                    return;
                }

                if (upperGraphicRef(__instance) != null)
                {
                    return;   // 已经加载过（正常放置的建筑走这条）
                }

                if (!UnityData.IsInMainThread)
                {
                    return;
                }

                loadGraphicsMethod.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] 【65】PostDraw 补加载失败（这条只打一次）："
                    + ex.Message, ErrorOnceKey);
            }
        }
    }
}
