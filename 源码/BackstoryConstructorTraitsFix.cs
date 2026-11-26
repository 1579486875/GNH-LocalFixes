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
        /// 绘制前把 null 的列表参数换成静态空列表。
        /// <para>
        /// 参数名必须与原方法一致（<c>first</c> / <c>second</c>），
        /// Harmony 靠名字绑定；加 <c>ref</c> 才能真正改掉传进去的引用。
        /// </para>
        /// </summary>
        private static void DrawTraitsPrefix(ref List<string> first, ref List<string> second)
        {
            if (first == null) first = EmptyTraits;
            if (second == null) second = EmptyTraits;
        }
    }
}
