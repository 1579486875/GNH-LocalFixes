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
    // 而 VFE Empire 自己的**九个**爵位全都用到了它
    //（Defs\RoyalTitles\RoyalTitles_Empire.xml 的第 263 / 556 / 842 / 1159 / 1478 /
    //  1819 / 2186 / 2553 / 2946 行，依次属于 Archcount、Marquess ×2、Archduke ×2、
    //  Magister、Despot ×2、HighStellarch）。
    // 【2026-10-06 审计更正】此处原先写「两个爵位…第 263 行和第 556 行」——
    //  那两个行号本身没错，但只数了两处，实际是九处。
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

        // ── 「作者修好后自动让路」的实现 ──────────────────────────────────
        //
        // 探测结果缓存：null = 还没探测过。
        // 只探测一次 —— 调用方在 VFEE 的静态构造期间会对**每一个 def** 各问一次，
        // 不缓存就等于把同一段 IL 反复读几万遍。
        private static bool? originalIsSafe;

        /// <summary>
        /// 用户在「模组设置」里的开关。
        ///
        /// 每次都现读、不缓存：设置对象是在模组构造函数里建起来的，
        /// 而静态构造可能比它更早，缓存会读到 null。
        /// 读不到设置对象时按「开启」处理 —— 宁可多修，也不要因为设置
        /// 还没就绪就把保护悄悄撤掉。
        /// </summary>
        private static bool FeatureEnabled
        {
            get
            {
                GNHLocalFixesSettings settings = LocalFixesMod.Settings;
                return (settings == null) || settings.fixVfeeInstrumentSpace;
            }
        }

        /// <summary>
        /// 探测「VFE Empire 那句判断现在还会不会崩」。
        ///
        /// 【早期版本为什么永远判「不安全」（2026-10-12 修）】
        ///   旧实现扫的是**数据**：遍历全部 ThingDef，只要有一个 placeWorkers 为 null
        ///   就判「原版仍不安全」。而 placeWorkers 为 null 是绝大多数 ThingDef 的
        ///   **正常状态** —— 实测原版 Core 的 997 个 ThingDef 里只有 85 处声明了
        ///   placeWorkers —— 于是这个探针**必然**在第一个 def 上就下结论，
        ///   「自动让路」一次也不会发生。它测错了对象：要回答的是**代码**会不会崩，
        ///   而不是**数据**里有没有 null。
        ///
        /// 【现在的判据】
        ///   直接读 VFE Empire 那个判断方法（编译器生成的 predicate lambda）自己的 IL，
        ///   看「读 placeWorkers」与「第一次拿它调成员」之间有没有条件分支（判空短路）。
        ///   判据的实现与能力边界见 InstrumentSpaceProbe.cs。
        ///
        /// 【保守原则（最重要的一条）】
        ///   只要探测本身有任何不确定 —— 找不到宿主类型、找不到那个方法、IL 读不出来、
        ///   判据抛异常 —— 一律当作「原版不安全」，继续由我们保护。
        ///   **撤掉保护比多保护一段危险得多。**
        ///
        /// 结论照常缓存，理由见上面 originalIsSafe 的说明。
        /// </summary>
        private static bool OriginalPredicateIsSafe()
        {
            if (originalIsSafe.HasValue)
            {
                return originalIsSafe.Value;
            }

            bool safe;
            bool probeUnavailable;
            string verdict;

            try
            {
                MethodInfo lambda = FindPredicate();
                if (lambda == null)
                {
                    // 宿主类型在、但那个判断方法找不到（多半是作者改了写法，或编译器换了名字）。
                    // 探测拿不到对象 → 保守：继续保护。
                    safe = false;
                    probeUnavailable = true;
                    verdict = "probe unavailable (predicate method not found)";
                }
                else if (InstrumentSpaceProbe.HasNullGuardBeforeFirstUse(lambda))
                {
                    // 确凿看到判空分支 → 作者已经修好 → 让路。
                    safe = true;
                    probeUnavailable = false;
                    verdict = "the vanilla predicate is already null-safe";
                }
                else
                {
                    // 确凿看不到判空分支 → 作者还没修 → 继续保护。
                    safe = false;
                    probeUnavailable = false;
                    verdict = "the vanilla predicate is still not null-safe";
                }
            }
            catch (Exception ex)
            {
                // 探测自己出问题（IL 解析失败、方法没有方法体……）绝不能撤掉保护。
                safe = false;
                probeUnavailable = true;
                verdict = "probe unavailable (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            originalIsSafe = safe;

            // 三种结论各打一条不同的日志，排查时一眼能分清「作者修了」「作者没修」
            // 和「我们自己的探针没跑起来」。
            string tail;
            if (safe)
            {
                tail = " — VFE Empire appears to have fixed it, so this fix stands down and does nothing.";
            }
            else if (probeUnavailable)
            {
                tail = " — probe could not answer, so this fix conservatively stays active.";
            }
            else
            {
                tail = " — so this fix stays active.";
            }
            LocalFixesMod.SafeLogMessage("[GNH LocalFixes] InstrumentSpace probe: " + verdict + tail);

            return safe;
        }

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
            // 兜底做法：在那个内部类里找「任何一个接受 ThingDef、返回 bool 的方法」。
            //
            // ⚠ 这里**不能**再要求 IsStatic（2026-10-11 修正，第 5 项审计发现）：
            //   C# 编译器为「静态构造函数里的 lambda」生成的方法，其实挂着
            //   **实例**签名 —— 真实签名是
            //       internal bool <.cctor>b__1_0(ThingDef)
            //   它被放在编译器生成的闭包类 VFEEmpire.RoomRequirement_InstrumentSpace+<>c 上，
            //   靠该类的单例实例来调用。所以加 IsStatic 会把唯一的目标筛掉，
            //   这条兜底路径等于「永远找不到方法」—— 一旦作者改了那个自动生成的方法名，
            //   精确匹配失败之后就再也没有第二条路，修复会静默失效。
            //   （精确匹配那条路本来就没查 IsStatic，所以过去看起来是好的。）
            //
            // 同一个方法也是「自动让路」探针的入口：探针读它的 IL 判断作者修没修。
            foreach (MethodInfo candidate in host.GetMethods(AccessTools.all))
            {
                if (candidate.ReturnType != typeof(bool))
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
        //
        // 【两种情况下我们什么都不做、把原版放回去】
        //   1. 用户在模组设置里关掉了这一项；
        //   2. 探测发现原版那句判断**已经不会崩**了
        //      —— 多半是 VFE Empire 自己修好了（判据见 InstrumentSpaceProbe.cs）。
        //   第 2 条就是「作者修好后本补丁自动失效」，不必等本补丁包更新。
        private static bool PredicatePrefix(ThingDef def, ref bool __result)
        {
            if (!FeatureEnabled || OriginalPredicateIsSafe())
            {
                return true;   // 交还原版
            }

            __result = HasDanceFloorWorker(def);
            return false;
        }

        // 保底方案：跳过那个会崩的初始化，由我们自己把列表填好。
        //
        // 与方案一同样受「用户开关」和「原版是否已安全」这两道约束：
        // 任一条件成立就让原版自己跑。否则作者把初始化修好之后，
        // 这条保底路径反而会把作者修好的那套初始化整个抢掉。
        private static bool InitializerPrefix()
        {
            if (!FeatureEnabled || OriginalPredicateIsSafe())
            {
                return true;   // 交还原版
            }

            Type outer = AccessTools.TypeByName(OuterTypeName);
            FieldInfo field = (outer != null) ? AccessTools.Field(outer, InstrumentsFieldName) : null;
            if (field == null)
            {
                // 找不到那个列表字段（多半是 VFE Empire 改了字段名）。
                //
                // 【为什么这里必须 return true，而不是继续 return false】
                //   return false 的含义是「拦住原版的初始化，改由我们自己填列表」。
                //   可我们现在**没有**那个字段，也就**填不了**。
                //   如果仍然 return false 跑下去，结果会是：
                //       原版初始化被跳过 → Instruments 一直是 null
                //       → 而 Met() 里直接拿它去查有没有包含某个 def
                //       → 每一次房间判定都抛空引用异常。
                //   那就从「原版本来会怎样」变成了「比原版更糟」。
                //   所以这里交还给原版：最坏也只是回到原版自己的行为。
                LocalFixesMod.SafeLogWarning(
                    "[GNH LocalFixes] RoomRequirement_InstrumentSpace.Instruments field not found;"
                    + " handing the initializer back to the original mod.");
                return true;
            }

            try
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
                LocalFixesMod.SafeLogMessage("[GNH LocalFixes] Rebuilt RoomRequirement_InstrumentSpace.Instruments (count=" + instruments.Count + ").");
            }
            catch (Exception ex)
            {
                // 重建失败（字段是实例字段、或列表被别处锁住等）：同上，
                // 交还原版也比留一个 null 列表给别人用要安全。
                LocalFixesMod.SafeLogWarning("[GNH LocalFixes] Error while rebuilding the InstrumentSpace list,"
                    + " handing the initializer back to the original mod: " + ex.Message);
                return true;
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
