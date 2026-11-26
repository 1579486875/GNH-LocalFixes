// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 模拟真实的 Verse.BuildableDef.placeWorkers —— **它是公共字段**，不是属性。
    // 这一点是本轮实测出来的：第一版判据只认 `call get_placeWorkers`（属性读取器），
    // 而真实 IL 里是 `ldfld Verse.BuildableDef.placeWorkers`，于是判据在真实目标上
    // 永远答不出结论。所以下面**字段形态才是主用例**，属性形态只作防御性覆盖。
    // ==========================================================================
    public class FakeBuildableDef
    {
        public List<Type> placeWorkers;
    }

    // 防御性用例：万一将来那个成员改成属性。
    public class FakeThingDefWithProperty
    {
        private List<Type> _placeWorkers;

        public List<Type> placeWorkers
        {
            get { return _placeWorkers; }
            set { _placeWorkers = value; }
        }
    }

    public static class ProbeTest
    {
        // ================= 字段形态（真实情况） =================

        // ① 作者【没修】：读完直接消费，中间没有任何判空分支。
        //    与本机 VFE Empire 1.6 的真实 IL 同形。期望：false（继续保护）
        public static bool FieldUnsafeDirect(FakeBuildableDef d)
        {
            return d.placeWorkers.Contains(typeof(string));
        }

        // ② 作者【修了】：判空短路。期望：true（让路）
        public static bool FieldSafeAndShortCircuit(FakeBuildableDef d)
        {
            return d.placeWorkers != null && d.placeWorkers.Contains(typeof(string));
        }

        // ③ 作者【修了】：?. 写法。期望：true
        public static bool FieldSafeNullConditional(FakeBuildableDef d)
        {
            return (d.placeWorkers?.Contains(typeof(string))) ?? false;
        }

        // ④ 作者【修了】：提前 return 写法。期望：true
        public static bool FieldSafeEarlyReturn(FakeBuildableDef d)
        {
            if (d.placeWorkers == null)
            {
                return false;
            }
            return d.placeWorkers.Contains(typeof(string));
        }

        // ⑤ 假修复：只判「列表里有没有元素」，没判 null。期望：false（不能被骗）
        public static bool FieldFakeFixCountOnly(FakeBuildableDef d)
        {
            return d.placeWorkers.Count > 0 && d.placeWorkers.Contains(typeof(string));
        }

        // ⑥ 换成静态扩展方法调用，仍然不判空。期望：false
        public static bool FieldStaticEnumerableContains(FakeBuildableDef d)
        {
            return System.Linq.Enumerable.Contains(d.placeWorkers, typeof(string));
        }

        // ================= 属性形态（防御性覆盖） =================

        public static bool PropUnsafeDirect(FakeThingDefWithProperty d)
        {
            return d.placeWorkers.Contains(typeof(string));
        }

        public static bool PropSafeAndShortCircuit(FakeThingDefWithProperty d)
        {
            return d.placeWorkers != null && d.placeWorkers.Contains(typeof(string));
        }

        // ================= 其它 =================

        // ⑨ 作者整体重写，压根不再读 placeWorkers。期望：抛异常（→ 探测不可用 → 保守）
        public static bool RewrittenNoPlaceWorkers(FakeBuildableDef d)
        {
            return false;
        }

        // ================= 用 Reflection.Emit 逐条复刻真实 VFEE lambda =================
        //
        // 本机 VFEE Empire 1.6（MVID 7b87b4b5f2e8419a9b66f287653fbf84，方法 0x06000744，
        // 22 字节 / 6 条指令）的真实 IL：
        //     IL_0000: ldarg.1
        //     IL_0001: ldfld     Verse.BuildableDef.placeWorkers
        //     IL_0006: ldtoken   VFEEmpire.Placeworker_DanceFloorArea
        //     IL_000B: call      System.Type.GetTypeFromHandle
        //     IL_0010: callvirt  typespec.Contains
        //     IL_0015: ret
        // 下面这段按同样的指令序列生成一个方法（token 目标换成等价物），
        // 用它跑判据，就能确定「真实形态」下探针会给出什么结论。
        // 期望：false（作者未修 → 继续保护）—— 与基线日志 L3038 一致。
        private static MethodInfo BuildRealShapeMethod()
        {
            AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("FakeVFEE"), AssemblyBuilderAccess.Run);
            ModuleBuilder mb = ab.DefineDynamicModule("FakeVFEE.dll");
            TypeBuilder tb = mb.DefineType("Fake.VFEE", TypeAttributes.Public | TypeAttributes.Class);
            FieldBuilder fb = tb.DefineField("placeWorkers", typeof(List<Type>), FieldAttributes.Public);
            MethodBuilder meth = tb.DefineMethod(
                "<.cctor>b__1_0",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                typeof(bool),
                new Type[] { tb });

            ILGenerator il = meth.GetILGenerator();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldfld, fb);
            il.Emit(OpCodes.Ldtoken, typeof(string));
            il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle", new Type[] { typeof(RuntimeTypeHandle) }));
            MethodInfo containsType = typeof(List<Type>).GetMethod("Contains", new Type[] { typeof(Type) });
            if (containsType == null)
            {
                throw new InvalidOperationException("test setup: List<Type>.Contains not found");
            }
            il.Emit(OpCodes.Callvirt, containsType);
            il.Emit(OpCodes.Ret);

            Type built = tb.CreateType();
            return built.GetMethod("<.cctor>b__1_0");
        }

        private static int Main()
        {
            object[][] cases =
            {
                new object[] { "01 字段/未修: Contains()",              "FieldUnsafeDirect",            (bool?)false },
                new object[] { "02 字段/已修: != null && Contains()",   "FieldSafeAndShortCircuit",     (bool?)true  },
                new object[] { "03 字段/已修: ?.Contains() ?? false",   "FieldSafeNullConditional",     (bool?)true  },
                new object[] { "04 字段/已修: if(null) return",         "FieldSafeEarlyReturn",         (bool?)true  },
                new object[] { "05 字段/假修复: Count>0 && Contains()", "FieldFakeFixCountOnly",        (bool?)false },
                new object[] { "06 字段/未修: Enumerable.Contains(l,x)","FieldStaticEnumerableContains",(bool?)false },
                new object[] { "07 属性/未修: Contains()",              "PropUnsafeDirect",             (bool?)false },
                new object[] { "08 属性/已修: != null && Contains()",   "PropSafeAndShortCircuit",      (bool?)true  },
                new object[] { "09 重写: 不再读 placeWorkers",          "RewrittenNoPlaceWorkers",      null         },
                new object[] { "10 EMIT: 逐条复刻真实 VFEE lambda",      "@emitted",                     (bool?)false },
            };

            Console.WriteLine("InstrumentSpaceProbe.HasNullGuardBeforeFirstUse —— offline judgement test");
            Console.WriteLine(new string('-', 100));
            Console.WriteLine("{0,-40} {1,-12} {2,-12} {3}", "case", "expected", "actual", "verdict");
            Console.WriteLine(new string('-', 100));

            int pass = 0, fail = 0;
            foreach (object[] c in cases)
            {
                string name = (string)c[0];
                string which = (string)c[1];
                bool? expect = (bool?)c[2];

                MethodInfo mi;
                if (which == "@emitted")
                {
                    mi = BuildRealShapeMethod();
                }
                else
                {
                    mi = typeof(ProbeTest).GetMethod(which);
                }

                string expectText = expect.HasValue
                    ? (expect.Value ? "true(safe)" : "false(unsafe)")
                    : "exception";

                bool ok;
                string actualText;
                string note = "";
                try
                {
                    bool r = InstrumentSpaceProbe.HasNullGuardBeforeFirstUse(mi);
                    actualText = r ? "true(safe)" : "false(unsafe)";
                    ok = expect.HasValue && expect.Value == r;
                }
                catch (Exception ex)
                {
                    actualText = "exception";
                    ok = !expect.HasValue;
                    note = "  -> " + ex.GetType().Name + ": " + ex.Message;
                }

                Console.WriteLine("{0,-40} {1,-12} {2,-12} {3}", name, expectText, actualText, ok ? "PASS" : "FAIL");
                if (note.Length > 0)
                {
                    Console.WriteLine(note);
                }
                if (ok) { pass++; } else { fail++; }
            }

            Console.WriteLine(new string('-', 100));
            Console.WriteLine("PASS={0}  FAIL={1}", pass, fail);
            if (fail != 0)
            {
                Console.WriteLine("!! 判据有问题，绝不能上线：宁可继续保护，也不能错误让路。");
            }
            return fail == 0 ? 0 : 1;
        }
    }
}
