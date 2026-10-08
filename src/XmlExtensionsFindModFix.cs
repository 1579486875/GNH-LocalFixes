using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 修复：中文环境下 XmlExtensions 的 <FindMod> 按「名字」判定官方 DLC 会一直失败
    // ==========================================================================
    //
    // ⚠️ 先把话说清楚，免得后来人误会（2026-10-04 实地核实过）：
    //
    //   本文件**不是**游戏日志里那几条
    //       Patch operation Verse.PatchOperationFindMod(Royalty) failed
    //   的解药。那几条红字的真正原因完全在别处，详见下面「与那条报错的区别」一节。
    //
    //   本文件修的是**另一条、目前在本机没有触发、但确实存在**的隐患：
    //   XmlExtensions 自己那套 FindMod 在中文环境下会**静默**跳过补丁（不报红字，
    //   所以从日志里根本看不出来）。它属于「同样的病、不同的器官」。
    //
    // --------------------------------------------------------------------------
    // 【它到底修什么：一个会被翻译的名字】
    //
    // 反编译 XmlExtensions.dll（1.6 版，token 0x060002F5）里那句判定：
    //
    //     internal bool <Patch>b__0(ModContentPack m)
    //     {
    //         return (<>4__this.packageId ? m.PackageId.ToLower() : m.Name.ToLower())
    //                == mod.ToLower();
    //     }
    //
    // 没写 <packageId>true</packageId> 时走的是 m.Name 这一支。而：
    //
    //     Verse.ModContentPack.Name  =>  nameInt
    //     nameInt 由构造函数参数赋值，来源是 ModMetaData.Name
    //
    // 再看 Verse.ModMetaData.Name 的反编译（官方原文）：
    //
    //     public string Name
    //     {
    //         get
    //         {
    //             ExpansionDef expansion = Expansion;
    //             if (expansion == null) return meta.name;   // 普通模组：About.xml 的 <name>
    //             return expansion.label;                    // 官方 DLC：ExpansionDef.label（会被翻译！）
    //         }
    //     }
    //
    // 于是简体中文环境下：
    //
    //     模组作者写的        <mods><li>Royalty</li></mods>
    //     DLC 的 m.Name        「皇权」
    //     判定结果            "皇权" == "royalty"  →  false  →  整块补丁被静默跳过
    //
    // 注意「静默」两个字。XmlExtensions.Boolean.FindMod.Evaluation 的原文是：
    //
    //     if (LoadedModManager.RunningMods.Any(...)) { flag = true; if (logic == "or") break; }
    //     else                                      { flag = false; if (logic == "and") break; }
    //     b = flag;
    //     return true;
    //
    // —— 它只是把判定结果写进 ref bool b，**从不报错**。所以这种失败在日志里
    //    一条痕迹都没有：补丁整块不生效，玩家只会觉得「这个模组的内容怎么不全」。
    //
    // --------------------------------------------------------------------------
    // 【与那条红字报错的区别（重要）】
    //
    // 游戏里「找不到模组」这件事有**两条完全独立的代码路径**，各修各的：
    //
    //   ① 原版  Verse.PatchOperationFindMod.ApplyWorker
    //           → 调 ModLister.HasActiveModWithName(名字)
    //           → 由本修复包里的 FindModLanguageFix.cs 负责
    //
    //   ② XmlExtensions  XmlExtensions.FindMod.Patch / Boolean.FindMod.Evaluation
    //           → 直接遍历 LoadedModManager.RunningMods 比字符串
    //           → **由本文件负责**
    //
    // 而 2026-10-04 日志里那 4 条
    //     [Vanilla Furniture Expanded - Spacer Module] Patch operation Verse.PatchOperationFindMod(Royalty) failed
    // 属于路径 ①，而且**根本不是「找不到 Royalty」**：
    //
    //     · 原版 ApplyWorker 原文是：找不到 → 返回 true（安静跳过，不报错）
    //       只有 <match> 里的子操作失败，才会把 false 一路抛上来被打印成「FindMod 失败」
    //     · 既然日志出现了这条红字，就说明 Royalty **被找到了**，是它 <match> 里那个
    //       PatchOperationAdd 的 xpath 没匹配上
    //     · 真正的元凶是 disroom.mashiro（工坊 3297881350，中文名 disabledroomRequirements，
    //       加载位次 470）：它用
    //           <Operation Class="PatchOperationReplace">
    //             <xpath>Defs/RoyalTitleDef[@ParentName = "BaseEmpireTitle"]/bedroomRequirements</xpath>
    //             <value><bedroomRequirements Inherit="False" /></value>
    //       把所有帝国爵位的「卧室要求」整个换成空表，于是后面 687/705/722/1033 位次那批
    //       家具模组想往 .../bedroomRequirements/li[@Class="RoomRequirement_ThingAnyOf"]/things
    //       里加床，就找不到节点了。
    //
    //   —— 换句话说：那几条红字的**标题是误报的**，罪魁是 disroom.mashiro 清空了节点。
    //
    // --------------------------------------------------------------------------
    // 【为什么这些红字是 v1.3.12 之后才出现的 —— 这一点务必看懂】
    //
    // 不是说补丁包把游戏改坏了，恰恰相反：**是 FindModLanguageFix 让问题从「看不见」
    // 变成了「看得见」**。整条因果链是这样的：
    //
    //   v1.3.11 及更早
    //       FindMod(Royalty) 判定 false（名字被翻译成「皇权」）
    //       → ApplyWorker 走进「找不到」分支 → 返回 true → 整块 <match> 被静默跳过
    //       → 那 4 个模组的补丁**什么都没做**，但**一条日志都不留**
    //
    //   v1.3.12 装上 FindModLanguageFix 之后
    //       FindMod(Royalty) 判定 true
    //       → ApplyWorker 执行 <match> 里的 PatchOperationSequence
    //       → 里面那条 PatchOperationAdd 的 xpath 找不到节点（被 disroom.mashiro 清空了）
    //       → 子操作返回 false，ApplyWorker 原样把 false 抛回去
    //       → PatchOperation.Complete 打印
    //             Patch operation Verse.PatchOperationFindMod(Royalty) failed
    //         于是补丁包里的注释 Patches/VFEPianoFirepitRestore.xml 里那句
    //         「内层 FindMod 永远不命中 → 静默空转（不报错）」就过期了。
    //
    //   两条结论：
    //     · 这 4 条红字是**老问题第一次被如实报出来**，不是新增的损坏；
    //     · 它们本身无害 —— 那几条语句只想给「贵族卧室」多登记几张床，
    //       而卧室要求已经被 disroom.mashiro 整个删掉了，登记不登记都不影响游戏。
    //       （真正有价值的连带损失 —— VFE 的钢琴/营火/石棺那 8 条 —— 已经由
    //        本补丁包的 Patches/VFEPianoFirepitRestore.xml 补做回来了。）
    //
    //   想彻底消掉这几条红字，最干净的办法是**调整加载顺序**：
    //   把 disroom.mashiro 挪到那批家具模组（工坊 3491176484 / 1718190143 /
    //   3221850511 / 2028381079，本机位次 687~722）之后。
    //   这样它们先正常追加、disroom.mashiro 最后再统一清空，
    //   最终游戏效果一模一样，而红字不再出现。
    //
    // --------------------------------------------------------------------------
    // 【本文件到底修的是什么】
    //
    //   路径 ② —— 一个目前本机还没被触发的隐患：
    //   本机所有 XmlExtensions FindMod 调用（112 处，全部来自工坊 3531909708）都写了
    //   <packageId>true</packageId>，走的是不会被翻译的 packageId，所以暂时无恙。
    //   但只要哪天有模组按「名字」写 <li>Royalty</li>，它就会像路径 ① 一样静默失效 ——
    //   而且比路径 ① 更隐蔽：**连一句红字都不会有**。
    //   把两条路径都补上，这个坑才算真正填平。
    //
    // --------------------------------------------------------------------------
    // 【修法：不绕过原逻辑，而是把「模组名」临时换成它实际的名字】
    //
    // 本补丁给「判定」加一个前置步骤（Harmony Prefix）：
    //
    //     1. 先按原逻辑预判一次。**如果原逻辑本来就能成功，那就一个字节都不动。**
    //     2. 只有当原逻辑注定失败时，才去 RunningMods 里找：
    //        有没有哪个模组的 **packageId 末段** 恰好等于模组作者写的那个词？
    //       （官方 DLC 的 packageId 形如 Ludeon.RimWorld.Royalty，末段正是 Royalty）
    //     3. 找到就把作者写的那个词**临时替换成该模组实际的 Name**（也就是「皇权」）。
    //        这样原判定 `m.Name == mod` 自然成立，原方法的其余流程
    //       （caseTrue / caseFalse / logic=and/or / foundMod 记录 / 报错信息）**全部原样执行**。
    //     4. 判定结束后（Harmony Postfix）把改动原地还原，绝不留副作用。
    //
    // 为什么用「临时改名」而不是「直接抢先返回成功」：
    // 原方法在判定之后还有一大段逻辑（执行 caseTrue / caseFalse、按 and/or 组合、记录 foundMod），
    // 抢先返回就得把这些逻辑全部重写一遍，一旦 XmlExtensions 更新就会失配。
    // 只改一个字符串、让原代码自己跑完，才是最不容易出错的做法。
    //
    // --------------------------------------------------------------------------
    // 【为什么只对 Ludeon.RimWorld.* 做末段匹配 —— 最重要的安全设计】
    //
    // 若对所有模组放开「packageId 末段匹配」，会产生误匹配：
    // 比如某个模组的 packageId 以 .Core 结尾，那么任何写了 <li>Core</li> 的判定
    // 都会突然成功，把本不该执行的补丁激活起来 —— 那比原 bug 更糟。
    //
    // 所以规则收得很紧：
    //   · 完整 packageId 相等        → 允许（对所有模组）
    //   · 官方 DLC 的 packageId 末段  → 允许（仅 Ludeon.RimWorld 前缀）
    //   · 其余一律不处理
    //
    // --------------------------------------------------------------------------
    // 【副作用：它会让一批「本来被静默跳过」的补丁开始生效】
    //
    // 这一点必须写明：修复生效后，那些按「名字」判定 DLC 的第三方补丁会**真正执行**。
    // 它们本来就该执行，但补丁本身若年久失修，可能会冒出新的红字。
    // 权衡下来仍然选择修：静默不生效比报错更难排查，也更难被玩家发现。
    //（本机实测 0 处受影响的调用：所有 XmlExtensions FindMod 都写了 packageId。）
    //
    // --------------------------------------------------------------------------
    // 【性能】
    //
    // · 只在 Def 加载阶段被调用（每个含 <FindMod> 的补丁一次，量级几百次），
    //   **不在任何 tick / 每帧路径上**。
    // · 预判会遍历一次 RunningMods（约 1100 个），命中即 break；
    //   原逻辑自己同样要遍历一次，所以最坏情况只是「多遍历一次」，代价可忽略。
    // · 「短名 → 实际 Name」的映射只构建一次并缓存，之后是 O(1) 字典查找。
    // · 反射得到的 FieldInfo 也缓存，不重复查找。
    //
    // --------------------------------------------------------------------------
    // 【兼容性】
    //
    // · XmlExtensions 是绝大多数模组的必需前置，必然存在；万一没装，
    //   Install() 会安静地什么都不做（记一条 Message），绝不影响其它修复。
    // · 只 Patch 两个具体方法（Patch / Evaluation），不碰 XmlExtensions 的其它功能。
    // · mods 列表在 Postfix 里原地还原，不留被改过的数据。
    // ==========================================================================
    internal static class XmlExtensionsFindModFix
    {
        private const string HarmonyId = "gnh.cn.cys.localfixes.xmlextfindmod";

        // 避免重复安装。
        private static bool installAttempted;

        // ---- 反射缓存：目标类型的字段 ----
        // 目标类型是 internal 的（XmlExtensions.FindMod 等），编译期引用不到，
        // 所以只能按名字找类型、再按名字找字段。这些查找只做一次。
        private static FieldInfo modsField;
        private static FieldInfo packageIdField;
        private static readonly object fieldLock = new object();

        // ---- 缓存：官方 DLC 的「短名 → 实际显示名」 ----
        // 例如中文环境下 "Royalty" → "皇权"；英文环境下两者相同。
        private static Dictionary<string, string> dlcShortNameToActualName;
        private static readonly object mapLock = new object();

        private const int ErrorLogKey = 0x58454D46; // "XEMF"

        // 第二个错误键：**显式命名**，不要再写 ErrorLogKey + 1 ——
        // 「第二个键 = 第一个键 + 1」是个隐式约定，将来有人加第三个键时极易撞号。
        // 值仍然由 ErrorLogKey 推导，保证两者永不重复。
        private const int ErrorLogKeyRestoreFailed = ErrorLogKey + 1;

        // ----------------------------------------------------------------------
        // 一次「临时改名」的现场记录。
        //
        // 为什么不用「整份列表拷贝 + 整体换回去」：那样会把 mods 字段换成另一个
        // List 实例。虽然当前没有别的代码抓着旧实例不放，但原地改回去显然更干净 ——
        // 对象的身份自始至终不变。
        // ----------------------------------------------------------------------
        private sealed class SavedEntry
        {
            public List<string> List;
            public int Index;
            public string Original;
        }

        // ----------------------------------------------------------------------
        // 安装。由 LocalFixesMod 的构造函数调用，外面已经套了 try/catch。
        // ----------------------------------------------------------------------
        public static void Install()
        {
            if (installAttempted)
            {
                return;
            }
            installAttempted = true;

            // XmlExtensions 的这两个类都是 internal，编译期无法引用，只能按名字找。
            Type boolFindMod = AccessTools.TypeByName("XmlExtensions.Boolean.FindMod");
            Type patchFindMod = AccessTools.TypeByName("XmlExtensions.FindMod");

            if (boolFindMod == null && patchFindMod == null)
            {
                // 没装 XmlExtensions 属于正常情况：
                // 它是几十个模组的共同前置，但理论上仍可能没装。安静跳过。
                Log.Message("[GNH LocalFixes] XmlExtensionsFindModFix：未找到 XmlExtensions 的 FindMod 类，"
                    + "本修复跳过（正常情况：没装 XmlExtensions 时它本来也不会出问题）。");
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(XmlExtensionsFindModFix), nameof(Prefix));
            MethodInfo postfix = AccessTools.Method(typeof(XmlExtensionsFindModFix), nameof(Postfix));
            if (prefix == null || postfix == null)
            {
                Log.Error("[GNH LocalFixes] XmlExtensionsFindModFix：找不到自己的 Prefix/Postfix 方法，本修复未安装。");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            int patched = 0;

            // ---- 目标一：Boolean.FindMod.Evaluation(ref bool b, XmlDocument xml) ----
            // 「布尔判定器」版本，反编译确认是 protected override。
            patched += PatchOne(harmony, boolFindMod, "Evaluation",
                new[] { typeof(bool).MakeByRefType(), typeof(XmlDocument) }, prefix, postfix,
                "XmlExtensions.Boolean.FindMod.Evaluation");

            // ---- 目标二：FindMod.Patch(XmlDocument xml) ----
            // 「补丁操作」版本。XmlExtensions.FindMod 只声明了这一个 Patch，不会有歧义。
            patched += PatchOne(harmony, patchFindMod, "Patch",
                new[] { typeof(XmlDocument) }, prefix, postfix,
                "XmlExtensions.FindMod.Patch");

            if (patched > 0)
            {
                Log.Message("[GNH LocalFixes] 已安装 XmlExtensions FindMod 修复（" + patched
                    + " 个判定点）。它能让中文环境下 <li>Royalty</li> 这类按名字写的判定重新匹配成功 ——"
                    + "根因是官方 DLC 的显示名会被语言包翻译成「皇权」；"
                    + "原版那条路径（PatchOperationFindMod）由 FindModLanguageFix 负责，两者互不重叠。");
            }
            else
            {
                Log.Error("[GNH LocalFixes] XmlExtensionsFindModFix：找到了 FindMod 类但没能挂上任何判定点"
                    + "（XmlExtensions 可能改了版本），本修复未生效，其余修复不受影响。");
            }
        }

        // 挂一个判定点；失败只记日志并返回 0。
        private static int PatchOne(Harmony harmony, Type type, string methodName,
            Type[] parameters, MethodInfo prefix, MethodInfo postfix, string label)
        {
            if (type == null)
            {
                return 0;
            }
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName, parameters);
                if (target == null)
                {
                    Log.Error("[GNH LocalFixes] XmlExtensionsFindModFix：找不到方法 " + label + "，该判定点未挂上。");
                    return 0;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] XmlExtensionsFindModFix：挂载 " + label + " 失败（已跳过该点）：" + ex);
                return 0;
            }
        }

        // ----------------------------------------------------------------------
        // 前置：只在「原判定注定失败」时，把作者写的 DLC 短名换成该 DLC 的实际显示名。
        //
        // __instance —— 被调用的那个 FindMod 对象（类型是 internal 的，所以用 object 接）
        // __state    —— 把「改了哪一项」交给 Postfix 去原地还原；null 表示本次没改过
        // ----------------------------------------------------------------------
        private static void Prefix(object __instance, out SavedEntry __state)
        {
            __state = null;

            // 本补丁挂在 Def 加载路径上：任何意外都必须吞掉。
            // 吞掉之后的行为等同「没装这个补丁」，不会把加载流程搞坏。
            try
            {
                if (__instance == null)
                {
                    return;
                }

                EnsureFields(__instance.GetType());
                if (modsField == null)
                {
                    return;
                }

                List<string> mods = modsField.GetValue(__instance) as List<string>;
                if (mods == null || mods.Count == 0)
                {
                    return;
                }

                bool byPackageId = false;
                if (packageIdField != null)
                {
                    object raw = packageIdField.GetValue(__instance);
                    byPackageId = (raw is bool) && (bool)raw;
                }

                // ---- 第 1 步：先按原逻辑预判。能成功就彻底不碰。 ----
                for (int i = 0; i < mods.Count; i++)
                {
                    if (MatchesRunning(mods[i], byPackageId))
                    {
                        return;
                    }
                }

                // ---- 第 2 步：原逻辑注定失败，看看是不是「DLC 短名」的问题。 ----
                EnsureDlcMap();
                Dictionary<string, string> map = dlcShortNameToActualName;
                if (map == null || map.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < mods.Count; i++)
                {
                    string actualName;
                    if (map.TryGetValue(mods[i], out actualName) && !string.IsNullOrEmpty(actualName))
                    {
                        // ---- 第 3 步：临时替换成实际显示名，让原判定自己成功。 ----
                        SavedEntry saved = new SavedEntry();
                        saved.List = mods;
                        saved.Index = i;
                        saved.Original = mods[i];

                        mods[i] = actualName;
                        __state = saved;
                        return;                             // 一次调用只需修正一处
                    }
                }
            }
            catch (Exception ex)
            {
                __state = null;
                Log.ErrorOnce("[GNH LocalFixes] XmlExtensionsFindModFix.Prefix 出错"
                    + "（已忽略，不影响游戏；该补丁失效时行为与未安装一致）：" + ex, ErrorLogKey);
            }
        }

        // ----------------------------------------------------------------------
        // 收尾：把改动过的那一项原地还原，绝不给 XmlExtensions 留下被改过的数据。
        // ----------------------------------------------------------------------
        private static void Postfix(SavedEntry __state)
        {
            if (__state == null || __state.List == null)
            {
                return;
            }
            try
            {
                if (__state.Index >= 0 && __state.Index < __state.List.Count)
                {
                    __state.List[__state.Index] = __state.Original;
                }
            }
            catch (Exception ex)
            {
                // 还原失败影响也很小：那个节点里存的是 DLC 的实际名，下次同样能匹配成功。
                Log.ErrorOnce("[GNH LocalFixes] XmlExtensionsFindModFix.Postfix 还原 mods 失败"
                    + "（影响很小）：" + ex, ErrorLogKeyRestoreFailed);
            }
        }

        // ----------------------------------------------------------------------
        // 缓存目标类型的字段。只有第一次需要反射查找。
        // ----------------------------------------------------------------------
        private static void EnsureFields(Type type)
        {
            if (modsField != null && modsField.DeclaringType == type)
            {
                return;
            }
            lock (fieldLock)
            {
                if (modsField != null && modsField.DeclaringType == type)
                {
                    return;
                }
                modsField = AccessTools.Field(type, "mods");
                packageIdField = AccessTools.Field(type, "packageId");
            }
        }

        // ----------------------------------------------------------------------
        // 用与 XmlExtensions **完全相同**的方式判断「这个名字是否命中某个已加载模组」。
        //
        // byPackageId 为 true 时比对 PackageId，否则比对 Name ——
        // 与反编译出的原实现逐字对应：
        //     (packageId ? m.PackageId.ToLower() : m.Name.ToLower()) == mod.ToLower()
        // ----------------------------------------------------------------------
        private static bool MatchesRunning(string value, bool byPackageId)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            IEnumerable<ModContentPack> running = LoadedModManager.RunningMods;
            if (running == null)
            {
                return false;
            }

            foreach (ModContentPack pack in running)
            {
                if (pack == null)
                {
                    continue;
                }
                string candidate = byPackageId ? pack.PackageId : pack.Name;
                if (!string.IsNullOrEmpty(candidate)
                    && string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ----------------------------------------------------------------------
        // 构建「官方 DLC 的短名 → 它的实际显示名」映射，例如中文环境下 "Royalty" → "皇权"。
        // 只收 Ludeon.RimWorld 前缀的模组，避免任何误匹配。
        // ----------------------------------------------------------------------
        private static void EnsureDlcMap()
        {
            if (dlcShortNameToActualName != null)
            {
                return;
            }
            lock (mapLock)
            {
                if (dlcShortNameToActualName != null)
                {
                    return;
                }

                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                IEnumerable<ModContentPack> running = LoadedModManager.RunningMods;
                if (running != null)
                {
                    foreach (ModContentPack pack in running)
                    {
                        if (pack == null)
                        {
                            continue;
                        }

                        string packageId = pack.PackageId;
                        if (string.IsNullOrEmpty(packageId))
                        {
                            continue;
                        }

                        // 只处理官方系列（Core 与各 DLC）。
                        if (!packageId.StartsWith("Ludeon.RimWorld", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string actualName = pack.Name;   // 中文环境下就是「皇权」这类被翻译过的名字
                        if (string.IsNullOrEmpty(actualName))
                        {
                            continue;
                        }

                        // 完整 packageId 也登记一条（作者写全名的情况）
                        if (!map.ContainsKey(packageId))
                        {
                            map[packageId] = actualName;
                        }

                        // 再登记「末段短名」：Ludeon.RimWorld.Royalty → Royalty
                        int lastDot = packageId.LastIndexOf('.');
                        if (lastDot >= 0 && lastDot + 1 < packageId.Length)
                        {
                            string shortName = packageId.Substring(lastDot + 1);
                            if (!map.ContainsKey(shortName))
                            {
                                map[shortName] = actualName;
                            }
                        }
                    }
                }

                // 基础游戏：packageId 是 Ludeon.RimWorld、末段是 RimWorld，
                // 但模组作者习惯写 Core。补上这条别名。
                if (!map.ContainsKey("Core"))
                {
                    string coreName;
                    if (map.TryGetValue("Ludeon.RimWorld", out coreName) && !string.IsNullOrEmpty(coreName))
                    {
                        map["Core"] = coreName;
                    }
                }

                // ⚠️ 只在「确实扫到了官方模组」时才缓存。
                // 万一本方法被在「模组列表还没填充好」的时机调用过，缓存下空表会导致
                // 之后永远修不好 —— 这个坑在同类补丁上踩过一次，这里务必避开。
                if (map.Count > 0)
                {
                    dlcShortNameToActualName = map;
                }
            }
        }
    }
}
