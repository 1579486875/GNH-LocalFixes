using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 让「生物大师的ElToro Patches」与 telanda 的动物遗传模组不再抢同一批 defName。
    // ==========================================================================
    //
    // 【冲突是什么】
    //
    // 两个模组各自定义了一整套 RJW_BGS.RaceGeneDef，而且**名字完全相同**：
    //
    //   · telanda 的 RJW Animal Gene Inheritance（`telanda.rjw.animalgeneinheritance`）
    //     自带 39 个（Base 2 / CommonGenes 1 / FallbackGenes 1 / Vanilla_Racegroups 7
    //     / template 17 / VE_Genetics 11）；
    //   · 「生物大师的ElToro Patches」（`Mask.ElToro.Patches`）自带 25 个，
    //     而这 25 个**全部**与上面重名。
    //
    // 两个一起启用时，RimWorld 的处理**不是忽略后者，而是给它改随机名**
    //（反编译 DefDatabase<T>.Add 确认）：
    //
    //     while (defsByName.ContainsKey(def.defName))
    //     {
    //         Log.Error("Adding duplicate " + typeof(T) + " name: " + def.defName);
    //         def.defName += Mathf.RoundToInt(Rand.Value * 1000f);   // ← 追加随机数
    //     }
    //
    // 后果比"报个错"严重得多：每次启动那些基因的名字都不一样，
    // 所有按名引用它们的地方（存档、其他模组的 XML）全部失配。
    // 这正是"一加 telanda 就出问题"的真正原因。
    //
    // 顺带一提，Mask 作者其实**已经意识到**要处理这件事，他写了一个
    // Common/Patches/DefaultRJWGenesRemoval.xml 来移除 telanda 的同名定义 ——
    // 但只写了 7 个（Canine/Insect/Feline/Equine/Dragon/Rodent/Racoon），
    // 漏了 18 个；而且其中 "Racoon" 还拼错了（telanda 那边写的是 "Raccoon"，
    // 两个 c），那条移除其实从未命中。
    //
    // 【修法：只剔重名的，不碰独有的】
    //
    // 在 LoadedModManager.LoadModXML 之后（此时 XML 已读入、尚未合并成统一文档），
    // 把 **telanda 那一边**、名字落在这 25 个名单里的 RJW_BGS.RaceGeneDef 节点删掉。
    // 于是：
    //   · 25 个重名项：只剩 Mask 的那一份 → 无冲突，Mask 的改版数据生效；
    //   · telanda 独有的 14 个（CommonGenes / FallbackGenes / VE_Genetics 的 11 个
    //     / template 里的 CowMaki）：原样保留 → 它的代码要用的东西一个不少，
    //     特别是 FallbackGenes —— telanda 的 RaceGeneDef_Helper 里有一句
    //     DefDatabase<RaceGeneDef>.GetNamed("FallbackGenes", true)，
    //     第二个参数是「找不到就报错」，所以那个 def 绝不能被删。
    //
    // 【为什么不删文件、也不删整个 asset】
    //
    // 删 asset 会连 CowMaki 和 FallbackGenes 一起丢掉；而按文件删又依赖文件名。
    // 按 defName 精确剔，既不依赖文件名、也不丢任何独有内容。
    // 也正因为如此，本补丁**不需要**去改 telanda 自己的 LoadFolders.xml ——
    // 那个模组更新时会被覆盖，改动留不住。
    //
    // 【安全设计】
    //
    //   1. 只有两个模组**同时在场**才动手。Mask 不在时，telanda 就是唯一数据源，
    //      此时剔掉它的 def 等于把功能删了 —— 所以必须双向检查。
    //   2. 全表大小写严格比较（defName 本来就区分大小写）。
    //   3. 整段 try/catch：任何意外都只是"没剔成"，不会让模组加载失败。
    //   4. 只改 telanda 的 asset，逐个 asset 判断，绝不触碰别人的 XML。
    [HarmonyPatch(typeof(LoadedModManager), "LoadModXML")]
    public static class Patch_LoadModXML_FilterTelandaGenes
    {
        private const string TelandaPackageId = "telanda.rjw.animalgeneinheritance";
        private const string MaskPackageId = "Mask.ElToro.Patches";
        private const string ElementName = "RJW_BGS.RaceGeneDef";

        // 与 telanda 重名的 25 个 defName（2026-10-10 从 Mask 的三个 Defs 文件提取）。
        private static readonly HashSet<string> ClashingNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Insect_base", "Slime_base", "Muffalo", "Bison",
            "Cow", "Yak", "Rhinoceros", "Pig",
            "WildBoar", "Elephant", "Wolf_Arctic", "Wolf_Great",
            "Wolf_Timber", "Bear_Grizzly", "Bear_Polar", "BORUW",
            "YAEMU", "ASEBI", "Canine", "Insect",
            "Feline", "Equine", "Dragon", "Rodent",
            "Raccoon",
        };

        [HarmonyPostfix]
        public static void Postfix(ref List<LoadableXmlAsset> __result)
        {
            if (__result == null || __result.Count == 0)
            {
                return;
            }

            try
            {
                if (!IsModActive(MaskPackageId) || !IsModActive(TelandaPackageId))
                {
                    return;   // 缺任何一方都不动手
                }

                int removed = 0;
                for (int i = 0; i < __result.Count; i++)
                {
                    LoadableXmlAsset asset = __result[i];
                    if (asset == null || asset.mod == null || asset.xmlDoc == null)
                    {
                        continue;
                    }
                    if (asset.mod.PackageId != TelandaPackageId)
                    {
                        continue;
                    }
                    removed += StripClashingNodes(asset.xmlDoc);
                }

                if (removed > 0)
                {
                    Log.Message("[GNH LocalFixes] Animal-gene defName clash resolved: removed " + removed
                        + " duplicate RJW_BGS.RaceGeneDef definition(s) from RJW Animal Gene Inheritance, "
                        + "so that Mask.ElToro.Patches owns all " + ClashingNames.Count
                        + " shared names. Its own-only defs (incl. FallbackGenes) are untouched.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Animal-gene clash filter skipped this session (that fix is NOT active): " + ex);
            }
        }

        // 删掉该文档里所有 defName 命中名单的 RJW_BGS.RaceGeneDef 节点，返回删除条数。
        private static int StripClashingNodes(XmlDocument doc)
        {
            XmlNodeList nodes = doc.GetElementsByTagName(ElementName);
            if (nodes == null || nodes.Count == 0)
            {
                return 0;
            }

            // 先收集再删 —— 遍历过程中修改节点集合会出问题。
            List<XmlNode> doomed = new List<XmlNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                XmlNode node = nodes[i];
                if (node == null)
                {
                    continue;
                }
                XmlNode defNameNode = null;
                for (int j = 0; j < node.ChildNodes.Count; j++)
                {
                    XmlNode child = node.ChildNodes[j];
                    if (child != null && child.Name == "defName")
                    {
                        defNameNode = child;
                        break;
                    }
                }
                if (defNameNode == null)
                {
                    continue;
                }
                string name = (defNameNode.InnerText ?? string.Empty).Trim();
                if (ClashingNames.Contains(name))
                {
                    doomed.Add(node);
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

        private static bool IsModActive(string packageId)
        {
            // ⚠ RunningMods 是 IEnumerable<ModContentPack>，只能 foreach（见另一处同类注释）。
            foreach (ModContentPack pack in LoadedModManager.RunningMods)
            {
                if (pack != null && pack.PackageId == packageId)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
