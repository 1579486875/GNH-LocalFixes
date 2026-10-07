using HarmonyLib;
using RimWorld;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：你在文化里亲手定好的「领袖头衔」，
    // 一改模因（或者点一次「随机符号」）就被游戏重新掷骰子、换成了别的名字。
    // ==========================================================================
    //
    // 【名字是怎么没的 —— 反编译出来的原文】
    //
    // 领袖头衔存在 RimWorld.Ideo 的两个字段上：
    //
    //     public string leaderTitleMale;      // 男领袖的头衔
    //     public string leaderTitleFemale;    // 女领袖的头衔
    //
    // 而在整个游戏里，给这两个字段赋值的地方几乎只有一个：
    // RimWorld.IdeoFoundation.GenerateLeaderTitle()。它的原文是：
    //
    //     public virtual void GenerateLeaderTitle()
    //     {
    //         if (ideo.classicMode)                          // ① 无 DLC 的经典模式
    //         {
    //             ideo.leaderTitleMale   = PreceptDefOf.IdeoRole_Leader.label;
    //             ideo.leaderTitleFemale = ideo.leaderTitleMale;
    //             return;
    //         }
    //         if (ideo.culture.leaderTitleMaker == null)     // ② 这个文化没有头衔生成规则
    //         {
    //             ideo.leaderTitleMale   = null;
    //             ideo.leaderTitleFemale = null;
    //             return;
    //         }
    //         GrammarRequest request = new GrammarRequest { Includes = { ideo.culture.leaderTitleMaker } };
    //         for (int i = 0; i < ideo.memes.Count; i++)
    //         {
    //             if (ideo.memes[i].generalRules != null)
    //             {
    //                 request.IncludesBare.Add(ideo.memes[i].generalRules);
    //             }
    //         }
    //         ideo.leaderTitleMale   = NameGenerator.GenerateName(request, null, false, "r_leaderTitle");  // ③ 现掷
    //         ideo.leaderTitleFemale = ideo.leaderTitleMale;  // 男女强制同一个
    //     }
    //
    // 看明白一件事就够了：**这个方法只要被调用，你写的名字就没了。**
    // 它不读旧值、不做比较、不打商量，直接覆盖。
    //
    // 【谁会在背后调用它 —— 全部 6 个调用点，已逐个反编译核对】
    //
    //     1. IdeoFoundation_Deity.Init              新建文化 / 读档初始化文化时
    //     2. IdeoUIUtility.DoName                   「随机符号」按钮（就在文化编辑界面里）
    //     3. IdeoUIUtility.<>c__DisplayClass116_0   文化界面里的另一处按钮回调
    //     4. Precept_Role.GenerateNameRaw           重算「领袖」这个职位的显示名时
    //     5. Dialog_ChooseMemes                     改模因的对话框   ← 最常撞上的就是它
    //     6. Dialog_ReformIdeo                      改革（Reform）文化的对话框
    //
    // 所以「改个模因，头衔就变了」这件事的来龙去脉是：
    //     你在界面上勾掉/勾上一个模因 → Dialog_ChooseMemes 确认时 →
    //     第 5 条调用 GenerateLeaderTitle() → 你的「主席」被重新掷成了「游击队区域指挥官」。
    //
    // 【为什么不能靠 nameLocked 锁住它】
    //
    // 普通戒律（Precept）的名字有 <nameLocked> 这个锁，原版 Ideo.RegenerateAllPreceptNames()
    // 里写得很清楚：
    //     foreach (Precept precept in precepts)
    //         if (precept.UsesGeneratedName && !precept.nameLocked)
    //             precept.RegenerateName();
    // 但领袖头衔**不是 Precept 的字段**，它是 Ideo 自己的两个字符串，
    // 完全不经过 nameLocked 那一套机制 —— 这就是它「怎么锁都锁不住」的根本原因。
    //
    // 【我们怎么修】
    //
    // 只做一件事：**在方法最前面看一眼，已经有名字了就直接把整个方法跳过。**
    //
    //     头衔是空的  → 放行，让原方法照常生成（新建文化的第一次必须靠它）
    //     头衔有内容  → 拦住，谁也别想覆盖它
    //
    // 于是：
    //   · 新建文化、第一次生成头衔 —— 和原版一模一样；
    //   · 之后你在 .rid 里或界面上定好的名字 —— 从此稳定不动；
    //   · 哪天想重新随机 —— 把模组设置里的开关关掉，点一次「随机符号」，
    //     再打开（或者干脆把头衔清空，它就会重新生成）。
    //
    // 【为什么不给 classicMode / leaderTitleMaker == null 开特例】
    //
    // 上面那两条分支写进去的东西分别是「固定的『领袖』文案」和「null」，
    // 都是**和当前值无关的常量**：
    //   · 第一次调用时头衔为空 → 我们放行 → 原方法照写；
    //   · 第二次调用时它已经是那个常量了 → 我们拦住 → 结果一模一样。
    // 所以不需要为它们写额外判断，行为天然一致。
    //
    // 【万一和别的模组撞车】
    //
    // 这里标了 [HarmonyPriority(Priority.Low)]，意思是「我们的判断排在后面做」：
    // 别的模组若也补这个方法做保护或改写，它们的代码多半会先跑完，再轮到我们。
    // 真要冲突了，模组设置里可以直接把本补丁关掉 —— 关掉之后行为 100% 等于原版。
    //
    // ⚠ 顺带记一个很容易记反的常量（已从 refs\0Harmony.dll 反射读出核实，
    //   运行时装完补丁再读回来，priority 也正是 200）：
    //     HarmonyLib.Priority 是**数值越大越先执行**：
    //       First=800, VeryHigh=700, High=600, HigherThanNormal=500, Normal=400,
    //       LowerThanNormal=300, Low=200, VeryLow=100, Last=0
    //   所以 Low(200) 的含义是「靠后」，不是「靠前」。
    //
    // 【关于「有模组重写了这个方法」】
    //
    // IdeoFoundation 是个**抽象类**，所以真正跑起来的永远是某个子类的实例；
    // 而 GenerateLeaderTitle 是它身上的一个 public virtual 方法（有方法体）。
    // 原版 1.6 里它**只有一个**具体子类 IdeoFoundation_Deity，并且那个子类
    // **没有重写**这个方法 —— 反编译核对过一遍，运行时反射又复核过一遍，
    // 所以补基类就等于补全部原版路径。
    // 假如将来某个模组定义了自己的 IdeoFoundation 子类并重写了 GenerateLeaderTitle，
    // 那个子类的版本不会被本补丁拦住；不过那种情况下，那个模组自己多半也在管头衔，
    // 我们不插手反而是对的。
    //
    // 【性能】
    //
    // 每次调用只做一两次引用比较加一次字符串判空，不分配任何对象。
    // 而它被调用的场合只有「新建文化 / 打开文化界面 / 点几个按钮」，
    // 不在任何 tick 循环里、更不是每帧 —— 性能开销按 0 计。
    //
    // 【想自己验一遍】
    //
    //   · 判断逻辑（LeaderTitlePolicy.cs）离线测试：
    //         cd E:\TAML\_work\gnh-localfixes-test
    //         dotnet run -c Release
    //   · 补丁真装真跑（独立进程里真的调用一次 GenerateLeaderTitle 对照行为）：
    //         cd E:\TAML\_work\gnh-localfixes-test
    //         powershell -ExecutionPolicy Bypass -File verify-patch-runtime.ps1
    [HarmonyPatch(typeof(IdeoFoundation), nameof(IdeoFoundation.GenerateLeaderTitle))]
    [HarmonyPriority(Priority.Low)]
    public static class Patch_IdeoFoundation_KeepLeaderTitle
    {
        // Prefix（前缀）= Harmony 的「插在原方法前面的那段代码」。
        // 它的返回值决定原方法跑不跑：
        //     return true;   → 继续执行原方法
        //     return false;  → 跳过原方法，直接返回
        //
        // 参数名 __instance 是 Harmony 的约定：它会被自动填成「当前正在被调用的那个对象」，
        // 也就是这里的 IdeoFoundation 实例。名字必须一模一样，不能改。
        [HarmonyPrefix]
        public static bool Prefix(IdeoFoundation __instance)
        {
            // 开关是「关」的时候，我们完全按原版来，等于这个补丁不存在。
            // 设置读不到时（几乎不会发生）按内置默认值「开启保护」走。
            GNHLocalFixesSettings settings = LocalFixesMod.Settings;
            bool featureEnabled = (settings == null) || settings.keepLeaderTitle;

            // 防御一：拿不到实例（正常不会发生）→ 不插手。
            if (__instance == null)
            {
                return true;
            }

            // 防御二：foundation 还没和文化绑定上。原版方法里会直接访问 ideo.xxx，
            // 这种状态下它自己就会出错，我们不抢这个责任，交给它自己处理。
            Ideo ideo = __instance.ideo;
            if (ideo == null)
            {
                return true;
            }

            // 真正的判断在 LeaderTitlePolicy.cs，单独放一个文件是为了能脱离游戏做测试。
            // 已经有头衔 → 跳过原方法，名字保住了。
            if (LeaderTitlePolicy.ShouldKeepExistingTitle(ideo.leaderTitleMale, featureEnabled))
            {
                return false;
            }

            // 还没有头衔 → 让原方法去生成。
            return true;
        }
    }
}
