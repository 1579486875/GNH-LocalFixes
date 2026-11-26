// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：**背景故事创建器**（BackstoryConstructor，工坊 2907131508）
    // 一打开窗口就抛 NullReferenceException，导致「禁用特性 / 绑定特性」两栏
    // 整个画不出来、点也点不动。
    // ==========================================================================
    //
    // 【为什么必须由我们来补】
    //
    // 作者的代码里有两处缺陷，而这两处叠加才致命：
    //
    //   ① `BackstoryTemplate` 的构造函数是空的 ——
    //        public BackstoryTemplate() { }
    //      所以 `forcedTraits` / `disallowedTraits` 两个 `List<string>` 字段的初始值是 null。
    //
    //   ② `ExposeData()` 读盘时**不给默认值** ——
    //        Scribe_Collections.Look<string>(ref forcedTraits, "forcedTraits", LookMode.Value,
    //                                        Array.Empty<object>());
    //      第 4 个参数是 `params object[] ctorArgs`，**不是 defaultValue**。
    //      配置文件里没有这个键时，字段就保持 null。
    //
    //   ③ 绘制时不判空 ——
    //        private void DrawBackstoryTraits(..., List<string> first, List<string> second)
    //        {
    //            ...
    //            if (!first.Contains(traitInfo) && !second.Contains(traitInfo))   // ← 崩在这里
    //
    //   `Utility.MakeBackstoryTemplate()`（「新建」按钮走的那条路）会显式 new 出两个 List，
    //   所以**新建的模板是安全的**；凡是从旧配置读进来、或由别的模组直接 new 出来的模板，
    //   这两个字段就一定是 null。
    //
    //   实测本机配置 `Mod_2907131508_BackstoryConstructorMod.xml`：
    //   **1,074 个模板，`forcedTraits` 与 `disallowedTraits` 一个都没有** → 全部 null
    //   → 编辑器只要一开就必然报错，跟玩家点哪个模板无关。
    //
    // 【修法】
    //
    //   挂 `BackstoryTemplate.ExposeData()` 的 Postfix：读完盘后，把仍是 null 的
    //   那两个字段补成空 `List<string>()`。这样：
    //     · 不需要玩家去删配置文件；
    //     · 不需要改作者的任何文件（工坊更新时自动跟随）；
    //     · 空列表与「没有禁用/绑定特性」语义完全一致，**不改变任何既有数据**；
    //     · 之后游戏自己写盘时也会把这两个键写进去，问题就此根除。
    //
    //   另挂 `Window_BackstoryConstructor.DrawBackstoryTraits` 的 Prefix 做第二道防线：
    //   万一将来作者改了加载时机、或者有别的模组绕过 ExposeData 直接 new 模板，
    //   绘制时也会把 null 换成空列表，而不是让整个窗口炸掉。
    //
    // 【为什么全程走反射】
    //
    //   `BackstoryTemplate` / `Window_BackstoryConstructor` 都是**第三方模组的类型**，
    //   我们的程序集在编译期不能引用它（作者随时可能改名或删掉整个模组）。
    //   所以：类型、方法、字段全部用字符串名字在运行时找；找不到就置位跳过、
    //   只在日志里留一条 Message，绝不让本补丁影响其它功能。
    // ==========================================================================

    /// <summary>
    /// 背景故事创建器（BackstoryConstructor）的空引用防护。
    /// <para>
    /// 安装方式：由 <see cref="LocalFixesMod"/> 在模组构造函数里调用一次
    /// <see cref="Install"/>；内部有静态标志，重复调用不会重复 patch。
    /// </para>
    /// </summary>
    internal static class BackstoryConstructorTraitsFix
    {
        /// <summary>一次性守卫：装过就不再尝试（本进程只试一次）。</summary>
        private static bool installed;

        /// <summary>补丁装上了才会用它来补字段，避免每次都反射。</summary>
        private static FieldInfo forcedTraitsField;
        private static FieldInfo disallowedTraitsField;

        /// <summary><c>BackstoryConstructor.Utility.Traits</c> —— 点「+」时列出来的可选特性目录。</summary>
        private static FieldInfo utilityTraitsField;

        /// <summary>目录清洗只做一次：第一次绘制前扫一遍就够了。</summary>
        private static bool traitsSanitized;

        /// <summary>绘制侧的第二道防线用的静态空列表（只读，绝不往里写）。</summary>
        private static readonly List<string> EmptyTraits = new List<string>();

        /// <summary>
        /// 安装补丁。找不到目标就安静跳过（那只说明玩家没装这个模组）。
        /// </summary>
        public static void Install()
        {
            if (installed) return;
            installed = true;                     // 先置位：失败也不重试，免得刷屏

            try
            {
                Type templateType = AccessTools.TypeByName("BackstoryConstructor.BackstoryTemplate");
                Type windowType = AccessTools.TypeByName("BackstoryConstructor.Window_BackstoryConstructor");

                if (templateType == null && windowType == null)
                {
                    // 玩家没装「背景故事创建器」—— 这是最常见的情况，不是错误。
                    Log.Message("[GNH LocalFixes] BackstoryConstructor 不在场，跳过「背景故事特性空引用」修复。");
                    return;
                }

                bool any = false;

                // ── 第一道防线：读盘后补齐 null 字段 ───────────────────────────
                if (templateType != null)
                {
                    forcedTraitsField = AccessTools.Field(templateType, "forcedTraits");
                    disallowedTraitsField = AccessTools.Field(templateType, "disallowedTraits");

                    MethodInfo expose = AccessTools.Method(templateType, "ExposeData", Type.EmptyTypes);
                    if (expose != null && forcedTraitsField != null && disallowedTraitsField != null)
                    {
                        LocalFixesMod.HarmonyInstance.Patch(
                            expose,
                            postfix: new HarmonyMethod(typeof(BackstoryConstructorTraitsFix)
                                .GetMethod(nameof(ExposeDataPostfix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        any = true;
                    }
                    else
                    {
                        Log.Warning("[GNH LocalFixes] BackstoryTemplate 结构变了"
                            + "（ExposeData 或两个特性字段找不到），读盘侧保护未安装；"
                            + "绘制侧防护仍会尝试。");
                    }
                }

                // ── 第二道防线：绘制时把 null 换成空列表 ──────────────────────
                if (windowType != null)
                {
                    // Utility.Traits 是「点 + 时能选什么」的来源，绘制前要清洗它，
                    // 所以先把字段反射到手（类型不在场就留 null，清洗时自动跳过）。
                    Type utilityType = AccessTools.TypeByName("BackstoryConstructor.Utility");
                    if (utilityType != null)
                    {
                        utilityTraitsField = AccessTools.Field(utilityType, "Traits");
                    }

                    MethodInfo draw = AccessTools.Method(windowType, "DrawBackstoryTraits");
                    if (draw != null)
                    {
                        LocalFixesMod.HarmonyInstance.Patch(
                            draw,
                            prefix: new HarmonyMethod(typeof(BackstoryConstructorTraitsFix)
                                .GetMethod(nameof(DrawTraitsPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)));
                        any = true;
                    }
                }

                if (any)
                {
                    Log.Message("[GNH LocalFixes] 已给「背景故事创建器」挂上特性空引用防护："
                        + "编辑器里的「禁用特性 / 绑定特性」不会再因为空列表整栏画不出来。");
                }
                else
                {
                    Log.Message("[GNH LocalFixes] 「背景故事创建器」在场，但目标方法签名对不上"
                        + "（多半是作者改版），本项修复本次不生效。");
                }
            }
            catch (Exception ex)
            {
                // 绝不让本项修复连累其它 76 项。
                Log.Warning("[GNH LocalFixes] 「背景故事特性空引用」修复安装失败，本项不生效：" + ex);
            }
        }

        /// <summary>
        /// 读完盘之后，把仍然是 null 的两个特性列表补成空列表。
        /// <para>
        /// 这里**必须**用 <see cref="object"/> 接 <c>__instance</c>：
        /// 目标类型是第三方模组的，我们不能在编译期引用它。
        /// </para>
        /// </summary>
        private static void ExposeDataPostfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                if (forcedTraitsField != null
                    && forcedTraitsField.GetValue(__instance) == null)
                {
                    forcedTraitsField.SetValue(__instance, new List<string>());
                }
                if (disallowedTraitsField != null
                    && disallowedTraitsField.GetValue(__instance) == null)
                {
                    disallowedTraitsField.SetValue(__instance, new List<string>());
                }
            }
            catch (Exception ex)
            {
                // 单项失败只记一次，不打断加载。
                Log.Warning("[GNH LocalFixes] 背景故事模板特性字段补齐失败（本个模板跳过）：" + ex);
            }
        }

        /// <summary>
        /// 绘制前做两件事：把 null 的列表参数换成静态空列表；顺手清洗 <c>Utility.Traits</c>。
        /// <para>
        /// 参数名必须与原方法一致（<c>first</c> / <c>second</c>），
        /// Harmony 靠名字绑定；加 <c>ref</c> 才能真正改掉传进去的引用。
        /// </para>
        /// </summary>
        private static void DrawTraitsPrefix(ref List<string> first, ref List<string> second)
        {
            if (first == null) first = EmptyTraits;
            if (second == null) second = EmptyTraits;
            SanitizeTraitCatalog();
        }

        /// <summary>
        /// 清洗 <c>BackstoryConstructor.Utility.Traits</c>，只留下「真能被作者那段代码解析」的项。
        /// <para>
        /// 【为什么必须做这一步 —— 2026-11-26 实机取证】
        /// </para>
        /// <para>
        /// 作者在点「+」之后的循环里这样写（反编译还原）：
        /// </para>
        /// <code>
        /// string traitInfo = enumerator.Current;
        /// if (!first.Contains(traitInfo) &amp;&amp; !second.Contains(traitInfo))
        /// {
        ///     string text = traitInfo.Split('~')[0].Trim();
        ///     int.TryParse(traitInfo.Split('[').Last().Split(']')[0], ref degree);
        ///     TraitDef val3 = TraitDef.Named(text);                              // ← 可能 null
        ///     TraitDegreeData val4 = val3.degreeDatas.Find(x => x.degree == degree);  // ← 可能 null
        ///     val2.Add(new DebugMenuOption(val4.LabelCap + ...));               // ← NRE 在这里
        /// }
        /// </code>
        /// <para>
        /// 目录项的格式是 <c>"{defName} ~ ({label}) [{degree}]"</c>，
        /// 而 <c>Split('[').Last().Split(']')[0]</c> 只要标签里自己带了方括号
        /// （模组特性标签常写成「[OO] 某某」这种），解析出来的就不是数字，
        /// <c>degree</c> 保持上一次的值 → <c>Find</c> 找不到同 degree 的档位 → 返回 null → 崩。
        /// </para>
        /// <para>
        /// 我们没法改作者的方法体，但可以**在进循环之前把不能解析的项摘掉** ——
        /// 效果等价于「这个特性本来就不该出现在可选列表里」，而不会让整个窗口崩掉。
        /// 清洗只做一次（静态标志），代价是一次 O(n) 遍历。
        /// </para>
        /// </summary>
        private static void SanitizeTraitCatalog()
        {
            if (traitsSanitized) return;
            traitsSanitized = true;

            try
            {
                if (utilityTraitsField == null) return;
                List<string> raw = utilityTraitsField.GetValue(null) as List<string>;
                if (raw == null || raw.Count == 0) return;

                // 逐项按作者的解析方式重演一遍，保留「两边都不为 null」的项
                List<string> kept = new List<string>(raw.Count);
                int dropped = 0;
                for (int i = 0; i < raw.Count; i++)
                {
                    string item = raw[i];
                    if (string.IsNullOrEmpty(item)) { dropped++; continue; }
                    if (CanAuthorResolve(item)) kept.Add(item);
                    else dropped++;
                }

                if (dropped > 0)
                {
                    raw.Clear();
                    raw.AddRange(kept);
                    Log.Message("[GNH LocalFixes] 背景故事创建器：特性目录里有 " + dropped
                        + " 项按作者的解析方式拿不到 TraitDef 或对应档位（多半是标签里带了方括号），"
                        + "已从可选列表剔除，剩 " + kept.Count + " 项。"
                        + "这份列表只是「点 + 时能选什么」，不影响任何既有模板数据。");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 特性目录清洗失败（不影响其它功能，"
                    + "但点「+」仍可能报空引用）：" + ex);
            }
        }

        /// <summary>
        /// 按作者那段代码的解析方式，判断某一项目录项能否解析出 TraitDef 与对应档位。
        /// </summary>
        private static bool CanAuthorResolve(string traitInfo)
        {
            try
            {
                string name = traitInfo.Split('~')[0].Trim();
                if (name.Length == 0) return false;

                // 复刻作者的两段 Split（故意与他的写法保持一致，才能预测他的结果）
                string[] byOpen = traitInfo.Split('[');
                string[] byClose = byOpen[byOpen.Length - 1].Split(']');
                int degree;
                if (!int.TryParse(byClose[0], out degree)) return false;

                TraitDef def = TraitDef.Named(name);
                if (def == null) return false;
                if (def.degreeDatas == null) return false;

                TraitDegreeData data = def.degreeDatas.Find(x => x.degree == degree);
                return data != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
