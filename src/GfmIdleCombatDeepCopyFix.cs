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
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 第 26 项：修掉「Guards For Me」模组的一处类型转换错误。
    // ==========================================================================
    //
    // 【它是谁】
    //
    //   Guards For Me（作者 aRandomKiwi，工坊 1855885448，packageId = aRandomKiwi.GuardsForMe，
    //   当前版本 3.0.10）给小人加了「巡逻卫兵 / 贴身保镖 / 定点站岗 / 快速反应部队」四种守卫工作。
    //   它的程序集是 1.6\Assemblies\GuardsForMe.dll，内部命名空间叫 aRandomKiwi.GFM。
    //
    // 【症状】
    //
    //   每次进游戏（Def 加载阶段）都会在日志里留下这几条红字：
    //
    //     Error while resolving references for def Nivarian_ThinkTree:
    //         System.InvalidCastException: Specified cast is not valid.
    //     Error while resolving references for def Nivarian_MechUnit_ThinkTree: ... 同上 ...
    //     Error while resolving references for def PAS_PoweredApparelMaintenance: ... 同上 ...
    //
    //   堆栈（日志实测原文）：
    //
    //     at aRandomKiwi.GFM.JobGiver_IdleCombat.DeepCopy (System.Boolean resolve) [0x00008]
    //     at Verse.AI.ThinkNode.DeepCopy (System.Boolean resolve) [0x0002b]
    //     at Verse.AI.ThinkNode_Conditional.DeepCopy (System.Boolean resolve) [0x00000]
    //     ...
    //     at Verse.AI.ThinkNode_Subtree.ResolveSubnodes ()
    //     at Verse.AI.ThinkNode.ResolveSubnodesAndRecur ()
    //     at Verse.ThinkTreeDef.ResolveReferences ()
    //
    // 【根因（反编译 + IL 双重核对）】
    //
    //   GFM 的 JobGiver_IdleCombat.DeepCopy 是这样的（反编译原文）：
    //
    //       public override ThinkNode DeepCopy(bool resolve = true)
    //       {
    //           JobGiver_Idle val = (JobGiver_Idle)((ThinkNode_JobGiver)this).DeepCopy(resolve);
    //           val.ticks = ticks;
    //           return (ThinkNode)(object)val;
    //       }
    //
    //   对照原版 Verse.AI.JobGiver_Idle.DeepCopy：
    //
    //       public override ThinkNode DeepCopy(bool resolve = true)
    //       {
    //           JobGiver_Idle obj = (JobGiver_Idle)base.DeepCopy(resolve);
    //           obj.ticks = ticks;
    //           return obj;
    //       }
    //
    //   一眼可见：GFM 这段是【从原版 JobGiver_Idle 复制过来的】——
    //   连变量名都只是把 obj 改成了 val，唯独【忘了改强转的目标类型】。
    //
    //   为什么这一忘就必然崩？看 IL（实测反汇编）：
    //
    //       IL_0003: call      Verse.AI.ThinkNode_JobGiver.DeepCopy
    //       IL_0008: castclass Verse.AI.JobGiver_Idle          <-- [0x00008]，报错的就是这一条
    //       IL_0010: ldfld     aRandomKiwi.GFM.JobGiver_IdleCombat.ticks
    //       IL_0015: stfld     Verse.AI.JobGiver_Idle.ticks
    //
    //   IL_0003 那句 call 的目标是 ThinkNode_JobGiver，说明
    //   JobGiver_IdleCombat 的【直接基类就是 ThinkNode_JobGiver】，它和
    //   JobGiver_Idle 是【兄弟关系】（两者都直接继承 ThinkNode_JobGiver），
    //   【不是父子关系】。所以 castclass 到 JobGiver_Idle 必然失败。
    //
    //   更决定性的是原版 ThinkNode.DeepCopy 的实现：
    //
    //       public virtual ThinkNode DeepCopy(bool resolve = true)
    //       {
    //           ThinkNode thinkNode = (ThinkNode)Activator.CreateInstance(GetType());
    //           ...
    //       }
    //
    //   GetType() 取的是【运行时类型】，也就是 JobGiver_IdleCombat 本身。
    //   所以 base.DeepCopy(resolve) 返回的实例【永远是 JobGiver_IdleCombat】，
    //   castclass 成 JobGiver_Idle 在任何情况下都不可能成功。
    //
    // 【影响不只是日志噪音】
    //
    //   抛异常的这条链是 ResolveSubnodes —— 它的职责是把 think tree 里
    //   【通过 Subtree 引用的另一棵 think tree 的节点深拷贝进来】。
    //   异常在这里抛出，等于【整棵子树都没被复制进来】。
    //   也就是说 Nivarian_ThinkTree / Nivarian_MechUnit_ThinkTree /
    //   PAS_PoweredApparelMaintenance 这三棵行为树是不完整的，
    //   用到它们的种族（涅瓦莲、PasserRace）的 AI 行为会实实在在地缺失。
    //
    // 【修法：用 Transpiler 只改两条指令】
    //
    //   「Transpiler」是 Harmony 的一种补丁类型。它不改方法的 C# 源码，
    //   而是在【补丁安装的那一刻】把方法的 IL（.NET 的中间指令表）逐条过一遍，
    //   允许你替换其中某几条。这里只做两处等价替换：
    //
    //     1) castclass Verse.AI.JobGiver_Idle
    //          改 -> castclass aRandomKiwi.GFM.JobGiver_IdleCombat
    //     2) stfld     Verse.AI.JobGiver_Idle.ticks
    //          改 -> stfld     aRandomKiwi.GFM.JobGiver_IdleCombat.ticks
    //
    //   改完就等价于作者本来想写的那份代码：
    //
    //       JobGiver_IdleCombat val = (JobGiver_IdleCombat)base.DeepCopy(resolve);
    //       val.ticks = ticks;
    //       return val;
    //
    //   为什么【必须两条一起改】：改完第 1 条之后，栈上那个对象的静态类型变成了
    //   JobGiver_IdleCombat；若第 2 条仍然写 JobGiver_Idle.ticks，就等于
    //   拿一个「并非 JobGiver_Idle」的对象去访问 JobGiver_Idle 的字段，
    //   校验器会直接判定这段 IL 非法。两条一起改才自洽。
    //
    // 【安全设计（本工程的纪律）】
    //
    //   1. Transpiler 【先整表扫描、确认各命中恰好一次，之后才动手改】。
    //      因为 CodeInstruction 是引用对象，边扫边改会在「只找到一条」时
    //      留下半成品 IL。扫描不通过就一个字节都不动、原样返回。
    //   2. 如果作者后续修好了这个 bug（castclass 已经指向 JobGiver_IdleCombat），
    //      我们就会「一条都找不到」-> 自动退化为什么都不做，
    //      绝不会把已经正确的代码改坏。这是本补丁可以长期留在包里的前提。
    //   3. installed 静态守卫 —— 本工程纪律：补丁安装必须幂等
    //      （LoadedModManager.CreateModClasses 一局内会被多次调用）。
    //   4. 对 GFM 的 dll 零编译期依赖：类型、方法、字段全部走字符串 + 反射查找，
    //      GFM 不在场时只打一条 Message 就跳过，不影响其它任何修复项。
    //   5. 只碰这一个方法，不改变 GFM 的任何玩法逻辑。
    //
    // 【给作者的修复建议（源码级，改一个词就行）】
    //
    //   在 JobGiver_IdleCombat.cs 里把 DeepCopy 改成：
    //
    //       public override ThinkNode DeepCopy(bool resolve = true)
    //       {
    //           JobGiver_IdleCombat val = (JobGiver_IdleCombat)base.DeepCopy(resolve);
    //           val.ticks = ticks;
    //           return val;
    //       }
    //
    //   即把 val 的声明类型与强转类型从 JobGiver_Idle 换成 JobGiver_IdleCombat，
    //   其余一个字都不用动。
    // ==========================================================================
    public static class GfmIdleCombatDeepCopyFix
    {
        /// <summary>
        /// 补丁目标类型名。用字符串而不是 typeof：本包不允许对 GFM 的 dll 产生编译期依赖，
        /// 否则「谁不在场都不影响别人」这条原则就破了。
        /// </summary>
        private const string TargetTypeName = "aRandomKiwi.GFM.JobGiver_IdleCombat";

        /// <summary>补丁目标方法名。</summary>
        private const string TargetMethodName = "DeepCopy";

        /// <summary>
        /// 原版那个「被误当成父类」的类型名。
        /// 我们并不修改它，只用它来【认出】需要替换的那两条指令。
        /// </summary>
        private const string IdleTypeName = "Verse.AI.JobGiver_Idle";

        /// <summary>本补丁是否已经装过。Harmony 补丁是叠加的，装两次会把同一段 IL 处理两遍。</summary>
        private static bool installed;

        /// <summary>原版 Verse.AI.JobGiver_Idle 类型（用于识别待替换的指令）。</summary>
        private static Type idleType;

        /// <summary>GFM 的 JobGiver_IdleCombat 类型（替换后的目标类型）。</summary>
        private static Type combatType;

        /// <summary>GFM 自己的 ticks 字段（替换后的目标字段）。</summary>
        private static FieldInfo combatTicksField;

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
                combatType = AccessTools.TypeByName(TargetTypeName);
                if (combatType == null)
                {
                    // 没装 Guards For Me —— 这是正常情况，不是错误。
                    installed = true;
                    Log.Message("[GNH LocalFixes] Guards For Me is not present; its DeepCopy fix was skipped"
                        + " (nothing to do, other fixes are unaffected).");
                    return;
                }

                idleType = AccessTools.TypeByName(IdleTypeName);
                if (idleType == null)
                {
                    // 原版类型都找不到，说明游戏版本不对 —— 不置位 installed，留给下次重试。
                    Log.Error("[GNH LocalFixes] Guards For Me is present but " + IdleTypeName
                        + " could not be found; its DeepCopy fix is deferred and will retry.");
                    return;
                }

                // 目标方法的签名是 DeepCopy(bool resolve)。
                MethodInfo target = AccessTools.Method(combatType, TargetMethodName, new[] { typeof(bool) });
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] Guards For Me is present but "
                        + TargetTypeName + "." + TargetMethodName
                        + "(bool) was not found (its API probably changed); the fix is deferred and will retry.");
                    return;
                }

                combatTicksField = AccessTools.Field(combatType, "ticks");
                if (combatTicksField == null)
                {
                    Log.Error("[GNH LocalFixes] Guards For Me is present but "
                        + TargetTypeName + ".ticks was not found; the fix is deferred and will retry.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    null,
                    null,
                    new HarmonyMethod(AccessTools.Method(typeof(GfmIdleCombatDeepCopyFix), nameof(Transpiler), null, null)));

                installed = true;

                // 装完读回补丁表：这是「装上了」与「装对了」之间的那次验证。
                Patches info = Harmony.GetPatchInfo(target);
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " (transpilers=" + (info != null ? info.Transpilers.Count : -1) + ")."
                    + " This fixes the InvalidCastException that aborted the reference resolution of"
                    + " Nivarian_ThinkTree / Nivarian_MechUnit_ThinkTree / PAS_PoweredApparelMaintenance.");
            }
            catch (Exception ex)
            {
                // 装不上不影响游戏本体，也不影响其它修复项，但必须留痕。
                Log.Warning("[GNH LocalFixes] Failed to install the Guards For Me DeepCopy fix"
                    + " (that fix is NOT active this session; everything else is unaffected): " + ex);
            }
        }

        /// <summary>
        /// 逐条过一遍 IL，把两条写错的指令改成正确的目标。
        ///
        /// 注意本方法【在补丁安装的那一刻执行一次】，不是每次调用都执行，
        /// 所以它对游戏运行时性能的影响是零。
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // ToList 之后，list 里的元素仍然是【同一批指令对象】——这也是下面
            // 必须先扫描、后修改的原因：一旦改了对象，原始序列也跟着变。
            List<CodeInstruction> list = instructions.ToList();

            int castIndex = -1;      // 第 1 条要改的指令在表里的位置
            int castCount = 0;       // 它出现了几次
            int fieldIndex = -1;     // 第 2 条要改的指令在表里的位置
            int fieldCount = 0;      // 它出现了几次

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];

                // 第 1 条：把副本强转成 JobGiver_Idle —— 就是抛异常的那句。
                if (ci.opcode == OpCodes.Castclass && ci.operand is Type castTarget && castTarget == idleType)
                {
                    castIndex = i;
                    castCount++;
                    continue;
                }

                // 第 2 条：把 ticks 写进 JobGiver_Idle 的字段。
                // 只认【声明类型是 JobGiver_Idle 且名字叫 ticks】的 stfld，
                // 免得误伤同方法里其它字段写入。
                if (ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f
                    && f.DeclaringType == idleType && f.Name == "ticks")
                {
                    fieldIndex = i;
                    fieldCount++;
                }
            }

            // 只有「两条都恰好各命中一次」才动手。
            if (castCount != 1 || fieldCount != 1)
            {
                // 走到这里最常见的原因是：作者已经把 bug 修好了。
                // 什么都不做才是正确的 —— 绝不能在已经正确的代码上乱改。
                Log.Message("[GNH LocalFixes] Guards For Me's " + TargetTypeName + "." + TargetMethodName
                    + " does not need fixing this version"
                    + " (expected 1 castclass " + IdleTypeName + " and 1 stfld " + IdleTypeName + ".ticks,"
                    + " found " + castCount + " and " + fieldCount + "). Left untouched.");
                return list;
            }

            // 扫描确认无误，现在才修改。
            list[castIndex].operand = combatType;          // castclass -> JobGiver_IdleCombat
            list[fieldIndex].operand = combatTicksField;   // stfld     -> JobGiver_IdleCombat.ticks

            Log.Message("[GNH LocalFixes] Guards For Me DeepCopy fix applied:"
                + " castclass " + IdleTypeName + " -> " + TargetTypeName + " (instruction #" + castIndex + "),"
                + " stfld " + IdleTypeName + ".ticks -> " + TargetTypeName + ".ticks (instruction #" + fieldIndex + ").");

            return list;
        }
    }
}