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
    // 【53】边缘仙路（RimImmortal-Core）的配方 postfix 在 DefOf 还没初始化时必崩
    // ==========================================================================
    //
    // 【症状（1.6 侧日志原文，2026-11-12 02:5x）】
    //
    //   --- 先兆（几百行前）：
    //   Tried to use an uninitialized DefOf of type RI_RecipesDefOf.
    //   DefOfs are initialized right after all defs all loaded. Uninitialized DefOfs
    //   will return only nulls. (hint: don't use DefOfs in static constructors etc.)
    //
    //   --- 然后是致命的那一条：
    //   Could not recover from errors loading play data. Giving up.
    //   Exception from asynchronous event: System.NullReferenceException
    //     at WhoXiuXian.HarmonyPatchs.Recipes_CopyBook_Gen.Postfix (IEnumerable`1[T] values)
    //     at RimWorld.RecipeDefGenerator.ImpliedRecipeDefs (Boolean hotReload)
    //     at RimWorld.DefGenerator.GenerateImpliedDefs_PreResolve (Boolean hotReload)
    //     at Verse.PlayDataLoader.DoPlayLoad ()
    //
    //   后果与其它几起一样：play data 加载失败 → `Resetting mods config` →
    //   **ModsConfig 被重写成只剩官方 DLC** → `Giving up`。
    //
    // 【根因：反编译 `3296476341\1.6\Assemblies\MOD.dll`（MVID ee72bf43…，与日志一致）】
    //
    //   [HarmonyPatch(typeof(RecipeDefGenerator), "ImpliedRecipeDefs")]
    //   public static class WhoXiuXian.HarmonyPatchs.Recipes_CopyBook_Gen
    //   {
    //       [HarmonyPostfix]
    //       public static IEnumerable<RecipeDef> Postfix(IEnumerable<RecipeDef> values)
    //       {
    //           try { AddCopyBookRecipes(); }
    //           catch (Exception ex) { Log.Error("Recipes_CopyBook_Gen::Postfix Error: " + ...); }
    //           finally
    //           {
    //               RI_RecipesDefOf.RI_RecipeBase_CopyBook.recipeUsers = new List<ThingDef>();
    //               // ↑↑ 这一句在 finally 里，**catch 管不到它**
    //           }
    //           return values;
    //       }
    //   }
    //
    //   两处硬伤叠在一起：
    //     ① `ImpliedRecipeDefs` 跑在 **DefOf 初始化之前**（DefOfs 要等所有 Def 加载完才注入），
    //        所以 `RI_RecipesDefOf.RI_RecipeBase_CopyBook` 此时**就是 null**；
    //        `AddCopyBookRecipes()` 里第一次碰它就抛 NRE —— 被 catch 吞掉、只打一行日志；
    //     ② 接着 `finally` 又去解引用同一个 null 字段，
    //        **finally 不归 catch 管**，这次异常直接逃出整个 Postfix，
    //        把 `ImpliedRecipeDefs` 这个迭代器打断 ⇒ play data 加载失败。
    //
    //   作者本意大概只是「跑完把 recipeUsers 清空」，但没想过那句会先炸。
    //
    // 【为什么用 Prefix 守卫，而不是 finalizer】
    //
    //   ⚠ 看起来 finalizer 更省事（像【50】那样），但**这里不行**：
    //     Postfix 返回 `IEnumerable<RecipeDef>`，finalizer 吞掉异常后 Harmony 只能让方法
    //     返回 **default = null**，而 `GenerateImpliedDefs_PreResolve` 拿到 null 之后
    //     照样会在 foreach 上 NRE —— 等于把一个 NRE 换成另一个。
    //
    //   所以改成 **Prefix 返回 false 直接跳过整个 Postfix**：
    //     · 判断「`RI_RecipesDefOf.RI_RecipeBase_CopyBook` 现在是不是 null」；
    //     · 是 null ⇒ 这一轮本来也做不了任何事（作者那句 `finally` 就是要去清它的字段），
    //       跳过与原意等价，只是不再崩；
    //     · 不是 null ⇒ `return true`，原 Postfix 一字不改地跑。
    //
    //   `RI_RecipesDefOf` 是 DefOf 类型，等 Def 全部加载完后游戏会把字段注入进来，
    //   所以**之后**再被调用（例如热重载）时会自动放行 —— 这不是「永久停用」，是「等它就绪」。
    //
    // 【自动让路（本工程纪律 §3.1）】
    //
    //   · 类型 `WhoXiuXian.HarmonyPatchs.Recipes_CopyBook_Gen` 找不到 -> 没装边缘仙路，
    //     打一行 Message 跳过，不影响任何其它修复项；
    //   · 它的 `Postfix` 找不到 / 签名变了 -> 打 Error 并**不置位 installed**，留给下次重试；
    //   · `RI_RecipesDefOf` 或那个字段找不到 -> 说明作者改了实现，
    //     此时**不装**（因为我们的判据依赖那个字段），只打一行说明。宁可少修，不乱修。
    //
    // 【对 1.6 / 1.7 都适用】
    //
    //   这个模组的 `LoadFolders.xml` 是 `<v1.5>/<v1.6>/<v1.7>` 三段、各加载自己的目录，
    //   而 **1.6 与 1.7 两版的 dll 里都有同一个 `WhoXiuXian.HarmonyPatchs.Recipes_CopyBook_Gen`**。
    //   实机观察：1.6 侧它必崩（DefOf 未初始化），1.7 侧没崩（时序不同、DefOf 已就绪）——
    //   所以本项在 1.7 下通常**什么都不做**（Prefix 每次都返回 true），这正是预期行为。
    //   全程字符串反射，不写 `typeof(WhoXiuXian.…)`，没装这个模组也编译得过。
    // ==========================================================================
    public static class XiuXianCopyBookPostfixGuardFix
    {
        /// <summary>mod 里那个补丁类的完整类型名。</summary>
        private const string TargetTypeName = "WhoXiuXian.HarmonyPatchs.Recipes_CopyBook_Gen";

        /// <summary>它的方法名。</summary>
        private const string TargetMethodName = "Postfix";

        /// <summary>DefOf 类型（字段由游戏在 Def 全部加载后注入）。</summary>
        private const string DefOfTypeName = "WhoXiuXian.RI_RecipesDefOf";

        /// <summary>`finally` 里被解引用的那个静态字段名。</summary>
        private const string DefOfFieldName = "RI_RecipeBase_CopyBook";

        /// <summary>本补丁是否已经装过。</summary>
        private static bool installed;

        /// <summary>是否已经打印过「没装这个模组」的说明。</summary>
        private static bool announced;

        /// <summary>缓存的 FieldInfo —— Prefix 每次生成配方都会跑，不能每次反射查找。</summary>
        private static FieldInfo defOfField;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type targetType = AccessTools.TypeByName(TargetTypeName);
                if (targetType == null)
                {
                    installed = true;
                    if (!announced)
                    {
                        announced = true;
                        Log.Message("[GNH LocalFixes] RimImmortal (边缘仙路) is not present;"
                            + " its CopyBook postfix guard was skipped (nothing to do).");
                    }
                    return;
                }

                // Postfix(IEnumerable<RecipeDef> values) —— 按参数个数 1 找，不绑死泛型实参，
                // 免得作者哪天把返回类型从 IEnumerable<RecipeDef> 改成别的就装不上。
                MethodInfo target = null;
                foreach (MethodInfo m in targetType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name == TargetMethodName && m.GetParameters().Length == 1)
                    {
                        target = m;
                        break;
                    }
                }
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] RimImmortal is present but " + TargetTypeName + "."
                        + TargetMethodName + "(…) was not found (its API probably changed);"
                        + " the guard is deferred and will retry.");
                    return;
                }

                Type defOfType = AccessTools.TypeByName(DefOfTypeName);
                FieldInfo field = defOfType == null ? null : AccessTools.Field(defOfType, DefOfFieldName);
                if (field == null)
                {
                    // 判据依赖这个字段；拿不到就不装（宁可少修，不乱修）。
                    installed = true;
                    Log.Message("[GNH LocalFixes] RimImmortal's " + DefOfTypeName + "." + DefOfFieldName
                        + " could not be found (the author probably changed the implementation);"
                        + " the CopyBook postfix guard is intentionally NOT installed.");
                    return;
                }
                defOfField = field;

                MethodInfo prefix = AccessTools.Method(
                    typeof(XiuXianCopyBookPostfixGuardFix), nameof(Prefix), null, null);
                if (prefix == null)
                {
                    Log.Error("[GNH LocalFixes] Internal error: XiuXianCopyBookPostfixGuardFix.Prefix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(target, new HarmonyMethod(prefix), null, null);
                installed = true;

                Patches info = Harmony.GetPatchInfo(target);
                Log.Message("[GNH LocalFixes] Patched " + TargetTypeName + "." + TargetMethodName
                    + " (prefixes=" + (info != null ? info.Prefixes.Count : -1) + ")."
                    + " When " + DefOfTypeName + "." + DefOfFieldName + " is still null (DefOfs are not"
                    + " injected yet at ImpliedRecipeDefs time), the whole postfix is skipped instead of"
                    + " throwing out of its unprotected finally block — which used to abort play-data"
                    + " loading and made the game reset ModsConfig to DLC-only.");
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Failed to install the RimImmortal CopyBook postfix guard"
                    + " (that fix is NOT active this session; everything else is unaffected): " + ex);
            }
        }

        /// <summary>
        /// `false` = 跳过原 Postfix；`true` = 照常执行。
        ///
        /// 只在「那个 DefOf 字段还是 null」时返回 false —— 那种情况下原 Postfix 的
        /// `finally` 必然解引用 null 并抛出去，跳过它才是安全的。
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix()
        {
            try
            {
                if (defOfField == null)
                {
                    // 没装/没缓存到 —— 不干预。
                    return true;
                }
                object v = defOfField.GetValue(null);
                return v != null;
            }
            catch (Exception)
            {
                // 判空本身出错时按「放行」处理：宁可留一条红字，也不静默改变原行为。
                return true;
            }
        }
    }
}
