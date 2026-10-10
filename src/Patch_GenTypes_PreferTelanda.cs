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
    //   · telanda 不在 → 用我们自己的兜底定义。
    //
    // 【为什么安全】
    //
    //   · 只比较字符串，非目标类型直接 return true 放行，逻辑等价于没装本补丁；
    //   · 这是热路径（解析 XML 时每个类型名都会问一次），所以判断写成
    //     两次字符串相等比较，没有反射、没有字典、没有分配；
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
            bool isRaceGene = typeName == RaceGeneDefName;
            if (!isRaceGene && typeName != GeneEntryName)
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
                // telanda 不在场，交给我们自己的定义兜底。
                __result = isRaceGene
                    ? typeof(RJW_BGS.RaceGeneDef)
                    : typeof(RJW_BGS.BestialityGeneInheritanceDef);
                return false;
            }
            catch (Exception)
            {
                // 出任何意外都退回原逻辑，绝不因为本补丁让类型解析整个失败。
                return true;
            }
        }

        // 在 telanda 模组的程序集里按名取类型。没启用该模组时返回 null。
        private static Type FindInTelanda(string typeName)
        {
            // ⚠ LoadedModManager.RunningMods 的静态类型是 IEnumerable<ModContentPack>，
            //   不是 List —— 不能用 .Count 或索引器，只能 foreach（2026-10-10 编译报错后更正）。
            foreach (ModContentPack pack in LoadedModManager.RunningMods)
            {
                if (pack == null || pack.PackageId != TelandaPackageId)
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
