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

        public override void ExposeData()
        {
            base.ExposeData();

            // 第三个参数是「配置里找不到这一项时用什么值」——
            // 从还没有这个开关的旧版本升上来时，读到的就是 true（保持默认开启）。
            Scribe_Values.Look(ref keepLeaderTitle, "keepLeaderTitle", true);
            Scribe_Values.Look(ref fixVfeeInstrumentSpace, "fixVfeeInstrumentSpace", true);
        }
    }
}
