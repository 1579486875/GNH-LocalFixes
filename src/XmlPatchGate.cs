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

        /// <summary>
        /// Apply 阶段「正常返回 false」的文件名集合 —— 也就是「目标节点 / 目标模组
        /// 不在场，这条补丁按设计让路」。
        ///
        /// 【为什么需要它】
        ///   本模组的 12 个 XML 补丁都是「装上目标模组才需要修」的那种。目标没装时
        ///   它们当然打不中，但 RimWorld 的收尾阶段
        ///   （<c>LoadedModManager.ClearCachedPatches</c> → <c>PatchOperation.Complete</c>）
        ///   会把任何「一次都没成功过」的操作报成
        ///   <c>Patch operation ... failed</c> 的 **ERROR**。
        ///   于是 1.6 那个精简环境里，只要目标模组没装，一开游戏就刷一屏红字 ——
        ///   而实际上什么都没坏。
        ///
        /// 【怎么区分「按设计让路」和「真出错」】
        ///   反编译 <c>Verse.PatchOperation.Apply</c>：
        ///   · <c>ApplyWorker</c> 正常返回 false → <c>Apply</c> 返回 false，**不打日志**；
        ///   · <c>ApplyWorker</c> 抛异常          → <c>Apply</c> 内部**已经** Log.Error 过一次。
        ///   所以「返回 false 且日志里没有 Error」精确等于「目标不在场」。
        ///   真出错（xpath 语法错、类型解析失败……）在 Apply 那一层就已经红过了，
        ///   这里放过 Complete 的那条 failed，不会让任何真问题被埋掉。
        /// </summary>
        private static readonly HashSet<string> failedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经为「目标不在场」打过说明日志的文件名（一个文件只打一次）。</summary>
        private static readonly HashSet<string> reportedMissingTarget =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经为「上游已自行修好」打过说明日志的文件名（一个文件只打一次）。</summary>
        private static readonly HashSet<string> reportedYield =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 本进程里 <see cref="Verse.PatchOperation.Apply"/> 被调用的总次数 ——
        /// **不限于本模组的补丁文件**，任何模组的任何一条 Operation 都会让这个数加一。
        ///
        /// 【为什么要数这个】
        ///   它回答的是「这一轮 XML 补丁到底有没有被执行」。
        ///   正常的启动会走 thousands 次（本机实测一万两千多条 Operation）；
        ///   而某些「Def 加载缓存」类模组（例如 DefLoadCache，packageId
        ///   fluxxfield.defloadcache）会在命中缓存时**直接跳过整个 ApplyPatches**，
        ///   def 数据库从磁盘上的缓存快照载入。这种情况下一次都不会加。
        ///
        /// 【为什么这个数能区分「缓存跳过」与「我们的开关没接上」】
        ///   Resolve() 只对本模组登记过的 12 个文件返回 true，剩下的
        ///   都在第一道判定就 return true 放行 —— 但它们**仍然先经过 Prefix**。
        ///   所以只要 ApplyPatches 跑过，这个数就必然远大于 0。
        ///   它为 0 只可能是「Apply 根本没被调用」，而不是「文件没匹配上」。
        ///
        /// 只在启动加载期写入（单线程），读取发生在设置界面 —— 用 volatile 足够。
        /// </summary>
        internal static volatile int ApplyCallsSeen;

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
            // 计数放在 try 之前：即使下面出了意外，也要如实反映「Apply 确实被调用过」。
            ApplyCallsSeen++;
            try
            {
                string fileName;
                string toggleId;
                if (!Resolve(__instance, out fileName, out toggleId))
                {
                    return true;   // 不是本模组的补丁文件 —— 无条件放行
                }

                // ---- 「上游已经自己修好了」让路（模板 §3.1 的第四种形态）----
                //
                // 前三节讲的三类判据（类型存在性 / 方法存在性 / 前置模组在场性）都是
                // 检测「目标消失」。但最危险的恰恰是第四种：**目标还在，缺陷却已被作者修好** ——
                // 这时继续删/改，就是拿我们的补丁去破坏作者刚写好的新逻辑，
                // 而且日志里一条异常都不会有。
                //
                // 本机实例（2026-11-10 反编译坐实）：
                //   雪兔服装扩展在 11-05 的更新里新建了 `1.7\Assemblies\SRAELib.dll`
                //   并删掉了旧的 `SRAELib_old.dll`。新的 SRAELib 里
                //   `ThoughtDef_HediffAttraction.targetNullifyingHediffs` **是存在的**
                //   （1.6 与 1.7 目录里那两个 dll 的 MVID 完全相同：5b5cead8…），
                //   而 `1.7\Defs` 里有 4 处 `<targetNullifyingHediffs>` 正被这个补丁引用。
                //   于是在当前环境里继续执行 = **把「认知护盾免疫」这个功能删掉**。
                //
                // 判据用「目标类型有没有那个字段」——它精确回答「还需要删吗」，
                // 而且字段一旦回来就永久让路，不需要我们再回来改代码。
                if (ShouldYieldByTargetState(fileName))
                {
                    // __result = true：对 ApplyPatches 而言这一条没出错。
                    // 真正会制造红字的是收尾阶段的 Complete()，那一侧有同样的判据拦着。
                    __result = true;
                    FixReport.MarkLoaded(toggleId);   // 已处理过（主动让路），统计里算「已装载」
                    if (reportedYield.Add(fileName))
                    {
                        LocalFixesMod.SafeLogMessage(
                            "[GNH LocalFixes] XML 补丁按「上游已自行修好」让路：" + fileName
                            + " [" + toggleId + "]。目标 dll 里那个字段现在是存在的，"
                            + "继续删除只会破坏原作者的功能，所以本项这次什么都不做。"
                            + "这是设计内的自动退场，不是故障；作者的 dll 若退回旧版，本项会自动恢复。");
                    }
                    return false;
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

        /// <summary>
        /// Apply 之后记账：这条属于本模组的 Operation 是不是「目标不在场，按设计让路」。
        ///
        /// <para>
        /// 只处理 <c>__result == false</c> 的情况，也就是 <c>ApplyWorker</c> **正常**返回 false。
        /// 真正抛异常的那种，<c>PatchOperation.Apply</c> 内部已经 Log.Error 过一次，
        /// 异常本身仍然可见，这里再补一刀只会变成噪音。
        /// </para>
        /// </summary>
        private static void Postfix(PatchOperation __instance, bool __result)
        {
            if (__result)
            {
                return;    // 补丁成功执行，什么都不用记
            }
            try
            {
                string fileName;
                string toggleId;
                if (!Resolve(__instance, out fileName, out toggleId))
                {
                    return;    // 不是本模组的文件，一概不碰
                }
                failedFiles.Add(fileName);
                if (reportedMissingTarget.Add(fileName))
                {
                    LocalFixesMod.SafeLogMessage(
                        "[GNH LocalFixes] XML 补丁按「目标不在场」跳过：" + fileName
                        + " [" + toggleId + "]。这一条要修的目标模组 / Def 当前没有安装或没有启用，"
                        + "属于设计内的让路，不是故障；装上目标模组后下次启动会自动生效。"
                        + "（如果目标模组明明启用着还看到这一行，那就是这条 xpath 写错了，"
                        + "请把这一行连同 Player.log 反馈给作者。）");
                }
            }
            catch (Exception)
            {
                // 记账本身失败不影响补丁执行；宁可少一条日志，也不要从补丁里漏异常出去。
            }
        }

        /// <summary>
        /// 「上游已经自己修好了」让路判据。
        ///
        /// <para>
        /// 名字里之所以不叫「冗余」而叫「让路」：判断的是**这一条补丁现在还有没有活干**，
        /// 而不是「这一条该不该从清单里删掉」。删条目是人的决定（还要同步文档、语言文件、
        /// 工坊简介与清单图），而让路是代码在每次启动时自己重新判断 —— 上游修好就静默退场，
        /// 上游退回去又自动恢复。两者混为一谈会让「清单」变成一份会撒谎的文档。
        /// </para>
        ///
        /// <para>
        /// 目前只有一项需要它。<b>加新项时必须同时满足两个条件</b>：
        /// ① 该项的「需要修」前提是**目标 dll 缺某个成员**（不是目标模组不在场 —— 那是
        /// <see cref="Resolve"/> + 开关管辖的另一回事）；
        /// ② 这个成员能用 <c>AccessTools</c> 精确问到。做不到就别加 ——
        /// 猜错的代价是「明明还需要修却不修」，那比多修一次糟得多。
        /// </para>
        /// </summary>
        /// <param name="fileName">补丁文件名（含 .xml），来自 <see cref="Resolve"/>。</param>
        /// <returns>true 表示「本项现在无事可做，应当整段跳过」。</returns>
        private static bool ShouldYieldByTargetState(string fileName)
        {
            if (string.Equals(fileName, "RJW17_SraeLibFieldCleanup.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return SraeLibHasTargetField();
            }
            // ── 【44】NMM 旧架构守卫（2026-11-11 用户追问后补）──
            //
            // 这条删的是 10 个 `li Class="NudityMattersMore.CompProperties_XXX"`。
            // 判定前提是「那 10 个类型已经不存在」。但玩家可能仍在用**旧版 NMM**：
            //
            //   旧版 11-06（316,416 B）：那 10 个类型**全在**，且**没有 CompNMM**
            //   新版 11-10（363,520 B）：10 个合并进 CompNMM，旧类型消失
            //
            // 所以：**只有在新架构（CompNMM 存在）时才删**；
            // 旧版 NMM 下必须整段让路 —— 否则删掉的是他们真正在用的 10 个组件，
            // 功能会真的丢（不是「清理死引用」了）。
            if (string.Equals(fileName, "NMM_StaleCompCleanup.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return !NmmHasNewArchitecture();
            }
            // ── TY2Valley 的远程炮击组件（同类缺陷，2026-11-11 排查时一并补）──
            //
            // 这条删的是 `li Class="VFESecurity.CompProperties_LongRangeArtillery"`。
            // 实测当前所有 VFE Security 的 dll（113,664 B / 79,872 B）**都不含这个类型** ——
            // 作者在某次更新里删掉了它，所以留着会产生
            //   `Could not find type named VFESecurity.CompProperties_LongRangeArtillery`
            // 并连带整个 Def 文件加载失败。
            //
            // 但**旧版 VFE Security 是有这个类型的**：那种玩家装上我们这条，
            // 等于把炮塔的远程炮击能力删掉。所以和【44】一样按类型存在性分流。
            if (string.Equals(fileName, "FixTY2ValleyLongRangeArtillery.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return VfeSecurityStillHasLongRangeArtillery();
            }
            // ── 2026-11-11：把「只考虑最新版」这个盲区扩到 Add / AddModExtension 类 ──
            //
            // 上面两条是 Remove 类。但 **Add 类同样会坑旧版玩家**：
            // 往 def 里塞一个 `<li Class="某第三方类型">`，若那个类型在旧版模组里不存在，
            // 解析该 def 时就会 `Could not find type named ...`，**整个 def 被丢弃**。
            //
            // 下面三条都是「写死了第三方类型名」的，统一按类型存在性分流：
            //   · 类型在  ⇒ 照做（认为该模组版本支持这套扩展）
            //   · 类型不在 ⇒ **整段让路**（宁可少修一处，不可让玩家的 def 被丢掉）
            if (string.Equals(fileName, "VFEE_RoyalTitleExtFix.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                // 18 条 AddModExtension 全部往**原版爵位 def**（Yeoman/Acolyte/Knight…）里塞
                // VFEEmpire.RoyalTitleDefExtension。旧版 VFEE 若没这个扩展类型，
                // 被丢的是原版爵位 —— 影响面比本补丁要修的东西大得多。
                return !TypeExists("VFEEmpire.RoyalTitleDefExtension");
            }
            if (string.Equals(fileName, "RJW_SkillSexOrderFix.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                // 【48】往 RJW 的 Sex 技能里塞 CharacterEditorSkillFix.SkillOrderExtension。
                // FindMod 只能证明「那个模组开着」，证明不了「那个模组有这个类型」。
                return !TypeExists("CharacterEditorSkillFix.SkillOrderExtension");
            }
            // ⚠ 2026-11-11 自查：原本这里还给 ConfigErrorCleanup.xml 加了一条
            //   「OberoniaAurea.CompProperties_Hackable 不存在就让路」的判据 —— **已删掉**。
            //   原因：那个 Class 名只出现在**注释**里（是「v1.4.12 已作废」的历史记录），
            //   该文件 12 条操作里没有一条真的碰它。按注释加守卫 = 让整份文件白白让路，
            //   属于会误伤其它 11 条修复的多余判据。
            //
            // ── 【50】无限精液工坊的两个储液桶（2026-11-11）──
            // 删的是 `li Class="RJW_Menstruation_Fluids.CompProperties_CumGatherer"`。
            // 实测 ELT 那个模组的三个 dll（57,856 / 25,088 / 24,576 B）**都没有这个类型**，
            // 而 `MayRequire="eltoro.rjw.menstruation.fluids"` 只查模组启用、不查类型存在，
            // 所以这两行会一路走到解析阶段才炸（comps 整表失败 ⇒ 两个储液桶 def 一起消失）。
            //
            // 守卫逻辑与【44】/TY2Valley 同款：**类型还在就让路**。
            // 万一作者把 CumGatherer 加回来，那两行就是真功能，删了等于砍掉精液收集。
            if (string.Equals(fileName, "CumworksBadCompRefFix.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TypeExists("RJW_Menstruation_Fluids.CompProperties_CumGatherer");
            }
            return false;
        }

        // ── 通用小工具 ──────────────────────────────────────────────

        private static readonly System.Collections.Generic.Dictionary<string, bool> typeCache =
            new System.Collections.Generic.Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>
        /// 类型是否真实存在（带缓存）。判不出来按「不存在」处理 —— 调用方据此让路。
        /// </summary>
        private static bool TypeExists(string fullName)
        {
            bool v;
            if (typeCache.TryGetValue(fullName, out v))
            {
                return v;
            }
            try
            {
                v = AccessTools.TypeByName(fullName) != null;
            }
            catch (Exception)
            {
                v = false;
            }
            typeCache[fullName] = v;
            return v;
        }

        private static int vfeArtState;   // 0=未判, 1=还有该类型, -1=已删除

        /// <summary>
        /// VFE Security 是否仍然带着 <c>CompProperties_LongRangeArtillery</c>。
        ///
        /// ⚠ 2026-11-11 第一版这里写错过：我用 `Building_TurretGunCE` /
        /// `CompProperties_Shield` 两个**猜的**类型名去判断「VFE Security 在不在」，
        /// 结果两个都是 null ⇒ 判成「判不出来」⇒ 永远让路 ⇒
        /// **原修复失效，日志里照样报
        /// `Could not find type named VFESecurity.CompProperties_LongRangeArtillery`**。
        ///
        /// 正确做法：判断「模组装没装」用 <see cref="Verse.ModsConfig.IsActive"/>，
        /// 它是 id 判定，不依赖我猜的类型名。只有「版本」这一层才需要类型探测。
        /// </summary>
        private static bool VfeSecurityStillHasLongRangeArtillery()
        {
            if (vfeArtState != 0)
            {
                return vfeArtState > 0;
            }
            const string typeName = "VFESecurity.CompProperties_LongRangeArtillery";
            const string modId = "vanillaexpanded.vfesecurity";

            // ① 该类型还在 ⇒ 旧版 VFE Security，删了会丢炮塔的远程炮击能力 ⇒ 让路
            if (TypeExists(typeName))
            {
                vfeArtState = 1;
                return true;
            }

            // ② 类型不在。但要确认「是版本删了它」还是「根本没装」——
            //    没装时 xpath 本来就匹配不到，让不让路都一样，按让路处理最省事。
            bool modPresent;
            try
            {
                modPresent = ModsConfig.IsActive(modId);
            }
            catch (Exception)
            {
                modPresent = false;
            }
            if (!modPresent)
            {
                return true;      // 没装 ⇒ 不缓存、让路
            }

            // ③ 装了、但类型没了 ⇒ 新架构 ⇒ 执行删除（这才是本补丁要修的）
            vfeArtState = -1;
            return false;
        }

        /// <summary>
        /// NMM 是否已经是「新架构」（10 个 comp 合并成 CompNMM）。
        /// 判据是类是否存在 —— 这是唯一能把新老两版分开的可靠信号：
        /// 新版有 <c>NudityMattersMore.CompNMM</c>，旧版没有。
        /// </summary>
        private static int nmmArchState;   // 0=未判, 1=新架构, -1=旧架构

        private static bool NmmHasNewArchitecture()
        {
            if (nmmArchState != 0)
            {
                return nmmArchState > 0;
            }
            bool has = false;
            try
            {
                // 只在 NMM 本体在场时才判；不在场时按「旧架构」处理（= 不删），
                // 因为 NMM 没装时那些节点本来就不存在，删不删都一样 —— 保守起见不动。
                Type t = AccessTools.TypeByName("NudityMattersMore.CompNMM");
                if (t == null)
                {
                    Type any = AccessTools.TypeByName("NudityMattersMore.CompProperties_BugsTracker")
                        ?? AccessTools.TypeByName("NudityMattersMore.CompProperties_CoverBody");
                    if (any == null)
                    {
                        return false;      // 完全判不出来 → 不缓存、按旧架构（不删）
                    }
                    has = false;           // 找到旧类型 ⇒ 旧架构
                    nmmArchState = -1;
                    return has;
                }
                has = true;
                nmmArchState = 1;
            }
            catch (Exception)
            {
                has = false;
            }
            return has;
        }

        /// <summary>
        /// 目标字段结果缓存：0 = 还没判过，1 = 字段在（要让路），-1 = 字段不在（照常执行）。
        /// </summary>
        private static int sraeLibFieldState;

        /// <summary>
        /// 雪兔公共库（SRAELib）的 <c>ThoughtDef_HediffAttraction</c> 里到底有没有
        /// <c>targetNullifyingHediffs</c> 这个字段。
        ///
        /// <para>
        /// 三种情况都按「让路判据不成立」以外的语义处理，逐一交代：
        /// · <b>类型找不到</b>（SRAELib 没装 / 没启用）→ 返回 false，让原补丁照常走。
        ///   那时 xpath 命中 0 个节点，Remove 自带 <c>success=Always</c>，静默无事 ——
        ///   与不加守卫完全一致。**不缓存**这个结果，免得某次判定过早把结论钉死。
        /// · <b>字段在</b> → 返回 true，整段跳过，保护作者的功能。
        /// · <b>字段不在</b>（玩家装的是旧 dll）→ 返回 false，执行删除，恢复原本要修的问题。
        /// </para>
        /// </summary>
        private static bool SraeLibHasTargetField()
        {
            if (sraeLibFieldState != 0)
            {
                return sraeLibFieldState > 0;
            }
            bool has = false;
            try
            {
                Type t = AccessTools.TypeByName("SRAELib.ThoughtDef_HediffAttraction");
                if (t != null)
                {
                    has = AccessTools.Field(t, "targetNullifyingHediffs") != null;
                    sraeLibFieldState = has ? 1 : -1;   // 类型在才固化结论
                }
            }
            catch (Exception)
            {
                // 反射失败一律按「字段不在」处理：那会让原补丁照常执行，
                // 也就是退回到「没有这条守卫」的老行为 —— 绝不能因为守卫自己出错就
                // 把需要修的东西也一起跳掉。
                has = false;
            }
            return has;
        }

        /// <summary>
        /// 这条 Operation 是不是「上游已修好、按设计让路」。
        /// 收尾阶段据此放过那条 <c>failed</c>（否则玩家会看到一条莫名其妙的红字）。
        /// </summary>
        internal static bool IsYieldedByTargetState(PatchOperation op)
        {
            try
            {
                string fileName;
                string toggleId;
                if (!Resolve(op, out fileName, out toggleId))
                {
                    return false;
                }
                return ShouldYieldByTargetState(fileName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 这条 Operation 是不是「刚才在 Apply 阶段因为目标不在场而正常失败过」。
        /// 收尾阶段据此放过那条 <c>failed</c>，避免 1.6 精简环境一开游戏就刷红字。
        /// </summary>
        internal static bool IsKnownMissingTarget(PatchOperation op)
        {
            try
            {
                string fileName;
                string toggleId;
                if (!Resolve(op, out fileName, out toggleId))
                {
                    return false;
                }
                return failedFiles.Contains(fileName);
            }
            catch (Exception)
            {
                return false;   // 判定出错时照常报，宁可多一条红字也不埋问题
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
            // ① 玩家在设置里关掉了这个补丁文件 —— 它本来一次都没跑，不该报 failed。
            if (Patch_PatchOperation_Apply.IsSkippedBySettings(__instance))
            {
                return false;
            }

            // ② 目标模组 / Def 不在场，ApplyWorker 正常返回了 false —— 那是设计内的
            //    让路，Apply 的后置已经打过一条说明，这里不再补一条 ERROR。
            //    （判据见 Patch_PatchOperation_Apply.failedFiles 的注释：
            //     真正的故障在 Apply 那一层就已经 Log.Error 过了。）
            if (Patch_PatchOperation_Apply.IsKnownMissingTarget(__instance))
            {
                return false;
            }

            // ③ 上游已自行修好、我们在 Apply 阶段整段跳过了 —— 同样一次都没跑，
            //    同样不该报 failed。少了这一步，玩家每次启动都会看到一条
            //    「Patch operation ... failed」，而实际上什么都没坏。
            return !Patch_PatchOperation_Apply.IsYieldedByTargetState(__instance);
        }
    }
}
