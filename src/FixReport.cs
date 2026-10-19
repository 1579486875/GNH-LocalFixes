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
        /// 把 FixToggles 里全部条目按当前状态数一遍。
        /// 判据顺序：已关闭 → 目标未装 → 失败 → 已装载 → 未运行。
        /// </summary>
        internal static void Count(out int total, out int loadedCount, out int disabledCount,
            out int missingCount, out int failedCount, out int notRunCount)
        {
            total = 0; loadedCount = 0; disabledCount = 0;
            missingCount = 0; failedCount = 0; notRunCount = 0;

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
    }
}
