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
    // 【55】「缇瓦莱特贞操科技」的 GameComponent 从来没被创建过 —— 整套挑战系统是死的
    // ==========================================================================
    //
    // 【症状】
    //
    //   每次读档 / 开新档都打一条（本机实测一轮三次）：
    //
    //       2026-11-12 19:29:56 ERROR [Verse] Could not instantiate a GameComponent of type
    //         LT.TwilightChastityTechnology.GameComponent_TwilightOrgasmPermissionChallenges:
    //         System.MissingMethodException: Constructor on type
    //         'LT.TwilightChastityTechnology.GameComponent_TwilightOrgasmPermissionChallenges' not found.
    //           at System.RuntimeType.CreateInstanceImpl(…)
    //           at ….PatchedActivator.CreateInstance(Type type, Object[] objects, Object me)
    //           at Verse.Game.FillComponents_Patch3(Game this)
    //           at Verse.Game..ctor()
    //
    // 【根因：构造函数签名与引擎约定不符（反编译实证）】
    //
    //   引擎侧（1.7.5005 反编译原文，Verse.Game.FillComponents）：
    //
    //       private void FillComponents()
    //       {
    //           components.RemoveAll((GameComponent component) => component == null);
    //           foreach (Type item2 in typeof(GameComponent).AllSubclassesNonAbstract())
    //           {
    //               if (GetComponent(item2) == null)
    //               {
    //                   try
    //                   {
    //                       GameComponent item = (GameComponent)Activator.CreateInstance(item2, this);  // ← 固定传一个 Game
    //                       components.Add(item);
    //                   }
    //                   catch (Exception ex)
    //                   {
    //                       Log.Error("Could not instantiate a GameComponent of type " + item2 + ": " + ex);
    //                   }
    //               }
    //           }
    //       }
    //
    //   模组侧（反编译 LT.TwilightChastityTechnology.dll，168 类型 / 730 方法）
    //   这个类**只有一个无参构造函数**：
    //
    //       public GameComponent_TwilightOrgasmPermissionChallenges() { }   // 见下：方法体其实非空
    //
    //   参数个数对不上 → Activator 抛 MissingMethodException → 引擎 catch 掉、打一条红字、
    //   **这个组件永远不会进 components 列表**。
    //
    //   ⚠ 顺带记一个容易误判的点：那个构造函数虽然"看起来是空的"，IL 里其实有两条
    //     字段初始化（handlers / legacyCheckedPawnIds 都 new 了空集合）。
    //     所以只要能被创建出来，它内部不会是 null —— 这也是本条敢直接补组件的依据。
    //
    // 【后果远比那三行红字严重】
    //
    //   这个 GameComponent 承载「高潮许可挑战」的全部状态：
    //     · handlers（每只小人的挑战进度）—— 从来没被创建；
    //     · GameComponentTick() —— 从来没跑过，挑战计时 / 过期全部停摆；
    //     · legacyCheckedPawnIds —— 旧存档迁移逻辑从来没执行；
    //     · GetHandler(pawn, createIfMissing) —— 外部调用一律拿不到 handler。
    //   换句话说：**这套系统一个功能都不工作，日志里却只有三行红字。**
    //
    // 【修法：在 FillComponents 之后把漏掉的那个补上】
    //
    //   不碰工坊 / 本地 DLL 的一个字节，只在本模组里加一个 Harmony Postfix：
    //   等引擎把该建的组件都建完，再自己用「无参构造」造一个塞进 components。
    //
    //   三点必须注意的地方（都已在实现里处理）：
    //     1. **幂等** —— FillComponents 在 `Game..ctor` 与 `Game.ExposeSmallComponents`
    //        两条路径上都会走（反编译 callers 实测：Game..ctor 与 Game.ExposeSmallComponents）。
    //        不判重会让 GameComponentTick 一轮跑两遍。
    //     2. **只在类型存在时才动** —— 没装那个模组就直接跳过，不是错误。
    //     3. **旧存档不会有这个节点** —— 补进去之后，该组件的 ExposeData 会被 Game 统一调用；
    //        存档里没有它时按默认值（空 handlers / 空 legacyCheckedPawnIds）初始化，
    //        这正是「组件从没被创建过」应有的起点，不崩、不丢数据，第一次存盘会写出新节点。
    //
    // 【为什么这是保意图，而不是"自作主张加功能"】
    //
    //   作者的 GameComponentTick / GetHandler / ShouldCheckLegacyState 全部照原样跑，
    //   没有一处被短路或改写。**唯一的变化是「它终于会跑了」。**
    //   如果玩家不想要，设置界面可以随时关掉本项。
    //
    // 【自动让路】
    //
    //   · 模组没装 → AccessTools.TypeByName 返回 null → 直接跳过（打一行 Message，不是 Error）；
    //   · 模组更新后把构造函数改对了 → 引擎自己就能创建 → 本项的判重会让它什么都不做；
    //   · FillComponents 改名 / 消失 → 装不上，打 Error 并留待下次重试；
    //   · 玩家关掉开关 → 整项不安装。
    // ==========================================================================

    /// <summary>
    /// 【55】给「缇瓦莱特贞操科技」补上那个永远创建不出来的 GameComponent。
    /// </summary>
    internal static class TwilightGameComponentRebuildFix
    {
        /// <summary>目标类型的完整名（模组程序集里的 internal/public 类，只能按字符串找）。</summary>
        private const string TargetTypeName =
            "LT.TwilightChastityTechnology.GameComponent_TwilightOrgasmPermissionChallenges";

        /// <summary>Log.ErrorOnce 的去重键（ASCII "GNHt"）。</summary>
        private const int ErrorOnceKey = 0x474E4874;

        /// <summary>本补丁是否已装。</summary>
        private static bool installed;

        /// <summary>目标类型缓存（可能为 null，表示模组未安装）。</summary>
        private static Type targetType;

        /// <summary>目标类型是否已解析过（避免每次都走一次字符串查找）。</summary>
        private static bool typeResolved;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                // ---- 先把类型找出来。找不到 = 模组没装，这属于正常情况。 ----
                Type found = ResolveTargetType();
                if (found == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【55】the Twilight chastity-tech mod is not present;"
                        + " its GameComponent rebuild was skipped (nothing to do).");
                    return;
                }

                // ---- FillComponents 在 1.6 / 1.7 都是 private，只能用反射按名字取 ----
                MethodInfo fill = AccessTools.Method(typeof(Game), "FillComponents");
                if (fill == null)
                {
                    Log.Error("[GNH LocalFixes] 【55】Verse.Game.FillComponents was not found"
                        + " (its name probably changed); the rebuild is deferred and will retry.");
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(TwilightGameComponentRebuildFix), nameof(Postfix));
                if (postfix == null)
                {
                    Log.Error("[GNH LocalFixes] 【55】internal error: Postfix not found.");
                    return;
                }

                new Harmony("GNH.LocalFixes.TwilightGameComponentRebuild")
                    .Patch(fill, postfix: new HarmonyMethod(postfix));

                installed = true;
                Log.Message("[GNH LocalFixes] 【55】Twilight GameComponent rebuild installed"
                    + " (target: " + TargetTypeName + ").");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【55】failed to install the Twilight GameComponent"
                    + " rebuild: " + ex);
            }
        }

        private static Type ResolveTargetType()
        {
            if (typeResolved)
            {
                return targetType;
            }

            typeResolved = true;
            try
            {
                targetType = AccessTools.TypeByName(TargetTypeName);
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 【55】looking up " + TargetTypeName + " threw: "
                    + ex.Message);
                targetType = null;
            }

            return targetType;
        }

        /// <summary>
        /// 引擎把所有 GameComponent 建完之后，检查目标组件在不在；不在就用无参构造补一个。
        /// </summary>
        private static void Postfix(Game __instance)
        {
            if (__instance == null)
            {
                return;
            }

            try
            {
                Type t = ResolveTargetType();
                if (t == null)
                {
                    return;
                }

                // ---- 幂等：已经存在就什么都不做 ----
                // 注意这里用「类型的完整名」比较，而不是 `is` / `== typeof(...)`：
                // 我们手上只有反射得到的 Type，用 FullName 比较既准确又不受可见性影响。
                if (__instance.components != null)
                {
                    for (int i = 0; i < __instance.components.Count; i++)
                    {
                        GameComponent c = __instance.components[i];
                        if (c != null && c.GetType().FullName == TargetTypeName)
                        {
                            return;
                        }
                    }
                }

                // ---- 用无参构造造一个（这正是模组作者写的那种构造） ----
                object raw = Activator.CreateInstance(t);
                GameComponent comp = raw as GameComponent;
                if (comp == null)
                {
                    Log.ErrorOnce("[GNH LocalFixes] 【55】" + TargetTypeName
                        + " is not a Verse.GameComponent — skipped.", ErrorOnceKey);
                    return;
                }

                __instance.components.Add(comp);
                Log.Message("[GNH LocalFixes] 【55】added the missing GameComponent "
                    + TargetTypeName + " (its constructor takes no arguments, so the engine"
                    + " could never create it; the orgasm-permission challenge system can now run).");
            }
            catch (Exception ex)
            {
                // 补不上也不能让 FillComponents 的调用方（Game 构造函数）受影响。
                Log.ErrorOnce("[GNH LocalFixes] 【55】rebuilding the Twilight GameComponent"
                    + " failed: " + ex, ErrorOnceKey);
            }
        }
    }
}
