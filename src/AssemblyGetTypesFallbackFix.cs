using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：某个模组的 dll 引用了「根本不存在的程序集」时，
    // 会把**所有**调用 Assembly.GetTypes() 的模组一起拖下水。
    // ==========================================================================
    //
    // 【先说说 GetTypes() 是干什么的】
    //
    // 一个 dll 里装着很多「类」。Assembly.GetTypes() 的意思是：
    // 「把这个 dll 里所有的类，列一份完整清单给我」。
    //
    // 模组启动时经常要干这件事。例如《近战动画》(Melee Animation) 会：
    //     对每一个已加载的 dll 都问一遍「你里面有哪些类」，
    //     然后挑出所有「继承了 Verb_MeleeAttack（会打拳）」的类，
    //     给它们挂上自己的命中检测。
    //
    // 【它的失败方式很坑：全有或全无】
    //
    // 列清单的时候，.NET 要逐个确认「这个类能不能用」——
    // 它的父类在不在、它字段的类型在不在、它引用的东西在不在。
    //
    // 只要有**一个**类确认不了，它不会「跳过这一个、接着列剩下的」，
    // 而是直接抛 ReflectionTypeLoadException —— **整份清单一个都不给**。
    //
    // 打个比方：老师点名，班上有个学生的档案不全（他写着「我是 XX 家的孩子」，
    // 可 XX 家根本不在这个学校）。老师不是把他记成「缺席」然后继续往下点，
    // 而是当场宣布「今天点名作废」——整份名单一起作废。
    //
    // 【2026-10-08 实机抓到的实例（完整证据链）】
    //
    // 日志里每次启动都出现：
    //     Exception in post-load event 'Apply final patches':
    //     System.Reflection.ReflectionTypeLoadException
    //     Could not resolve type ... 'AchievementsExpanded.TrackerBase'
    //       in assembly 'AchievementsExpanded, Version=1.4.8986.18107'
    //       at AM.Patches.Patch_Verb_MeleeAttack_ApplyMeleeDamageToTarget.PatchAll()
    //
    // 顺着查下去：
    //   · 模组「Geneva Checklist」（工坊 3339044171）里有个
    //         1.6\Assemblies\GenevaChecklistAchievements.dll
    //     是「成就联动」组件，它引用了 AchievementsExpanded 程序集；
    //   · 但该组件被作者放进了**无条件加载**的目录 —— 看它的 loadfolders.xml，
    //     <li>1.6</li> 这一项不带 IfModActive 条件，而真正该有条件的那一项
    //     （1.6/Mods/AchievementsExpanded）里**根本没有放那个 dll**；
    //   · AchievementsExpanded 这个模组本身也没启用，且它的 1.5 / 1.6
    //     目录下只有 Defs、没有程序集。
    //   于是每次启动，这个组件都会去解析一个不存在的类型。
    //
    // 更麻烦的是 Melee Animation 那段代码的写法（已反编译核对）：
    //
    //     foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    //     {
    //         Type[] types = assembly.GetTypes();     // ← 这一句不在 try 里
    //         foreach (Type type in types)
    //         {
    //             ...
    //             try   { ...打补丁... }
    //             catch (Exception e) { Core.Error(...); }   // try 只包住打补丁
    //         }
    //     }
    //
    // GetTypes() 一抛，**整个循环当场中断**，排在后面的 dll 全都不再扫。
    // 所以后果不是「某一个补丁没装上」，而是「一批补丁都没装上」——
    // 而且日志里只有孤零零一条 Exception，根本看不出漏了多少。
    //
    // 【我们的做法】
    //
    // 给 GetTypes() 挂一个「收尾器」—— Harmony 的 Finalizer，
    // 字面意思就是「最后接手的那个人」：
    //
    //   · 一切正常时        —— 什么都不做，原样返回（与没装这个补丁完全一样）
    //   · 抛的是类型加载失败 —— 把「确认不了的那些」剔掉，把**能用的那些**交出去，
    //                          并且不让异常继续往上抛
    //   · 抛的是别的异常     —— 不插手，照原样抛出去（免得把真问题盖住）
    //
    // 也就是说：从「一整份清单作废」变成「少几个类，但清单还能用」。
    // 对 Melee Animation 来说，就是「跳过那个坏掉的 dll，继续扫剩下的」，
    // 于是它的补丁能装全。
    //
    // 【为什么这样做是安全的】
    //
    //   1. 它只在**本来就要炸**的那一刻才改变行为。正常路径一行都不干涉，
    //      所以不可能让原本正常的模组出问题。
    //   2. 抛的如果不是「类型加载失败」，我们原样抛出去 —— 不掩盖问题。
    //   3. 被剔掉的那些类**本来就是用不了的**（.NET 自己都没法确认它们），
    //      所以「不把它们交给调用方」不会让谁少拿到能用的东西。
    //   4. 绝不返回 null：调用方通常直接 foreach，拿到 null 会立刻变成
    //      空引用异常，比原来的问题更难查。清单为空时给一个空数组。
    //
    // 【已知的取舍 —— 写在这里，免得以后有人踩】
    //
    // 如果某个模组写了「GetTypes 失败就走备用方案」这种代码，兜底之后它会
    // 以为「没失败」，于是走正常路径、拿到的是一份不完整的清单。
    // 这种情况很少见，但确实存在 —— 所以每次拦截都**打一条警告**，
    // 写明是哪个原因、跳过了几个类，让它看得见，而不是被悄悄藏起来。
    //
    // 【安装时机】
    //
    // 在 LocalFixesMod 构造函数里、**排在其它修复之前**安装。
    // 模组类构造发生在「模组加载」阶段，早于 Def 解析与 post-load 事件；
    // 而 Melee Animation 的 PatchAll 是在 post-load 事件里跑的，来得及。
    //
    // 【为什么要遍历所有实现】
    //
    // ⚠ 2026-10-08 订正：这一节原先写的是「Assembly.GetTypes() 是虚方法，
    //   Mono 里真正干活的是它的某个子类实现（RuntimeAssembly / MonoAssembly 之类）」——
    //   那是**没核实过的推测，而且不成立**。
    //   反编译 Unity 自带的 mscorlib 核对：Assembly.GetTypes(bool) 是
    //   internal virtual、isAbstract = false，它的 overrideIds 只指向
    //   AssemblyBuilder；**没有任何 Mono 子类覆写 GetTypes()**。
    //   也就是说：只给基类 Assembly.GetTypes() 打补丁，在当前 Unity 上就已经生效。
    //
    //   那为什么代码仍然遍历所有子类？两点：
    //     · 万一将来 Mono / Unity 改了实现方式（把 GetTypes 挪进子类），这套写法自动跟上；
    //     · 某些宿主里确实存在覆写者（例如动态程序集的 AssemblyBuilder）。
    //   换句话说：这是**防御性设计**，不是当前生效的必要条件。
    //   判据是「m.DeclaringType == 自己」，不依赖具体类型名叫什么。
    //
    // 本步幂等：**只靠 installed 这个静态守卫**。
    //
    // ⚠ 不要写成「installed 守卫 + Harmony 自身的重复补丁检测」—— 2026-10-08
    //   反编译核对 0Harmony 2.4.1 的 PatchInfo.Add 之后确认：它就是一句无条件的
    //       list.AddRange(add.Where(m => m != null).Select(...));
    //   **没有任何去重判断**。也就是说，一旦 installed 守卫被拿掉，
    //   补丁就会老老实实叠一层 —— 这里没有第二道防线。
    // ==========================================================================
    internal static class AssemblyGetTypesFallbackFix
    {
        private static bool installed;

        // 日志去重：同一个原因只报一次，避免刷屏。
        private static readonly object ReportLock = new object();
        private static readonly HashSet<string> ReportedReasons = new HashSet<string>();

        // 「同一种失败原因最多记住多少条」。现实里失败原因只有个位数，
        // 给 64 是留足余量；有它兜着，这个集合就不可能无限长大。
        private const int MaxReportedReasons = 64;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                List<MethodBase> targets = FindAllGetTypesImplementations();
                if (targets.Count == 0)
                {
                    // 找不到就**不置位**：留着下次再试，而不是永久放弃。
                    Log.Error("[GNH LocalFixes] Could not locate any Assembly.GetTypes() implementation; "
                        + "the get-types safety net is deferred (will retry).");
                    return;
                }

                MethodInfo finalizer = AccessTools.Method(
                    typeof(AssemblyGetTypesFallbackFix), nameof(GetTypesFinalizer));
                if (finalizer == null)
                {
                    Log.Error("[GNH LocalFixes] Could not locate GetTypesFinalizer; "
                        + "the get-types safety net is deferred (will retry).");
                    return;
                }

                int patched = 0;
                StringBuilder patchedNames = new StringBuilder();
                for (int i = 0; i < targets.Count; i++)
                {
                    MethodBase target = targets[i];
                    try
                    {
                        LocalFixesMod.HarmonyInstance.Patch(target, null, null, null,
                            new HarmonyMethod(finalizer));
                        patched++;
                        if (patchedNames.Length > 0)
                        {
                            patchedNames.Append(", ");
                        }
                        patchedNames.Append(Describe(target));
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[GNH LocalFixes] Could not patch " + Describe(target)
                            + " for the get-types safety net: " + ex.Message);
                    }
                }

                if (patched == 0)
                {
                    Log.Error("[GNH LocalFixes] Every Assembly.GetTypes() implementation failed to patch; "
                        + "the get-types safety net is deferred (will retry).");
                    return;
                }

                installed = true;
                Log.Message("[GNH LocalFixes] Installed the Assembly.GetTypes() safety net on "
                    + patched + " implementation(s) (" + patchedNames
                    + "). This stops one mod's broken assembly reference from aborting every other "
                    + "mod's type scan; details in AssemblyGetTypesFallbackFix.cs.");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Failed to install the Assembly.GetTypes() safety net "
                    + "(will retry): " + ex);
            }
        }

        // 找出「所有自己声明了 GetTypes() 的 Assembly 子类」。
        //
        // 只看 m.DeclaringType == t 的类型：
        //   · 覆写者（MonoAssembly / RuntimeAssembly 之类）满足；
        //   · 若某个版本里 GetTypes 不是虚方法而是直接在 Assembly 上实现的，也满足；
        //   · 单纯继承基类实现的派生类会被排除，避免同一个方法被重复打补丁。
        private static List<MethodBase> FindAllGetTypesImplementations()
        {
            List<MethodBase> result = new List<MethodBase>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly asm = assemblies[i];
                if (asm == null)
                {
                    continue;
                }

                Type[] types = SafeGetTypes(asm);
                for (int j = 0; j < types.Length; j++)
                {
                    Type type = types[j];
                    if (type == null || !typeof(Assembly).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo method;
                    try
                    {
                        method = type.GetMethod("GetTypes", BindingFlags.Public | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (method == null || method.IsAbstract)
                    {
                        continue;
                    }
                    if (method.DeclaringType != type)
                    {
                        continue;
                    }

                    result.Add(method);
                }
            }

            return result;
        }

        // 安装期间本补丁还没装上，所以这里必须先自己兜一次。
        private static Type[] SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] partial = ex.Types;
                if (partial == null)
                {
                    return new Type[0];
                }
                List<Type> list = new List<Type>(partial.Length);
                for (int i = 0; i < partial.Length; i++)
                {
                    if (partial[i] != null)
                    {
                        list.Add(partial[i]);
                    }
                }
                return list.ToArray();
            }
            catch (Exception)
            {
                // 别的异常（例如动态程序集自己抛）就当它没有类型可用。
                return new Type[0];
            }
        }

        // 收尾器：Harmony 保证它**每次调用都会跑**（不管有没有异常），
        // 所以第一件事就是判断「到底有没有出事」。
        private static Exception GetTypesFinalizer(Exception __exception, ref Type[] __result)
        {
            // 正常路子：什么都没发生，原样放行。
            if (__exception == null)
            {
                return null;
            }

            ReflectionTypeLoadException loadFailure = __exception as ReflectionTypeLoadException;
            if (loadFailure == null)
            {
                // 不是「类型加载失败」就一概不插手 —— 免得把真问题藏起来。
                return __exception;
            }

            Type[] partial = loadFailure.Types;
            List<Type> usable = new List<Type>();
            int failed = 0;
            if (partial != null)
            {
                for (int i = 0; i < partial.Length; i++)
                {
                    if (partial[i] == null)
                    {
                        failed++;
                    }
                    else
                    {
                        usable.Add(partial[i]);
                    }
                }
            }

            // 绝不给调用方返回 null（理由见文件头「为什么这样做是安全的」第 4 条）。
            __result = usable.ToArray();

            ReportOnce(loadFailure, usable.Count, failed);

            // 返回 null = 把异常吞掉，让调用方继续跑完剩下的程序集。
            return null;
        }

        // 打一条「我兜住了一次」的警告。
        //
        // ⚠ 这里**必须**走 LocalFixesMod.SafeLogWarning，绝不能写裸的 Log.Warning。
        //    2026-10-08 用离线测试宿主实测（T6）：让 Log.Warning 抛异常，
        //    裸写法会让异常**穿出收尾器**，于是本该正常返回类型数组的 GetTypes()
        //    变成抛异常 —— 我们去救别人，反倒把别人打得更惨。
        //
        //    为什么 Harmony 不帮我们兜住？看 Harmony 生成的代码就知道（MethodCreator）：
        //      · 正常路径上的收尾器调用是 AddFinalizers(catchExceptions: false)，
        //        而它位于外层 .try 之内 —— 收尾器自己抛的异常会被外层 catch 接住，
        //        结果是「原方法正常的返回值丢了、异常反倒传给了调用方」；
        //      · 只有异常路径上的那次（catchExceptions: true）才自带 try/catch。
        //    也就是说：**正常路径上没有任何保护**。这条只能我们自己守。
        //
        // 去重：同一个原因只报一次，避免刷屏（key = 失败个数 + 首个错误前 96 字）。
        // 上限：Set 最多记 64 条 —— 够覆盖任何现实情况，同时杜绝理论上的无界增长。
        private static void ReportOnce(ReflectionTypeLoadException ex, int usableCount, int failedCount)
        {
            string reason = DescribeFirstError(ex);
            string key = failedCount.ToString() + "|" + (reason.Length > 96 ? reason.Substring(0, 96) : reason);

            lock (ReportLock)
            {
                if (ReportedReasons.Count >= MaxReportedReasons)
                {
                    return;
                }
                if (!ReportedReasons.Add(key))
                {
                    return;
                }
            }

            LocalFixesMod.SafeLogWarning("[GNH LocalFixes] 已兜住一次「类型清单作废」：有 " + failedCount
                + " 个类无法确认（多半是它引用了没装的模组），已跳过它们、"
                + "把其余 " + usableCount + " 个类交给调用方继续处理。"
                + "这不会修好那个模组本身，但不会再连累别的模组扫不到类型。原因：" + reason);
        }

        private static string DescribeFirstError(ReflectionTypeLoadException ex)
        {
            try
            {
                Exception[] inner = ex.LoaderExceptions;
                if (inner != null)
                {
                    for (int i = 0; i < inner.Length; i++)
                    {
                        if (inner[i] != null)
                        {
                            return inner[i].Message;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // LoaderExceptions 自己也可能抛，忽略，退回外层消息。
            }
            return ex.Message;
        }

        private static string Describe(MethodBase method)
        {
            Type declaring = method.DeclaringType;
            string owner = (declaring != null) ? declaring.FullName : "?";
            return owner + "." + method.Name + "()";
        }
    }
}
