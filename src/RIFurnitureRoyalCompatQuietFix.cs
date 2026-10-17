using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个补丁解决的是：每次启动固定刷 54 条 ERROR ——
    //
    //     [RI-Furniture][RoyalCompat] 皇权房间要求补丁未生效：爵位「Acolyte」的谒见厅要求中
    //                              缺少本包家具「RI_Building_SuperDragonThrone」。...
    //     （爵位 × 要求项 逐条一条，加一条汇总，实测 54 条）
    //
    // 刷屏的模组是「[1.7]边缘仙路-雕梁画栋 RimImmortal-Furniture」
    // （packageId = RI.RimImmortal.Furniture，工坊 3478098371，v1.1.2）。
    // ==========================================================================
    //
    // 【第一件事：这些红字说的是真的吗 —— 是，而且永远不会变好】
    //
    // RI-Furniture 的 Patches\RI_Patch_RoyalTitles_Empire.xml 想把自己家的家具
    // （雕龙金漆龙椅 / 金柱 / 屏风）加进帝国爵位的「谒见厅 / 卧室要求」列表里。
    // 它用的是 PatchOperationConditional —— 「列表里那个节点还在，我才追加」。
    //
    // 而本机同时装着 disroom.mashiro（工坊 3297881350，模组名就是
    // disabledroomRequirements「禁用所有房间要求」）。它在 1.6\Patches\RoyalTitles_VFEEmpire.xml 里：
    //
    //     <PatchOperationReplace MayRequire="OskarPotocki.VFE.Empire">
    //       <xpath>Defs/RoyalTitleDef[@ParentName="DukeBase" or "BaseEmpireTitle"
    //                                  or "ConsulBase" or "StellarchBase"]/throneRoomRequirements</xpath>
    //       <value><throneRoomRequirements Inherit="False" /></value>     ← 整个换空
    //     </PatchOperationReplace>
    //
    // 它把 throneRoomRequirements / bedroomRequirements / modExtensions **整片清空**。
    // 于是 RI 要追加的那个节点根本不存在 → Conditional 静默跳过 → RI 启动自检发现
    // 自家家具没进列表 → 打 54 条「未生效」。
    //
    // **这不是 bug，是两份设计目标直接对撞**：disroom 的整个存在意义就是「让爵位不再需要
    // 任何房间要求」，而 RI 想往里加要求。用户是**故意**装 disroom 的。
    //
    // 【第二件事：调加载顺序没用】（这一点查过，别白试）
    //
    // 本机 ri.rimimmortal.furniture 排 [344]、disroom.mashiro 排 [1145]。
    // 就算把 RI 挪到 disroom 之后 —— **列表已经被清空了**，那个 Conditional 仍然匹配不到，
    // 照样跳过、照样报 54 条。清空是不可逆的单向操作，顺序救不了。
    //
    // 【第三件事：那怎么办 —— 只静音，不改行为】
    //
    // 要让 RI 的家具真的进列表，唯一办法是**把 disroom 的清空撤销掉** ——
    // 那等于反过来推翻用户装 disroom 的意图，是错的。
    // 所以正确做法是：**保留冲突的结果（爵位无需房间要求），只把这段必然发生的自检提示静音**。
    //
    // 【第四件事：拦在哪一层 —— 拦它的日志入口，而不是拦它的自检逻辑】
    //
    // 反编译 RI_Furniture.dll 可见，那段自检是 RI.Furniture.RoyalRoomRequirementSelfCheck.Run()，
    // 它所有输出都走同一个工具方法：
    //
    //     RI.Furniture.RILogger.Err(string module, string msg)   →   Log.Error(Tag(module) + " " + msg)
    //     （全库只有这一个签名、没有重载）
    //
    // 而 module 参数正好是 "RoyalCompat"。所以前缀里只要比这一个字符串：
    // **module == "RoyalCompat" 就地拦下，其余任何模块一律放行**。
    //
    // 这样 RI-Furniture 的其它所有日志（折叠组、地形、调试面板……）**一条都不会少**。
    // 我们拦的只是「自检结论：本包家具没能进爵位要求」这一类 —— 而它的成因
    // （disroom 清空）已经在上面写清楚了，不需要每次启动再念 54 遍。
    //
    // 【第五件事：什么情况下它会自动失效】
    //
    //   · RI-Furniture 没装 / 被禁用 → 类型找不到，本项什么都不做（打一条 Message 说明后跳过）。
    //   · 用户哪天卸掉 disroom.mashiro → RI 的补丁会真的生效、自检自然不再报错，
    //     本项也无事可做（拦不到东西，一条日志都不打）。
    //   · 作者把 RILogger 改名/改结构 → 找不到目标方法，本次跳过并在下次启动重试（留 Error 线索）。
    //
    // 【性能说明】
    //
    // 目标方法只在**启动自检那一次**被调用几十次，不在任何热路径上。
    // 前缀里只做一次字符串比较，代价可忽略。
    // 首次成功拦下时打一条说明（只打一次），方便在 Player.log 里确认补丁生效。
    // ==========================================================================
    internal static class RIFurnitureRoyalCompatQuietFix
    {
        // 目标类型：RI.Furniture.RILogger（public static class）。
        private const string LoggerTypeName = "RI.Furniture.RILogger";

        // 目标方法名：Err(string module, string msg)。
        private const string TargetMethodName = "Err";

        // 只静音这一个模块 —— 它的 `module` 参数就是 "RoyalCompat"（反编译核对过，共 3 处调用同属该类）。
        private const string QuietModule = "RoyalCompat";

        private static bool installed;
        private static int suppressedCount;
        private static bool announced;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type loggerType = AccessTools.TypeByName(LoggerTypeName);
                if (loggerType == null)
                {
                    // 找不到类型 = 模组没装，或作者改了命名空间。重试也没用，置位跳过。
                    // 用 Message 而不是 Error：没装这个模组是完全正常的状态，不该报红。
                    installed = true;
                    Log.Message("[GNH LocalFixes] 未找到 " + LoggerTypeName
                        + "（RI-Furniture 未安装或已改版）；"
                        + "RI-Furniture 皇权自检静音补丁已跳过。");
                    return;
                }

                MethodInfo target = FindErrMethod(loggerType);
                if (target == null)
                {
                    // 类型在、方法找不到 → 多半是作者改了结构。不置位，留给下次重试；并留 Error 线索。
                    Log.Error("[GNH LocalFixes] 在 " + LoggerTypeName + " 上找不到 "
                        + TargetMethodName + "(string, string) 方法；"
                        + "RI-Furniture 皇权自检静音补丁本次未安装，下次会重试。");
                    return;
                }

                // Patch 的参数顺序是 (原方法, 前缀, 后缀, 转译器, 收尾器)。这里只需要前缀。
                // ⚠ AccessTools.Method 的第四个参数是**泛型类型参数**，不是返回类型 ——
                //    非泛型方法必须传 null（传错会抛 InvalidOperationException）。
                LocalFixesMod.HarmonyInstance.Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(
                        typeof(RIFurnitureRoyalCompatQuietFix), nameof(Prefix), null, null)),
                    null,
                    null,
                    null);

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装 RI-Furniture 皇权自检静音补丁"
                    + "（" + LoggerTypeName + "." + TargetMethodName + "，只拦 module = 「" + QuietModule + "」）。"
                    + "那些红字说的是「本包家具没能进爵位房间要求」——"
                    + "成因是 disroom.mashiro 把 throneRoomRequirements 整片清空了，"
                    + "而那是玩家**故意**启用的效果，冲突结果属于预期。"
                    + "本补丁只静音这一类自检结论，该模组的其它日志一律照常输出。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装 RI-Furniture 皇权自检静音补丁失败（下次会重试）：" + ex);
            }
        }

        // ==================================================================
        // 真正干活的前缀：module 是 RoyalCompat 就返回 false，让原方法整个跳过。
        // ==================================================================
        //
        // 【为什么参数写成 __0 而不是 module】
        // Harmony 允许两种绑定方式：按**参数名**（写 string module）、或按**位置**（写 string __0）。
        // 这里用位置绑定：万一作者哪天把参数名从 module 改成别的，按名字写的前缀会直接挂不上，
        // 按位置写的照常工作。
        public static bool Prefix(string __0)
        {
            // 绝大多数调用在这一行结束（长度/内容不同直接放行）。
            if (__0 != QuietModule)
            {
                return true;
            }

            suppressedCount++;

            // 只在第一次拦截时说明一下，之后完全静默 —— 否则等于换个方式继续刷屏。
            if (!announced)
            {
                announced = true;
                Log.Message("[GNH LocalFixes] 已开始静音 RI-Furniture 的皇权房间要求自检"
                    + "（模块「" + QuietModule + "」的输出不再写日志）。"
                    + "原因：disroom.mashiro 会清空全部爵位的房间要求列表，"
                    + "RI 的家具因此必然加不进去 —— 这是预期中的设计冲突，不需要每次启动重复提示。"
                    + "该模组的其它模块日志不受影响。之后不再重复打印。");
            }

            return false;
        }

        // ==================================================================
        // 找到 public static void Err(string, string) 这个方法。
        // ==================================================================
        //
        // 不写死参数类型数组，而是按「名字叫 Err、且收两个参数」去找 ——
        // 这样即便将来作者改了参数类型，也不会因为签名对不上而漏装。
        private static MethodInfo FindErrMethod(Type loggerType)
        {
            MethodInfo[] methods = loggerType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != TargetMethodName)
                {
                    continue;
                }

                if (m.GetParameters().Length == 2)
                {
                    return m;
                }
            }

            return null;
        }
    }
}
