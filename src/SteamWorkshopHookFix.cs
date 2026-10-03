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
    // 顶替之后，「Steam 信息」对象里那个「作者」字段会保持默认值（空的）。
    // 与它相关的判断只有 MayHaveAuthorNotCurrentUser 一处，而它读的是缓存字段、
    // 不是去问 Steam，所以这里也谈不上有什么副作用（该属性在整个游戏程序集里没有调用点）。
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

        internal static void Install()
        {
            if (installed)
            {
                return;
            }

            // 注意：下面这两步反射查找也要包在 try 里。
            //
            // 理由是 AccessTools.Method 在「同名方法有重载、无法唯一确定」时会抛
            // AmbiguousMatchException（它内部先按名字全量取，遇到歧义再试无参版本，
            // 仍旧歧义就把异常抛出来）。而本方法是被 LocalFixesMod 的构造函数
            // 挨个调用的：这里一旦把异常抛出去，排在后面的
            // CrossPromotionUnpatch.Install() 就根本不会执行 ——
            // 那正是最要命的「打开模组页面即崩溃」的修复，会被连累着跳过。
            try
            {
                MethodInfo details = AccessTools.Method(typeof(WorkshopItemHook), DetailsQueryName);
                MethodInfo getHook = AccessTools.Method(typeof(ModMetaData), GetHookName);

                if (details == null || getHook == null)
                {
                    Log.Error("[GNH LocalFixes] Steam workshop hook fix: target missing ("
                        + DetailsQueryName + "=" + (details != null)
                        + ", ModMetaData." + GetHookName + "=" + (getHook != null) + ").");
                    return;
                }

                Harmony harmony = LocalFixesMod.HarmonyInstance;

                harmony.Patch(details,
                    new HarmonyMethod(AccessTools.Method(typeof(SteamWorkshopHookFix), nameof(SkipDetailsQuery))),
                    null, null, null);

                harmony.Patch(getHook,
                    new HarmonyMethod(AccessTools.Method(typeof(SteamWorkshopHookFix), nameof(SafeGetWorkshopItemHook))),
                    null, null, null);

                installed = true;

                // 装完之后再把补丁表读出来看一眼，确认真的装上了。
                // 因为 Harmony 有时会「悄悄跳过」而不报错，光看日志说「已执行」不代表装成功。
                Patches info1 = Harmony.GetPatchInfo(details);
                Patches info2 = Harmony.GetPatchInfo(getHook);
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
        private static bool SafeGetWorkshopItemHook(ModMetaData __instance, ref WorkshopItemHook __result)
        {
            try
            {
                if (cacheField == null)
                {
                    cacheField = AccessTools.Field(typeof(ModMetaData), "workshopHookInt");
                    ownerField = AccessTools.Field(typeof(WorkshopItemHook), "owner");
                }

                if (cacheField == null || ownerField == null)
                {
                    // 找不到字段，说明将来游戏版本把内部结构改了。
                    // 这时就放弃接管，老老实实走游戏原本的路径。
                    Log.ErrorOnce("[GNH LocalFixes] Workshop hook fields not found; falling back to the vanilla path.", ErrorKeyFieldsNotFound);
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
                    // 这里把它填成当前登录的 Steam 用户：这些模组本来就放在本机 Mods 目录、
                    // 由本人发布，填自己才是事实。只对「本机 Mods 文件夹里的模组」这么做；
                    // 工坊订阅来的模组保持原样（Nil），免得把别人的作品误判成自己的。
                    if (__instance.Source == ContentSource.ModsFolder)
                    {
                        if (authorField == null)
                        {
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
                            object steamId = null;
                            try
                            {
                                Type steamUserType = AccessTools.TypeByName("Steamworks.SteamUser");
                                MethodInfo getSteamId = steamUserType != null
                                    ? AccessTools.Method(steamUserType, "GetSteamID")
                                    : null;
                                if (getSteamId != null)
                                {
                                    steamId = getSteamId.Invoke(null, null);
                                }
                            }
                            catch (Exception ex2)
                            {
                                Log.Warning("[GNH LocalFixes] Could not read the local Steam user id: " + ex2.Message);
                            }
                            if (steamId != null)
                            {
                                authorField.SetValue(hook, steamId);
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
