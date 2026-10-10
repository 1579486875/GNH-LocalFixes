using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 保证「RJW_BGS.RaceGeneDef」这个类型名解析到 telanda 的那一份。
    // ==========================================================================
    //
    // 【问题】
    //
    // 本补丁包为了在 telanda 缺席时不刷红字，也定义了一份
    // RJW_BGS.RaceGeneDef / BestialityGeneInheritanceDef（见 RaceGeneDefCompat.cs）。
    // 于是当 telanda **在场**时，同一个类型名会同时存在于两个程序集里。
    //
    // RimWorld 的 XML 解析走 Verse.GenTypes.GetTypeInAnyAssembly，它的实现是
    // （已反编译核对）：
    //
    //     Type type = typeof(GenTypes).Assembly.GetType(typeName, ...);   // 游戏本体
    //     if (type != null) return type;
    //     foreach (Assembly a in AllActiveAssemblies)                     // ← 按模组加载顺序
    //     {
    //         Type t = a.GetType(typeName, ...);
    //         if (t != null) return t;                                    // 第一个命中就返回
    //     }
    //
    // 而 AllActiveAssemblies 的顺序来自 LoadedModManager.RunningMods，
    // 也就是 ModsConfig 里的模组排列顺序 —— **纯看谁排在前面**。
    //
    // 这很危险：DefDatabase<T> 是按**具体类型**索引的。假如解析到了我们这份，
    // 那么 XML 实例会进 DefDatabase<我们的 RaceGeneDef>，而 telanda 的代码查的是
    // DefDatabase<它的 RaceGeneDef> —— 同一个名字、两个不同的类型，结果是空表，
    // telanda 的基因继承会静默失效。**我们本来想帮忙，反而把功能搞坏。**
    //
    // 【修法】
    //
    // 在解析入口拦一道，只针对这两个类型名：
    //   · telanda 在场 → 一律用它的类型（无论两者谁排在前面）；
    //   · telanda 不在 → **不兜底**，交还原版逻辑（类型找不到，该节点被安全跳过）。
    //
    // 【为什么不再提供兜底定义（v1.3.29 撤除，实测事故）】
    //
    // v1.3.28 曾在 telanda 缺席时把这两个类型名指向本补丁包自带的同名定义
    //（src\RaceGeneDefCompat.cs），想借此消掉「not a Def type」红字。
    // **2026-10-10 首次实测即导致游戏卡死**：日志精确停在第一个
    // RJW_BGS.RaceGeneDef 节点（Mask 的 Muffalo）本该报错的那一行，此后
    // 24 分钟零输出、内存却从 2.1 GB 涨到 4.3 GB，直到用户重启电脑。
    // 对照 v1.3.27 的完整日志（同一位置继续正常推进到 14745 行），
    // 两轮唯一的行为差异就是「这个类型从解析不了变成了解析得了」。
    //
    // 注意：**只把 Prefix 改成 return true 是不够的** ——
    // GenTypes.GetTypeInAnyAssembly 的原版实现会遍历所有已加载程序集，
    // 只要本 DLL 里存在这个类型，它照样会被找到并解析成功。
    // 所以 v1.3.29 同时**删掉了那份兜底定义**，让类型真的不存在。
    //
    // 那批数据在 telanda 缺席时本来就无人消费（1.6 的 Rjw-Genes.dll 完全不引用
    // RJW_BGS：类型名出现 0 次），加载成功不会带来任何功能，只会带来解析开销与
    // 这次卡死。真正该做的是让它们安静地不加载 —— 由
    // Patch_LoadModXML_FilterTelandaGenes 在 XML 阶段整批摘除。
    //
    // 【为什么安全】
    //
    //   · 只比较字符串，非目标类型直接 return true 放行，逻辑等价于没装本补丁；
    //   · 这是热路径（解析 XML 时每个类型名都会问一次），所以判断写成
    //     直接字符串比较，没有反射、没有字典、没有分配；
    //   · 全程 try/catch：万一 RunningMods 还没就绪，退回原逻辑（Harmony 原方法照跑）。
    [HarmonyPatch(typeof(GenTypes), "GetTypeInAnyAssembly")]
    public static class Patch_GenTypes_PreferTelanda
    {
        private const string RaceGeneDefName = "RJW_BGS.RaceGeneDef";
        private const string GeneEntryName = "RJW_BGS.BestialityGeneInheritanceDef";
        private const string TelandaPackageId = "telanda.rjw.animalgeneinheritance";

        [HarmonyPrefix]
        public static bool Prefix(string typeName, ref Type __result)
        {
            if (typeName == null)
            {
                return true;
            }
            // 先用最快的判断挡掉绝大多数调用：只有这两个名字才需要特殊处理。
            if (typeName != RaceGeneDefName && typeName != GeneEntryName)
            {
                return true;
            }

            try
            {
                Type fromTelanda = FindInTelanda(typeName);
                if (fromTelanda != null)
                {
                    __result = fromTelanda;
                    return false;
                }
            }
            catch (Exception)
            {
                // 出任何意外都退回原逻辑，绝不因为本补丁让类型解析整个失败。
                return true;
            }

            // telanda 不在场：**故意不兜底**，交还原版逻辑。
            //
            // 此时这两个类型名在整个进程里都不存在（本补丁包自 v1.3.29 起
            // 已删除自己的兜底定义），所以引用它们的 XML 节点会被原版安全跳过，
            // 不可能进入解析。为什么不兜底 —— 见文件头那一节。
            return true;
        }

        // 在 telanda 模组的程序集里按名取类型。没启用该模组时返回 null。
        private static Type FindInTelanda(string typeName)
        {
            // ⚠ LoadedModManager.RunningMods 的静态类型是 IEnumerable<ModContentPack>，
            //   不是 List —— 不能用 .Count 或索引器，只能 foreach（2026-10-10 编译报错后更正）。
            foreach (ModContentPack pack in LoadedModManager.RunningMods)
            {
                // packageId 比较一律忽略大小写：RimWorld 会把它规范化成小写，
                // 用 Ordinal 比较就可能静默失效（同类事故见
                // Patch_LoadModXML_FilterTelandaGenes 里 MaskPackageId 的注释）。
                if (pack == null || !string.Equals(pack.PackageId, TelandaPackageId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var assemblies = pack.assemblies;
                if (assemblies == null || assemblies.loadedAssemblies == null)
                {
                    return null;
                }
                for (int j = 0; j < assemblies.loadedAssemblies.Count; j++)
                {
                    Assembly asm = assemblies.loadedAssemblies[j];
                    if (asm == null)
                    {
                        continue;
                    }
                    Type t = asm.GetType(typeName, throwOnError: false);
                    if (t != null)
                    {
                        return t;
                    }
                }
                return null;
            }
            return null;
        }
    }
}
