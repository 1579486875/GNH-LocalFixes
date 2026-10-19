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
    //   文件名也只在启动时算一次（按 sourceFile 取）。
    // ==========================================================================
    [HarmonyPatch(typeof(PatchOperation), nameof(PatchOperation.Apply))]
    internal static class Patch_PatchOperation_Apply
    {
        /// <summary>文件名（含 .xml）→ 开关 Id。只含本模组登记过的文件。</summary>
        private static readonly Dictionary<string, string> fileToToggle = BuildMap();

        /// <summary>
        /// 本模组的根目录，由 LocalFixesMod 构造函数在安装补丁之前写入。
        ///
        /// 用途：判定一条 Operation 是不是**我们自己的**补丁文件。
        /// 只比文件名不够 —— 别的模组只要也有一个同名文件（例如也放一份
        /// ConfigErrorCleanup.xml），我们的开关就会去拦别人的补丁。
        /// 本机 6543 个 patch 文件实测零碰撞，但那是运气，不是保证。
        /// 为空时退化为「只按文件名匹配」（并已在构造函数里打过警告）。
        /// </summary>
        internal static string OwnRootDir;

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
        /// 把一条 Operation 解析成「本模组的某个补丁文件 + 对应开关 Id」。
        /// 解析不出来（不是本模组的补丁 / 代码里手工 new 的 / 出异常）一律返回 false，
        /// 调用方据此放行。
        /// </summary>
        internal static bool Resolve(PatchOperation op, out string fileName, out string toggleId)
        {
            fileName = null;
            toggleId = null;
            try
            {
                if (op == null)
                {
                    return false;
                }
                string path = op.sourceFile;
                if (path.NullOrEmpty())
                {
                    return false;
                }
                fileName = Path.GetFileName(path);
                if (fileName.NullOrEmpty())
                {
                    return false;
                }
                if (!fileToToggle.TryGetValue(fileName, out toggleId))
                {
                    return false;   // 名字不在本模组的表里 —— 无条件放行
                }

                // 名字对上还不够，路径也得在本模组目录下 —— 否则别的模组放一个同名
                // 补丁文件就会被我们误拦。OwnRootDir 为空（没取到）时跳过这一层，
                // 退化成「只按文件名匹配」，与旧行为一致，不会因此全都不拦。
                string root = OwnRootDir;
                if (!root.NullOrEmpty()
                    && !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                // 判定本身出错时按「不是本模组的补丁」处理 —— 放行。
                return false;
            }
        }

        /// <summary>
        /// 这条 Operation 是不是「属于本模组某个被玩家关掉的补丁文件」。
        /// Apply 与 Complete 两处共用它，保证「执行」与「报错」两个阶段的判定绝不跑偏。
        /// </summary>
        internal static bool IsSkippedBySettings(PatchOperation op)
        {
            string fileName;
            string toggleId;
            if (!Resolve(op, out fileName, out toggleId))
            {
                return false;
            }
            try
            {
                return !LocalFixesMod.IsFixEnabled(toggleId);
            }
            catch (Exception)
            {
                return false;   // 读设置出错就当没关 —— 宁可多修
            }
        }

        /// <summary>
        /// 返回 false 表示「跳过这一条」。
        /// </summary>
        private static bool Prefix(PatchOperation __instance, ref bool __result)
        {
            try
            {
                string fileName;
                string toggleId;
                if (!Resolve(__instance, out fileName, out toggleId))
                {
                    return true;   // 不是本模组的补丁文件 —— 无条件放行
                }

                if (LocalFixesMod.IsFixEnabled(toggleId))
                {
                    // 记账：这一条真的被执行了。同一个文件的多条 Operation 会重复记，
                    // 但 FixReport 内部是 HashSet，重复添加无副作用。
                    FixReport.MarkLoaded(toggleId);
                    return true;   // 玩家没关这一项
                }

                // 玩家关掉了：跳过这一条。
                //
                // __result 给 true 是「对 ApplyPatches 而言没出错」。
                // 真正会制造红字的是收尾阶段的 Complete()（它看的是私有字段
                // neverSucceeded，而那个字段只在 Apply() 体内被置 false），
                // 那一步由下面的 Patch_PatchOperation_Complete 拦住。
                __result = true;

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

    // ==========================================================================
    // 收尾阶段：被玩家关掉的那些 Operation，不许再报「failed」。
    // ==========================================================================
    //
    // 【为什么必须补这一刀 —— 这是 v1.4.10 引入的一个真 bug，v1.4.12 修掉】
    //
    //   反编译 Verse.LoadedModManager.ClearCachedPatches（原文）：
    //
    //       foreach (ModContentPack runningMod in runningMods)
    //           foreach (PatchOperation patch in runningMod.Patches)
    //               patch.Complete(runningMod.Name);
    //
    //   而 Verse.PatchOperation 的收尾判据是一个私有字段 neverSucceeded：
    //
    //       private bool neverSucceeded = true;          // 初值 true
    //
    //       public bool Apply(XmlDocument xml) {
    //           bool flag = ApplyWorker(xml);
    //           ...                                       // success 模式换算
    //           if (flag) { neverSucceeded = false; }     // 只有这里会清掉它
    //           return flag;
    //       }
    //
    //       public virtual void Complete(string modIdentifier) {
    //           if (neverSucceeded)
    //               Log.Error($"[{modIdentifier}] Patch operation {this} failed\nfile: {sourceFile}");
    //       }
    //
    //   关键点：neverSucceeded **只在 Apply() 体内**被清掉。
    //   我们在 Apply 的 Prefix 里直接返回 false、整段 Apply 根本不执行，
    //   于是它永远保持 true —— Complete() 必然打一条
    //   「Patch operation ... failed」外加文件路径。
    //
    //   换句话说：**玩家关掉一个 XML 开关，下次启动反而多一条红字**，
    //   与「关掉就不该有噪音」的初衷正好相反。
    //   （v1.4.10/v1.4.11 的注释曾把因果写反，以为「返回值给成功」就能避免 ——
    //     实际上返回值根本影响不到这个字段，必须从 Complete 这一侧拦。）
    //
    //   PatchOperationSequence 覆写了 Complete，但反编译显示它只做了
    //   base.Complete(...) + 清空 lastFailedOperation，所以拦基类就够。
    // ==========================================================================
    [HarmonyPatch(typeof(PatchOperation), nameof(PatchOperation.Complete))]
    internal static class Patch_PatchOperation_Complete
    {
        /// <summary>
        /// 返回 false = 跳过原方法 = 不打那条 failed。
        /// 只对「本模组某个被关掉的 XML 补丁文件」生效，别的模组一概不碰。
        /// </summary>
        private static bool Prefix(PatchOperation __instance)
        {
            return !Patch_PatchOperation_Apply.IsSkippedBySettings(__instance);
        }
    }
}
