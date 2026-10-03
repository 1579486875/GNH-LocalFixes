using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：一打开开发者面板，日志就被刷屏（每次约 1241 条报错）。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // Vehicle Framework 在生成调试菜单时，把每一项的名称存成了
    // 「显示名{真正的defName}」这种拼接格式（大括号里才是真名字）。
    //
    // 然后它把这个「整串」拿去问游戏：「有没有叫这个名字的东西？」
    // 而且用的是「找不到就报错」的版本。结果 1241 个节点，报 1241 次错。
    //
    // 【我们怎么修】
    //
    // 在它执行之前插一段代码：
    //   1. 把大括号里的真正名字抠出来；
    //   2. 把车辆相关的项从菜单里删掉；
    //   3. 剩下的正常处理。
    //
    // 这里要说明白：这个补丁不只是「消掉红字」，它同时**修好了一个功能缺陷**。
    //
    // 原方法用 DefDatabase<PawnKindDef>.GetNamed(label) 去找这个节点（errorOnFail 为 true），
    // 而它手里的 label 是「显示名{defName}」这种带花括号的串 —— 这样查必然失败、
    // 拿到 null，于是后面那句 null?.race is VehicleDef 永远为假，**一个车辆节点都删不掉**。
    // 「隐藏车辆」这个功能本来就是坏的。
    //
    // 我们先把花括号里的真名抠出来、再改用 GetNamedSilentFail 静默查询，
    // 于是要删的节点真的被删掉了。也就是说：不再刷屏 + 功能恢复，两者都是这个补丁带来的。
    //
    // 【两点说明】
    //
    //   * 整条安装路径都做了保护：如果没装 Vehicle Framework，
    //     或者它将来改了方法名、改了字段名，我们就只记一条日志然后什么都不做，
    //     不会因此引发新的错误。
    //
    //   * 如果将来 Vehicle Framework 自己修好了这个问题，我们这个补丁
    //     （它是「跳过原方法」型的）会顶替掉它的正常逻辑。
    //     到那时请把本文件删掉。
    [StaticConstructorOnStartup]
    public static class VehicleFrameworkDebugFix
    {
        private const string VehicleDefTypeName = "Vehicles.VehicleDef";
        private const string PatchDebugTypeName = "Vehicles.Patch_Debug";
        private const string TargetMethodName = "DebugHideVehiclesFromPawnSpawner";

        static VehicleFrameworkDebugFix()
        {
            try
            {
                Type type = AccessTools.TypeByName(PatchDebugTypeName);
                if (type == null)
                {
                    Log.Message("[GNH LocalFixes] Vehicle Framework not detected; its debug patch fix was skipped.");
                    return;
                }
                MethodInfo target = AccessTools.Method(type, TargetMethodName, null, null);
                if (target == null)
                {
                    Log.Warning("[GNH LocalFixes] Could not find Vehicles.Patch_Debug." + TargetMethodName + " (framework version changed?); fix skipped.");
                    return;
                }
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(VehicleFrameworkDebugFix), "Prefix", null, null)),
                    null, null, null);
                Log.Message("[GNH LocalFixes] Patched Vehicle Framework " + TargetMethodName + " (developer panel log spam fixed).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the Vehicle Framework debug fix: " + ex);
            }
        }

        // 注意：下面这个参数名 "__0" 不能改。
        // Harmony 是按「第几个参数」来注入的，改成别的名字就注入不进去了。
        public static bool Prefix(List<DebugActionNode> __0)
        {
            try
            {
                if (__0 == null)
                {
                    return false;
                }
                for (int i = __0.Count - 1; i >= 0; i--)
                {
                    DebugActionNode node = __0[i];
                    if (node != null && IsVehicleRace(ResolvePawnKind(node.label)))
                    {
                        __0.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Error while filtering vehicle pawn kinds (skipped this pass): " + ex.Message);
            }
            return false;
        }

        // 菜单项的文本长这样：  显示名称{真正的defName}
        // 我们优先使用大括号里的真名字。
        // 如果这一项根本没有大括号（说明它不是车辆相关的项），
        // 就按原文处理，行为跟以前完全一样。
        private static PawnKindDef ResolvePawnKind(string label)
        {
            if (GenText.NullOrEmpty(label))
            {
                return null;
            }
            int close = label.LastIndexOf('}');
            if (close > 0)
            {
                int open = label.LastIndexOf('{', close);
                if (open >= 0 && open < close)
                {
                    PawnKindDef byDefName = DefDatabase<PawnKindDef>.GetNamedSilentFail(label.Substring(open + 1, close - open - 1));
                    if (byDefName != null)
                    {
                        return byDefName;
                    }
                }
            }
            return DefDatabase<PawnKindDef>.GetNamedSilentFail(label);
        }

        // 判断「这是不是车辆」的办法：沿着它的类型继承链一层层往上找，
        // 看有没有一个祖先叫 Vehicles.VehicleDef。
        // 用名字比对而不是直接引用类型，这样编译时不需要依赖 Vehicle Framework。
        private static bool IsVehicleRace(PawnKindDef kind)
        {
            if (kind == null || kind.race == null)
            {
                return false;
            }
            Type type = kind.race.GetType();
            while (type != null)
            {
                if (type.FullName == VehicleDefTypeName)
                {
                    return true;
                }
                type = type.BaseType;
            }
            return false;
        }
    }
}
