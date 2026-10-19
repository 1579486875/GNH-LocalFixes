// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 让 Patches\ 下的 XML 补丁也能在「模组设置」里单独关掉。
    // ==========================================================================
    //
    // 【以前为什么不给 XML 补丁做开关】
    //
    //   技术上一直做得到，只是没做：XML 补丁的开关不能靠「装不装」实现 ——
    //   Patches\ 下的文件在 ModContentPack.LoadPatches() 里就被读进
    //   List<PatchOperation> 了，那一步比本模组的构造函数还早。
    //   所以只能**在它执行的那一刻拦下来**。
    //
    // 【拦截点为什么选 PatchOperation.Apply】
    //
    //   反编译 Verse.LoadedModManager.ApplyPatches（原文）：
    //
    //       foreach (PatchOperation item in runningMods.SelectMany(rm => rm.Patches))
    //       {
    //           try { item.Apply(xmlDoc); }
    //           catch (Exception ex) { Log.Error("Error in patch.Apply(): " + ex); }
    //       }
    //
    //   每一条 Operation 都走 Apply —— 在这里挂一个前缀，
    //   判定「这条属于哪个文件、玩家有没有关掉它」，关掉就 return false 跳过。
    //
    // 【怎么知道一条 Operation 属于哪个文件】
    //
    //   Verse.PatchOperation 有个 public 字段 sourceFile，由
    //   ModContentPack.LoadPatches() 赋值：
    //
    //       patchOperation.sourceFile = list[i].FullFilePath;
    //
    //   是**完整路径**。所以按 Path.GetFileName() 取文件名去查表即可。
    //   这个表只收 FixToggles 里明确登记了 XmlPatchFile 的项，
    //   因此**不会误伤别的模组的任何补丁** —— 名字对不上就直接放行。
    //
    // 【安全性】
    //
    //   · 任何异常都按「照常执行」处理（Prefix 返回 true）：宁可多修，不可漏修；
    //   · 玩家关掉了「让 XML 补丁也能单独关闭」那项时，本补丁类根本不会被装
    //     （InstallPatchClass 会跳过），等于全部执行；
    //   · 同一文件的多条 Operation 只会打一条「已跳过」日志（HashSet 去重），
    //     避免一个文件里十几条 Operation 刷十几遍。
    //
    // 【性能】
    //
    //   Apply 只在**启动加载期**跑，进地图之后一次都不会再调用 ——
    //   不在任何 tick / 渲染热路径上。查表是一次字典命中，
    //   文件名也只在启动时算一次（按 sourceFile 缓存）。
    // ==========================================================================
    [HarmonyPatch(typeof(PatchOperation), nameof(PatchOperation.Apply))]
    internal static class Patch_PatchOperation_Apply
    {
        /// <summary>文件名（含 .xml）→ 开关 Id。只含本模组登记过的文件。</summary>
        private static readonly Dictionary<string, string> fileToToggle = BuildMap();

        /// <summary>已经打过「已跳过」日志的文件名，避免同一文件刷多遍。</summary>
        private static readonly HashSet<string> reportedSkip =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> BuildMap()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                FixToggles.Entry[] all = FixToggles.All;
                if (all == null)
                {
                    return map;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    FixToggles.Entry e = all[i];
                    if (e == null || e.XmlPatchFile.NullOrEmpty())
                    {
                        continue;
                    }
                    map[e.XmlPatchFile] = e.Id;
                }
            }
            catch (Exception ex)
            {
                // 建表失败就返回空表 —— 空表意味着「一个都不拦」，等价于全部照常执行。
                LocalFixesMod.SafeLogError(
                    "[GNH LocalFixes] XmlPatchGate: 建表失败，本次不拦截任何 XML 补丁（全部照常执行）：" + ex);
            }
            return map;
        }

        /// <summary>
        /// 返回 false 表示「跳过这一条」。
        /// </summary>
        private static bool Prefix(PatchOperation __instance, ref bool __result)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }

                string path = __instance.sourceFile;
                if (path.NullOrEmpty())
                {
                    return true;   // 不是从 Patches\ 文件里读出来的（例如代码里手工 new 的）
                }

                string fileName = Path.GetFileName(path);
                if (fileName.NullOrEmpty())
                {
                    return true;
                }

                string toggleId;
                if (!fileToToggle.TryGetValue(fileName, out toggleId))
                {
                    return true;   // 不是本模组的补丁文件 —— 无条件放行
                }

                if (LocalFixesMod.IsFixEnabled(toggleId))
                {
                    return true;   // 玩家没关这一项
                }

                // 玩家关掉了：跳过，且把返回值按「没成功」处理。
                // 注意不能返回「成功」—— PatchOperation.neverSucceeded 的语义会让
                // 上层在收尾时把「从未成功过的补丁」记一条 error，那反而制造噪音。
                __result = false;

                if (reportedSkip.Add(fileName))
                {
                    LocalFixesMod.SafeLogMessage(
                        "[GNH LocalFixes] XML 补丁已按设置跳过：" + fileName + " [" + toggleId + "]。"
                        + "该项对应的修复这一次不会生效；想恢复请在模组设置里把对应开关重新打开。");
                }
                return false;
            }
            catch (Exception ex)
            {
                // 拦截逻辑自己出错时，宁可让补丁照常执行。
                try
                {
                    LocalFixesMod.SafeLogError(
                        "[GNH LocalFixes] XmlPatchGate.Prefix 出错，本条目按「照常执行」处理：" + ex);
                }
                catch (Exception)
                {
                    // 连日志都写不出去就彻底放弃，绝不让异常从补丁里漏出去。
                }
                return true;
            }
        }
    }
}
