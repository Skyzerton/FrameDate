using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>Капсула в плоскости X/Y: ось-отрезок [a, b] и радиус.</summary>
    public struct Capsule2D
    {
        public Vector2 a;
        public Vector2 b;
        public float radius;

        public Capsule2D(Vector2 a, Vector2 b, float radius)
        {
            this.a = a;
            this.b = b;
            this.radius = radius;
        }
    }

    /// <summary>
    /// Контакт: hitbox интервала Attack впервые задел hurtbox цели на кадре frame.
    /// Отталкивание — вычисленный интервал движения цели [frame, pushEndFrame).
    /// </summary>
    public struct FrameContact
    {
        public int trackIndex;
        public int spanIndex;
        public int frame;
        public int pushEndFrame;
        /// <summary>Реакция цели из каталога, запущенная контактом; пусто — отталкивание FrameDate.</summary>
        public string reaction;
        public float pushDistance;
    }

    /// <summary>
    /// Покадровый расчёт приёма. Чистая функция от данных: ни состояния, ни кеша,
    /// поэтому картинка, таймлайн и инспектор не могут разойтись между собой.
    ///
    /// Порядок внутри кадра f:
    ///   1. авторское движение обоих акторов (дорожки Movement);
    ///   2. отталкивание цели от контактов прошлых кадров;
    ///   3. пушбоксы: если тела сошлись ближе суммы полуширин, цель сдвигается на недостающее
    ///      и остаётся сдвинутой — атакующий, идущий вперёд, толкает цель перед собой;
    ///   4. проверка контакта hitbox → hurtbox на итоговых позициях. Контакт на кадре K
    ///      запускает отталкивание [K, K + pushFrames): на самом кадре K цель ещё не сдвинута.
    /// Один интервал Attack даёт не больше одного контакта.
    /// </summary>
    public sealed class FrameDateSimulation
    {
        public const MoveProfile PushProfile = MoveProfile.EaseOut;

        public readonly int frameCount;
        public readonly float[] attackerX;
        public readonly float[] targetX;
        /// <summary>На кадре пушбоксы упёрлись и сдвинули цель.</summary>
        public readonly bool[] pushboxBlocked;
        /// <summary>Левая и правая граница хода центров на кадре: стены, сжатые камерными границами.</summary>
        public readonly float[] leftLimit;
        public readonly float[] rightLimit;
        /// <summary>На кадре дистанция дошла до порога и включились камерные границы.</summary>
        public readonly bool[] cameraEngaged;
        /// <summary>Актор на кадре упёрся в стену или камерную границу.</summary>
        public readonly bool[] attackerAtLimit;
        public readonly bool[] targetAtLimit;
        /// <summary>На кадре граница вытолкнула одного из пары и, раз пушбоксы расталкивались, сдвинула и другого.</summary>
        public readonly bool[] wallRecoil;
        public readonly List<FrameContact> contacts = new List<FrameContact>();

        public enum ReactionKind
        {
            Hit,
            Crit,
            BlockIn,
            BlockHit
        }

        /// <summary>Реакция цели: анимация из каталога, кадр начала и обрыва (новая реакция обрывает прежнюю).</summary>
        public struct ReactionPlay
        {
            public string name;
            public ReactionKind kind;
            public int startFrame;
            /// <summary>Кадр обрыва новой реакцией; int.MaxValue — не обрывалась.</summary>
            public int endFrame;
            public int trackIndex;
            public int spanIndex;
        }

        public readonly List<ReactionPlay> reactions = new List<ReactionPlay>();

        private readonly float _attackerY;
        private readonly float _targetY;

        private FrameDateSimulation(FrameDateMove move)
        {
            frameCount = Mathf.Max(0, move.durationFrames);
            attackerX = new float[frameCount];
            targetX = new float[frameCount];
            pushboxBlocked = new bool[frameCount];
            leftLimit = new float[frameCount];
            rightLimit = new float[frameCount];
            cameraEngaged = new bool[frameCount];
            attackerAtLimit = new bool[frameCount];
            targetAtLimit = new bool[frameCount];
            wallRecoil = new bool[frameCount];
            _attackerY = move.attackerPosition.y;
            _targetY = move.targetPosition.y;
        }

public static FrameDateSimulation Run(FrameDateMove move)
        {
            var sim = new FrameDateSimulation(move);
            if (sim.frameCount == 0) return sim;

            var metrics = move.Metrics;
            float minGap = move.PushHalfWidth(Actor.Attacker) + move.PushHalfWidth(Actor.Target);
            float massA = Mathf.Max(1e-4f, move.attackerBody.mass);
            float massT = Mathf.Max(1e-4f, move.targetBody.mass);
            // Как в RepulsionSystem игры: каждый уступает долю, равную массе другого к общей массе.
            float attackerShare = massT / (massA + massT);

            float shiftA = 0f;
            float shiftT = 0f;
            // Камерные границы, как в игре, считаются от положения на прошлом кадре: иначе середина
            // съезжает за отходящим, и неподвижного противника тащит следом.
            float startA = move.StartPosition(Actor.Attacker).x;
            float startT = move.StartPosition(Actor.Target).x;
            float prevA = startA;
            float prevT = startT;
            var contacted = new HashSet<int>();

            for (int f = 0; f < sim.frameCount; f++)
            {
                // Атакующий смотрит вправо (+X), цель — влево (−X): «вперёд» у них разного знака.
                float rawA = startA + AuthoredOffset(move, Actor.Attacker, f) + shiftA;
                float rawT = startT - AuthoredOffset(move, Actor.Target, f) + shiftT;

                // Отталкивание уводит цель от атакующего, то есть в +X.
                for (int c = 0; c < sim.contacts.Count; c++)
                {
                    var contact = sim.contacts[c];
                    int length = contact.pushEndFrame - contact.frame;
                    rawT += contact.pushDistance *
                            MoveProfiles.Evaluate(PushProfile, (float)(f - contact.frame) / length);
                }

                // Реакции цели сдвигают её собственной анимацией (отлёт), вместо отталкивания FrameDate.
                rawT += sim.ReactionOffset(move, f);

                float xa = rawA;
                float xt = rawT;

                // 1. Пушбоксы: перекрытие делится по массам.
                float overlap = minGap - (xt - xa);
                if (overlap > 0f)
                {
                    xa -= overlap * attackerShare;
                    xt += overlap * (1f - attackerShare);
                    sim.pushboxBlocked[f] = true;
                }

                // 2. Стены и камерные границы. Камерные включаются, когда на прошлом кадре центры разошлись
                //    на порог, и стоят на середине ± доля × порог (в игре 0.55, предел дистанции 1.1 × порог).
                float left = metrics.leftWallX;
                float right = metrics.rightWallX;
                float threshold = metrics.maxDistanceThreshold;
                if (prevT - prevA >= threshold)
                {
                    float middle = (prevA + prevT) * 0.5f;
                    float half = threshold * metrics.cameraBorderRatio;
                    left = Mathf.Max(left, middle - half);
                    right = Mathf.Min(right, middle + half);
                    sim.cameraEngaged[f] = true;
                }

                // 3. Как UpdateBordersRepulsion в игре: граница выталкивает заступившего на величину заступа, а если на этом
                //    кадре пушбоксы пары расталкивались — на ту же величину сдвигается и противник. Так стена «отдаёт»
                //    атакующему, когда он загоняет цель в угол, и бойцы не проходят ни сквозь стену, ни друг сквозь друга.
                bool pairShifted = sim.pushboxBlocked[f];
                float wallA = xa < left ? left - xa : (xa > right ? right - xa : 0f);
                xa += wallA;
                if (wallA != 0f && pairShifted) xt += wallA;
                float wallT = xt > right ? right - xt : (xt < left ? left - xt : 0f);
                xt += wallT;
                if (wallT != 0f && pairShifted) xa += wallT;
                bool pinnedA = wallA != 0f;
                bool pinnedT = wallT != 0f;
                sim.wallRecoil[f] = pairShifted && (pinnedA || pinnedT);

                // Сдвиги от пушбоксов и границ копятся: вытолкнутый остаётся там, куда его вытолкнули.
                shiftA += xa - rawA;
                shiftT += xt - rawT;
                prevA = xa;
                prevT = xt;

                sim.attackerX[f] = xa;
                sim.targetX[f] = xt;
                sim.leftLimit[f] = left;
                sim.rightLimit[f] = right;
                sim.attackerAtLimit[f] = pinnedA;
                sim.targetAtLimit[f] = pinnedT;

                // Форма тела на этом кадре учитывает дорожки Hurtbox: приседание цели уводит hurtbox вниз,
                // приседание атакующего опускает центр тела, а с ним и hitbox.
                // Подъём защиты: на старте удара с исходом «блок», если цель ближе дистанции блока (Block: Distance в игре).
                for (int t = 0; t < move.tracks.Count; t++)
                {
                    var track = move.tracks[t];
                    if (track.role != TrackRole.Attack || track.actor != Actor.Attacker) continue;
                    for (int s = 0; s < track.spans.Count; s++)
                    {
                        var span = track.spans[s];
                        if (span.startFrame != f) continue;
                        ReactionKind kind;
                        string guard = ResolveReaction(move, span, false, out kind);
                        if (guard == null || xt - xa > span.blockDistance) continue;
                        sim.StartReaction(guard, kind, f, t, s);
                    }
                }

                var hurtboxes = sim.TargetHurtboxesAt(move, f, xt);
                var attackerCenter = ShapedCenter(move, Actor.Attacker, xa, f);

                for (int t = 0; t < move.tracks.Count; t++)
                {
                    var track = move.tracks[t];
                    if (track.role != TrackRole.Attack || track.actor != Actor.Attacker) continue;

                    for (int s = 0; s < track.spans.Count; s++)
                    {
                        var span = track.spans[s];
                        if (!span.Contains(f)) continue;

                        int key = t * 65536 + s;
                        if (contacted.Contains(key)) continue;
                        if (!sim.SpanHits(move, span, f, attackerCenter, hurtboxes)) continue;

                        ReactionKind reactionKind;
                        string reaction = ResolveReaction(move, span, true, out reactionKind);

                        contacted.Add(key);
                        sim.contacts.Add(new FrameContact
                        {
                            trackIndex = t,
                            spanIndex = s,
                            frame = f,
                            pushEndFrame = f + Mathf.Max(1, span.pushFrames),
                            // С реакцией из каталога цель сдвигает её анимация, а не отталкивание FrameDate.
                            pushDistance = reaction != null ? 0f : Mathf.Max(0f, span.pushDistance),
                            reaction = reaction
                        });
                        if (reaction != null) sim.StartReaction(reaction, reactionKind, f, t, s);
                    }
                }
            }

            return sim;
        }

        /// <summary>Авторское смещение актора «вперёд» на кадре: сумма всех его интервалов Movement.</summary>
        public static float AuthoredOffset(FrameDateMove move, Actor actor, int frame)
        {
            float offset = 0f;
            for (int t = 0; t < move.tracks.Count; t++)
            {
                var track = move.tracks[t];
                if (track.role != TrackRole.Movement || track.actor != actor) continue;

                for (int s = 0; s < track.spans.Count; s++)
                {
                    var span = track.spans[s];
                    if (span.Length <= 0) continue;
                    offset += span.distance * MoveProfiles.Evaluate(span.profile, span.Progress(frame));
                }
            }
            return offset;
        }

        private int Clamp(int frame) => Mathf.Clamp(frame, 0, Mathf.Max(0, frameCount - 1));

        public Vector2 AttackerCenter(int frame) =>
            frameCount == 0 ? Vector2.zero : new Vector2(attackerX[Clamp(frame)], _attackerY);

        public Vector2 TargetCenter(int frame) =>
            frameCount == 0 ? Vector2.zero : new Vector2(targetX[Clamp(frame)], _targetY);

        /// <summary>
        /// Реакция удара на его итоговый исход: имя анимации из каталога и вид.
        /// До контакта (onContact = false) — только подъём защиты для исхода «блок». На контакте: попадание; крит
        /// (если у удара нет крит-реакции — обычная); блок — удар в блок (нет — подъём защиты).
        /// null — для этого исхода в каталоге реакции нет.
        /// </summary>
        public static string ResolveReaction(FrameDateMove move, FrameSpan span, bool onContact, out ReactionKind kind)
        {
            kind = ReactionKind.Hit;
            if (move.reactions == null) return null;
            var outcome = move.OutcomeFor(span);
            string name = null;
            if (!onContact)
            {
                if (outcome != TargetOutcome.Block) return null;
                kind = ReactionKind.BlockIn;
                name = span.reactionBlockIn;
            }
            else if (outcome == TargetOutcome.Crit && !string.IsNullOrEmpty(span.reactionCrit))
            {
                kind = ReactionKind.Crit;
                name = span.reactionCrit;
            }
            else if (outcome == TargetOutcome.Block)
            {
                kind = ReactionKind.BlockHit;
                name = !string.IsNullOrEmpty(span.reactionBlockHit) ? span.reactionBlockHit : span.reactionBlockIn;
            }
            else
            {
                name = span.reactionHit;
            }
            return move.reactions.IsValid(move.reactions.Find(name)) ? name : null;
        }

        private void StartReaction(string name, ReactionKind kind, int frame, int track, int span)
        {
            for (int i = 0; i < reactions.Count; i++)
            {
                var play = reactions[i];
                if (play.endFrame > frame)
                {
                    play.endFrame = frame;
                    reactions[i] = play;
                }
            }
            reactions.Add(new ReactionPlay { name = name, kind = kind, startFrame = frame, endFrame = int.MaxValue, trackIndex = track, spanIndex = span });
        }

        /// <summary>Реакция, которую цель играет на кадре: последняя начатая, пока не оборвана и не доиграна.</summary>
        public bool ActiveReaction(FrameDateMove move, int frame, out FrameDateReactionCatalog.Reaction reaction, out int local, out ReactionPlay play)
        {
            reaction = null;
            local = 0;
            play = default(ReactionPlay);
            if (move.reactions == null) return false;
            for (int i = reactions.Count - 1; i >= 0; i--)
            {
                var candidate = reactions[i];
                if (candidate.startFrame > frame || frame >= candidate.endFrame) continue;
                var found = move.reactions.Find(candidate.name);
                if (!move.reactions.IsValid(found) || frame - candidate.startFrame >= found.frameCount) return false;
                reaction = found;
                local = frame - candidate.startFrame;
                play = candidate;
                return true;
            }
            return false;
        }

        /// <summary>Накопленный сдвиг цели её реакциями к кадру: оборванная реакция замирает на кадре обрыва.</summary>
        private float ReactionOffset(FrameDateMove move, int frame)
        {
            float offset = 0f;
            if (move.reactions == null) return 0f;
            foreach (var play in reactions)
            {
                if (play.startFrame > frame) continue;
                var reaction = move.reactions.Find(play.name);
                if (!move.reactions.IsValid(reaction)) continue;
                int local = Mathf.Min(frame, play.endFrame) - play.startFrame;
                // Цель смотрит в −X: «вперёд» реакции — к атакующему, отлёт назад уводит в +X.
                offset -= move.reactions.MotionForward(reaction, Mathf.Clamp(local, 0, reaction.frameCount - 1));
            }
            return offset;
        }

        /// <summary>Капсулы цели на кадре: реакции, если она идёт, иначе Idle героя или капсула тела FrameDate.</summary>
        public List<Capsule2D> TargetHurtboxesAt(FrameDateMove move, int frame, float targetX)
        {
            FrameDateReactionCatalog.Reaction reaction;
            int local;
            ReactionPlay play;
            if (!ActiveReaction(move, frame, out reaction, out local, out play)) return TargetHurtboxes(move, targetX, frame);
            var list = new List<Capsule2D>();
            var catalog = move.reactions;
            for (int c = 0; c < catalog.capsules.Count; c++)
                if (catalog.capsules[c].collidable) list.Add(TargetReactionCapsule(move, reaction, local, c, targetX));
            return list;
        }

        public static Capsule2D TargetReactionCapsule(FrameDateMove move, FrameDateReactionCatalog.Reaction reaction, int local, int capsule, float targetX)
        {
            var catalog = move.reactions;
            Vector2 a, b;
            catalog.CapsuleModel(reaction, local, capsule, out a, out b);
            float motionX = catalog.Position(reaction, local, catalog.motionBone).x;
            float ground = GroundY(move, Actor.Target);
            return new Capsule2D(
                new Vector2(targetX - catalog.forwardSign * (a.x - motionX), ground + a.y),
                new Vector2(targetX - catalog.forwardSign * (b.x - motionX), ground + b.y),
                catalog.capsules[capsule].radius);
        }

        /// <summary>
        /// Капсулы цели, по которым можно попасть: Collidable-капсулы Idle героя из игры (повёрнутые к атакующему),
        /// если они есть у приёма, иначе капсула тела FrameDate.
        /// </summary>
        public static List<Capsule2D> TargetHurtboxes(FrameDateMove move, float targetX, int frame)
        {
            var list = new List<Capsule2D>();
            var pose = move.pose;
            if (pose != null && pose.HasIdle)
            {
                for (int c = 0; c < pose.capsules.Count; c++)
                    if (pose.capsules[c].collidable) list.Add(TargetGameCapsule(move, c, targetX));
            }
            else
            {
                list.Add(ShapedHurtbox(move, Actor.Target, targetX, frame));
            }
            return list;
        }

        /// <summary>Удар берёт hitbox из капсул игры: у приёма есть капсулы, у интервала — атакующие детали.</summary>
        public static bool UsesGameHitbox(FrameDateMove move, FrameSpan span) =>
            move.pose != null && move.pose.HasCapsules && span.attackingParts != null && span.attackingParts.Count > 0;

        /// <summary>Пол актора: низ капсулы тела на старте. Поза игры стоит на полу в Y = 0.</summary>
        public static float GroundY(FrameDateMove move, Actor actor) =>
            move.StartPosition(actor).y - move.Body(actor).height * 0.5f;

        /// <summary>Капсула игры атакующего на кадре в мире FrameDate: относительно motion-кости, привязана к X атакующего.</summary>
        public static Capsule2D AttackerGameCapsule(FrameDateMove move, int frame, int capsule, float attackerX)
        {
            var pose = move.pose;
            Vector2 a, b;
            pose.CapsuleModel(frame, capsule, out a, out b);
            float motionX = pose.Position(frame, pose.motionBone).x;
            float ground = GroundY(move, Actor.Attacker);
            return new Capsule2D(
                new Vector2(attackerX + pose.forwardSign * (a.x - motionX), ground + a.y),
                new Vector2(attackerX + pose.forwardSign * (b.x - motionX), ground + b.y),
                pose.capsules[capsule].radius);
        }

        /// <summary>Капсула Idle героя для цели: цель смотрит на атакующего, то есть в −X.</summary>
        public static Capsule2D TargetGameCapsule(FrameDateMove move, int capsule, float targetX)
        {
            var pose = move.pose;
            Vector2 a, b;
            pose.IdleCapsuleModel(capsule, out a, out b);
            float ground = GroundY(move, Actor.Target);
            return new Capsule2D(
                new Vector2(targetX - pose.forwardSign * (a.x - pose.idleMotionX), ground + a.y),
                new Vector2(targetX - pose.forwardSign * (b.x - pose.idleMotionX), ground + b.y),
                pose.capsules[capsule].radius);
        }

        /// <summary>
        /// Попадание интервала Attack на кадре f. Детали из игры — как AttackCollisionSystem: в 2D, по области,
        /// которую капсула детали заметает между прошлым и текущим кадром. Иначе — ручной hitbox FrameDate.
        /// </summary>
        private bool SpanHits(FrameDateMove move, FrameSpan span, int f, Vector2 attackerCenter, List<Capsule2D> hurtboxes)
        {
            if (!UsesGameHitbox(move, span))
            {
                var hitbox = Hitbox(span, attackerCenter);
                foreach (var hurtbox in hurtboxes)
                    if (Overlaps(hitbox, hurtbox)) return true;
                return false;
            }

            var pose = move.pose;
            int previousFrame = Mathf.Max(0, f - 1);
            foreach (var part in span.attackingParts)
            {
                int c = pose.FindCapsule(part);
                if (c < 0) continue;
                var current = AttackerGameCapsule(move, f, c, attackerX[f]);
                var previous = AttackerGameCapsule(move, previousFrame, c, attackerX[previousFrame]);
                foreach (var hurtbox in hurtboxes)
                    if (SweptOverlaps(previous, current, hurtbox)) return true;
            }
            return false;
        }

        /// <summary>
        /// Пересечение цели с областью, заметённой капсулой за кадр: четырёхугольник current.a, current.b,
        /// previous.b, previous.a, расширенный на радиус, — как GetIntersectOfCapsulesPoint2D в игре.
        /// </summary>
        public static bool SweptOverlaps(Capsule2D previous, Capsule2D current, Capsule2D target)
        {
            float reach = current.radius + target.radius;
            if (SegmentDistance(current.a, current.b, target.a, target.b) <= reach) return true;
            if (SegmentDistance(previous.a, previous.b, target.a, target.b) <= reach) return true;
            if (SegmentDistance(current.a, previous.a, target.a, target.b) <= reach) return true;
            if (SegmentDistance(current.b, previous.b, target.a, target.b) <= reach) return true;
            return InsideQuad(target.a, current.a, current.b, previous.b, previous.a);
        }

        private static bool InsideQuad(Vector2 p, Vector2 v0, Vector2 v1, Vector2 v2, Vector2 v3)
        {
            var v = new[] { v0, v1, v2, v3 };
            bool inside = false;
            for (int i = 0, j = 3; i < 4; j = i++)
            {
                if ((v[i].y > p.y) != (v[j].y > p.y) &&
                    p.x < (v[j].x - v[i].x) * (p.y - v[i].y) / (v[j].y - v[i].y) + v[i].x)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Индекс контакта этого интервала или −1, если контакта нет.</summary>
        public int FindContact(int trackIndex, int spanIndex)
        {
            for (int i = 0; i < contacts.Count; i++)
                if (contacts[i].trackIndex == trackIndex && contacts[i].spanIndex == spanIndex) return i;
            return -1;
        }

        // ------------------------------------------------------------------
        // Геометрия
        // ------------------------------------------------------------------

        /// <summary>
        /// Масштаб тела на кадре: первая активная дорожка Hurtbox актора в порядке дорожек.
        /// Без активной дорожки — исходный размер. Смена формы мгновенная, на границе интервала.
        /// </summary>
        public static void ShapeScale(FrameDateMove move, Actor actor, int frame, out float heightScale, out float radiusScale)
        {
            heightScale = 1f;
            radiusScale = 1f;
            for (int t = 0; t < move.tracks.Count; t++)
            {
                var track = move.tracks[t];
                if (track.role != TrackRole.Hurtbox || track.actor != actor) continue;
                for (int s = 0; s < track.spans.Count; s++)
                {
                    var span = track.spans[s];
                    if (!span.Contains(frame)) continue;
                    heightScale = Mathf.Max(0f, span.hurtHeightScale);
                    radiusScale = Mathf.Max(0f, span.hurtRadiusScale);
                    return;
                }
            }
        }

        /// <summary>
        /// Центр тела на кадре с учётом формы. Низ капсулы закреплён на месте исходного низа,
        /// поэтому при уменьшении высоты центр опускается — это и есть приседание.
        /// </summary>
        public static Vector2 ShapedCenter(FrameDateMove move, Actor actor, float x, int frame)
        {
            var body = move.Body(actor);
            float heightScale, radiusScale;
            ShapeScale(move, actor, frame, out heightScale, out radiusScale);
            float bottom = move.StartPosition(actor).y - body.height * 0.5f;
            return new Vector2(x, bottom + body.height * heightScale * 0.5f);
        }

        public static Capsule2D ShapedHurtbox(FrameDateMove move, Actor actor, float x, int frame)
        {
            var body = move.Body(actor);
            float heightScale, radiusScale;
            ShapeScale(move, actor, frame, out heightScale, out radiusScale);
            return Hurtbox(body.radius * radiusScale, body.height * heightScale, ShapedCenter(move, actor, x, frame));
        }

        /// <summary>Hurtbox актора на кадре расчёта.</summary>
        public Capsule2D HurtboxAt(FrameDateMove move, Actor actor, int frame)
        {
            var center = actor == Actor.Attacker ? AttackerCenter(frame) : TargetCenter(frame);
            return ShapedHurtbox(move, actor, center.x, Clamp(frame));
        }

        /// <summary>Центр тела актора на кадре расчёта — от него отсчитывается hitbox.</summary>
        public Vector2 CenterAt(FrameDateMove move, Actor actor, int frame)
        {
            var center = actor == Actor.Attacker ? AttackerCenter(frame) : TargetCenter(frame);
            return ShapedCenter(move, actor, center.x, Clamp(frame));
        }

        public static Capsule2D Hurtbox(float radius, float height, Vector2 center)
        {
            float half = Mathf.Max(0f, height * 0.5f - radius);
            return new Capsule2D(center + Vector2.down * half, center + Vector2.up * half, radius);
        }

        /// <summary>Вертикальная капсула тела с центром в center, исходного размера.</summary>
        public static Capsule2D Hurtbox(ActorBody body, Vector2 center)
        {
            float half = Mathf.Max(0f, body.height * 0.5f - body.radius);
            return new Capsule2D(center + Vector2.down * half, center + Vector2.up * half, body.radius);
        }

        /// <summary>Горизонтальная капсула hitbox атакующего, ось идёт вперёд (+X) от смещения.</summary>
        public static Capsule2D Hitbox(FrameSpan span, Vector2 attackerCenter)
        {
            var a = attackerCenter + span.hitOffset;
            return new Capsule2D(a, a + new Vector2(Mathf.Max(0f, span.hitLength), 0f), span.hitRadius);
        }

        /// <summary>Капсулы пересекаются, если расстояние между осями не больше суммы радиусов.</summary>
        public static bool Overlaps(Capsule2D p, Capsule2D q) =>
            SegmentDistance(p.a, p.b, q.a, q.b) <= p.radius + q.radius;

        public static float SegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            if (SegmentsCross(a, b, c, d)) return 0f;
            return Mathf.Min(
                Mathf.Min(PointSegment(a, c, d), PointSegment(b, c, d)),
                Mathf.Min(PointSegment(c, a, b), PointSegment(d, a, b)));
        }

        private static float PointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            float t = lengthSq > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        /// <summary>Строгое пересечение отрезков; касания и коллинеарность ловит PointSegment.</summary>
        private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(b - a, c - a);
            float d2 = Cross(b - a, d - a);
            float d3 = Cross(d - c, a - c);
            float d4 = Cross(d - c, b - c);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                   ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }
    }
}
