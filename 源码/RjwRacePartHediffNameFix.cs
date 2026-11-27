// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------
//
// ==========================================================================
// 【71】RJW 的 RacePartDef.hediffName 被汉化包误译成中文，导致 700 条红字
// ==========================================================================
//
// 【症状】
//   日志里成片刷这一条（本机实测 **700 次**，占全部 ERROR 的 83%）：
//
//     [RJW] Could not find a HediffDef named 肛门 referenced by RacePartDef named Anus.
//     [RJW] Could not find a HediffDef named 乳房 referenced by RacePartDef named Breasts.
//     [RJW] Could not find a HediffDef named 阴道 referenced by RacePartDef named Vagina.
//     [RJW] Could not find a HediffDef named 阴茎 referenced by RacePartDef named Penis.
//     …（机械阴道 / 机械植入器 / 泄殖腔肛门 / 龙阴道 / 犬阴茎 等，共 65 个 def）
//
// 【根因】
//   反编译 `rjw.RacePartDef`（1.7/Assemblies/RJW.dll，MVID 83b5bb80…）：
//
//     public string hediffName;                    // ← 这是 HediffDef 的 **defName 引用**
//
//     public bool TryGetHediffDef(out HediffDef hediffDef)
//     {
//         hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(hediffName);
//         if (hediffDef == null)
//             ModLog.Error("Could not find a HediffDef named " + hediffName
//                        + " referenced by RacePartDef named " + base.defName + ".");
//         return hediffDef != null;
//     }
//
//   而 `Mods/!Translation_AI_Pack/Languages/ChineseSimplified/DefInjected/rjw.RacePartDef/`
//   下的两个 AI 机器翻译文件，把 `hediffName` **当成普通文本翻了**：
//
//     <Anus.hediffName>肛门</Anus.hediffName>
//     <Breasts.hediffName>乳房</Breasts.hediffName>
//     …（rim_job_world 60 条 + castle_tentaclecults 5 条，共 65 条）
//
//   **`hediffName` 是 defName 引用，按本地化规范绝不能译**
//   （见「边缘世界本地化翻译提示词」第 5 条）。
//   译了之后 `GetNamedSilentFail("肛门")` 必然返回 null → 每个引用它的种族各报一次 → 700 条。
//
//   ⚠ 只有 **RacePartDef** 的 `hediffName` 出问题；它的 `defName` 没被污染
//     （报错里 `referenced by RacePartDef named Anus` 仍是英文）。
//
// 【修法】
//   运行时把 `hediffName` **还原成 `defName`**。
//
//   **依据**：RJW 本体 `1.7/Defs/RaceSupport/RacePartDefs.xml` 里 61 个 RacePartDef，
//   其中 60 个 `hediffName` 与 `defName` **完全相同**，0 个不同（1 个未写该字段）。
//
//   **为什么不去改 `!Translation_AI_Pack` 的文件**：
//     · 那是别人的模组，模板 §0 明确「绝不去改别人模组目录里的东西」；
//     · 就算改了，对方一更新就被覆盖；
//     · 我们自己的补丁在运行时修，换谁的汉化包都管用。
//
//   **安全性**：改之前**先验证** `defName` 能翻译成真实 HediffDef，
//   验证不过就**不动那一项**（宁可留一条红字，也不静默改可能合法的数据）。
//
// 【自动让路】
//   · RJW 不在场 ⟶ 找不到 `rjw.RacePartDef` 类型，直接返回；
//   · 汉化包没误译（`hediffName` 本来就能查到）⟶ 一项都不动；
//   · 设置开关 `rjwRacePartHediffNameFix` 关掉 ⟶ 不执行；
//   · 每项都单独判空，单点失败不影响其它项。
//
// 【★★★ 入口为什么是静态构造，而不是 Mod 构造函数 —— 2026-11-27 连错六版换来的】
//
//   反编译 `Verse.PlayDataLoader.DoPlayLoad` 的阶段顺序：
//
//     LoadedModManager.LoadAllActiveMods();     // ① 跑 Mod 构造函数
//     …
//     Parallel.ForEach(typeof(Def).AllSubclasses(), defType =>
//         GenGeneric.InvokeStaticMethodOnGenericType(
//             typeof(DefDatabase<>), defType, "AddAllInMods"));   // ② 才填充 DefDatabase
//     …
//     LongEventHandler.ExecuteWhenFinished(delegate {
//         StaticConstructorOnStartupUtility.CallAll();            // ③ 最晚，def 早已就绪
//     });
//
//   **在 ① 里读不到任何 def**，所以本项的扫描必须放在 ③。
//
//   而 ③ 只能由**静态构造**触发（`CallAll` 用反射访问带该特性的类型的静态字段），
//   所以本类标了 `[StaticConstructorOnStartup]`。
//
//   ⚠⚠ **最后一环（最难查）**：`LocalFixesMod` 构造函数里**绝对不能引用本类**
//   （包括 `TryInstall(..., RjwRacePartHediffNameFix.Install)` 这种把方法组传进去的写法）——
//   一旦引用，CLR 会在 ① 就把本类的静态构造跑掉（那时 def 还是空的，`done` 被置位），
//   而 ③ 再触发时 **CLR 保证静态构造只跑一次，不会再跑** → 永远修不到。
//
//   本类的对策：
//     · `LocalFixesMod` 里传的是 `EmptyInstall`（它自己的空方法），不引用本类；
//     · `FixToggles` 的 Entry 也不填 `patchClassName`；
//     · 记账（`FixReport.MarkLoaded`）由本类的静态构造自己做。
//
//   实测证据：错误版本里两次「读不到 def」的日志时间戳只差 **1 毫秒** —— 就是 ① 与
//   `TryInstall` 同一瞬间触发的两条。
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    /// <summary>
    /// 【71】把被汉化包误译的 <c>rjw.RacePartDef.hediffName</c> 还原成 <c>defName</c>。
    /// <para>
    /// 这是**纯数据修正**，不挂任何 Harmony 补丁 —— 在 <c>[StaticConstructorOnStartup]</c>
    /// 里跑一次即可，跑完就可以退出历史舞台。
    /// </para>
    /// <para>
    /// 入口选择与「为什么不能在 Mod 构造函数里做」的完整推导见文件头注释的最后一节。
    /// </para>
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RjwRacePartHediffNameFix
    {
        /// <summary>类型全名。用反射拿，RJW 不在场时不会因为强引用而崩。</summary>
        private const string RacePartDefTypeName = "rjw.RacePartDef";

        /// <summary>已跑过的标志，防止重复扫描（静态构造本身只跑一次，这是双保险）。</summary>
        private static bool done;

        /// <summary>
        /// 静态构造 —— **本项唯一的入口**。
        /// <para>
        /// 由 <c>StaticConstructorOnStartupUtility.CallAll()</c> 触发，那个调用点在
        /// `PlayDataLoader.DoPlayLoad` 的最后一个 `ExecuteWhenFinished` 里，
        /// 此时 `DefDatabase.AddAllInMods` 早已完成，读得到全部 def。
        /// </para>
        /// <para>
        /// 之所以要标 `[StaticConstructorOnStartup]` 而不是从 `LocalFixesMod` 构造函数调：
        /// 构造函数跑在 `AddAllInMods` **之前**，那时所有 `AllDefs` 都是空的。
        /// </para>
        /// </summary>
        static RjwRacePartHediffNameFix()
        {
            try
            {
                Install();

                // 记账：本项不经过 TryInstall，所以自己把「装上了」写进 FixReport，
                // 设置界面读的就是那份账本。
                if (LocalFixesMod.IsFixEnabled("rjwRacePartHediffNameFix"))
                {
                    FixReport.MarkLoaded("rjwRacePartHediffNameFix");
                }
            }
            catch (Exception ex)
            {
                // 静态构造里抛异常会连累整个类（乃至 `CallAll` 的后续项），必须自己兜住。
                Log.Warning("[GNH LocalFixes] 【71】静态构造出错（不影响其它修复）：" + ex);
            }
        }

        /// <summary>
        /// 扫描全部 <c>RacePartDef</c>，把查不到 HediffDef 的 <c>hediffName</c> 还原成 <c>defName</c>。
        /// </summary>
        private static void Install()
        {
            if (done) return;
            done = true;

            // 开关检查：本项不经过 TryInstall，所以这里自己查一遍。
            try
            {
                if (!LocalFixesMod.IsFixEnabled("rjwRacePartHediffNameFix"))
                {
                    Log.Message("[GNH LocalFixes] 【71】开关已关闭，跳过 RacePartDef 译名还原。");
                    return;
                }
            }
            catch { /* 读不到开关就当开着 */ }

            try
            {
                Type racePartDefType = AccessTools.TypeByName(RacePartDefTypeName);
                if (racePartDefType == null)
                {
                    // RJW 没装 —— 正常情况，不是错误。
                    Log.Message("[GNH LocalFixes] RJW 不在场，跳过【71】RacePartDef 译名还原。");
                    return;
                }

                // 拿字段：hediffName（要改的）与 defName（还原依据，在基类 Def 上）
                FieldInfo hediffNameField = AccessTools.Field(racePartDefType, "hediffName");
                FieldInfo defNameField = AccessTools.Field(typeof(Def), "defName");
                if (hediffNameField == null || defNameField == null)
                {
                    Log.Warning("[GNH LocalFixes] 【71】RacePartDef 结构变了"
                        + "（找不到 hediffName 或 defName 字段），本项跳过。");
                    return;
                }

                List<Def> parts = CollectRacePartDefs(racePartDefType);
                if (parts.Count == 0)
                {
                    Log.Warning("[GNH LocalFixes] 【71】没拿到任何 RacePartDef"
                        + "（RJW 装了但它的 XML 没加载？），本项跳过。");
                    return;
                }

                int fixedCount = 0;
                int stillBad = 0;
                List<string> samples = new List<string>();

                for (int i = 0; i < parts.Count; i++)
                {
                    Def part = parts[i];
                    try
                    {
                        string hediffName = hediffNameField.GetValue(part) as string;
                        string defName = defNameField.GetValue(part) as string;

                        // 空值 / 本来就能查到 → 不动
                        if (string.IsNullOrEmpty(hediffName)) continue;
                        if (DefDatabase<HediffDef>.GetNamedSilentFail(hediffName) != null) continue;

                        // 查不到 → 尝试用 defName 还原
                        if (string.IsNullOrEmpty(defName)) { stillBad++; continue; }
                        if (DefDatabase<HediffDef>.GetNamedSilentFail(defName) == null)
                        {
                            // defName 也查不到 —— 说明这个 RacePartDef 本身指向了不存在的 HediffDef，
                            // 不是译名问题。按「拿不准时不动」原则保持原样。
                            stillBad++;
                            continue;
                        }

                        hediffNameField.SetValue(part, defName);
                        fixedCount++;
                        if (samples.Count < 6) samples.Add(defName + "（原值「" + hediffName + "」）");
                    }
                    catch (Exception ex)
                    {
                        stillBad++;
                        if (samples.Count < 6) samples.Add("(异常: " + ex.GetType().Name + ")");
                    }
                }

                if (fixedCount > 0)
                {
                    Log.Message("[GNH LocalFixes] 【71】已把 " + fixedCount
                        + " 个 RacePartDef 的 hediffName 还原成 defName"
                        + "（汉化包把 defName 引用当文本翻了，导致 RJW 刷"
                        + "「Could not find a HediffDef named 中文…」）。样例："
                        + string.Join("；", samples.ToArray()));
                }
                else
                {
                    Log.Message("[GNH LocalFixes] 【71】没有需要还原的 RacePartDef"
                        + "（汉化包没误译，或已被作者修好）。扫描了 " + parts.Count + " 个。");
                }

                if (stillBad > 0)
                {
                    Log.Warning("[GNH LocalFixes] 【71】另有 " + stillBad
                        + " 个 RacePartDef 的 hediffName 查不到对应 HediffDef，"
                        + "且用 defName 也查不到 —— 那是那个 RacePartDef 本身指向了不存在的 HediffDef，"
                        + "不是译名问题，按「拿不准不动」原则保持原样。");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【71】RacePartDef 译名还原失败（不影响其它修复）：" + ex);
            }
        }

        /// <summary>
        /// 收集全部 <c>RacePartDef</c>。**双路径**：先走官方索引，空了再走模组原始数据。
        /// <para>
        /// ⚠ **这个点连错三版，全是 2026-11-27 实机抓出来的**，记下来免得再犯：
        /// </para>
        /// <list type="number">
        /// <item>
        /// <c>DefDatabase&lt;Def&gt;.AllDefsListForReading</c> —— **永远是空表**。
        /// 这是模板**附录 A 第 33 条**：基类 `Def` 那个泛型实例的 `defsList` 从不被填充，
        /// 游戏只往 `DefDatabase&lt;TraitDef&gt;` 这类**具体类型**里加。
        /// </item>
        /// <item>
        /// 自己反射 `MakeGenericType(typeof(DefDatabase&lt;&gt;), racePartDefType)` +
        /// `AllDefsListForReading` —— 仍然空（当时是在 `Mod` 构造函数期调用的）。
        /// </item>
        /// <item>
        /// <c>GenDefDatabase.GetAllDefsInDatabaseForDef(Type)</c> —— 它内部走
        /// `GenGeneric.GetStaticPropertyOnGenericType(typeof(DefDatabase&lt;&gt;), defType, "AllDefs")`，
        /// 是游戏自己用的入口，**现在时机对了就能拿到**。
        /// </item>
        /// <item>
        /// 兜底：直接遍历 <c>LoadedModManager.RunningModsListForReading</c> 的 `AllDefs`。
        /// 反编译确认 `DefDatabase&lt;T&gt;.AddAllInMods()` 的数据源就是
        /// `DefsToGoInDatabase&lt;T&gt;(mod) =&gt; mod.AllDefs.OfType&lt;T&gt;()`，
        /// 所以这条路径不依赖任何泛型索引，最不容易出意外。
        /// </item>
        /// </list>
        /// </summary>
        private static List<Def> CollectRacePartDefs(Type racePartDefType)
        {
            List<Def> parts = new List<Def>();
            HashSet<string> seen = new HashSet<string>();

            void Take(Def d)
            {
                if (d == null) return;
                if (!racePartDefType.IsInstanceOfType(d)) return;
                if (string.IsNullOrEmpty(d.defName)) return;
                // 同名只取第一条（与 DefDatabase 的「先到先得」一致）
                if (!seen.Add(d.defName)) return;
                parts.Add(d);
            }

            // ── 路径 A：官方索引 ──
            try
            {
                System.Collections.IEnumerable all = GenDefDatabase.GetAllDefsInDatabaseForDef(racePartDefType);
                if (all != null)
                {
                    foreach (object o in all) Take(o as Def);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【71】走 DefDatabase 取 RacePartDef 失败，改用模组原始数据：" + ex);
            }

            if (parts.Count > 0) return parts;

            // ── 路径 B：模组原始数据（兜底）──
            try
            {
                List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
                if (mods != null)
                {
                    for (int m = 0; m < mods.Count; m++)
                    {
                        ModContentPack mod = mods[m];
                        if (mod == null || mod.AllDefs == null) continue;
                        // AllDefs 是 IEnumerable<Def>，直接 foreach
                        foreach (Def d in mod.AllDefs) Take(d);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【71】遍历模组 AllDefs 也失败：" + ex);
            }

            return parts;
        }
    }
}
