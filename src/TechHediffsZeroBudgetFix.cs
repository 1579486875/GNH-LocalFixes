using System;
using System.Collections.Generic;
using System.Linq;
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
    //     预算上限 <= 0  且  没有必装植入体  且  原版确实会走进那个循环
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
    //   * 预算上限 > 0 —— 交给原版正常抽签。
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
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] Patched " + GeneratorTypeName + "." + TargetMethodName
                    + " (zero-budget pawn kinds no longer hit the totalWeight=0 NullReferenceException).");
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

                // 走到这里：预算恒 <= 0、没有必装件、而原版确实会进入抽取循环。
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

        /// <summary>
        /// 缓存下来的「有正市值的 tech hediff」清单。
        ///
        /// 为什么要缓存：候选筛选要遍历全部 ThingDef（本机一万多条），
        /// 而下面那个判断**每次生成 Pawn 都会被调用一次** —— 每次都全表扫太亏。
        /// 这些 Def 在启动之后基本不变，构建一次就够了。
        /// （开发者热重载 Def 之后这份缓存会过期，最坏结果只是"退回旧行为"，
        ///   不会造成新的错误，所以不额外做失效检测。）
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
                if (!kind.techHediffsTags.Any(tag => candidate.techHediffsTags.Contains(tag)))
                {
                    continue;
                }
                if (kind.techHediffsDisallowTags != null
                    && kind.techHediffsDisallowTags.Any(tag => candidate.techHediffsTags.Contains(tag)))
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
