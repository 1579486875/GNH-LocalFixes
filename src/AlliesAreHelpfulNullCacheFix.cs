using System;
using System.Reflection;
using HarmonyLib;
using Verse;

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
    // 这是模组「Allies are Helpful」的一个笔误型空引用缺陷。
    // ==========================================================================
    //
    // 【第一件事：出问题的是哪个模组】
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
    // 【第二件事：错在哪一行】
    //
    // AlliesAreHelpful.dll 里有个静态类 PawnTendAndRescuePatch
    // （这个类**没有命名空间**，全名就叫 PawnTendAndRescuePatch），
    // 它有两个私有的静态缓存字段：
    //
    //     private static List<Pawn> _cachedTendTargets;    // 需要被照顾的伤员
    //     private static List<Pawn> _cachedRescueTargets;  // 需要被救走的倒地者
    //
    // 它的 UpdateCache() 里有这么一行（已用反编译器逐字核对过）：
    //
    //     if (dangerSystemEnabled
    //         && (   _cachedTendTargets != null
    //             || (_cachedRescueTargets != null && _cachedTendTargets.Count != 0)
    //             || _cachedRescueTargets.Count != 0            // ← 就是这里
    //            )
    //         && Find.CurrentMap?.mapPawns != null
    //         && Find.TickManager.TicksGame - _lastEnemyCacheUpdateTick > EnemyCacheUpdateInterval)
    //
    // 作者显然想写的是「两个缓存里只要有一个非空就继续」，
    // 但这段的三段判断在**两个缓存都还是 null** 的情况下会一路走到底：
    //
    //     第 1 段：_cachedTendTargets != null          → false（它是 null）
    //     第 2 段：_cachedRescueTargets != null && ... → false（它也是 null，&& 直接短路）
    //     第 3 段：_cachedRescueTargets.Count          → 💥 对 null 取 .Count → 空引用异常
    //
    // 同样的写法在 Postfix() 里还有一处：
    //
    //     if ((_cachedTendTargets == null && _cachedRescueTargets == null)     // ← 注意是 &&
    //         || (_cachedTendTargets.Count == 0 && _cachedRescueTargets.Count == 0))
    //
    // 这里第一段用 && 是错的：只有当**两个同时为 null** 时才会提前返回；
    // 只要有一个是 null、另一个不是，就会掉进第二段去对 null 取 .Count，同样会炸。
    //
    // 【第三件事：为什么缓存字段真的会是 null】
    //
    // 已逐个确认，这不是猜测：
    //
    //   · 反编译 PawnTendAndRescuePatch 的静态构造函数，它是**空的**：
    //         static PawnTendAndRescuePatch() { }
    //     也就是说这两个字段的初始值就是 C# 引用类型的默认值 —— null。
    //
    //   · 唯一会给它们赋值的地方是 UpdateCache()，而那次赋值被一个
    //     「距离上次更新超过 CacheUpdateInterval」的时间闸门挡着；
    //     更重要的是它同时要求 Find.CurrentMap?.mapPawns != null。
    //     一旦这一条不成立（地图正在切换、当前地图暂时为空等瞬间），
    //     赋值整段被跳过，而下面那段判断**照样会执行** —— 于是撞上 null。
    //
    //   · 两个 getter（GetTendTargets / GetRescueTargets）的第一行都是
    //         List<Pawn> list = new List<Pawn>();
    //     所有 return 都返回 list，**永远不会返回 null**。
    //     所以字段一旦被赋过值，就再也不会是 null —— 出问题的窗口
    //     只存在于「还没赋过值」的那段时间里。
    //
    //   · ClearCache() 用的是 _cachedTendTargets?.Clear() 这种写法，
    //     只会清空内容、**不会把字段设回 null**，所以它不会制造新的 null 窗口。
    //
    // 【第四件事：本补丁做什么】
    //
    // 只做一件事：在 PawnTendAndRescuePatch.Postfix 真正开始跑之前插一脚，
    // 如果发现这两个缓存字段是 null，就把它们**换成一个刚建好的空列表**。
    //
    // 这样就等于同时堵住了上面说的两个洞：
    //
    //   · Postfix()  里  (tend == null && rescue == null) → 不再成立，
    //                  于是不会走进那段对 null 取 .Count 的代码；
    //   · UpdateCache() 里 _cachedTendTargets != null → 直接为真，整段短路放行。
    //
    // 【为什么这是安全的（不是"随便吞个异常"）】
    //
    // 「空列表」表达的语义正是**「目前没有需要照顾/救援的目标」**，
    // 这与字段还没被初始化时**本来应该表达的意思完全一致** ——
    // 作者写这段代码的本意就是「缓存还没建立时当作没有目标，直接返回」。
    // 我们只是把它**本来想写对**的那个状态替它补上，没有改变任何正常路径：
    //
    //   · 缓存本来就有内容时：字段非 null，本补丁**一个字都不改**，原逻辑照跑；
    //   · 缓存确实是空列表时：本补丁也不改（它非 null）；
    //   · 下次 UpdateCache() 该刷新时，照样会用真实数据覆盖掉我们这个空列表。
    //
    // 换句话说：**我们只把"会崩"变成"什么也不做"，而不是把"会做事"变成"不做事"。**
    //
    // 【为什么用前缀补丁而不是收尾器（Finalizer）吞异常】
    //
    // 收尾器只能"把异常吃掉"，但异常是在方法**执行到一半**时抛出来的 ——
    // 方法内部已经做过的副作用（比如往 jobQueue 里塞过的任务）会保留下来，
    // 而后面没做完的部分则永远不执行，属于**状态不明**。
    // 前缀则是在进入方法前就把数据修正到合法状态，让原方法**完整、正常地跑完**，
    // 不留半截状态。这是本补丁包一贯的选择。
    //
    // 【性能说明（为什么挂在 TickRare 上也不怕）】
    //
    // 本前缀挂在 Verse.Pawn.TickRare() 上，而 TickRare 是**每 250 个游戏刻
    // 才调用一次**的低频方法（不是每帧的 Tick）。前缀里做的是两次
    // FieldInfo.GetValue 判空 —— 对引用类型字段取值不会装箱，
    // 属于几十纳秒级的操作，放在这个频率上对帧率的影响可以忽略。
    //
    // 一旦两个字段都已经非 null（也就是游戏跑起来之后的绝大多数时候），
    // 前缀只是读两个字段、发现不是 null，然后直接返回，**什么都不写**。
    // ==========================================================================
    internal static class AlliesAreHelpfulNullCacheFix
    {
        // 目标类型。注意：这个类**没有命名空间**，全名就是 "PawnTendAndRescuePatch"。
        private const string PatchTypeName = "PawnTendAndRescuePatch";

        // 目标方法：挂在 Verse.Pawn.TickRare 上的那个后缀补丁方法。
        private const string TargetMethodName = "Postfix";

        // 需要"救活"的两个私有静态缓存字段。
        private const string TendCacheFieldName = "_cachedTendTargets";
        private const string RescueCacheFieldName = "_cachedRescueTargets";

        private static bool installed;

        // 这两个反射句柄只在安装时解析一次，之后每次调用直接复用（避免热路径反复查字段）。
        private static FieldInfo tendCacheField;
        private static FieldInfo rescueCacheField;

        // 一共补过几次 null（只用来写日志，不参与逻辑判断）。
        private static int repairedCount;

        // 是否已经打过"开始修复"的说明日志。
        // 这个前缀会被成千上万次调用，每次打日志反而把日志刷满，所以只打第一条。
        private static bool announced;

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
                    // 这两种情况重试也没用，所以置位跳过（并且这条消息是 Message 不是 Error：
                    // 没装这个模组是完全正常的状态，不该报红）。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + PatchTypeName
                        + "（Allies are Helpful 未安装或已改版）；"
                        + "AlliesAreHelpful 空缓存修复已跳过。");
                    return;
                }

                MethodInfo target = FindPostfixMethod(patchType);
                if (target == null)
                {
                    // 类型在、方法找不到 → 多半是作者改了结构。不置位，留给下次重试；并且留 Error 线索。
                    Log.Error("[GNH LocalFixes] 在 " + PatchTypeName + " 上找不到 "
                        + TargetMethodName + " 方法；AlliesAreHelpful 空缓存修复本次未安装，下次会重试。");
                    return;
                }

                const BindingFlags FieldFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                tendCacheField = patchType.GetField(TendCacheFieldName, FieldFlags);
                rescueCacheField = patchType.GetField(RescueCacheFieldName, FieldFlags);

                if (tendCacheField == null || rescueCacheField == null)
                {
                    // 字段改名了。不置位 → 下次重试；并且必须留 Error 线索，不许静默跳过。
                    Log.Error("[GNH LocalFixes] 在 " + PatchTypeName + " 上找不到 "
                        + TendCacheFieldName + " / " + RescueCacheFieldName
                        + " 字段（模组可能改版了）；AlliesAreHelpful 空缓存修复本次未安装，下次会重试。");
                    return;
                }

                // 只在两个字段都确实是引用类型（List 之类）时才继续 ——
                // 万一作者把字段类型改成了 int 之类的值类型，这里就应当停手。
                if (tendCacheField.FieldType.IsValueType || rescueCacheField.FieldType.IsValueType)
                {
                    Log.Error("[GNH LocalFixes] " + PatchTypeName + " 的缓存字段不再是引用类型；"
                        + "AlliesAreHelpful 空缓存修复本次未安装，下次会重试。");
                    return;
                }

                // Patch 的参数顺序是 (原方法, 前缀, 后缀, 转译器, 收尾器)。
                // 这里只需要前缀，其余三个传 null。
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(AlliesAreHelpfulNullCacheFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装 Allies are Helpful 空缓存修复"
                    + "（PawnTendAndRescuePatch.Postfix）。"
                    + "该模组把两个静态缓存字段当成【一定已经建好】来用，"
                    + "但它们在赋值之前就是 null，一旦在地图切换等瞬间走进判断就会抛"
                    + " NullReferenceException 并中断该角色的 TickRare。"
                    + "本补丁只在这两个字段还是 null 时把它们换成一个空列表 ——"
                    + "也就是作者本来想表达的那个状态【暂时没有目标】，"
                    + "缓存有内容时一个字都不改动。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装 AlliesAreHelpful 空缓存修复失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 真正干活的前缀：把还是 null 的缓存字段换成一个空列表。
        // ==================================================================
        //
        // 这个方法会被 TickRare 高频调用，所以里面的每一步都刻意写得很省：
        //   · 不做任何 LINQ、不建临时集合；
        //   · 字段非 null（绝大多数情况）时只有两次判空读取，没有任何写入；
        //   · 只有在真的发现 null 时才会反射写一次字段（整个存档生命周期里通常只有一两次）。
        public static void Prefix()
        {
            try
            {
                RepairIfNull(tendCacheField);
                RepairIfNull(rescueCacheField);
            }
            catch (Exception ex)
            {
                // 本前缀绝不能把异常抛回给游戏 —— 那会把"修 bug"变成"制造 bug"。
                // 出任何意外都只是放弃这次修复，让原方法按原样去跑。
                Log.Warning("[GNH LocalFixes] AlliesAreHelpful 空缓存修复的前缀出错，"
                    + "已放行原逻辑（不影响游戏）：" + ex.Message);
            }
        }

        // 单个字段的处理：是 null 就换成一个新建的空列表，否则什么也不做。
        private static void RepairIfNull(FieldInfo field)
        {
            if (field == null)
            {
                return;
            }

            // 对引用类型字段取值不会装箱，代价很低。
            if (field.GetValue(null) != null)
            {
                return;
            }

            // 用字段**自己的类型**去建空列表，因此不需要在编译期引用 List<Pawn>，
            // 也就不需要引用 AlliesAreHelpful.dll —— 这正是本补丁包"零硬依赖"的做法。
            object emptyList = Activator.CreateInstance(field.FieldType);
            field.SetValue(null, emptyList);

            repairedCount++;
            if (!announced)
            {
                announced = true;
                Log.Message("[GNH LocalFixes] 已修好 Allies are Helpful 的空缓存字段（"
                    + field.Name + " = null → 空列表）。"
                    + "这会消除它在地图切换等瞬间抛出的 NullReferenceException；"
                    + "该模组的功能不受影响（空列表就等于【暂时没有需要帮助的目标】）。"
                    + "之后不再重复打印。");
            }
        }

        // ==================================================================
        // 找到 PawnTendAndRescuePatch 这个类型。
        // ==================================================================
        //
        // 它是**没有命名空间**的顶层类，所以先用 AccessTools.TypeByName 去查；
        // 万一查不到（该方法对无命名空间类型的处理在不同 Harmony 版本里略有差异），
        // 再退一步，把所有已加载程序集挨个问一遍 —— 这样两种写法总有一种能命中。
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
                catch
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
        // 在目标类型上找到那个只接收一个 Pawn 参数的 Postfix 方法。
        // ==================================================================
        //
        // 不写成 AccessTools.Method(type, "Postfix", new[] { typeof(Pawn) }, null)：
        // 那样会把本补丁**编译期**绑定到 Verse.Pawn 上，虽然 Pawn 是原版类型、
        // 绑定它没问题，但这里按签名特征去认（"名字叫 Postfix、且只有一个参数"）
        // 更宽容 —— 将来作者把参数类型换成别的 Pawn 子类也照样能挂上。
        private static MethodInfo FindPostfixMethod(Type patchType)
        {
            MethodInfo[] methods = patchType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != TargetMethodName)
                {
                    continue;
                }

                if (m.GetParameters().Length != 1)
                {
                    continue;
                }

                return m;
            }

            return null;
        }
    }
}
