// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与人类作者（大赢经直插白皮赢道）决定。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 【68】翻译文件给 description 多带了首尾空白 —— DevMode 下每次加载刷
    //       「Config error in X: description has trailing whitespace」
    // ==========================================================================
    //
    // ── 症状（2026-11-16 第五轮日志诊断）──
    //
    //   ERROR [Verse] Config error in LDR_ISBM_silo: description has trailing whitespace
    //
    //   本轮日志里出现 2 次（跨两次启动）。全程只报这一个 def，
    //   但它并不是孤例 —— 见下面的全库扫描结果。
    //
    // ── 根因：真凶是**翻译文件**，不是源 def ──
    //
    //   源 def 在
    //     …\294100\3695591404\1.6\Defs\ThingDefs_Buildings\Buildings_MissileSilo_LDR.xml
    //   里是干净的，没有任何尾随空白。真正带空白的是它的简体中文翻译：
    //     …\294100\3695591404\1.6\Languages\ChineseSimplified (简体中文)\
    //        DefInjected\ThingDef\Buildings_MissileSilo_LDR.xml
    //   那条 <LDR_ISBM_silo.description> 的文本末尾多了一个**半角空格**：
    //     "…其发射设施应用了诸多先进的加固技术。 "   ← 引号内最后那个空格
    //   同一个文件里另外 9 条 description 都没有这个问题，只有这一条有。
    //
    // ── 为什么只有「翻译」这条路会出问题（已反编译确认）──
    //
    //   · 普通 `Defs/*.xml` 的 <description> 走 `DirectXmlToObjectNew` 解析，
    //     多行缩进由引擎处理，不会把排版空白塞进字段；
    //   · `DefInjected` 走的是
    //       LoadedLanguage.InjectIntoData_BeforeImpliedDefs()
    //         → DefInjectionPackage.InjectIntoDefs()
    //         → field.SetValue(def, value)
    //     **原样赋值、不 trim**。XML 里写什么，字段里就是什么。
    //
    //   实测佐证：本机 1000+ 模组、32,842 个 DefInjected XML 文件里，
    //   `Defs/*.xml` 的多行 description 数以万计却零报错，
    //   而扫描出的 20 条「两段式 defName.description 首尾带空白」**全部**来自
    //   DefInjected —— 两者一一对应，路径判断成立。
    //
    // ── 为什么扫描出 20 条、日志却只报 1 条（重要，别以为漏了）──
    //
    //   `Def.ConfigErrors()` 里确实有这两条检查：
    //       if (char.IsWhiteSpace(description[0]))                     → "description has leading whitespace"
    //       if (char.IsWhiteSpace(description[description.Length - 1])) → "description has trailing whitespace"
    //   但**很多 Def 子类 override 了 ConfigErrors() 却根本没调用 base.ConfigErrors()**。
    //   已反编译确认：`Verse.GeneDef.ConfigErrors()` 通篇没有 base 调用 ——
    //   所以 `RJW_Traits_Bimbo_Gene.description` 那条前导空格**永远不会被报出来**；
    //   `IRBM.MissileBodyDef`（IRBM_DF41A 那条）同理。
    //   只有「override 了、并且调了 base」的类型才会报 ——
    //   `ThingDef.ConfigErrors()` 调了，所以 LDR_ISBM_silo 就露出来了。
    //
    //   ⇒ 报告里列出的其余 19 条**不是误报**，它们只是被各自的类"藏"住了；
    //     一旦作者更新时换了基类、或游戏某版补上 base 调用，就会集体冒出来。
    //
    // ── 为什么不直接改那个翻译文件 ──
    //
    //   那是**作者的工坊文件**（mod 3695591404 自带简中）。直接改会在
    //   Steam 更新时被覆盖回去，属于治标不治本，而且我们的补丁不该去动别人的发布物。
    //   （与【61】改 LDR 的 int 字段不同：那处是用 XML Patch 打补丁、可随作者更新自动让路；
    //     而翻译层不受 PatchOperation 管辖 —— PatchOperation 只作用于 Def 数据库，
    //     DefInjected 是在此之后注入的，补丁够不着。）
    //
    // ── 我们怎么修：在翻译注入完成、校验开始之前，把 description 首尾空白裁掉 ──
    //
    //   加载时序（反编译 `Verse.PlayDataLoader.DoPlayLoad()` 逐段确认）：
    //      ① LoadedModManager.LoadAllActiveMods()      ← 本模组构造函数在这里
    //      ② DefDatabase<T>.AddAllInMods()             ← Def 进全局库
    //      ③ …ResolveAllWantedCrossReferences…
    //      ④ DefOfHelper.RebindAllDefOfs(early)
    //      ⑤ TKeySystem.BuildMappings()
    //      ⑥ BackstoryTranslationUtility…
    //    ★ ⑦ LanguageDatabase.activeLanguage.InjectIntoData_BeforeImpliedDefs()
    //                                                      ← **翻译在这里注入**
    //      ⑧ DefGenerator.GenerateImpliedDefs_PreResolve()
    //      ⑨ …ResolveAllReferences…
    //      ⑩ ResetStaticDataPost()
    //   ★ ⑪ if (Prefs.DevMode) DefDatabase<T>.ErrorCheckAllDefs()   ← **红字在这里报**
    //      ⑫ LongEventHandler.ExecuteWhenFinished( InjectIntoData_AfterImpliedDefs )
    //   ★ ⑬ LongEventHandler.ExecuteWhenFinished( StaticConstructorOnStartupUtility.CallAll() )
    //
    //   结论有两条：
    //     · `[StaticConstructorOnStartup]` 在 ⑬，**晚于** ⑪ 的校验 —— 用它来 trim 来不及；
    //     · ⑦ 是 ⑪ 之前**最后一个**能碰到翻译结果的公开入口，所以挂它的 Postfix。
    //   （`InjectIntoData_BeforeImpliedDefs` 在 1.6.4871 与 1.7.5011 都存在且签名一致，
    //     `GenDefDatabase.AllDefTypesWithDatabases()` / `GetAllDefsInDatabaseForDef(Type)`
    //     同样两版都有 —— 已用 DecompilerServer 对两套 Assembly-CSharp 逐一核对。）
    //
    // ── 为什么 trim 是「修好」而不是「掩盖」──
    //
    //   · 游戏自己在 `Def.ConfigErrors()` 里把首尾空白**明确定义为错误**，
    //     所以裁掉它们 = 把数据修成引擎认可的状态，不是把报警捂住；
    //   · `string.Trim()` 去掉的是**不可见字符**，不改动任何一个人能读到的字，
    //     译文语义零变化；
    //   · 不动 `label`。虽然扫描时也看到几条 label 带前导空格
    //     （如 `MechBaby_BornFromMech.label`、`Conversion.comps.2.label`），
    //     但 label 会被用于字符串拼接，作者可能**故意**留空格当分隔符 ——
    //     trim 它有实际破坏风险，而这不在「红字」范畴内，故一律不碰。
    //
    // ── 边界处理（只在这几种情况下什么都不做）──
    //
    //   · description 为 null 或空 → 跳过（空串是另一条校验 "empty description"，别越界）；
    //   · description **整条都是空白** → 跳过。因为 trim 完会变成 ""，
    //     那反而会把「首尾空白」升级成「empty description」，越修越糟；
    //   · 首尾本来就没有空白 → 跳过（绝大多数 def 走这条，零开销）。
    //
    // ── 幂等与自愈 ──
    //
    //   · 已经 trim 过的字符串再 trim 不变，重复调用安全（热重载 / 切语言都无害）；
    //   · 作者哪天自己把翻译改对了，本项就什么都不做，日志也不再打印；
    //   · 玩家关掉本项 → 整项不安装，回到原状。
    // ==========================================================================

    /// <summary>
    /// 【68】在语言包把翻译注入 Def 之后、DevMode 校验之前，
    /// 把所有 Def 的 <see cref="Def.description"/> 首尾空白裁掉。
    /// 只动不可见字符，不改任何可见文本；不碰 label。
    /// </summary>
    internal static class DescriptionWhitespaceTrimFix
    {
        /// <summary>本补丁是否已装。</summary>
        private static bool installed;

        /// <summary>被裁掉空白的 def 个数（累计）。</summary>
        private static int trimmedCount;

        /// <summary>被改动的 defName 样例，用于在日志里点名。</summary>
        private static readonly List<string> samples = new List<string>();

        /// <summary>日志里最多点名几个。</summary>
        private const int MaxSamples = 6;

        /// <summary>安装本补丁。由 LocalFixesMod 的构造函数通过 TryInstall 调用。</summary>
        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(LoadedLanguage), "InjectIntoData_BeforeImpliedDefs", null, null);
                if (target == null)
                {
                    Log.Message("[GNH LocalFixes] 【68】LoadedLanguage.InjectIntoData_BeforeImpliedDefs"
                        + " not found; the description-whitespace trim was skipped.");
                    installed = true;
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(DescriptionWhitespaceTrimFix), nameof(Postfix));
                if (postfix == null)
                {
                    Log.Error("[GNH LocalFixes] 【68】internal error: Postfix not found.");
                    return;
                }

                LocalFixesMod.HarmonyInstance.Patch(
                    target, null, new HarmonyMethod(postfix), null, null);

                installed = true;
                Log.Message("[GNH LocalFixes] 【68】description whitespace trim installed"
                    + " (runs right after language injection, before DevMode def validation).");
            }
            catch (Exception ex)
            {
                Log.Error("[GNH LocalFixes] 【68】failed to install the description trim: " + ex);
            }
        }

        /// <summary>
        /// 翻译刚注入完，遍历所有 Def 把 description 的首尾空白裁掉。
        /// 必须在 ErrorCheckAllDefs() 之前跑，否则那条红字已经先报出去了。
        /// </summary>
        public static void Postfix()
        {
            try
            {
                int now = TrimAllDescriptions();
                trimmedCount += now;

                if (now <= 0)
                {
                    return;
                }

                Log.Message("[GNH LocalFixes] 【68】已裁掉 " + now
                    + " 个 Def 的 description 首尾空白（" + DescribeSamples() + "）。"
                    + "原因：DefInjected 翻译是原样 SetValue 进字段的、不 trim，"
                    + "而 Def.ConfigErrors() 把首尾空白判为错误，DevMode 下就会刷"
                    + "「description has trailing/leading whitespace」。"
                    + "本项只去掉不可见字符，不改任何可见文字，也不动 label。"
                    + "若你关掉本项，那些红字会原样回来 —— 可以用来向模组作者反馈。");
            }
            catch (Exception ex)
            {
                // 兜底：裁剪失败绝不能连累加载流程。
                Log.Error("[GNH LocalFixes] 【68】description trim threw: " + ex);
            }
        }

        /// <summary>遍历全部 def 类型，裁掉每个 def 的 description 首尾空白。返回改动的个数。</summary>
        private static int TrimAllDescriptions()
        {
            int count = 0;

            foreach (Type defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                if (defType == null)
                {
                    continue;
                }

                IEnumerable<Def> defs;
                try
                {
                    defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType);
                }
                catch (Exception)
                {
                    // 某个类型暂时没有数据库（生成期/热重载中间态）→ 跳过它，不影响其它类型。
                    continue;
                }
                if (defs == null)
                {
                    continue;
                }

                foreach (Def def in defs)
                {
                    if (def == null)
                    {
                        continue;
                    }
                    if (TrimOne(def))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>裁一个 def。真的改动了才返回 true。</summary>
        private static bool TrimOne(Def def)
        {
            string raw = def.description;
            if (raw == null || raw.Length == 0)
            {
                return false;
            }

            // 先看首尾是不是真有空白 —— 绝大多数 def 在这里就退出了，不做任何分配。
            if (!char.IsWhiteSpace(raw[0]) && !char.IsWhiteSpace(raw[raw.Length - 1]))
            {
                return false;
            }

            string trimmed = raw.Trim();

            // 整条都是空白：裁完会变成空串，那会触发另一条 "empty description" 校验。
            // 宁可保持原样（现状只是"首尾空白"这一条），也不要把问题升级。
            if (trimmed.Length == 0)
            {
                return false;
            }

            def.description = trimmed;

            if (samples.Count < MaxSamples)
            {
                samples.Add(def.defName);
            }
            return true;
        }

        /// <summary>把样例拼成「a、b、c 等」。</summary>
        private static string DescribeSamples()
        {
            if (samples.Count == 0)
            {
                return "样本未记录";
            }
            string joined = string.Join("、", samples.ToArray());
            return trimmedCount > samples.Count ? joined + " 等" : joined;
        }

        /// <summary>累计被裁掉空白的 def 个数（供自检/文档引用）。</summary>
        internal static int TrimmedCount
        {
            get { return trimmedCount; }
        }
    }
}
