// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 诊断：把「Parsed X as int」定位到 **具体 XML 文件 + 具体字段**
    // ==========================================================================
    //
    // ── 2026-11-16 第五轮升级：补上「字段名」──
    //
    //   第一版只能给出「文件名 + 文件夹」，因为 `ParseIntPermissive(string)` 只收到字符串。
    //   实测日志长这样：
    //
    //       Parsed 6.9 as int — from ThingDef_Shell.xml (…\1.7\Core\Defs\ThingDefs)
    //
    //   一个文件里带小数的元素可能有几十处（`ThingDef_Shell.xml` 就有 63 处 / 16 种字段），
    //   光有文件名**定位不到是哪一处**。
    //
    // ── 怎么拿到字段名（反编译实证）──
    //
    //   `DirectXmlToObjectNew.EmitIlToCreateAndPopulateComplexType` 生成的 IL 里，
    //   每个字段都是这样处理的：
    //
    //       FieldInfo f = ResolveFieldForNode(typeBeingDeserialized, node, parentForDebug);
    //       Type t = ResolveTypeForNode(f, node);
    //       GetFieldSetterForType(t)(target, f, node, t);        // ← f 就是字段
    //
    //   而 `ParseValueAndSetFieldDelegate` 的签名是
    //       delegate void ParseValueAndSetFieldDelegate(object target, FieldInfo field, XmlNode node, Type t)
    //
    //   ⇒ **`ResolveFieldForNode(Type, XmlNode, XmlNode)` 的返回值就是正在解析的字段**。
    //      给它加 Postfix 记下 `FieldInfo.Name`，`ParseIntPermissive` 时一起打出即可。
    //
    //   ⚠ 为什么不去 Patch `ParseValueAndSetFieldDelegate.Invoke`：
    //      那是 delegate 的 Invoke，由 `DynamicMethod.CreateDelegate` 在运行时生成，
    //      **没有可 Patch 的 MethodBase**。
    //
    // ── 为什么默认关闭 ──
    //
    //   它是**诊断**，不是修复 —— 打开后会给日志加行。按模板 §0.2 的分类属于
    //   「纯诊断（不改行为）」，所以做成独立开关、默认关。
    //   实测本机 26 个文件 / 49 条，打开后日志多 49 行，可接受。
    //
    // ── 边界 ──
    //
    //   · 只在 `str` 含 `.` 时才打（正常整数不刷）；
    //   · 最多打 200 条（防止某个坏模组把它变成刷屏器）；
    //   · 拿不到 asset / field 时打 `(unknown)`，不崩；
    //   · 三个目标方法任一找不到就整项跳过并说明。
    // ==========================================================================

    /// <summary>
    /// 诊断：把「Parsed X as int」定位到具体 XML **文件 + 字段**。**默认关闭**。
    /// </summary>
    internal static class ParsedIntLocatorDiagnostic
    {
        private const int MaxPrints = 200;

        private static bool installed;
        private static int printed;
        private static bool capWarned;

        /// <summary>当前正在解析的 XML 文件名（由 DefFromNodeNew 的 Prefix 写入）。</summary>
        private static string curFile = "(unknown)";

        /// <summary>当前正在解析的 XML 所在文件夹。</summary>
        private static string curFolder = "(unknown)";

        /// <summary>当前正在解析的字段名（由 ResolveFieldForNode 的 Postfix 写入）。</summary>
        private static string curField = "(unknown)";

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ① DefFromNodeNew(XmlNode, LoadableXmlAsset) —— 拿文件
                MethodInfo defFromNode = AccessTools.Method(
                    typeof(DirectXmlToObjectNew), "DefFromNodeNew",
                    new[] { typeof(XmlNode), typeof(LoadableXmlAsset) }, null);

                // ② ParseIntPermissive(string) —— 拿「谁被截断了」
                MethodInfo parseInt = AccessTools.Method(
                    typeof(ParseHelper), "ParseIntPermissive",
                    new[] { typeof(string) }, null);

                // ③ ResolveFieldForNode(Type, XmlNode, XmlNode) —— 拿字段名（2026-11-16 新增）
                MethodInfo resolveField = AccessTools.Method(
                    typeof(DirectXmlToObjectNew), "ResolveFieldForNode",
                    new[] { typeof(Type), typeof(XmlNode), typeof(XmlNode) }, null);

                if (defFromNode == null || parseInt == null)
                {
                    Log.Error("[GNH LocalFixes] target method not found"
                        + " (DefFromNodeNew / ParseIntPermissive); the diagnostic is skipped.");
                    return;
                }

                MethodInfo preDef = AccessTools.Method(
                    typeof(ParsedIntLocatorDiagnostic), nameof(DefFromNodePrefix));
                MethodInfo preInt = AccessTools.Method(
                    typeof(ParsedIntLocatorDiagnostic), nameof(ParseIntPrefix));

                if (preDef == null || preInt == null)
                {
                    Log.Error("[GNH LocalFixes] internal error: prefixes not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    defFromNode, new HarmonyMethod(preDef), null, null, null);
                LocalFixesMod.HarmonyInstance.Patch(
                    parseInt, new HarmonyMethod(preInt), null, null, null);

                // 字段名这一环是可选的：拿不到就退回「只有文件名」，不影响主功能。
                string fieldNote = "no (ResolveFieldForNode not found)";
                if (resolveField != null)
                {
                    MethodInfo postField = AccessTools.Method(
                        typeof(ParsedIntLocatorDiagnostic), nameof(ResolveFieldPostfix));
                    if (postField != null)
                    {
                        LocalFixesMod.HarmonyInstance.Patch(
                            resolveField, null, new HarmonyMethod(postField), null, null);
                        fieldNote = "yes";
                    }
                }

                installed = true;
                Log.Message("[GNH LocalFixes] diagnostic armed (default OFF in settings)."
                    + " When enabled it prints one extra line for each \"Parsed X as int.\","
                    + " naming the XML file AND the field that produced it."
                    + " Field name support: " + fieldNote + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] failed to arm the Parsed-int locator: " + ex);
            }
        }

        /// <summary>进入一个 Def 时记下它来自哪个文件 / 文件夹。</summary>
        public static void DefFromNodePrefix(XmlNode node, LoadableXmlAsset loadingAsset)
        {
            try
            {
                if (loadingAsset == null)
                {
                    curFile = "(unknown)";
                    curFolder = "(unknown)";
                    return;
                }
                curFile = loadingAsset.name ?? "(unknown)";
                curFolder = loadingAsset.fullFolderPath ?? "(unknown)";
            }
            catch (Exception)
            {
                curFile = "(unknown)";
                curFolder = "(unknown)";
            }
        }

        /// <summary>
        /// 每次解析一个字段时记下字段名。**这是 2026-11-16 新增的关键一环** ——
        /// 没有它就只能定位到文件（一个文件里可能有几十处小数）。
        /// </summary>
        public static void ResolveFieldPostfix(FieldInfo __result)
        {
            try
            {
                curField = (__result == null) ? "(unknown)" : __result.Name;
            }
            catch (Exception)
            {
                curField = "(unknown)";
            }
        }

        /// <summary>
        /// 只在「值里含小数点」时打一行。正常整数不刷。
        /// </summary>
        public static void ParseIntPrefix(string str)
        {
            try
            {
                if (str == null || str.IndexOf('.') < 0)
                {
                    return;
                }
                if (printed >= MaxPrints)
                {
                    if (!capWarned)
                    {
                        capWarned = true;
                        Log.Warning("[GNH LocalFixes] Parsed-int locator reached its "
                            + MaxPrints + "-line cap; further occurrences are not printed."
                            + " (This only limits the diagnostic, not the game.)");
                    }
                    return;
                }
                printed++;
                Log.Warning("[GNH LocalFixes] Parsed " + str + " as int"
                    + " — field: " + curField
                    + "  ←  " + curFile + "  (" + curFolder + ")");
            }
            catch (Exception)
            {
                // 诊断自身绝不能抛。
            }
        }
    }
}
