using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Verse;
using Verse.Steam;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 这个类解决的是和 CrossPromotionUnpatch.cs 同一场崩溃，是「第二道保险」。
    // ==========================================================================
    //
    // （下面这条调用链已经对照过两份一模一样的崩溃转储，不是猜的。
    //   出问题的环境：RimWorld 1.6.4871，steam_api64.dll 6.91.21.57）
    //
    //   游戏在画「模组」页面
    //     → CrossPromotion 插进来的那段代码
    //       （它被包在 Visual Exceptions / Achtung! / Camera+ 内部，
    //         运行时才从内嵌资源里解出来，所以磁盘上那份 dll 里找不到它）
    //       → 去问游戏：「这个模组的 Steam 信息是什么？」
    //         → 游戏新建一个「Steam 信息」对象
    //           → 新建过程中调用 Steam 官方早就废弃的 RequestUGCDetails
    //             ← 这里直接抛异常，被游戏崩溃处理器抓住，进程就被杀了
    //
    // 这个异常跟内存占用没有任何关系。
    //
    // 【为什么要装两道保险】
    //
    // 因为实测发现「只修一处」不管用。
    //
    // 原因跟 CrossPromotionUnpatch.cs 里写的一样：Mono 运行时会把很短的方法
    // 直接抄写到调用它的地方（内联），所以那段会崩的代码可能已经被抄进
    // 上层方法里了，我们改下面那一层，它根本不知道。
    //
    //   第一道：跳过那个废弃的 Steam 调用（SkipDetailsQuery）。
    //           代价为零 —— 那个接口本来就废弃了，
    //           它返回的数据「模组」页面从来不显示。
    //
    //   第二道：把「取 Steam 信息」这个方法整个顶替掉
    //           （SafeGetWorkshopItemHook）。
    //           换一种说法是：从源头绕开那个会崩的构造函数，它压根不会被执行。
    //
    // 【代价是什么】
    //
    // 顶替之后要特别当心「作者」这个字段，它**确实有**调用点，而且后果不小：
    //   ModMetaData.CanToUploadToWorkshop() 的检查顺序是
    //     · 官方模组 → 返回 false；
    //     · Source != ContentSource.ModsFolder（例如工坊订阅来的）→ 返回 false；
    //     · GetWorkshopItemHook().MayHaveAuthorNotCurrentUser → 返回 false；
    //   而 MayHaveAuthorNotCurrentUser 读的正是 WorkshopItemHook 里的 steamAuthor 字段。
    //   空壳对象里它是默认值（CSteamID.Nil）→ 被判成「作者可能不是你」
    //   → 上传/更新菜单项直接消失，而且不报任何错。
    //
    //   这里有个前提必须记住：那个属性的第一行是
    //  「PublishedFileId == PublishedFileId_t.Invalid 时直接 return false」。
    //   也就是说，只有模组目录里已经有 PublishedFileId.txt（曾发布过）时，
    //   Nil 才会被判成「作者可能不是你」；从没发布过的模组 PublishedFileId 是 Invalid，
    //   那句直接返回 false —— 对它们来说上传项本来就在。
    //
    // 所以下面**必须**把这个字段也填上（见 SafeGetWorkshopItemHook），
    // 否则本修复虽然挡住了崩溃，却顺手弄丢了「上传到创意工坊」。
    //（填值只在 Source == ContentSource.ModsFolder 时执行；对其它来源，
    //  CanToUploadToWorkshop 在上面第二道检查就已经返回 false，填不填都一样。）
    //
    // 它读的是缓存字段、不是去问 Steam，所以填值本身没有其它副作用。
    //
    // 而其它所有对外属性（名称、描述、标签、目录、预览图、版本）
    // 都是转发给 owner 字段去查的 —— 我们在下面手动把 owner 填好了，
    // 所以它们一切照常。
    //
    // 【千万不要删 CrossPromotion.dll】
    //
    // VisualExceptions.HarmonyMain 和 AchtungMod.AchtungLoader 都以
    // 「类型」的方式引用了 Brrainz.CrossPromotion。
    // 一旦把 dll 删掉，这两个模组会立刻抛 TypeLoadException（找不到类型）。
    //
    // 【一个值得知道的取舍】
    //
    // 这两道保险是无条件安装的。也就是说：即使玩家以后把 brrainz 的三个模组
    // 全部卸载了（那样这场崩溃本来就不可能发生），第二道保险依然在生效，
    // 代价就是上面说的「作者信息不显示」。
    //
    // 这是有意的：Steam 随时可能把 CrossPromotion 推回来，
    // 而「少显示一行作者」远比「整个游戏进程被杀掉」可接受得多。
    internal static class SteamWorkshopHookFix
    {
        private const string DetailsQueryName = "SendSteamDetailsQuery";
        private const string GetHookName = "GetWorkshopItemHook";
        // Log.ErrorOnce 的「去重键」：同一个键在整个进程里只会输出一次。
        // 两条错误消息必须各用各的键 —— 共用一个键的话，后出现的那条会被静默丢掉，
        // 而它往往正是最需要看到的一条（真实异常堆栈）。
        private const int ErrorKeyFieldsNotFound = 2030417;

        /// <summary>
        /// 「WorkshopItemHook 的字段找不到」专用键。
        ///
        /// 为什么必须与上面那个分开（2026-10-05 复审查出）：本文件上方 :90-92 的注释
        /// 自己写下了规则 ——「两条错误消息必须各用各的键，共用一个键的话，
        /// 后出现的那条会被静默丢掉，而它往往正是最需要看到的一条」——
        /// 但代码里两处传的却是同一个键，等于亲手把那条规则推翻了。
        ///
        /// 触发场景很现实：SendSteamDetailsQuery 先改名（第一条打印），
        /// 之后 steamAuthor / workshopHookInt 也改名（第二条被吞）——
        /// 玩家只会看到较轻的那条「已回退到 GetWorkshopItemHook 兜底」，
        /// 而真正致命的「已回退到会崩溃的原版路径」永远不会出现在日志里，
        /// 排查的人会朝着完全错误的方向找。
        /// </summary>
        private const int ErrorKeyHookFieldsMissing = 2030419;
        private const int ErrorKeyHookFailed = 2030418;

        private static bool installed;

        private static FieldInfo cacheField;
        private static FieldInfo ownerField;

        /// <summary>
        /// WorkshopItemHook 里那个「作者 SteamID」字段。
        /// 为什么要管它：游戏判断模组能不能上传创意工坊时，最后一道检查是
        /// MayHaveAuthorNotCurrentUser，而它读的正是这个字段。空壳对象里
        /// 这个字段是默认值（CSteamID.Nil），会被判定成「作者可能不是你」，
        /// 于是上传/更新那一项直接从「高级…」菜单里消失，且没有任何提示。
        /// </summary>
        private static FieldInfo authorField;

        /// <summary>
        /// 是否已经查过 steamAuthor 这个字段。
        ///
        /// 为什么不能只用 authorField != null 来判断（2026-10-04 审计查出的性能坑）：
        /// 万一原版把这个字段改了名，authorField 会**永远是 null**，于是下面那段
        /// 「没有就查一次」变成「每次调用都查一次」。而 GetWorkshopItemHook 在模组页面里
        /// 是**每帧 × 每个模组**被调用的（本机 1146 个模组），那就是每帧上千次反射元数据查找。
        /// 用一个独立的标志把「查过」和「查到了」分开记。
        /// </summary>
        private static bool authorFieldResolved;

        /// <summary>
        /// 是否已经查过 workshopHookInt / owner 这两个字段。
        ///
        /// 理由与 authorFieldResolved 一模一样（见上面那段）：
        /// 这两个字段原先是用「== null 就当没查过」判断的，
        /// 原版一旦给它们改名，就会退化成每次调用都重查一遍反射。
        /// </summary>
        private static bool hookFieldsResolved;

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            // 注意：下面这两步反射查找也要包在 try 里。
            //
            // 理由是 AccessTools.Method 在「同名方法有重载、无法唯一确定」时：
            // 它先按名字全量取，遇到歧义会改取**无参重载**；只有连无参重载也取不到，
            // 才会把 AmbiguousMatchException 抛出来。而本方法是被 LocalFixesMod 的构造函数
            // 挨个调用的：这里一旦把异常抛出去，排在后面的
            // CrossPromotionUnpatch.Install() 就根本不会执行 ——
            // 那正是最要命的「打开模组页面即崩溃」的修复，会被连累着跳过。
            try
            {
                MethodInfo details = AccessTools.Method(typeof(WorkshopItemHook), DetailsQueryName);
                MethodInfo getHook = AccessTools.Method(typeof(ModMetaData), GetHookName);

                // 两道保险**各自独立安装**，不绑成一个条件。
                //
                // 为什么（2026-10-04 审计查出的 P0 级隐患）：
                // 原来写的是 `if (details == null || getHook == null) return;` ——
                // 只要任一方改名消失，**两道都不装**。而这两道的分量完全不同：
                //   · SkipDetailsQuery 挡的是「构造 WorkshopItemHook 时去调 Valve 已废弃的
                //     UGC 接口」，属于从源头绕开；
                //   · SafeGetWorkshopItemHook 是兜底：万一那个构造函数真被调到了，
                //     也把结果换成一份安全的空壳。
                // 一旦 RimWorld 小版本更新改掉了 SendSteamDetailsQuery 的名字，
                // 原写法会把最要命的第二道也一起放弃 —— 玩家打开模组页面照样崩。
                // 所以拆开：谁还在就装谁，能装一道是一道。
                Harmony harmony = LocalFixesMod.HarmonyInstance;

                if (details != null)
                {
                    harmony.Patch(details,
                        new HarmonyMethod(AccessTools.Method(typeof(SteamWorkshopHookFix), nameof(SkipDetailsQuery))),
                        null, null, null);
                }
                else
                {
                    Log.ErrorOnce("[GNH LocalFixes] " + DetailsQueryName
                        + " not found; falling back to the GetWorkshopItemHook bypass only.",
                        ErrorKeyFieldsNotFound);
                }

                if (getHook != null)
                {
                    harmony.Patch(getHook,
                        new HarmonyMethod(AccessTools.Method(typeof(SteamWorkshopHookFix), nameof(SafeGetWorkshopItemHook))),
                        null, null, null);
                }
                else
                {
                    // 这一道才是真·关键：它没了，模组页面就是原来的崩溃行为。
                    Log.Error("[GNH LocalFixes] CRITICAL: ModMetaData." + GetHookName
                        + " not found; the mods-page crash guard is NOT installed.");
                }

                // 装上任意一道就算这一趟成功了 —— 免得每次构造模组对象都白重试一遍。
                installed = details != null || getHook != null;
                if (!installed)
                {
                    // 两道都没找到（多半是原版大改）：不置位，留着下次再试。
                    return;
                }

                // 装完之后再把补丁表读出来，确认前缀数量确实写进去了。
                // 这不是因为「Harmony 会悄悄跳过而不报错」—— 装不上它会正常抛错；
                // 读补丁表是为了留一份可查的证据：万一将来 Harmony 换了实现、
                // 或者目标方法被整段替换，翻日志就能立刻看出补丁到底有没有落上去。
                Patches info1 = (details != null) ? Harmony.GetPatchInfo(details) : null;
                Patches info2 = (getHook != null) ? Harmony.GetPatchInfo(getHook) : null;
                Log.Message("[GNH LocalFixes] Steam workshop hook fix installed. prefixes: "
                    + DetailsQueryName + "=" + (info1 != null ? info1.Prefixes.Count : -1)
                    + ", ModMetaData." + GetHookName + "=" + (info2 != null ? info2.Prefixes.Count : -1) + ".");
            }
            catch (Exception ex)
            {
                // 这里故意不把 installed 置为 true。
                // 万一这次因为环境问题失败了，下次模组对象被重新创建时还会再试一遍，
                // 不会因为这个标记把自己永久地、悄无声息地关掉。
                Log.Error("[GNH LocalFixes] Steam workshop hook fix failed (will retry): " + ex);
            }
        }

        // 返回 false 的意思是告诉 Harmony：「别执行原来那段代码了。」
        // 这样那个已经废弃的 SteamUGC.RequestUGCDetails 就永远不会被调用到。
        private static bool SkipDetailsQuery()
        {
            return false;
        }

        // 这个方法把 ModMetaData.GetWorkshopItemHook() 整个顶替掉了。
        //
        // 游戏原本的写法是：
        //     如果还没建过，就 new 一个 WorkshopItemHook；
        //     然后把它返回。
        //
        // 问题就出在那个 new 上面 —— 它一执行就会崩。
        //
        // 所以我们的做法是：绕开构造函数，直接「造一个空壳对象」出来，
        // 然后手动把里面的 owner 字段填好。
        //
        // 为什么填 owner 就够了：这个类对外露出的每一个属性
        // （名称、描述、标签、目录、预览图路径等等）都只是把请求转发给 owner，
        // 所以只要 owner 是对的，对象用起来跟正常的一样。
        //
        // 另外我们仍然使用游戏原本的那个缓存字段，所以「只建一次、之后复用」
        // 这个原有的行为也保留了下来。
        /// <summary>「读当前 Steam 用户 ID 失败」的日志去重键。</summary>
        private const int ErrorKeySteamIdFailed = 2030420;

        /// <summary>是否已经问过 Steamworks「当前登录的是谁」。与「问到了没有」分开记，理由同 hookFieldsResolved。</summary>
        private static bool steamIdResolved;

        /// <summary>缓存下来的当前 Steam 用户 ID。一局之内不会变，问一次就够。</summary>
        private static object cachedSteamId;

        /// <summary>
        /// 问 Steamworks「当前登录的是谁」，结果缓存起来。
        ///
        /// 为什么要缓存（2026-10-05 复审建议）：本方法处在「每个模组首次建 hook」的路径上，
        /// 而 AccessTools.TypeByName 在 Type.GetType 失败之后会**遍历全部已加载程序集**，
        /// 最后还要 AllTypes().ToArray()（一次大数组分配 + LINQ）。
        /// 按「玩家浏览过的每个模组各一次」算，本机可以到上千次 —— 纯属浪费。
        /// SteamID 一局内不会变，缓存一次就够。
        ///
        /// 用「查过」和「查到了」两个字段分开记：作者在过去几轮里已经踩过两次
        /// 「拿结果是不是 null 当查过没查过」的坑，这里不再重复。
        /// </summary>
        private static object ResolveLocalSteamId()
        {
            if (!steamIdResolved)
            {
                steamIdResolved = true;
                try
                {
                    Type steamUserType = AccessTools.TypeByName("Steamworks.SteamUser");
                    MethodInfo getSteamId = (steamUserType != null)
                        ? AccessTools.Method(steamUserType, "GetSteamID")
                        : null;
                    if (getSteamId != null)
                    {
                        cachedSteamId = getSteamId.Invoke(null, null);
                    }
                }
                catch (Exception ex)
                {
                    // 只报第一次：本方法会被每个模组各调用一次，
                    // 普通 Warning 会按模组数量刷屏。
                    Log.WarningOnce("[GNH LocalFixes] Could not read the local Steam user id; "
                        + "the workshop upload menu item may stay hidden: " + ex.Message,
                        ErrorKeySteamIdFailed);
                }
            }
            return cachedSteamId;
        }

        private static bool SafeGetWorkshopItemHook(ModMetaData __instance, ref WorkshopItemHook __result)
        {
            try
            {
                // 「查过一次就不再查」—— 与 authorField 那边同样的道理：
                // 拿字段本身是不是 null 当「查过」的标志，会让「原版改了字段名」
                // 这种情形退化成**每次调用都重查一遍反射**。
                // 而 AccessTools.Field 内部是 FindIncludingBaseTypes(...GetField...)，
                // 毫无缓存（已用反编译核实）—— 这正是本文件 :113-118 自己指出过的坑，
                // 只是先前只修了 authorField，把这两个漏了。
                if (!hookFieldsResolved)
                {
                    hookFieldsResolved = true;
                    cacheField = AccessTools.Field(typeof(ModMetaData), "workshopHookInt");
                    ownerField = AccessTools.Field(typeof(WorkshopItemHook), "owner");
                }

                if (cacheField == null || ownerField == null)
                {
                    // 找不到字段，说明将来游戏版本把内部结构改了。
                    // 这时就放弃接管，老老实实走游戏原本的路径。
                    // 注意这里用**独立的键**：这条比上面那条严重得多
                    //（回退到原版路径 = 回到会崩的状态），不能被上面的键吞掉。
                    Log.ErrorOnce("[GNH LocalFixes] Workshop hook fields not found; falling back to the "
                        + "vanilla path, which may crash the mods page: ",
                        ErrorKeyHookFieldsMissing);
                    return true;
                }

                WorkshopItemHook hook = (WorkshopItemHook)cacheField.GetValue(__instance);
                if (hook == null)
                {
                    hook = (WorkshopItemHook)FormatterServices.GetUninitializedObject(typeof(WorkshopItemHook));
                    ownerField.SetValue(hook, __instance);

                    // 光填 owner 是不够的 —— 这里必须再补一个「作者」字段，
                    // 否则「上传到创意工坊 / 在创意工坊更新」那一项会凭空消失。
                    //
                    // 原因：游戏判断一个模组能不能上传时（ModMetaData.CanToUploadToWorkshop）
                    // 最后会问 GetWorkshopItemHook().MayHaveAuthorNotCurrentUser，而它读的是
                    // WorkshopItemHook 自己的 steamAuthor 字段 —— 这个字段并不从 owner 转发，
                    // 空壳对象里是默认值 CSteamID.Nil，于是那句话会判定成
                    // 「作者可能不是你」，直接禁止上传；表现就是「高级…」菜单里没有上传项，
                    // 而且不报任何错，极难查。
                    //
                    // 前提：只有模组目录里已经有 PublishedFileId.txt（曾发布过）时，
                    // Nil 才会被判成「作者可能不是你」；从没发布过的模组 PublishedFileId 是
                    // Invalid，MayHaveAuthorNotCurrentUser 第一行就返回 false，上传项本来就在。
                    //
                    // 这里把它填成当前登录的 Steam 用户，等于告诉游戏「这个条目是我的」。
                    //
                    // 前提是**假定**这些模组由本人发布 —— 本机 Mods 目录里的模组绝大多数
                    // 是自己做的，但也可能是从别处手工拷进来、还带着原作者
                    // PublishedFileId.txt 的。对后者，这道填值会让「上传到创意工坊」
                    // 重新出现并指向别人的条目。真要点下去，Steam 那边会因为
                    // 「你不是创建者」而拒绝，所以更可能是报个错、而不是覆盖别人的作品；
                    // 但游戏原本是用这道检查来防这个的，这里相当于把它让开了。
                    //
                    // 取舍：不放开的后果是**自己的模组也传不上去**（这正是本次要修的问题），
                    // 两害相权，选择放开，并把这件事写进 About.xml 让玩家知道。
                    // 只对「本机 Mods 文件夹里的模组」这么做；工坊订阅来的模组
                    // （Source 是 SteamWorkshop）保持原样，不会被误判。
                    if (__instance.Source == ContentSource.ModsFolder)
                    {
                        // 「查过一次就不再查」——注意「标志」和「字段」是两回事：
                        // authorField 为 null 只说明「没查到」，不代表「没查过」。
                        // 拿字段本身当标志，会让「原版给这个字段改了名」这种情形
                        // 退化成每次调用都重查一遍反射。
                        if (!authorFieldResolved)
                        {
                            authorFieldResolved = true;
                            authorField = AccessTools.Field(typeof(WorkshopItemHook), "steamAuthor");
                        }
                        if (authorField != null)
                        {
                            // 用反射去问 Steamworks「当前登录的是谁」，而不是直接调
                            // SteamUser.GetSteamID()。原因是那样得给本工程再加一个
                            // com.rlabrecque.steamworks.net 程序集引用 —— 为了一个调用
                            // 多担一份「缺库就整个补丁加载失败」的风险并不划算。
                            //
                            // 反射拿不到就什么都不填（保持 Nil）：后果只是回到「不能上传」
                            // 的老样子，绝不会因此让补丁本身出问题。
                            object steamId = ResolveLocalSteamId();
                            // 这一步也必须包在自己的 try 里：万一将来 Steamworks 或游戏
                            // 把字段类型改了，这里会抛异常 —— 而外层那个 catch 是
                            // 「交还给游戏原方法」（return true），那等于把玩家直接送回
                            // SendSteamDetailsQuery 那条崩溃路径上。
                            try
                            {
                                authorField.SetValue(hook, steamId);
                            }
                            catch (Exception ex3)
                            {
                                Log.Warning("[GNH LocalFixes] Could not set steamAuthor on the workshop hook: "
                                    + ex3.Message);
                            }
                        }
                    }

                    cacheField.SetValue(__instance, hook);
                }

                __result = hook;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[GNH LocalFixes] SafeGetWorkshopItemHook failed: " + ex, ErrorKeyHookFailed);
                // 出异常就交还给游戏原本的实现（返回 true = 继续执行原方法），
                // 而不是塞一个 null 出去 —— 调用方拿到 null 很可能会直接空引用崩溃。
                return true;
            }

            return false;
        }
    }
}
