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
    // 游戏原本的射程计算方法里，有两处直接使用「攻击者」这个对象，
    // 却没有先检查它是不是空的：
    //
    //     float num = (rangeStat == null) ? range : attacker.GetStatValue(rangeStat);
    //     ...
    //     Map mapHeld = attacker.MapHeld;
    //
    // 平时这样没问题，因为算射程的时候总是有个持枪的人。
    //
    // 【那到底炸在哪一行】
    //
    // 两处都可能，取决于这把武器有没有配 rangeStat（射程属性）：
    //   · 配了（绝大多数枪械都有）—— 第一行就炸，因为 attacker.GetStatValue 要解引用 attacker；
    //   · 没配 —— 第一行走的是 `range` 那个分支、根本不碰 attacker，
    //     于是要往下走到 `attacker.MapHeld` 才炸。
    //（2026-10-08 反编译逐行核对原方法后更正：原注释把崩溃点笼统写成「上面那两行」，
    //  读者会以为第一行在 rangeStat 为 null 时也会炸 —— 实际不会。
    //  不过结论不变：只要 attacker 是 null，这一趟必然在其中一行炸掉。）
    //
    // 【那什么时候会没有攻击者】
    //
    // CeleTech 模组有一个「自动切换武器」的组件，它会对收在背包容器里、
    // 当前没有主人的武器去询问射程。这时「攻击者」自然是空的，
    // 上面那两行就会抛空引用异常。
    //
    // 而这个检查每 15 tick 跑一次（CeleTech 的 CompAppWeaTransferAppPart.CompTick
    // 里 smartSwapCheckCounter 递减到 0 才做检查，随后把它重设为 15），
    // 所以大约每 0.25 秒就会抛一次；异常会中断该小人当 tick 的剩余更新。
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
        // ⚠ 参数名 attacker 不是随便起的，必须与原方法的参数名**完全一致**。
        //
        //   原方法签名是：Verse.VerbProperties.AdjustedRange(Verb ownerVerb, Thing attacker)
        //  （已用反编译核对过）。Harmony 是靠「参数名对得上」把原方法的实参喂进来的；
        //   一旦改了名字，这里就注入不到东西、只会得到 null ——
        //   而本补丁的全部判断都压在「attacker 是不是 null」上，
        //   那会把**每一次正常调用**都误判成异常情况，后果比不装这个补丁还糟。
        //
        // 这里刻意**不套 try/catch**：整个方法只有一次 null 比较和一次字段读，
        // 而 __instance 来自非静态方法的实例（不可能是 null），找不到任何会抛异常的点。
        // （本模组其它补丁入口都带 try/catch，那几处确实有反射/字典/日志调用。）
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
