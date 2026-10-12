using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 「VFE Empire 的乐器空间判断还崩不崩」—— 一个只读 IL 的探针。
    // ==========================================================================
    //
    // 【为什么需要它】
    //
    // VFEEInstrumentSpaceFix 给 VFE Empire 那句会崩的判断打了保护补丁。为了做到
    // 「作者自己修好之后本补丁自动让路」，我们必须在运行时回答一个问题：
    //
    //     「作者那句判断，现在还会不会对 null 调方法？」
    //
    // ⚠ 早期版本回答错了对象：它去扫「Def 库里还有没有 placeWorkers 为 null 的
    //   ThingDef」。而 placeWorkers 为 null 是绝大多数 ThingDef 的**正常状态**
    //  （实测原版 Core 的 997 个 ThingDef 里只有 85 处声明了 placeWorkers），
    //   所以那个探针**永远**判「不安全」，「自动让路」一次也不会触发 ——
    //   它测的是**数据**，而要回答的是**代码**。
    //
    // 【现在怎么问：读 IL】
    //
    // 直接读 VFE Empire 那个判断方法自己的 IL，看它在「读 placeWorkers」和
    // 「拿读到的值去调成员」之间，有没有一条**条件分支**。
    //
    // ⚠⚠ 一个必须记住的实测细节：placeWorkers 是 **Verse.BuildableDef 的公共字段**，
    //    不是属性。所以真实 IL 里出现的是 **ldfld**，不是 call get_placeWorkers：
    //
    //       作者没修（会崩）—— 这就是本机 VFE Empire 1.6 的真实 IL
    //       （MVID 7b87b4b5f2e8419a9b66f287653fbf84，方法 0x06000744，22 字节 6 条指令）：
    //
    //           IL_0000: ldarg.1
    //           IL_0001: ldfld     Verse.BuildableDef.placeWorkers   ← 读字段
    //           IL_0006: ldtoken   VFEEmpire.Placeworker_DanceFloorArea
    //           IL_000B: call      System.Type.GetTypeFromHandle
    //           IL_0010: callvirt  typespec.Contains                 ← 直接消费
    //           IL_0015: ret
    //                                                   ↑ 中间没有任何条件分支
    //
    //       作者修了（判空短路）：
    //           ldarg.1
    //           ldfld     Verse.BuildableDef.placeWorkers
    //           brfalse.s L_skip                  ← ★ 判空分支
    //           ldarg.1
    //           ldfld     Verse.BuildableDef.placeWorkers
    //           ldtoken   ...
    //           call      System.Type.GetTypeFromHandle
    //           callvirt  typespec.Contains
    //         L_skip: ret
    //
    //       作者修了（?. 写法）：
    //           ldfld     Verse.BuildableDef.placeWorkers
    //           dup
    //           brtrue.s  L_go                     ← ★ 判空分支
    //           pop
    //           ldc.i4.0
    //           ...
    //
    //   所以判据**同时**认两种访问形态：字段（ldfld / ldsfld …，名字 placeWorkers）
    //   与属性读取器（call get_placeWorkers）。只认其中一种都会让探针在真实目标上
    //   永远答不出结论 —— 那等于「自动让路」根本没修好。
    //
    // 【判据的能力边界（宁可继续保护，也不要错误让路）】
    //
    //   · 找不到 placeWorkers 的读取      → 抛异常，调用方按「不安全」处理
    //   · 读取之后没有任何方法调用        → 抛异常，同上
    //   · IL 解析不了 / 方法没有方法体    → 抛异常，同上
    //   · 作者把逻辑整体重写掉            → 同上（继续保护，只是不再自动让路）
    //
    //   也就是说：**这里只会在「确凿看到判空分支」时才说「安全」**，
    //   其余一切情况都交给调用方走保守路径 —— 撤掉保护远比多保护一段危险。
    //
    // 【为什么单独放一个文件】
    //
    // 这样它可以被一个**不依赖游戏程序集**的离线测试工程直接编译
    //（见 tools\test-instrument-probe\），用若干已知答案的小方法验证判据本身 ——
    // 探针如果自己判错，比没有探针更糟。测试里既覆盖字段形态（真实情况），
    // 也覆盖属性形态（防御将来改成属性），还包含一个用 Reflection.Emit
    // 逐条复刻真实 VFEE lambda 形态的用例。
    internal static class InstrumentSpaceProbe
    {
        /// <summary>
        /// 要盯住的那个成员的**字段名**。真实形态是字段（Verse.BuildableDef.placeWorkers）。
        /// </summary>
        internal const string PlaceWorkersMemberName = "placeWorkers";

        /// <summary>
        /// 同一个成员若将来被改成属性，读取器的名字就是这个（get_ 前缀）。
        /// 两种形态都认，免得改一次写法探测就整体失效。
        /// </summary>
        internal const string PlaceWorkersGetterName = "get_" + PlaceWorkersMemberName;

        // ----------------------------------------------------------------------
        // IL 解码
        // ----------------------------------------------------------------------

        private struct Instruction
        {
            internal OpCode Opcode;
            internal int Offset;
            internal int OperandOffset;   // -1 表示没有操作数
            internal int OperandSize;
        }

        /// <summary>
        /// opcode 值 → OpCode。键用 OpCode.Value（两字节前缀的 opcode 其 Value 是
        /// 0xFE00 | 第二字节，作为 short 是个负数，两边一致所以能对上）。
        /// </summary>
        private static readonly Dictionary<short, OpCode> OpCodeTable = BuildOpCodeTable();

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            Dictionary<short, OpCode> map = new Dictionary<short, OpCode>();
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                object value = fields[i].GetValue(null);
                if (value is OpCode)
                {
                    OpCode op = (OpCode)value;
                    map[op.Value] = op;
                }
            }
            return map;
        }

        /// <summary>
        /// 把 IL 字节流切成指令。**必须按 opcode 的真实长度前进**，不能用字节搜索
        /// 代替 —— 操作数里偶然出现分支 opcode 的字节会造成误判，而误判的方向恰好是
        /// 「错误地宣布安全、撤掉保护」，是最危险的失败模式。
        /// </summary>
        private static List<Instruction> Decode(byte[] il)
        {
            List<Instruction> list = new List<Instruction>();
            int i = 0;
            while (i < il.Length)
            {
                int start = i;
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length)
                    {
                        throw new BadImageFormatException("IL 在 0xFE 前缀处截断。");
                    }
                    code = unchecked((short)(0xFE00 | il[i++]));
                }

                OpCode op;
                if (!OpCodeTable.TryGetValue(code, out op))
                {
                    throw new BadImageFormatException("遇到不认识的 IL opcode：0x" + code.ToString("X4"));
                }

                int operandSize;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        operandSize = 0;
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                    case OperandType.ShortInlineBrTarget:
                        operandSize = 1;
                        break;
                    case OperandType.InlineVar:
                        operandSize = 2;
                        break;
                    case OperandType.InlineI:
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.ShortInlineR:
                        operandSize = 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        operandSize = 8;
                        break;
                    case OperandType.InlineSwitch:
                        if (i + 4 > il.Length)
                        {
                            throw new BadImageFormatException("IL 的 switch 操作数截断。");
                        }
                        int caseCount = BitConverter.ToInt32(il, i);
                        operandSize = 4 + 4 * caseCount;
                        break;
                    default:
                        throw new BadImageFormatException("不支持的 IL 操作数类型：" + op.OperandType);
                }

                Instruction ins;
                ins.Opcode = op;
                ins.Offset = start;
                ins.OperandSize = operandSize;
                ins.OperandOffset = (operandSize > 0) ? i : -1;
                list.Add(ins);

                i += operandSize;
                if (i > il.Length)
                {
                    throw new BadImageFormatException("IL 指令的操作数越过了方法体末尾。");
                }
            }
            return list;
        }

        /// <summary>
        /// 解析 InlineMethod 指令指向的方法。解析不出来（token 不是方法等）返回 null，
        /// 由调用方继续往后扫 —— 这里**不抛**，因为「某个 token 认不出来」不该让整条
        /// 探测链断掉。
        /// </summary>
        private static MethodBase TryResolveMethod(Module module, byte[] il, Instruction ins)
        {
            if (ins.OperandOffset < 0 || ins.OperandSize != 4)
            {
                return null;
            }
            try
            {
                int token = BitConverter.ToInt32(il, ins.OperandOffset);
                return module.ResolveMethod(token);
            }
            catch (Exception)
            {
                // ArgumentException / BadImageFormatException 等：这个 token 不是可解析的方法。
                return null;
            }
        }

        /// <summary>
        /// 这条指令是不是「读 placeWorkers」——**字段与属性两种形态都算**。
        ///
        /// 字段形态是本机的真实情况（Verse.BuildableDef.placeWorkers → ldfld）；
        /// 属性形态是防御性的，万一将来那个成员改成属性，这里不用跟着改。
        /// </summary>
        private static bool IsPlaceWorkersAccess(Module module, byte[] il, Instruction ins)
        {
            if (ins.OperandOffset < 0 || ins.OperandSize != 4)
            {
                return false;
            }

            int token = BitConverter.ToInt32(il, ins.OperandOffset);

            if (ins.Opcode.OperandType == OperandType.InlineField)
            {
                try
                {
                    FieldInfo field = module.ResolveField(token);
                    return field != null && field.Name == PlaceWorkersMemberName;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            if (ins.Opcode.OperandType == OperandType.InlineMethod)
            {
                try
                {
                    MethodBase method = module.ResolveMethod(token);
                    return method != null && method.Name == PlaceWorkersGetterName;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        // ----------------------------------------------------------------------
        // 判据
        // ----------------------------------------------------------------------

        /// <summary>
        /// 判断一个「接收 ThingDef、返回 bool」的判断方法里，读 placeWorkers 之后
        /// 是否存在判空分支。
        ///
        /// 返回 true  **仅当**确凿看到「读 placeWorkers」与「第一次拿它调方法」之间
        /// 夹着一条条件分支。
        /// 其余一切情况（含各种解析不出来）都**抛异常**，由调用方按「不安全」处理。
        /// </summary>
        /// <param name="lambda">VFE Empire 的那个判断方法（编译器生成的 lambda）。</param>
        internal static bool HasNullGuardBeforeFirstUse(MethodInfo lambda)
        {
            if (lambda == null)
            {
                throw new ArgumentNullException("lambda");
            }

            MethodBody body = lambda.GetMethodBody();
            if (body == null)
            {
                throw new InvalidOperationException("方法没有方法体（可能是 abstract / extern）。");
            }

            byte[] il = body.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                throw new InvalidOperationException("方法体是空的。");
            }

            List<Instruction> ops = Decode(il);
            Module module = lambda.Module;

            // ── ① 找到「读 placeWorkers」那一条（字段 ldfld 或属性 getter 都认）──
            int readIndex = -1;
            for (int k = 0; k < ops.Count; k++)
            {
                if (IsPlaceWorkersAccess(module, il, ops[k]))
                {
                    readIndex = k;
                    break;
                }
            }
            if (readIndex < 0)
            {
                throw new InvalidOperationException(
                    "在这段判断里找不到对 " + PlaceWorkersMemberName + " 的读取。");
            }

            // ── ② 从它后面往后扫：先记录有没有条件分支，遇到「第一次真正的方法调用」就下结论 ──
            //
            // 重新读一次 placeWorkers **不算**消费 —— 那正是
            //     placeWorkers != null && placeWorkers.Contains(...)
            // 编译出来的样子（判空之后会再读一次），必须继续往后走，
            // 否则会把判空分支漏在区间之外。
            //
            // 注意真实 IL 里 placeWorkers 是**字段**，重读是 ldfld（InlineField），
            // 不是方法调用 —— 所以「先判 IsPlaceWorkersAccess、再判 InlineMethod」
            // 这个顺序不能反过来。
            bool sawConditionalBranch = false;
            for (int k = readIndex + 1; k < ops.Count; k++)
            {
                OpCode op = ops[k].Opcode;

                if (op.FlowControl == FlowControl.Cond_Branch)
                {
                    sawConditionalBranch = true;
                    continue;
                }

                if (IsPlaceWorkersAccess(module, il, ops[k]))
                {
                    // 又读了一次：这是判空后的第二次读取，不算消费，继续往后。
                    continue;
                }

                if (op.OperandType != OperandType.InlineMethod)
                {
                    continue;
                }

                MethodBase target = TryResolveMethod(module, il, ops[k]);
                if (target == null)
                {
                    continue;
                }

                // 到这里就是「第一次真正拿它调方法」——下结论。
                return sawConditionalBranch;
            }

            throw new InvalidOperationException(
                "读取 " + PlaceWorkersMemberName + " 之后找不到任何方法调用，无法判断。");
        }
    }
}
