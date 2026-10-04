using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这是整个修复补丁包的「入口」类。游戏会自动发现它，并在启动早期调用它的构造函数。
    // ==========================================================================
    //
    // 【为什么要放在这么早】
    //
    // 游戏通过 LoadedModManager.CreateModClasses() 来创建各个模组的对象。
    // 这个时机比「解析 Def 数据」更早，更比所有 [StaticConstructorOnStartup]
    // 类型的初始化早得多。
    //
    // 正是这个「早」的窗口，让我们来得及给一个本来会崩溃的静态构造函数打补丁，
    // 详见 VFEEInstrumentSpaceFix.cs。
    //
    // 【为什么要防重复】
    //
    // 先说清楚实际机制（已用反编译核对过，以免后来人照着错的印象改代码）：
    // 游戏在一局里确实会多次调用 LoadedModManager.LoadAllActiveMods —— 触发它的是
    // 开发者工具的热重载（PlayDataLoader.HotReloadDefs）和切换语言
    //（LanguageDatabase.SelectLanguage → PlayDataLoader.LoadAllPlayData），
    // **不含**打开游戏内的「模组」页面（反编译查证：LoadAllActiveMods 的调用点只有
    // PlayDataLoader.DoPlayLoad，以及 HotReloadDefs 里那个委托）。
    // 但其中的 CreateModClasses() 有一句 if (!runningModClasses.ContainsKey(type)) 去重 ——
    // runningModClasses 是个静态字段（在 LoadedModManager 的静态构造里创建）、
    // 条目由 CreateModClasses 逐类型写入，之后从不清空。
    // 所以正常路径下，同一个模组类的构造函数在一个进程里**只跑一次**。
    //
    // 那为什么还要立防重复的标记？它挡的是：
    //   首次构造时抛异常，运行库的去重表里没能写下这一项（见
    //   LoadedModManager.CreateModClasses 里的 runningModClasses），于是下次还会被构造一次。
    //
    // 要说明白的是：**程序集被重新加载这种情况它挡不住** —— 那样一来所有静态状态
    // 都是全新的，标记必然从空开始。真要防那一条得靠卸载旧补丁，不是靠这个标记。
    //
    // 而 Harmony 装补丁的方式是「叠加」而不是「替换」：
    // 同一个方法上装十次，就会有十层代码依次执行，越玩越卡直到卡死。
    // 所以每一个补丁类都必须在**装成功之后**立刻立起自己的标记 —— 代价只有一次集合查找。
    //
    // 【2026-10-04 的重要变更：不再用 PatchAll】
    //
    // 这里原本是一句 HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly())，
    // 现已改成逐类安装，理由写在下文 InstallAllPatchClasses 的注释里。
    // 一句话：PatchAll 遇到任何一个补丁类出错就整体中止；更麻烦的是它内部那句
    // GetTypes() 一旦因为「某个模组没装、程序集里有类型解析不了」而失败，
    // 整个补丁包会一个都装不上 —— 那正是「本地修复补丁反被别的模组拖死」的情形。
    public class LocalFixesMod : Mod
    {
        public const string HarmonyId = "gnh.cn.cys.localfixes";

        internal static readonly Harmony HarmonyInstance = new Harmony(HarmonyId);

        // 已经成功安装过的「补丁类」。
        //
        // 用「逐个类记录」而不是「一个全局 bool」，是为了让失败的类能单独重试，
        // 同时绝不重复安装已经装好的那些（Harmony 补丁叠加会造成越玩越卡）。
        private static readonly HashSet<Type> installedPatchClasses = new HashSet<Type>();

        public LocalFixesMod(ModContentPack content) : base(content)
        {
            InstallAllPatchClasses();

            // 下面这些补丁各自隔离：任何一步失败都不许把整个构造函数掀翻。
            //
            // 为什么这么要紧（2026-10-02 事故的机制）：
            //   LoadedModManager.CreateModClasses() 里写的是
            //       runningModClasses[type] = (Mod)Activator.CreateInstance(type, modContentPack);
            //   —— **先构造、后登记**。构造函数一旦抛异常，去重表里就留不下这个类型，
            //   于是下次 CreateModClasses() 会再构造一遍（触发它的是**开发者工具热重载**与
            //   **切换语言**；**不含**打开游戏内的「模组」页面 —— 见本文件开头那段反编译核对），
            //   而 Harmony 补丁是叠加的 → 越玩越卡直到卡死。
            //   宁可某一步没装上，也不能让它带塌整个构造函数。
            //
            // 同时 catch 里**必须**打 Log.Error：那场事故的另一半教训是
            //   「异常被静默吞掉、补丁其实从没生效，却没人知道」。
            //   日志是排查者唯一能看出「这一步没装上」的线索。
            TryInstall("VFEEInstrumentSpaceFix", VFEEInstrumentSpaceFix.Install);
            TryInstall("SteamWorkshopHookFix", SteamWorkshopHookFix.Install);
            TryInstall("CrossPromotionUnpatch", CrossPromotionUnpatch.Install);

            // 下面两个是 2026-10-04 根据游戏日志新增的修复，详见各自的文件头注释。
            // 安装方式都是「按类型名去找，找不到就置位跳过」，
            // 所以 CCOE 有没有装、装的是哪个版本，都不会牵连到上面那几个补丁。
            TryInstall("CcoeReflectionFix", CcoeReflectionFix.Install);
            TryInstall("TechHediffsZeroBudgetFix", TechHediffsZeroBudgetFix.Install);

            // 2026-10-04 新增：拦下游戏每帧刷的 InvalidCastException。
            // 那个崩溃来自 MusicManagerPlay.UpdateMusicFadeout —— 它去「乐器」分组里取东西、
            // 强行当成乐器建筑用，结果分组里混进了真实类型不是乐器的对象（def 与实际类型对不上）。
            // 详见 MusicManagerFadeoutFix.cs 的文件头注释（含原版源码与 IL 证据）。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("MusicManagerFadeoutFix", MusicManagerFadeoutFix.Install);

            // 2026-10-04 新增：修 XmlExtensions 自己的 <FindMod> 判定。
            //
            // 这是和 FindModLanguageFix **不同的另一条代码路径**：
            //   · 原版 Verse.PatchOperationFindMod → ModLister.HasActiveModWithName → FindModLanguageFix 管
            //   · XmlExtensions 的 FindMod          → 直接遍历 LoadedModManager.RunningMods → 本步管
            // v1.3.12 只修了前者，所以日志里「Patch operation FindMod(Royalty) failed」
            // 那 4 条依旧存在（一次启动 4 个模组的整块补丁失败）。
            //
            // 根因：官方 DLC 的显示名会被语言包翻译（Royalty → 皇权），
            // 而模组作者写的是英文短名，字符串比对必然失败。
            // 详见 XmlExtensionsFindModFix.cs 的文件头注释（含反编译证据与安全设计）。
            // 本步幂等：Install() 内部有自己的静态守卫，重复调用只会立刻返回。
            TryInstall("XmlExtensionsFindModFix", XmlExtensionsFindModFix.Install);
        }

        // ======================================================================
        // 逐个补丁类地安装（替代 Harmony.PatchAll 那种一把梭的写法）
        // ======================================================================
        //
        // 【为什么不直接用 PatchAll】
        //
        // PatchAll 内部是「先把程序集里所有类型都取出来（GetTypes），
        // 再一类一类地装」。有两处要命的地方：
        //
        //   1) GetTypes() 是**整体失败**的。
        //      本程序集里有一个类（ElToro_BAddon.JobDriver_BestialityInvite_Watching）
        //      在编译期引用了 ElToro_BAddon.dll 和 RJW.dll 的类型。
        //      哪天用户把 RJW 或 ElToro 禁用/卸载了，运行库去取那个类型时找不到依赖程序集，
        //      GetTypes() 就会抛 ReflectionTypeLoadException —— **整批类型都拿不到**，
        //      于是整个补丁包一个都装不上。
        //      这正是「本地修复补丁反而被某个没启用的模组拖死」的情形。
        //
        //   2) 只要有一个类装失败，PatchAll 会直接中止，排在它后面的类全部不装。
        //
        // 【现在的做法】
        //
        //   * 取类型时兜住 ReflectionTypeLoadException，把**能加载的那部分**继续用作候选
        //    （取不到的那几个本来就是引用了缺失模组的类，对应功能本来也用不上）；
        //   * 每个补丁类单独 try/catch：失败只记一条 Error，不影响别的类；
        //   * 每个类成功安装后记进 installedPatchClasses，
        //     下次（游戏里每次打开模组管理器都会重新构造本模组对象）直接跳过 ——
        //     既不会漏装，也不会叠层。
        //
        // 一句话：**任何单个补丁的失败，都不再可能拖垮整个补丁包。**
        private static void InstallAllPatchClasses()
        {
            Type[] candidates = LoadAllTypesTolerantly(Assembly.GetExecutingAssembly());
            if (candidates == null)
            {
                return;
            }

            int installedThisPass = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                Type type = candidates[i];
                if (type == null || !IsHarmonyPatchClass(type))
                {
                    continue;
                }
                if (InstallPatchClass(type))
                {
                    installedThisPass++;
                }
            }

            if (installedThisPass > 0)
            {
                Log.Message("[GNH LocalFixes] Harmony patch classes installed this pass: " + installedThisPass + ".");
            }
        }

        // 取类型；遇到「部分类型加载不了」不放弃，把能用的挑出来继续用。
        private static Type[] LoadAllTypesTolerantly(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 这种情况基本只有一个原因：本程序集里有类型引用了当前没装的模组程序集。
                // 这是**允许**的 —— 那些类型对应的功能本来就不存在，跳过它们即可，
                // 其余补丁照常工作。
                Type[] partial = ex.Types;
                List<Type> usable = new List<Type>();
                if (partial != null)
                {
                    for (int i = 0; i < partial.Length; i++)
                    {
                        if (partial[i] != null)
                        {
                            usable.Add(partial[i]);
                        }
                    }
                }
                Log.Warning("[GNH LocalFixes] Some types could not be loaded (a referenced mod assembly is probably missing). "
                    + usable.Count + " type(s) are still usable and the rest of the patch pack continues. First loader error: "
                    + FirstLoaderError(ex));
                return usable.ToArray();
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] Could not enumerate types for patching: " + ex);
                return null;
            }
        }

        // LoaderExceptions 里常常夹着一堆 null，挑第一条有内容的报出来就够了。
        private static string FirstLoaderError(ReflectionTypeLoadException ex)
        {
            Exception[] errors = ex.LoaderExceptions;
            if (errors == null || errors.Length == 0)
            {
                return "(none reported)";
            }
            for (int i = 0; i < errors.Length; i++)
            {
                if (errors[i] != null)
                {
                    return errors[i].GetType().Name + ": " + errors[i].Message;
                }
            }
            return "(all null)";
        }

        // 只有带 [HarmonyPatch] 的类才需要交给 Harmony 处理。
        // 不带还硬塞给 PatchClassProcessor 的话，它会直接抛异常（白记一条 Error）。
        private static bool IsHarmonyPatchClass(Type type)
        {
            try
            {
                object[] attributes = type.GetCustomAttributes(typeof(HarmonyPatch), true);
                return attributes != null && attributes.Length > 0;
            }
            catch (Exception)
            {
                // 连特性都读不到的类（依赖缺失等），当作「不是补丁类」跳过。
                return false;
            }
        }

        // 装一个补丁类。成功安装返回 true。
        private static bool InstallPatchClass(Type type)
        {
            lock (installedPatchClasses)
            {
                if (installedPatchClasses.Contains(type))
                {
                    return false;
                }

                try
                {
                    List<MethodInfo> patched = HarmonyInstance.CreateClassProcessor(type).Patch();

                    // 顺序是「先立标记、再打日志」，不能反过来：
                    // 万一 Log.Message 自己抛了异常，标记也已经立住，
                    // 不会导致下次重复安装、把同一批补丁叠成两层。
                    installedPatchClasses.Add(type);

                    Log.Message("[GNH LocalFixes] Patch class installed: " + type.Name
                        + " (" + ((patched != null) ? patched.Count : 0) + " target method(s)).");
                    return true;
                }
                catch (Exception ex)
                {
                    // 不立标记 → 下次构造本模组对象时会再试一次；但不影响其它任何补丁类。
                    Log.Error("[GNH LocalFixes] Patch class " + type.Name
                        + " failed to install; other patches are unaffected, and this one will be retried: " + ex);
                    return false;
                }
            }
        }

        // 把「装一步补丁」包起来：失败只记日志，绝不往外抛。
        // 理由见构造函数里那段注释（构造函数抛异常会破坏 mod 类的去重登记）。
        private static void TryInstall(string name, Action install)
        {
            try
            {
                install();
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] " + name + ".Install() failed; that fix is NOT active this session: " + ex);
            }
        }
    }
}
