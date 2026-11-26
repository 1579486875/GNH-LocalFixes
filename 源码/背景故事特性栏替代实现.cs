// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------
//
// ==========================================================================
// 【70】Cybranian - Backstory Constructor（DimonSever000.BackstoryConstructor.Specific）
//      编辑器里「禁用特性 / 绑定特性」两栏的加号按钮点不开
// ==========================================================================
//
// 【症状】
//   打开「背景故事创建器」后点「+」，窗口整片报错甚至崩掉；日志原文（实机）：
//
//     ERROR Exception filling window for BackstoryConstructor.Window_BackstoryConstructor:
//       System.NullReferenceException
//         at BackstoryConstructor.Window_BackstoryConstructor.DrawBackstoryTraits(...)
//         at ...DrawBackstoryDisallowedTraits(...)  line 347
//         at ...DrawMainRect(Rect parent)           line 485
//
//   玩家感受就是「那两栏点不开、特性加不进去」。
//
// 【根因】反编译 1.7/Assemblies/BackstoryConstructor.dll（MVID e0e4b3d6…）
//   作者的 DrawBackstoryTraits 在点「+」之后这样反解特性目录：
//
//     string text = traitInfo.Split('~')[0].Trim();
//     int.TryParse(traitInfo.Split('[').Last().Split(']')[0], ref degree);
//     TraitDef val3 = TraitDef.Named(text);
//     TraitDegreeData val4 = val3.degreeDatas.Find(x => x.degree == degree);
//     val2.Add(new DebugMenuOption(val4.LabelCap + " (" + val3.modContentPack.Name + ")", ...));
//
//   而目录由 Utility..cctor 用 $"{defName} ~ ({label}) [{degree}]" 拼成。
//   实机取证：Utility.Traits = List(505 项)、first/second 都是 List(0 项)、
//   两者都**不是** null，可仍在 line 301 崩 —— 说明崩点在这段**一处判空都没有**的
//   反解链上：505 项里只要有一项在当前加载顺序下拿不到 TraitDef 或对应 degree，
//   val3/val4 就是 null，val4.LabelCap 直接 NRE。
//
//   ⚠ 前面试过四版「外围兜底」全部被实机推翻：
//     v1 兜 first/second 为 null      → 实机两个都是 List(0 项)
//     v2 直接改 backstoryTemplate 字段 → 字段补上了，仍然 NRE
//     v3 清洗 Utility.Traits          → 实机是 List(505 项)，不是 null
//     v4 加 Finalizer 兜异常          → 兜住了，但那一栏永远画不出来
//   所以本文件不再兜底，改成**整栏接管**。
//
// 【修法】Prefix 返回 false 让原方法根本不执行，改由 Draw() 自己画：
//   · 标题行：同原版（Translate(labelKey) + 黄色；反射取 Utility.YellowColor，
//     取不到退化 Color.yellow —— 少一次反射失败就崩一个窗口不值得）
//   · 「+」按钮：点击时**自己构建候选**，逐项验证 TraitDef.Named + degreeDatas.Find，
//     坏项跳过并计数写日志；不再像作者那样在 label 拼装时踩 null
//   · 已选列表：自己排两列（列宽 (width-10)/2、高 25、行距 5，与原版一致）
//   · 高度累加：height = y + 15f，与原来的 += drawn.height + 15f 等价，
//     保证后面栏位（Possessions）的位置不变
//
//   **为什么不用 Transpiler**：那要改作者方法体里的 IL，
//   任何一次作者更新都会让锚点失配、静默失效，风险比自绘高。
//   **为什么不用 Finalizer 吞异常**：吞掉之后这一栏仍然是空白，
//   玩家依旧点不开 —— 只是不崩，等于没修。
//
// 【自动让路】
//   · 目标模组没装 / 版本里没有 Window_BackstoryConstructor ⟶ 反射拿不到类型，
//     Install 直接返回，本文件任何方法都不会被调用（见 BackstoryConstructorTraitsFix.Install）
//   · 设置界面开关 backstoryTraitsNullGuard 关掉 ⟶ 不挂 Harmony 补丁
//   · 本文件内部每一步都自带 try/catch：单个小方块画不出来只跳过那一块，
//     不会连带掀翻窗口
// ==========================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【70】背景故事创建器：**完整接管**特性栏的绘制。
    // ==========================================================================
    //
    // 【为什么从「打补丁兜住」改成「整个方法自己画」】
    //
    // 前面试过四版兜底，全部失败。每一版的诊断都被实机推翻：
    //
    //   v1 只兜 first/second 为 null        → 实机：两个都是 List(0 项)，非 null
    //   v2 改成直接改 backstoryTemplate 字段 → 实机：字段补上了，仍然 NRE
    //   v3 清洗 Utility.Traits              → 实机：Utility.Traits = List(505 项)，不是 null
    //   v4 加 Finalizer 兜异常              → 实机：兜住了，但那一栏永远画不出来
    //
    // 最后一次实机日志（2026-11-26 21:28:28）：
    //
    //     【70】背景故事编辑器首次绘制，补丁已生效。
    //     当前模板：forcedTraits=List(0 项)  disallowedTraits=List(0 项)
    //     ｜ Utility.Traits=List(505 项)
    //     WARN 特性栏绘制出错，已拦下…（line 301）
    //
    // **三个候选全部非 null，但仍在 line 301 崩。** 而行号对应的那条 IL
    // 是 `if (Widgets.ButtonText(val, "+", true, true, true, null))` ——
    // `ButtonText` 本身是安全的（只是转发），所以崩溃在**它返回 true 之后那段**：
    //
    //     Enumerator<string> enumerator = Utility.Traits.GetEnumerator();
    //     while (enumerator.MoveNext())
    //     {
    //         string traitInfo = enumerator.Current;
    //         if (!first.Contains(traitInfo) && !second.Contains(traitInfo))
    //         {
    //             string text = traitInfo.Split('~')[0].Trim();
    //             int.TryParse(traitInfo.Split('[').Last().Split(']')[0], ref degree);
    //             TraitDef val3 = TraitDef.Named(text);                              // ← 可能 null
    //             TraitDegreeData val4 = val3.degreeDatas.Find(x => x.degree == degree);  // ← 可能 null
    //             val2.Add(new DebugMenuOption(val4.LabelCap + " (" + val3.modContentPack.Name + ")", ...));
    //         }
    //     }
    //
    // `Utility.Traits` 是 505 项 —— 来自**所有已加载模组**的每个 TraitDef 档位。
    // 只要其中**任何一项**在当前加载顺序 / 模组启用状态下拿不到 TraitDef 或对应档位，
    // `val3`/`val4` 就会是 null，`val4.LabelCap` 直接 NRE。
    // 而这段代码**在作者手里完全没有判空**。
    //
    // 【本文件的方案】
    //
    // Prefix 返回 false → **原方法根本不执行**，改由我们自己画这一栏：
    //
    //   · 标题：与原版一致（`Translator.Translate(labelKey)`，黄色）
    //   · 右侧「+」按钮：点击时**自己构建候选列表**，逐项判空后跳过坏项
    //   · 已选列表：带 × 移除按钮 + tooltip（复刻原版观感）
    //   · 高度累加：与原版一致，保证后续栏位位置不变
    //
    // **界面长相、交互、数据语义都不变**，只是不再走作者那段会崩的循环。
    // ==========================================================================

    /// <summary>
    /// 背景故事创建器特性栏的**替代实现**。
    /// <para>
    /// 由 <see cref="BackstoryConstructorTraitsFix"/> 安装；Prefix 返回 false
    /// 即跳过原方法，改用 <see cref="Draw"/> 自己画。
    /// </para>
    /// </summary>
    internal static class BackstoryTraitsRenderer
    {
        /// <summary>原版里用来缓存截断文本的字典（可选，拿不到就用自己的）。</summary>
        private static FieldInfo truncateCacheField;

        /// <summary>“+” 弹出的候选列表缓存：每次点击重建，避免每帧遍历 505 项。</summary>
        private static readonly List<TraitChoice> candidates = new List<TraitChoice>();

        /// <summary>候选里的一项：显示名 + 要写进列表的原始串。</summary>
        private struct TraitChoice
        {
            public string Label;      // 显示给玩家看的
            public string Raw;        // 写进 forcedTraits / disallowedTraits 的原始串
        }

        /// <summary>把窗口类型上的 `cache` 字段反射到手（拿不到不影响功能）。</summary>
        public static void BindWindow(Type windowType)
        {
            try
            {
                truncateCacheField = AccessTools.Field(windowType, "cache");
            }
            catch { truncateCacheField = null; }
        }

        /// <summary>
        /// 替代原 `DrawBackstoryTraits`：
        /// <c>(ref float height, float width, string labelKey, List&lt;string&gt; first, List&lt;string&gt; second)</c>
        /// </summary>
        public static bool Draw(ref float height, float width, string labelKey,
                               List<string> first, List<string> second)
        {
            try
            {
                if (first == null) first = new List<string>();
                if (second == null) second = new List<string>();

                // ── 标题行 ────────────────────────────────────────────────
                Rect labelRect = new Rect(0f, height, width, 30f);
                Widgets.Label(labelRect,
                    ColoredText.Colorize(Translator.Translate(labelKey), TitleColor()));
                height += labelRect.height;

                // ── 右侧“+”按钮 ──────────────────────────────────────────
                Rect plusRect = labelRect;
                plusRect.y -= 3f;
                plusRect.xMin = labelRect.xMax - labelRect.height;
                if (Widgets.ButtonText(plusRect, "+", true, true, true, null))
                {
                    OpenChooser(first, second);
                }

                // ── 已选列表 ──────────────────────────────────────────────
                // 自己排两列（与原版 `(width-10)/2` 一列宽、25 高、行距 5 一致），
                // 不走 GenUI.DrawElementStack —— 那个要传 Verse 内部的委托类型，
                // 在外部程序集里引用不到，也省得再踩它的边界情况。
                float colW = (labelRect.width - 10f) / 2f;
                const float rowH = 25f;
                const float rowGap = 5f;
                float y = height;
                int col = 0;
                int drawnCount = 0;
                for (int i = 0; i < first.Count; i++)
                {
                    // 保险：列表异常长时不把窗口撑爆（正常最多几十项）
                    if (drawnCount >= 400) break;
                    float x = (col == 0) ? 0f : colW + 10f;
                    DrawChip(new Rect(x, y, colW, rowH), first[i], first, labelRect);
                    drawnCount++;
                    col++;
                    if (col >= 2)
                    {
                        col = 0;
                        y += rowH + rowGap;
                    }
                }
                if (col > 0) y += rowH + rowGap;

                height = y + 15f;
                return false;   // 已自行绘制，原方法不再执行
            }
            catch (Exception ex)
            {
                // 我们自己的实现也兜一层 —— 万一将来游戏 API 变了，
                // 至少这一栏画不出来时不会把整个窗口掀翻。
                Log.Warning("[GNH LocalFixes] 【70】替代绘制也出错了（这一栏跳过，其它字段仍可编辑）：" + ex);
                return false;
            }
        }

        /// <summary>
        /// 画一个「已选特性」小方块：背景 + × 移除按钮 + 截断文本 + tooltip。
        /// 结构与原版一致，只是每一处都做了判空。
        /// </summary>
        private static void DrawChip(Rect r, string text2, List<string> first, Rect labelRect)
        {
            try
            {
                Color old = GUI.color;
                GUI.color = CharacterCardUtility.StackElementBackground;
                GUI.DrawTexture(r, BaseContent.WhiteTex);
                GUI.color = old;

                // 右侧的 × 区域（复刻原版：贴右边、正方形、内缩 3px）
                Rect removeRect = GenUI.ContractedBy(
                    new Rect(r.xMax - r.height, r.y, r.height, r.height), 3f);
                Widgets.CheckboxDraw(removeRect.x, removeRect.y, false, false, removeRect.height);

                if (Widgets.ButtonInvisible(removeRect, true))
                {
                    first.Remove(text2);
                    return;
                }

                Rect textRect = r;
                textRect.x += 10f;
                textRect.xMax = removeRect.xMin - 5f;
                Widgets.Label(textRect, Truncate(text2, textRect.width));

                if (Mouse.IsOver(textRect))
                {
                    TooltipHandler.TipRegion(textRect, new TipSignal(text2));
                    Widgets.DrawHighlight(r);
                }
                GUI.color = Color.white;
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【70】画特性小方块出错（跳过这一块）：" + ex);
            }
        }

        /// <summary>
        /// 取作者那个 `Utility.YellowColor`（标题色）。
        /// <para>
        /// 我们没有引用 BackstoryConstructor.dll，所以只能反射；
        /// 拿不到就退化成 `Color.yellow` —— 观感接近，不会因为读不到就崩。
        /// </para>
        /// </summary>
        private static Color TitleColor()
        {
            if (cachedTitleColor.HasValue) return cachedTitleColor.Value;
            Color c = Color.yellow;
            try
            {
                Type utilityType = AccessTools.TypeByName("BackstoryConstructor.Utility");
                if (utilityType != null)
                {
                    FieldInfo f = AccessTools.Field(utilityType, "YellowColor");
                    if (f != null)
                    {
                        object v = f.GetValue(null);
                        if (v is Color) c = (Color)v;
                    }
                }
            }
            catch { }
            cachedTitleColor = c;
            return c;
        }

        /// <summary>标题色缓存，避免每帧反射。</summary>
        private static Color? cachedTitleColor;

        /// <summary>截断文本。有原版的 cache 就用它，拿不到就退化成不截断。</summary>
        private static string Truncate(string s, float width)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            try
            {
                object cache = truncateCacheField == null ? null : truncateCacheField.GetValue(null);
                if (cache != null)
                {
                    // 原版签名是 GenText.Truncate(string, float, Dictionary<string,string>)
                    return GenText.Truncate(s, width, cache as Dictionary<string, string>);
                }
            }
            catch { /* 失败就退化成原样返回 */ }
            return s;
        }

        /// <summary>
        /// 构建并弹出特性选择列表。
        /// <para>
        /// **这里是与作者实现的关键差异**：每一项都先验证
        /// `TraitDef.Named` 与 `degreeDatas.Find` 的结果，坏项直接跳过，
        /// 不再像作者那样在 label 拼装时踩 null。
        /// </para>
        /// </summary>
        private static void OpenChooser(List<string> first, List<string> second)
        {
            candidates.Clear();

            List<string> raw = null;
            try
            {
                Type utilityType = AccessTools.TypeByName("BackstoryConstructor.Utility");
                if (utilityType != null)
                {
                    FieldInfo f = AccessTools.Field(utilityType, "Traits");
                    if (f != null) raw = f.GetValue(null) as List<string>;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【70】读 Utility.Traits 失败：" + ex);
            }

            if (raw == null || raw.Count == 0)
            {
                Messages.Message("[GNH LocalFixes] 特性目录是空的（作者那边没构建出来），"
                    + "暂时没有可选特性。其它字段仍可正常编辑。",
                    MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            int skipped = 0;
            for (int i = 0; i < raw.Count; i++)
            {
                string traitInfo = raw[i];
                if (string.IsNullOrEmpty(traitInfo)) { skipped++; continue; }
                if (first.Contains(traitInfo) || second.Contains(traitInfo)) continue;

                TraitChoice choice;
                if (!TryResolve(traitInfo, out choice)) { skipped++; continue; }
                candidates.Add(choice);
            }

            if (skipped > 0)
            {
                Log.Message("[GNH LocalFixes] 【70】特性候选里跳过了 " + skipped
                    + " 项（拿不到 TraitDef 或对应档位，多半是那个模组已停用/改版），"
                    + "可用 " + candidates.Count + " 项。");
            }

            if (candidates.Count == 0)
            {
                Messages.Message("[GNH LocalFixes] 没有可添加的特性"
                    + (skipped > 0 ? "（" + skipped + " 项因为模组缺失被跳过）" : "") + "。",
                    MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            List<DebugMenuOption> options = new List<DebugMenuOption>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                TraitChoice c = candidates[i];
                options.Add(new DebugMenuOption(c.Label, DebugMenuOptionMode.Action, delegate
                {
                    first.Add(c.Raw);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options, null));
        }

        /// <summary>
        /// 按作者的解析方式还原一项，**成功才返回 true**。
        /// 失败（名对不上 / 档位不存在 / 标签被截断）一律返回 false。
        /// </summary>
        private static bool TryResolve(string traitInfo, out TraitChoice choice)
        {
            choice = default(TraitChoice);
            try
            {
                string name = traitInfo.Split('~')[0].Trim();
                if (name.Length == 0) return false;

                string[] byOpen = traitInfo.Split('[');
                string[] byClose = byOpen[byOpen.Length - 1].Split(']');
                int degree;
                if (!int.TryParse(byClose[0], out degree)) return false;

                TraitDef def = TraitDef.Named(name);
                if (def == null) return false;
                if (def.degreeDatas == null) return false;

                TraitDegreeData data = def.degreeDatas.Find(x => x.degree == degree);
                if (data == null) return false;

                string label = data.LabelCap;
                if (string.IsNullOrEmpty(label)) label = name;

                string pack = "";
                try
                {
                    if (def.modContentPack != null && !string.IsNullOrEmpty(def.modContentPack.Name))
                    {
                        pack = " (" + def.modContentPack.Name + ")";
                    }
                }
                catch { /* 包名拿不到就不显示 */ }

                choice.Label = label + pack;
                choice.Raw = traitInfo;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
