using System.Collections.Generic;
using Verse;

// ==========================================================================
// 这不是一个 Harmony 补丁 —— 它补的是两个**缺失的类型定义**。
// ==========================================================================
//
// 【为什么要补类型】
//
// RJW 生态里有两个模组共用同一个 Def 类型 `RJW_BGS.RaceGeneDef`：
//
//   · telanda 的 RJW Animal Gene Inheritance（`telanda.rjw.animalgeneinheritance`）
//     —— 这个类型的**拥有者**，代码和数据表都在它那里；
//   · 另有若干数据表模组（例如「生物大师的ElToro Patches」）只用这个类型
//     写 XML，自己不提供程序集。
//
// 一旦 telanda 没启用，这个类型就不存在，于是所有引用它的 XML 全部加载失败：
//
//     Type RJW_BGS.RaceGeneDef is not a Def type or could not be found,
//       in file RaceGeneDefs_template.xml.
//
// 而 RJW-Genes 本体（Rjw-Genes.dll，1.6 版）**自己并不引用**这个类型
// （已用反编译核对：1.6 的 dll 里 "RJW_BGS" 出现 0 次，"RaceGeneDef" 0 次），
// 所以那些表本质上是给 telanda 的代码准备的数据。
//
// 【这段代码做什么、不做什么】
//
// 只把**类型定义**补齐，字段与 telanda 的原版**逐字段一致**（见下）：
//   · 补上之后，没装 telanda 时那些 XML 能正常加载，红字消失；
//   · 它**不会**凭空造出「动物基因继承」这个功能 —— 那功能是 telanda 的
//     6 个 Harmony 补丁实现的，没有它的 dll 就不存在。这一点必须说清楚。
//
// 【与 telanda 共存时会不会打架】
//
// 会，如果解析顺序不利的话 —— 见 Patch_GenTypes_PreferTelanda。
// 那个补丁保证：**只要 telanda 在场，就一律用它的类型**，
// 这里的定义只在它缺席时兜底。两个类型的字段完全一致，因此不会读出错值。
//
// 【字段来源】
//
// 2026-10-10 反编译 RJWAnimalGeneInheritance.dll（MVID 05360c6a...）逐字段抄录：
//     public class RaceGeneDef : Def {
//         public int priority;  public string raceGroup;
//         public List<string> raceGroups;   public List<string> raceNames;
//         public List<string> pawnKindNames;
//         public List<BestialityGeneInheritanceDef> genes;  public string hybridName;
//     }
//     public class BestialityGeneInheritanceDef {   // 注意：它不是 Def
//         public string defName;  public float chance = 1f;
//     }
//
// ⚠ 字段名/类型一处都不能改：XML 是按字段名反射写入的，
//   改一个字，对应的标签就会解析失败或静默丢失。
namespace RJW_BGS
{
    // `genes` 列表的元素类型。它**不是** Def —— 只是一个带两个字段的普通类，
    // 在 XML 里以内联形式书写：
    //     <genes><li><defName>AG_InsectBlood</defName><chance>0.5</chance></li></genes>
    public class BestialityGeneInheritanceDef
    {
        public string defName;

        public float chance = 1f;
    }

    // 与 telanda 的 RaceGeneDef 同命名空间、同名的兜底定义。
    // 仅在 telanda 缺席时会被 RimWorld 的类型解析选中。
    public class RaceGeneDef : Def
    {
        public int priority;

        public string raceGroup;

        public List<string> raceGroups;

        public List<string> raceNames;

        public List<string> pawnKindNames;

        public List<BestialityGeneInheritanceDef> genes;

        public string hybridName;
    }
}
