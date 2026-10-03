using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：某个小人每隔一小会儿就报一次空引用错误。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // 游戏原本的射程计算方法里，有一句直接使用了「攻击者」这个对象，
    // 却没有先检查它是不是空的：
    //
    //     float num = (rangeStat == null) ? range : attacker.GetStatValue(rangeStat);
    //     ...
    //     Map mapHeld = attacker.MapHeld;
    //
    // 平时这样没问题，因为算射程的时候总是有个持枪的人。
    //
    // 【那什么时候会没有攻击者】
    //
    // CeleTech 模组有一个「自动切换武器」的组件，它会对收在背包容器里、
    // 当前没有主人的武器去询问射程。这时「攻击者」自然是空的，
    // 上面那两行就会抛空引用异常。
    //
    // 而这个计算是在小人的每帧更新里被调用的，所以异常会不断出现，
    // 每次都会把那个小人当帧的所有行为打断。
    //
    // 【我们怎么修】
    //
    // 在方法最前面加一段判断：
    //
    //   如果「攻击者」是空的 —— 那就没有属性来源、也没有地图可查，
    //   直接把武器的基础射程返回，并跳过原方法剩下的部分。
    //
    //   如果「攻击者」是正常的 —— 原方法（包括其它模组对它做的补丁）
    //   照常执行，我们的代码等于不存在。
    //
    // 只加了这一道空值检查，正常情况下的行为一点都没变。
    [HarmonyPatch(typeof(VerbProperties), "AdjustedRange")]
    public static class Patch_VerbProperties_AdjustedRange
    {
        [HarmonyPrefix]
        public static bool Prefix(VerbProperties __instance, Thing attacker, ref float __result)
        {
            if (attacker == null)
            {
                __result = __instance.range;
                return false;
            }
            return true;
        }
    }
}
