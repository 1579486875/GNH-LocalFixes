using System.Collections.Generic;
using Verse;
using Verse.AI;
using RimWorld;
using rjw;

namespace ElToro_BAddon
{
    // ==========================================================================
    // 这个文件解决的是：ElToro 模组的一个「任务类型找不到」报错。
    // ==========================================================================
    //
    // 【问题出在哪】
    //
    // ElToro 的 Def 文件里定义了一个叫 BestialityInvite_Watching 的任务，
    // 里面写着「这个任务由某某类负责实现」。
    //
    // 但作者发布出来的 ElToro_BAddon.dll 里，偏偏少了那个类。
    // 于是游戏加载到这个 Def 时找不到对应的类，直接报
    // 「Could not find a type named ...」，这个任务也就变成了空壳。
    //
    // 【我们的做法】
    //
    // 按 ElToro 同类任务（JobDriver_BestialityInvite_InviteToWatch）的结构重写一份，
    // 放在同一个命名空间、用同一个类名，这样那个 Def 就能正常解析了。
    // （说明：作者并没发布过这个类的源码，所以这里是「重写」而不是「照抄」；
    //   判定互动用的 API 也与同类不同 —— 同类用 GetResistanceLevel() 的枚举，
    //   这里用 GetCalculatedResistance() 的 float 配阈值。）
    //
    // 【将来作者自己补上了怎么办】
    //
    // 那时候会有两个程序集声明同一个完整类名，但不会报「有歧义」：
    // 游戏的 GenTypes.GetTypeInAnyAssemblyRaw 会按顺序遍历，直接返回第一个命中的。
    // 而本补丁包在 About.xml 里声明了「排在 ElToro.BAddon 之后加载」，
    // 所以作者的版本会排在前面、优先被选中。
    // 真到那时候，你把本文件删掉即可。
    public class JobDriver_BestialityInvite_Watching : JobDriver
    {
        protected Pawn human => (Pawn)this.job.targetA.Thing;
        protected Pawn animal => (Pawn)this.job.targetB.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => !pawn.CanReach(TargetA, PathEndMode.Touch, Danger.Some));
            this.FailOn(() => pawn.Drafted);
            this.FailOn(() => TargetA.Pawn.Drafted);

            // 用带 face 参数的重载，而不是自己覆盖 initAction / tickAction。
            //
            // 原因是反编译看出来的：Toils_General.Wait 在 initAction 里放了
            // pather.StopDead()（让小人停下脚步）。早先的写法整个覆盖掉 initAction，
            // 停步那一步就没了 —— 表现是小人一边「等待」一边继续沿着原来的路线走。
            // 而传了 face 参数之后，框架会自己把 handlingFacing 打开，
            // 并在 tickIntervalAction 里让小人转头面向目标，正好就是要的效果。
            var waitMating = Toils_General.Wait(400, TargetIndex.A);
            waitMating.socialMode = RandomSocialMode.Off;
            yield return waitMating;

            var interactToil = new Toil();
            interactToil.initAction = () =>
            {
                // 这个组件是由 CompInjector_CompBestialityMemory 注入到人形单位身上的。
                // 下面那些「先判断是不是空」的检查，是为了防止某个数据不完整的小人
                // 在任务执行过程中抛出异常，把整个任务流程打断。
                CompBestialityMemory memory = pawn.TryGetComp<CompBestialityMemory>();
                if (memory == null)
                {
                    return;
                }

                if (!memory.WitnessedConsensualBestiality)
                {
                    memory.WitnessedConsensualBestiality = true;
                    if (Settings.DebugMode)
                        ModLog.Message($"[Bestiality Milestone] Milestone reached for pawn {pawn.NameShortColored}: Witnessed Consensual Bestiality.");
                }
                // 0.025 = 每看一次涨 2.5% 好感，幅度刻意很小：
                // 这个行为是「看着别人做事」，不该比亲自参与涨得还快。
                memory.ApplyAnimalOpinion(animal, 0.025f, isPermanent: false);

                float resistance = memory.GetCalculatedResistance();
                InteractionDef interaction;
                // 按「对方有多抗拒」挑三种反应之一：
                //   0.30 以下 = 不太抗拒（低抗拒版）、0.70 以下 = 中等、再高 = 高抗拒版。
                // 三个候选互动都是从 ElToro 作者自己的 Def 里取现成的，本文件不新增文本。
                if (resistance <= 0.30f)
                {
                    interaction = defs.BestialityInvite_Interaction_Watching_LowRes;
                }
                else if (resistance <= 0.70f)
                {
                    interaction = defs.BestialityInvite_Interaction_Watching_MedRes;
                }
                else
                {
                    interaction = defs.BestialityInvite_Interaction_Watching_HighRes;
                }

                if (interaction != null)
                {
                    pawn.interactions?.TryInteractWith(human, interaction);
                }

                CompBestialityMemory humanMemory = human.TryGetComp<CompBestialityMemory>();
                if (humanMemory == null)
                {
                    return;
                }

                if (!humanMemory.WasSeenPositive)
                {
                    humanMemory.WasSeenPositive = true;
                    if (Settings.DebugMode)
                        ModLog.Message($"[Bestiality Milestone] Milestone reached for pawn {human.NameShortColored}: Was Seen Positively during bestiality.");
                }
            };
            interactToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return interactToil;
        }
    }
}
