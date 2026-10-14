using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FrameDate.EditorTools
{
    /// <summary>
    /// Таймлайн: общая линейка кадров и дорожки под ней.
    /// Фигуры рисуются через painter2D, весь текст — настоящими Label'ами с абсолютным
    /// позиционированием, которое считает тот же код, что и фигуры. Поэтому подписи
    /// не могут разъехаться с тем, что нарисовано.
    /// </summary>
    public sealed class FrameDateTimelineView : VisualElement
    {
        public const float GutterWidth = 152f;
        public const float RulerHeight = 26f;
        public const float RowHeight = 24f;
        public const float RowGap = 2f;
        private const float RightPad = 14f;
        private const float EdgeGrab = 6f;

        private enum DragMode { None, Scrub, MoveSpan, ResizeStart, ResizeEnd, CreateSpan, AddTrack, SelectTrack, DeleteTrack }

        // Раскладка: разделы по акторам, в каждом группы «Интервалы» и «Техника».
        private enum RowKind { Section, Group, Track, Derived }

        private struct LayoutRow
        {
            public RowKind kind;
            public float y;
            public float height;
            public Actor actor;
            public bool technical;
            public int trackIndex;
        }

        public const float SectionHeight = 22f;
        public const float GroupHeight = 18f;
        private const float PlusWidth = 22f;

        private static readonly Color AttackerAccent = new Color(1.00f, 0.50f, 0.40f);
        private static readonly Color TargetAccent = new Color(0.40f, 0.85f, 0.75f);

        private const string StyleSheetPath = "Assets/FrameDate/Editor/FrameDateTimeline.uss";
        public const string CursorResizeClass = "fd-cursor-resize";
        public const string CursorMoveClass = "fd-cursor-move";
        public const string CursorLinkClass = "fd-cursor-link";

        private FrameDateMove _move;
        private int _currentFrame;

        private DragMode _drag = DragMode.None;
        private int _dragGrabOffset;
        private int _dragAnchorFrame;

        private readonly List<Label> _trackLabels = new List<Label>();
        private readonly List<Label> _rulerLabels = new List<Label>();

        public int SelectedTrack { get; private set; } = -1;
        public int SelectedSpan { get; private set; } = -1;

        public event Action<int> FrameScrubbed;
        public event Action DataChanged;
        public event Action SelectionChanged;
        /// <summary>Нажат «+» в заголовке группы: актор и техническая ли группа.</summary>
        public event Action<Actor, bool> AddTrackRequested;
        /// <summary>Нажат «×» у дорожки: индекс дорожки в данных.</summary>
        public event Action<int> DeleteTrackRequested;

        public FrameDateTimelineView()
        {
            focusable = true;
            style.flexGrow = 1f;
            style.minHeight = 120f;

            generateVisualContent += OnGenerateVisualContent;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<GeometryChangedEvent>(_ => RebuildLabels());
            RegisterCallback<PointerLeaveEvent>(_ => { if (_drag == DragMode.None) SetCursorClass(null); });

            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null) styleSheets.Add(sheet);
            else Debug.LogWarning("FrameDate: не найден стиль таймлайна " + StyleSheetPath);
        }

        public FrameDateMove Move
        {
            get => _move;
            set
            {
                _move = value;
                SelectedTrack = -1;
                SelectedSpan = -1;
                RebuildLabels();
                MarkDirtyRepaint();
            }
        }

        public int CurrentFrame
        {
            get => _currentFrame;
            set
            {
                int clamped = _move == null ? 0 : Mathf.Clamp(value, 0, Mathf.Max(0, _move.durationFrames - 1));
                if (clamped == _currentFrame) return;
                _currentFrame = clamped;
                MarkDirtyRepaint();
            }
        }

        public void Select(int trackIndex, int spanIndex)
        {
            SelectedTrack = trackIndex;
            SelectedSpan = spanIndex;
            SelectionChanged?.Invoke();
            MarkDirtyRepaint();
        }

        public FrameSpan SelectedSpanOrNull()
        {
            if (_move == null) return null;
            if (SelectedTrack < 0 || SelectedTrack >= _move.tracks.Count) return null;
            var spans = _move.tracks[SelectedTrack].spans;
            if (SelectedSpan < 0 || SelectedSpan >= spans.Count) return null;
            return spans[SelectedSpan];
        }

        // ------------------------------------------------------------------
        // Геометрия
        // ------------------------------------------------------------------

        private float Duration => _move == null ? 1f : Mathf.Max(1, _move.durationFrames);

        private float PxPerFrame
        {
            get
            {
                float usable = Mathf.Max(40f, contentRect.width - GutterWidth - RightPad);
                return usable / Duration;
            }
        }

        private float FrameToX(float frame) => GutterWidth + frame * PxPerFrame;

        private int XToFrame(float x) =>
            Mathf.Clamp(Mathf.RoundToInt((x - GutterWidth) / PxPerFrame), 0, (int)Duration);

/// <summary>
        /// Раскладка строк сверху вниз: Атакующий → Интервалы, Техника; Цель → Интервалы, Техника
        /// и под ней вычисленное отталкивание. Внутри группы — порядок дорожек в данных.
        /// Группа дорожки определяется её актором и ролью, поэтому смена роли или актора
        /// сама переносит дорожку. Возвращает новый список: безопасно вызывать внутри обхода.
        /// </summary>
        private List<LayoutRow> Layout()
        {
            var rows = new List<LayoutRow>();
            if (_move == null) return rows;

            float y = RulerHeight;
            for (int a = 0; a < 2; a++)
            {
                var actor = a == 0 ? Actor.Attacker : Actor.Target;
                y = AddRow(rows, RowKind.Section, y, SectionHeight, actor, false, -1);

                for (int g = 0; g < 2; g++)
                {
                    bool technical = g == 1;
                    y = AddRow(rows, RowKind.Group, y, GroupHeight, actor, technical, -1);

                    for (int t = 0; t < _move.tracks.Count; t++)
                    {
                        var track = _move.tracks[t];
                        if (track.actor != actor || FrameDateMove.IsTechnicalRole(track.role) != technical) continue;
                        y = AddRow(rows, RowKind.Track, y, RowHeight, actor, technical, t);
                    }

                    if (actor == Actor.Target && technical)
                        y = AddRow(rows, RowKind.Derived, y, RowHeight, actor, true, -1);
                }
            }
            return rows;
        }

        private static float AddRow(List<LayoutRow> rows, RowKind kind, float y, float height, Actor actor, bool technical, int trackIndex)
        {
            rows.Add(new LayoutRow { kind = kind, y = y, height = height, actor = actor, technical = technical, trackIndex = trackIndex });
            return y + height + RowGap;
        }

        private float TimelineWidth => Mathf.Max(0f, contentRect.width - GutterWidth - RightPad);

        private static Rect TimelineRect(LayoutRow row, float width) => new Rect(GutterWidth, row.y, width, row.height);

        private Rect RowRect(int trackIndex)
        {
            foreach (var row in Layout())
                if (row.kind == RowKind.Track && row.trackIndex == trackIndex)
                    return TimelineRect(row, TimelineWidth);
            return new Rect(GutterWidth, -1000f, TimelineWidth, RowHeight);
        }

        private bool RowInfoAt(float y, out LayoutRow found)
        {
            foreach (var row in Layout())
            {
                if (y >= row.y && y < row.y + row.height)
                {
                    found = row;
                    return true;
                }
            }
            found = default(LayoutRow);
            return false;
        }

private int RowAt(float y)
        {
            LayoutRow row;
            return RowInfoAt(y, out row) && row.kind == RowKind.Track ? row.trackIndex : -1;
        }

        private static float BottomOf(List<LayoutRow> rows) =>
            rows.Count == 0 ? RulerHeight : rows[rows.Count - 1].y + rows[rows.Count - 1].height;

        public float PreferredHeight => BottomOf(Layout()) + 12f;

        // ------------------------------------------------------------------
        // Подписи
        // ------------------------------------------------------------------

private void RebuildLabels()
        {
            foreach (var label in _trackLabels) label.RemoveFromHierarchy();
            foreach (var label in _rulerLabels) label.RemoveFromHierarchy();
            _trackLabels.Clear();
            _rulerLabels.Clear();

            if (_move == null || contentRect.width <= 0f) return;

            var layout = Layout();
            for (int i = 0; i < layout.Count; i++)
            {
                var row = layout[i];
                switch (row.kind)
                {
                    case RowKind.Section:
                    {
                        var label = GutterLabel(row.actor == Actor.Attacker ? "АТАКУЮЩИЙ" : "ЦЕЛЬ", row, 10f, 3f, 11);
                        label.style.unityFontStyleAndWeight = FontStyle.Bold;
                        label.style.color = row.actor == Actor.Attacker ? AttackerAccent : TargetAccent;
                        break;
                    }

                    case RowKind.Group:
                    {
                        var label = GutterLabel(row.technical ? "Техника" : "Интервалы", row, 16f, 1f, 10);
                        label.style.color = new Color(0.70f, 0.70f, 0.74f);

                        var plus = GutterLabel("+", row, GutterWidth - PlusWidth, 0f, 13);
                        plus.style.width = PlusWidth;
                        plus.style.unityTextAlign = TextAnchor.MiddleCenter;
                        plus.style.color = new Color(0.85f, 0.85f, 0.88f);

                        // Пустая группа подсказывает, как её наполнить.
                        bool empty = i + 1 >= layout.Count ||
                                     (layout[i + 1].kind != RowKind.Track && layout[i + 1].kind != RowKind.Derived);
                        if (empty)
                        {
                            var hint = GutterLabel(row.technical
                                    ? "нет дорожек · «+» добавит Movement; Hurtbox — сменой роли"
                                    : "нет дорожек · «+» слева добавит",
                                row, GutterWidth + 6f, 1f, 10);
                            hint.style.width = Mathf.Max(0f, TimelineWidth - 12f);
                            hint.style.color = new Color(0.50f, 0.50f, 0.54f);
                        }
                        break;
                    }

                    case RowKind.Track:
                    {
                        var track = _move.tracks[row.trackIndex];
                        var label = GutterLabel(track.name, row, 24f, 3f, 11);
                        label.style.width = GutterWidth - PlusWidth - 24f;
                        label.style.color = track.color;
                        label.tooltip = "Роль: " + RoleLabel(track.role);

                        var cross = GutterLabel("×", row, GutterWidth - PlusWidth, 0f, 13);
                        cross.style.width = PlusWidth;
                        cross.style.unityTextAlign = TextAnchor.MiddleCenter;
                        cross.style.color = new Color(0.62f, 0.62f, 0.66f);
                        break;
                    }

                    case RowKind.Derived:
                    {
                        var label = GutterLabel("Реакция цели · расчёт", row, 24f, 3f, 11);
                        label.style.unityFontStyleAndWeight = FontStyle.Italic;
                        label.style.color = DerivedColor;

                        // Имена реакций цели внутри их интервалов.
                        var reactionSim = FrameDateSimulation.Run(_move);
                        foreach (var play in reactionSim.reactions)
                        {
                            int end;
                            if (!ReactionEnd(play, out end)) continue;
                            float x0 = FrameToX(play.startFrame);
                            float x1 = FrameToX(end);
                            if (x1 - x0 < 36f) continue;
                            var name = new Label(play.name);
                            name.style.position = Position.Absolute;
                            name.style.left = x0 + 3f;
                            name.style.top = row.y + 4f;
                            name.style.width = x1 - x0 - 6f;
                            name.style.height = row.height - 6f;
                            name.style.fontSize = 9;
                            name.style.overflow = Overflow.Hidden;
                            name.style.color = new Color(0.08f, 0.08f, 0.10f);
                            name.pickingMode = PickingMode.Ignore;
                            Add(name);
                            _trackLabels.Add(name);
                        }
                        break;
                    }
                }
            }

            int step = ChooseRulerStep();
            for (int frame = 0; frame <= Duration; frame += step)
            {
                var label = new Label(frame.ToString());
                label.style.position = Position.Absolute;
                label.style.left = FrameToX(frame) + 3f;
                label.style.top = 4f;
                label.style.fontSize = 10;
                label.style.color = new Color(0.62f, 0.62f, 0.66f);
                label.pickingMode = PickingMode.Ignore;
                Add(label);
                _rulerLabels.Add(label);
            }
        }

        /// <summary>Подпись строки раскладки. PickingMode.Ignore — курсоры и клики проходят к таймлайну.</summary>
        private Label GutterLabel(string text, LayoutRow row, float left, float topPad, int fontSize)
        {
            var label = new Label(text);
            label.style.position = Position.Absolute;
            label.style.left = left;
            label.style.top = row.y + topPad;
            label.style.height = row.height - topPad;
            label.style.width = GutterWidth - left - 4f;
            label.style.fontSize = fontSize;
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.paddingTop = 0f;
            label.style.paddingBottom = 0f;
            label.pickingMode = PickingMode.Ignore;
            Add(label);
            _trackLabels.Add(label);
            return label;
        }

        private static readonly Color DerivedColor = new Color(0.95f, 0.62f, 0.40f);

        private static Color ReactionColor(FrameDateSimulation.ReactionKind kind)
        {
            switch (kind)
            {
                case FrameDateSimulation.ReactionKind.Crit: return new Color(1.00f, 0.38f, 0.38f);
                case FrameDateSimulation.ReactionKind.BlockIn: return new Color(0.55f, 0.80f, 1.00f);
                case FrameDateSimulation.ReactionKind.BlockHit: return new Color(0.35f, 0.55f, 1.00f);
                default: return DerivedColor;
            }
        }

        /// <summary>Конец показа реакции на таймлайне: конец анимации, обрыв новой реакцией или конец приёма.</summary>
        private bool ReactionEnd(FrameDateSimulation.ReactionPlay play, out int end)
        {
            end = 0;
            var reaction = _move.reactions != null ? _move.reactions.Find(play.name) : null;
            if (reaction == null) return false;
            end = Mathf.Min(Mathf.Min(play.endFrame, play.startFrame + reaction.frameCount), (int)Duration);
            return end > play.startFrame;
        }

        private int ChooseRulerStep()
        {
            foreach (int candidate in new[] { 1, 2, 5, 10, 20, 30, 60, 120 })
                if (candidate * PxPerFrame >= 34f) return candidate;
            return 240;
        }

        public static string RoleLabel(TrackRole role) => role == TrackRole.None ? "нет" : role.ToString();

        // ------------------------------------------------------------------
        // Отрисовка
        // ------------------------------------------------------------------

private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            if (_move == null) return;

            var painter = context.painter2D;
            float width = contentRect.width;
            float timelineWidth = TimelineWidth;
            var layout = Layout();
            float bottom = BottomOf(layout);

            FillRect(painter, new Rect(0f, 0f, width, RulerHeight), new Color(0.19f, 0.20f, 0.22f));
            FillRect(painter, new Rect(0f, RulerHeight, GutterWidth, bottom - RulerHeight), new Color(0.17f, 0.18f, 0.20f));

            int step = ChooseRulerStep();
            int minor = Mathf.Max(1, step / 5);
            for (int frame = 0; frame <= Duration; frame += minor)
            {
                float x = FrameToX(frame);
                bool major = frame % step == 0;
                FillRect(painter,
                    new Rect(x, major ? 2f : RulerHeight - 7f, 1f, major ? RulerHeight - 2f : 5f),
                    major ? new Color(0.45f, 0.46f, 0.50f) : new Color(0.32f, 0.33f, 0.36f));

                if (major && frame < Duration)
                    FillRect(painter, new Rect(x, RulerHeight, 1f, bottom - RulerHeight), new Color(1f, 1f, 1f, 0.05f));
            }

            var derivedRow = new Rect(GutterWidth, -1000f, timelineWidth, RowHeight);
            int stripe = 0;

            foreach (var row in layout)
            {
                switch (row.kind)
                {
                    case RowKind.Section:
                        FillRect(painter, new Rect(0f, row.y, width, row.height), new Color(0.12f, 0.13f, 0.15f));
                        FillRect(painter, new Rect(0f, row.y, 4f, row.height),
                            row.actor == Actor.Attacker ? AttackerAccent : TargetAccent);
                        break;

                    case RowKind.Group:
                        stripe = 0;
                        FillRect(painter, new Rect(0f, row.y, width, row.height), new Color(0.155f, 0.16f, 0.18f));
                        FillRect(painter, new Rect(0f, row.y, 4f, row.height),
                            (row.actor == Actor.Attacker ? AttackerAccent : TargetAccent) * new Color(1f, 1f, 1f, 0.45f));
                        FillRect(painter, new Rect(GutterWidth - PlusWidth + 3f, row.y + 2f, PlusWidth - 6f, row.height - 4f),
                            new Color(0.27f, 0.28f, 0.31f));
                        break;

                    case RowKind.Derived:
                        derivedRow = TimelineRect(row, timelineWidth);
                        FillRect(painter, derivedRow, new Color(0.17f, 0.17f, 0.19f));
                        break;

                    case RowKind.Track:
                    {
                        int i = row.trackIndex;
                        var track = _move.tracks[i];
                        var rowRect = TimelineRect(row, timelineWidth);

                        // Выделенная дорожка подсвечивается в левой колонке и тонкой рамкой по строке.
                        if (i == SelectedTrack)
                        {
                            FillRect(painter, new Rect(4f, row.y, GutterWidth - 4f, row.height), new Color(0.30f, 0.33f, 0.40f));
                            FillRect(painter, new Rect(4f, row.y, 3f, row.height), track.color);
                        }

                        FillRect(painter, rowRect, stripe++ % 2 == 0
                            ? new Color(0.22f, 0.23f, 0.25f)
                            : new Color(0.20f, 0.21f, 0.23f));

                        for (int s = 0; s < track.spans.Count; s++)
                        {
                            var span = track.spans[s];
                            float x0 = FrameToX(span.startFrame);
                            float x1 = FrameToX(span.endFrame);
                            var rect = new Rect(x0, rowRect.y + 2f, Mathf.Max(2f, x1 - x0), rowRect.height - 4f);

                            bool active = span.Contains(_currentFrame);
                            var fill = track.color;
                            fill.a = active ? 0.95f : 0.62f;
                            FillRect(painter, rect, fill);

                            bool selected = i == SelectedTrack && s == SelectedSpan;
                            if (selected)
                            {
                                StrokeRect(painter, rect, Color.white, 2f);
                                // Захваты границ показываются только у выделенного интервала.
                                FillRect(painter, new Rect(rect.x, rect.y, 2f, rect.height), Color.white);
                                FillRect(painter, new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), Color.white);
                            }
                        }
                        break;
                    }
                }
            }

            // Вычисленное: контакты и отталкивание. Считается заново на каждую перерисовку,
            // поэтому при протяжке интервалов следует за данными без отдельной синхронизации.
            var sim = FrameDateSimulation.Run(_move);
            for (int c = 0; c < sim.contacts.Count; c++)
            {
                var contact = sim.contacts[c];

                // Кадр контакта — ячейка [K, K+1) на интервале Attack, белая рамка и полоса снизу.
                Rect attackRow = derivedRow;
                bool found = false;
                foreach (var row in layout)
                {
                    if (row.kind != RowKind.Track || row.trackIndex != contact.trackIndex) continue;
                    attackRow = TimelineRect(row, timelineWidth);
                    found = true;
                    break;
                }

                if (found)
                {
                    float cx0 = FrameToX(contact.frame);
                    float cx1 = FrameToX(contact.frame + 1);
                    var cell = new Rect(cx0, attackRow.y + 2f, Mathf.Max(2f, cx1 - cx0), attackRow.height - 4f);
                    StrokeRect(painter, cell, Color.white, 2f);
                    FillRect(painter, new Rect(cell.x, cell.yMax - 4f, cell.width, 4f), Color.white);
                }

                // С реакцией из каталога цель сдвигает её анимация — её рисует блок реакций ниже.
                if (!string.IsNullOrEmpty(contact.reaction)) continue;
                float px0 = FrameToX(contact.frame);
                float px1 = FrameToX(Mathf.Min(contact.pushEndFrame, (int)Duration));
                if (px1 <= px0) continue;
                var push = new Rect(px0, derivedRow.y + 2f, px1 - px0, derivedRow.height - 4f);
                bool pushActive = _currentFrame >= contact.frame && _currentFrame < contact.pushEndFrame;
                var pushFill = DerivedColor;
                pushFill.a = pushActive ? 0.85f : 0.40f;
                FillRect(painter, push, pushFill);
                StrokeRect(painter, push, DerivedColor, 1f);
            }

            // Реакции цели: интервал от начала до конца анимации или до обрыва новой реакцией, цвет по виду.
            foreach (var play in sim.reactions)
            {
                int end;
                if (!ReactionEnd(play, out end)) continue;
                float rx0 = FrameToX(play.startFrame);
                float rx1 = FrameToX(end);
                if (rx1 <= rx0) continue;
                var reactionRect = new Rect(rx0, derivedRow.y + 2f, rx1 - rx0, derivedRow.height - 4f);
                var fill = ReactionColor(play.kind);
                fill.a = _currentFrame >= play.startFrame && _currentFrame < end ? 0.95f : 0.55f;
                FillRect(painter, reactionRect, fill);
                StrokeRect(painter, reactionRect, ReactionColor(play.kind), 1f);
            }

            float playheadX = FrameToX(_currentFrame);
            if (SelectedTrack >= 0)
            {
                foreach (var row in layout)
                {
                    if (row.kind != RowKind.Track || row.trackIndex != SelectedTrack) continue;
                    StrokeRect(painter, TimelineRect(row, timelineWidth), new Color(0.75f, 0.80f, 0.95f, 0.55f), 1f);
                }
            }

            FillRect(painter, new Rect(playheadX - 1f, 0f, 2f, bottom), new Color(1f, 0.92f, 0.3f, 0.95f));
            FillRect(painter, new Rect(playheadX - 5f, 0f, 10f, 5f), new Color(1f, 0.92f, 0.3f));
        }

        private static void FillRect(Painter2D painter, Rect rect, Color color)
        {
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Fill();
        }

        private static void StrokeRect(Painter2D painter, Rect rect, Color color, float lineWidth)
        {
            painter.strokeColor = color;
            painter.lineWidth = lineWidth;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Stroke();
        }

        // ------------------------------------------------------------------
        // Ввод
        // ------------------------------------------------------------------

/// <summary>
        /// Единственный hit-test таймлайна. Его используют и клик, и наведение,
        /// поэтому курсор никогда не обещает не то, что сделает клик.
        /// None — мимо всех строк; Scrub — линейка; CreateSpan — пустое место дорожки.
        /// </summary>
/// <summary>
        /// Единственный hit-test таймлайна. Его используют и клик, и наведение,
        /// поэтому курсор никогда не обещает не то, что сделает клик.
        /// None — заголовок раздела, вычисленная строка или зазор; Scrub — линейка;
        /// AddTrack — «+» в заголовке группы; CreateSpan — пустое место дорожки.
        /// </summary>
        private DragMode HitTest(Vector2 local, out int row, out int span)
        {
            LayoutRow info;
            return HitTest(local, out row, out span, out info);
        }

        private DragMode HitTest(Vector2 local, out int row, out int span, out LayoutRow info)
        {
            row = -1;
            span = -1;
            info = default(LayoutRow);
            if (_move == null) return DragMode.None;
            if (local.y < RulerHeight) return DragMode.Scrub;

            if (!RowInfoAt(local.y, out info)) return DragMode.None;

            if (info.kind == RowKind.Group)
                return local.x >= GutterWidth - PlusWidth && local.x < GutterWidth ? DragMode.AddTrack : DragMode.None;

            if (info.kind != RowKind.Track) return DragMode.None;

            row = info.trackIndex;

            // Левая колонка дорожки — выбор дорожки, «×» справа в ней — удаление.
            // Интервалы здесь не создаются: клик по имени раньше заводил интервал на кадре 0.
            if (local.x < GutterWidth)
                return local.x >= GutterWidth - PlusWidth ? DragMode.DeleteTrack : DragMode.SelectTrack;
            var track = _move.tracks[row];
            for (int s = 0; s < track.spans.Count; s++)
            {
                var candidate = track.spans[s];
                float x0 = FrameToX(candidate.startFrame);
                float x1 = FrameToX(candidate.endFrame);
                if (local.x < x0 - EdgeGrab || local.x > x1 + EdgeGrab) continue;

                span = s;
                if (Mathf.Abs(local.x - x0) <= EdgeGrab) return DragMode.ResizeStart;
                if (Mathf.Abs(local.x - x1) <= EdgeGrab) return DragMode.ResizeEnd;
                return DragMode.MoveSpan;
            }

            return DragMode.CreateSpan;
        }

private static string CursorClassFor(DragMode mode)
        {
            switch (mode)
            {
                case DragMode.ResizeStart:
                case DragMode.ResizeEnd:
                    return CursorResizeClass;
                case DragMode.MoveSpan:
                    return CursorMoveClass;
                case DragMode.AddTrack:
                case DragMode.DeleteTrack:
                    return CursorLinkClass;
                default:
                    return null;
            }
        }

private void SetCursorClass(string cursorClass)
        {
            EnableInClassList(CursorResizeClass, cursorClass == CursorResizeClass);
            EnableInClassList(CursorMoveClass, cursorClass == CursorMoveClass);
            EnableInClassList(CursorLinkClass, cursorClass == CursorLinkClass);
        }

        private void UpdateHoverCursor(Vector2 local)
        {
            int row, span;
            SetCursorClass(CursorClassFor(HitTest(local, out row, out span)));
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_move == null || evt.button != 0) return;
            Focus();

            var local = (Vector2)evt.localPosition;
            int row, s;
            LayoutRow info;
            var hit = HitTest(local, out row, out s, out info);

            switch (hit)
            {
                case DragMode.None:
                    return;

                case DragMode.AddTrack:
                    AddTrackRequested?.Invoke(info.actor, info.technical);
                    evt.StopPropagation();
                    return;

                case DragMode.SelectTrack:
                    Select(row, -1);
                    evt.StopPropagation();
                    return;

                case DragMode.DeleteTrack:
                    DeleteTrackRequested?.Invoke(row);
                    evt.StopPropagation();
                    return;

                case DragMode.Scrub:
                    _drag = DragMode.Scrub;
                    SetCursorClass(null);
                    this.CapturePointer(evt.pointerId);
                    ScrubTo(local.x);
                    evt.StopPropagation();
                    return;

                case DragMode.ResizeStart:
                case DragMode.ResizeEnd:
                case DragMode.MoveSpan:
                {
                    var span = _move.tracks[row].spans[s];
                    Select(row, s);
                    // Новая группа на каждый жест: одна протяжка = один шаг Undo,
                    // но два разных жеста не сливаются в один, даже если попали в один кадр.
                    Undo.IncrementCurrentGroup();
                    Undo.RecordObject(_move, "FrameDate: правка интервала");

                    _drag = hit;
                    if (hit == DragMode.MoveSpan)
                        _dragGrabOffset = XToFrame(local.x) - span.startFrame;

                    // Во время жеста курсор держится по режиму, даже если мышь ушла с края.
                    SetCursorClass(CursorClassFor(hit));
                    this.CapturePointer(evt.pointerId);
                    evt.StopPropagation();
                    return;
                }
            }

            // Пустое место дорожки — протягиваем новый интервал.
            var track = _move.tracks[row];
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_move, "FrameDate: новый интервал");
            _dragAnchorFrame = XToFrame(local.x);
            int start = Mathf.Clamp(_dragAnchorFrame, 0, Mathf.Max(0, _move.durationFrames - 1));
            var created = new FrameSpan(start, start + 1);
            track.spans.Add(created);
            Select(row, track.spans.Count - 1);
            _drag = DragMode.CreateSpan;
            // Протяжка нового интервала тянет его конец — курсор растягивания.
            SetCursorClass(CursorResizeClass);
            this.CapturePointer(evt.pointerId);
            MarkData();
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_move == null) return;

            var local = evt.localPosition;

            if (_drag == DragMode.None)
            {
                UpdateHoverCursor(local);
                return;
            }

            if (_drag == DragMode.Scrub)
            {
                ScrubTo(local.x);
                evt.StopPropagation();
                return;
            }

            var span = SelectedSpanOrNull();
            if (span == null) return;

            int frame = XToFrame(local.x);
            int max = _move.durationFrames;

            switch (_drag)
            {
                case DragMode.ResizeStart:
                    span.startFrame = Mathf.Clamp(frame, 0, span.endFrame - 1);
                    break;

                case DragMode.ResizeEnd:
                    span.endFrame = Mathf.Clamp(frame, span.startFrame + 1, max);
                    break;

                case DragMode.MoveSpan:
                {
                    int length = span.Length;
                    int start = Mathf.Clamp(frame - _dragGrabOffset, 0, max - length);
                    span.startFrame = start;
                    span.endFrame = start + length;
                    break;
                }

                case DragMode.CreateSpan:
                {
                    int a = Mathf.Min(_dragAnchorFrame, frame);
                    int b = Mathf.Max(_dragAnchorFrame, frame);
                    if (b <= a) b = a + 1;
                    span.startFrame = Mathf.Clamp(a, 0, max - 1);
                    span.endFrame = Mathf.Clamp(b, span.startFrame + 1, max);
                    break;
                }
            }

            MarkData();
            evt.StopPropagation();
        }

private void OnPointerUp(PointerUpEvent evt)
        {
            if (_drag == DragMode.None) return;
            _drag = DragMode.None;
            this.ReleasePointer(evt.pointerId);
            MarkData();
            // Жест закончен — курсор снова по тому, что под мышью сейчас.
            UpdateHoverCursor(evt.localPosition);
            evt.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Delete && evt.keyCode != KeyCode.Backspace) return;
            if (DeleteSelectedSpan()) evt.StopPropagation();
        }

        public bool DeleteSelectedSpan()
        {
            if (_move == null) return false;
            var span = SelectedSpanOrNull();
            if (span == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_move, "FrameDate: удалить интервал");
            _move.tracks[SelectedTrack].spans.RemoveAt(SelectedSpan);
            Select(SelectedTrack, -1);
            MarkData();
            return true;
        }

        private void ScrubTo(float x)
        {
            int frame = Mathf.Clamp(XToFrame(x), 0, Mathf.Max(0, _move.durationFrames - 1));
            CurrentFrame = frame;
            FrameScrubbed?.Invoke(frame);
        }

        private void MarkData()
        {
            if (_move != null) EditorUtility.SetDirty(_move);
            DataChanged?.Invoke();
            MarkDirtyRepaint();
        }

        public void Refresh()
        {
            RebuildLabels();
            MarkDirtyRepaint();
        }
    }
}
