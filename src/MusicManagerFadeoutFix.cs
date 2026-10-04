using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 修复：游戏每帧抛 System.InvalidCastException（音乐淡出相关）
    // ==========================================================================
    //
    // 【本文件里有两个补丁，一前一后配合】
    //
    //   ① Patch_ListerThings_Add_RejectMislabeledInstrument —— 主力，源头拦截
    //      在游戏「把物品登记进分组」的那一刻就检查：凡是「def 说是乐器、
    //      实际不是乐器」的对象，一律不许进「乐器」分组。
    //      于是正常情况下**根本没有东西能混进去**，也就永远不会崩 ——
    //      连第一条红字都不会出现。它不新增任何会被写进存档的东西，
    //      所以将来卸载本模组也不会在存档里留下脏数据。
    //
    //   ② MusicManagerFadeoutFix.Finalizer —— 兜底，崩了才处理
    //      万一有哪条路径绕过了 ①（比如别的模组直接往名单里塞东西），
    //      它会在异常往外抛之前把脏对象摘掉，并吞掉这一条异常，避免每帧刷屏。
    //
    // 正常情况下你只会看到 ① 的日志。如果看到 ② 的日志，
    // 说明还有别的路径在制造同类脏数据，那是需要留意的信号。
    //
    // --------------------------------------------------------------------------
    // 【看到的症状】
    //
    // 游戏日志里刷出成百上千条：
    //
    //     Root level exception in Update(): System.InvalidCastException
    //       at RimWorld.MusicManagerPlay.UpdateMusicFadeout () [0x0008f]
    //
    // 2026-10-04 那次实测：一个多小时里刷了 109 条，几乎每一帧一条。
    // 每条异常都要构造、记录、写日志，本身就是纯粹的浪费，会让游戏变卡。
    //
    // --------------------------------------------------------------------------
    // 【出问题的那行游戏源码长什么样】
    //
    // 反编译 RimWorld 的 MusicManagerPlay.UpdateMusicFadeout，关键几行是：
    //
    //     List<Thing> list = currentMap.listerThings.ThingsInGroup(ThingRequestGroup.MusicalInstrument);
    //     for (int i = 0; i < list.Count; i++)
    //     {
    //         Building_MusicalInstrument b = (Building_MusicalInstrument)list[i];   // ← 崩在这一行
    //         if (b.IsBeingPlayed) { ...算音乐音量随距离淡出... }
    //     }
    //
    // 也就是说：游戏去「乐器」这个分类里取东西，取出来强行当成「乐器建筑」用。
    // 结果里面混进了一个**不是**乐器建筑的东西，这一转就炸了。
    //
    // 我们核对过游戏自己的 IL（中间代码）指令表，崩的那一条正好是：
    //     IL_008F: castclass RimWorld.Building_MusicalInstrument
    // 和日志里的 [0x0008f] 完全对上 —— 所以崩的就是上面这一行，没有别的可能。
    //
    // --------------------------------------------------------------------------
    // 【为什么「乐器」分类里会有不是乐器的东西？】
    //
    // 要回答这个，得看游戏是怎么决定「一个东西属不属于某个分类」的。
    // 分类在游戏里叫 ThingRequestGroup（物品查找分组），
    // 每个分组都有一句自己的判断规则，写在 Verse.ThingListGroupHelper.Includes 里。
    // 「乐器」这一组的规则是：
    //
    //     case ThingRequestGroup.MusicalInstrument:
    //         return typeof(Building_MusicalInstrument).IsAssignableFrom(def.thingClass);
    //
    // 翻译成人话：**只看这个物品的 def（数据定义）里写的 thingClass 是不是乐器类**。
    //
    // 注意关键点：这里查的是 def 上写的类，**不是这个物体本身真实的类**。
    //
    // 平时这两个当然是一致的 —— 因为游戏造东西时本来就是照着 def.thingClass 去 new 的。
    // 但只要出现下面这种情况，两者就会对不上：
    //
    //     * 模组更新过、或者存档是从老版本迁移来的，
    //       某个建筑物体身上挂的 def 指向了「乐器」，但它自己的真实类型却是普通的 Verse.Building
    //       （最常见的就是存档里记的类名和现在 def 里写的类名不一致）。
    //
    // 于是：判断规则说「你是乐器，进组」，可实际拿到手一强转就崩。
    // 这也解释了为什么**全盘搜索所有 XML 都搜不到**任何东西主动往这个组里加料 ——
    // 根本不是谁「加」进去的，是判断规则和真实类型对不上，被误判进去的。
    //
    // --------------------------------------------------------------------------
    // 【2026-10-04 实测到的真实案例（本补丁就是为它写的）】
    //
    // 存档里一共有 8 架「立式钢琴」（def 名 Joy_Piano，来自 Vanilla Furniture Expanded）：
    //
    //     7 架：<thing Class="Building_MusicalInstrument">   ← 正常
    //     1 架：<thing Class="Building">                     ← 元凶，id = Joy_Piano485569
    //
    // Vanilla Furniture Expanded 自带的 Patches\Royalty.xml 里有一条补丁，
    // 把 Joy_Piano 的 ParentName 改成 MusicalInstrumentBase
    //（目的：让钢琴改用皇权的音乐系统，贵族弹琴能触发音乐心情）。
    // 改完之后，这个 def 的 thingClass 就变成了 Building_MusicalInstrument。
    //
    // 但改 def 只影响**以后新造**的钢琴。那架**在改动之前就已经摆在地图上**的旧钢琴，
    // 它的 C# 对象在内存里早就是 Verse.Building 了 —— 对象的真实类型是改不了的。
    // 于是它就成了「def 说是乐器、对象却不是乐器」的那个倒霉蛋：
    // 被误判进乐器分组，然后每帧强转失败，把游戏拖到每秒约 2 帧。
    //
    // 实测坐标：**地图 0 的 (305, 228)**。
    // 想彻底了结它，就去那儿把那架钢琴拆掉、在原位重建一个 —— 新造的会是正确类型，
    // 也能正常使用（也就是拿到 VFE 原本想要的那个皇权音乐功能）。
    // 当然，放着不管也完全没问题：本补丁已经把它从分组里摘出去了。
    //
    // --------------------------------------------------------------------------
    // 【这个补丁怎么修】
    //
    // 给 UpdateMusicFadeout 挂一个 Harmony 的 Finalizer（收尾器）。
    // Finalizer 的意思是：「这个方法要是抛异常了，在异常往外传之前，先让我插一手」。
    //
    // 我们的收尾器做三件事：
    //
    //   1. 只有异常是 InvalidCastException（强转失败）或 NullReferenceException（空引用）
    //      才处理 —— 别的异常原样往上抛，绝不乱吞。
    //
    //   2. 把「乐器」分组里所有**真实类型不是乐器**的对象，从分组名单里摘出去。
    //      顺手也检查一下「音乐源」分组（规则是「必须有 CompPlaysMusic 组件」），
    //      避免同类问题在隔壁那半段代码里再炸一次。
    //
    //      因为游戏取分组名单时返回的就是内部那个 List 本身（不是副本），
    //      所以这里直接从名单里删掉，一帧之后就永久干净了 —— 不必改存档、不必重开游戏。
    //
    //   3. 把这次摘掉了什么、为什么摘，写进日志（只写前几次，之后不刷屏）。
    //      这条很重要：绝不能「静默吞掉异常」，否则问题被藏起来、以后没人查得出来。
    //
    // --------------------------------------------------------------------------
    // 【为什么这样修是安全的】
    //
    // * **只动分组名单，不动物体本身。**
    //   那个建筑照样在图上的原位、照样能点、能拆、能派小人用，
    //   只是不再参与「按距离把音乐音量调小」这一个计算 —— 它本来也不该参与，
    //   因为它压根不是乐器。所以没有任何东西会因此丢失或损坏。
    //
    // * **不改 def，不碰存档。** 摘名单是纯运行期的内存操作，存档里什么都不变；
    //   下次读档时如果问题还在，本补丁会再摘一遍（幂等，重复执行没有副作用）。
    //
    // * **只在已经崩了之后才动手。** 判断规则只做只读检查，不改变游戏行为；
    //   没崩的时候本补丁完全不存在感。
    //
    // * **即使判断错了也只会更稳。** 万一「乐器组里有非乐器」这个判断本身是错的，
    //   那么分组名单是干净的、我们一个元素也不会摘，只会吞掉那一条已经发生的异常，
    //   并打一条 Error 说明「没能定位到原因」—— 不会误删任何东西。
    //
    // --------------------------------------------------------------------------
    // 【和「本地补丁合集」其它补丁的关系】
    //
    // 完全独立：只依赖游戏自带的 MusicManagerPlay 和 ListerThings。
    // 不管用户装没装别的模组，这个补丁都能装、都能生效，不做任何硬依赖。
    // ==========================================================================
    public static class MusicManagerFadeoutFix
    {
        // Harmony 要求每个补丁有唯一 id；这里用「本模组 id + 本补丁名」拼出来。
        private const string HarmonyId = "gnh.cn.cys.localfixes.musicfadeout";

        // 防重复安装。
        //
        // 为什么需要它：本模组的构造函数在一个进程里可能被调用多次
        //（开发者工具热重载、切换语言都会重新来一遍），
        // 而 Harmony 装补丁是「叠加」不是「替换」—— 装两遍就会有两层一样的代码。
        // 所以装之前先看标记，装完立刻立标记。
        private static bool installAttempted;

        // 详细报告只写前几次，避免万一有模组在不停制造新脏对象时把日志刷爆。
        private const int MaxDetailedReports = 3;
        private static int detailedReportsWritten;

        // 累计一共摘掉了多少个脏对象，只用于日志统计。
        private static int totalRemoved;

        // 找不到原因时用的日志去重 key（Log.ErrorOnce 需要它）。
        private const int NotLocatedLogKey = 0x4D4D4644; // "MMFD"

        // 每次清理时，最多逐条列出多少个对象（其余只计数）。防止极端情况下日志被撑爆。
        private const int MaxDescribedObjects = 20;

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

            // 按「类型 + 方法名」找目标方法。
            // UpdateMusicFadeout 是 private（私有）的，AccessTools 默认就能找到私有方法。
            MethodInfo target = AccessTools.Method(typeof(MusicManagerPlay), "UpdateMusicFadeout");
            if (target == null)
            {
                // 找不到说明游戏版本变了、这个方法被改名或删掉了。
                // 这不是致命问题：本补丁不装，别的补丁照常工作。
                Log.Error("[GNH LocalFixes] MusicManagerFadeoutFix：在本游戏版本里找不到 "
                    + "RimWorld.MusicManagerPlay.UpdateMusicFadeout，本修复未安装（其余修复不受影响）。");
                return;
            }

            MethodInfo finalizer = AccessTools.Method(typeof(MusicManagerFadeoutFix), nameof(Finalizer));
            if (finalizer == null)
            {
                Log.Error("[GNH LocalFixes] MusicManagerFadeoutFix：找不到自己的 Finalizer 方法，本修复未安装。");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));

            Log.Message("[GNH LocalFixes] 已安装音乐淡出保护补丁（MusicManagerPlay.UpdateMusicFadeout）。"
                + "它能拦下「非乐器混进乐器分组」导致的每帧 InvalidCastException，并自动把脏数据摘掉。");
        }

        // ----------------------------------------------------------------------
        // 收尾器：本方法会在 UpdateMusicFadeout 抛异常时被调用。
        //
        // 参数 __exception 是 Harmony 替我们装好的「刚抛出来的那个异常」。
        // 返回值规定：返回什么，游戏就往外抛什么；返回 null 表示「这个异常我处理掉了，别抛了」。
        // ----------------------------------------------------------------------
        private static Exception Finalizer(Exception __exception)
        {
            // 没异常就等于正常走完，什么都不用做。
            if (__exception == null)
            {
                return null;
            }

            bool isInvalidCast = __exception is InvalidCastException;
            bool isNullRef = __exception is NullReferenceException;

            // 只处理我们研究过的这两种。
            // 其它异常（例如游戏自己版本变了）原样抛出去，绝不当「万能消音器」——
            // 那种做法会把真正的问题一起藏起来。
            if (!isInvalidCast && !isNullRef)
            {
                return __exception;
            }

            int removed = 0;
            string detail = null;

            try
            {
                detail = Repair(Find.CurrentMap, out removed);
            }
            catch (Exception ex)
            {
                // 连清理自己都出错了：记下来，但绝不因此再抛一个新异常出去。
                Log.Error("[GNH LocalFixes] MusicManagerFadeoutFix：清理分组时自身出错"
                    + "（已忽略，不影响游戏继续运行）：" + ex);
            }

            if (removed > 0)
            {
                totalRemoved += removed;

                // 前几次写详细报告（哪个对象、什么类型、来源哪个模组），之后只写一行汇总。
                if (detailedReportsWritten < MaxDetailedReports)
                {
                    detailedReportsWritten++;
                    Log.Warning("[GNH LocalFixes] 已拦下一次「每帧崩溃」：" + __exception.GetType().Name
                        + "（来自 MusicManagerPlay.UpdateMusicFadeout）。"
                        + "本次从分组名单里摘掉了 " + removed + " 个不该在那儿的对象。" + detail
                        + "\n  说明：这些对象之所以被误判进「乐器」分组，是因为它们身上挂的 def 写着 "
                        + "thingClass 是乐器类，但它们自己的真实类型并不是乐器。"
                        + "\n  本次处理只把它们从分组名单里摘出去，**没有动对象本身** —— "
                        + "建筑照旧在原位、可点可用，只是不再参与音乐远近淡出计算。");
                }
                else
                {
                    Log.Warning("[GNH LocalFixes] 又摘掉 " + removed
                        + " 个误入分组的对象（累计 " + totalRemoved + " 个）。详细报告已不再重复打印。");
                }

                // 问题已经处理掉了，这一条异常不必再抛给游戏（抛了也只是刷日志）。
                return null;
            }

            // 走到这里说明：异常确实发生了，但我们没在分组里找到任何可疑对象。
            // 也就是说根因不在这里（或者表现在别处）。这时**只吞掉这一条**并留下记录，
            // 让日志里能看出「有这么个情况、但没定位到」，而不是什么都不说。
            Log.ErrorOnce("[GNH LocalFixes] 捕获到 " + __exception.GetType().Name
                + "（来自 MusicManagerPlay.UpdateMusicFadeout），但「乐器」与「音乐源」两个分组里"
                + "都没有发现真实类型不符的对象，因此没有改动任何游戏数据，只吞掉了这一条异常。"
                + "如果你愿意帮忙排查，请把下面这段原始异常发给作者："
                + "\n" + __exception, NotLocatedLogKey);
            return null;
        }

        // ----------------------------------------------------------------------
        // 真正的清理动作。返回一段给人看的说明文字，通过 out 参数返回摘掉的数量。
        //
        // 参数 map 由调用方给：崩溃收尾器传的是「当前正在显示的地图」，
        // 而进图主动清理的那个组件传的是「它自己所属的地图」。
        //
        // 声明成 internal 是为了让同程序集的 MusicGroupSanitizer 也能调用它。
        // ----------------------------------------------------------------------
        internal static string Repair(Map map, out int removed)
        {
            removed = 0;

            if (map == null)
            {
                return "\n  （当前没有正在显示的地图，本次跳过。）";
            }

            ListerThings lister = map.listerThings;
            if (lister == null)
            {
                return "\n  （当前地图没有物品索引，本次跳过。）";
            }

            StringBuilder sb = new StringBuilder();
            int removedFromInstruments = 0;
            int removedFromSources = 0;

            // 逐个对象的说明最多只写 MaxDescribedObjects 条。
            // 万一某个模组批量制造出几百上千个这种脏对象，日志绝不能被撑爆 ——
            // 写几十万行日志本身就是另一种形式的卡顿，比原来的 bug 还糟。
            int described = 0;

            // ---- ① 「乐器」分组：凡是真实类型不是 Building_MusicalInstrument 的，一律摘掉 ----
            //
            // 注意：游戏返回的就是它内部那张名单本身（不是拷贝），
            // 所以下面这句 RemoveAt 会真正改动游戏的名单，下一帧就不会再拿到这个脏对象了。
            List<Thing> instruments = lister.ThingsInGroup(ThingRequestGroup.MusicalInstrument);
            if (instruments != null && instruments.Count > 0)
            {
                // 从后往前删：删掉后面的元素不会影响前面还没看过的那些下标。
                for (int i = instruments.Count - 1; i >= 0; i--)
                {
                    Thing t = instruments[i];

                    // is 判断比强转安全：不符合只会得到 false，绝不会抛异常。
                    if (t is Building_MusicalInstrument)
                    {
                        continue;
                    }

                    AppendDescription(sb, t, i, "乐器分组", ref described);
                    instruments.RemoveAt(i);
                    removed++;
                    removedFromInstruments++;
                }
            }

            // ---- ② 「音乐源」分组 ----
            //
            // 这一组的规则是「必须有 CompPlaysMusic 组件」。游戏那半段代码是：
            //     CompPlaysMusic c = thing.TryGetComp<CompPlaysMusic>();
            //     if (c.Playing)   // ← 万一 c 是 null，这里就是 NullReferenceException
            // 所以这里顺手把「拿不到该组件」的对象也摘掉，防止它那边也每帧崩。
            List<Thing> sources = lister.ThingsInGroup(ThingRequestGroup.MusicSource);
            if (sources != null && sources.Count > 0)
            {
                for (int i = sources.Count - 1; i >= 0; i--)
                {
                    Thing t = sources[i];

                    // null 和「没有该组件」都要摘：前者会直接崩，后者会崩在 c.Playing 那一行。
                    if (t != null && t.TryGetComp<CompPlaysMusic>() != null)
                    {
                        continue;
                    }

                    AppendDescription(sb, t, i, "音乐源分组", ref described);
                    sources.RemoveAt(i);
                    removed++;
                    removedFromSources++;
                }
            }

            if (removed == 0)
            {
                return "\n  （检查过两个分组，都是干净的，没有摘掉任何东西。）";
            }

            return "\n  分组明细：乐器分组摘掉 " + removedFromInstruments + " 个，"
                 + "音乐源分组摘掉 " + removedFromSources + " 个。" + sb;
        }

        // ----------------------------------------------------------------------
        // 往说明文字里追加一条对象描述，但最多只追加 MaxDescribedObjects 条。
        // 抽成一个方法是为了让「乐器」和「音乐源」两个分支共用同一套
        // 「限流 + 省略提示」逻辑，不必写两遍。
        // ----------------------------------------------------------------------
        private static void AppendDescription(StringBuilder sb, Thing t, int index, string groupName, ref int described)
        {
            if (described == MaxDescribedObjects)
            {
                sb.Append("\n    - （同类对象还有很多，为避免把日志撑爆，从这里开始不再逐个列出。）");
                described++;
                return;
            }
            if (described > MaxDescribedObjects)
            {
                return;
            }

            sb.Append(Describe(t, index, groupName));
            described++;
        }

        // ----------------------------------------------------------------------
        // 把一个对象「是什么来头」描述成一行中文，写进日志帮人定位到底是哪个模组干的。
        //
        // 两个地方共用它：
        //   * Repair()：从分组名单里摘掉脏对象时（index 是它在名单里的位置）；
        //   * Patch_ListerThings_Add...：在登记阶段就拦下脏对象时
        //     （那种情况它压根没进名单，所以传 index = -1，表示不打印下标）。
        // ----------------------------------------------------------------------
        internal static string Describe(Thing t, int index, string groupName)
        {
            // index 小于 0 表示「它并不在名单里」，这种情况就不打印下标了，免得误导读者。
            string prefix = (index >= 0) ? ("名单下标 " + index + "：") : "";

            if (t == null)
            {
                return "\n    - [" + groupName + "] " + prefix + "是个 null（空引用，访问它就会崩）";
            }

            // t.def 是「数据定义」，t.GetType() 是「这个物体真实的 C# 类」。
            // 这两者不一致，正是本次崩溃的根源，所以两个都要打出来。
            ThingDef def = t.def;
            string realType = t.GetType().FullName;
            string defName = (def != null) ? def.defName : "(该对象没有 def)";
            string defThingClass = (def != null && def.thingClass != null) ? def.thingClass.FullName : "(无)";
            string sourcePack = (def != null && def.modContentPack != null) ? def.modContentPack.Name : "(来源未知)";
            string destroyed = t.Destroyed
                ? "是（已经不在图上了，属于改版后留下的残影）"
                : "否（还留在图上）";

            // 位置信息是给玩家用的：拿到坐标就能在游戏里找到它、拆掉它、重建一个正常的。
            // 只有「已经摆在地图上」的物体才有坐标，所以这里要判 Spawned。
            string where;
            if (t.Spawned && t.Map != null)
            {
                where = "，位置=" + t.Position + "（地图 " + t.Map.Index + "）";
            }
            else
            {
                where = "，位置=（不在图上，没有坐标）";
            }

            // ThingID 是这局游戏里给每个物体的唯一编号，配合开发者工具能精确定位到它。
            string id;
            try
            {
                id = "，ThingID=" + t.ThingID;
            }
            catch (Exception)
            {
                // 极少数对象可能没有可读的 ID，缺这一项不影响排查。
                id = "";
            }

            return "\n    - [" + groupName + "] " + prefix
                 + "真实类型=" + realType
                 + "，def=" + defName
                 + "，def 里写的 thingClass=" + defThingClass
                 + "，来源模组=" + sourcePack
                 + "，是否已销毁=" + destroyed
                 + where + id;
        }
    }

    // ==========================================================================
    // 从源头拦住：「def 说是乐器、实际不是乐器」的对象，一律不许进分组名单。
    // ==========================================================================
    //
    // 【为什么不能只靠上面的收尾器】
    //
    // 收尾器（Finalizer）是「崩了才处理」：它虽然能立刻把脏对象摘掉、让之后不再崩，
    // 但第一次那一下已经真实发生了 —— 玩家会看到一条红字，那一帧的音乐淡出也会失效。
    // 而本补丁要治的这个毛病（旧存档里有一架实例类型不对的钢琴）
    // 在读档那一刻就已经存在，完全可以赶在它第一次发作之前就处理掉。
    //
    // ----------------------------------------------------------------------
    // 【为什么不用 MapComponent —— 一个踩过的坑，务必保留这段记录】
    //
    // 最初的做法是写一个 Verse.MapComponent 子类，在「进入地图后的第一次 Tick」
    // 主动扫一遍分组。功能上没问题，但它有一个无法接受的副作用：
    //
    //     ★ MapComponent 会被**写进玩家的存档**。
    //
    // 证据是反编译 Verse.Map.ExposeComponents 看到的第一手代码：
    //
    //     Scribe_Collections.Look(ref components, "components", LookMode.Deep, this);
    //
    // 也就是说，存档里会多出一条 <li Class="GNH.LocalFixes.MusicGroupSanitizer" />。
    // 这绝不是「看不见的小事」：玩家哪天卸载了本模组，读档时游戏就找不到这个类，
    // 只能退化成抽象的 Verse.MapComponent，于是直接抛
    //     SaveableFromNode exception: Can't load abstract class Verse.MapComponent
    // —— **我们用来修 bug 的东西，自己变成了新的 bug 源**。
    //（这类脏数据在本用户的日志里已经出现过 3 条，来自别的模组；见 RI_Frame_WineBarrel 那一批。）
    //
    // 所以改成现在的方案：**不新增任何会被存档记录的东西**，
    // 而是直接接管游戏「把物品登记进分组」的那一步。
    //
    // ----------------------------------------------------------------------
    // 【游戏是怎么登记的】
    //
    // Verse.ListerThings.Add(Thing t) 内部是这样（反编译原文，简化后）：
    //
    //     ThingRequestGroup[] allGroups = ThingListGroupHelper.AllGroups;
    //     foreach (ThingRequestGroup group in allGroups)
    //         if (GroupIncludes(t, group))              // ← 判定用的是 t.def
    //             listsByGroup[(uint)group].Add(t);     // ← 加进名单
    //
    // 我们在它**加完之后**立刻检查一次：如果这是个「def 说是乐器、实际不是乐器」的对象，
    // 就把它从「乐器」名单里摘回来。于是它从来没有机会留在名单上，
    // MusicManagerPlay.UpdateMusicFadeout 自然也就永远不会崩。
    //
    // 这个做法还有一个额外好处：读档时每个物品都要走一遍 Add，
    // 所以存档里所有这类脏对象**在进组的瞬间**就被处理掉了，根本不必等它崩。
    //
    // ----------------------------------------------------------------------
    // 【性能 —— 这是本补丁唯一需要小心的地方】
    //
    // Add 位于热路径（每生成一个物品、读档时每读出一个物品都会调用），
    // 所以判断顺序是刻意排过的，绝大多数对象在第一行就被排除：
    //
    //   ① t 是不是 null                        —— 1 次比较
    //   ② t.def.category 是不是 Building        —— 1 次字段读 + 1 次枚举比较
    //      （植物、物品、小人、投射物……全都在这里返回，占实际调用量的绝大头）
    //   ③ t 是不是 Building_MusicalInstrument   —— 1 条 isinst 指令，正常的乐器在这里返回
    //   ④ 最后才做反射判断 def.thingClass        —— 只有「既是建筑、又不是乐器」的才会走到
    //
    // 也就是说，最坏情况也只是**重复一次游戏自己刚刚做过的同一个判断**
    //（游戏的 Includes 里本来就写着 IsAssignableFrom(def.thingClass)），
    // 而绝大多数调用只花两三次廉价判断就返回了。量级参考：本用户存档约 17 万个物体，
    // 全部走一遍也只是百万次量级的简单操作，对读档时间的影响可以忽略。
    //
    // 另外：本补丁只处理 Global（地图级）的登记表。
    // 区域级（Region）的登记表里根本不会存放「乐器」这一组，
    // 若在那里查询 ThingsInGroup 反而会让游戏自己报一条 Error，所以先按 use 过滤掉。
    // ==========================================================================
    [HarmonyPatch(typeof(ListerThings), nameof(ListerThings.Add))]
    internal static class Patch_ListerThings_Add_RejectMislabeledInstrument
    {
        // 把乐器基类的 Type 缓存下来，省掉每次调用都重新取一次的开销。
        private static readonly Type InstrumentThingClass = typeof(Building_MusicalInstrument);

        // 详细日志只写前几次：万一某个模组批量制造这种对象，也不能让日志被刷爆。
        private const int MaxDetailedLogs = 3;
        private static int detailedLogsWritten;
        // 累计拦下的「标签写错的乐器」数量（仅用于日志统计）。
        private static int badInstrumentCount;

        // 累计拦下的「挂着配方、但不是工作台」的数量（仅用于日志统计）。
        private static int badBillGiverCount;

        // 本补丁自己出错时的去重日志 key（Log.ErrorOnce 需要）。
        private const int PatchErrorLogKey = 0x4D4D4645; // "MMFE"

        private static void Postfix(ListerThings __instance, Thing t)
        {
            // 这是挂在热路径上的补丁：任何意外都必须被吞掉，
            // 绝不能让「修 bug 的东西」自己变成新的崩点。
            // 但「吞掉」不等于「不吭声」—— 出错会在下面记一条 ErrorOnce，日志里仍然查得到。
            try
            {
                // 只处理地图级的登记表。
                // 区域级的登记表里没有「乐器」组，在那里查 ThingsInGroup 会触发游戏自己的一条 Error。
                if (__instance == null || __instance.use != ListerThingsUse.Global)
                {
                    return;
                }

                // ① 最廉价的排除。不是建筑的东西，下面两个分组都不可能涉及。
                if (t == null)
                {
                    return;
                }
                ThingDef def = t.def;
                if (def == null || def.category != ThingCategory.Building)
                {
                    return;
                }

                // ② 乐器方向：def 说自己是乐器，对象本身却不是。
                //
                //    后果：MusicManagerPlay.UpdateMusicFadeout 每帧把它强转成
                //    Building_MusicalInstrument 并失败，每次异常都要抓堆栈并写日志
                //    （实测把游戏拖到每秒约 2 帧）。
                if (!(t is Building_MusicalInstrument)
                    && InstrumentThingClass.IsAssignableFrom(def.thingClass))
                {
                    RemoveFromGroup(__instance, ThingRequestGroup.MusicalInstrument, t);
                    ReportBadInstrument(t);
                    return;
                }

                // ③ 工作台方向：def 挂着配方（于是被算作「可能的工作台」），对象却不是 IBillGiver。
                //
                //    后果：原版 BillUtility.MapBillGivers 每次遍历这一组都会撞上它，
                //    打印「Found non-bill-giver tagged as PotentialBillGiver」。
                //    典型成因同样是「模组作者给这类建筑写了配方，后来模组的类没了、
                //    存档里的旧实例降级成了 Verse.Building」。
                //
                //    性能说明：def.AllRecipes 的结果是带缓存的，而且游戏自己在这一步之前
                //    （分组判定 Includes 里那句 !def.AllRecipes.NullOrEmpty()）就已经访问过它，
                //    所以这里读到的是现成缓存，不会触发重复计算。
                if (!(t is IBillGiver) && !def.AllRecipes.NullOrEmpty())
                {
                    RemoveFromGroup(__instance, ThingRequestGroup.PotentialBillGiver, t);
                    ReportBadBillGiver(t);
                }
            }
            catch (Exception ex)
            {
                // 用固定 key 的 ErrorOnce：无论触发多少次，日志里最多只会出现一条。
                Log.ErrorOnce("[GNH LocalFixes] Patch_ListerThings_Add_RejectMislabeledInstrument 出错"
                    + "（已忽略，不影响游戏；音乐淡出的收尾器仍会兜底）：" + ex, PatchErrorLogKey);
            }
        }

        // ----------------------------------------------------------------------
        // 把一个对象从指定的分组名单里摘掉。
        // 游戏返回的就是内部那张名单本身（不是拷贝），所以 Remove 会真正生效。
        // ----------------------------------------------------------------------
        private static void RemoveFromGroup(ListerThings lister, ThingRequestGroup group, Thing t)
        {
            List<Thing> list = lister.ThingsInGroup(group);
            if (list != null)
            {
                list.Remove(t);
            }
        }

        // ----------------------------------------------------------------------
        // 报告「标签写错的乐器」，前几次详细、之后只计数。
        // ----------------------------------------------------------------------
        private static void ReportBadInstrument(Thing t)
        {
            badInstrumentCount++;

            if (detailedLogsWritten >= MaxDetailedLogs)
            {
                Log.Warning("[GNH LocalFixes] 又拦下 1 个「标签写错」的乐器"
                    + "（累计 " + badInstrumentCount + " 个）。详细报告不再重复打印。");
                return;
            }

            detailedLogsWritten++;
            Log.Warning("[GNH LocalFixes] 拦下一个「标签写错」的乐器：它的 def 说自己是乐器，"
                + "但对象本身不是 —— 如果放它进「乐器」分组，游戏会每帧抛 InvalidCastException。"
                + MusicManagerFadeoutFix.Describe(t, -1, "乐器分组")
                + "\n  常见原因：某个模组或补丁把这个 def 改成了乐器类，"
                + "但存档里那个旧实例仍然是原来的普通建筑（对象的真实类型是改不掉的）。"
                + "\n  建议：按上面的坐标把它拆掉、在原地重新建一个，新建的会是正确类型。"
                + "\n  不做也完全没问题：本补丁会持续拦住它，游戏不会因此出错。");
        }

        // ----------------------------------------------------------------------
        // 报告「挂着配方却不是工作台」的对象。
        // 这类对象原本会让原版每遍历一次就打印一条红字（虽然 ErrorOnce 只出现一条，
        // 但它在日志里看起来像是个错误，且会让那条分组每次都被无谓扫描）。
        // ----------------------------------------------------------------------
        private static void ReportBadBillGiver(Thing t)
        {
            badBillGiverCount++;

            if (detailedLogsWritten >= MaxDetailedLogs)
            {
                Log.Warning("[GNH LocalFixes] 又拦下 1 个「挂着配方但不是工作台」的对象"
                    + "（累计 " + badBillGiverCount + " 个）。详细报告不再重复打印。");
                return;
            }

            detailedLogsWritten++;
            Log.Warning("[GNH LocalFixes] 拦下一个「挂着配方但不是工作台」的对象：它的 def 有配方，"
                + "所以会被算进「可能的工作台」分组，但它本身并不是 IBillGiver。"
                + MusicManagerFadeoutFix.Describe(t, -1, "可能的工作台分组")
                + "\n  这原本会让原版打印「Found non-bill-giver tagged as PotentialBillGiver」。"
                + "\n  常见原因：提供这个建筑类的模组已被卸载，存档里的旧实例降级成了普通建筑。"
                + "\n  建议：按上面的坐标把它拆掉即可；不拆也没关系，本补丁会一直拦住它。");
        }
    }
}
