using System.Collections.Generic;
using Verse;

namespace GNH.LocalFixes
{
    // ==========================================================================
    // 本补丁包的模组设置。
    // ==========================================================================
    //
    // 游戏会把这里的字段存成
    //     <配置目录>\Config\ModSettings\GNH.LocalFixes.xml
    // 也就是说设置跟着玩家走，换存档依然有效。
    //
    // 【为什么有两个「老字段」和一张字典】
    //
    //   最早只有两个开关（领袖头衔、VFE 乐器空间），各自是一个 bool 字段，
    //   键名 keepLeaderTitle / fixVfeeInstrumentSpace 已经写进了所有老玩家的配置文件。
    //   **动它们的键名 = 老玩家的这两项设置被丢弃**，所以原样保留成独立字段。
    //
    //   2026-10-12 起开关数量涨到二十多项，再一项一个字段就得写二十多行 Scribe；
    //   于是新增项统一走下面这张 toggles 字典（键就是 FixToggles 里的 Id）。
    //   查不到记录时一律按**开启**处理 —— 老配置升级上来、以及将来新增的项，
    //   默认都是修着的。本补丁包的定位是「装上就别管」，
    //   默认关闭只会让玩家莫名其妙地继续崩，而且根本想不到是这里要手动开。
    //
    // ⚠ 改这个文件时注意：ExposeData 里**每一个** Look 的第三个参数
    //   （缺省值）都必须是 true。写错成 false，等于给所有老玩家悄悄关掉一项修复。
    public class GNHLocalFixesSettings : ModSettings
    {
        // 默认 true：默认就保护玩家自己设好的领袖头衔。
        // 想恢复原版行为（点「随机符号」时连头衔一起重掷），把它关掉即可。
        public bool keepLeaderTitle = true;

        // 默认 true：默认启用「VFE Empire 乐器空间」的空引用修复。
        //
        // 为什么单独给它一个开关（2026-10-11 复审后加）：
        //   本补丁包里其余各项大多是「先问原版、真需要才插手」（Prefix 里一堆
        //   `return true` 把控制权交还原版），而这一项是**替原版做判断**。
        //   虽然它做的判断与原版完全等价（原版逻辑 + 一层 null 检查），
        //   但万一 VFE Empire 日后**改了那句判断本身**（例如支持更多种类的
        //   跳舞地板 worker），本补丁就会盖掉作者的改动。
        //   留一个开关：真遇到那种情况可以直接关掉，不必等本补丁包更新。
        //
        // 注意：本项**多数情况下不需要手动关** —— 它自带一个探测，
        //       一旦发现原版那句判断已经不会崩（多半就是作者修好了），
        //       它会自己让路。详见 VFEEInstrumentSpaceFix.OriginalPredicateIsSafe()。
        public bool fixVfeeInstrumentSpace = true;

        // 其余全部开关：FixToggles.All 里的 Id → 是否启用。
        //
        // 用字典而不是二十多个字段，是为了让「加一个新开关」不必再改本文件 ——
        // 漏改一次 Scribe 的后果很隐蔽：设置界面里能点、玩起来也生效，
        // 但重启游戏后那一项悄悄回到默认值，玩家只会觉得「这开关时灵时不灵」。
        public Dictionary<string, bool> toggles = new Dictionary<string, bool>();

        // ======================================================================
        // 读写
        // ======================================================================

        private void EnsureDict()
        {
            if (toggles == null)
            {
                toggles = new Dictionary<string, bool>();
            }
        }

        /// <summary>
        /// 取某一项的开关状态。**查不到时返回 true（开启）**。
        /// </summary>
        public bool IsOn(string id)
        {
            // 两个老字段优先：它们在配置文件里是独立的键，不走字典。
            if (id == "keepLeaderTitle")
            {
                return keepLeaderTitle;
            }
            if (id == "fixVfeeInstrumentSpace")
            {
                return fixVfeeInstrumentSpace;
            }
            EnsureDict();
            bool value;
            if (toggles.TryGetValue(id, out value))
            {
                return value;
            }
            return true;
        }

        /// <summary>设置某一项。两个老字段写回它们自己的键，其余写字典。</summary>
        public void SetOn(string id, bool value)
        {
            if (id == "keepLeaderTitle")
            {
                keepLeaderTitle = value;
                return;
            }
            if (id == "fixVfeeInstrumentSpace")
            {
                fixVfeeInstrumentSpace = value;
                return;
            }
            EnsureDict();
            toggles[id] = value;
        }

        /// <summary>把所有已知项一次设成同一个值（界面上的「全部开启 / 全部关闭」）。</summary>
        public void SetAll(bool value)
        {
            for (int i = 0; i < FixToggles.All.Length; i++)
            {
                SetOn(FixToggles.All[i].Id, value);
            }
        }

        /// <summary>当前有多少项是**关闭**的（设置界面顶部显示用）。</summary>
        public int CountDisabled()
        {
            int n = 0;
            for (int i = 0; i < FixToggles.All.Length; i++)
            {
                if (!IsOn(FixToggles.All[i].Id))
                {
                    n++;
                }
            }
            return n;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // 第三个参数是「配置里找不到这一项时用什么值」——
            // 从还没有这个开关的旧版本升上来时，读到的就是 true（保持默认开启）。
            Scribe_Values.Look(ref keepLeaderTitle, "keepLeaderTitle", true);
            Scribe_Values.Look(ref fixVfeeInstrumentSpace, "fixVfeeInstrumentSpace", true);

            // 其余开关。LookMode.Value 表示「键和值都按普通值序列化」
            // （string / bool 都属于值类型范畴，不需要 LookMode.Deep）。
            Scribe_Collections.Look(ref toggles, "toggles", LookMode.Value, LookMode.Value);

            // 老配置里没有 toggles 这一段，读完是 null。
            // （本项不需要「读盘期间的非空保证」：IsOn / SetOn 里都先做了 EnsureDict。）
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureDict();
            }
        }
    }
}
