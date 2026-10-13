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
        public readonly List<FrameContact> contacts = new List<FrameContact>();

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
            float prevA = move.attackerPosition.x;
            float prevT = move.targetPosition.x;
            var contacted = new HashSet<int>();

            for (int f = 0; f < sim.frameCount; f++)
            {
                // Атакующий смотрит вправо (+X), цель — влево (−X): «вперёд» у них разного знака.
                float rawA = move.attackerPosition.x + AuthoredOffset(move, Actor.Attacker, f) + shiftA;
                float rawT = move.targetPosition.x - AuthoredOffset(move, Actor.Target, f) + shiftT;

                // Отталкивание уводит цель от атакующего, то есть в +X.
                for (int c = 0; c < sim.contacts.Count; c++)
                {
                    var contact = sim.contacts[c];
                    int length = contact.pushEndFrame - contact.frame;
                    rawT += contact.pushDistance *
                            MoveProfiles.Evaluate(PushProfile, (float)(f - contact.frame) / length);
                }

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

                bool pinnedA = false;
                bool pinnedT = false;
                if (xa < left) { xa = left; pinnedA = true; }
                else if (xa > right) { xa = right; pinnedA = true; }
                if (xt > right) { xt = right; pinnedT = true; }
                else if (xt < left) { xt = left; pinnedT = true; }

                // 3. Если граница вдавила одного в другого, остаток перекрытия достаётся тому, кто не упёрся.
                overlap = minGap - (xt - xa);
                if (overlap > 0f)
                {
                    if (pinnedT && !pinnedA) xa -= overlap;
                    else if (pinnedA && !pinnedT) xt += overlap;
                    else
                    {
                        xa -= overlap * attackerShare;
                        xt += overlap * (1f - attackerShare);
                    }
                    sim.pushboxBlocked[f] = true;
                }

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
                var hurtbox = ShapedHurtbox(move, Actor.Target, xt, f);
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
                        if (!Overlaps(Hitbox(span, attackerCenter), hurtbox)) continue;

                        contacted.Add(key);
                        sim.contacts.Add(new FrameContact
                        {
                            trackIndex = t,
                            spanIndex = s,
                            frame = f,
                            pushEndFrame = f + Mathf.Max(1, span.pushFrames),
                            pushDistance = Mathf.Max(0f, span.pushDistance)
                        });
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
