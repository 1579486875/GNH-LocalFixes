// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

//
// ==========================================================================
// 【70】Cybranian - Backstory Constructor（DimonSever000.BackstoryConstructor.Specific）
//      编辑器一打开就抛 NRE，「禁用特性 / 绑定特性」两栏画不出来也点不动
// ==========================================================================
//
// 【症状】
//   ERROR Exception filling window for BackstoryConstructor.Window_BackstoryConstructor:
//     System.NullReferenceException
//       at ...DrawBackstoryTraits(Single& height, Single width, String labelKey,
//                                List`1 first, List`1 second)   line 301
//       at ...DrawBackstoryDisallowedTraits(...)                line 347
//       at ...DrawMainRect(Rect parent)                         line 485
//   玩家看到的就是那两栏空白、加号点不开。
//
// 【根因】反编译 1.7/Assemblies/BackstoryConstructor.dll（MVID e0e4b3d6…）取证：
//   · BackstoryTemplate 的构造函数是空的（public BackstoryTemplate() { }），
//     两个 List<string> 字段初值是 null；
//   · Utility..cctor 用 $"{defName} ~ ({label}) [{degree}]" 拼出 505 项的目录；
//   · DrawBackstoryTraits 点「+」后按同样格式反解，
//     对 TraitDef.Named / degreeDatas.Find / modContentPack **一处判空都没有**。
//   实机取证（2026-11-26 21:28:28）：
//     forcedTraits=List(0 项)  disallowedTraits=List(0 项)  ｜ Utility.Traits=List(505 项)
//   **三个候选全非 null 却仍在 line 301 崩** —— 崩点在那段无判空的反解链上。
//
//   ⚠ 订正留痕（§3.2.2 纪律 ③）：本文件早期版本以为「字段为 null」是根因，
//     连写四版外围兜底（兜参数 null → 改 backstoryTemplate 字段 → 清洗 Utility.Traits
//     → Finalizer 吞异常），每一版都被实机推翻。现在的判定是：**兜底救不了它**，
//     必须整栏接管（见同目录 背景故事特性栏替代实现.cs）。
//
// 【修法】本文件只保留三件**不碰绘制**的活，绘制全部交给替代实现：
//   ① ExposeDataPostfix：读盘后把 null 的 forcedTraits / disallowedTraits 补成空列表
//      （治本：让后续任何读这两字段的代码都不会踩 null）；
//   ② DrawTraitsPrefix（返回 bool）：打磨目录 + 兜字段，然后转交替代实现并返回 false，
//      让作者的 DrawBackstoryTraits **根本不执行**；
//   ③ DrawTraitsFinalizer：万一替代实现自己出问题，兜住异常不让整个窗口崩。
//
//   **为什么不再用 ref 参数改传入引用**：Harmony 的 ref 只在原方法参数本身是
//   ref/out 时才写回；作者的 first/second 是普通值传递，前缀改的只是形参副本 ——
//   这一条被实机日志直接证实（PREFIX 挂上了、签名也对，方法体拿到的仍是 null）。
//
// 【自动让路】
//   · 反射拿不到 BackstoryConstructor.Window_BackstoryConstructor ⟶ Install 直接返回；
//   · 设置开关 backstoryTraitsNullGuard 关掉 ⟶ 不挂补丁；
//   · requiresModById 已登记目标 packageId，设置界面会标「目标模组未启用」；
//   · 每个补丁方法自带 try/catch，单点失败不连锁。
// ==========================================================================

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

        /// <summary>
        /// <c>Window_BackstoryConstructor.backstoryTemplate</c>（private static）。
        /// 绘制前要顺着它拿当前模板，把 null 的列表字段补掉 —— 见 DrawTraitsPrefix 的注释。
        /// </summary>
        private static FieldInfo windowTemplateField;

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
                        // 顺着 private static 的 backstoryTemplate 拿当前模板 ——
                        // 这是唯一能可靠改到「方法体真正读到的那个对象」的路径。
                        windowTemplateField = AccessTools.Field(windowType, "backstoryTemplate");

                        // 替代实现要读窗口上的 cache 字段（截断文本用），拿不到就退化
                        BackstoryTraitsRenderer.BindWindow(windowType);

                        LocalFixesMod.HarmonyInstance.Patch(
                            draw,
                            prefix: new HarmonyMethod(typeof(BackstoryConstructorTraitsFix)
                                .GetMethod(nameof(DrawTraitsPrefix),
                                    BindingFlags.Static | BindingFlags.NonPublic)),
                            finalizer: new HarmonyMethod(typeof(BackstoryConstructorTraitsFix)
                                .GetMethod(nameof(DrawTraitsFinalizer),
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
        /// 绘制前把模板的两个特性列表补成空列表，并清洗特性目录。
        /// <para>
        /// 【为什么必须改字段而不是改参数 —— 2026-11-26 实机取证】
        /// </para>
        /// <para>
        /// 上一版用的是 <c>Prefix(ref List&lt;string&gt; first, ref List&lt;string&gt; second)</c>，
        /// 以为能改掉传进去的引用。日志证明**无效**：
        /// </para>
        /// <code>
        /// - PREFIX gnh.cn.cys.localfixes: ...DrawBackstoryTraits
        ///          &lt;- DrawTraitsPrefix(List`1&amp; first, ...)
        /// System.NullReferenceException at DrawBackstoryTraits(...)
        /// </code>
        /// <para>
        /// 原因：**Harmony 的 <c>ref</c> 参数只在原方法参数本身是 <c>ref</c>/<c>out</c> 时才写回**。
        /// 作者的 <c>DrawBackstoryTraits(..., List&lt;string&gt; first, List&lt;string&gt; second)</c>
        /// 是普通值传递，所以前缀里改的只是形参副本，方法体拿到的仍是 null。
        /// </para>
        /// <para>
        /// 【为什么「Disallowed 不崩、Forced 崩」—— 短路求值】
        /// </para>
        /// <code>
        /// if (!first.Contains(traitInfo) &amp;&amp; !second.Contains(traitInfo))
        /// </code>
        /// <list type="bullet">
        /// <item>第一次（Disallowed）：first = disallowedTraits（非 null）→ 不崩，
        ///       &amp;&amp; 直接短路，second 根本不求值</item>
        /// <item>第二次（Forced）：first = forcedTraits（**null**）→ first.Contains 崩</item>
        /// </list>
        /// <para>
        /// 所以真凶是 **forcedTraits 为 null**，而 Disallowed 那一趟被短路保护了。
        /// </para>
        /// <para>
        /// 【修法】直接改 <c>Window_BackstoryConstructor.backstoryTemplate</c>
        /// （private static 字段）所指对象上的两个 List —— 这样参数传进来时就已经非 null，
        /// 不依赖 Harmony 能否写回形参。
        /// </para>
        /// </summary>
        private static bool DrawTraitsPrefix(ref float height, float width, string labelKey,
                                               List<string> first, List<string> second)
        {
            // ── 【70】最终方案：整栏改由我们自己的实现绘制 ─────────────────
            // 作者那段在 505 项特性目录上必崩（实机 line 301），
            // 前面四版兜底都只能「不崩但画不出来」。这里直接接管：
            // 返回 false 让原方法不执行，改画一份同样观感、全程判空的版本。
            try
            {
                BackstoryTraitsRenderer.Draw(ref height, width, labelKey, first, second);

                // 首次绘制打一条自证 —— 「玩家点开了编辑器」这件事我们这边看不到，
                // 只有这条能证明 Prefix 真的跑到了（否则只能像上几轮那样靠猜）。
                // ⚠ 2026-11-26 实机教训：这条原先被挪进 LegacyDiagnostics()，
                //   而那个方法已不再被调用，于是日志里一直没有【70】的痕迹，
                //   验证时误判成「补丁没生效」。现在放回主路径。
                if (!firstDrawLogged)
                {
                    firstDrawLogged = true;
                    string state = "模板字段无法读取";
                    string traitsState = "Utility.Traits 无法读取";
                    try
                    {
                        object template = windowTemplateField == null ? null : windowTemplateField.GetValue(null);
                        if (template == null)
                        {
                            state = "backstoryTemplate 为 null";
                        }
                        else
                        {
                            state = "forcedTraits=" + DescribeList(forcedTraitsField == null ? null : forcedTraitsField.GetValue(template))
                                  + "  disallowedTraits=" + DescribeList(disallowedTraitsField == null ? null : disallowedTraitsField.GetValue(template));
                        }
                    }
                    catch { }
                    try
                    {
                        object tv = utilityTraitsField == null ? null : utilityTraitsField.GetValue(null);
                        traitsState = (tv == null)
                            ? "Utility.Traits 是 null"
                            : ("Utility.Traits=" + DescribeList(tv));
                    }
                    catch { }
                    Log.Message("[GNH LocalFixes] 【70】特性栏替代实现已接管绘制（" + labelKey + "）。当前模板："
                        + state + "  ｜ " + traitsState);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【70】替代绘制入口出错（这一栏跳过）：" + ex);
            }
            return false;   // 跳过原方法
        }

        /// <summary>原诊断逻辑，保留作参考（当前不再执行）。</summary>
        private static void LegacyDiagnostics()
        {
            SanitizeTraitCatalog();       // 目录清洗与模板字段无关，先做掉
            EnsureTemplateLists();        // 关键：把 null 字段补成空列表

            // 第一次进绘制时打一条自证日志 —— 用户「点开了编辑器」这件事
            // 从我们这边是看不到的，只有这条能证明补丁真的跑到了。
            if (!firstDrawLogged)
            {
                firstDrawLogged = true;
                string state = "模板字段无法读取";
                string traitsState = "Utility.Traits 无法读取";
                try
                {
                    object template = windowTemplateField == null ? null : windowTemplateField.GetValue(null);
                    if (template == null)
                    {
                        state = "backstoryTemplate 为 null";
                    }
                    else
                    {
                        state = "forcedTraits=" + DescribeList(forcedTraitsField == null ? null : forcedTraitsField.GetValue(template))
                              + "  disallowedTraits=" + DescribeList(disallowedTraitsField == null ? null : disallowedTraitsField.GetValue(template));
                    }
                }
                catch { }
                try
                {
                    // IL 里 DrawBackstoryTraits 有一段是
                    //     ldsfld Utility.Traits
                    //     callvirt GetEnumerator
                    // **中间没有判空**。如果 Traits 是 null，点 + 时必崩。
                    // 这里先把它的真实状态打出来，省得再猜。
                    if (utilityTraitsField == null)
                    {
                        traitsState = "utilityTraitsField 反射失败（Utility 类型或字段名对不上）";
                    }
                    else
                    {
                        object tv = utilityTraitsField.GetValue(null);
                        if (tv == null)
                        {
                            traitsState = "Utility.Traits 是 **null** —— 作者的 cctor 可能没跑到赋值那行";
                        }
                        else
                        {
                            traitsState = "Utility.Traits=" + DescribeList(tv);
                        }
                    }
                }
                catch (Exception ex2)
                {
                    traitsState = "读 Utility.Traits 抛异常：" + ex2.GetType().Name;
                }
                Log.Message("[GNH LocalFixes] 【70】背景故事编辑器首次绘制，补丁已生效。当前模板：" + state
                    + "  ｜ " + traitsState);
            }
        }

        /// <summary>
        /// 把 <c>backstoryTemplate</c> 上的 <c>forcedTraits</c> / <c>disallowedTraits</c>
        /// 补成空列表（若为 null）。这个对象是 static 字段指向的，绘制期间一直有效。
        /// </summary>
        private static void EnsureTemplateLists()
        {
            try
            {
                if (windowTemplateField == null) return;
                object template = windowTemplateField.GetValue(null);
                if (template == null) return;

                if (forcedTraitsField != null && forcedTraitsField.GetValue(template) == null)
                {
                    forcedTraitsField.SetValue(template, new List<string>());
                }
                if (disallowedTraitsField != null && disallowedTraitsField.GetValue(template) == null)
                {
                    disallowedTraitsField.SetValue(template, new List<string>());
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 背景故事模板特性字段补齐失败（本次绘制跳过）：" + ex);
            }
        }

        /// <summary>
        /// 兜底：即使前面两道防线都没拦住，也不让整个编辑窗口被这一个方法掀翻。
        /// <para>
        /// 返回 null = 吞掉异常。代价是这一栏当帧画不出来（与崩溃时一样），
        /// 但**窗口本身还能用**，玩家至少能继续编辑别的字段、能保存。
        /// </para>
        /// <para>
        /// 同一条异常只报一次（用 HashSet 去重），避免每帧刷屏。
        /// </para>
        /// </summary>
        private static Exception DrawTraitsFinalizer(Exception __exception, object[] __args)
        {
            if (__exception == null) return null;

            string key = __exception.GetType().Name + "|" + (__exception.StackTrace ?? "");
            if (reportedExceptions.Add(key))
            {
                // 把四个参数的实际状态一并打出来 —— 万一三保险仍没拦住，
                // 这一行就能告诉我们是「字段没补上」还是「崩在别处」。
                // 参数顺序：0=ref float height, 1=float width, 2=string labelKey,
                //           3=List<string> first, 4=List<string> second
                string argState = "参数无法读取";
                try
                {
                    if (__args != null && __args.Length >= 5)
                    {
                        argState = "labelKey=" + (__args[2] as string)
                            + "  first=" + DescribeList(__args[3])
                            + "  second=" + DescribeList(__args[4]);
                    }
                }
                catch { /* 诊断失败不影响兜底 */ }

                Log.Warning("[GNH LocalFixes] 背景故事创建器的特性栏绘制出错，已拦下以避免整个窗口崩掉"
                    + "（这一栏当帧画不出来，其它字段仍可编辑）。"
                    + "\n  " + argState
                    + "\n  异常：" + __exception);
            }
            return null;
        }

        /// <summary>把一个参数描述成「null」或「N 项」，用于诊断输出。</summary>
        private static string DescribeList(object o)
        {
            if (o == null) return "null";
            List<string> list = o as List<string>;
            if (list != null) return "List(" + list.Count + " 项)";
            return o.GetType().Name;
        }

        /// <summary>是否已经打过「首次绘制」的自证日志。</summary>
        private static bool firstDrawLogged;

        /// <summary>已经报过的异常指纹，防止每帧刷同一条。</summary>
        private static readonly HashSet<string> reportedExceptions = new HashSet<string>();

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
