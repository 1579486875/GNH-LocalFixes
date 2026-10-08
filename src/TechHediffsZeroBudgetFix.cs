using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：RimWorld 原版 PawnTechHediffsGenerator.GenerateTechHediffsFor
    // 在「植入体预算恰好为 0」时会以 0 权重抽取元素、拿到 null，然后解引用崩溃。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // 原版这段逻辑还原成 C# 是这样（1.6.4871）：
    //
    //     float partsMoney = pawn.kindDef.techHediffsMoney.RandomInRange;   // 预算
    //     int num = pawn.kindDef.techHediffsMaxAmount;                      // 要装几个
    //     if (pawn.kindDef.techHediffsRequired != null) { ...装着必装件、扣预算... }
    //     if (pawn.kindDef.techHediffsTags == null || pawn.kindDef.techHediffsChance <= 0f)
    //         return;
    //     for (int i = 0; i < num; i++) {
    //         if (Rand.Value > pawn.kindDef.techHediffsChance) continue;
    //         var source = DefDatabase<ThingDef>.AllDefs.Where(x => x.isTechHediff
    //             && x.BaseMarketValue <= partsMoney && ...);      // ← 注意这里用预算做门槛
    //         if (source.Any()) {
    //             ThingDef thingDef = source.RandomElementByWeight(w => w.BaseMarketValue);
    //             partsMoney -= thingDef.BaseMarketValue;      // ← 崩溃点
    //             InstallPart(pawn, thingDef);
    //             tmpGeneratedTechHediffsList.Add(thingDef);
    //         }
    //     }
    //
    // 关键在 GenCollection.RandomElementByWeight 的边界行为：它先求总权重，
    // 若总权重不大于 0，就**不抽签**，而是
    //
    //     Log.Error("RandomElementByWeight with totalWeight=0 - use TryRandomElementByWeight.");
    //     return default(T);          // 对引用类型就是 null
    //
    // 所以只要「预算 == 0」而且候选里存在 BaseMarketValue == 0 的植入体，
    // 筛选条件 BaseMarketValue <= partsMoney 会把它们全放进来，
    // 但它们每一个的权重又都是 0 → 总权重 0 → 返回 null → 下一行解引用直接空引用异常。
    //
    // 实测日志（2026-10-04 10:39:12，三条连在一起正好是完整的因果链）：
    //
    //     ERROR: RandomElementByWeight with totalWeight=0 - use TryRandomElementByWeight.
    //     ERROR: Error while generating pawn. Rethrowing. Exception: System.NullReferenceException
    //     ERROR: at RimWorld.PawnTechHediffsGenerator.GenerateTechHediffsFor (Verse.Pawn pawn)
    //
    // 【为什么不能去怪 RavenRace】
    //
    // 那条堆栈里还会带一行
    //     - TRANSPILER ZuoYao.RavenRace ... Patch_FusangFluidImplantPreference:Transpiler
    // 因为 RavenRace 也改了这同一个调用点：它把 RandomElementByWeight 换成了
    // 自己的 ChooseImplant(...)，而 ChooseImplant 的最后一行 fallback 仍然是
    //     return source.RandomElementByWeight(weight);
    // 也就是说原版这个缺陷被完整保留了下来，RavenRace 只是恰好出现在同一条堆栈上。
    // 结论：这是**原版的边界缺陷**，任何模组只要给出「预算为 0 + 零价值植入体」
    // 这个组合就会踩中，不是某一家模组的锅。
    //
    // 【我们怎么修，以及为什么这样修是安全的】
    //
    // 不重写、不顶替原方法，只在**必定会崩的那一种配置**下提前放行（返回 false 跳过）：
    //
    //     候选里一个「权重为正」的项都没有（也就是原版这一趟必然抽到 null）
    //
    //     【2026-10-06 审计更正】这里原先写的是「预算上限 <= 0」，那是**过期说法** ——
    //     本文件下面「关键判断：原版这一趟到底能不能抽出东西来」那一整段
    //     记录了「光判预算上限还不够」的那次实机教训。
    //     真正的判据是「**候选里有没有一个权重为正的项**」——
    //     预算 > 0 时同样可能一个都没有（见下面「反过来说，下面这些情况我们一律放行」
    //     那一段里带 ⚠ 的那条）。
    //     （2026-10-08 更正：这里原先写的是「见下面第 84 行」—— 行号被后来的编辑推走了，
    //       此后一律改用文字锚点，不再写行号。）
    //
    // 为什么跳过不损失任何功能：预算恒为 0 或负数时，本来就只有
    // BaseMarketValue == 0 的植入体能通过门槛，而它们在原版里恰恰是
    // 「抽不中（返回 null）+ 崩溃」的那一批 —— 正常游戏里从来装不上。
    // 所以跳过 == 「一个植入体都不装」，与应有的正确行为完全一致。
    //
    // 反过来说，下面这些情况我们一律**放行**，原版逻辑照跑：
    //   * techHediffsTags 为空 —— 候选集本来就是空的；
    //   * techHediffsChance <= 0 —— 原版自己会提前 return；
    //   * techHediffsMaxAmount <= 0 —— 原版那个循环根本不会执行；
    //   * 有必装植入体（techHediffsRequired）—— 会被扣成负数，候选为空，不会崩，
    //     而且**绝不能跳过**，否则连必装件都装不上了；
    //   * 预算上限 > 0 **并且**候选里至少存在一个「权重为正（BaseMarketValue > 0）」的项
    //     —— 交给原版正常抽签。
    //     ⚠ 光看「预算 > 0」是不够的：预算为正、候选却全是零价值植入体时，
    //       原版照样会抽到 null 然后崩 —— 那属于上面「必定会崩」那一类，我们要拦。
    //       （2026-10-07 订正：本行原先只写「预算上限 > 0」，与代码不符，也和上面那段更正打架。）
    //
    // 唯一可察觉的差异：跳过时原版那句 tmpGeneratedTechHediffsList.Clear() 不会执行。
    // 那是方法内部用来避免重复挑选的临时静态列表，每次进入方法开头都会清一次，
    // 所以少清这一次对后续调用没有任何影响。
    //
    // 【安装时机与探测】
    //
    // 在 LocalFixesMod 构造函数里通过 TryInstall 安装，早于任何 pawn 生成。
    // 目标类型是游戏本体类型，不依赖任何模组；万一将来原版改了方法签名，
    // 我们打一条 Error 并保留重试，绝不静默失败。
    internal static class TechHediffsZeroBudgetFix
    {
        private const string GeneratorTypeName = "RimWorld.PawnTechHediffsGenerator";
        private const string TargetMethodName = "GenerateTechHediffsFor";

        private static bool installed;

        // 只在第一次真正跳过时打一条日志：pawn 生成很频繁，
        // 每次都打会把日志淹掉，反而看不出别的问题。
        private static bool skipLogged;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type type = AccessTools.TypeByName(GeneratorTypeName);
                if (type == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] PawnTechHediffsGenerator not found; zero-budget fix was skipped.");
                    return;
                }

                MethodInfo target = AccessTools.Method(type, TargetMethodName, new[] { typeof(Pawn) }, null);
                if (target == null)
                {
                    // 装了游戏却找不到方法 → 多半是原版改了签名。
                    // 不置位，留给下次重试；并且必须留下 Error 线索。
                    Log.Error("[GNH LocalFixes] Could not find "
                        + GeneratorTypeName + "." + TargetMethodName
                        + "(Pawn); zero-budget fix deferred and will retry.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(TechHediffsZeroBudgetFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    // 收尾器：前缀没拦住的（预算随机抽签、同一只 Pawn 的第二轮挑选），
                    // 由它把空引用异常吞掉，绝不让它掀翻整条 Pawn 生成流程。
                    new HarmonyMethod(AccessTools.Method(typeof(TechHediffsZeroBudgetFix), nameof(Finalizer), null, null)));

                installed = true;
                Log.Message("[GNH LocalFixes] Patched " + GeneratorTypeName + "." + TargetMethodName
                    + " (zero-budget pawn kinds no longer hit the totalWeight=0 NullReferenceException;"
                    + " a finalizer catches the random-budget leftovers).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the tech-hediff zero-budget fix (will retry): " + ex);
            }
        }

        // Prefix 返回 false 表示「跳过原方法」。
        //
        // 整个判断都包在 try/catch 里，出任何意外就返回 true 放行原版 ——
        // 我们的判断逻辑再怎么样也不该改变游戏行为，更不该把游戏弄崩。
        public static bool Prefix(Pawn pawn)
        {
            try
            {
                if (pawn == null)
                {
                    return true;
                }

                PawnKindDef kind = pawn.kindDef;
                if (kind == null)
                {
                    return true;
                }

                // 候选集必然为空，原版抽不到东西。
                if (kind.techHediffsTags == null || kind.techHediffsTags.Count == 0)
                {
                    return true;
                }

                // 原版自己就会提前 return。
                if (kind.techHediffsChance <= 0f)
                {
                    return true;
                }

                // 原版那个 for 循环一次都不会执行。
                if (kind.techHediffsMaxAmount <= 0)
                {
                    return true;
                }

                // 有必装植入体：预算会被扣成负数，候选为空，不会崩；
                // 而且这条路径必须让原版跑，否则必装件就装不上了。
                if (kind.techHediffsRequired != null && kind.techHediffsRequired.Count > 0)
                {
                    return true;
                }

                // ── 关键判断：原版这一趟到底能不能抽出东西来 ──
                //
                // 早先这里只判「预算上限 <= 0」，而 2026-10-04 的实机日志证明**不够**：
                //
                //     RandomElementByWeight with totalWeight=0
                //     NullReferenceException at GenerateTechHediffsFor [0x001ac]
                //       - PREFIX gnh.cn.cys.localfixes: TechHediffsZeroBudgetFix:Prefix   ← 我们被调用了
                //
                // 我们被调用、却返回了 true 放行 —— 说明那个 PawnKindDef 的预算**大于 0**，
                // 可候选的**权重和仍然是 0**。原因只能有一个：
                // 参与抽签的 ThingDef 里存在 BaseMarketValue == 0 的项（权重函数就是它），
                // 而预算是正的、把它们全放了进来，于是总权重 0 → 返回 null → 解引用崩溃。
                //
                // 所以正确的判断不是「预算多大」，而是「**有没有一个权重为正的候选**」。
                // 下面就在候选列表里找一遍：找到一个能用的就放行原版；一个都没有才跳过。
                //
                // 这么做不损失任何功能：原版在这种情况下的实际结果是「崩溃」，
                // 而我们跳过 = 「这次不装植入体」。就"该 Pawn 的装备"而言两者都是「没装上」，
                // 区别只在于我们不会让整条生成流程炸掉。
                float budget = kind.techHediffsMoney.max;
                if (HasViableCandidate(pawn, kind, budget))
                {
                    return true;
                }

                // 走到这里：没有必装件，而候选里一个「权重为正」的项都没有。
                // 注意此时 budget **可能大于 0**（见上面「关键判断：原版这一趟到底能不能
                // 抽出东西来」那段教训：预算为正、候选却全是零价值植入体时同样会崩），
                // 所以别再把它当成「预算 <= 0」的条件分支。
                // 这正是「总权重 0 → 返回 null → 解引用崩溃」的唯一配置。
                if (!skipLogged)
                {
                    skipLogged = true;
                    Log.Message("[GNH LocalFixes] Skipped tech-hediff generation for pawn kind \""
                        + kind.defName + "\" (budget=" + budget
                        + ", but no tech hediff with a positive market value matches): this is the "
                        + "configuration that used to throw the totalWeight=0 NullReferenceException.");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] zero-budget tech-hediff check failed; letting the original method run: " + ex.Message);
                return true;
            }
        }

        // ==================================================================
        // 收尾器（Finalizer）：把前缀没拦住的空引用异常吞掉
        // ==================================================================
        //
        // 【为什么光有前缀还不够 —— 2026-10-04 19:12 的实机日志给的答案】
        //
        // 那次日志长这样（三条连在一起，是一条完整的因果链）：
        //
        //     ERROR: RandomElementByWeight with totalWeight=0 - use TryRandomElementByWeight.
        //     ERROR: Error while generating pawn. Rethrowing. Exception: NullReferenceException
        //     at RimWorld.PawnTechHediffsGenerator.GenerateTechHediffsFor (...) [0x001ac]
        //       - PREFIX gnh.cn.cys.localfixes: TechHediffsZeroBudgetFix:Prefix   ← 我们被调用了
        //
        // 我们**被调用了**却放行了，说明 `HasViableCandidate` 判断「有能用的候选」，
        // 可原版那一趟的实际结果仍然是「总权重 0」。反编译原方法后原因很清楚：
        //
        //     float partsMoney = pawn.kindDef.techHediffsMoney.RandomInRange;   // ← 随机抽一次！
        //     ...
        //     x.BaseMarketValue <= partsMoney                                   // ← 用抽到的值做门槛
        //
        // 而前缀里用的是 `kind.techHediffsMoney.max`，也就是**可能抽到的最大值**。
        // 只要实际抽到的小于「最便宜的那个正市值候选」，候选里就只剩市值为 0 的项，
        // 权重和 0 → RandomElementByWeight 返回 null → 下一行解引用崩溃。
        //
        // 还有第二个漏洞：原版的筛选条件里有一句
        //     !tmpGeneratedTechHediffsList.Contains(x)
        // 「同一只 Pawn 已经装过的不要再挑」。所以哪怕第一轮挑到了好东西，
        // 第二轮（techHediffsMaxAmount > 1 时）候选可能已经被掏空成只剩 0 权重项。
        // 前缀那份清单不知道这件事。
        //
        // 这两件事都**没法在前缀里可靠预测**：预算要抽签才知道，而抽签会用掉 Rand 状态
        // （我们不能替它抽，那会改变后面所有随机数 —— 代价远大于这个 bug 本身）。
        //
        // 【所以改成「不预测，兜底」】
        //
        // 反编译原文里，从 null 到崩溃只隔一行：
        //
        //     ThingDef thingDef = source.RandomElementByWeight((ThingDef w) => w.BaseMarketValue);
        //     partsMoney -= thingDef.BaseMarketValue;      // ← 崩在这里
        //     InstallPart(pawn, thingDef);                 // ← 还没执行到
        //
        // 也就是说：崩溃发生时**什么都没被改坏** —— partsMoney 没动、植入体没装。
        // 原版真正想要的语义本来就是「这次抽不中就不装」（它自己的报错信息都在教人用
        // TryRandomElementByWeight）。所以我们在这里吞掉异常，
        // 得到的结果与「原版用对了 API」完全一致，只是少装了这一个植入体。
        //
        // 【安全边界】
        //
        //   · 只吞 NullReferenceException；其它任何异常原样抛出，绝不掩盖别的问题。
        //   · 正常路径（__exception == null）一行代码都不走，零开销。
        //   · 每吞一次都会记一条 Warning，并写明是什么 PawnKind —— 不静默。
        //     （只对前若干个不同的 PawnKind 记，免得刷屏。）
        //   · Harmony 约定：Finalizer 返回 null 表示「已处理，按默认值返回」；
        //     该方法返回 void，所以就是「安静地结束这次调用」。
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

            return __exception;   // 其它异常照旧往上报
        }

        // 已经报过的 PawnKind 名字，以及已经报过几个了。
        //
        // 为什么会限流：一次地图生成会批量创建上百个 Pawn，如果每个都打一条详细报告，
        // 日志会被刷爆。这里「同一个 PawnKind 只报一次」，且总共最多报 MaxReportedKinds 个
        // 不同的 PawnKind；超过之后只累计次数，不再逐条打印。
        private static readonly HashSet<string> reportedKinds = new HashSet<string>();
        private static int swallowedCount;

        // 最多为多少个「不同的 PawnKind」打详细报告。
        // 取 8 的理由：足够留下可排查的样本，又不会把日志刷屏。
        private const int MaxReportedKinds = 8;

        // 报一条 Warning，说明「这里本来会崩，被我们兜住了」。
        // 同一个 PawnKind 只报一次；最多报 8 个不同的 PawnKind，之后只记数不刷屏。
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
                    // 连 kindDef 都读不到就保持 "(未知)"，绝不能在收尾器里再抛一次。
                }

                if (reportedKinds.Contains(kindName) || reportedKinds.Count >= MaxReportedKinds)
                {
                    return;   // 报过了 / 报够了，只记数
                }
                reportedKinds.Add(kindName);

                Log.Warning("[GNH LocalFixes] 已兜住 PawnTechHediffsGenerator 的「总权重 0」空引用"
                    + "（PawnKind=" + kindName + "）。"
                    + "这是原版用 RandomElementByWeight 抽 0 权重候选拿到 null 的边界缺陷；"
                    + "被兜住的结果 == 原版本来的语义「这次抽不中，不装这个植入体」，"
                    + "没有装到一半的残留（崩溃点在 InstallPart 之前）。"
                    + "累计已兜住 " + swallowedCount + " 次。");
            }
            catch (Exception)
            {
                // 收尾器里绝不能因为「记日志」再抛异常。
            }
        }

        /// <summary>
        /// 缓存下来的「有正市值的 tech hediff」清单。
        ///
        /// 为什么要缓存：候选筛选要遍历全部 ThingDef（本机一万多条），
        /// 而下面那个判断**每次生成 Pawn 都会被调用一次** —— 每次都全表扫太亏。
        /// 这些 Def 在启动之后基本不变，构建一次就够了。
        /// （⚠ 开发者热重载 Def 之后这份缓存**不会自动重建**：万一热重载后新出现了一个
        ///   「权重为正」的候选，我们仍然按旧清单判断，**可能误跳过**一次本该正常生成的
        ///   植入体 —— 那不是「退回旧行为」，而是「该装的没装」。
        ///   权衡后接受这点代价（只影响开发者热重载之后的那一次生成），不做失效检测；
        ///   真怀疑踩到了，重进游戏即可 —— 这份缓存活不过一个进程。）
        /// </summary>
        private static List<ThingDef> viableTechHediffs;
        /// <summary>
        /// 判断给定 PawnKindDef 有没有「能真正抽中」的 tech hediff 候选。
        ///
        /// 复刻的是原版 GenCollection.RandomElementByWeight 的**权重视角**：
        /// 权重函数是 w =&gt; w.BaseMarketValue，所以市值为 0 的项对总权重没有任何贡献。
        /// 原版只看「有没有候选」（source.Any()），**完全不看权重和** —— 这正是它崩的原因。
        /// 我们这里多看一眼：有没有**权重为正**的候选。
        ///
        /// 只做「有 / 没有」的判断，不代替原版抽签：
        /// 只要有一个能用的就放行，具体抽中谁仍然完全交给原版。
        /// 所以这不是「替原版做决定」，只是「提前发现它这一趟必然会崩」。
        /// </summary>
        private static bool HasViableCandidate(Pawn pawn, PawnKindDef kind, float budget)
        {
            EnsureViableList();
            if (viableTechHediffs == null || viableTechHediffs.Count == 0)
            {
                // 清单没建起来（异常）：保守放行，让原版按老样子跑。
                // 宁可偶尔崩一次，也不要因为我们的缓存出问题而误跳过正常生成。
                return true;
            }

            for (int i = 0; i < viableTechHediffs.Count; i++)
            {
                ThingDef candidate = viableTechHediffs[i];

                if (candidate.BaseMarketValue > budget)
                {
                    continue;
                }
                // 这里刻意用显式 for 而**不用** LINQ 的 .Any(tag => ...)：
                // 这段代码每个生成的 Pawn 都会跑一遍，而 lambda 会在循环内捕获 candidate
                //（编译器要生成 display class + 委托），List<string> 经 IEnumerable<T> 调 Any
                // 还会把 List 的枚举器装箱 —— 合计每个候选最多 6 次堆分配。
                // 下面这两个 TagListsOverlap 是零分配版本。
                if (!TagListsOverlap(kind.techHediffsTags, candidate.techHediffsTags))
                {
                    continue;
                }
                if (kind.techHediffsDisallowTags != null
                    && TagListsOverlap(kind.techHediffsDisallowTags, candidate.techHediffsTags))
                {
                    continue;
                }
                if (candidate.violentTechHediff && pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    continue;
                }

                return true;   // 找到一个能用的就够了
            }
            return false;
        }

        /// <summary>
        /// 两个字符串列表有没有交集。
        ///
        /// 语义等价于 a.Any(x =&gt; b.Contains(x))，但**零堆分配** ——
        /// 这段代码在「每生成一个 Pawn」的路径上，不能用 LINQ（理由见调用处注释）。
        /// 任一列表为 null 都按「无交集」处理（调用方已经先判过 null）。
        /// </summary>
        private static bool TagListsOverlap(List<string> a, List<string> b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (b.Contains(a[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 构建上面那份候选清单，只在第一次用到时做一次。
        /// </summary>
        private static void EnsureViableList()
        {
            if (viableTechHediffs != null)
            {
                return;
            }

            try
            {
                List<ThingDef> built = new List<ThingDef>();
                List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    ThingDef def = all[i];
                    if (def == null || !def.isTechHediff)
                    {
                        continue;
                    }
                    // 市值为 0 的**一律不收**：它们正是把总权重拖到 0 的元凶。
                    // 市值取不到（抛异常）也当 0 处理 —— 同样是抽不中的项。
                    float value;
                    try
                    {
                        value = def.BaseMarketValue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (value <= 0f)
                    {
                        continue;
                    }
                    if (def.techHediffsTags == null || def.techHediffsTags.Count == 0)
                    {
                        continue;
                    }
                    built.Add(def);
                }
                viableTechHediffs = built;
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Could not build the viable tech-hediff list; "
                    + "the zero-weight guard will let the original method run this session: " + ex.Message);
                // 保持 null：下次再试。调用方已经把 null 当"放行"处理。
            }
        }
    }
}
