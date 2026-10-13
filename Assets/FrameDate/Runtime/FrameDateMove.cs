using System;
using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>К кому относится дорожка.</summary>
    public enum Actor
    {
        Attacker,
        Target
    }

    /// <summary>
    /// Роль дорожки — единственное, что интерпретирует инструмент.
    /// Имя и цвет дорожки задаёт пользователь; роль связывает её с механикой игры.
    /// None означает свободную пометку: рисуется и сохраняется, но в расчёт не входит.
    /// </summary>
    public enum TrackRole
    {
        None,
        Attack,
        Blocking,
        Throwable,
        Uninterrupt,
        Invulnerable,
        Unbreakable,
        Invincible,
        // Добавлена в конец: роли сериализуются номером, вставка в середину сдвинула бы существующие.
        Movement,
        // Размер hurtbox по времени: приседание, пригибание.
        Hurtbox
    }

    /// <summary>Как расстояние интервала движения распределяется внутри интервала.</summary>
    public enum MoveProfile
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut
    }

    public static class MoveProfiles
    {
        /// <summary>Доля пройденного расстояния при прогрессе t ∈ [0, 1].</summary>
        public static float Evaluate(MoveProfile profile, float t)
        {
            t = Mathf.Clamp01(t);
            switch (profile)
            {
                case MoveProfile.EaseIn: return t * t;
                case MoveProfile.EaseOut: return 1f - (1f - t) * (1f - t);
                case MoveProfile.EaseInOut: return t * t * (3f - 2f * t);
                default: return t;
            }
        }
    }

    /// <summary>
    /// Тело актора: капсула hurtbox (вертикальная, центр — позиция актора) и пушбокс.
    /// Пушбокс — полуширина по X; он не даёт телам зайти друг в друга и во вьюпорте не рисуется.
    /// </summary>
    [Serializable]
    public class ActorBody
    {
        [Tooltip("Радиус капсулы тела (hurtbox), Units.")]
        public float radius = 30f;

        [Tooltip("Полная высота капсулы тела (hurtbox), Units. Не меньше двух радиусов.")]
        public float height = 180f;

        [Tooltip("Своя ширина пушбокса вместо ширины Idle из конфига метрик.")]
        public bool overridePushWidth;

        [Tooltip("Полная ширина пушбокса по X, Units. Действует, только если включена своя ширина. Во вьюпорте не рисуется.")]
        public float pushWidth = 60f;

        [Tooltip("Масса при взаимном расталкивании: перекрытие пушбоксов делится обратно пропорционально массам. В игре стоя 1, лёжа 5.")]
        public float mass = 1f;
    }

    /// <summary>Интервал кадров [startFrame, endFrame): начало включено, конец исключён.</summary>
    [Serializable]
    public class FrameSpan
    {
        public int startFrame;
        public int endFrame;

        // --- Movement ---
        [Tooltip("Расстояние за интервал. Плюс — вперёд, к противнику; минус — назад.")]
        public float distance = 50f;

        [Tooltip("Как расстояние распределяется внутри интервала.")]
        public MoveProfile profile = MoveProfile.Linear;

        // --- Attack ---
        [Tooltip("Начало оси hitbox относительно центра тела атакующего. X — вперёд.")]
        public Vector2 hitOffset = new Vector2(20f, 25f);

        [Tooltip("Длина оси hitbox вперёд. 0 — шар.")]
        public float hitLength = 55f;

        [Tooltip("Радиус капсулы hitbox.")]
        public float hitRadius = 12f;

        [Tooltip("На сколько отталкивается цель при контакте.")]
        public float pushDistance = 30f;

        [Tooltip("За сколько кадров проходит отталкивание, начиная с кадра контакта.")]
        public int pushFrames = 6;

        // --- Hurtbox ---
        [Tooltip("Доля исходной высоты тела на этом интервале. 1 — без изменений. Низ капсулы остаётся на месте.")]
        public float hurtHeightScale = 0.6f;

        [Tooltip("Доля исходного радиуса тела на этом интервале. 1 — без изменений.")]
        public float hurtRadiusScale = 1f;

        public FrameSpan() { }

        public FrameSpan(int start, int end)
        {
            startFrame = start;
            endFrame = end;
        }

        public int Length => Mathf.Max(0, endFrame - startFrame);

        public bool Contains(int frame) => frame >= startFrame && frame < endFrame;

        public bool Overlaps(FrameSpan other) =>
            startFrame < other.endFrame && other.startFrame < endFrame;

        /// <summary>
        /// Прогресс движения на кадре: на startFrame — 0 (ещё не сдвинут), к endFrame — 1 (сдвиг полный).
        /// </summary>
        public float Progress(int frame) => Length > 0 ? Mathf.Clamp01((float)(frame - startFrame) / Length) : 0f;
    }

    /// <summary>Дорожка: пользовательские имя и цвет плюс роль, понятная инструменту.</summary>
    [Serializable]
    public class FrameTrack
    {
        public string name = "Новая дорожка";
        public Color color = new Color(0.35f, 0.70f, 1f);
        public Actor actor = Actor.Attacker;
        public TrackRole role = TrackRole.None;
        public List<FrameSpan> spans = new List<FrameSpan>();

        public bool IsActiveAt(int frame)
        {
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].Contains(frame)) return true;
            return false;
        }
    }

    /// <summary>
    /// Приём: частота, длительность, позиции акторов и набор дорожек.
    /// Кадры нумеруются с 0. Активность — полуинтервал [start, end).
    /// </summary>
    [CreateAssetMenu(fileName = "FrameDateMove", menuName = "FrameDate/Move")]
    public class FrameDateMove : ScriptableObject
    {
        public const int MinFrameRate = 30;
        public const int MaxFrameRate = 60;

        [Tooltip("Отображаемое имя приёма.")]
        public string moveName = "New Move";

        [Tooltip("Частота: 30 или 60. По умолчанию 30 — базовая частота игровых таблиц.")]
        public int frameRate = 30;

        [Tooltip("Длительность приёма в кадрах. Кадры идут 0..duration-1.")]
        public int durationFrames = 40;

        [Tooltip("Позиция атакующего в рабочей плоскости X/Y.")]
        public Vector2 attackerPosition = new Vector2(-139.5f, 0f);

        [Tooltip("Позиция цели в рабочей плоскости X/Y.")]
        public Vector2 targetPosition = new Vector2(139.5f, 0f);

        [Tooltip("Метрики игры: единицы, арена, ширина Idle, камерные границы. Пусто — встроенные значения.")]
        public FrameDateMetrics metrics;

        public ActorBody attackerBody = new ActorBody();
        public ActorBody targetBody = new ActorBody();

        public List<FrameTrack> tracks = new List<FrameTrack>();

        public float FrameToSeconds(int frame) => frameRate > 0 ? (float)frame / frameRate : 0f;

        public int LastFrame => Mathf.Max(0, durationFrames - 1);

        public ActorBody Body(Actor actor) => actor == Actor.Attacker ? attackerBody : targetBody;

        /// <summary>
        /// «Техническая» дорожка описывает геометрию — движение и форму тела, а не интервалы состояний приёма.
        /// На таймлайне такие дорожки живут в отдельной группе «Техника».
        /// </summary>
        public static bool IsTechnicalRole(TrackRole role) => role == TrackRole.Movement || role == TrackRole.Hurtbox;

        public Vector2 StartPosition(Actor actor) => actor == Actor.Attacker ? attackerPosition : targetPosition;

        /// <summary>Назначенный конфиг метрик либо встроенные значения.</summary>
        public FrameDateMetrics Metrics => metrics != null ? metrics : FrameDateMetrics.Fallback;

        /// <summary>Полуширина пушбокса актора: своя, если включена, иначе ширина Idle из метрик.</summary>
        public float PushHalfWidth(Actor actor)
        {
            var body = Body(actor);
            return 0.5f * (body.overridePushWidth ? body.pushWidth : Metrics.idleBodyWidth);
        }

        /// <summary>
        /// Список проблем конфигурации. Пустой список означает, что всё корректно.
        /// Некорректные значения не исправляются молча — они показываются пользователю.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (frameRate != 30 && frameRate != 60)
                problems.Add($"Частота {frameRate} не поддерживается. Допустимы 30 или 60.");

            if (durationFrames <= 0)
                problems.Add($"Длительность {durationFrames} должна быть больше нуля.");

            if (!(attackerPosition.x < targetPosition.x))
                problems.Add("Атакующий должен стоять левее цели: инструмент считает, что атакующий смотрит вправо.");

            ValidateBody(problems, "Атакующий", attackerBody);
            ValidateBody(problems, "Цель", targetBody);

            var m = Metrics;
            problems.AddRange(m.Validate());
            if (attackerPosition.x < m.leftWallX || attackerPosition.x > m.rightWallX)
                problems.Add($"Атакующий стартует за стеной: X {attackerPosition.x} вне арены [{m.leftWallX}, {m.rightWallX}].");
            if (targetPosition.x < m.leftWallX || targetPosition.x > m.rightWallX)
                problems.Add($"Цель стартует за стеной: X {targetPosition.x} вне арены [{m.leftWallX}, {m.rightWallX}].");

            for (int t = 0; t < tracks.Count; t++)
            {
                var track = tracks[t];
                string label = string.IsNullOrEmpty(track.name) ? $"дорожка {t}" : $"«{track.name}»";

                for (int i = 0; i < track.spans.Count; i++)
                {
                    var span = track.spans[i];

                    if (span.startFrame < 0)
                        problems.Add($"{label}: начало {span.startFrame} меньше нуля. Кадры нумеруются с 0.");

                    if (span.endFrame <= span.startFrame)
                        problems.Add($"{label}: конец {span.endFrame} должен быть строго больше начала {span.startFrame}. Пустой интервал недопустим.");

                    if (span.endFrame > durationFrames)
                        problems.Add($"{label}: конец {span.endFrame} выходит за длительность приёма ({durationFrames} кадров).");

                    for (int j = i + 1; j < track.spans.Count; j++)
                    {
                        if (span.Overlaps(track.spans[j]))
                        {
                            problems.Add(
                                $"{label}: интервалы [{span.startFrame},{span.endFrame}) и " +
                                $"[{track.spans[j].startFrame},{track.spans[j].endFrame}) пересекаются. " +
                                "На одной дорожке интервалы пересекаться не могут.");
                        }
                    }

                    string at = $"{label} [{span.startFrame},{span.endFrame})";

                    if (track.role == TrackRole.Movement && (float.IsNaN(span.distance) || float.IsInfinity(span.distance)))
                        problems.Add($"{at}: расстояние движения не является числом.");

                    if (track.role == TrackRole.Hurtbox)
                    {
                        var body = Body(track.actor);
                        if (!(span.hurtHeightScale > 0f))
                            problems.Add($"{at}: доля высоты hurtbox {span.hurtHeightScale} должна быть больше нуля.");
                        if (!(span.hurtRadiusScale > 0f))
                            problems.Add($"{at}: доля радиуса hurtbox {span.hurtRadiusScale} должна быть больше нуля.");
                        if (body != null && body.height * span.hurtHeightScale < 2f * body.radius * span.hurtRadiusScale)
                            problems.Add($"{at}: hurtbox становится ниже двух своих радиусов — уменьшите и радиус.");
                    }

                    if (track.role == TrackRole.Attack && track.actor == Actor.Attacker)
                    {
                        if (!(span.hitRadius > 0f))
                            problems.Add($"{at}: радиус hitbox {span.hitRadius} должен быть больше нуля.");
                        if (!(span.hitLength >= 0f))
                            problems.Add($"{at}: длина hitbox {span.hitLength} не может быть отрицательной.");
                        if (!(span.pushDistance >= 0f))
                            problems.Add($"{at}: отталкивание {span.pushDistance} не может быть отрицательным.");
                        if (span.pushFrames < 1)
                            problems.Add($"{at}: отталкивание длится {span.pushFrames} кадров, нужно не меньше 1.");
                    }
                }
            }

            return problems;
        }

        private static void ValidateBody(List<string> problems, string who, ActorBody body)
        {
            if (body == null)
            {
                problems.Add($"{who}: нет параметров тела.");
                return;
            }
            if (!(body.radius > 0f))
                problems.Add($"{who}: радиус тела {body.radius} должен быть больше нуля.");
            if (!(body.height >= 2f * body.radius))
                problems.Add($"{who}: высота тела {body.height} меньше двух радиусов ({2f * body.radius}).");
            if (body.overridePushWidth && !(body.pushWidth > 0f))
                problems.Add($"{who}: своя ширина пушбокса {body.pushWidth} должна быть больше нуля.");
            if (!(body.mass > 0f))
                problems.Add($"{who}: масса {body.mass} должна быть больше нуля.");
        }

        /// <summary>
        /// Предзаполненный набор дорожек по состояниям игры, чтобы не начинать с пустого листа.
        /// Пользователь волен добавить, переименовать, перекрасить и удалить любую.
        /// </summary>
        public static List<FrameTrack> CreateDefaultTracks()
        {
            return new List<FrameTrack>
            {
                NewTrack("Attack",      new Color(1.00f, 0.35f, 0.30f), Actor.Attacker, TrackRole.Attack),
                NewTrack("Движение",    new Color(0.95f, 0.95f, 0.55f), Actor.Attacker, TrackRole.Movement),
                NewTrack("Uninterrupt", new Color(1.00f, 0.75f, 0.25f), Actor.Attacker, TrackRole.Uninterrupt),
                NewTrack("Blocking",    new Color(0.35f, 0.70f, 1.00f), Actor.Attacker, TrackRole.Blocking),
                NewTrack("Throwable",   new Color(0.75f, 0.55f, 1.00f), Actor.Attacker, TrackRole.Throwable),
                NewTrack("Blocking",    new Color(0.30f, 0.85f, 0.75f), Actor.Target,   TrackRole.Blocking),
                NewTrack("Throwable",   new Color(0.55f, 0.85f, 0.45f), Actor.Target,   TrackRole.Throwable),
                NewTrack("Uninterrupt", new Color(0.90f, 0.60f, 0.45f), Actor.Target,   TrackRole.Uninterrupt),
            };
        }

        private static FrameTrack NewTrack(string name, Color color, Actor actor, TrackRole role) =>
            new FrameTrack { name = name, color = color, actor = actor, role = role };
    }
}
