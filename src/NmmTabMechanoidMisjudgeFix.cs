// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）完成。
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
    // 【69】「裸体评价更多看法」的页签对「人工种族」不出现 —— 因为 RJW 用
    //       字符串包含把它们的 FleshType 当成机械体
    // ==========================================================================
    //
    // ── 症状（2026-11-16 玩家实测反馈，两轮对话确认）──
    //
    //   · 原版人类（24 岁）：页签栏是「幻化 | 裸体 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康」
    //   · 某个「人工种族」（16 岁，随后在人物编辑器里改成 20 岁并存读档）：
    //                        「幻化 | 日志 | 装备 | 社交 | 角色 | 需求 | 健康」← **没有「裸体」**
    //   · 但**机制完全正常**：全裸会遮体、走光会掉心情、评语照常生成。
    //   · 改年龄无效 —— 与年龄无关。
    //
    // ── 判定链（全部反编译核实，1.7.5011 的 Assembly-CSharp 与 RJW.dll）──
    //
    //   页签是否出现只看 `ITab_Pawn_NMMOpinions.IsVisible`，它只有三个条件：
    //
    //       return canDoLoving && isHuman && !isEntityOrGhoul;
    //
    //   ① `isHuman` = `selectedPawn.def.race.Humanlike`
    //      而 `Verse.RaceProperties.Humanlike => (int)intelligence >= 2`
    //      —— 人工种族继承 Human，intelligence 都是 Humanlike ⇒ **通常为 true**
    //   ② `isEntityOrGhoul` = `NudityMattersMore.InfoHelper.EntityOrGhoul(pawn)`
    //      实现是 `if (!pawn.IsEntity) return pawn.kindDef == PawnKindDefOf.Ghoul;`
    //      —— 人工种族两者都不是 ⇒ **通常为 false**，取反后为 true
    //   ③ `canDoLoving` = `rjw.xxx.can_do_loving(pawn)`
    //      **第一行就是 `if (is_mechanoid(pawn)) return false;`**
    //
    //   而 `rjw.xxx.is_mechanoid` 的判据里有一串**字符串包含**：
    //
    //       if (AndroidsCompatibility.IsAndroid(pawn)) return false;        // ← 唯一的豁免
    //       return pawn.RaceProps.IsMechanoid
    //           || pawn.RaceProps.FleshType == FleshTypeDefOf.Mechanoid
    //           || pawn.RaceProps.FleshType.corpseCategory.ToString().Contains("CorpsesMechanoid")
    //           || pawn.RaceProps.FleshType.defName.Contains("GR_Mechanoid")
    //           || pawn.RaceProps.FleshType.defName.Contains("MechanisedInfantry")
    //           || pawn.RaceProps.FleshType.defName.Contains("Android");    // ← 名字带 Android 就算
    //
    //   而豁免只认一个 mod 的扩展类型：
    //
    //       // rjw.AndroidsCompatibility 的静态构造
    //       typeName = "Androids.SexualizeAndroidRJW";
    //
    //   ⇒ **除了 `Androids` 那一家，任何 FleshType 名字里含 `Android` 的种族
    //      都会被 RJW 判成机械体** ⇒ `can_do_loving` 第一行返回 false
    //      ⇒ `IsVisible` 为 false ⇒ 页签不出现。
    //
    //   这就解释了全部现象：与年龄无关（第一行就拦了，压根没走到年龄判断）、
    //   只对 mod 种族发生（原版人类 FleshType 是 `Flesh`）、机制却完全正常
    //   （NMM 的裸体判定走自己的代码，不经过 `is_mechanoid`）。
    //
    // ── 我们怎么修：只在「**确实是误判**」时放宽，别的都不动 ──
    //
    //   给 `ITab_Pawn_NMMOpinions.get_IsVisible` 加 Postfix：
    //   原方法返回 false 时，只有在**同时**满足下面全部条件时才改成 true：
    //
    //     ① pawn 不是 null；
    //     ② `pawn.def.race.Humanlike` —— 是人形（真机械体、动物在这一步就被排除）；
    //     ③ `!EntityOrGhoul(pawn)` —— 不是 Anomaly 实体、不是 Ghoul；
    //     ④ `is_mechanoid(pawn) == true` —— **确认它正是被判成机械体的那一种**。
    //
    //   第 ④ 条是关键：它把「放宽」严格限制在「RJW 把一个人形种族当成机械体」这一种情形。
    //   于是：
    //     · 未成年的人形种族 → `is_mechanoid` 为 false → **仍然按原逻辑不显示**（尊重作者意图）；
    //     · 真正的机械体 → 不是 Humanlike → 不动；
    //     · Ghoul / 实体 → 不动；
    //     · 原版人类 → 本来就显示，Postfix 直接跳过；
    //     · 被 FleshType 名字误伤的人工种族 → **显示**（这正是要修的）。
    //
    //   换句话说：**没有推翻作者的任何一条设计**，只是把「RJW 的误判」从可见性判定里摘出去。
    //
    // ── 自动让路 ──
    //
    //   · 没装「裸体评价更多看法」→ 找不到类型，打一行 Message 跳过；
    //   · 作者以后自己修了（比如把 can_do_loving 换成自己的判据）→
    //     那时 `is_mechanoid` 不再是 false 的原因，本项自然什么都不做；
    //   · 没装 RJW 或 RJW 改了这个方法 → 拿不到 `is_mechanoid`，本项一律不放宽（返回保守值）；
    //   · 玩家关掉本项 → 整项不安装，回到原状。
    //
    // ── 为什么用反射而不是直接引用 ──
    //
    //   本补丁包不依赖「裸体评价更多看法」，也不依赖 RJW。直接 `using` 那些类型会让
    //   没装它们的玩家在加载本模组时就炸。所以全部走 `AccessTools.TypeByName`，
    //   拿不到就当「不适用」，一个 `NullReferenceException` 都不往外扔。
    // ==========================================================================

    /// <summary>
    /// 【69】当 RJW 因为 FleshType 名字含 "Android" 把一个人形种族误判成机械体时，
    /// 让「裸体评价」页签仍然显示。只放宽这一种情形，其余一概不动。
    /// </summary>
    internal static class NmmTabMechanoidMisjudgeFix
    {
        private const string TabTypeName = "NudityMattersMore_opinions.ITab_Pawn_NMMOpinions";
        private const string RjwXxxTypeName = "rjw.xxx";
        private const string InfoHelperTypeName = "NudityMattersMore.InfoHelper";

        private const string IsVisibleGetterName = "IsVisible";
        private const string SelPawnPropName = "SelPawnForOpinions";
        private const string IsMechanoidName = "is_mechanoid";
        private const string EntityOrGhoulName = "EntityOrGhoul";

        private static bool installed;

        /// <summary>累计放宽过几次（供自检引用）。</summary>
        private static int revealedCount;

        /// <summary>是否已经为「已放宽」打过一次说明。</summary>
        private static bool revealLogged;

        private static PropertyInfo selPawnProp;
        private static MethodInfo isMechanoidMethod;
        private static MethodInfo entityOrGhoulMethod;
        private static bool lookupsDone;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type tabType = AccessTools.TypeByName(TabTypeName);
                if (tabType == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【69】Nudity Matters More (opinions) is not present;"
                        + " the \"artificial race\" tab guard was skipped (nothing to do).");
                    return;
                }

                MethodInfo target = AccessTools.PropertyGetter(tabType, IsVisibleGetterName);
                if (target == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【69】" + TabTypeName + "." + IsVisibleGetterName
                        + " not found (the author may have renamed it); the tab guard was skipped.");
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(NmmTabMechanoidMisjudgeFix), nameof(Postfix));
                if (postfix == null)
                {
                    Log.Error("[GNH LocalFixes] 【69】internal error: Postfix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, null, new HarmonyMethod(postfix), null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【69】\"artificial race\" nudity-tab guard installed"
                    + " (shows the tab again for humanlike races that RJW mistakes for mechanoids"
                    + " because their FleshType defName contains \"Android\").");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【69】failed to install the nudity-tab guard: " + ex);
            }
        }

        /// <summary>
        /// 原方法已经判了 false 之后才轮到我们。
        /// 只有「人形 + 非 entity/ghoul + 确实被当成机械体」才翻成 true。
        /// </summary>
        public static void Postfix(object __instance, ref bool __result)
        {
            // 原方法已经让页签显示 → 什么都不用做（绝大多数情况走这条）。
            if (__result)
            {
                return;
            }
            if (__instance == null)
            {
                return;
            }

            try
            {
                Pawn pawn = GetSelectedPawn(__instance);
                if (pawn == null || pawn.def == null || pawn.def.race == null)
                {
                    return;
                }

                // ① 必须是「人形」。真正的机械体/动物在这一步就被挡掉。
                if (!pawn.def.race.Humanlike)
                {
                    return;
                }

                // ② Anomaly 实体与 Ghoul 不放宽（与原方法保持一致）。
                //    判不出来时按「是」处理 —— 宁可不动，也不要误开。
                if (IsEntityOrGhoul(pawn))
                {
                    return;
                }

                // ③ 关键：只有「RJW 把它当成机械体」这一种原因才放宽。
                //    若它本来就不是机械体判定，说明是别的条件（比如年龄）挡的，
                //    那就尊重作者的原始设计，什么都不做。
                if (!IsMechanoid(pawn))
                {
                    return;
                }

                __result = true;
                revealedCount++;

                if (!revealLogged)
                {
                    revealLogged = true;
                    Log.Message("[GNH LocalFixes] 【69】已让「" + pawn.def.defName
                        + "」这个种族的「裸体评价」页签重新出现。"
                        + "原因：RJW 的 rjw.xxx.is_mechanoid 用**字符串包含**判断机械体 ——"
                        + " 它的 FleshType 名字里含 \"Android\" 就被当成机械体，"
                        + "于是 can_do_loving 第一行直接返回 false，页签跟着消失。"
                        + "本项只在「人形 + 确实被误判成机械体」时放宽，"
                        + "未成年、真机械体、Ghoul、实体一律维持原逻辑。"
                        + "（本条及后续同类只计数，不再逐条打印。）");
                }
            }
            catch (Exception)
            {
                // 只要有一丝不确定，就维持原方法的结果。
            }
        }

        /// <summary>取这个页签当前对应的 pawn（原方法用的同一个属性，所以口径一致）。</summary>
        private static Pawn GetSelectedPawn(object tab)
        {
            try
            {
                if (selPawnProp == null)
                {
                    selPawnProp = tab.GetType().GetProperty(
                        SelPawnPropName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (selPawnProp == null)
                {
                    return null;
                }
                return selPawnProp.GetValue(tab, null) as Pawn;
            }
            catch (Exception)
            {
                // 原属性本身在「当前选中的不是 pawn」时会抛 InvalidOperationException，
                // 原方法用 try/catch 处理过，这里同样按「没有 pawn」处理。
                return null;
            }
        }

        /// <summary>调 RJW 的 rjw.xxx.is_mechanoid。拿不到就返回 false（不放宽）。</summary>
        private static bool IsMechanoid(Pawn pawn)
        {
            EnsureLookups();
            if (isMechanoidMethod == null)
            {
                return false;
            }
            try
            {
                object r = isMechanoidMethod.Invoke(null, new object[] { pawn });
                return r is bool b && b;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 调本体的 NudityMattersMore.InfoHelper.EntityOrGhoul。
        /// **判不出来时返回 true**（当作 entity，不放宽）—— 保守优先。
        /// </summary>
        private static bool IsEntityOrGhoul(Pawn pawn)
        {
            EnsureLookups();
            if (entityOrGhoulMethod == null)
            {
                return true;
            }
            try
            {
                object r = entityOrGhoulMethod.Invoke(null, new object[] { pawn });
                return !(r is bool b) || b;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>一次性解析两个外部方法（各自可能不存在，各自独立缓存）。</summary>
        private static void EnsureLookups()
        {
            if (lookupsDone)
            {
                return;
            }
            lookupsDone = true;

            try
            {
                Type rjw = AccessTools.TypeByName(RjwXxxTypeName);
                if (rjw != null)
                {
                    isMechanoidMethod = AccessTools.Method(
                        rjw, IsMechanoidName, new[] { typeof(Pawn) }, null);
                }
            }
            catch (Exception)
            {
                isMechanoidMethod = null;
            }

            try
            {
                Type helper = AccessTools.TypeByName(InfoHelperTypeName);
                if (helper != null)
                {
                    entityOrGhoulMethod = AccessTools.Method(
                        helper, EntityOrGhoulName, new[] { typeof(Pawn) }, null);
                }
            }
            catch (Exception)
            {
                entityOrGhoulMethod = null;
            }
        }

        /// <summary>累计放宽的次数（供自检/文档引用）。</summary>
        internal static int RevealedCount
        {
            get { return revealedCount; }
        }
    }
}
