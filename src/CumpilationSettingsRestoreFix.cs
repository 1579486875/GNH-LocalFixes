using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    /// <summary>
    /// 【修的是什么问题】
    ///   「RJW Menstruation Fluids」（eltoro.rjw.menstruation.fluids，ELT 的月经体液）
    ///   里有一段 [StaticConstructorOnStartup] 代码（Cumpilation_Integration.CumpilationSettingsOverride；
///   源文件名是 SettingsDisabler.cs，但**类名**是这个），
    ///   它在**每次**游戏加载完成时无条件把「Cumpilation」（vegapnk.cumpilation）的 9 个
    ///   设置项写成 false —— 含 Cumflation、Stuffing、渗漏/自动泄压/隐私等一大票开关。
    ///   玩家在设置界面里打开也没用：下次启动又被按回去。
    ///
    /// 【为什么会这样】三层叠加，缺一不可
    ///   ① Cumpilation 的设置字段**全是 static**（public static bool EnableCumflation = true;）；
    ///   ② 它的 Settings.ExposeData() 用 Scribe_Values.Look(ref 静态字段, 名字, 默认值) 读盘；
    ///   ③ 读盘发生在 Mod 构造函数（CreateModClasses 期间），而 ELT 的覆盖发生在更晚的
    ///      StaticConstructorOnStartupUtility.CallAll() —— 于是刚读好的 true 被覆盖成 false，
    ///      之后再没有任何读盘来纠正它。
    ///
    /// 【怎么修】不硬编码任何开关值，而是让 Cumpilation **自己重新读一次盘**
    ///   LoadedModManager.ReadModSettings&lt;T&gt; 内部会 Scribe_Deep.Look → 触发 ExposeData()
    ///   → 那 12 个 Scribe_Values.Look 把静态字段设回磁盘上的值（磁盘上没写的项回落到默认值）。
    ///   效果 = 「把玩家自己存过的设置原样恢复」。
    ///
    /// 【时机为什么这样选】
    ///   ELT 用 LongEventHandler.ExecuteWhenFinished(Apply)，其执行顺序取决于
    ///   GenTypes.AllTypesWithAttribute 的枚举顺序，和我们谁先谁后**不可控**。
    ///   但无论谁先，每一次 ExecuteWhenFinished 最终都会走到
    ///   LongEventHandler.ExecuteToExecuteWhenFinished() —— 把恢复挂在这个方法的 Postfix 上，
    ///   就一定能排在 ELT 的 Apply 之后（且只真正恢复一次）。
    ///
    /// 【什么情况下会跳过】
    ///   · 没装 Cumpilation → 直接返回，不做任何事；
    ///   · 没装 ELT 月经体液 → 没有覆盖者，我们仍恢复一次（无害，只是把内存值对齐磁盘）；
    ///   · 反射拿不到类型/方法、或读盘抛异常 → 记一条 Warning 后放弃，绝不外抛。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CumpilationSettingsRestoreFix
    {
        private const string CumpilationPackageId = "vegapnk.cumpilation";
        private const string HarmonyId = "gnh.cn.cys.localfixes.cumpilationrestore";

        private static bool patched;
        private static bool restored;

        static CumpilationSettingsRestoreFix()
        {
            Install();
        }

        private static void Install()
        {
            if (patched) return;
            patched = true;
            try
            {
                // AccessTools.Method 的第 4 个参数是「泛型类型参数」，非泛型方法必须传 null。
                MethodInfo target = AccessTools.Method(
                    typeof(LongEventHandler), "ExecuteToExecuteWhenFinished", null, null);
                MethodInfo postfix = AccessTools.Method(
                    typeof(CumpilationSettingsRestoreFix), "Postfix", null, null);
                if (target == null || postfix == null)
                {
                    Log.Warning("[GNH LocalFixes] Cumpilation 设置恢复：找不到挂点，跳过（不影响其它修复）。");
                    return;
                }
                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(postfix));
                Log.Message("[GNH LocalFixes] 已挂上 Cumpilation 设置恢复 —— "
                    + "「ELT 月经体液」每次启动强制关闭的那些开关，会被还原成你自己存过的值。");
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Cumpilation 设置恢复安装失败（不影响其它修复）：" + ex);
            }
        }

        /// <summary>
        /// 挂在 LongEventHandler.ExecuteToExecuteWhenFinished() 之后。
        /// 此时所有 ExecuteWhenFinished 排队的动作（含 ELT 的强制关闭）都已执行完毕。
        /// 只真正恢复一次：之后每次长事件结束都会进来，直接 return。
        ///
        /// ⚠ 依赖前提（2026-10-12 第二轮复审记录；Lead 与复审员两条独立路径都指出过）：
        ///   restored 是在**恢复之前**置位的，语义是「只试一次」。这要求 Postfix 第一次触发时，
        ///   ELT 的 Apply 已经在同一批次里跑过。
        ///   依据：LongEventHandler.ExecuteWhenFinished 是「先把 action 入列，再（可能）执行整批」，
        ///   而本 Postfix 挂在 ExecuteToExecuteWhenFinished **返回之后** —— 只要 Apply 与 Postfix
        ///   属于同一批次，顺序就是确定的。
        ///   理论上仍有一个窗口：若 StaticConstructorOnStartupUtility.CallAll() 期间发生过一次
        ///   ExecuteToExecuteWhenFinished（即 currentEvent == null 或 ShouldWaitUntilDisplayed），
        ///   且那一批次里还没有 ELT 的 Apply，我们就会先恢复、随后被 Apply 覆盖且不再重试。
        ///   实测本机时间线（2026-10-12 基线日志）：Apply 与恢复各只出现一次，中间 86 行是同批
        ///   其它模组的 action，该窗口没有发生。
        ///   若将来日志出现「日志说恢复成功、但设置仍被关」，就是踩到了这个窗口 —— 届时把
        ///   restored = true 移到恢复成功之后（或允许重试一次）即可，不需要改其它逻辑。
        /// </summary>
        public static void Postfix()
        {
            if (restored) return;
            restored = true;
            try
            {
                Restore();
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] Cumpilation 设置恢复出错（不影响其它修复）：" + ex);
            }
        }

        private static void Restore()
        {
            ModContentPack mod = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(m => m != null && m.PackageId == CumpilationPackageId);
            if (mod == null)
            {
                return;   // 没装 Cumpilation，无需恢复
            }

            int ok = 0;
            if (RestoreOne("Cumpilation.Settings", mod.FolderName, "Cumpilation_SettingsController"))
            {
                ok++;
            }
            if (RestoreOne("Cumpilation.Leaking.Settings", mod.FolderName, "LeakCum_SettingsController"))
            {
                ok++;
            }

            Log.Message("[GNH LocalFixes] Cumpilation 设置已按磁盘值恢复（" + ok + "/2 组）。"
                + "作用：抵消「ELT 月经体液」每次启动把它们强制关闭的副作用；"
                + "恢复的是你自己在设置界面里存过的值，本补丁不写死任何开关。");
        }

        /// <summary>
        /// 让某个 ModSettings 子类重新读一次盘。
        /// ReadModSettings&lt;T&gt; 内部会 Scribe_Deep.Look → 触发该类的 ExposeData()，
        /// 而 Cumpilation 的字段是 static，所以读盘结果会直接落到全局状态上。
        /// </summary>
        private static bool RestoreOne(string settingsTypeName, string modIdentifier, string modHandleName)
        {
            Type settingsType = AccessTools.TypeByName(settingsTypeName);
            if (settingsType == null)
            {
                return false;   // 该版本的 Cumpilation 没有这个设置类
            }
            MethodInfo read = AccessTools.Method(typeof(LoadedModManager), "ReadModSettings", null, null);
            if (read == null || !read.IsGenericMethodDefinition)
            {
                return false;
            }
            try
            {
                read.MakeGenericMethod(settingsType)
                    .Invoke(null, new object[] { modIdentifier, modHandleName });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 恢复 " + settingsTypeName + " 失败：" + ex.Message);
                return false;
            }
        }
    }
}
