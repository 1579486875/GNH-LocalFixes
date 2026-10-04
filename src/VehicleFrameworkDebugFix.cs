using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁要顶替的是 Vehicle Framework 的调试菜单补丁：
    // Vehicles.Patch_Debug.DebugHideVehiclesFromPawnSpawner
    //（它是挂在 Verse.DebugToolsSpawning.SpawnPawn 上的 postfix）。
    // 我们把它那句「查不到就报错」换成「查不到就静默跳过」。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // Vehicle Framework 在生成调试菜单之后做了一次后置处理，逻辑等价于：
    //
    //     foreach (列表里的每个节点 node)
    //         if (DefDatabase<PawnKindDef>.GetNamed(node.label, true)?.race is VehicleDef)
    //             删掉这个节点；
    //
    // 关键在 GetNamed 的第二个参数是 true —— 「找不到就把错误写进日志」。
    // 所以只要某个节点的 label 不是现存 PawnKindDef 的 defName，
    // 打开一次开发者面板就会按节点数量刷出成片的红字。
    //
    // 【我们怎么修】
    //
    // 在它执行之前插一段代码：
    //   1. 先按原版方式，把 label 当 defName 查；
    //   2. 查不到再从花括号里解析（防御将来 label 变成「显示名{defName}」这类格式）；
    //   3. 仍然查不到就静默跳过 —— 不再把失败写进日志；
    //   4. 判定为车辆的节点照旧从菜单里删掉。
    //
    // 也就是说：本补丁真正的价值是**兜住「找不到就报错」这条刷屏路径** ——
    // 先按原版方式查，查不到再尝试从花括号里解析（防御未来格式变化），
    // 最后静默失败而不是报错。功能与原方法一致。
    //
    // 【核实记录（2026-10-03，反编译）】
    //
    // 旧版注释写过「label 是『显示名{defName}』拼接格式」「原方法必然失败、
    // 一个节点都删不掉」「一次报 1241 条错」—— 这三条都与事实不符，已删除。实际的核实结果是：
    //   · Verse.DebugToolsSpawning.SpawnPawn() 传的是 localKindDef.defName（纯 defName）；
    //   · LudeonTK.DebugActionNode 的构造函数只做 this.label = label，全类没有花括号拼接；
    //   · Vehicles.dll（工坊 3014915404 / 1.6）里检索不到任何含「{」的字符串字面量。
    // 所以在那几个版本上，这条报错路径并没有被触发，原方法本来也能正常工作
    //（label 是 defName 时 GetNamed 必然成功，车辆节点的 race 确实是 VehicleDef，会被删掉）。
    // 保留下面的花括号解析纯粹是防御：万一将来某个版本、或别的模组把 label 换成
    // 非 defName 的格式，这里只会静默跳过，而不是每个节点写一条错误。
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
        // Harmony 是按「第几个参数」来注入的（__0 = 目标方法的第 0 个参数），
        // 改成别的名字就注入不进去了。
        // 尤其不能写成 "__result"：那是 Harmony「原方法返回值」的专用名，
        // 而目标方法 DebugHideVehiclesFromPawnSpawner 返回 void。
        // Harmony 2.4.1 在生成补丁时对这种情况会直接抛
        //「Cannot get result from void method ...」，补丁根本装不上。
        public static bool Prefix(List<DebugActionNode> __0)
        {
            try
            {
                if (__0 == null)
                {
                    // 拿不到目标列表（多半是原版改了 SpawnPawn 的调用形式）时**必须放行原方法**。
                    //
                    // 这里原来写的是 return false —— 那是「永久跳过原方法」，后果是
                    // 「隐藏载具」这个功能悄悄消失，而且日志里一个字都没有。
                    // 宁可让原方法自己去跑、去报它自己的错，也不要静默丢功能。
                    return true;
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

        // 正常情况下，node.label 就是 PawnKindDef 的 defName。
        // 这里仍然保留「先从大括号里取」的写法，只是防御将来 label 变成
        //「显示名{defName}」这类拼接格式：有花括号就优先用里面的真名，
        // 没有就按原文（defName）处理。
        // 两条路都查不到时静默返回 null，不会写日志 ——
        // 这一点正是与原来那句 GetNamed(label, true) 的区别，也是本补丁的兜底价值所在。
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
