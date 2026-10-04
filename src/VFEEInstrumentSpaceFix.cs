using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：VFE Empire 的「乐器空间」房间需求一加载就崩。
    // （涉及的 VFE Empire 1.6 程序集 MVID：7b87b4b5f2e8419a9b66f287653fbf84）
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // VFE Empire 有一段初始化代码，还原成 C# 大概是这样：
    //
    //     把所有 ThingDef 逐个过一遍，
    //     只看那些「有 placeWorkers、并且里面包含跳舞地板」的，
    //     收进 Instruments 这个列表里。
    //
    // 问题出在 placeWorkers 这个字段上：
    //   如果一个 Def 从来没声明过 placeWorkers，它是「空」而不是「空列表」。
    //   对着一个空值去问「你有没有包含某某」，就会抛空引用异常。
    //
    // 而这个循环会遍历所有 ThingDef，所以只要遇到第一个没声明 placeWorkers 的，
    // 整个初始化就崩了。
    //
    // 更麻烦的是：一个类型的初始化一旦失败，.NET 运行时会把它永久标记为
    // 「初始化失败」，之后再想用它会直接报错。
    //
    // 最终结果：「乐器空间」这个房间需求用不了，
    // 而 VFE Empire 自己的两个爵位正好都用到了它
    //（Defs\RoyalTitles\RoyalTitles_Empire.xml 的第 263 行和第 556 行）。
    //
    // 【我们的做法（按「尽量保留原作者逻辑」的优先级排序）】
    //
    //   方案一（首选）：只把那句不安全的判断换掉。
    //       作者自己的初始化流程照常跑，我们只是让它不再崩。
    //
    //   方案二（保底）：万一句判断找不到（比如将来编译方式变了），
    //       就把整个初始化接管过来，自己把列表填好。
    //
    // 【安装时机】
    //
    // 在 LocalFixesMod 的构造函数里安装。那个时刻 Def 还没解析，
    // 所有静态构造函数也都还没跑，来得及。
    //
    // 因为首次构造抛异常时，下次模组对象还会被构造一次（原因见 LocalFixesMod.cs），
    // 所以 installed 标记只在「补丁真的装上了」或者「确认不需要装」之后才立起：
    //   * 主动跳过（例如根本没装 VFE Empire）算成功，否则会白重试无数次；
    //   * 抛异常则保持未立起，下次构造时再试一遍。
    internal static class VFEEInstrumentSpaceFix
    {
        private const string OuterTypeName = "VFEEmpire.RoomRequirement_InstrumentSpace";
        private const string LambdaHostTypeName = "VFEEmpire.RoomRequirement_InstrumentSpace+<>c";
        private const string LambdaMethodName = "<.cctor>b__1_0";
        private const string DanceFloorWorkerName = "Placeworker_DanceFloorArea";
        private const string InstrumentsFieldName = "Instruments";

        private static bool installed;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type outer = AccessTools.TypeByName(OuterTypeName);
                if (outer == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] VFE Empire not detected; RoomRequirement_InstrumentSpace fix skipped.");
                    return;
                }

                MethodInfo predicate = FindPredicate();
                if (predicate != null)
                {
                    LocalFixesMod.HarmonyInstance.Patch(
                        predicate,
                        new HarmonyMethod(AccessTools.Method(typeof(VFEEInstrumentSpaceFix), nameof(PredicatePrefix))),
                        null, null, null);
                    installed = true;
                    Log.Message("[GNH LocalFixes] Patched VFEEmpire RoomRequirement_InstrumentSpace predicate (null-safe placeWorkers).");
                    return;
                }

                // 取静态构造函数必须走 Type.TypeInitializer，**不能**写成
                // AccessTools.Method(outer, ".cctor")：反射里静态构造函数是
                // ConstructorInfo，而 MethodInfo 是它的兄弟类型（两者同继承 MethodBase），
                // GetMethod 永远找不到它、只会返回 null。
                // 之前正是这么写的，结果这条「保底方案」从一开始就没装上过
                //（实机日志里也只有方案一那条记录）。Harmony.Patch 接受 MethodBase，
                // 所以 ConstructorInfo 可以直接用。
                ConstructorInfo initializer = outer.TypeInitializer;
                if (initializer == null)
                {
                    // 找不到就**不置位**：留着下次再试，而不是永久放弃。
                    Log.Error("[GNH LocalFixes] Could not locate the RoomRequirement_InstrumentSpace initializer; fix deferred (will retry).");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    initializer,
                    new HarmonyMethod(AccessTools.Method(typeof(VFEEInstrumentSpaceFix), nameof(InitializerPrefix))),
                    null, null, null);
                installed = true;
                Log.Message("[GNH LocalFixes] Patched VFEEmpire RoomRequirement_InstrumentSpace initializer (predicate not found).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the RoomRequirement_InstrumentSpace fix (will retry): " + ex);
            }
        }

        // 这里要提醒一个很容易踩的坑：
        //
        // AccessTools.Method 的最后那个参数，是「泛型类型参数」，
        // 而不是「方法的返回类型」。
        //
        // 如果给一个普通方法传了东西（而不是 null），它会抛
        // InvalidOperationException（大意是「这不是一个泛型方法定义」）。
        // 而如果外面套了 try/catch，这个异常会被悄悄吃掉 ——
        // 补丁看起来装了，其实根本没装，问题依然在。
        //
        // 所以我们的做法是：先用「参数列表」把方法找出来，
        // 返回类型由我们自己判断。
        private static MethodInfo FindPredicate()
        {
            Type host = AccessTools.TypeByName(LambdaHostTypeName);
            if (host == null)
            {
                return null;
            }

            MethodInfo exact = AccessTools.Method(host, LambdaMethodName, new[] { typeof(ThingDef) }, null);
            if (exact != null && exact.ReturnType == typeof(bool))
            {
                return exact;
            }

            // 上面那个方法名是编译器自动生成的，将来可能变。
            // 兜底做法：在那个内部类里找「任何一个接受 ThingDef、返回 bool 的静态方法」。
            foreach (MethodInfo candidate in host.GetMethods(AccessTools.all))
            {
                if (!candidate.IsStatic || candidate.ReturnType != typeof(bool))
                {
                    continue;
                }
                ParameterInfo[] ps = candidate.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(ThingDef))
                {
                    return candidate;
                }
            }
            return null;
        }

        // 这个方法顶替掉原来那句判断：
        //     原来：直接查 placeWorkers 里有没有包含跳舞地板（碰到空值会崩）
        //     现在：先看是不是空，是空就直接返回「不是」，否则再正常查
        private static bool PredicatePrefix(ThingDef def, ref bool __result)
        {
            __result = HasDanceFloorWorker(def);
            return false;
        }

        // 保底方案：跳过那个会崩的初始化，由我们自己把列表填好。
        private static bool InitializerPrefix()
        {
            try
            {
                Type outer = AccessTools.TypeByName(OuterTypeName);
                FieldInfo field = (outer != null) ? AccessTools.Field(outer, InstrumentsFieldName) : null;
                if (field != null)
                {
                    List<ThingDef> instruments = new List<ThingDef>();
                    foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
                    {
                        if (HasDanceFloorWorker(def))
                        {
                            instruments.Add(def);
                        }
                    }
                    field.SetValue(null, instruments);
                    Log.Message("[GNH LocalFixes] Rebuilt RoomRequirement_InstrumentSpace.Instruments (count=" + instruments.Count + ").");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Error while rebuilding the InstrumentSpace list: " + ex.Message);
            }
            return false;
        }

        // 这里是用「类型名字」来比对的，而不是直接引用类型。
        // 好处是本程序集编译时不需要依赖 VFE Empire —— 它没装也能编过。
        private static bool HasDanceFloorWorker(ThingDef def)
        {
            if (def == null || def.placeWorkers == null)
            {
                return false;
            }
            for (int i = 0; i < def.placeWorkers.Count; i++)
            {
                Type worker = def.placeWorkers[i];
                if (worker != null && worker.Name == DanceFloorWorkerName)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
