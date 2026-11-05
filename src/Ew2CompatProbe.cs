// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed and reviewed by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 详细错误勘探（诊断工具，默认关闭）：把「谁在改这几个方法」摆到日志里
    //
    // 起因（2026-11-05）：
    //   有玩家报「ExtraWeapon2 + 多武器 + 手持远程武器的小人，一开始狩猎就闪退，
    //   看不到红字」。同一帖里另一位玩家回帖说，他遇到的是
    //   「一个给数字加千位逗号的 mod 把程序集搞坏了」。
    //
    //   这两条线索指向同一类病因：**transpiler 撞车**。
    //
    // ── 为什么 transpiler 撞车会「闪退且没有红字」──
    //
    //   Harmony 的 transpiler 不改行为，它**改 IL**。两个模组依次改同一个方法时，
    //   后一个看到的是前一个改完的指令序列。于是有三种坏法：
    //
    //     ① 后者的「按第 N 个匹配点定位」失准 —— ExtraWeapon2 的
    //        `Patch_VerbBurstCooldown_ForExtraWeapon` 用的正是
    //        `TranspileCasterIsPawnOutsideShootLine(instructions, matchesToSkip: 1)`，
    //        它靠「`CasterIsPawn` 第几次出现」来定位。前面只要有人插/删过指令，
    //        它跳过的就可能是错的那一处，**补丁看着装上了、实际改错了地方**。
    //     ② 改出非法的指令流 —— 栈不平衡 / 分支目标错位。这类错在 JIT 编译
    //        那个方法时才炸，而 RimWorld 的 try/catch 包不到 JIT 期。
    //        表现就是**瞬间退出、一条日志都没有**（这正是玩家描述的现象）。
    //     ③ 少数情况是 AccessViolation / 栈溢出，同样不写日志。
    //
    // ── 本项做什么、不做什么 ──
    //
    //   只读地列一遍：ExtraWeapon2 动过的那几个方法上，现在一共挂着几个补丁、
    //   分别来自哪个 Harmony ID。如果一个方法上有 **≥2 个 transpiler**，
    //   就是撞车现场，直接点名。
    //
    //   **它不修任何东西、不改任何指令、不卸载别人的补丁。**
    //   最坏情况只是多几行日志。所以它默认开启，没有任何风险。
    //
    // ── 为什么必须延迟到主菜单再扫 ──
    //
    //   我们自己的 TryInstall 在模组初始化阶段就要跑，而**别的模组的
    //   Harmony.PatchAll() 同样在那一阶段，彼此顺序不保证**。此刻扫可能
    //   扫到半成品。UIRootUpdate 是界面第一帧，那时全部模组初始化早已结束，
    //   是能拿到「最终补丁面」的最早时刻（该挂点范式与 CrossPromotionUnpatch 一致）。
    // ==========================================================================
    internal static class Ew2TranspilerConflictProbe
    {
        private const string Ew2HarmonyId = "EW2_Patch";

        private static bool installed;
        private static bool reported;

        // 被 ExtraWeapon2 直接改过的方法。名字与签名都已按 1.7 反编译逐字核对：
        //   · Verb.TryStartCastOn 有 5 参与 6 参两个重载，5 参那个只是转调 6 参，
        //     所以真正会生效的是 6 参这一个 —— 只探测它就够了。
        //   · Verb.TryCastNextBurstShot 是 protected，用 AccessTools 能拿到。
        //   · Pawn.GetChildHolders 是 public override。
        private static IEnumerable<KeyValuePair<string, MethodBase>> Targets()
        {
            yield return Pair("Verb.TryStartCastOn(6参)",
                AccessTools.Method(typeof(Verb), "TryStartCastOn", new[]
                {
                    typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                    typeof(bool), typeof(bool), typeof(bool), typeof(bool)
                }, null));

            yield return Pair("Verb.TryCastNextBurstShot",
                AccessTools.Method(typeof(Verb), "TryCastNextBurstShot", Type.EmptyTypes, null));

            yield return Pair("Verb.CanHitTarget(LocalTargetInfo)",
                AccessTools.Method(typeof(Verb), "CanHitTarget", new[] { typeof(LocalTargetInfo) }, null));

            yield return Pair("Verb.Notify_EquipmentLost",
                AccessTools.Method(typeof(Verb), "Notify_EquipmentLost", Type.EmptyTypes, null));

            yield return Pair("Verb.FirstApparelPreventingShooting",
                AccessTools.Method(typeof(Verb), "FirstApparelPreventingShooting", Type.EmptyTypes, null));

            yield return Pair("Verb_MeleeAttack.TryCastShot",
                AccessTools.Method(typeof(Verb_MeleeAttack), "TryCastShot", Type.EmptyTypes, null));

            yield return Pair("Pawn.GetChildHolders",
                AccessTools.Method(typeof(Pawn), "GetChildHolders", new[] { typeof(List<IThingHolder>) }, null));

            yield return Pair("Pawn_EquipmentTracker.Notify_KilledPawn",
                AccessTools.Method(typeof(Pawn_EquipmentTracker), "Notify_KilledPawn", new[] { typeof(Pawn) }, null));
        }

        private static KeyValuePair<string, MethodBase> Pair(string label, MethodBase m)
        {
            return new KeyValuePair<string, MethodBase>(label, m);
        }

        // ==================================================================
        // 安装：只在 UIRootUpdate 上挂一个「扫一次就撤」的 postfix。
        // ==================================================================
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            // 模组没装就没什么可探的。注意这里查的是一个小类型名，
            // 因为 `ExtraWeaponComponent` 就在全局命名空间里（该模组没给类型套 namespace），
            // 用 TypeByName 能直接命中。
            if (AccessTools.TypeByName("ExtraWeaponComponent") == null)
            {
                installed = true;
                Log.Message("[GNH LocalFixes] 详细错误勘探：未检测到 ExtraWeapon2（ExtraWeaponComponent 不存在），"
                    + "本项跳过。");
                return;
            }

            try
            {
                HarmonyMethod postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(Ew2TranspilerConflictProbe), nameof(UiProbePostfix)));

                // 方法名是 UIRootUpdate，不是 Update —— 详见 CrossPromotionUnpatch.cs 里的同一处说明。
                MethodInfo entry = AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate");
                MethodInfo play = AccessTools.Method(typeof(UIRoot_Play), "UIRootUpdate");

                if (entry == null && play == null)
                {
                    Log.Error("[GNH LocalFixes] 详细错误勘探：找不到 UIRootUpdate 挂点，本次未安装（下次会重试）。");
                    return;
                }

                if (entry != null)
                {
                    LocalFixesMod.HarmonyInstance.Patch(entry, null, postfix, null, null);
                }
                if (play != null && play != entry)
                {
                    LocalFixesMod.HarmonyInstance.Patch(play, null, postfix, null, null);
                }

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装「详细错误勘探」（诊断工具，默认关闭）"
                    + "（等界面第一帧再扫，那时所有模组的 Harmony 补丁都已就位）。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装「详细错误勘探」失败（下次会重试）：" + ex);
            }
        }

        // 界面每帧都会进来，只认第一次。
        public static void UiProbePostfix()
        {
            if (reported)
            {
                return;
            }
            reported = true;

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                // 探针自身绝不能影响游戏。
                Log.Warning("[GNH LocalFixes] 详细错误勘探：扫描自身出错（已忽略）：" + ex.Message);
            }
        }

        private static void Run()
        {
            List<string> lines = new List<string>();
            int conflicts = 0;
            int probed = 0;

            foreach (KeyValuePair<string, MethodBase> kv in Targets())
            {
                if (kv.Value == null)
                {
                    continue;
                }
                probed++;

                Patches info;
                try
                {
                    info = Harmony.GetPatchInfo(kv.Value);
                }
                catch (Exception ex)
                {
                    lines.Add("  · " + kv.Key + " —— 读取补丁信息失败：" + ex.GetType().Name);
                    continue;
                }

                if (info == null)
                {
                    continue;
                }

                List<string> names = new List<string>();
                int transpilers = 0;
                int foreignTranspilers = 0;

                Collect(info.Prefixes, "prefix", names, ref transpilers, ref foreignTranspilers);
                Collect(info.Postfixes, "postfix", names, ref transpilers, ref foreignTranspilers);
                Collect(info.Transpilers, "transpiler", names, ref transpilers, ref foreignTranspilers);
                Collect(info.Finalizers, "finalizer", names, ref transpilers, ref foreignTranspilers);

                if (names.Count == 0)
                {
                    continue;
                }

                bool hot = transpilers >= 2 && foreignTranspilers >= 1;
                if (hot)
                {
                    conflicts++;
                }

                lines.Add("  " + (hot ? "★ " : "· ") + kv.Key
                    + "  [" + transpilers + " transpiler] "
                    + string.Join(", ", names.ToArray()));
            }

            if (lines.Count == 0)
            {
                Log.Message("[GNH LocalFixes] 详细错误勘探：扫描完成，"
                    + "它所触及的 " + probed + " 个方法上**没有**任何补丁痕迹"
                    + "（可能是本模组未启用，或加载顺序使它尚未安装）。");
                return;
            }

            string head = conflicts > 0
                ? "[GNH LocalFixes] 详细错误勘探：⚠ 检测到 " + conflicts + " 处 transpiler 撞车。"
                  + "同一方法上有多个模组在改 IL，ExtraWeapon2 的「按第 N 个匹配点定位」有可能失准，"
                  + "表现为该武器行为异常、或点开火时瞬间退出且无日志。"
                  + "排查办法：把本行下面标 ★ 的方法上的**另一个**模组临时停用，再试一次。"
                : "[GNH LocalFixes] 详细错误勘探：扫描完成，没有发现 transpiler 撞车"
                  + "（每个方法最多只被一个模组改）。";

            Log.Message(head + "\n" + string.Join("\n", lines.ToArray()));
        }

        private static void Collect(
            System.Collections.Generic.IList<Patch> patches,
            string kind,
            List<string> names,
            ref int transpilers,
            ref int foreignTranspilers)
        {
            if (patches == null)
            {
                return;
            }

            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null)
                {
                    continue;
                }

                string owner = string.IsNullOrEmpty(p.owner) ? "(未知)" : p.owner;
                names.Add(kind + ":" + owner);

                if (kind == "transpiler")
                {
                    transpilers++;
                    if (!string.Equals(owner, Ew2HarmonyId, StringComparison.Ordinal))
                    {
                        foreignTranspilers++;
                    }
                }
            }
        }
    }

    // ==========================================================================
    // 【43】ExtraWeapon2 的「额外武器击杀回调」补一道异常护栏
    //
    // 症状来源：
    //   ExtraWeapon2 的 `Patch_PawnEquipment_ExtraWeaponsKilledPawn` 是一个
    //   **postfix**，挂在 `Pawn_EquipmentTracker.Notify_KilledPawn` 上。反编译 1.7 DLL
    //   （MVID 003d14ce…，2026-11-05 版）的实际实现是：
    //
    //       Pawn pawn = __instance?.pawn;
    //       if (!ExtraWeaponStoredRegistry.TryGet(pawn, out var component)) return;
    //       foreach (ThingWithComps item in component.ExtraWeaponsForReading())
    //           item.Notify_KilledPawn(pawn);
    //
    //   它自己这一段**已经够了防护**：
    //     · `__instance?.pawn` 与 `TryGet(pawn, …)` 两层都判了空；
    //     · `ExtraWeaponsForReading()` 的实现是索引遍历 + 逐项判空
    //       （`for (i…) if (weapons[i]?.Weapon is ThingWithComps x) yield return x;`），
    //       所以即便遍历途中有槽位被清掉，也不会抛 InvalidOperationException。
    //
    //   真正的风险在**被调的那个原版方法** `ThingWithComps.Notify_KilledPawn(Pawn)`：
    //   它先读 `pawn.Ideo`，再遍历这武器身上所有 `comps`。
    //   任何一个第三方 comp 在这里抛异常，都会中断整条 postfix 链 ——
    //   排在它后面的死亡处理逻辑（掉装备、尸体生成、关系与记录更新）就**都不会执行**。
    //   这不会闪退，但会让一次死亡变成半截子状态，后续再撞上别的模组就容易连锁。
    //
    //   ⚠ 2026-11-05 复审订正：本条最初把风险归给了 EW2 的遍历写法（「没判空 / 撞上集合
    //     被改动」），那是**错的** —— 逐行反编译核对后已改正。护栏本身仍然值得留
    //     （它拦的是第三方 comp），但理由必须写对，否则下一个维护者会跑去改 EW2
    //     那段本来就没问题的代码。
    //
    // ── 本项做法 ──
    //
    //   给**它那个 postfix 方法本身**再挂一个 Finalizer（Harmony 支持补丁打补丁）：
    //   捕获异常、记一次限流日志、然后把它吞掉。这样：
    //     · 额外武器那一环出错，不再连累后续的死亡处理；
    //     · 日志里能留下第一手证据（异常类型 + 堆栈），而不是彻底看不见；
    //     · 不改 ExtraWeapon2 的任何逻辑，它想做的事照做。
    //
    // ── 边界 ──
    //
    //   · 目标模组没装 / 它改了方法名 → 安静跳过，只在日志留一行。
    //   · 前 3 次打完整堆栈，之后每次只加一个计数，避免刷屏。
    //   · 这只是护栏，不是「已修复玩家的闪退」——闪退的根因若在别处，
    //     本项不会让它消失，但它能让病历更完整。
    // ==========================================================================
    internal static class Ew2DeathPathRepair
    {
        private static bool installed;
        private static int caught;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                Type patchType = AccessTools.TypeByName("Patch_PawnEquipment_ExtraWeaponsKilledPawn");
                if (patchType == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【43】未检测到 ExtraWeapon2 的 "
                        + "Patch_PawnEquipment_ExtraWeaponsKilledPawn，本项跳过。");
                    return;
                }

                // ⚠ 不能用 AccessTools.Method(patchType, "Postfix", Type.EmptyTypes, null)：
                //   那个 postfix 的签名是 `Postfix(Pawn_EquipmentTracker __instance)` —— **有一个参数**。
                //    按空参数数组去找必然返回 null，于是这项会「静默跳过」。
                //    （2026-11-05 首次实机验证就是这么跳过的，日志写着「找不到无参 Postfix」。）
                //    这里改成按名字扫一遍，不猜签名：补丁类的 postfix 恒为 public static void Postfix(...)。
                MethodInfo postfix = null;
                MethodInfo[] all = patchType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Name == "Postfix")
                    {
                        postfix = all[i];
                        break;
                    }
                }

                if (postfix == null)
                {
                    installed = true;
                    Log.Message("[GNH LocalFixes] 【43】ExtraWeapon2 的 Patch_PawnEquipment_ExtraWeaponsKilledPawn "
                        + "里没有任何名为 Postfix 的静态方法，本项跳过（可能作者改了实现方式）。");
                    return;
                }

                MethodInfo finalizer = AccessTools.Method(
                    typeof(Ew2DeathPathRepair), nameof(SwallowFinalizer), null, null);

                LocalFixesMod.HarmonyInstance.Patch(postfix, null, null, null,
                    new HarmonyMethod(finalizer));

                installed = true;
                Log.Message("[GNH LocalFixes] 已安装【43】ExtraWeapon2 死亡路径护栏："
                    + "给它的 Notify_KilledPawn postfix 加了 Finalizer。"
                    + "该 postfix 自己那段遍历是安全的（索引遍历 + 逐项判空）；真正的风险在被调的 ThingWithComps.Notify_KilledPawn："
                    + "它读 pawn.Ideo、再遍历这武器的所有 comp，第三方 comp 一抛就会中断整条 postfix 链，让排在后面的死亡处理（掉装备、尸体生成、记录）全部跳过。"
                    + "现在改为吞掉它自己的异常并留日志。");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 安装【43】ExtraWeapon2 死亡路径护栏失败（下次会重试）：" + ex);
            }
        }

        // Harmony Finalizer：返回 null = 吞掉异常；返回原异常 = 继续抛。
        public static Exception SwallowFinalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }

            caught++;
            if (caught <= 3)
            {
                Log.Warning("[GNH LocalFixes] 【43】ExtraWeapon2 的额外武器击杀回调抛异常，"
                    + "已拦下（第 " + caught + " 次；后续同类只计数不再刷堆栈）。原始异常："
                    + __exception);
            }
            else if (caught == 4)
            {
                Log.Warning("[GNH LocalFixes] 【43】同类异常已达 4 次，之后不再打印堆栈。");
            }

            return null;
        }
    }
}
