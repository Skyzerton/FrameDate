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

        private enum DragMode { None, Scrub, MoveSpan, ResizeStart, ResizeEnd, CreateSpan }

        private const string StyleSheetPath = "Assets/FrameDate/Editor/FrameDateTimeline.uss";
        public const string CursorResizeClass = "fd-cursor-resize";
        public const string CursorMoveClass = "fd-cursor-move";

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

        private Rect RowRect(int trackIndex)
        {
            float y = RulerHeight + trackIndex * (RowHeight + RowGap);
            return new Rect(GutterWidth, y, Mathf.Max(0f, contentRect.width - GutterWidth - RightPad), RowHeight);
        }

        private int RowAt(float y)
        {
            if (_move == null || y < RulerHeight) return -1;
            int index = Mathf.FloorToInt((y - RulerHeight) / (RowHeight + RowGap));
            return index >= 0 && index < _move.tracks.Count ? index : -1;
        }

        /// <summary>
        /// Строк на таймлайне: дорожки плюс одна вычисленная строка «Отталкивание» под ними.
        /// Вычисленная строка не попадает в RowAt, поэтому hit-test её не видит и мышью она не правится.
        /// </summary>
        private int RowCount => _move == null ? 0 : _move.tracks.Count + 1;

        public float PreferredHeight =>
            RulerHeight + RowCount * (RowHeight + RowGap) + 10f;

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

            for (int i = 0; i < _move.tracks.Count; i++)
            {
                var track = _move.tracks[i];
                var row = RowRect(i);

                var label = new Label($"{(track.actor == Actor.Attacker ? "А" : "Ц")}  {track.name}")
                {
                    tooltip = $"{(track.actor == Actor.Attacker ? "Атакующий" : "Цель")} · роль: {RoleLabel(track.role)}"
                };
                label.style.position = Position.Absolute;
                label.style.left = 8f;
                label.style.top = row.y + 3f;
                label.style.width = GutterWidth - 16f;
                label.style.fontSize = 11;
                label.style.overflow = Overflow.Hidden;
                label.style.color = track.color;
                label.pickingMode = PickingMode.Ignore;
                Add(label);
                _trackLabels.Add(label);
            }

            {
                var row = RowRect(_move.tracks.Count);
                var label = new Label("Ц  Отталкивание · расчёт");
                label.style.position = Position.Absolute;
                label.style.left = 8f;
                label.style.top = row.y + 3f;
                label.style.width = GutterWidth - 16f;
                label.style.fontSize = 11;
                label.style.overflow = Overflow.Hidden;
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
                label.style.color = DerivedColor;
                label.pickingMode = PickingMode.Ignore;
                Add(label);
                _trackLabels.Add(label);
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

        private static readonly Color DerivedColor = new Color(0.95f, 0.62f, 0.40f);

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
            float bottom = RulerHeight + RowCount * (RowHeight + RowGap);

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

            for (int i = 0; i < _move.tracks.Count; i++)
            {
                var track = _move.tracks[i];
                var row = RowRect(i);

                FillRect(painter, row, i % 2 == 0
                    ? new Color(0.22f, 0.23f, 0.25f)
                    : new Color(0.20f, 0.21f, 0.23f));

                for (int s = 0; s < track.spans.Count; s++)
                {
                    var span = track.spans[s];
                    float x0 = FrameToX(span.startFrame);
                    float x1 = FrameToX(span.endFrame);
                    var rect = new Rect(x0, row.y + 2f, Mathf.Max(2f, x1 - x0), row.height - 4f);

                    bool active = span.Contains(_currentFrame);
                    var fill = track.color;
                    fill.a = active ? 0.95f : 0.62f;
                    FillRect(painter, rect, fill);

                    bool selected = i == SelectedTrack && s == SelectedSpan;
                    if (selected) StrokeRect(painter, rect, Color.white, 2f);

                    // Захваты границ показываются только у выделенного интервала,
                    // чтобы не зашумлять дорожку.
                    if (selected)
                    {
                        FillRect(painter, new Rect(rect.x, rect.y, 2f, rect.height), Color.white);
                        FillRect(painter, new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), Color.white);
                    }
                }
            }

            // Вычисленное: контакты и отталкивание. Считается заново на каждую перерисовку,
            // поэтому при протяжке интервалов следует за данными без отдельной синхронизации.
            var sim = FrameDateSimulation.Run(_move);
            var derivedRow = RowRect(_move.tracks.Count);
            FillRect(painter, derivedRow, new Color(0.17f, 0.17f, 0.19f));

            for (int c = 0; c < sim.contacts.Count; c++)
            {
                var contact = sim.contacts[c];

                // Кадр контакта — ячейка [K, K+1) на интервале Attack, белая рамка и полоса снизу.
                var attackRow = RowRect(contact.trackIndex);
                float cx0 = FrameToX(contact.frame);
                float cx1 = FrameToX(contact.frame + 1);
                var cell = new Rect(cx0, attackRow.y + 2f, Mathf.Max(2f, cx1 - cx0), attackRow.height - 4f);
                StrokeRect(painter, cell, Color.white, 2f);
                FillRect(painter, new Rect(cell.x, cell.yMax - 4f, cell.width, 4f), Color.white);

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

            float playheadX = FrameToX(_currentFrame);
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
        private DragMode HitTest(Vector2 local, out int row, out int span)
        {
            row = -1;
            span = -1;
            if (_move == null) return DragMode.None;
            if (local.y < RulerHeight) return DragMode.Scrub;

            row = RowAt(local.y);
            if (row < 0) return DragMode.None;

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
                default:
                    return null;
            }
        }

        private void SetCursorClass(string cursorClass)
        {
            EnableInClassList(CursorResizeClass, cursorClass == CursorResizeClass);
            EnableInClassList(CursorMoveClass, cursorClass == CursorMoveClass);
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
            var hit = HitTest(local, out row, out s);

            switch (hit)
            {
                case DragMode.None:
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
