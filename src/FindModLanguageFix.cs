using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 修复：XML 补丁里的 <li>Royalty</li> 在中文环境下永远匹配不上
    // ==========================================================================
    //
    // 【症状】
    //
    // 游戏日志里成片出现这样的错误（2026-10-04 实测，一次启动就 4 个模组中招）：
    //
    //     [Vanilla Furniture Expanded]                 Patch operation FindMod(Royalty) failed
    //     [Blue Archive Furniture]                     Patch operation FindMod(Royalty) failed
    //     [华夏扩展 Chinese Comprehensive Expansion]    Patch operation FindMod(Royalty) failed
    //     [Vanilla Furniture Expanded - Spacer Module] Patch operation FindMod(Royalty) failed
    //
    // 而这些模组确实都装了、也确实都加载了（日志里 Ludeon.RimWorld.Royalty 赫然在列）。
    // 所以「找不到 Royalty」这件事本身就是错的 —— 它是被判定逻辑坑了。
    //
    // --------------------------------------------------------------------------
    // 【根因：判定比的是「会被翻译的显示名」】
    //
    // 反编译 Verse.PatchOperationFindMod.ApplyWorker（原文）：
    //
    //     for (int i = 0; i < mods.Count; i++)
    //     {
    //         if (ModLister.HasActiveModWithName(mods[i]))   // ← 只按「名字」查
    //         { flag = true; break; }
    //     }
    //
    // 再看 Verse.ModLister.HasActiveModWithName（原文）：
    //
    //     foreach (ModMetaData mod in mods)
    //     {
    //         if (mod.Active && mod.Name == name)            // ← 只比对 mod.Name
    //             return true;
    //     }
    //
    // 关键就在这个 mod.Name。对普通模组来说它取自 About.xml 的 <name>，是固定的；
    // 但**官方 DLC 的 mod.Name 取自 ExpansionDef.label —— 一个会被语言包翻译的显示名**。
    //
    // 于是简体中文环境下：
    //
    //     模组作者写的             <li>Royalty</li>
    //     而 DLC 的实际 mod.Name    「皇权」
    //     判定结果                 "皇权" == "Royalty"  →  false
    //
    // 补丁于是**静默空转**：不报红字的原因只是外面套了 FindMod，
    // 它把「找不到」当成正常分支安静跳过了。
    //
    // 更麻烦的是，这类判定往往写在 PatchOperationSequence 里面，
    // 而 Sequence 是**一句失败、后面全部中止** —— 所以每一个这样的失败，
    // 都会连带丢掉一批**与 Royalty 毫无关系**的补丁。
    //
    //（这个坑我第一次是在 VFE 的钢琴补丁上遇到的，当时写了
    //  Patches/VFEPianoFirepitRestore.xml 用 xpath 绕过去补做；
    //  现在把根因一次性修掉，所有模组都受益。）
    //
    // --------------------------------------------------------------------------
    // 【修法：只在「原本判定失败」时，按 packageId 再补一次】
    //
    // 直接给 ModLister.HasActiveModWithName 挂一个收尾器（Postfix）：
    //
    //     · 原本匹配成功 → 什么都不做（绝不打扰既有行为）
    //     · 原本匹配失败 → 拿 name 去查「官方 DLC 的 packageId 末段」
    //
    // 为什么是「末段」：官方 DLC 的 packageId 格式非常规整 ——
    //
    //     Ludeon.RimWorld            （基础游戏）
    //     Ludeon.RimWorld.Royalty
    //     Ludeon.RimWorld.Ideology
    //     Ludeon.RimWorld.Biotech
    //     Ludeon.RimWorld.Anomaly
    //     Ludeon.RimWorld.Odyssey
    //
    // 末段恰好就是模组作者写的那几个词（Royalty / Ideology / ...）。
    //
    // --------------------------------------------------------------------------
    // 【为什么只对 Ludeon.RimWorld.* 做末段匹配 —— 这是本补丁最重要的安全设计】
    //
    // 如果放开了对**所有模组**做「packageId 末段匹配」，会引入误匹配：
    // 比如某个模组的 packageId 以 .Core 结尾，那么任何写了 <li>Core</li> 的补丁
    // 都会突然「匹配成功」，把本来不该执行的补丁激活起来 —— 那是比原 bug 更糟的事。
    //
    // 所以这里的规则收得很紧：
    //   · 完整 packageId 相等  → 允许（对所有模组）
    //   · 官方 DLC 的末段相等  → 允许（仅限 Ludeon.RimWorld 前缀）
    //   · 其余一律不匹配
    //
    // --------------------------------------------------------------------------
    // 【性能】
    //
    // · DLC 名 → packageId 的映射**只在第一次用到时构建一次**（官方 DLC 就那 5~6 个），
    //   之后每次查询只是一次字典查找，O(1)。
    // · 本方法只在「Def 加载阶段」被调用（每个含 FindMod 的补丁一次，量级几百次），
    //   **不在任何 tick / 每帧路径上**，所以开销可以忽略。
    // · 映射里存的是字符串引用，不产生垃圾。
    //
    // --------------------------------------------------------------------------
    // 【影响面】
    //
    // HasActiveModWithName 在整个游戏程序集里只有 2 个调用者（已用反编译工具核对）：
    //   · Verse.PatchOperationFindMod.ApplyWorker  ← 本补丁要修的就是它
    //   · Verse.DesignationCategoryDef             ← 顺带也修好了（同源问题）
    // 所以这个补丁不会波及别处。
    // ==========================================================================
    [HarmonyPatch(typeof(ModLister), nameof(ModLister.HasActiveModWithName))]
    internal static class Patch_ModLister_HasActiveModWithName
    {
        // 「官方 DLC 的短名」→「它的完整 packageId」，例如 "Royalty" → "Ludeon.RimWorld.Royalty"。
        // 第一次用到时构建，之后一直复用。
        private static Dictionary<string, string> dlcShortNameToPackageId;

        // 出错时用的去重日志 key（Log.ErrorOnce 需要）。
        private const int ErrorLogKey = 0x464D4C46; // "FMLF"

        // 官方 DLC 的 packageId 前缀。只有以此开头的模组才会参与「末段匹配」。
        private const string OfficialPrefix = "Ludeon.RimWorld";

        private static void Postfix(string name, ref bool __result)
        {
            // 原本就匹配成功 → 一个字都不动。
            // 这是本补丁的核心安全前提：只在原本会失败时才介入。
            if (__result)
            {
                return;
            }

            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // 这个补丁跑在 Def 加载阶段，任何意外都不该掀翻加载流程。
            // 但「吞掉」不等于「不吭声」：出错会记一条 ErrorOnce。
            try
            {
                EnsureDlcMap();

                if (dlcShortNameToPackageId == null)
                {
                    return;
                }

                string packageId;
                if (!dlcShortNameToPackageId.TryGetValue(name, out packageId))
                {
                    return;   // 不是官方 DLC 的短名，照旧判为「没找到」
                }

                // 还要确认这个 DLC 当前**确实启用着** ——
                // 玩家可能装了但没勾选，那种情况下依然应当判为「没找到」。
                if (ModLister.GetActiveModWithIdentifier(packageId, false) != null)
                {
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] Patch_ModLister_HasActiveModWithName 出错"
                    + "（已忽略，不影响游戏；该补丁失效时行为与未安装本补丁一致）：" + ex, ErrorLogKey);
            }
        }

        // ----------------------------------------------------------------------
        // 构建「官方 DLC 短名 → packageId」映射。
        // 只在第一次需要时执行，之后直接返回。
        // ----------------------------------------------------------------------
        private static void EnsureDlcMap()
        {
            if (dlcShortNameToPackageId != null)
            {
                return;
            }

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 遍历所有已安装的模组（含官方 DLC）。
            // 注意：这里刻意**不筛 Active** —— 映射描述的是「名字与 packageId 的对应关系」，
            // 与「当前有没有启用」是两回事；启用与否在 Postfix 里另行判断。
            foreach (ModMetaData mod in ModLister.AllInstalledMods)
            {
                if (mod == null)
                {
                    continue;
                }

                string packageId = mod.PackageId;
                if (string.IsNullOrEmpty(packageId))
                {
                    continue;
                }

                if (!packageId.StartsWith(OfficialPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 只收官方系列，避免任何误匹配
                }

                // 完整 packageId 也登记一条（例如模组作者写全名的情况）
                if (!map.ContainsKey(packageId))
                {
                    map[packageId] = packageId;
                }

                // 再登记「末段短名」
                int lastDot = packageId.LastIndexOf('.');
                if (lastDot >= 0 && lastDot + 1 < packageId.Length)
                {
                    string shortName = packageId.Substring(lastDot + 1);
                    if (!map.ContainsKey(shortName))
                    {
                        map[shortName] = packageId;
                    }
                }
            }

            // 基础游戏的特殊情况：
            // 它的 packageId 是 "Ludeon.RimWorld"，末段是 "RimWorld"，
            // 但模组作者习惯把它写作 Core。这里补上这一条别名。
            map["Core"] = "Ludeon.RimWorld";

            dlcShortNameToPackageId = map;
        }
    }
}
