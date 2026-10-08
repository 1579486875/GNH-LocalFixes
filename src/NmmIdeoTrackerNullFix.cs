using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：NudityMattersMore（创意工坊包名 dord.nuditymattersmore）
    // 的 CoverBody.UpdateIdeo 会在「调用方传进来的 tracker 是 null、而它内部的静态字典
    // 里恰好已经有一条同 thingIDNumber 的记录」时解引用 null 而崩溃，
    // 进而把整条 Pawn 生成流程掀翻 —— 表现在游戏里就是「开新局卡死在生成阶段」。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // 反编译 NudityMattersMore.dll（本机 MVID c7f1a2d25f9246cd94581be1c9ed2025，
    // 与出错堆栈里的那个完全一致）后，这段逻辑还原成 C# 是这样：
    //
    //     public static void UpdateIdeo(Pawn pawn, PawnIdeoTracker tracker, int gameTick)
    //     {
    //         if (pawn.ideo == null || pawn.ideo.Ideo == null) return;
    //         if (gameTick == 0) gameTick = Find.TickManager.TicksGame;
    //         if (!PawnInteractionManager.ideoTrackers.ContainsKey(pawn.thingIDNumber))
    //         {
    //             PawnInteractionManager.ideoTrackers[pawn.thingIDNumber] = new PawnIdeoTracker();
    //             tracker = PawnInteractionManager.ideoTrackers[pawn.thingIDNumber];   // 只有这条路径给 tracker 赋值
    //         }
    //         List<Precept> list = pawn.ideo.Ideo.PreceptsListForReading ?? new List<Precept>();
    //         tracker.lastCheckTick = gameTick;          // 崩在这里
    //         ...
    //     }
    //
    // 要害是那个 if：只有字典里「没有」该 Pawn 的记录时，它才会把新建的 tracker
    // 赋值给局部变量。如果字典里已经有这条记录（ContainsKey 为 true），
    // tracker 就原封不动地保持调用方传进来的值 —— 而那个调用方硬编码传的是 null：
    //
    //     // NudityMattersMore.Patch_PawnGenerator_NMM.NMMPawnInitialization（HarmonyPostfix）
    //     private static void NMMPawnInitialization(ref PawnGenerationRequest request, ref Pawn __result)
    //     {
    //         PawnInteractionManager.InitializePawn(__result);
    //         if (__result.ideo != null && __result.ideo.Ideo != null && __result.IsColonist
    //             && __result.ideo.Ideo.memes != null && __result.ideo.Ideo.memes.Count > 0
    //             && !InfoHelper.EntityOrGhoul(__result))
    //         {
    //             CoverBody.UpdateIdeo(__result, null, 0);      // 第二个实参（tracker）硬编码为 null
    //         }
    //     }
    //
    // 于是只要「字典里已有该 thingIDNumber」+「调用方传 null」同时成立，
    // 方法体里第一处用到 tracker 的地方就直接空引用。
    //
    // 【为什么开新局开得越多越容易炸】
    //
    // PawnInteractionManager.ideoTrackers 是个 public static 字段
    // （Dictionary<int, PawnIdeoTracker>），它活在进程里、跨局不清空；
    // 而 RimWorld 每开一局都会新建 UniqueIDsManager，thingIDNumber 从 1 重新数。
    // 两件事叠在一起：新局里刚生成的殖民者，拿到的 thingIDNumber 很可能正好命中
    // 上一局（或更早某一局）留下的记录 → ContainsKey 为 true → tracker 仍是 null → 崩。
    //
    // 实测（2026-10-06，同一个游戏进程里连续用 Quickstarter 开新局）：
    //
    //     第 1 次  01:51                                   不崩
    //     第 2-6 次 10:44 / 10:52 / 12:25 / 12:45 / 12:51   不崩
    //     第 7 次  13:19   连抛 4 次 → 卡死，其后约 4 分 15 秒内该异常不再出现，玩家只能重开
    //     第 8 次  13:24   抛 3 条同堆栈的日志，被 PawnGenerator 自己的重试兜住 → 地图正常生成
    //
    //     （口径说明：上面「第几次」按日志里 [HugsLib] Quickstarter generating map 的出现次序数；
    //      「抛几次」按 Error while generating pawn / Trying one more time /
    //      Exception from asynchronous event 三条同堆栈记录合并计。）
    //
    // 日志原文（2026-10-06 13:19:57.324，与 IL 偏移对得上）：
    //
    //     ERROR: Error while generating pawn. Rethrowing. Exception: System.NullReferenceException
    //       at NudityMattersMore.CoverBody.UpdateIdeo (Verse.Pawn, ...tracker..., System.Int32) [0x00079]
    //       at NudityMattersMore.Patch_PawnGenerator_NMM.NMMPawnInitialization (...) [0x0005c]
    //       at Verse.PawnGenerator.GenerateNewPawnInternal (...) [0x0015f]
    //         - POSTFIX dord.nuditymattersmore: NudityMattersMore.Patch_PawnGenerator_NMM:NMMPawnInitialization
    //       at Verse.PawnGenerator.GeneratePawn (...) [0x00113]
    //
    // 把 0x79 = 121 拿去比 IL，正是这一句：
    //
    //     IL_0079: ldarg.1        ← tracker
    //     IL_007A: ldarg.2        ← gameTick
    //     IL_007B: stfld NudityMattersMore.PawnIdeoTracker.lastCheckTick
    //
    // 说白了就是「往一个 null 引用上写字段」。
    //
    // 【我们怎么修】
    //
    // 不重写、不顶替原方法，只在「必定会崩的那一种组合」下，把挡路的那条残留记录挪开：
    //
    //     __args[1]（也就是 tracker 参数）为 null  且  字典里已经有同 thingIDNumber 的记录
    //         → 把那条记录从字典里删掉
    //         → 原方法再执行时 ContainsKey 为 false，走「新建 tracker」那条分支，一切照旧
    //
    // 为什么删掉是对的、而不是「丢数据」：字典里那条记录属于上一局的同号 Pawn，
    // 那个 Pawn 早就随上一局消失了。新局的 Pawn 本来就该有一份干净的 tracker。
    // 删掉它不但修好这一次，还顺手让后面同号的 Pawn 不再反复踩这个坑。
    //
    // 【为什么只在 __args[1] == null 时才动手】
    //
    // 这个方法的调用点一共三个（已用反编译逐个核对）：
    //   * CoverBody.CoverCheck
    //   * InfoHelper.IsUncaring
    //   * Patch_PawnGenerator_NMM.NMMPawnInitialization   ← 只有这个传 null
    // 前两个传的 tracker 都是从字典里取出来的、必然非 null。
    //（CompTick 并不直接调用本方法，它是每 60 tick 调一次 CoverCheck，再由后者按 600 tick 的门槛调进来。）
    // 它们必须原样放行 —— 否则每次走到这里都会把 tracker 删掉重建，
    // 既丢状态又白白掉帧。
    // 而「字典里有这条记录」+「调用方传 null」这个组合只有 NMMPawnInitialization 会造成，
    // 所以判断是精确的，不会误伤别的调用点。
    //
    // 【为什么不直接引用 NudityMattersMore.dll】
    //
    // 两条原因：
    //   1. 这份 dll 压根没有放进 refs\，编译期就取不到它的类型。
    //      工程只为「确实要继承、或要直接调用其成员」的第三方 dll 才放 refs\
    //      （目前是 ElToro_BAddon 与 RJW 两份）；本补丁只需要判断一个对象是不是 null，
    //      不值得为此多背一份 dll 引用。
    //   2. 本补丁包必须能在「NMM 没装 / 被禁用」的情况下照常工作 ——
    //      走反射 + AccessTools.TypeByName，找不到目标就置位跳过、只打一条 Message，
    //      其余补丁一点都不受影响。
    //      （⚠ 别把它写成「一旦编译期引用了它，运行库枚举类型时就会整批失败」：
    //        那条口径 2026-10-08 已被推翻 —— 本程序集里引用了 RJW / ElToro 类型的那个类
    //        照样能被正常枚举出来，详见 LocalFixesMod.LoadAllTypesTolerantly 的注释。）
    // 所以这里全部走反射 + AccessTools.TypeByName：
    //   · 找不到 NudityMattersMore.CoverBody → 直接置位跳过，只打一条 Message；
    //   · 字典用非泛型 IDictionary 接口来操作（Dictionary<int,T> 本来就实现了它），
    //     既不认识 PawnIdeoTracker，也不需要认识。
    //
    // 【收尾器兜底：为什么光有前缀不够】
    //
    // UpdateIdeo 里还有两处同样没判空的解引用：
    //
    //     List<MemeDef> memes = pawn.ideo.Ideo.memes;
    //     foreach (MemeDef item in memes) { if (item.defName == "Nudism") ... }   // memes 里混了 null 就崩
    //     ...
    //     foreach (Precept item2 in list) { if (item2.def.defName == ...) }       // precept 的 def 为 null 就崩
    //
    // 而本机日志里正好有这两个来源：
    //     「Some ideoligion memes were null after loading.」（OAF_meme_Shubing 之类未启用模组的内容）
    //     「Some ideoligion precepts were null after loading.」（FO_Slavery_* / ExHumanCattle* 等）
    // 也就是说，只要某个文化里带着这类僵尸引用，生成它的成员时就会从这两处崩。
    //
    // 这两处不能在前缀里改：memes 是 Ideo 的公开字段，直接 RemoveAll 会永久改动游戏状态、
    // 牵动 meme 计数等其它系统；PreceptsListForReading 干脆是只读属性。
    // 而且它们真正的成因是文化数据本身，属于另一条线，不该由本补丁包顺手改掉。
    // 所以这里用 Finalizer 把空引用只在这一层吞掉：
    //   · 崩溃点发生在方法刚开头（memes / precepts 两个循环都在那儿），此时方法还没有
    //     写出任何**跟踪状态** —— tracker 那七个 bool 字段是最后几行才赋的。
    //     唯一可能已被写进去的是 lastCheckTick（它恰好排在 memes 循环之前），
    //     而那个字段被提前更新反而有利（限流计数往前走，不会反复刷同一条）。
    //     所以吞掉 == 「这一次不更新裸体状态」，与「原版跑完但没轮到赋值」没有区别；
    //   · 只吞 NullReferenceException，其它异常照旧往上报，绝不掩盖别的问题；
    //   · 正常路径（__exception == null）一行代码都不走，零开销；
    //   · 每吞一次记一条 Warning 并累计次数，最多报 8 条以免刷屏 —— 不静默。
    //
    // 【性能：为什么这个补丁可以放心挂着】
    //
    // 先看调用频率 —— 反编译 NudityMattersMore.dll 后可以看到，UpdateIdeo 一共只有三个调用点，
    // 其中两个「正常」路径都自带 600 tick 的门槛：
    //
    //     // CoverBody.CoverCheck（它自己由 CompTick 每 60 tick 调一次）
    //     int ticksGame = Find.TickManager.TicksGame;
    //     if ((float)(ticksGame - pawnIdeoTracker.lastCheckTick) > 600f)   // 600 tick = 10 秒
    //     {
    //         UpdateIdeo(pawn, pawnIdeoTracker, ticksGame);
    //     }
    //
    //     // InfoHelper.IsUncaring —— 同样带 600 tick 门槛
    //
    // 也就是说，每个 Pawn 最多 10 秒才走进来一次；极端到 500 个 Pawn 的殖民地也不过每秒几十次。
    // 而前缀最先执行的是 __args[1] != null 这一句比较，正常路径到这里就返回了 ——
    // 既不碰反射、也不碰字典。只有 NMMPawnInitialization 那条路径（__args[1] == null，
    // 也就是「正在生成一个殖民者」）才会继续往下走 FieldInfo.GetValue 和字典查找，
    // 那个频率是「每生成一个 Pawn 一次」。
    //
    // 再说代价 —— 这一条是实测，不是估计：
    // Harmony 会为「声明了 object[] __args 的补丁」在每次调用时现装一个参数数组
    //（本方法 3 个参数，其中 gameTick 是 int，要装箱）。在本机 net48 上量过：
    // 同样是字典查找，泛型 ContainsKey 每次约 4.5 纳秒，走 IDictionary 接口（object 键，装箱）
    // 每次约 12.5 纳秒，即每次多 8 纳秒。把这个数放到上面算出来的调用频率上，
    // 结果远在 RimWorld 每秒几 MB 的分配噪声之下。
    //
    // 所以这里刻意**保留** __args，没有改成「用 gameTick == 0 当指纹」那种更省的做法：
    // 后者要依赖「NMM 内部那 600 tick 门槛永远不变」这个我们管不着的假设，
    // 一旦它改了，前缀就会在错误的时机去动字典 —— 拿可靠性换 8 纳秒，不划算。
    internal static class NmmIdeoTrackerNullFix
    {
        // 目标类型/成员名。任何一步找不到，都按「模组没装」处理，绝不让本补丁包的其他部分受牵连。
        private const string CoverBodyTypeName = "NudityMattersMore.CoverBody";
        private const string TrackerManagerTypeName = "NudityMattersMore.PawnInteractionManager";
        private const string TrackerTypeName = "NudityMattersMore.PawnIdeoTracker";
        private const string TargetMethodName = "UpdateIdeo";
        private const string TrackerDictFieldName = "ideoTrackers";

        // NMM 的程序集「简单名」（不含 .dll 后缀）。用来在调用 AccessTools.TypeByName 之前
        // 先做一次极廉价的「它到底加载了没有」检查，理由见 Install() 里的长注释。
        private const string NmmAssemblySimpleName = "NudityMattersMore";

        // 幂等守卫：装成功之后立刻立起来。重复调用只会立刻返回
        //（Harmony 补丁是叠加的，装两层会让同一个方法跑两遍这里的逻辑）。
        private static bool installed;

        // 静态字典的字段句柄。拿不到就退化成「只有收尾器兜底」，并在安装时打一条 Warning。
        private static FieldInfo trackerDictField;

        // 日志限流：前缀和后缀都可能被高频调用，不能每次都刷。
        //
        // reportedKinds 的用法：同一个 PawnKind 只报一次，且总共最多报 MaxReportedKinds 个
        // 不同的 PawnKind（超过之后只累计次数）。理由是一次地图生成会批量创建上百个 Pawn，
        // 逐条打印会把日志刷爆。
        private static bool removalLogged;
        private static int removedCount;
        private static int swallowedCount;
        private static readonly HashSet<string> reportedKinds = new HashSet<string>();

        // 最多为多少个「不同的 PawnKind」打详细报告（够排查、又不刷屏）。
        private const int MaxReportedKinds = 8;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ── 先用最廉价、绝不会抛异常的方式确认 NMM 的程序集加载了没有 ──
                //
                // 为什么要多这一步：AccessTools.TypeByName 在「按完整全名找不到」时，
                // 最后会执行 AllTypes().ToArray() —— 那是「遍历全部已加载程序集的所有类型」，
                // 既有一次大数组分配 + LINQ，又可能因为**别的**模组存在加载不了的类型
                // 而抛 ReflectionTypeLoadException（那是 GetTypes() 的已知行为）。
                //
                // 真掉进下面的 catch 会怎样：catch 分支故意**不**置位 installed
                //（为的是「目标模组装了、只是这次没找到」时还能重试），于是每次构造本模组对象
                // 都会把这份昂贵的操作重跑一遍，日志里还会反复出现同一条 Error。
                //
                // 而 AppDomain.CurrentDomain.GetAssemblies() 只返回「已经加载成功」的程序集，
                // 本身不抛异常。NMM 没装 / 没启用 / 加载失败 —— 这三种情况下它的程序集
                // 都不会出现在那个列表里，我们在这里就干净地跳过：一条 Message，零成本。
                if (!IsAssemblyLoaded(NmmAssemblySimpleName))
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] Assembly " + NmmAssemblySimpleName + " is not loaded; "
                        + "the NudityMattersMore ideo-tracker null fix was skipped (that mod is probably not active).");
                    return;
                }

                Type coverBody = AccessTools.TypeByName(CoverBodyTypeName);
                if (coverBody == null)
                {
                    // NMM 不在（没装或被禁用）—— 这是正常情况，不是错误。
                    // 置位跳过，免得每次热重载都白找一遍。
                    installed = true;
                    Log.Message("[GNH LocalFixes] " + CoverBodyTypeName + " not found; "
                        + "the NudityMattersMore ideo-tracker null fix was skipped (that mod is probably not active).");
                    return;
                }

                // 优先按精确签名找：一旦 NMM 将来加了重载，也不至于打错目标。
                // 注意 AccessTools.Method 的第 4 个参数是「泛型类型参数」，不是返回类型 —— 非泛型方法必须传 null。
                Type trackerType = AccessTools.TypeByName(TrackerTypeName);
                MethodInfo target = null;
                if (trackerType != null)
                {
                    target = AccessTools.Method(coverBody, TargetMethodName,
                        new[] { typeof(Pawn), trackerType, typeof(int) }, null);
                }
                if (target == null)
                {
                    // 退路：按名字找（本方法在 NMM 里没有重载）。
                    target = AccessTools.Method(coverBody, TargetMethodName, null, null);
                }
                if (target == null)
                {
                    // NMM 在、方法却找不到 → 多半是它改了签名。
                    // 不置位，留给下次重试；并且必须留下 Error 线索，绝不静默失败。
                    Log.Error("[GNH LocalFixes] Could not find " + CoverBodyTypeName + "." + TargetMethodName
                        + "(Pawn, PawnIdeoTracker, int); the ideo-tracker null fix is deferred and will retry.");
                    return;
                }

                Type managerType = AccessTools.TypeByName(TrackerManagerTypeName);
                trackerDictField = (managerType != null)
                    ? AccessTools.Field(managerType, TrackerDictFieldName)
                    : null;
                if (trackerDictField == null)
                {
                    // 字典拿不到：前缀的核心动作做不了，但收尾器仍然值得装 ——
                    // 它还能挡住 memes / precepts 那两条 null 路径。
                    Log.Warning("[GNH LocalFixes] Could not locate " + TrackerManagerTypeName + "." + TrackerDictFieldName
                        + "; the stale-tracker removal step is disabled this session, "
                        + "but the NullReferenceException guard is still installed.");
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(NmmIdeoTrackerNullFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    // 收尾器：前缀没覆盖到的空引用（memes / precepts 那两条路径）由它吞掉，
                    // 绝不让它掀翻整条 Pawn 生成流程。
                    new HarmonyMethod(AccessTools.Method(typeof(NmmIdeoTrackerNullFix), nameof(Finalizer), null, null)));

                installed = true;
                Log.Message("[GNH LocalFixes] Patched " + CoverBodyTypeName + "." + TargetMethodName
                    + " (stale cross-save ideo-tracker entries are removed before the original runs,"
                    + " so newly opened games no longer die in pawn generation;"
                    + " a finalizer additionally catches the remaining NullReferenceExceptions).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the NudityMattersMore ideo-tracker fix (will retry): " + ex);
            }
        }

        // 判断某个程序集有没有被加载。
        //
        // 刻意不用 AccessTools 的任何东西：GetAssemblies() 只返回已加载成功的程序集，
        // GetName() 也只是读元数据，两者都不该抛异常（外面依旧有 try/catch 兜底）。
        private static bool IsAssemblyLoaded(string simpleName)
        {
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    Assembly asm = loaded[i];
                    if (asm == null)
                    {
                        continue;
                    }
                    AssemblyName name = asm.GetName();
                    if (name != null && string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 连枚举程序集都失败：当作「没加载」处理。最坏结果是跳过本修复，
                // 而不是让本补丁包的其他部分受到牵连。
            }
            return false;
        }

        // Prefix 不跳过原方法（本方法没有返回值可跳过），只把拦路的残留记录挪开。
        //
        // 整个判断都包在 try/catch 里：出任何意外就什么都不做、放行原版 ——
        // 我们的判断逻辑再怎么样也不该改变游戏行为，更不该把游戏弄崩。
        //
        // __args 是 Harmony 提供的「原方法参数数组」：
        //     __args[0] = pawn (Pawn)      __args[1] = tracker (PawnIdeoTracker，调用方可能传 null)
        //     __args[2] = gameTick (int)
        //
        // 为什么不用强类型写 `PawnIdeoTracker tracker`：那是第三方 dll 里的类型，
        // 本工程编译期引用不到它（一旦引用，NMM 没装时就编不过 —— 这正是本补丁要避免的）。
        // 用 __args 是这种「只想知道它是不是 null，又不想认识它的类型」场景下最直白的写法；
        // 代价是 Harmony 每次调用要现装一个 3 元素数组，实测频率下可忽略（见文件头性能一节）。
        // 这里只读它，不写回 —— 补丁的正确性不依赖「改 __args 能不能影响原方法」这条不确定的路径。
        public static void Prefix(Pawn pawn, object[] __args)
        {
            try
            {
                if (trackerDictField == null || pawn == null)
                {
                    return;
                }
                if (__args == null || __args.Length < 2)
                {
                    return;
                }

                // 调用方自己给了 tracker（CoverCheck / InfoHelper.IsUncaring 那两条路径）
                // → 与我无关，原样放行。
                if (__args[1] != null)
                {
                    return;
                }

                IDictionary trackers = trackerDictField.GetValue(null) as IDictionary;
                if (trackers == null)
                {
                    return;
                }

                int id = pawn.thingIDNumber;

                // 字典里没有这条记录 → 原方法自己会新建 tracker，不会崩，不必插手。
                //
                // （小知识：IDictionary.Contains(null) 会抛 ArgumentNullException —— 已实测确认。
                //   这里传的 id 是 int，装箱之后不可能是 null，所以踩不到这个坑；
                //   将来若有人把 id 改成引用类型，记得补一次 null 判断。）
                if (!trackers.Contains(id))
                {
                    return;
                }

                // 走到这里就是那条必然崩的组合：调用方传 null + 字典里已有一条同号残留。
                trackers.Remove(id);
                ReportRemoval(pawn, id);
            }
            catch (Exception)
            {
                // 前缀里绝不能往外抛 —— 那是把「修 bug 的代码」变成「制造 bug 的代码」。
                //
                // 这里**刻意不打日志**（本工程别处的 catch 都要求至少打一条）：
                //   · 本方法跑在 Pawn 生成路径上，而日志自身也可能抛（磁盘写满、
                //     日志文件被占用、被别的模组打坏了 Log）—— 在这里打日志等于多引入一个失败点；
                //   · 这一步失败 ==「没删掉那条残留」== 等同于本补丁没生效，
                //     真正的后果会由紧随其后的收尾器原样上报，那里才是该报错的地方。
            }
        }

        // 收尾器：只吞空引用，别的一律原样上报。
        public static Exception Finalizer(Exception __exception, Pawn pawn)
        {
            if (__exception == null)
            {
                return null;      // 正常路径：什么都不做
            }

            if (__exception is NullReferenceException)
            {
                ReportSwallowed(pawn);
                return null;      // 吞掉，不让它掀翻整条 Pawn 生成流程
            }

            return __exception;   // 其它异常照旧往上抛
        }

        // 报告「挪掉了一条残留记录」。同一个进程里只报一次，之后只累计次数。
        private static void ReportRemoval(Pawn pawn, int id)
        {
            try
            {
                removedCount++;
                if (removalLogged)
                {
                    return;
                }
                removalLogged = true;

                string kindName = "(未知)";
                try
                {
                    if (pawn != null && pawn.kindDef != null)
                    {
                        kindName = pawn.kindDef.defName;
                    }
                }
                catch (Exception)
                {
                    // 连 kindDef 都读不到就保持 "(未知)"，绝不能在补丁里再抛一次。
                }

                Log.Message("[GNH LocalFixes] NudityMattersMore：已清掉一条跨局残留的 ideo tracker 记录"
                    + "（thingIDNumber=" + id + "，PawnKind=" + kindName + "），让 UpdateIdeo 改走「新建 tracker」分支。"
                    + "成因是 NMM 的静态字典 ideoTrackers 跨局不清空、而新局 thingIDNumber 从头计数造成的 ID 复用；"
                    + "不清掉的话，每次生成同号殖民者都会抛空引用并可能卡死开局。"
                    + "累计已清理 " + removedCount + " 条。");
            }
            catch (Exception)
            {
                // 记日志失败也不能再抛。
            }
        }

        // 报告「兜住了一次空引用」。同一 PawnKind 只报一次，最多报 8 个不同的 PawnKind，之后只记数。
        private static void ReportSwallowed(Pawn pawn)
        {
            try
            {
                swallowedCount++;

                string kindName = "(未知)";
                try
                {
                    if (pawn != null && pawn.kindDef != null)
                    {
                        kindName = pawn.kindDef.defName;
                    }
                }
                catch (Exception)
                {
                    // 同上，保持 "(未知)"。
                }

                if (reportedKinds.Contains(kindName) || reportedKinds.Count >= MaxReportedKinds)
                {
                    return;   // 报过了 / 报够了，只记数
                }
                reportedKinds.Add(kindName);

                Log.Warning("[GNH LocalFixes] 已兜住 NudityMattersMore.CoverBody.UpdateIdeo 的空引用"
                    + "（PawnKind=" + kindName + "）。"
                    + "崩溃点在方法刚开头、还没有写出跟踪状态（lastCheckTick 可能已更新，那反而有利），"
                    + "所以被兜住的结果 == 「这一次不更新裸体状态」，"
                    + "不会留下半截数据。若此处持续出现，通常是某个文化里带着 def 已不存在的"
                    + "precept/meme 僵尸引用（日志里的「Some ideoligion precepts/memes were null after loading.」），"
                    + "那属于文化数据本身的问题。累计已兜住 " + swallowedCount + " 次。");
            }
            catch (Exception)
            {
                // 收尾器里绝不能因为「记日志」再抛异常。
            }
        }
    }
}
