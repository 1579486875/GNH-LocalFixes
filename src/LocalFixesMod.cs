using System;
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
    // 那为什么还要立这个标记？它挡的是：
    //   首次构造时抛异常，运行库的去重表里没能写下这一项（见
    //   LoadedModManager.CreateModClasses 里的 runningModClasses），于是下次还会被构造一次。
    //
    // 要说明白的是：**程序集被重新加载这种情况它挡不住** —— 那样一来所有静态状态
    // 都是全新的，标记必然从 false 开始。真要防那一条得靠卸载旧补丁，不是靠这个标记。
    //
    // 而 Harmony 装补丁的方式是「叠加」而不是「替换」：
    // 同一个方法上装十次，就会有十层代码依次执行，越玩越卡直到卡死。
    // 这个标记就是最后一道闸 —— 代价只有一次 bool 判断。
    //
    // 标记是在 PatchAll 返回之后才立起来的。
    //
    // 关于「PatchAll 失败时会怎样」（已用反编译逐条核对 Harmony 2.4.1）：
    //   PatchAll 内部是「一个补丁类一个补丁类地装」，而任何一类装失败时，
    //   Harmony 都会把它包成 HarmonyException **往外抛**，整个 PatchAll 就此中止。
    //   所以下面这个 catch 是**拦得到**的 —— 单类失败同样不会把标记立起来。
    //
    // 但有两件事必须知道：
    //   1. 抛出点之后的那些补丁类，这一次就不会安装了；
    //   2. 重试是从第一个补丁类重新装一遍，而 Harmony **不做去重** ——
    //      所以「装了一半 + 重试」会让已经装上的补丁再叠一层。
    //   好在 PatchAll 的失败窗口很窄（要么整体通过，要么在第一处就中止），
    //   而且我们的补丁类只有寥寥几个，实际风险很低。
    //
    // 如果将来需要更细的控制（某个补丁类失败只补装它自己），
    // 得改成用 Harmony.GetPatchInfo 逐个目标校验，而不是依赖这里的返回值。
    public class LocalFixesMod : Mod
    {
        public const string HarmonyId = "gnh.cn.cys.localfixes";

        internal static readonly Harmony HarmonyInstance = new Harmony(HarmonyId);

        private static bool patchesApplied;

        public LocalFixesMod(ModContentPack content) : base(content)
        {
            if (!patchesApplied)
            {
                try
                {
                    HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                    // 顺序是「先立标记、再打日志」，不能反过来。
                    //
                    // 理由：万一 Log.Message 自己抛了异常，标记就不会被立起来，
                    // 下次构造模组对象时又会全量 PatchAll 一遍 —— 而 Harmony 没有去重，
                    // 已经装好的补丁会被叠上第二层，正是我们最怕的那种「越玩越卡」。
                    // 少打一条日志只是少了条线索，补丁叠层才是真麻烦。
                    patchesApplied = true;
                    Log.Message("[GNH LocalFixes] Harmony patches applied (id=" + HarmonyId + ").");
                }
                catch (Exception ex)
                {
                    Log.Error("[GNH LocalFixes] PatchAll failed; it will be retried on the next mod construction: " + ex);
                }
            }

            // 下面三步各自隔离：任何一步失败都不许把整个构造函数掀翻。
            //
            // 为什么这么要紧（2026-10-02 事故的机制）：
            //   LoadedModManager.CreateModClasses() 里写的是
            //       runningModClasses[type] = (Mod)Activator.CreateInstance(type, modContentPack);
            //   —— **先构造、后登记**。构造函数一旦抛异常，去重表里就留不下这个类型，
            //   于是下次 CreateModClasses()（游戏内每次打开模组管理器都会跑一次）会再构造一遍，
            //   而 Harmony 补丁是叠加的 → 越玩越卡直到卡死。
            //   宁可某一步没装上，也不能让它带塌整个构造函数。
            //
            // 同时 catch 里**必须**打 Log.Error：那场事故的另一半教训是
            //   「异常被静默吞掉、补丁其实从没生效，却没人知道」。
            //   日志是排查者唯一能看出「这一步没装上」的线索。
            TryInstall("VFEEInstrumentSpaceFix", VFEEInstrumentSpaceFix.Install);
            TryInstall("SteamWorkshopHookFix", SteamWorkshopHookFix.Install);
            TryInstall("CrossPromotionUnpatch", CrossPromotionUnpatch.Install);
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
