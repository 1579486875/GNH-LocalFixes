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
    // 【54】「缇瓦莱特贞操科技」的 GameComponent 从来没被创建过 —— 整套挑战系统是死的
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
    //   引擎侧（Verse.Game.FillComponents）：
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
    // 【修法：Prefix 先补上 + Postfix 兜底，两处共用同一个判重流程】
    //
    //   ⚠ 2026-11-16 改：原先只装 Postfix，结果**原版循环仍然先失败并打一条 ERROR**，
    //      然后我们的 Postfix 才补上。日志长这样（相隔 17 毫秒）：
    //
    //          12:46:15.039 ERROR Could not instantiate a GameComponent of type LT…Challenges …
    //          12:46:15.056 INFO  [GNH LocalFixes] 【54】added the missing GameComponent …
    //
    //      补丁**是生效的**，但玩家每次开档仍会看到一条红字，极容易被误判成「补丁坏了」。
    //
    //   改法来自一份日志诊断报告的 §3.3「首选方案」：
    //   **在 Prefix 里就把组件建好塞进 components，原版循环遍历到它时发现「已经有了」直接跳过**
    //   —— 把「先失败再补救」变成「一开始就不失败」。
    //
    //   为什么 Prefix 里能安全访问 components：
    //      引擎 `FillComponents()` 的第一行就是 `components.RemoveAll(…)`，
    //      说明进入该方法时 `components` **必然非 null**（Game 构造函数里已初始化）。
    //      所以 Prefix 里读它、写它都安全。
    //
    //   为什么**同时保留 Postfix**：
    //      两条路径都会走 `FillComponents`（`Game..ctor` 与 `Game.ExposeSmallComponents`），
    //      但只要判重存在，Postfix 在正常路径下就不会做任何事。
    //      保留它是为了兜住「Prefix 因某种原因没生效」的情况 —— 代价为零，收益是少一种失效模式。
    //
    //   ⚠ Prefix 不能返回 false（那会跳过原版方法体，其它 GameComponent 就全建不出来了）。
    //      必须是「只做自己的事、原样放行」的 void Prefix。
    //
    // 【三点必须注意的地方（都已在实现里处理）】
    //
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
    //   · 模组更新后把构造函数改对了 → 引擎自己就能创建 → 判重让它什么都不做；
    //   · FillComponents 改名 / 消失 → 装不上，打 Error 并留待下次重试；
    //   · 玩家关掉开关 → 整项不安装。
    // ==========================================================================

    /// <summary>
    /// 【54】给「缇瓦莱特贞操科技」补上那个永远创建不出来的 GameComponent。
    /// 2026-11-16 起改为 Prefix（先补）+ Postfix（兜底），消除「原版先报一条 ERROR」。
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

        /// <summary>Prefix 补上时的次数（只用于日志区分「首建」与「兜底」）。</summary>
        private static int addedInPrefix;

        /// <summary>Postfix 兜底补上时的次数。</summary>
        private static int addedInPostfix;

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
                    Log.Message("[GNH LocalFixes] 【54】the Twilight chastity-tech mod is not present;"
                        + " its GameComponent rebuild was skipped (nothing to do).");
                    return;
                }

                // ---- FillComponents 在 1.6 / 1.7 都是 private，只能用反射按名字取 ----
                MethodInfo fill = AccessTools.Method(typeof(Game), "FillComponents");
                if (fill == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】Verse.Game.FillComponents was not found"
                        + " (its name probably changed); the rebuild is deferred and will retry.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(
                    typeof(TwilightGameComponentRebuildFix), nameof(Prefix));
                MethodInfo postfix = AccessTools.Method(
                    typeof(TwilightGameComponentRebuildFix), nameof(Postfix));
                if (prefix == null || postfix == null)
                {
                    Log.Error("[GNH LocalFixes] 【54】internal error: Prefix/Postfix not found.");
                    return;
                }

                // 两个都装：Prefix 让原版循环不再报错，Postfix 兜住「Prefix 没生效」的意外。
                // 判重在 TryEnsureComponent 里，正常路径下 Postfix 什么都不会做。
                new Harmony("GNH.LocalFixes.TwilightGameComponentRebuild").Patch(
                    fill,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                installed = true;
                Log.Message("[GNH LocalFixes] 【54】Twilight GameComponent rebuild installed"
                    + " (prefix-first; target: " + TargetTypeName + ").");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【54】failed to install the Twilight GameComponent"
                    + " rebuild: " + ex);
            }
        }

        // 解析 Twilight 的 GameComponent 类型。
        // 该模组在不同版本里换过命名空间，所以按候选名列表逐个试，
        // 全试不到就返回 null（本项自动让路）。
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
                Log.Warning("[GNH LocalFixes] 【54】looking up " + TargetTypeName + " threw: "
                    + ex.Message);
                targetType = null;
            }

            return targetType;
        }

        /// <summary>
        /// 引擎建组件**之前**：先把它补上，这样原版循环遍历到它时 GetComponent 已非 null，
        /// 直接跳过，不会抛 MissingMethodException、也不会打那条 ERROR。
        ///
        /// ⚠ 必须是 void Prefix 且不返回 false —— 返回 false 会跳过整个原版方法体，
        ///    其它 GameComponent（原版的、别的模组的）就全都建不出来了。
        /// </summary>
        private static void Prefix(Game __instance)
        {
            if (TryEnsureComponent(__instance, true))
            {
                addedInPrefix++;
            }
        }

        /// <summary>
        /// 引擎建组件**之后**：兜底。正常情况下 Prefix 已经补上了，这里判重后会直接返回。
        /// </summary>
        private static void Postfix(Game __instance)
        {
            if (TryEnsureComponent(__instance, false))
            {
                addedInPostfix++;
            }
        }

        /// <summary>
        /// 判重后补组件。返回 true 表示本次真的补了一个。
        /// Prefix 与 Postfix 共用这同一段，保证两处的判重口径完全一致。
        /// </summary>
        private static bool TryEnsureComponent(Game game, bool fromPrefix)
        {
            if (game == null)
            {
                return false;
            }

            try
            {
                Type t = ResolveTargetType();
                if (t == null)
                {
                    return false;
                }

                // ---- 幂等：已经存在就什么都不做 ----
                // 注意这里用「类型的完整名」比较，而不是 `is` / `== typeof(...)`：
                // 我们手上只有反射得到的 Type，用 FullName 比较既准确又不受可见性影响。
                if (game.components != null)
                {
                    for (int i = 0; i < game.components.Count; i++)
                    {
                        GameComponent c = game.components[i];
                        if (c != null && c.GetType().FullName == TargetTypeName)
                        {
                            return false;
                        }
                    }
                }

                // ---- 用无参构造造一个（这正是模组作者写的那种构造） ----
                object raw = Activator.CreateInstance(t);
                GameComponent comp = raw as GameComponent;
                if (comp == null)
                {
                    Log.ErrorOnce("[GNH LocalFixes] 【54】" + TargetTypeName
                        + " is not a Verse.GameComponent — skipped.", ErrorOnceKey);
                    return false;
                }

                // components 理论上非 null（FillComponents 第一行就 RemoveAll 它），
                // 但 Prefix 阶段仍防御一次：万一为 null 就先建列表，否则 Add 会抛。
                if (game.components == null)
                {
                    game.components = new System.Collections.Generic.List<GameComponent>();
                }

                game.components.Add(comp);
                Log.Message("[GNH LocalFixes] 【54】added the missing GameComponent "
                    + TargetTypeName + " (via " + (fromPrefix ? "prefix" : "postfix")
                    + "; its constructor takes no arguments, so the engine could never create it;"
                    + " the orgasm-permission challenge system can now run).");
                return true;
            }
            catch (Exception ex)
            {
                // 补不上也不能让 FillComponents 的调用方（Game 构造函数）受影响。
                Log.ErrorOnce("[GNH LocalFixes] 【54】rebuilding the Twilight GameComponent"
                    + " failed: " + ex, ErrorOnceKey);
                return false;
            }
        }
    }
}
