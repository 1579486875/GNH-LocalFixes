using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 让「生物大师的ElToro Patches」与 telanda 的动物遗传模组不再抢同一批 defName；
    // 并在 telanda 缺席时，把 Mask 那批无人消费的数据表安静地摘掉。
    // ==========================================================================
    //
    // 【两个模组的关系】
    //
    // 两个模组各自定义了一整套 RJW_BGS.RaceGeneDef，而且**名字完全相同**：
    //
    //   · telanda 的 RJW Animal Gene Inheritance（`telanda.rjw.animalgeneinheritance`）
    //     自带 39 个（Base 2 / CommonGenes 1 / FallbackGenes 1 / Vanilla_Racegroups 7
    //     / template 17 / VE_Genetics 11）；
    //   · 「生物大师的ElToro Patches」（`Mask.ElToro.Patches`）自带 25 个，
    //     而这 25 个**全部**与上面重名。
    //
    // 【场景 A：两个都在场 —— 只剔重名的，不碰独有的】
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
    // 【场景 B：只有 Mask 在场 —— 整批摘掉（v1.3.29 新增）】
    //
    // 这时 RJW_BGS.RaceGeneDef 这个类型**不存在**：本补丁包自 v1.3.29 起不再提供
    // 兜底定义（原因见 Patch_GenTypes_PreferTelanda 的文件头 —— 提供兜底会让游戏
    // 卡死，2026-10-10 实测）。类型不存在 → 这 25 个节点会逐个触发
    //
    //     Type RJW_BGS.RaceGeneDef is not a Def type or could not be found
    //
    // 而它们在 telanda 缺席时**没有任何代码消费**（1.6 的 Rjw-Genes.dll 完全不引用
    // RJW_BGS：类型名出现 0 次），所以「让它们加载成功」并不能换来任何功能，
    // 只会换来报错。既然如此，正确做法是让它们根本不进文档：整批摘掉 →
    // 不解析 → 不报错，而且与「类型不存在」这个事实完全自洽。
    //
    // 【关于 Mask 自己的 `Common/Patches/DefaultRJWGenesRemoval.xml`】
    //
    // 那是作者为「telanda 在场」准备的，7 条 PatchOperationRemove 用绝对路径
    // `/Defs/RJW_BGS.RaceGeneDef[defName="Canine"]` 且**没有 FindMod 守卫**。
    //
    // 我们**摘不掉它**（2026-10-13 反编译订正，此前注释写错了）：
    //   · `LoadedModManager.LoadModXML` 只调 `ModContentPack.LoadDefs`，
    //     而 LoadDefs 扫的是 `DirectXmlLoader.XmlAssetsInModFolder(this, "Defs/")`
    //     —— **只含 Defs/**；
    //   · `Patches/` 下的文件由另一条路 `LoadPatches()`（同样只扫 "Patches/"）
    //     单独加载，**从不经过 LoadModXML**。
    //   · 所以 DefaultRJWGenesRemoval.xml 永远不在本 Postfix 的 __result 里，
    //     任何「清空它」的尝试都执行不到 —— 本节原先的 ClearDocumentElement
    //     是死代码，已随本次订正删除。
    //
    // 那这 7 条 patch 现在为什么不刷红字？（2026-10-13 实测）
    //   Mask 与 telanda 的第 7 个名字**都拼 `Raccoon`**；拼 `Racoon`（一个 c）的
    //   是 `vegapnk.rjw.genes`（`1.6\Defs\RaceGeneDefs\RaceGeneDefs_Vanilla_Racegroups.xml`）。
    //   也就是说这 7 条命中的是 RJW-Genes 本体的同名节点 —— 全量日志里
    //   0 条 `Patch operation ... failed` 是硬反证。
    //
    // ⚠ 已知限制（不藏）：一旦 RJW-Genes 侧那 7 个节点消失（被注释 / 被别的 patch
    //   先删 / 该模组改版），这 7 条就会全部落空并刷 7 条红字，而本补丁**管不到**
    //   （它加载于 Patches 路径，与本 Postfix 无交集）。届时的正确做法是改用
    //   `Verse.PatchOperation.Complete(string)` 的 Prefix 按 sourceFile 跳过报错，
    //   而不是在这里做 XML 手术。
    //
    // 【为什么不删文件、也不整个移除 asset】
    //
    // 按 defName 精确剔（场景 A）或整体清空文档元素（场景 B），既不依赖文件名，
    // 也不会误伤同一个 asset 里的其它内容。也正因为如此，本补丁**不需要**去改
    // 任何一个模组自己的文件 —— 那些文件在模组更新时会被覆盖，改动留不住。
    //
    // 【安全设计】
    //
    //   1. 两个模组都不在场 → 立即返回，什么都不做。
    //   2. 场景 A 只在「两个同时在场」时动手：Mask 不在时 telanda 就是唯一数据源，
    //      那时剔掉它的 def 等于把功能删了 —— 所以必须双向检查。
    //   3. 全表大小写严格比较（defName 本来就区分大小写）。
    //   4. 整段 try/catch：任何意外都只是「没剔成」，不会让模组加载失败。
    //   5. 只改这两家的 asset（逐个 asset 判 PackageId），绝不触碰别人的 XML。
    [HarmonyPatch(typeof(LoadedModManager), "LoadModXML")]
    public static class Patch_LoadModXML_FilterTelandaGenes
    {
        private const string TelandaPackageId = "telanda.rjw.animalgeneinheritance";

        // ⚠⚠ packageId 一律写小写，比较一律用 OrdinalIgnoreCase。
        //
        // RimWorld 会把 packageId 统一规范化成小写：ModsConfig.xml 里存的是
        // `mask.eltoro.patches`，而不是 About.xml 里写的 `Mask.ElToro.Patches`。
        //
        // 2026-10-10 实测事故：v1.3.28 这里写的是 `Mask.ElToro.Patches`，
        // 于是 `IsModActive` 永远返回 false —— **整套过滤从未真正生效过**
        // （只因当时 telanda 一直关着，才没有暴露出来）。
        // 排查线索是启动日志里一条 `unloaded on purpose` 都没有。
        private const string MaskPackageId = "mask.eltoro.patches";
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
                bool maskOn = IsModActive(MaskPackageId);
                bool telandaOn = IsModActive(TelandaPackageId);
                if (!maskOn && !telandaOn)
                {
                    return;   // 两家都不在，没什么可处理的
                }

                if (telandaOn && maskOn)
                {
                    ResolveClashInsideTelanda(__result);
                }
                else if (maskOn && RaceGeneDefTypeMissing())
                {
                    // ⚠ 2026-10-13 加固：判据从「telanda 模组是否启用」改成
                    //    「RJW_BGS.RaceGeneDef 这个类型现在到底存不存在」。
                    //
                    //    为什么必须换：真正决定这 25 个 def 是「无人消费的垃圾」还是
                    //    「合法数据」的，是**类型在不在**，不是某个模组开没开。
                    //    两者今天恰好等价（1.6 的 Rjw-Genes.dll 不含 RJW_BGS），
                    //    但将来上游一旦把该类型加回任何已启用程序集，
                    //    「telanda 关着」就不再等于「数据没用」——那时旧判据会
                    //    **静默删掉合法 def**，玩家只会看到一条 INFO 日志。
                    //    拿不准时按「类型存在」处理 ⇒ 不删（宁可留红字，也不静默删数据）。
                    SilenceMaskOwnTables(__result);
                }
                // 只剩 telanda 时不动它：它是唯一数据源，剔了就等于把功能删掉。
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Animal-gene clash filter skipped this session (that fix is NOT active): " + ex);
            }
        }

        // ---------------------------------------------------------------- 场景 A
        // telanda + Mask 同时在场：把 telanda 那份**重名的 25 个**摘掉，独有的 14 个原样保留。
        private static void ResolveClashInsideTelanda(List<LoadableXmlAsset> assets)
        {
            int removed = 0;
            for (int i = 0; i < assets.Count; i++)
            {
                LoadableXmlAsset asset = assets[i];
                if (asset == null || asset.mod == null || asset.xmlDoc == null)
                {
                    continue;
                }
                if (asset.mod.PackageId != TelandaPackageId)
                {
                    continue;
                }
                removed += StripNodes(asset.xmlDoc, ClashingNames);
            }

            if (removed > 0)
            {
                Log.Message("[GNH LocalFixes] Animal-gene defName clash resolved: removed " + removed
                    + " duplicate RJW_BGS.RaceGeneDef definition(s) from RJW Animal Gene Inheritance, "
                    + "so that Mask.ElToro.Patches owns all " + ClashingNames.Count
                    + " shared names. Its own-only defs (incl. FallbackGenes) are untouched.");
            }
        }

        // ---------------------------------------------------------------- 场景 B
        // 只有 Mask 在场：这批 RaceGeneDef 无人消费，载入只会报「not a Def type」。
        // 因此把它们整批摘掉，并顺带清空那个会在摘除后反过来报错的自我删除补丁。
        private static void SilenceMaskOwnTables(List<LoadableXmlAsset> assets)
        {
            int removedDefs = 0;

            for (int i = 0; i < assets.Count; i++)
            {
                LoadableXmlAsset asset = assets[i];
                if (asset == null || asset.mod == null || asset.xmlDoc == null)
                {
                    continue;
                }
                if (asset.mod.PackageId != MaskPackageId)
                {
                    continue;
                }

                // ⚠ 2026-10-13：原本这里还有一个
                //   LooksLikeSelfRemovalPatch -> ClearDocumentElement 分支，
                //   想清空 Mask 的 Patches\DefaultRJWGenesRemoval.xml。
                //   已删除：那个文件在 Patches/ 下，
                //   而本 Postfix 的 __result 只含 Defs/ ——
                //   分支从来没有被执行过（日志里
                //   neutralized 恒为 0，四次运行无例外）。
                //   详情见文件头部那段说明。
                removedDefs += StripNodes(asset.xmlDoc, null);   // null = 该文档里的全删
            }

            if (removedDefs > 0)
            {
                Log.Message("[GNH LocalFixes] Left Mask.ElToro.Patches' RJW_BGS.RaceGeneDef tables unloaded on purpose"
                    + " (removed " + removedDefs + " definition(s)). RJW Animal Gene Inheritance is NOT active, so nothing consumes"
                    + " these defs - the 1.6 Rjw-Genes.dll never references RJW_BGS - and loading them only produced"
                    + " \"Type RJW_BGS.RaceGeneDef is not a Def type\" errors. Enable"
                    + " telanda.rjw.animalgeneinheritance to get the animal-gene feature back.");
            }
        }

        // 删掉该文档里的 RJW_BGS.RaceGeneDef 节点：
        //   onlyTheseNames == null → 全删；
        //   否则只删 defName 落在名单里的那些。
        // 返回删除条数。
        private static int StripNodes(XmlDocument doc, HashSet<string> onlyTheseNames)
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

                if (onlyTheseNames == null)
                {
                    doomed.Add(node);
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
                if (onlyTheseNames.Contains(name))
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

        // 这个类型现在能不能被解析出来？判不准时一律返回 false（= 当作「存在」= 不删）。
        //
        // 为什么可以直接问 GenTypes：它正是本补丁包另一项修复
        // （Patch_GenTypes_PreferTelanda）所拦截的那个入口；而那一项对
        // "RJW_BGS.RaceGeneDef" 只在 telanda 在场时才短路，所以这里得到的
        // 就是「全进程真实可解析性」——telanda 不在场时与原版行为一致。
        private static bool RaceGeneDefTypeMissing()
        {
            try
            {
                return GenTypes.GetTypeInAnyAssembly(ElementName) == null;
            }
            catch
            {
                return false;   // 问不出来 ⇒ 保守当作「类型存在」⇒ 不删
            }
        }

        private static bool IsModActive(string packageId)
        {
            // ⚠ RunningMods 是 IEnumerable<ModContentPack>，只能 foreach（见另一处同类注释）。
            // ⚠ packageId 比较必须忽略大小写 —— 见 MaskPackageId 上面那段说明。
            foreach (ModContentPack pack in LoadedModManager.RunningMods)
            {
                if (pack != null && string.Equals(pack.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
