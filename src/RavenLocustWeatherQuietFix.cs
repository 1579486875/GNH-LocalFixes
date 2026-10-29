// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：1.7 下只要进了地图，日志就被同一行异常刷爆：
    //
    //     ERROR [Verse] System.MissingMethodException: Method not found:
    //         'Unity.Mathematics.float2 Unity.Mathematics.math.normalize(Unity.Mathematics.float2)'
    //
    // 实测 2026-10-28：15:43 进入地图后，6 分钟刷了 12,671 行，
    // 日志以约 4 MB/分钟 膨胀（那一轮累计 48 MB）。
    //
    // --------------------------------------------------------------------------
    // 【第一件事：谁在调】
    //
    //     at RavenRace.Features.DickRain.Doto.MapComponent_LocustWeather.MapComponentUpdate()
    //     at Verse.MapComponentUtility.MapComponentUpdate(Map map)
    //     at Verse.Map.MapUpdate_Patch7(Map this)
    //     at Verse.Game.UpdatePlay_Patch2(Game this)
    //
    // 来自 RavenRace（渡鸦种族，packageId = ZuoYao.RavenRaceNSFW，
    // 本机 dll：Mods\RavenRace_NSFW\1.6\Common\Assemblies\ZuoYao_RavenRaceNSFW.dll）
    // 的「蝗虫雨」天气组件。它在地图组件更新里每帧算一次粒子方向，
    // 用的就是 Unity.Mathematics.math.normalize(float2)。
    //
    // --------------------------------------------------------------------------
    // 【第二件事：为什么找不到这个方法】
    //
    // 1.7 换了引擎（Waylith），所有 UnityEngine.* 程序集被重定向到 UnityStubs ——
    // 见 RimWorld.runtimeconfig.json：
    //     "WaylithClr.AssemblyNameRedirects": "UnityEngine*=UnityStubs;..."
    //
    // UnityStubs.dll 里**确实有** Unity.Mathematics.math 这个类（全程序集 504 个类型），
    // 但**没有 normalize 这个重载** —— 一个都没有（已逐条列过它的全部公开静态方法）。
    // 这是引擎 stub 不全，模组侧补不了：UnityStubs.dll 是游戏自带的。
    //
    // --------------------------------------------------------------------------
    // 【第三件事：为什么不能直接给那个方法打 Prefix】
    //
    // 第一版就是那么写的，结果补丁自己装不上：
    //
    //     [GNH LocalFixes] 安装蝗虫雨刷屏消音补丁失败：System.MissingMethodException
    //       at HarmonyLib.MethodBodyReader.ReadOperand(ILInstruction instruction)
    //       at HarmonyLib.MethodBodyReader.GenerateInstructions()
    //       at HarmonyLib.MethodCreator.CreateReplacement()
    //       at HarmonyLib.PatchProcessor.Patch()
    //
    // 原因很直接：Harmony 打补丁时要**读目标方法自己的 IL**，而那段 IL 里就引用了
    // 缺失的 Unity.Mathematics.math.normalize —— 解析 method token 时当场抛异常。
    // 也就是说：目标方法坏在 IL 层，任何针对它本身的 patch 都装不上。
    //
    // --------------------------------------------------------------------------
    // 【第四件事：现在怎么做】
    //
    // 绕开那个方法，改为 patch **原版的遍历器**：
    //
    //     Verse.MapComponentUtility.MapComponentUpdate(Map map)
    //     {
    //         List<MapComponent> components = map.components;
    //         for (int i = 0; i < components.Count; i++)
    //         {
    //             try { components[i].MapComponentUpdate(); }
    //             catch (Exception ex) { Log.Error(ex.ToString()); }
    //         }
    //     }
    //
    // 这个方法的 IL 是干净的（只有一个虚方法调用），能正常打补丁。
    // 我们在它的 Prefix 里把 MapComponent_LocustWeather 从 map.components 里摘掉，
    // 之后它就再也不会被遍历到 —— 不抛异常、不刷日志、零性能开销。
    //
    // 附带确认：上面这段是**每个组件独立 try/catch**，所以蝗虫雨抛异常
    // 并不会中断其它地图组件的更新（这一点当初担心过，反编译后排除）。
    //
    // --------------------------------------------------------------------------
    // 【第五件事：功能损失评估】
    //
    // 该组件依赖 Unity.Jobs / Unity.Collections 的 NativeArray + Material 粒子渲染，
    // 这套东西在换引擎后本就跑不起来。也就是说「蝗虫雨」这个天气效果在 1.7 下
    // **本来就是废的** —— 摘掉它不损失任何现有功能。
    //
    // 【第六件事：模组没装 / 改版时】
    //
    // 全程按字符串查找目标类型，不硬引用 ZuoYao_RavenRaceNSFW.dll。
    // 模组没装、或作者改了命名空间，这里只打一条 Message 就跳过。
    // ==========================================================================
    internal static class RavenLocustWeatherQuietFix
    {
        // 要摘掉的组件类型（注意在 .Doto 子命名空间里）。
        private const string TargetTypeName =
            "RavenRace.Features.DickRain.Doto.MapComponent_LocustWeather";

        private static bool installed;

        // 累计摘掉多少个（只用于日志，不参与逻辑）。
        private static int totalRemoved;

        // 已经处理过的地图。用弱引用表，地图被卸载后条目自动消失，不会把老地图留住。
        private static readonly ConditionalWeakTable<Map, object> ProcessedMaps =
            new ConditionalWeakTable<Map, object>();

        private static readonly object MapMarker = new object();

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ★ 1.7 专属守卫（2026-10-29 补）。
                //   为什么必须加：这只「蝗虫雨」组件在 1.6 下工作正常 —— 1.6 的
                //   Unity.Mathematics 里有 normalize(float2)，不会抛 MissingMethodException。
                //   而 1.6 的 ZuoYao_RavenRaceNSFW.dll 里同样存在
                //   RavenRace.Features.DickRain.Doto.MapComponent_LocustWeather 这个类型
                //   （已反编译核对），所以只靠「类型在不在」这道判据会把 1.6 也判成要修，
                //   结果是把 1.6 正常的天气组件白摘掉 —— 那是功能损失，不是修复。
                //
                //   判据用 Verse.MaterialDef：它是 1.7 换引擎（Waylith）后新增的 Def 类型，
                //   1.6 的 Assembly-CSharp 里根本没有；与 Patches\RJW17_XmlFieldCleanup.xml
                //   用的是同一个锚点，两处保持一致。
                if (AccessTools.TypeByName("Verse.MaterialDef") == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 当前不是 1.7+（找不到 Verse.MaterialDef）；"
                        + "蝗虫雨刷屏消音补丁已跳过 —— 1.6 下该组件本身正常，不该摘。");
                    return;
                }

                // 再确认模组在不在。不在就安静跳过 —— 这是本包一贯的「自动让路」。
                Type locustType = AccessTools.TypeByName(TargetTypeName);
                if (locustType == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + TargetTypeName
                        + "（RavenRace 未安装或已改版）；蝗虫雨刷屏消音补丁已跳过。");
                    return;
                }

                MethodInfo target = AccessTools.Method(
                    typeof(MapComponentUtility), "MapComponentUpdate", new[] { typeof(Map) });
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] 找不到 Verse.MapComponentUtility.MapComponentUpdate(Map)；"
                        + "蝗虫雨刷屏消音补丁本次未安装，下次会重试。");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(RavenLocustWeatherQuietFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装蝗虫雨组件摘除补丁"
                    + "（Verse.MapComponentUtility.MapComponentUpdate）。"
                    + "RavenRace 的「蝗虫雨」天气组件在 1.7 的 Waylith 引擎下调 "
                    + "Unity.Mathematics.math.normalize(float2)，而 UnityStubs 里没有这个重载 ——"
                    + "每帧抛 MissingMethodException，实测 6 分钟刷 12,671 行、约 4 MB/分钟。"
                    + "该天气依赖的 Jobs/Burst 粒子在换引擎后本就无法运行，"
                    + "所以直接把该组件从地图组件列表里摘掉，功能损失为零。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装蝗虫雨组件摘除补丁失败（下次会重试）：" + ex);
            }
        }

        // 每帧都会被调用，但每张地图只真正处理一次（ConditionalWeakTable 记忆）。
        private static void Prefix(Map map)
        {
            try
            {
                if (map == null)
                {
                    return;
                }

                if (ProcessedMaps.TryGetValue(map, out _))
                {
                    return;
                }

                ProcessedMaps.Add(map, MapMarker);

                List<MapComponent> comps = map.components;
                if (comps == null || comps.Count == 0)
                {
                    return;
                }

                int removed = comps.RemoveAll(c =>
                    c != null && c.GetType().FullName == TargetTypeName);

                if (removed > 0)
                {
                    totalRemoved += removed;
                    Log.Message("[GNH LocalFixes] 已从地图组件列表摘除 " + removed
                        + " 个蝗虫雨组件（累计 " + totalRemoved + " 个）。"
                        + "它的每帧 MissingMethodException 应当就此停止。");
                }
            }
            catch (Exception ex)
            {
                // 这里在别人的方法前面跑，绝不能把异常抛出去。
                Log.Warning("[GNH LocalFixes] 摘除蝗虫雨组件时出错（已忽略）：" + ex.Message);
            }
        }
    }
}
