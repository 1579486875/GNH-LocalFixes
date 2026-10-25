// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 「这一项这次到底装上没有」的运行期账本。
    // ==========================================================================
    //
    // 【为什么需要它】
    //
    //   玩家（尤其是订阅了本模组、但并不看 Player.log 的人）需要一个能直接看到的答案：
    //   「装了这个之后，到底有多少项在干活？」
    //
    //   光看开关是不够的：开关只说明「你想开」，不说明「实际装上了」——
    //   补丁可能因为目标方法签名变了而安装失败，也可能因为前置模组不在场而空转。
    //   这份账本就是记录**实际结果**的地方。
    //
    // 【只记两种结果】
    //
    //   Loaded：安装动作跑完了（代码侧 = Patch 成功挂上；XML 侧 = 补丁真的被执行了）
    //   Failed：安装过程中抛了异常（已被各处 catch 住，不影响其它项）
    //
    //   「已关闭」「目标模组未安装」不在这里记 —— 它们由 FixToggles.StatusOf()
    //   直接读设置与模组清单得出，记在这里反而会出现两个真相。
    //
    // 【线程安全】
    //
    //   安装发生在主线程；设置界面可能读到。用一个锁保护，成本可忽略
    //   （只在启动期写入、界面打开时读取）。
    // ==========================================================================
    internal static class FixReport
    {
        private static readonly HashSet<string> loaded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> failed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object gate = new object();

        /// <summary>记一条「装上了」。同一 Id 重复调用无副作用。</summary>
        internal static void MarkLoaded(string id)
        {
            if (id.NullOrEmpty())
            {
                return;
            }
            try
            {
                lock (gate)
                {
                    loaded.Add(id);
                    failed.Remove(id);   // 后一次成功覆盖前一次失败
                }
            }
            catch (Exception)
            {
                // 记账失败绝不影响补丁本身。
            }
        }

        /// <summary>记一条「安装失败」。同一 Id 重复调用无副作用。</summary>
        internal static void MarkFailed(string id)
        {
            if (id.NullOrEmpty())
            {
                return;
            }
            try
            {
                lock (gate)
                {
                    if (!loaded.Contains(id))
                    {
                        failed.Add(id);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        internal static bool IsLoaded(string id)
        {
            if (id.NullOrEmpty())
            {
                return false;
            }
            try
            {
                lock (gate)
                {
                    return loaded.Contains(id);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsFailed(string id)
        {
            if (id.NullOrEmpty())
            {
                return false;
            }
            try
            {
                lock (gate)
                {
                    return failed.Contains(id);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 本轮 10 个 XML 补丁项是否「没有重跑、直接由 Def 加载缓存提供」。
        ///
        /// 界面读它来决定要不要多印一行解释。含义见 MarkXmlTogglesWhenApplySkipped。
        /// </summary>
        internal static bool XmlPatchesServedFromCache { get; private set; }

        /// <summary>「本轮补丁被缓存跳过」这条日志只在第一次调用时打印。</summary>
        private static bool announcedCacheSkip;

        /// <summary>
        /// 把 FixToggles 里全部条目按当前状态数一遍。
        /// 判据顺序：已关闭 → 目标未装 → 失败 → 已装载 → 未运行。
        /// </summary>
        internal static void Count(out int total, out int loadedCount, out int disabledCount,
            out int missingCount, out int failedCount, out int notRunCount)
        {
            total = 0; loadedCount = 0; disabledCount = 0;
            missingCount = 0; failedCount = 0; notRunCount = 0;

            // 先做一次「本轮 XML 补丁有没有被跳过」的迟到记账，
            // 否则下面数出来的「未运行」会把 10 个 XML 项全算进去。
            MarkXmlTogglesWhenApplySkipped();

            FixToggles.Entry[] all = FixToggles.All;
            if (all == null)
            {
                return;
            }

            for (int i = 0; i < all.Length; i++)
            {
                FixToggles.Entry e = all[i];
                if (e == null)
                {
                    continue;
                }
                total++;

                try
                {
                    FixToggles.FixStatus st = FixToggles.StatusOf(e);
                    if (st == FixToggles.FixStatus.Disabled)
                    {
                        disabledCount++;
                        continue;
                    }
                    if (st == FixToggles.FixStatus.TargetMissing)
                    {
                        missingCount++;
                        continue;
                    }
                    if (IsFailed(e.Id))
                    {
                        failedCount++;
                        continue;
                    }
                    if (IsLoaded(e.Id))
                    {
                        loadedCount++;
                        continue;
                    }
                    notRunCount++;
                }
                catch (Exception)
                {
                    notRunCount++;
                }
            }
        }

        // ==========================================================================
        // 「整轮 XML 补丁没跑」的迟到记账
        // ==========================================================================
        //
        // 【为什么需要】
        //
        //   正常情况下，Patches\ 下 10 个文件的记账发生在 XmlPatchGate 的前缀里 ——
        //   那一段跑在 Verse.LoadedModManager.ApplyPatches 里，一条 Operation 记一次。
        //
        //   但玩家可能装了「Def 加载缓存」类模组（本机实测：DefLoadCache，
        //   packageId fluxxfield.defloadcache，工坊 3703401461）。它会在模组清单
        //   与所有 Defs/Patches/Assemblies 文件的指纹都没变时**直接跳过整个
        //   ApplyPatches**，把上次「读完 + 打完补丁」的 Def 数据库原样载回来。
        //
        //   于是本轮 PatchOperation.Apply 一次都不会被调用 —— 那 10 个 XML 项
        //   一条也没记账，汇总行就变成「共 35 项、已装载 24 项」，
        //   剩下的 11 项在界面上无处交代（2026-10-19 玩家实际遇到的疑问）。
        //
        // 【为什么这不是功能问题】
        //
        //   缓存里存的就是**上次真正执行过补丁之后**的 Def 结果，
        //   所以修复效果照样在，只是这一轮没有重跑。
        //   而且那个缓存的指纹**包含每个模组的 Patches\*.xml 与 Assemblies\*.dll**，
        //   所以我们只要改动任何补丁文件或 dll，指纹就变、缓存自动重建、补丁重新执行。
        //
        // 【怎么判断这是「缓存跳过」而不是「我们的开关没接上」】
        //
        //   XmlPatchGate 的前缀挂在 Verse.PatchOperation.Apply 上，
        //   它统计的是**全部模组**的 Operation 调用次数（ApplyCallsSeen）。
        //   只要 ApplyPatches 跑过，这个数就是四位数以上；
        //   它为 0 只可能是 ApplyPatches 整个没执行。
        //
        //   再加一道保险：只有确实存在缓存类模组时才补记。
        //   没装缓存模组却出现「0 次调用」，那是真问题，必须让界面如实显示「未运行」。
        // ==========================================================================
        private static void MarkXmlTogglesWhenApplySkipped()
        {
            XmlPatchesServedFromCache = false;
            try
            {
                // 总控关掉时，LocalFixesMod.MarkXmlTogglesWhenGateOff 已经在构造期补记过。
                if (!LocalFixesMod.IsFixEnabled("xmlPatchGate"))
                {
                    return;
                }

                // 总控开着、但那个补丁类本身没挂上 —— 那是真的没接线，
                // 绝不能当成「缓存跳过」来粉饰。
                if (!IsLoaded("xmlPatchGate"))
                {
                    return;
                }

                // 跑过就不可能为 0 —— 那是「每个模组的每条补丁都会经过这个前缀」级别的量。
                if (Patch_PatchOperation_Apply.ApplyCallsSeen > 0)
                {
                    return;
                }

                if (!AnyDefLoadCacheModPresent())
                {
                    // 没有任何缓存类模组，却一条补丁都没跑：这是真异常，不粉饰。
                    return;
                }

                FixToggles.Entry[] all = FixToggles.All;
                if (all == null)
                {
                    return;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    FixToggles.Entry e = all[i];
                    if (e == null || e.XmlPatchFile.NullOrEmpty())
                    {
                        continue;
                    }
                    if (!LocalFixesMod.IsFixEnabled(e.Id))
                    {
                        continue;   // 玩家单独关掉的，不算「已装载」
                    }
                    MarkLoaded(e.Id);
                }
                XmlPatchesServedFromCache = true;

                // 只说明一次。设置界面每帧都在重画，Count() 每帧都会被调用，
                // 不设这道闸就会把日志刷满。
                if (!announcedCacheSkip)
                {
                    announcedCacheSkip = true;
                    LocalFixesMod.SafeLogMessage(
                        "[GNH LocalFixes] 本轮的 XML 补丁阶段被整体跳过（检测到 Def 加载缓存模组，"
                        + "PatchOperation.Apply 一次都没有被调用），10 个 XML 修复按「结果来自缓存」计为已装载。"
                        + "缓存里存的是上次打完补丁的 Def 快照，修复效果仍然在；"
                        + "改动任何 Patches\\*.xml 或本模组的 dll 都会让缓存自动重建并重新执行补丁。");
                }
            }
            catch (Exception)
            {
                // 记账失败绝不影响任何修复，也不影响界面打开。
            }
        }

        /// <summary>
        /// 当前加载的模组里有没有「Def 加载缓存」类模组。
        /// 认 packageId 里带 defloadcache 的（含名字空间前缀的变体）。
        /// 读不到模组清单时返回 false —— 宁可少补记，也不掩盖真问题。
        ///
        /// 结果缓存起来：本方法在缓存命中的情况下会被设置界面**每帧**调到一次，
        /// 而模组清单在启动之后不会再变。
        /// </summary>
        private static bool? anyDefLoadCacheMod;

        private static bool AnyDefLoadCacheModPresent()
        {
            if (anyDefLoadCacheMod.HasValue)
            {
                return anyDefLoadCacheMod.Value;
            }
            bool found = false;
            try
            {
                List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
                if (mods != null)
                {
                    for (int i = 0; i < mods.Count; i++)
                    {
                        ModContentPack pack = mods[i];
                        if (pack == null)
                        {
                            continue;
                        }
                        string id = pack.PackageId;
                        if (!id.NullOrEmpty()
                            && id.IndexOf("defloadcache", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            anyDefLoadCacheMod = found;
            return found;
        }
    }
}
