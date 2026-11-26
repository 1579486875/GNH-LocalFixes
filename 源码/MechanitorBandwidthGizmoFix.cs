// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 机制带宽小面板（MechanitorBandwidthGizmo）的布局死循环 + 每帧刷屏
    // ==========================================================================
    //
    // 【症状】
    //
    //   只要「选中了单个机械师小人」，日志就**每帧**多一条 ERROR：
    //
    //       2026-11-11 20:12:50.841 ERROR [Verse] Failed to fit bandwidth cells into gizmo rect.
    //           at .Verse.GizmoGridDrawer.DrawGizmoGrid_Patch5(...)
    //           at .Verse.GizmoGridDrawer.DrawGizmoGridFor_Patch6(...)
    //           at .RimWorld.MapGizmoUtility.MapUIOnGUI_Patch1()
    //           ...
    //
    //   本机实测：一次游戏会话（约 4 小时）累计 **57,137 条**，占整份日志 ERROR 的 98.8%。
    //
    // 【真正的危害不是「报错」】
    //
    //   反编译 Verse.Log.Notify_MessageReceivedThreadedInternal（1.7.5005 原文）：
    //
    //       public static void Notify_MessageReceivedThreadedInternal(string msg, string stackTrace, LogType type)
    //       {
    //           if (++messageCount == 10000)
    //           {
    //               Warning("Reached max messages limit. Stopping logging to avoid spam.");
    //               Notify_LogMessageReceived("Reached max messages limit. Stopping logging to avoid spam.",
    //                                          StackTraceUtility.ExtractStackTrace(), LogType.Warning);
    //               reachedMaxMessagesLimit = true;
    //               Debug.unityLogger.logEnabled = false;      // <-- 全局、永久关掉
    //           }
    //       }
    //
    //   也就是说：**累计 10,000 条之后，游戏会把自己整个日志关掉**。
    //   这条缺陷一条就能把配额打满 5 遍以上，于是从那以后**其它所有模组的报错都写不进日志**。
    //   玩家与排错的人看到的是「日志突然不涨了」，实际是日志被静音 —— 比崩溃更难查。
    //
    // 【根因：`GizmoOnGUI` 里的格子布局循环会除零】
    //
    //   反编译 RimWorld.MechanitorBandwidthGizmo.GizmoOnGUI（1.7.5005，方法体 1206 字节）：
    //
    //       int num  = Mathf.Max(usedBandwidth, totalBandwidth);                 // 要摆几个格子
    //       Rect rect4 = new Rect(rect2.x, rect3.yMax + 6f, rect2.width, rect2.height - rect3.height - 6f);
    //       int num2 = 2;                                                        // 行数
    //       int num3 = Mathf.FloorToInt(rect4.height / (float)num2);             // 格子边长
    //       int num4 = Mathf.FloorToInt(rect4.width  / (float)num3);             // 每行几个
    //       int num5 = 0;
    //       while (num2 * num4 < num)                                            // 装不下就加行
    //       {
    //           num2++;
    //           num3 = Mathf.FloorToInt(rect4.height / (float)num2);
    //           num4 = Mathf.FloorToInt(rect4.width  / (float)num3);
    //           num5++;
    //           if (num5 >= 1000)
    //           {
    //               Log.Error("Failed to fit bandwidth cells into gizmo rect.");
    //               return new GizmoResult(GizmoState.Clear);
    //           }
    //       }
    //
    //   代入本机的确定数值（都不是猜的）：
    //     · GetWidth() 反编译回来是 `return 136f;`（写死的常量）；
    //     · rect   = new Rect(x, y, 136f, 75f);
    //     · rect2  = rect.ContractedBy(6f)  -> 高 63、宽 124；
    //     · rect3  = 高 20（写死的 `20f`）；
    //     · rect4.height = 63 - 20 - 6 = **37**，rect4.width = **124**。
    //
    //   于是 `num3 = floor(37 / num2)`：
    //     · num2 = 2  -> num3 = 18 -> num4 = 6   -> 容量 12
    //     · num2 = 10 -> num3 = 3  -> num4 = 41  -> 容量 410
    //     · num2 = 37 -> num3 = 1  -> num4 = 124 -> 容量 4588      <= 还能画
    //     · num2 = 38 -> num3 = **0** -> num4 = floor(124 / 0f) = floor(+∞)
    //
    //   `Mathf.FloorToInt(+∞)` 在 .NET 上是未定义行为，实际给出 `int.MinValue`。
    //   接着 `num2 * num4` 这个 **int 乘法溢出**，结果在 0 与 int.MinValue 之间来回跳，
    //   两者都 < num（num 是正数），于是条件 `num2 * num4 < num` **永远成立**
    //   -> 循环一路跑到 num5 = 1000 -> 打一条 Error -> 返回 Clear。
    //
    //   一句话：**只要「要摆的格子数」超过 4588，这个 gizmo 就再也摆不出来，
    //   并且每帧重算一次、每帧打一条 Error。**
    //
    //   这也解释了为什么之前的报告说「阈值约 40」是错的 —— 只要带宽在 4588 以内，
    //   循环是能正常收敛的（虽然会画出密密麻麻的小方块）。真正的悬崖在 4588。
    //
    // 【修法：两处，都是「只加不减」，不改变任何正常路径】
    //
    //   ① **给 num 加一个上限**（本补丁的主体）。在 IL 的
    //      `call Mathf::Max(int,int)` 与紧随其后的 `stloc` 之间插入
    //      `ldc.i4 1024` + `call Mathf::Min(int,int)`，等价于源码写：
    //
    //          int num = Mathf.Min(Mathf.Max(usedBandwidth, totalBandwidth), 1024);
    //
    //      为什么 1024 这个数足够安全：clamp 之后 num <= 1024，而
    //      num2 = 18 时容量已经是 1116 >= 1024 —— 循环在 num2 = 18 就收敛，
    //      离悬崖（num2 = 38）还有一倍余量。即使将来 rect 高度被改成一半，
    //      num2 = 10 时容量 1240 仍然够。**任何情况下都不会再除零。**
    //
    //      代价：带宽真的超过 1024 时，gizmo 只画出前 1024 格的填充状态。
    //      视觉上 124px 宽、37px 高的区域本来就画不出 1024 个以上的可辨方块，
    //      所以这个截断**玩家看不出来**；而它换来的是「日志不再被关闭」。
    //
    //   ② **把 `Log.Error` 换成只报一次的 `Log.ErrorOnce`**（兜底）。
    //      万一将来游戏改了 rect 尺寸、或者别的模组也来改这个方法，导致 ① 没生效，
    //      至少刷屏会停 —— 而 layout 失败本来也只是「这个 gizmo 不显示」，
    //      与原版行为完全一致（原版自己就是 `return new GizmoResult(GizmoState.Clear)`）。
    //
    // 【为什么不改 `num3` 而改 `num`】
    //
    //   把 `num3` 夹到最小 1 也能消掉除零，但那样 num2 会一路涨到
    //   `num / 124`，带宽几万时就是几百行、每帧画几十万个 Rect ——
    //   从「刷屏」变成「卡顿」，更糟。夹 num 才是从源头限住工作量。
    //
    // 【自动让路（本工程纪律 §3.1）】
    //
    //   两处都是「先整表扫描、确认各命中恰好一次，之后才动手」。
    //   如果哪天官方自己修好了（例如源码里已经写了 Mathf.Min，或者 Log 已经改成 ErrorOnce），
    //   我们就会「一条都找不到」-> 自动退化为什么都不做，并打一行 Message 说明。
    //   绝不会把已经正确的代码改坏。
    //
    // 【对 1.6 的兼容】
    //
    //   全部走字符串反射（`AccessTools.TypeByName`），不写 `typeof(MechanitorBandwidthGizmo)`。
    //   这样即使某个版本里没有这个类型，本文件也照样编译、照样加载，
    //   只是 Install() 打一行 Message 然后什么也不做。
    //   插入用到的 `Mathf.Max/Min(int,int)` 与 `Log.Error/ErrorOnce(string)` 在 1.6 与 1.7 都存在。
    // ==========================================================================
    public static class MechanitorBandwidthGizmoFix
    {
        /// <summary>
        /// 补丁目标类型。用字符串而不是 typeof：本包不对「游戏某一版才有的类型」产生编译期依赖。
        /// </summary>
        private const string TargetTypeName = "RimWorld.MechanitorBandwidthGizmo";

        /// <summary>补丁目标方法名。</summary>
        private const string TargetMethodName = "GizmoOnGUI";

        /// <summary>
        /// 格子数的硬上限。理由见文件头注释：
        /// 在本机的确定尺寸下，num2 = 18 时容量已是 1116，足够覆盖 1024；
        /// 离「num3 变 0」的悬崖（num2 = 38）有一倍以上余量。
        /// </summary>
        internal const int MaxDrawableCells = 1024;

        /// <summary>
        /// `Log.ErrorOnce` 的去重键。同一进程内这个键只报一次。
        /// 取一个不大可能与他人相撞的值（ASCII "GNHB"）。
        /// </summary>
        private const int ErrorOnceKey = 0x474E4842;

        /// <summary>原版那句会刷屏的错误文本。用来精确定位要替换的那一条 `call Log.Error`。</summary>
        private const string FailMessage = "Failed to fit bandwidth cells into gizmo rect.";

        /// <summary>本补丁是否已经装过。Harmony 补丁是叠加的，装两次会把同一段 IL 处理两遍。</summary>
        private static bool installed;

        /// <summary>是否已经打印过「本项已让路」的说明，避免每次进入界面都刷一行。</summary>
        private static bool announced;

        /// <summary>
        /// 安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。
        /// </summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type targetType = AccessTools.TypeByName(TargetTypeName);
                if (targetType == null)
                {
                    // 没有这个类型（例如未装 Biotech 的极简环境）—— 正常情况，不是错误。
                    installed = true;
                    Log.Message("[GNH LocalFixes] " + TargetTypeName + " is not present;"
                        + " the mechanitor bandwidth gizmo fix was skipped (nothing to do).");
                    return;
                }

                MethodInfo target = FindTargetMethod(targetType);
                if (target == null)
                {
                    // 方法名/签名变了。不置位 installed，留给下次重试（并让 GetTypes 兜底那份照常工作）。
                    Log.Error("[GNH LocalFixes] " + TargetTypeName + "." + TargetMethodName
                        + " was not found with the expected signature; the mechanitor bandwidth gizmo fix"
                        + " is deferred and will retry.");
                    return;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(MechanitorBandwidthGizmoFix), nameof(Transpiler), null, null);
                if (transpiler == null)
                {
                    Log.Error("[GNH LocalFixes] Internal error: MechanitorBandwidthGizmoFix.Transpiler not found."
                        + " The mechanitor bandwidth gizmo fix is NOT active this session.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(target, null, null, new HarmonyMethod(transpiler));

                installed = true;

                // 装完读回补丁表：这是「装上了」与「装对了」之间的那次验证。
                Patches info = Harmony.GetPatchInfo(target);
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " (transpilers=" + (info != null ? info.Transpilers.Count : -1) + ")."
                    + " Cells are clamped to " + MaxDrawableCells
                    + " and the per-frame Log.Error is throttled, so selecting a mechanitor can no longer"
                    + " spam the log (and can no longer burn the 10,000-message budget that makes the game"
                    + " switch logging off entirely).");
            }
            catch (Exception ex)
            {
                // 装不上不影响游戏本体，也不影响其它修复项，但必须留痕。
                Log.Warning("[GNH LocalFixes] Failed to install the mechanitor bandwidth gizmo fix"
                    + " (that fix is NOT active this session; everything else is unaffected): " + ex);
            }
        }

        /// <summary>
        /// 找 `GizmoOnGUI(Vector2, float, GizmoRenderParms)`。
        /// 先按精确签名找；找不到再退回「同名 + 三个参数」，这样即使某一版把
        /// `GizmoRenderParms` 换了名字也还能装上（Transpiler 本身只认指令形态，不认参数名）。
        /// </summary>
        private static MethodInfo FindTargetMethod(Type targetType)
        {
            try
            {
                MethodInfo exact = targetType.GetMethod(
                    TargetMethodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(Vector2), typeof(float), typeof(GizmoRenderParms) },
                    null);
                if (exact != null)
                {
                    return exact;
                }
            }
            catch (Exception)
            {
                // GamizmoRenderParms 在某个版本里不存在时 GetMethod 会抛 —— 落到下面的宽松查找。
            }

            try
            {
                return targetType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == TargetMethodName && m.GetParameters().Length == 3);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 逐条过一遍 IL，做两处改动：
        ///   ① `call Mathf::Max(int,int)` 之后插 `ldc.i4 1024` + `call Mathf::Min(int,int)`；
        ///   ② `ldstr "Failed to fit..."` 之后那条 `call Log::Error(string)` 换成 `QuietError`。
        ///
        /// 注意本方法【在补丁安装的那一刻执行一次】，不是每次绘制都执行，
        /// 所以它对运行时帧率的影响是零。
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // ToList 之后，list 里的元素仍然是【同一批指令对象】——
            // 这也是下面必须先扫描、后修改的原因：一旦改了对象，原始序列也跟着变。
            List<CodeInstruction> list = instructions.ToList();
            if (list.Count == 0)
            {
                return list;
            }

            MethodInfo maxInt = AccessTools.Method(typeof(Mathf), "Max", new[] { typeof(int), typeof(int) });
            MethodInfo minInt = AccessTools.Method(typeof(Mathf), "Min", new[] { typeof(int), typeof(int) });
            MethodInfo logError = AccessTools.Method(typeof(Log), "Error", new[] { typeof(string) });
            MethodInfo quietError = AccessTools.Method(typeof(MechanitorBandwidthGizmoFix), nameof(QuietError), null, null);

            if (maxInt == null || minInt == null || quietError == null)
            {
                // 连 UnityEngine.Mathf 都找不齐，说明环境异常 —— 原样返回，一个字节都不动。
                Log.Warning("[GNH LocalFixes] Mathf.Min/Max(int,int) could not be resolved;"
                    + " the mechanitor bandwidth gizmo fix left the method untouched.");
                return list;
            }

            // ── 第 ① 处：Mathf.Max(int,int) 且紧随其后是一条 stloc ──
            //
            // 为什么要额外要求「下一条是 stloc」：
            //   `Mathf.Max` 在原方法里只被调用一次（就是 `int num = Mathf.Max(...)`），
            //   但万一将来官方在别处也用了它，这个附加条件能保证我们只夹住「存进局部变量」
            //   那一种形态，不会把「直接参与表达式」的调用也夹掉。
            int maxIndex = -1;
            int maxCount = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Call && IsSameMethod(ci.operand as MethodInfo, maxInt)
                    && IsStoreLocal(list[i + 1].opcode))
                {
                    maxIndex = i;
                    maxCount++;
                }
            }

            // ── 第 ② 处：ldstr "Failed to fit..." + call Log.Error(string) ──
            //
            // 必须连字符串一起认：同一个方法里可能还有别的 Log.Error 调用，
            // 只按 `call Log::Error` 匹配会误伤。
            int errorIndex = -1;
            int errorCount = 0;
            for (int i = 1; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Call && IsSameMethod(ci.operand as MethodInfo, logError)
                    && list[i - 1].opcode == OpCodes.Ldstr
                    && string.Equals(list[i - 1].operand as string, FailMessage, StringComparison.Ordinal))
                {
                    errorIndex = i;
                    errorCount++;
                }
            }

            // ── 判断是否还有活干 ──
            //
            // 两处都是「恰好各命中一次」才动手。任何一处不满足都说明
            // 这个方法已经被别人（多半是官方）改过了 —— 那就一个字节都不动。
            bool needClamp = maxCount == 1;
            bool needQuiet = errorCount == 1;

            if (!needClamp && !needQuiet)
            {
                // 最常见的良性原因：官方自己把这两处都修好了。
                if (!announced)
                {
                    announced = true;
                    Log.Message("[GNH LocalFixes] " + TargetTypeName + "." + TargetMethodName
                        + " does not need fixing this version"
                        + " (expected 1 `Mathf.Max(int,int)+stloc` and 1 `ldstr \"" + FailMessage + "\"+Log.Error,"
                        + " found " + maxCount + " and " + errorCount + "). Left untouched.");
                }
                return list;
            }

            // ── 先做不改索引的那一处：替换 Log.Error 的调用目标 ──
            if (needQuiet)
            {
                list[errorIndex].operand = quietError;
            }

            // ── 再做需要插入指令的那一处：在 Max 与 stloc 之间夹一个 Min ──
            //
            // 插入后栈形态： [ ... , maxResult , 1024 ]  --call Min-->  [ ... , clamped ]
            // 紧接着原本那条 stloc 把结果存进局部变量 —— 完全等价于
            //     int num = Mathf.Min(Mathf.Max(used, total), 1024);
            // 不需要 load 局部变量，也不需要知道局部变量的下标。
            if (needClamp)
            {
                list.InsertRange(maxIndex + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldc_I4, MaxDrawableCells),
                    new CodeInstruction(OpCodes.Call, minInt),
                });
            }

            Log.Message("[GNH LocalFixes] Mechanitor bandwidth gizmo fix applied:"
                + (needClamp
                    ? " cells clamped to " + MaxDrawableCells + " (inserted after instruction #" + maxIndex + ");"
                    : " cell clamp skipped (pattern not found);")
                + (needQuiet
                    ? " the per-frame Log.Error replaced with Log.ErrorOnce (instruction #" + errorIndex + ")."
                    : " log throttle skipped (pattern already changed)."));

            return list;
        }

        /// <summary>
        /// 替身：把「每帧一条」的错误降级成「整局一条」。
        ///
        /// 保留 Error 级别而不是删掉，是因为「gizmo 摆不下」本身仍然是值得知道的信息；
        /// 只是它每帧都会重算一次，原版那样报会把日志配额打光。
        /// </summary>
        internal static void QuietError(string text)
        {
            Log.ErrorOnce(
                text + " （此错误每帧都会重复触发，已由「多模组红字修复补丁」改为只报一次；"
                    + "该 gizmo 的显示可能与实际带宽不符，但不会再淹没其它模组的报错。）",
                ErrorOnceKey);
        }

        /// <summary>
        /// 两个 MethodInfo 是否指向同一个方法。
        /// 不用 `==` 是因为 MethodInfo 的引用相等在不同加载上下文里不保证成立
        /// （本机就有 Assembly-CSharp 被两份程序集加载的情形）；按声明类型 + 名字 + 参数类型比更稳。
        /// </summary>
        private static bool IsSameMethod(MethodInfo a, MethodInfo b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            {
                return false;
            }
            if (a.DeclaringType != b.DeclaringType)
            {
                return false;
            }
            ParameterInfo[] pa = a.GetParameters();
            ParameterInfo[] pb = b.GetParameters();
            if (pa.Length != pb.Length)
            {
                return false;
            }
            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i].ParameterType != pb[i].ParameterType)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 这条指令是不是「把栈顶存进某个局部变量」。
        ///
        /// ⚠ 不能只判 `stloc`：C# 编译器会按局部变量下标自动选择紧凑形式
        /// （`stloc.0` … `stloc.3` / `stloc.s` / `stloc`），六种都要认。
        /// </summary>
        private static bool IsStoreLocal(OpCode opcode)
        {
            return opcode == OpCodes.Stloc
                || opcode == OpCodes.Stloc_S
                || opcode == OpCodes.Stloc_0
                || opcode == OpCodes.Stloc_1
                || opcode == OpCodes.Stloc_2
                || opcode == OpCodes.Stloc_3;
        }
    }
}
