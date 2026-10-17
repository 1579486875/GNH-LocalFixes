using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：每次启动都刷一条红字 ——
    //
    //     Type rjwpe.SoundReplacementDef is not a Def type or could not be found,
    //     in file SoundDef_Replacement.xml.
    //
    // 出问题的模组是「RimJobWorld Pedophilia Extension」
    // （packageId = rim.job.world.pe，本机路径 Mods\RJW_功能_重金属冶炼核心，v 按 About 为准）。
    // ==========================================================================
    //
    // 【第一件事：到底是谁的错 —— 是「作者改名后忘了删旧文件」】
    //
    // 该模组的 Voice Patch 目录下有两个 XML，它们的**结构几乎一样**，但用的类型名不同：
    //
    //     Voice Patch\Defs\ReplacementDef\ReplacementDef_Voice.xml   ← 2025-09-04
    //         <rjwpe.VoiceReplacementDef>      ← 新类型名，日志里**不报错**
    //
    //     Voice Patch\Defs\SoundDefs\SoundDef_Replacement.xml        ← 2025-08-12
    //         <rjwpe.SoundReplacementDef>      ← 旧类型名，每次都报错
    //
    // 全模组 11 个 DLL 逐个搜过：`SoundReplacementDef` **一次都没出现过**，
    // 而 `VoiceReplacementDef` 在 rjwpe.VoicePatch.dll 里**找得到**（反编译核对）。
    // 结论：作者在 2025-09-04 把类型从 SoundReplacementDef 重命名为 VoiceReplacementDef，
    // 更新了新文件、但把 8 月 12 日的旧文件落在了原地。
    //
    // 于是游戏读到 `<rjwpe.SoundReplacementDef>` 时找不到这个类，
    // 整份文件的两个 def 都被跳过 —— **那条功能从来就没有生效过**，
    // 而新文件 ReplacementDef_Voice.xml 已经把「按年龄换声音」这件事完整实现了。
    //
    // 【第二件事：所以「摘掉它」不会丢任何功能】
    //
    // 被跳过的两个 def（Replacement_HumanFemale_Voice / Replacement_HumanMale_Voice）
    // 从加入那天起就没进过 Def 数据库。摘掉它们等于把游戏一直在做的事
    // （跳过 + 报错）改成「跳过 + 不报错」——**行为完全等价，只是不再刷红字**。
    //
    // 【第三件事：为什么在 XML 阶段摘，而不是在补丁阶段摘】
    //
    // 这条红字是 `DirectXmlLoader` **解析阶段**抛的，发生在「所有 def 合并」之前 ——
    // 那时候 XML PatchOperation 还没轮到执行。所以拿 `.xml` 补丁去改它是够不着的，
    // 必须在 XML 刚读进内存、还没送进解析器之前动手。
    // 本补丁挂在 `LoadedModManager.LoadModXML` 的收尾处，正是那个时机
    // （与 Patch_LoadModXML_FilterTelandaGenes 同一手法）。
    //
    // 【第四件事：什么情况下它会自动失效（不用手动关）】
    //
    // 判据是「**这个类型现在到底存不存在**」，不是「某个模组开没开」：
    //
    //   · 作者哪天把 SoundReplacementDef 加回 DLL，判据立刻变成「类型存在」，
    //     本补丁**什么都不做** —— 那份 XML 会照常加载，功能照常生效。
    //   · 作者哪天把旧文件删了，那就什么都匹配不到，本补丁也什么都不做。
    //   · 模组没装 / 被禁用 → 直接返回。
    //
    // 拿不准时一律**按「类型存在」处理 ⇒ 不摘**：宁可留一条红字，也不静默删掉合法数据。
    //
    // 【安全设计】
    //
    //   1. 只处理 packageId 正好是 rim.job.world.pe 的文档，绝不碰别人的 XML。
    //   2. 只删**元素名精确等于** rjwpe.SoundReplacementDef 的节点，不删整个文件 ——
    //      将来作者往同一文件里加正常 def，照样能加载。
    //   3. 先收集、后删除（遍历中改节点集合会出问题）。
    //   4. 整段 try/catch：任何意外都只是「没摘成」，绝不会让模组加载失败。
    //   5. 摘完之后打一条说明日志（含条数），方便在 Player.log 里核对。
    // ==========================================================================
    [HarmonyPatch(typeof(LoadedModManager), "LoadModXML")]
    public static class Patch_LoadModXML_FilterRjwpeStaleDefs
    {
        // ⚠ packageId 一律写小写来比较：RimWorld 会把它们规范化成小写，
        //    拿 About.xml 里的原始大小写去比会**静默失效**（同类事故见 telanda 那份补丁的注释）。
        private const string PePackageId = "rim.job.world.pe";

        // 要找的那个元素名（就是报错里念的那一串，一字不差）。
        private const string StaleElementName = "rjwpe.SoundReplacementDef";

        [HarmonyPostfix]
        public static void Postfix(ref List<LoadableXmlAsset> __result)
        {
            if (__result == null || __result.Count == 0)
            {
                return;
            }

            try
            {
                // 前置一：模组不在场就没什么可处理的。
                if (!IsPeActive())
                {
                    return;
                }

                // 前置二：类型**存在**就立刻收手 —— 说明作者的 DLL 里又有这个类了，
                //        那份 XML 是合法数据，必须原样加载。
                if (TypeExists(StaleElementName))
                {
                    return;
                }

                int removed = 0;

                for (int i = 0; i < __result.Count; i++)
                {
                    LoadableXmlAsset asset = __result[i];
                    if (asset == null || asset.mod == null || asset.xmlDoc == null)
                    {
                        continue;
                    }

                    if (!string.Equals(asset.mod.PackageId, PePackageId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    removed += StripNodes(asset.xmlDoc);
                }

                if (removed > 0)
                {
                    Log.Message("[GNH LocalFixes] 已摘掉 " + removed
                        + " 个无人可用的 " + StaleElementName + " 定义"
                        + "（来自 RJW PE 的 SoundDef_Replacement.xml）。"
                        + "原因：该类型在 PE 的全部 11 个程序集里都不存在 —— 作者 2025-09-04 把它改名为 "
                        + "rjwpe.VoiceReplacementDef 并另写了 ReplacementDef_Voice.xml，"
                        + "但把 8 月 12 日的旧文件忘在了原地，于是启动时必报 "
                        + "\"Type ... is not a Def type\"。"
                        + "那批定义**从来没生效过**，摘掉它们与原版行为完全等价，只是不再刷红字。"
                        + "若作者日后把类型加回来，判据会自动变成「类型存在」，本项随即不再动手。");
                }
            }
            catch (Exception ex)
            {
                // 出任何意外都只是「这一项没跑成」，绝不影响模组加载本身。
                Log.Warning("[GNH LocalFixes] rjwpe 残留 def 清理本次跳过（该项修复未生效）：" + ex);
            }
        }

        // 删掉该文档里全部 <rjwpe.SoundReplacementDef> 节点，返回删除条数。
        private static int StripNodes(XmlDocument doc)
        {
            XmlNodeList nodes = doc.GetElementsByTagName(StaleElementName);
            if (nodes == null || nodes.Count == 0)
            {
                return 0;
            }

            // 先收集再删 —— 遍历过程中修改节点集合会出问题。
            List<XmlNode> doomed = new List<XmlNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null)
                {
                    doomed.Add(nodes[i]);
                }
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                XmlNode node = doomed[i];
                if (node.ParentNode != null)
                {
                    node.ParentNode.RemoveChild(node);
                }
            }

            return doomed.Count;
        }

        // 这个类型现在能不能被解析出来？判不准时一律返回 true（= 当作「存在」= 不摘）。
        private static bool TypeExists(string typeName)
        {
            try
            {
                return GenTypes.GetTypeInAnyAssembly(typeName) != null;
            }
            catch
            {
                return true;   // 问不出来 ⇒ 保守当作「类型存在」⇒ 不删
            }
        }

        // ⚠ LoadedModManager.RunningMods 的静态类型是 IEnumerable<ModContentPack>，
        //    不是 List —— 不能用 .Count 或索引器，只能 foreach。
        private static bool IsPeActive()
        {
            foreach (ModContentPack pack in LoadedModManager.RunningMods)
            {
                if (pack != null
                    && string.Equals(pack.PackageId, PePackageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
