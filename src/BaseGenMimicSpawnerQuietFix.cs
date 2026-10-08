using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：每次生成古代神殿（GenStep_ScatterShrines），
    // 游戏日志里都会被刷上几十条
    //
    //     Could not find any RuleDef for symbol "mimicSpawner"
    //     with any resolver that could resolve rect=(...)
    //
    // 的 WARNING。实测一次开局 34 条。
    // ==========================================================================
    //
    // 【第一件事：这句警告的字面意思是骗人的】
    //
    // 它写着「找不到任何 RuleDef」，听起来像"规则没加载"。不是的。
    // 看原版源码就清楚了（1.6.4871，RimWorld.BaseGen.BaseGen.Resolve，
    // 已用反编译逐行核对过）：
    //
    //     string symbol = toResolve.symbol;
    //     ResolveParams resolveParams = toResolve.resolveParams;
    //     tmpResolvers.Clear();
    //     if (rulesBySymbol.TryGetValue(symbol, out var value))    // ← ① 先按 symbol 查字典
    //     {
    //         for (...) for (...)
    //             if (r.CanResolve(resolveParams)) tmpResolvers.Add(r);   // ← ② 再逐个问"你能不能干"
    //     }
    //     if (!tmpResolvers.Any())                                 // ← ③ 一个都没收进来
    //     {
    //         Log.Warning("Could not find any RuleDef for symbol \"" + symbol + "\" ...");
    //         return;                                              // ← 什么都不做就退出
    //     }
    //
    // 关键在于：**"字典里压根没这个 symbol" 和 "字典里有、但每个 resolver 都拒绝"，
    // 打印的是同一句话**。而实际发生的是后者 —— 规则一直都在，是传进来的矩形坏了。
    //
    // 【第二件事：坏的是矩形，不是规则】
    //
    // 全库唯一一条 mimicSpawner 规则定义在模组
    // 「RimJobWorld - Onahole Extension」的 Defs\Rules\MimicSpawner.xml 里，
    // 它的 resolver 是 RJW_Onahole.Data.SymbolResolver_MimicSpawner。
    // 这个类**存在**、**名字没错**（都核对过），而且它**没有重写 CanResolve**，
    // 也就是说用的是基类 RimWorld.BaseGen.SymbolResolver 的默认实现：
    //
    //     public IntVec2 minRectSize = IntVec2.One;      // ← 注意默认值是 (1, 1)
    //
    //     public virtual bool CanResolve(ResolveParams rp)
    //     {
    //         if (rp.rect.Width >= minRectSize.x)
    //             return rp.rect.Height >= minRectSize.z;
    //         return false;
    //     }
    //
    // 翻译成大白话：**矩形的宽和高必须都 ≥ 1，否则一律拒绝。**
    //
    // 那矩形是谁传进来的？是模组自己的 Harmony 补丁
    // RJW_Onahole.Patches.SymbolResolver_Ancient_Patch 里这段：
    //
    //     resolveParams.rect = rp.rect.ContractedBy(2);         // ← 无条件四边各内缩 2 格
    //     BaseGen.symbolStack.Push("mimicSpawner", resolveParams);
    //
    // 而古代神殿生成时会拿很多**只有一两格大**的子区域去跑 resolver
    // （单个家具位、单个格子）。一个 1×1 的矩形再四边各缩 2 格，
    // 就变成了宽 −3、高 −3 的**空矩形**（数学上就是"没有面积的区域"）。
    //
    // 【日志里的数字怎么读】
    //
    //     rect=(51,238,47,234)
    //
    // CellRect.ToString() 的格式是 (minX, minZ, maxX, maxZ)（已反编译核对），
    // 所以这里 minX=51 而 maxX=47 —— **最大值比最小值还小**，正是空矩形。
    // 把它四边各外扩 2 格就还原成 (49,236,49,236)，正好是一个 1×1 的单格。
    // 日志里那 34 条全是这样成对出现的小格子，完全对得上。
    //
    // 空矩形的宽高都是 **0**，不是负数 —— CellRect.Width 的 getter 里有一句
    //     if (minX > maxX) return 0;
    // 对退化矩形直接返回 0。（第一版这里写成了"负数"，方向对、数值错，
    // 后来用 verify-mimic-quiet.ps1 在真实的 CellRect 上量过一次才改对。）
    // 而 resolver 基类要求宽高都 ≥ 1 → 必然被拒
    // → CanResolve 返回 false → tmpResolvers 为空 → 打印那句措辞误导人的警告。
    //
    // 旁证：模组 Resolve() 里那句 "[Mimic] SymbolResolver_MimicSpawner.Resolve called ..."
    // 调试输出，实测**一次都没打印过**，说明 resolver 从头到尾没被执行过。
    //
    // 【第三件事：为什么不能用 XML 补丁修（这条是踩过的坑）】
    //
    // 曾经试过在 Defs 下再追加一条同 symbol 的 RuleDef 做兜底 —— **无效**。
    // 因为新规则的 resolver 还是同一个类，minRectSize 还是默认的 (1, 1)，
    // 面对同一个空矩形照样返回 false。问题从来不在"有没有规则"。
    //
    // （把 minRectSize 调成 0 或负数倒是能强行放行 —— 空矩形的宽高正好是 0，
    //   0 ≥ 0 就过关了。但那是**改变游戏行为**：会让 resolver 真的跑到一个
    //   空矩形的"中心点"上去生成物件。不能那么干。）
    //
    // 【第四件事：本补丁做什么，以及为什么它是安全的】
    //
    // 只做一件事：在 BaseGen.Resolve 真正开始之前插一脚，一旦发现
    //     ① symbol 是 "mimicSpawner"，并且
    //     ② 矩形宽或高 < 1（也就是原版必然拒绝的那一种）
    // 就**跳过整个方法**。
    //
    // 这与原版行为**完全等价**：原版在这种输入下的实际动作就是
    // 「一个 resolver 都收不到 → 打印警告 → return」，什么都没做、什么都没改。
    // 我们只是把「打印警告」这一步省掉。
    //
    // 反过来说：只要矩形是正常的（宽高 ≥ 1），本补丁**一行都不插手**，
    // 原版逻辑照跑，mimic 该生成照样生成。
    //
    // 【为什么只认 "mimicSpawner" 一个 symbol，不做成通用的】
    //
    // 通用的写法是「只要该 symbol 的所有 resolver 都 CanResolve=false 就跳过」，
    // 但那需要去读 BaseGen 的**私有静态字典** rulesBySymbol，耦合更深、风险更大；
    // 而实测日志里只有 mimicSpawner 这一个 symbol 在刷。
    // **范围越小越安全** —— 这是本补丁包一贯的原则。
    //
    // 【安全性小结】
    //
    //   · 不引用 Onahole 模组的任何类型，只比对字符串。模组没装时没人会 push
    //     这个 symbol，本补丁永远走不到跳过分支，零副作用。
    //   · 连 RimWorld 的 SymbolStack.Element 类型也不做强类型引用 ——
    //     前缀参数声明成 object，字段用字符串名反射读取。
    //     这样即使将来原版改了这个嵌套类型，本补丁类也不会加载失败，
    //     更不会连累 LocalFixesMod 的构造函数。
    //   · 整个判断包在 try/catch 里：出任何意外一律 return true 放行原版。
    //   · 目标方法或类型找不到时打 Error 并**保留重试**，绝不静默失败。
    //   · 目标方法是 private static，Harmony 可以正常打补丁。
    internal static class BaseGenMimicSpawnerQuietFix
    {
        // 目标：RimWorld.BaseGen.BaseGen.Resolve(RimWorld.BaseGen.SymbolStack.Element)
        private const string BaseGenTypeName = "RimWorld.BaseGen.BaseGen";

        // ⚠⚠ 这里有个必须记住的坑（2026-10-07 实机验证抓到的真 bug）：
        //
        //   SymbolStack.Element 是个**嵌套类型**，而嵌套类型在 .NET 里的**规范名**
        //   是用 '+' 连接的：
        //         RimWorld.BaseGen.SymbolStack+Element        ← 这个才是它的真名
        //         RimWorld.BaseGen.SymbolStack.Element        ← 这个根本不存在
        //
        //   实测（本仓库的 verify-mimic-quiet.ps1 会当场复现这一条）：
        //         Assembly.GetType("RimWorld.BaseGen.SymbolStack.Element")       -> null
        //         AccessTools.TypeByName("RimWorld.BaseGen.SymbolStack.Element") -> null
        //         Assembly.GetType("RimWorld.BaseGen.SymbolStack+Element")       -> 找到了
        //
        //   本补丁的第一版就栽在这里：elementType 拿到 null → 走"未找到 → 已跳过"
        //   那条分支 → 补丁**根本装不上**，只在日志里留一句不起眼的话，很容易被忽略。
        //
        //   现在改成**从方法签名里把参数类型取出来**（见下面的 FindResolveMethod），
        //   一个名字都不用自己拼，两个坑一起绕开。下面这两个常量只用于兜底比对。
        private const string ElementTypeNamePlus = "RimWorld.BaseGen.SymbolStack+Element";
        private const string ElementTypeNameDot = "RimWorld.BaseGen.SymbolStack.Element";
        private const string TargetMethodName = "Resolve";

        // 只处理这一个符号。要放开范围必须先想清楚为什么。
        private const string MimicSymbol = "mimicSpawner";

        private static bool installed;

        // 跳过了多少次（只用来写日志，不参与逻辑）。
        private static int skippedCount;

        // 是否已经打过"开始静默"的说明日志。
        // 一次开局会触发几十次，每次都打反而把日志淹了，所以只打第一条。
        private static bool announced;

        // ==================================================================
        // ★ 性能：这三个反射句柄只在安装时解析一次，之后每次调用直接用。
        // ==================================================================
        //
        // 【为什么不能每次调用都现查】
        //
        // 本前缀挂在 BaseGen.Resolve 上。那个方法是**地图生成期间每解析一个符号
        // 就被调用一次**的，属于热路径 —— 单位成本会被乘以调用次数。
        //
        // 实测（本仓库的 bench-mimic-prefix.ps1，计时循环放在 C# 里量的）：
        //   · 原先用 Traverse 现查的写法：热路径每次调用约 195 ns、**分配 280 字节**；
        //   · 换成这里缓存的 FieldInfo 之后：**零堆分配**，耗时也明显下降。
        //
        // 280 字节看着不多，但它是**每一次**调用都会产生的垃圾，而垃圾迟早要靠
        // GC 回收 —— GC 一跑就会卡帧。这就是"性能友好"与"不友好"的分水岭。
        //
        // 【为什么读 symbol 不会装箱】
        //
        // symbol 字段是 string（引用类型）。FieldInfo.GetValue 对引用类型字段是
        // 直接把对象引用取出来，**不会装箱**，所以那条热路径上没有任何堆分配。
        // （下面读结构体字段的那几步确实会装箱，但它们全在冷路径上，
        //   一次开局只走几十次，见 Prefix 里的说明。）
        private static FieldInfo symbolField;
        private static FieldInfo resolveParamsField;
        private static FieldInfo rectField;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type baseGenType = AccessTools.TypeByName(BaseGenTypeName);
                if (baseGenType == null)
                {
                    // 找不到就置位跳过：这是"游戏版本变了"的情形，重试也没用。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + BaseGenTypeName
                        + "；mimicSpawner 消音补丁已跳过。");
                    return;
                }

                MethodInfo target = FindResolveMethod(baseGenType);
                if (target == null)
                {
                    // 找不到方法 → 多半是原版改了签名。不置位，留给下次重试；并且必须留 Error 线索。
                    Log.Error("[GNH LocalFixes] 找不到 " + BaseGenTypeName + "." + TargetMethodName
                        + "(" + ElementTypeNameDot + ")；mimicSpawner 消音补丁本次未安装，下次会重试。");
                    return;
                }

                // 把后面热路径要用的三个字段句柄先解析出来（只做这一次）。
                //
                // 参数类型直接从**方法签名**上取，不再按名字去猜那个嵌套类型 ——
                // 这既绕开了 "+"/"." 的命名坑，也让下面这几行拿到的一定是
                // 原版真正使用的那个类型。
                Type elementType = target.GetParameters()[0].ParameterType;
                const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.Instance;

                symbolField        = elementType.GetField("symbol", FieldFlags);
                resolveParamsField = elementType.GetField("resolveParams", FieldFlags);

                if (symbolField == null || resolveParamsField == null)
                {
                    // 结构变了。不置位 → 下次重试；并且必须留 Error 线索，不许静默跳过。
                    Log.Error("[GNH LocalFixes] " + elementType.FullName
                        + " 上拿不到 symbol / resolveParams 字段（原版结构可能改了）；"
                        + "mimicSpawner 消音补丁本次未安装，下次会重试。");
                    return;
                }

                rectField = resolveParamsField.FieldType.GetField("rect", FieldFlags);
                if (rectField == null)
                {
                    Log.Error("[GNH LocalFixes] " + resolveParamsField.FieldType.FullName
                        + " 上拿不到 rect 字段（原版结构可能改了）；"
                        + "mimicSpawner 消音补丁本次未安装，下次会重试。");
                    return;
                }

                // Patch 的参数顺序是 (原方法, 前缀, 后缀, 转译器, 收尾器)。
                // 这里只需要前缀，其余三个传 null。
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(BaseGenMimicSpawnerQuietFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装 mimicSpawner 警告消音补丁（BaseGen.Resolve）。"
                    + "它只拦「矩形宽或高小于 1 的 mimicSpawner 请求」——"
                    + "那种请求原版本来就会拒绝、而且什么都不做，所以跳过它与原版等价，"
                    + "只是不再往日志里刷警告。正常矩形的生成完全不受影响。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装 mimicSpawner 消音补丁失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 找到 BaseGen.Resolve(SymbolStack.Element) 这个私有静态方法。
        // ==================================================================
        //
        // 为什么不写成 AccessTools.Method(type, "Resolve", new[] { elementType }, null)：
        // 那样得先拿到 elementType，而它是**嵌套类型**，按名字去找有两个坑 ——
        //   ① 规范名要用 "+"（SymbolStack+Element），写成 "." 永远找不到（已实测）；
        //   ② 就算找到了，还得再手工拼一个 Type[] 数组传进去。
        //
        // 直接从**方法签名**里把参数类型读出来：一个名字都不用自己拼，
        // 两个坑一起绕开；将来就算原版把这个嵌套类型搬了家，也照样能找到。
        private static MethodInfo FindResolveMethod(Type baseGenType)
        {
            // Resolve 是 private static，所以必须带 NonPublic；
            // DeclaredOnly 表示只看 BaseGen 自己声明的（写出来更明确）。
            MethodInfo[] methods = baseGenType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != TargetMethodName)
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1)
                {
                    continue;   // 只认单参数那个重载（实测 BaseGen 里叫 Resolve 的只有这一个）
                }

                // 参数必须正好是 SymbolStack 里那个嵌套的 Element。
                //
                // ⚠ 只比对「+」这一种写法就够了 —— .NET 的 Type.FullName 对嵌套类型
                //   一律是「外层+内层」，**永远不可能出现「.」的写法**（本文件上面已写明这点）。
                //   这里原先还比对了 ElementTypeNameDot，那是一个永远不可能命中的死分支，
                //   2026-10-08 清掉。
                //   点号那个常量本身仍然留着 —— 它只用在「找不到目标」的日志文案里，
                //   是打印给人看的，不是判据。
                string declared = ps[0].ParameterType.FullName;
                if (declared == ElementTypeNamePlus)
                {
                    return m;
                }
            }

            return null;
        }

        // ==================================================================
        // 前缀：返回 false 表示「跳过原方法，别执行」。
        // ==================================================================
        //
        // 参数名 toResolve 必须与目标方法的参数名**完全一致** ——
        // Harmony 是按参数名把目标方法的实参喂给前缀的。
        //
        // 类型写成 object 而不是 SymbolStack.Element，是为了不产生编译期依赖，
        // 理由见类头注释的「安全性小结」。
        public static bool Prefix(object toResolve)
        {
            try
            {
                if (toResolve == null)
                {
                    return true;
                }

                // 还没装成（或安装走了失败分支）时，symbolField 仍是 null。
                // 这一行是**防御性**的，而且必须放在最前面 —— 理由实测过：
                //
                //   基准测试（bench-mimic-prefix.ps1）第一次跑优化后的版本时，
                //   因为没有先调 Install()，symbolField 是 null，于是每一次调用都
                //   抛 NullReferenceException → 掉进下面的 catch → 又去调 Log.Warning。
                //   结果热路径从 195 ns 退化到 **92,906 ns（约 500 倍）**，
                //   每次还额外产生近 2 KB 垃圾。
                //
                //   正常情况下 Install() 一定先于任何一次 BaseGen.Resolve 跑完
                //   （它在模组构造函数里），但这属于"配置对不对"的问题；
                //   热路径上**永远不该有依赖外部状态的异常路径**。
                //   一行 null 判断换掉 500 倍退化，非常划算。
                if (symbolField == null)
                {
                    return true;
                }

                // ── 热路径：整条路径只有这一次字段读取 ──
                //
                // 用安装时缓存好的 FieldInfo，而不是每次 Traverse.Create 现查。
                // symbol 是 string（引用类型），GetValue 直接给出对象引用、**不会装箱**，
                // 所以这一行零堆分配 —— 绝大多数符号都在这里就被排除掉了。
                //
                // 用 `as string` 而不是强转：万一将来原版把这个字段换成别的类型，
                // as 会安静地给出 null，而 null != MimicSymbol 就直接放行 ——
                // 绝不会因为类型对不上而抛异常穿出前缀。
                string symbol = symbolField.GetValue(toResolve) as string;
                if (symbol != MimicSymbol)
                {
                    return true;
                }

                // ── 以下都属于冷路径 ──
                //
                // 只有 symbol 真的叫 mimicSpawner 时才走得到这里，一次开局也就几十次，
                // 所以保持直白好读的写法，不在这些地方做微优化。
                //
                // 注意 ResolveParams 和 CellRect 都是**结构体**，FieldInfo.GetValue
                // 会把它们装箱成副本。这里只是**读**、不写回，副本不影响原对象。
                object parms = resolveParamsField.GetValue(toResolve);
                if (parms == null)
                {
                    return true;
                }

                object rect = rectField.GetValue(parms);
                if (rect == null)
                {
                    return true;
                }


                Traverse rectTraverse = Traverse.Create(rect);
                int width = rectTraverse.Property("Width").GetValue<int>();
                int height = rectTraverse.Property("Height").GetValue<int>();

                // 矩形是好的 → 交给原版正常处理，我们一个字都不插手。
                if (width >= 1 && height >= 1)
                {
                    return true;
                }

                // 走到这里：矩形是空/退化的。原版必然拒绝它并打印那条警告，
                // 而且**什么都不会做**。所以直接跳过，与原版行为等价，只是不刷警告。
                skippedCount++;

                // ⚠ 记日志这件事**必须单独包一层 try/catch**，不能让它跟判断结果绑在一起。
                //
                //   为什么（2026-10-07 实机验证抓到的）：返回 false 之前调 Log.Message，
                //   万一 Log 自己抛了异常（比如宿主环境里没有 Unity 的 ECall 支持），
                //   异常会一路穿出这个前缀。而 Harmony 的前缀一旦抛异常，
                //   **整个 BaseGen.Resolve 就会跟着失败** —— 那不是"少消一条警告"，
                //   而是把整张地图的生成流程掀翻。
                //
                //   判断结果（return false）与"顺便说一句"是两件事，必须解耦：
                //   日志打不出来就算了，该跳过还是要跳过。
                if (!announced)
                {
                    announced = true;
                    try
                    {
                        Log.Message("[GNH LocalFixes] 已开始静默 mimicSpawner 的无效矩形请求"
                            + "（首次遇到：rect=" + rect + "，宽 " + width + "、高 " + height + "）。"
                            + "这类请求原版必然拒绝且不做任何事，跳过它与原版行为完全等价；"
                            + "本模组的功能（RimJobWorld - Onahole Extension 的古代神殿 mimic 生成）不受影响。"
                            + "之后不再重复打印。");
                    }
                    catch (Exception)
                    {
                        // 日志打不出来不影响判断：下面照样返回 false。
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                // 判断逻辑出任何意外，都放行原版 —— 我们的检查再怎么样
                // 也不该改变游戏行为，更不该把地图生成弄崩。
                //
                // 这里的 Log.Warning 同样要包一层：它是**异常处理路径**上的代码，
                // 如果它自己再抛，就会把一个"本来已经被兜住"的小问题
                // 升级成穿出前缀的大异常。异常处理里绝不允许再抛异常。
                try
                {
                    Log.Warning("[GNH LocalFixes] mimicSpawner 矩形检查出错，已放行原版逻辑（不影响游戏）：" + ex.Message);
                }
                catch (Exception)
                {
                }
                return true;
            }
        }
    }
}
