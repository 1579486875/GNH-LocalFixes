// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【42】每次读档刷 132 条 `/fixPawn` 的 loadID 报错
    //
    // 症状（成对出现，各 66 条）：
    //
    //   Tried to register the same load ID twice: null, pathRelToParent=/fixPawn, parent=…
    //   Could not get load ID. … never added during LoadingVars. pathRelToParent=/fixPawn, parent=…
    //
    // ── 根因：模组把一个「可能为 null 的引用」无条件交给了 Scribe ──
    //
    //   RJW 的「裸体评价」（dord.nuditymattersmore）里有个 ThingComp `Fixation`，
    //   它的 PostExposeData 是这么写的（源码 ThingComps/Fixation.cs）：
    //
    //       private Pawn fixPawn;
    //       …
    //       Scribe_References.Look(ref fixPawn, "fixPawn");     ← 无条件
    //       …
    //       if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
    //       if (!hasFixation || fixPawn == null || fixPawn.Dead) { hasFixation = false; fixPawn = null; }
    //
    //   注意顺序：**引用早就交出去了**，清空是在之后才做的。
    //   而 `Scribe_References.Look` 会拿 `target` 的 loadID 去登记；`fixPawn` 是 null 时，
    //   登记的就是一个 **null loadID** —— 于是「register the same load ID twice」。
    //
    //   这些存档里的 fixPawn 之所以是 null：1.6 → 1.7 迁移后目标的 loadID 解析不出来
    //   （对象没进 loadedObjectDirectory，或者引用的是已被替换的旧对象）。
    //   属于**脏数据**，不是崩溃 —— 但它每读一次档刷 132 条，把真正的问题埋掉。
    //
    // ── 修法：只拦「label 是 fixPawn 且 target 确实是 null」这一种 ──
    //
    //   那种情况下 `Scribe_References.Look` 做的事没有意义（null 引用不需要登记），
    //   跳过它与原版行为等价 —— 而且紧接着那段 PostLoadInit 本来就会把它置空。
    //
    //   **只在 target == null 时才跳**：如果 fixPawn 是有效引用，一个字节都不改，
    //   存档的序列化格式、向后兼容性完全不变（这一点很重要 —— 258 MB 的存档不能拿来冒险）。
    //
    // ── 为什么不去改模组本身 ──
    //
    //   把 `Scribe_References.Look` 换成「先存 thingID 再解析」确实更彻底，
    //   但那会改变存档格式：老档里没有 thingID 字段，新档写法不同，来回切版本会丢数据。
    //   本项是纯读侧的过滤，删掉本模组就完全恢复原状。
    //
    // ── 什么情况下它会自动失效 ──
    //
    //   · 用户关掉本项 → 前缀不装，Scribe 完全不受影响。
    //   · 目标模组改了 label（不叫 "fixPawn" 了）→ 前缀匹配不到，本项不动作。
    //   · 存档里没有脏数据（fixPawn 都是有效引用）→ 前缀一次都不拦，也不打日志。
    //   · 原版 Scribe_References.Look 泛型签名变了 → 找不到目标，本次跳过并留 Error。
    //
    // ── 性能 ──
    //
    //   `Scribe_References.Look<Pawn>` 只在**存读档**时调用，不在 tick / 渲染路径上。
    //   前缀里一次字符串比较 + 一次 null 判断。
    // ==========================================================================
    internal static class FixPawnScribeNullFix
    {
        private const string ScribeRefsTypeName = "Verse.Scribe_References";
        private const string LookMethodName = "Look";

        // 只处理这一个 label。改别的 label 的引用不动。
        private const string TargetLabel = "fixPawn";

        private static bool installed;
        private static int suppressed;
        private static bool announced;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type t = AccessTools.TypeByName(ScribeRefsTypeName);
                if (t == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + ScribeRefsTypeName
                        + "；【42】/fixPawn 脏数据过滤本次跳过。");
                    return;
                }

                // ⚠ 这里不能用 AccessTools.Method(t, "Look", null, new[]{ typeof(Pawn) }) ——
                //   Verse.Scribe_References 有**两个**同名泛型重载，泛型参数都能是 Pawn：
                //       Look<T>(ref T              refee, string label, bool saveDestroyedThings)
                //       Look<T>(ref WeakReference<T> refee, string label, bool saveDestroyedThings)
                //   所以按「方法名 + 泛型参数」找会抛 AmbiguousMatchException（v1.4.18 第一次实机就撞上了）。
                //   这里改成自己遍历：只认「第一个参数是 ref T、且 T 就是那个泛型参数本身」的那个重载。
                MethodInfo generic = FindLookByRefMethod(t);
                if (generic == null)
                {
                    Log.Error("[GNH LocalFixes] 在 " + ScribeRefsTypeName + " 上找不到 Look<T>(ref T, string, bool)；"
                        + "【42】本次未安装，下次会重试。");
                    return;
                }

                MethodInfo target = generic.MakeGenericMethod(typeof(Pawn));
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] Look<T> 封闭到 Pawn 失败；【42】本次未安装，下次会重试。");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(FixPawnScribeNullFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【42】/fixPawn 脏数据过滤"
                    + "（" + ScribeRefsTypeName + ".Look<Pawn>）。"
                    + "只在「label = " + TargetLabel + " 且引用本身是 null」时跳过登记，"
                    + "有效引用一个字节都不动，存档格式不变。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【42】/fixPawn 脏数据过滤失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 前缀：label 是 fixPawn、且要登记的引用确实是 null → 整个跳过。
        // ==================================================================
        //
        // 参数用位置绑定：`ref Pawn __0` 就是 `Look<Pawn>(ref Pawn target, …)` 的第一个参数。
        // label 用名字绑定（它在该方法上叫 label，且不是泛型参数，名字稳定）。
        public static bool Prefix(ref Pawn __0, string label)
        {
            if (label != TargetLabel || __0 != null)
            {
                return true;
            }

            suppressed++;
            if (!announced)
            {
                announced = true;
                Log.Message("[GNH LocalFixes] 已开始过滤 /fixPawn 的空引用登记。"
                    + "成因：1.6→1.7 迁移后这些存档里的引用解析不出来，模组又无条件交给了 Scribe，"
                    + "于是每次读档刷 132 条 loadID 报错。跳过的是「登记一个 null」，"
                    + "与原版后续把它置空的行为等价；有效引用不受影响。之后不再重复打印。");
            }

            return false;
        }

        // ==================================================================
        // 从 Verse.Scribe_References 上挑出 `Look<T>(ref T, string, bool)` 这个重载。
        // ==================================================================
        //
        // 判据（三条都要满足）：
        //   ① 名字是 Look
        //   ② 是泛型方法定义（还没封闭到具体 T）
        //   ③ 第一个参数是 **by-ref**，且解引用后的类型**就是那个泛型参数本身**
        //      —— 这一条把 `ref WeakReference<T>` 那个重载排除掉了
        //      （它的解引用类型是 WeakReference`1，不是泛型参数）。
        private static MethodInfo FindLookByRefMethod(Type scribeRefsType)
        {
            MethodInfo[] methods = scribeRefsType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != LookMethodName)
                {
                    continue;
                }

                if (!m.IsGenericMethodDefinition)
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 3)
                {
                    continue;
                }

                Type first = ps[0].ParameterType;
                if (!first.IsByRef)
                {
                    continue;
                }

                Type element = first.GetElementType();
                if (element == null || !element.IsGenericParameter)
                {
                    continue;
                }

                return m;
            }

            return null;
        }
    }
}
