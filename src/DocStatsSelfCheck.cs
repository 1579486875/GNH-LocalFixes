// ---------------------------------------------------------------------------
// 文档数字自检（DocStatsSelfCheck）
//
// 【这个文件解决什么问题】
//   About.xml 的简介里写着「现在 N 条」，README 里写着「N 项 / N 个开关」。
//   这些是**静态文本**，游戏不会去改它；而修复项是**活数据**（FixToggles.All）。
//   两边的唯一联系就是「有人记得手改一下」—— 而人一定会忘。
//
//   实测事故：2026-10-28 加了【36】【37】【38】三项修复，代码、About 的项数清单
//   都改了，但忘了在 FixToggles.All 里建条目 —— 界面显示「共 35 项」、实际装了 38 项，
//   其中 3 项在设置界面**根本看不到**，也关不掉。这件事直到 2026-10-29 才被发现。
//
// 【做法】
//   每次启动把「About.xml 里声明的条数」与「FixToggles.All 的真实长度」比一次，
//   不一致就打一条 WARNING 指名差多少。
//
//   为什么不把期望值写死在代码里：那等于把「会失同步的东西」从文档搬进代码，
//   问题原封不动。读文件才能真的发现「文档没跟上实现」。
//
//   为什么不直接改文档：About.xml 是静态文件，游戏运行时改它既不可靠也不礼貌
//   （玩家可能只读安装、或工坊版是只读的）。所以这里**只报不改**，
//   真正的改写交给 tools/docstats.py，人跑一次就行。
//
// 【它不做的事】
//   不修正任何东西、不阻止加载、不影响任何开关。最坏情况就是一条 WARNING。
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.RegularExpressions;
using Verse;

namespace GNH.LocalFixes
{
    [StaticConstructorOnStartup]
    internal static class DocStatsSelfCheck
    {
        private const string OurPackageId = "gnh.cn.cys.localfixes";

        static DocStatsSelfCheck()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                // 自检本身绝不能影响加载。出错就安静收场。
                Log.Warning("[GNH LocalFixes] 文档数字自检自身出错（已忽略）：" + ex.Message);
            }
        }

        private static void Run()
        {
            int actual = (FixToggles.All == null) ? 0 : FixToggles.All.Length;
            if (actual <= 0)
            {
                return;
            }

            string rootDir = FindOurRootDir();
            if (rootDir.NullOrEmpty())
            {
                // 通常意味着这是工坊版被改名后的场景，或者本地目录结构异常。
                // 这种情况下没有可比对的 About.xml，直接不报。
                return;
            }

            string aboutPath = Path.Combine(Path.Combine(rootDir, "About"), "About.xml");
            if (!File.Exists(aboutPath))
            {
                return;
            }

            string text;
            try
            {
                text = File.ReadAllText(aboutPath);
            }
            catch (Exception ex)
            {
                Log.Warning("[GNH LocalFixes] 读不到自己的 About.xml，文档数字自检跳过：" + ex.Message);
                return;
            }

            // 简介里那句是「……现在 38 条，条条独立。」
            // 只认这一种写法，宁可漏检也不误报。
            Match m = Regex.Match(text, @"现在\s*(\d+)\s*条");
            if (!m.Success)
            {
                Log.Warning("[GNH LocalFixes] About.xml 简介里找不到「现在 N 条」这个句式，"
                    + "文档数字自检跳过。实际注册 " + actual + " 项。");
                return;
            }

            int declared;
            if (!int.TryParse(m.Groups[1].Value, out declared))
            {
                return;
            }

            if (declared == actual)
            {
                // 一致时**不打印**：这类自检只在出问题时说话，正常启动别刷日志。
                return;
            }

            Log.Warning("[GNH LocalFixes] 文档与实际不一致：About.xml 简介写「现在 "
                + declared + " 条」，而 FixToggles.All 实际注册 " + actual + " 项，"
                + "相差 " + (actual - declared) + " 项。"
                + "设置界面按实际注册数显示（" + actual + " 项），所以界面上是对的，"
                + "只是简介文字过期了。跑一次仓库里的 tools/docstats.py 可以自动改齐。");
        }

        /// <summary>
        /// 找到本模组自己的根目录。
        /// 先按 packageId 在已加载模组里找；找不到再退化到「本 dll 所在目录的上一级」。
        /// 后者能兜住「本地副本被改了 packageId」这种少见情况。
        /// </summary>
        private static string FindOurRootDir()
        {
            try
            {
                var mods = LoadedModManager.RunningModsListForReading;
                if (mods != null)
                {
                    for (int i = 0; i < mods.Count; i++)
                    {
                        ModContentPack pack = mods[i];
                        if (pack != null && pack.PackageId == OurPackageId)
                        {
                            return pack.RootDir;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 落到下面的退化方案。
            }

            try
            {
                // dll 在 <RootDir>\Assemblies\ 下。
                string asmDir = Path.GetDirectoryName(typeof(DocStatsSelfCheck).Assembly.Location);
                if (asmDir.NullOrEmpty())
                {
                    return null;
                }
                DirectoryInfo parent = Directory.GetParent(asmDir);
                return parent?.FullName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
