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
    // 游戏在一局里确实会多次调用 LoadedModManager.LoadAllActiveMods
    //（打开游戏内的「模组」页面等操作都会触发），但其中的 CreateModClasses()
    // 有一句 if (!runningModClasses.ContainsKey(type)) 去重 —— runningModClasses
    // 是个静态字典、只在类型静态构造里写入一次、之后从不清空。
    // 所以正常路径下，同一个模组类的构造函数在一个进程里**只跑一次**。
    //
    // 那为什么还要立这个标记？因为它挡的是两条罕见但真实的路径：
    //   1. 首次构造时抛异常，字典里没能写下这一项，于是下次还会被构造一次；
    //   2. 程序集被重新加载（更换 Type 对象），去重表认不出来是「同一个」。
    //
    // 而 Harmony 装补丁的方式是「叠加」而不是「替换」：
    // 同一个方法上装十次，就会有十层代码依次执行，越玩越卡直到卡死。
    // 这个标记就是最后一道闸 —— 代价只有一次 bool 判断。
    //
    // 标记只在 PatchAll 真的成功之后才立起来。这样万一这次失败了，
    // 下次还有机会再试一遍；而不会因为一次偶然失败，
    // 就把整个补丁包永久地、悄无声息地关掉。
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
                    // 先打日志、再立标记：万一 Log.Message 自己抛了异常，
                    // 标记就不会被立起来，日志里那句 "will be retried" 才名副其实。
                    Log.Message("[GNH LocalFixes] Harmony patches applied (id=" + HarmonyId + ").");
                    patchesApplied = true;
                }
                catch (Exception ex)
                {
                    Log.Error("[GNH LocalFixes] PatchAll failed; it will be retried on the next mod construction: " + ex);
                }
            }

            VFEEInstrumentSpaceFix.Install();
            SteamWorkshopHookFix.Install();
            CrossPromotionUnpatch.Install();
        }
    }
}
