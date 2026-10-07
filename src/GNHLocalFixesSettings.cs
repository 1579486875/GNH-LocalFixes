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

        public override void ExposeData()
        {
            base.ExposeData();

            // 第三个参数是「配置里找不到这一项时用什么值」——
            // 从还没有这个开关的旧版本升上来时，读到的就是 true（保持默认开启）。
            Scribe_Values.Look(ref keepLeaderTitle, "keepLeaderTitle", true);
        }
    }
}
