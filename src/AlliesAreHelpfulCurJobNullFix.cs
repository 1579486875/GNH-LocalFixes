using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：日志里反复出现的
    //
    //     Exception ticking 努斯丰斯 (at (234, 0, 246)):
    //         System.NullReferenceException: Object reference not set to an instance of an object
    //       at PawnTendAndRescuePatch.Postfix (Verse.Pawn __instance) [0x0036f]
    //         - POSTFIX alt4s.alliesarehelpful:
    //             Void PawnTendAndRescuePatch:Postfix(Pawn __instance)
    //       at Verse.Pawn.TickRare ()
    //
    // 这是模组「Allies are Helpful」的一个空引用缺陷。
    //
    // ──────────────────────────────────────────────────────────────────────────
    // 【一、出问题的是哪个模组】
    // ──────────────────────────────────────────────────────────────────────────
    //
    // 堆栈里那个 MVID（<97eb312989394681a27d3a0963e40694>）对应的是
    //
    //     D:\steam\steamapps\workshop\content\294100\3534920369\Assemblies\AlliesAreHelpful.dll
    //
    // （模组：Allies are Helpful，packageId = ninagoblin.alliesarehelpful，工坊 3534920369）
    //
    // 也就是说：异常**不是** RimWorld 原版的，也**不是**本补丁包的，
    // 而是这个「帮盟友搭把手」的模组自己写错的。
    //
    // ──────────────────────────────────────────────────────────────────────────
    // 【二、真正的原因：curJob 为 null，却被当成一定有值】
    // ──────────────────────────────────────────────────────────────────────────
    //
    // 该模组把 PawnTendAndRescuePatch.Postfix 挂在 Verse.Pawn.TickRare 上
    //（每个角色每 250 tick 跑一次）。Postfix 里是这样开头的（反编译逐字核对）：
    //
    //     Job val = __instance.jobs?.curJob;                       // ← 注意那个 ?.
    //     if (val?.def != null && val.def != JobDefOf.Wait_Wander
    //                          && val.def != JobDefOf.Follow)
    //     {
    //         return;                                              // 正在干别的活，不打扰
    //     }
    //     ...
    //     foreach (Pawn item in cachedTendTargets)
    //     {
    //         if (item != null && ... && val.def != JobDefOf.TendPatient)   // ← 💥 就是这里
    //         { ... }
    //     }
    //
    // 问题出在两处「不一致」：
    //
    //   · 第 1 行用了 `?.`，说明作者已经想到「curJob 可能是 null」；
    //   · 但紧接着的 if 写的是 `val?.def != null`。当 val 是 null 时，
    //     这个表达式求值为 `null != null` 也就是 **false** ——
    //     于是这个「拦截」条件整体为 false，**代码不返回，继续往下走**；
    //   · 再往下，第 130 行（以及救援分支的第 146 行）直接写 `val.def`，
    //     没有任何保护 —— 这时 val 还是 null，**空引用异常**。
    //
    // 换句话说：作者的本意是「角色正忙就别打扰它」，但当角色**完全没有工作**
    //（curJob == null，这在游戏里很常见）时，这段判断反而放行了。
    //
    // 为什么日志里一次开局只出现几次、而不是每 250 tick 都炸？
    // 因为要继续往下走，还得同时满足一串条件：缓存里有需要照顾/救援的人、
    // 那个人能被预约、自己没有处在危险中、年龄大于 2 岁……
    // 全部凑齐才会走到 `val.def` 那一行。
    //
    // ──────────────────────────────────────────────────────────────────────────
    // 【三、⚠ 一段被推翻的旧结论（保留在此，防止以后有人重走弯路）】
    // ──────────────────────────────────────────────────────────────────────────
    //
    // 本文件在 2026-10-08 之前叫 AlliesAreHelpfulNullCacheFix，
    // 当时的判断是「这个模组的两个私有静态缓存字段
    //（_cachedTendTargets / _cachedRescueTargets）没有被初始化，初值是 null」，
    // 修法是「在 Postfix 前面加一个前缀，发现是 null 就填一个空列表」。
    //
    // **那个判断是错的。** 2026-10-08 用反编译工具把 IL 逐条读出来之后确认：
    //
    //   · PawnTendAndRescuePatch 的静态构造函数 .cctor **不是空的** ——
    //     它有 117 字节、29 条指令，其中明确包含
    //         newobj List<Pawn>::.ctor
    //         stsfld PawnTendAndRescuePatch::_cachedTendTargets
    //     两字段在类型初始化之后就已经是「空列表」，而不是 null。
    //     （当时之所以误判成空，是因为反编译器把 .cctor 显示成了 `{ }` ——
    //       它没能把 `newobj typespec..ctor` 还原成 C# 写法，看起来就像空方法。
    //       **遇到「静态构造函数是空的」这种结论，一定要再看一眼 IL 指令表。**）
    //
    //   · 这两个字段只会被 UpdateCache() 赋值，而赋值来源
    //     GetTendTargets() / GetRescueTargets() 各只有一个 return 出口，
    //     返回的都是当场 new 出来的 List —— **永远不会返回 null**。
    //
    //   两个事实合起来 ⇒ 那两个字段在整个进程生命周期里**不可能是 null**，
    //   所以曾经那几处 `x == null && x.Count` 的写法虽然确实是笔误，
    //   却永远短路在第一个操作数上、不会真的抛异常；
    //   针对它们写的「修空缓存」前缀因此是一个**永不生效的空操作**。
    //   本版本已把它删除，改成修真正会炸的那一处（见上面第二节）。
    //
    // ──────────────────────────────────────────────────────────────────────────
    // 【四、本补丁怎么修】
    // ──────────────────────────────────────────────────────────────────────────
    //
    // 用「转译器」（Transpiler）改那两处读字段的指令：
    // 把方法体里每一次「读取 Verse.AI.Job.def 字段」都换成调用
    // SafeJobDef(job)。两者在栈上的效果完全一样（都是「消耗一个 Job、产出一个 JobDef」），
    // 区别只有一个：job 是 null 时，前者抛异常，后者返回 null。
    //
    // 于是原来那两行变成：
    //     SafeJobDef(val) != JobDefOf.TendPatient
    //     SafeJobDef(val) == JobDefOf.Rescue
    // val 为 null 时 `null != JobDefOf.TendPatient` 为 true —— 语义正好是
    // 「当前没有在做 TendPatient，所以可以给他安排一个」，与作者的本意一致。
    //
    // 为什么选「转译器」而不是更简单的两种写法：
    //
    //   · 不能用前缀直接 return false 跳过整个 Postfix ——
    //     那等于把「curJob 为 null 时自动去照料/救援」这个功能整个关掉。
    //     而 curJob 为 null 恰恰是最该给人安排任务的时候。
    //   · 不能用收尾器单纯把异常吞掉 —— 那只是把红字藏起来，
    //     该角色这一轮的 TickRare 依然被打断，功能依然是坏的。
    //
    // 另外仍然挂了一个收尾器做兜底（见 Finalizer 的注释）：
    // 万一将来模组改版让转译器匹配不上，至少不会再往日志里刷红字。
    //
    // 【安全性】
    //   · 替换是「等价改写」：非 null 时行为与原来一字不差；
    //     null 时由「抛异常」变成「返回 null」，而原代码本来就在那一步崩掉，
    //     不可能比原来更差。
    //   · 匹配不到任何一处时只记一条警告并原样放行，绝不动别的代码。
    //   · 对第三方 dll 零编译期依赖（类型名走字符串 + 反射），
    //     模组没装时只打一条 Message 就跳过。
    //   · 安装幂等：本类的 Install() 有自己的静态守卫，重复调用只会立刻返回。
    // ==========================================================================
    internal static class AlliesAreHelpfulCurJobNullFix
    {
        // 目标类型「没有命名空间」，全名就叫 PawnTendAndRescuePatch。
        private const string PatchTypeName = "PawnTendAndRescuePatch";

        // 要修的方法名。
        private const string TargetMethodName = "Postfix";

        private static bool installed;

        // 上一次转译一共替换了几处「读 Job.def」。
        // 只用于写日志（0 处通常意味着模组改版了，值得留一条线索）。
        private static int lastReplacementCount;

        // ==================================================================
        // 安装。由 LocalFixesMod 的构造函数调用，外面已经套了 try/catch。
        // ==================================================================
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type patchType = FindPatchType();
                if (patchType == null)
                {
                    // 找不到类型 = 模组没装，或作者改了类名。
                    // 这两种情况重试也没用，所以置位跳过。
                    // （并且这条是 Message 不是 Error：没装这个模组完全正常，不该报红。）
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + PatchTypeName
                        + "（Allies are Helpful 未安装或已改版）；"
                        + "AlliesAreHelpful 空引用修复已跳过。");
                    return;
                }

                MethodInfo target = FindPostfixMethod(patchType);
                if (target == null)
                {
                    // 类型在、方法找不到 → 多半是作者改了结构。不置位，留给下次重试。
                    Log.Error("[GNH LocalFixes] 在 " + PatchTypeName + " 上找不到 "
                        + TargetMethodName + " 方法；AlliesAreHelpful 空引用修复本次未安装，下次会重试。");
                    return;
                }

                MethodInfo transpiler = AccessTools.Method(
                    typeof(AlliesAreHelpfulCurJobNullFix), nameof(Transpiler));
                MethodInfo finalizer = AccessTools.Method(
                    typeof(AlliesAreHelpfulCurJobNullFix), nameof(Finalizer));

                if (transpiler == null || finalizer == null)
                {
                    // 只可能是本类自己写错了，属于开发期错误，必须留 Error。
                    Log.Error("[GNH LocalFixes] 找不到本补丁自己的 Transpiler / Finalizer 方法；"
                        + "AlliesAreHelpful 空引用修复未安装。");
                    return;
                }

                // Patch 的参数顺序是 (原方法, 前缀, 后缀, 转译器, 收尾器)。
                // 这里只要转译器 + 收尾器，其余传 null。
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    null,
                    null,
                    new HarmonyMethod(transpiler),
                    new HarmonyMethod(finalizer));

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装 Allies are Helpful 空引用修复"
                    + "（PawnTendAndRescuePatch.Postfix，改写 " + lastReplacementCount + " 处）。"
                    + "该模组在「角色当前没有工作」时会去读一个为 null 的对象，"
                    + "抛 NullReferenceException 并中断该角色的 TickRare。"
                    + "本补丁把那里改成 null 安全的写法，原行为在正常情况下不受影响。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装 AlliesAreHelpful 空引用修复失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 转译器：把方法体里每一次「读 Verse.AI.Job.def 字段」换成 SafeJobDef(...)。
        // ==================================================================
        //
        // 【为什么这是安全的等价改写】
        //
        // 两者在求值栈上的签名完全一样：
        //     ldfld  Verse.AI.Job::def              :  [Job] → [JobDef]
        //     call   JobDef SafeJobDef(Job)         :  [Job] → [JobDef]
        //
        // 所以直接把那条指令换成一次方法调用即可，前后的指令一个字都不用动。
        // 唯一的行为差异：当栈上那个 Job 是 null 时，
        // ldfld 会抛 NullReferenceException，而 SafeJobDef 会返回 null。
        //
        // 【为什么要把标签（labels）搬过去】
        //
        // 原指令如果正好是某个跳转的目标，那么替换之后必须让新指令接过这些标签，
        // 否则那条跳转会跳到别的指令上去 —— 这种错误不会报错，只会让逻辑悄悄跑偏。
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            lastReplacementCount = 0;

            FieldInfo defField = AccessTools.Field(typeof(Job), "def");
            MethodInfo helper = AccessTools.Method(
                typeof(AlliesAreHelpfulCurJobNullFix), nameof(SafeJobDef));

            if (defField == null || helper == null)
            {
                // 游戏把 Job.def 改名了。什么都不改，原样放行 —— 反正收尾器还在兜底。
                Log.Warning("[GNH LocalFixes] 找不到 Verse.AI.Job.def 字段，"
                    + "AlliesAreHelpful 空引用修复本次未改写任何指令（收尾器仍会兜底）。");
                return list;
            }

            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode != OpCodes.Ldfld || !defField.Equals(ci.operand))
                {
                    continue;
                }

                CodeInstruction call = new CodeInstruction(OpCodes.Call, helper);

                // 把原指令上的跳转标签与异常块原样搬过来，保证控制流不变。
                // ⚠ 每个标签只能搬一次 —— 重复添加会让同一个标签挂到两条指令上，
                //    ILGenerator 会直接报错，或者悄悄跳到错误的位置。
                if (ci.labels != null && ci.labels.Count > 0)
                {
                    call.labels.AddRange(ci.labels);
                }
                if (ci.blocks != null && ci.blocks.Count > 0)
                {
                    call.blocks.AddRange(ci.blocks);
                }

                list[i] = call;
                replaced++;
            }

            lastReplacementCount = replaced;
            if (replaced == 0)
            {
                // 一处都没换到：模组大概改版了。留一条线索，但不报红（游戏照常跑）。
                Log.Warning("[GNH LocalFixes] Allies are Helpful 的 "
                    + TargetMethodName + " 里没有找到可改写的 Job.def 读取"
                    + "（模组可能已改版）。本修复退化为仅由收尾器兜底。");
            }
            return list;
        }

        // ==================================================================
        // 辅助方法：读 Job.def，job 为 null 时返回 null 而不是抛异常。
        // ==================================================================
        //
        // 写成表达式体（=>）只是为了简短；它就是「job 空就返回空，否则返回 job.def」。
        public static JobDef SafeJobDef(Job job)
        {
            return (job == null) ? null : job.def;
        }

        // ==================================================================
        // 收尾器：兜底。
        // ==================================================================
        //
        // 【它什么时候起作用】
        //
        // Harmony 只在「被补丁的方法真的抛了异常」时才会调用收尾器，参数
        // __exception 就是刚抛出来的那个异常（正常跑完时它是 null）。
        //
        // 正常情况下上面的转译器已经把根因修掉了，这里永远不会被调用。
        // 它挡的是「转译器没匹配上」（模组改版、IL 结构变了）那一种情况 ——
        // 那时至少不会再把红字刷进日志、也不会中断该角色的 TickRare。
        //
        // 【为什么只处理 NullReferenceException】
        //
        // 别的异常（模组自己版本不兼容等等）一律原样抛回去。
        // 绝不当「万能消音器」—— 那会把真正的问题一起藏起来。
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }

            if (!(__exception is NullReferenceException))
            {
                return __exception;
            }

            // 用固定 key 的 ErrorOnce：无论触发多少次，日志里最多只出现一条。
            Log.ErrorOnce("[GNH LocalFixes] Allies are Helpful 的 " + TargetMethodName
                + " 仍然抛出了 NullReferenceException（转译器可能未匹配成功）。"
                + "本次已吞掉它以免刷屏，游戏继续运行；该角色这一轮 TickRare 被跳过。"
                + "如果你愿意帮忙排查，请把下面这段原始异常发给作者："
                + "\n" + __exception, ErrorLogKey);
            return null;
        }

        // Log.ErrorOnce 需要的固定去重 key（随便取一个不会被别人撞上的常数）。
        private const int ErrorLogKey = 0x41414846; // "AAHF"

        // ==================================================================
        // 找到 PawnTendAndRescuePatch 这个类型。
        // ==================================================================
        //
        // 它是**没有命名空间**的顶层类，所以先用 AccessTools.TypeByName 去查；
        // 万一查不到（该方法对无命名空间类型的处理在不同 Harmony 版本里略有差异），
        // 再退一步，把所有已加载程序集挨个问一遍 —— 两种写法总有一种能命中。
        private static Type FindPatchType()
        {
            Type t = AccessTools.TypeByName(PatchTypeName);
            if (t != null)
            {
                return t;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    t = assemblies[i].GetType(PatchTypeName, false);
                }
                catch (Exception)
                {
                    // 个别程序集在极端情况下会拒绝查询，跳过它继续找下一个即可。
                    t = null;
                }

                if (t != null)
                {
                    return t;
                }
            }

            return null;
        }

        // ==================================================================
        // 在目标类型里找到那个 Postfix 方法。
        // ==================================================================
        //
        // ⚠ 这里刻意不写成 AccessTools.Method(type, "Postfix", new[] { typeof(Pawn) }, null)：
        //   那要求本补丁包在编译期引用 Verse.Pawn 与第三方 dll 的准确签名，
        //   而本包对第三方是「零编译期依赖」的。用「名字 + 参数个数」来认更稳，
        //   也不怕作者将来给参数换个名字。
        //
        // 为什么要求「参数个数 == 1」：该方法的签名是 Postfix(Pawn __instance)，
        // 只有这一个参数。多加这一道判断是为了避开作者将来可能新加的重载。
        private static MethodInfo FindPostfixMethod(Type patchType)
        {
            MethodInfo[] methods;
            try
            {
                methods = patchType.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception)
            {
                return null;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m == null || m.Name != TargetMethodName)
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();
                if (ps != null && ps.Length == 1)
                {
                    return m;
                }
            }

            return null;
        }
    }
}
