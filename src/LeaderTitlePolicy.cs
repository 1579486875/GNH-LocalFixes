namespace GNH.LocalFixes
{
    // ==========================================================================
    // 「已经存在的领袖头衔，要不要保住」——这条判断被单独放进一个文件，原因有两个：
    //
    //   1. 它只用到了 string，不碰任何游戏类型，因此**可以脱离游戏单独编译、单独跑测试**
    //      （测试工程见 _work\gnh-localfixes-test\，各种极端输入都在那里验过）；
    //   2. 真正的补丁类就只剩「接线」的活儿，几行看完。
    // ==========================================================================
    internal static class LeaderTitlePolicy
    {
        // 返回 true  = 保住现有头衔，跳过游戏的重新生成。
        // 返回 false = 照原版重新生成（或者照原版把它置空）。
        //
        //   featureEnabled    模组设置里的那个总开关。关掉 → 完全等于原版行为。
        //   currentMaleTitle  当前 RimWorld.Ideo.leaderTitleMale 的值，可能是 null / "" / 正常字符串。
        //
        // 【为什么只看 male，不看 female】
        //
        // 原版 IdeoFoundation.GenerateLeaderTitle() 的最后一句是
        //     ideo.leaderTitleFemale = ideo.leaderTitleMale;
        // 男女头衔在机制上被强行绑成同一个值，male 就是这一对名字的唯一代表。
        //（顺带一提：.rid 里给两者写不同内容也存不住 —— 一触发重算就被抹平。）
        internal static bool ShouldKeepExistingTitle(string currentMaleTitle, bool featureEnabled)
        {
            if (!featureEnabled)
            {
                return false;
            }

            // 空值代表「这个文化还没有头衔」——新建文化的第一次生成，或者 .rid 里根本没写。
            // 这种情况必须放行，否则新文化的领袖头衔会永远是空的。
            return !string.IsNullOrEmpty(currentMaleTitle);
        }
    }
}
