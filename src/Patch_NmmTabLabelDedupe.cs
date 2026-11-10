// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 修：NMM 整合包的两个标签页撞名（【49】）
    // ==========================================================================
    //
    // 【现象】
    //   角色面板的页签栏里出现**两个名字一模一样**的标签页，点进去却是两套不同的界面。
    //
    // 【根因：上游两个类的 labelKey 写了同一个字符串】
    //
    //   反编译 dord.nuditymattersmore 的 NudityMattersMore.dll（1.7 版，358,912 字节）：
    //
    //     // NudityMattersMore.ITab_Pawn_NMM              ← 打开是 Awareness 界面
    //     public ITab_Pawn_NMM()
    //     {
    //         ((InspectTabBase)this).labelKey = "NMM_Nudity";
    //         ((InspectTabBase)this).tutorTag = "NMM_Nudity";
    //         ((InspectTabBase)this).size = new Vector2(770f, 510f);
    //     }
    //
    //     // NudityMattersMore_opinions.ITab_Pawn_NMMOpinions   ← 打开是「看法查看器」
    //     public ITab_Pawn_NMMOpinions()
    //     {
    //         ((InspectTabBase)this).labelKey = "NMM_Nudity";    // ← 同一个 key
    //         ((InspectTabBase)this).tutorTag = "NMM_Nudity";
    //         ((InspectTabBase)this).size = new Vector2(1344f, 510f);
    //     }
    //
    //   还有第三个类 NudityMattersMore.ITab_Pawn_NMMRouter（作者后来加的、带
    //   「风格：强化 / Style: Vanilla」切换的那套），它**继承自 ITab_Pawn_NMMOpinions**，
    //   所以也带着同一个 labelKey。
    //
    //   ⚠ 两个构造函数里写的是**同一个字符串字面量**，C# 编译时会把它们合并成
    //     常量池里的一份 —— 所以在 dll 里 grep `NMM_Nudity` 只命中 **1 处**，
    //     很容易误判成「只有一个类用了它」。这是本项排查时踩过的坑。
    //
    // 【为什么这是上游的问题，不是汉化的】
    //   · 我们的汉化包（shark510.nuditymattersmoreopinions.zhfix）只有
    //     Bootstrap / Patches / Zh / OwnListInjector 四个类，全是打 Harmony 补丁做
    //     文字替换，**不注册任何标签页**；
    //   · `NMM_Nudity` 这个 key 上游本来就给了中文（「裸露」）；
    //   · 真正撞名的是**上游两个类共用同一个 key** —— 汉化只是把这个 key 翻成了
    //     中文按，翻成什么都会撞。
    //
    // 【我们怎么修】
    //   不动上游任何文件，只在上游那两个构造函数跑完之后，把**后两个**页签的
    //   labelKey 换成各自的名字：
    //
    //     ITab_Pawn_NMM          → 保持 NMM_Nudity          （「裸体」）
    //     ITab_Pawn_NMMOpinions  → NMM.OpinionTab.Title     （「看法查看器」，这个 key 上游已有中文）
    //     ITab_Pawn_NMMRouter    → NMM_Tab_Router           （「裸体（强化）」）
    //
    //   注意 Router 继承自 Opinions：Postfix 挂在 Opinions 的构造函数上时，
    //   **Router 构造也会走到**（先跑 base ctor）。所以 Postfix 里必须按
    //   `__instance.GetType().Name` 再分一次流，否则 Router 会被改成和 Opinions 同名。
    //
    // 【为什么挂在构造函数上、而不是挂 TabLabel】
    //   `InspectTabBase.labelKey` 是 **public 字段**（反编译确认：`040003B1:F`，
    //   `String labelKey`，Public），构造函数里直接赋值。
    //   改字段比改建属性更省事，也不受 TabLabel 是否被 override 影响。
    //
    // 【自动让路】
    //   · 找不到 `NudityMattersMore_opinions.ITab_Pawn_NMMOpinions`（没装 / 改名了）
    //     → TargetMethod 返回 null，本补丁不安装，只打一条 Message；
    //   · 上游哪天自己把 labelKey 改成不同的值了 → 我们这个 Postfix 只是把它改成
    //     我们想要的名字，行为仍然正确，不会报错。
    //
    // 【性能】
    //   只在角色面板第一次构造这两个标签页时各跑一次，**不在任何 tick / 渲染路径上**。
    // ==========================================================================
    [HarmonyPatch]
    internal static class Patch_NmmTabLabelDedupe
    {
        private const string OpinionsTabTypeName = "NudityMattersMore_opinions.ITab_Pawn_NMMOpinions";
        private const string RouterTabTypeName = "NudityMattersMore.ITab_Pawn_NMMRouter";

        /// <summary>上游「看法查看器」页签原本就该用的 key（上游已有这个 key 的中文）。</summary>
        private const string KeyOpinions = "NMM.OpinionTab.Title";

        /// <summary>Router 那套（强化版）专用 key，由本补丁包的语言文件提供。</summary>
        private const string KeyRouter = "NMM_Tab_Router";

        private static Type opinionsTabType;
        private static bool logged;

        /// <summary>
        /// Harmony 的目标解析：找不到类型就返回 null ⇒ 本补丁不安装（不是报错）。
        /// </summary>
        internal static MethodBase TargetMethod()
        {
            try
            {
                opinionsTabType = AccessTools.TypeByName(OpinionsTabTypeName);
                if (opinionsTabType == null)
                {
                    if (!logged)
                    {
                        logged = true;
                        Log.Message("[GNH LocalFixes] 没找到 " + OpinionsTabTypeName
                            + "（多半是没装 NMM 整合包），【49】页签改名不安装。");
                    }
                    return null;
                }
                MethodBase ctor = AccessTools.Constructor(opinionsTabType, Type.EmptyTypes);
                if (ctor == null && !logged)
                {
                    logged = true;
                    Log.Warning("[GNH LocalFixes] 找到 " + OpinionsTabTypeName
                        + " 但找不到无参构造函数，【49】未安装（上游可能改了构造方式）。");
                }
                return ctor;
            }
            catch (Exception ex)
            {
                if (!logged)
                {
                    logged = true;
                    Log.Error("[GNH LocalFixes] 【49】解析目标时出错，本项跳过：" + ex);
                }
                return null;
            }
        }

        /// <summary>
        /// 构造函数跑完、labelKey 已经被上游赋值之后再改。
        /// </summary>
        private static void Postfix(object __instance)
        {
            try
            {
                InspectTabBase tab = __instance as InspectTabBase;
                if (tab == null)
                {
                    return;
                }

                string actual = __instance.GetType().Name;

                // Router 继承自 Opinions，所以它构造时也会跑到这里 —— 必须分流，
                // 否则两个页签会被改成同一个名字（那就白改了）。
                if (string.Equals(actual, "ITab_Pawn_NMMRouter", StringComparison.Ordinal))
                {
                    tab.labelKey = KeyRouter;
                    tab.tutorTag = KeyRouter;
                    LogOnce("ITab_Pawn_NMMRouter", KeyRouter);
                }
                else
                {
                    tab.labelKey = KeyOpinions;
                    tab.tutorTag = KeyOpinions;
                    LogOnce(actual, KeyOpinions);
                }
            }
            catch (Exception ex)
            {
                // 这里出错最多是「页签名字没改成功」，绝不能让它把构造流程打断。
                Log.ErrorOnce("[GNH LocalFixes] 【49】改页签名字时出错（已忽略）：" + ex, 0x4E4D5431);
            }
        }

        /// <summary>同一条改名只报一次（这两个页签可能被多次构造）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> reported =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void LogOnce(string typeName, string key)
        {
            try
            {
                if (reported.Add(typeName + "|" + key))
                {
                    Log.Message("[GNH LocalFixes] 【49】页签改名：" + typeName
                        + " 的 labelKey → " + key + "（上游两个页签原本写的是同一个 key，"
                        + "页签栏里会显示成两个一模一样的名字）。");
                }
            }
            catch (Exception)
            {
                // 日志本身失败不影响改名结果。
            }
        }
    }
}
