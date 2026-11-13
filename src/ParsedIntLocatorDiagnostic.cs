// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【69】诊断工具：把「Parsed X as int.」定位到具体的 XML 文件
    // ==========================================================================
    //
    // ── 为什么要做这个 ──
    //
    //   第三轮日志诊断 §5 里那 181 条 `Parsed <小数> as int.`：
    //     · 141 条已由【64】修掉（定位到 LDR 的 Buildings_MissileSilo_LDR.xml）
    //     · **还剩约 40 条没定位** —— 堆栈是 `ParseAndSetComplexTypeField_System_Int32`，
    //       不是 SiloTuple 那条路径，日志里也没有 defName，**光看日志找不出来**
    //
    //   全库搜那些值（0.1 / 1.5 / 14.9 / 6.9 …）也没用 —— 它们在小数字段里太常见，
    //   无法区分「哪个字段是 Int32」。
    //
    // ── 这个补丁怎么定位 ──
    //
    //   反编译看到解析链是：
    //       LoadedModManager.ParseAndProcessXML
    //         → DirectXmlToObjectNew.DefFromNodeNew(XmlNode node, LoadableXmlAsset loadingAsset)
    //           → …ParseAndSetComplexTypeField_System_Int32
    //             → ParseHelper.ParseIntPermissive(string str)
    //
    //   **`DefFromNodeNew` 的第二个参数就带着那份 XML 文件**，而 `LoadableXmlAsset` 有：
    //       public readonly string name;             // 文件名
    //       public readonly string fullFolderPath;   // 所在文件夹
    //       public readonly ModContentPack mod;      // 所属模组
    //
    //   所以：
    //     ① Prefix `DefFromNodeNew` → 把 name / fullFolderPath 存进静态字段；
    //     ② Prefix `ParseIntPermissive` → 只在小数（含 `.`）时，把这两个字段一起打出来。
    //
    //   这样每条 `Parsed X as int.` 后面就会多一行：
    //       [GNH LocalFixes] 【69】Parsed 14.9 as int — from Defs/AbilityDefs/Archon.xml
    //                        (…\workshop\content\294100\2842502659\1.7)
    //
    // ── 为什么默认关闭 ──
    //
    //   它是**诊断**，不是修复 —— 打开后会给日志加行。按模板 §0.2 的分类，这一项属于
    //   「纯诊断（不改行为）」，所以做成**独立开关**、**默认关**：需要排查时在设置里打开，
    //   查完再关掉。
    //
    //   默认关还有一个好处：它不改变任何游戏的加载结果，**开着关着都不影响存档**。
    //
    // ── 边界 ──
    //
    //   · 只在 `str` 含 `.` 时才打（正常整数不刷）；
    //   · 最多打 200 条（防止某个坏模组把它变成刷屏器）；
    //   · 拿不到 asset（比如运行时动态解析）时打 `(unknown)`，不崩；
    //   · 两个目标方法任一找不到就整项跳过并说明。
    // ==========================================================================

    /// <summary>
    /// 【69】诊断：把「Parsed X as int」定位到具体 XML 文件。**默认关闭**。
    /// </summary>
    internal static class ParsedIntLocatorDiagnostic
    {
        private const int MaxLines = 200;
        private const int ErrorOnceKey = 0x474E486F;   // ASCII "GNNo"

        private static bool installed;

        /// <summary>当前正在解析的 XML 文件名（由 DefFromNodeNew 的 Prefix 写入）。</summary>
        private static string currentName = "(unknown)";

        /// <summary>当前正在解析的 XML 所在文件夹。</summary>
        private static string currentFolder = "";

        private static int logged;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo defFromNode = AccessTools.Method(
                    typeof(DirectXmlToObjectNew), "DefFromNodeNew",
                    new[] { typeof(XmlNode), typeof(LoadableXmlAsset) }, null);
                MethodInfo parseInt = AccessTools.Method(
                    typeof(ParseHelper), "ParseIntPermissive",
                    new[] { typeof(string) }, null);

                if (defFromNode == null || parseInt == null)
                {
                    Log.Error("[GNH LocalFixes] 【69】target method not found"
                        + " (DefFromNodeNew / ParseIntPermissive); the diagnostic is skipped.");
                    return;
                }

                MethodInfo preDef = AccessTools.Method(
                    typeof(ParsedIntLocatorDiagnostic), nameof(DefFromNodePrefix));
                MethodInfo preInt = AccessTools.Method(
                    typeof(ParsedIntLocatorDiagnostic), nameof(ParseIntPrefix));

                if (preDef == null || preInt == null)
                {
                    Log.Error("[GNH LocalFixes] 【69】internal error: prefixes not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    defFromNode, new HarmonyMethod(preDef), null, null, null);
                LocalFixesMod.HarmonyInstance.Patch(
                    parseInt, new HarmonyMethod(preInt), null, null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【69】diagnostic armed (default OFF in settings)."
                    + " When enabled it prints one extra line for each \"Parsed X as int.\","
                    + " naming the XML file and folder that produced it —"
                    + " so the remaining ~40 decimal-in-int-field warnings from the third"
                    + " log-diagnosis round can finally be traced to their mods."
                    + " It changes nothing about loading or saving.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【69】failed to install: " + ex);
            }
        }

        /// <summary>记下「现在在解析哪个文件」。</summary>
        private static void DefFromNodePrefix(LoadableXmlAsset loadingAsset)
        {
            try
            {
                if (loadingAsset == null)
                {
                    currentName = "(unknown)";
                    currentFolder = "";
                    return;
                }
                currentName = loadingAsset.name ?? "(null)";
                currentFolder = loadingAsset.fullFolderPath ?? "";
            }
            catch
            {
                currentName = "(unknown)";
                currentFolder = "";
            }
        }

        /// <summary>小数被当整数解析时，把来源打出来。</summary>
        private static void ParseIntPrefix(string str)
        {
            try
            {
                if (!LocalFixesMod.IsFixEnabled("parsedIntLocator"))
                {
                    return;
                }
                if (str == null || str.IndexOf('.') < 0)
                {
                    return;
                }
                if (logged >= MaxLines)
                {
                    return;
                }
                logged++;

                string line = "[GNH LocalFixes] 【69】Parsed " + str + " as int — from "
                    + currentName;
                if (!string.IsNullOrEmpty(currentFolder))
                {
                    line += "  (" + currentFolder + ")";
                }
                if (logged == MaxLines)
                {
                    line += "   [达上限，后续不再打印]";
                }
                Log.Warning(line);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] 【69】diagnostic threw: " + ex.Message, ErrorOnceKey);
            }
        }
    }

}
