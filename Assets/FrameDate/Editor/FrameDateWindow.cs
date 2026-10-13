using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FrameDate.EditorTools
{
    /// <summary>
    /// Окно FrameDate. Самодостаточно: вьюпорт, таймлайн, транспорт и редактор дорожек
    /// живут здесь. Scene View, окно Animation, AnimationClip и Animator не участвуют.
    /// Часы воспроизведения тоже свои — вторых часов в инструменте нет.
    /// </summary>
    public sealed class FrameDateWindow : EditorWindow
    {
        private enum LoopMode { Off, WholeMove, SelectedSpan }

        [SerializeField] private FrameDateMove _move;
        [SerializeField] private int _frame;
        [SerializeField] private float _speed = 1f;
        [SerializeField] private LoopMode _loop = LoopMode.WholeMove;

        private bool _playing;
        private double _lastTime;
        private double _frameAccumulator;

        private FrameDatePreview _preview;
        private FrameDateTimelineView _timeline;
        private IMGUIContainer _viewport;
        private ScrollView _timelineScroll;
        private VisualElement _inspector;
        private Label _problems;
        private Button _playButton;
        
        // Поля выделенного интервала держим под рукой, чтобы обновлять их при правке мышью.
        private IntegerField _spanStartField;
        private IntegerField _spanEndField;
        private Label _contactReadout;
        private ObjectField _moveField;
        private const string ShowSkeletonPref = "FrameDate.ShowSkeleton";
private Label _frameReadout;

        [MenuItem("Tools/FrameDate")]
        public static void Open()
        {
            var window = GetWindow<FrameDateWindow>("FrameDate");
            window.minSize = new Vector2(880f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            _preview = new FrameDatePreview { ShowSkeleton = EditorPrefs.GetBool(ShowSkeletonPref, true) };
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            _lastTime = EditorApplication.timeSinceStartup;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            _preview?.Dispose();
            _preview = null;
        }

        // ------------------------------------------------------------------
        // Построение интерфейса
        // ------------------------------------------------------------------

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            root.Add(BuildToolbar());

            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1f;
            root.Add(body);

            var left = new VisualElement();
            left.style.flexDirection = FlexDirection.Column;
            left.style.flexGrow = 1f;
            body.Add(left);

            _viewport = new IMGUIContainer(DrawViewport);
            _viewport.style.flexGrow = 1f;
            _viewport.style.minHeight = 200f;
            left.Add(_viewport);

            left.Add(BuildTransport());

            _timelineScroll = new ScrollView(ScrollViewMode.Vertical);
            _timelineScroll.style.minHeight = 150f;
            _timelineScroll.style.maxHeight = 420f;

            _timeline = new FrameDateTimelineView();
            _timeline.FrameScrubbed += frame =>
            {
                _playing = false;
                UpdatePlayButton();
                SetFrame(frame);
            };
            _timeline.DataChanged += () => { SyncSpanFields(); RefreshProblems(); _viewport.MarkDirtyRepaint(); };
            _timeline.SelectionChanged += RebuildInspector;
            _timeline.AddTrackRequested += AddTrack;
            _timeline.DeleteTrackRequested += DeleteTrack;
            _timelineScroll.Add(_timeline);
            left.Add(_timelineScroll);

            _problems = new Label();
            _problems.style.whiteSpace = WhiteSpace.Normal;
            _problems.style.color = new Color(1f, 0.55f, 0.45f);
            _problems.style.paddingLeft = 8f;
            _problems.style.paddingRight = 8f;
            _problems.style.paddingTop = 4f;
            _problems.style.paddingBottom = 4f;
            left.Add(_problems);

            _inspector = new VisualElement();
            _inspector.style.width = 300f;
            _inspector.style.borderLeftWidth = 1f;
            _inspector.style.borderLeftColor = new Color(0f, 0f, 0f, 0.35f);
            _inspector.style.paddingLeft = 8f;
            _inspector.style.paddingRight = 8f;
            _inspector.style.paddingTop = 6f;
            // Инспектор с параметрами тел, hitbox и движения длиннее окна — нужна прокрутка.
            var inspectorScroll = new ScrollView(ScrollViewMode.Vertical);
            inspectorScroll.style.width = 316f;
            inspectorScroll.style.flexShrink = 0f;
            inspectorScroll.Add(_inspector);
            body.Add(inspectorScroll);

            BindMove(_move);
        }

        private VisualElement BuildToolbar()
        {
            var bar = new Toolbar();

            var moveField = new ObjectField("Приём")
            {
                objectType = typeof(FrameDateMove),
                allowSceneObjects = false,
                value = _move
            };
            moveField.style.width = 320f;
            _moveField = moveField;
            moveField.RegisterValueChangedCallback(evt => BindMove(evt.newValue as FrameDateMove));
            bar.Add(moveField);

            var create = new ToolbarButton(CreateMoveInteractive) { text = "Создать приём..." };
            bar.Add(create);

            var import = new ToolbarButton(ImportPackageInteractive) { text = "Импорт из игры..." };
            import.tooltip = "Пакет *.fdmove.json из экспортёра игры (SFA → FrameDate → Экспорт приёма в FrameDate).";
            bar.Add(import);

            bar.Add(new ToolbarSpacer { style = { flexGrow = 1f } });

            _frameReadout = new Label(" ");
            _frameReadout.style.unityTextAlign = TextAnchor.MiddleRight;
            _frameReadout.style.marginRight = 8f;
            bar.Add(_frameReadout);

            return bar;
        }

        private VisualElement BuildTransport()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 6f;
            row.style.paddingTop = 4f;
            row.style.paddingBottom = 4f;

            row.Add(new Button(() => StepFrame(-1)) { text = "◀ кадр" });

            _playButton = new Button(TogglePlay) { text = "▶ Играть" };
            _playButton.style.width = 90f;
            row.Add(_playButton);

            row.Add(new Button(() => StepFrame(1)) { text = "кадр ▶" });

            var speedLabel = new Label("Скорость");
            speedLabel.style.marginLeft = 12f;
            row.Add(speedLabel);

            var speed = new Slider(0.1f, 1f) { value = _speed };
            speed.style.width = 130f;
            var speedValue = new Label($"{_speed:0.00}×");
            speedValue.style.width = 46f;
            speed.RegisterValueChangedCallback(evt =>
            {
                _speed = evt.newValue;
                speedValue.text = $"{_speed:0.00}×";
            });
            row.Add(speed);
            row.Add(speedValue);

            var loop = new EnumField("Луп", _loop);
            loop.style.marginLeft = 12f;
            loop.style.width = 220f;
            loop.RegisterValueChangedCallback(evt => _loop = (LoopMode)evt.newValue);
            row.Add(loop);

            return row;
        }

        // ------------------------------------------------------------------
        // Инспектор справа
        // ------------------------------------------------------------------

private void RebuildInspector()
        {
            if (_inspector == null) return;
            _inspector.Clear();
            _spanStartField = null;
            _spanEndField = null;
            _contactReadout = null;

            if (_move == null)
            {
                _inspector.Add(new Label("Приём не выбран.") { style = { whiteSpace = WhiteSpace.Normal } });
                return;
            }

            _inspector.Add(Header("Приём"));

            var nameField = new TextField("Имя") { value = _move.moveName };
            nameField.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(_move, "FrameDate: имя приёма");
                _move.moveName = evt.newValue;
                MarkMoveDirty();
            });
            _inspector.Add(nameField);

            var fps = new DropdownField("Частота", new List<string> { "30 FPS", "60 FPS" }, _move.frameRate == 60 ? 1 : 0);
            fps.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(_move, "FrameDate: частота");
                _move.frameRate = evt.newValue.StartsWith("60") ? 60 : 30;
                MarkMoveDirty();
                RefreshAll();
            });
            _inspector.Add(fps);

            var duration = new IntegerField("Длительность, кадров") { value = _move.durationFrames };
            duration.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(_move, "FrameDate: длительность");
                _move.durationFrames = Mathf.Max(1, evt.newValue);
                MarkMoveDirty();
                RefreshAll();
            });
            _inspector.Add(duration);

            if (_move.pose != null) AddSourceSection(_move.pose);

            _inspector.Add(Header("Метрики игры"));
            var metricsField = new ObjectField("Конфиг") { objectType = typeof(FrameDateMetrics), allowSceneObjects = false, value = _move.metrics };
            metricsField.tooltip = "Единицы, арена, ширина Idle и камерные границы игры. Пусто — встроенные значения.";
            metricsField.RegisterValueChangedCallback(evt =>
            {
                Edit("FrameDate: конфиг метрик", () => _move.metrics = evt.newValue as FrameDateMetrics);
                _inspector.schedule.Execute(RebuildInspector);
            });
            _inspector.Add(metricsField);

            var metrics = _move.Metrics;
            _inspector.Add(Hint($"Все длины — в Units игры: 1 Unit = 1 см, {metrics.unitsPerMeter:0.#} Units = 1 м. " +
                                $"Арена {metrics.leftWallX:0.#} … {metrics.rightWallX:0.#} ({metrics.ArenaWidth:0.#}), " +
                                $"старт {metrics.startDistance:0.#}, ширина Idle {metrics.idleBodyWidth:0.#}, " +
                                $"порог {metrics.maxDistanceThreshold:0.#} → предел {metrics.MaxCenterDistance:0.#}."));

            var startButton = new Button(() => Edit("FrameDate: стартовая дистанция", () =>
            {
                float half = _move.Metrics.startDistance * 0.5f;
                _move.attackerPosition = new Vector2(-half, _move.attackerPosition.y);
                _move.targetPosition = new Vector2(half, _move.targetPosition.y);
                _inspector.schedule.Execute(RebuildInspector);
            }))
            { text = $"Старт из метрик: ±{metrics.startDistance * 0.5f:0.#}" };
            startButton.tooltip = "Ставит акторов на стартовую дистанцию раунда из конфига, симметрично относительно X = 0.";
            _inspector.Add(startButton);

            _inspector.Add(Header("Акторы"));
            AddActorFields("Атакующий", Actor.Attacker);
            AddActorFields("Цель", Actor.Target);

            _inspector.Add(Header("Дорожка"));

            int trackIndex = _timeline.SelectedTrack;
            if (trackIndex < 0 || trackIndex >= _move.tracks.Count)
            {
                _inspector.Add(Hint("Дорожка не выбрана. Кликните по дорожке на таймлайне."));
            }
            else
            {
                var track = _move.tracks[trackIndex];

                var trackName = new TextField("Имя") { value = track.name };
                trackName.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(_move, "FrameDate: имя дорожки");
                    track.name = evt.newValue;
                    MarkMoveDirty();
                    _timeline.Refresh();
                });
                _inspector.Add(trackName);

                var colorField = new ColorField("Цвет") { value = track.color };
                colorField.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(_move, "FrameDate: цвет дорожки");
                    track.color = evt.newValue;
                    MarkMoveDirty();
                    _timeline.Refresh();
                    _viewport.MarkDirtyRepaint();
                });
                _inspector.Add(colorField);

                // Актор и роль меняют набор полей интервала, поэтому после них инспектор
                // перестраивается — отложенно, чтобы не удалять поле из его же колбэка.
                var actorField = new EnumField("Актор", track.actor);
                actorField.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(_move, "FrameDate: актор дорожки");
                    track.actor = (Actor)evt.newValue;
                    MarkMoveDirty();
                    RefreshAfterEdit();
                    _inspector.schedule.Execute(RebuildInspector);
                });
                _inspector.Add(actorField);

                var roleField = new EnumField("Роль", track.role);
                roleField.tooltip = "Роль — единственное, что интерпретирует инструмент. None означает свободную пометку. " +
                                    "Movement и Hurtbox — техника, остальные — интервалы приёма: смена роли переносит дорожку в свою группу.";
                roleField.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(_move, "FrameDate: роль дорожки");
                    track.role = (TrackRole)evt.newValue;
                    MarkMoveDirty();
                    RefreshAfterEdit();
                    _inspector.schedule.Execute(RebuildInspector);
                });
                _inspector.Add(roleField);

                var removeTrack = new Button(() => DeleteTrack(trackIndex)) { text = "Удалить дорожку" };
                removeTrack.tooltip = "То же, что «×» у имени дорожки на таймлайне. Отменяется Undo.";
                _inspector.Add(removeTrack);
            }

            _inspector.Add(Header("Интервал"));
            _inspector.Add(Hint("[start, end): начало включено, конец исключён."));

            var span = _timeline.SelectedSpanOrNull();
            if (span == null)
            {
                _inspector.Add(Hint("Интервал не выбран. Протяните мышью по пустому месту дорожки, чтобы создать новый."));
            }
            else
            {
                var start = new IntegerField("Начало") { value = span.startFrame };
                var end = new IntegerField("Конец") { value = span.endFrame };

                start.RegisterValueChangedCallback(evt =>
                {
                    Undo.IncrementCurrentGroup();
                    Undo.RecordObject(_move, "FrameDate: начало интервала");
                    span.startFrame = evt.newValue;
                    MarkMoveDirty();
                    RefreshAfterEdit();
                });
                end.RegisterValueChangedCallback(evt =>
                {
                    Undo.IncrementCurrentGroup();
                    Undo.RecordObject(_move, "FrameDate: конец интервала");
                    span.endFrame = evt.newValue;
                    MarkMoveDirty();
                    RefreshAfterEdit();
                });

                _spanStartField = start;
                _spanEndField = end;
                _inspector.Add(start);
                _inspector.Add(end);
                _inspector.Add(Hint($"Длина: {span.Length} кадр(ов) · " +
                                    $"{_move.FrameToSeconds(span.startFrame):0.###}–{_move.FrameToSeconds(span.endFrame):0.###} с"));

                AddRoleSpanFields(_move.tracks[trackIndex], span);

                _inspector.Add(new Button(() => { _timeline.DeleteSelectedSpan(); RefreshAll(); })
                { text = "Удалить интервал (Del)" });
            }

            _inspector.Add(Header("Дорожки"));
            _inspector.Add(new Button(AddTrack) { text = "Добавить дорожку" });
        }

        /// <summary>Параметры тела актора: старт, hurtbox и невидимый пушбокс.</summary>
/// <summary>Параметры тела актора: старт, hurtbox, невидимый пушбокс и масса. Длины в Units.</summary>
        private void AddActorFields(string who, Actor actor)
        {
            var body = _move.Body(actor);

            var title = new Label(who);
            title.style.marginTop = 4f;
            title.style.unityFontStyleAndWeight = FontStyle.Italic;
            _inspector.Add(title);

            var startField = new Vector2Field("Старт") { value = _move.StartPosition(actor) };
            startField.tooltip = "Центр тела на кадре 0, Units.";
            startField.RegisterValueChangedCallback(evt => Edit("FrameDate: старт актора", () =>
            {
                if (actor == Actor.Attacker) _move.attackerPosition = evt.newValue;
                else _move.targetPosition = evt.newValue;
            }));
            _inspector.Add(startField);

            AddFloat("Радиус тела", body.radius, "FrameDate: тело", v => body.radius = v,
                "Радиус капсулы hurtbox, Units.");
            AddFloat("Высота тела", body.height, "FrameDate: тело", v => body.height = v,
                "Полная высота капсулы hurtbox, Units.");

            float idle = _move.Metrics.idleBodyWidth;
            var widthField = new FloatField("Ширина пушбокса") { value = body.overridePushWidth ? body.pushWidth : idle };
            widthField.tooltip = "Полная ширина пушбокса по X, Units. Во вьюпорте не рисуется.";
            widthField.SetEnabled(body.overridePushWidth);
            widthField.RegisterValueChangedCallback(evt => Edit("FrameDate: ширина пушбокса", () => body.pushWidth = evt.newValue));

            var overrideToggle = new Toggle("Своя ширина") { value = body.overridePushWidth };
            overrideToggle.tooltip = $"Выключено — ширина Idle из конфига метрик ({idle:0.#} Units).";
            overrideToggle.RegisterValueChangedCallback(evt =>
            {
                Edit("FrameDate: своя ширина пушбокса", () =>
                {
                    body.overridePushWidth = evt.newValue;
                    // Включая свою ширину, начинаем с ширины Idle, а не с давно забытого значения.
                    if (evt.newValue) body.pushWidth = idle;
                });
                widthField.SetValueWithoutNotify(evt.newValue ? body.pushWidth : idle);
                widthField.SetEnabled(evt.newValue);
            });
            _inspector.Add(overrideToggle);
            _inspector.Add(widthField);

            AddFloat("Масса", body.mass, "FrameDate: масса", v => body.mass = v,
                "Перекрытие пушбоксов делится обратно пропорционально массам. В игре стоя 1, лёжа 5.");
        }

        /// <summary>Поля интервала, которые имеют смысл только для роли его дорожки.</summary>
        private void AddRoleSpanFields(FrameTrack track, FrameSpan span)
        {
            if (track.role == TrackRole.Movement)
            {
                _inspector.Add(Header("Движение"));
                AddFloat("Расстояние", span.distance, "FrameDate: расстояние", v => span.distance = v,
                    "Плюс — вперёд, к противнику; минус — назад.");

                var profile = new EnumField("Профиль", span.profile);
                profile.RegisterValueChangedCallback(evt =>
                    Edit("FrameDate: профиль движения", () => span.profile = (MoveProfile)evt.newValue));
                _inspector.Add(profile);

                _inspector.Add(Hint("На кадре начала актор ещё не сдвинут, к кадру конца сдвинут на всё расстояние. " +
                                    "Интервалы движения одного актора складываются."));
                return;
            }

            if (track.role == TrackRole.Hurtbox)
            {
                var body = _move.Body(track.actor);
                _inspector.Add(Header("Hurtbox"));
                AddFloat("Высота, доля", span.hurtHeightScale, "FrameDate: hurtbox", v => span.hurtHeightScale = v,
                    "1 — исходная высота тела. Низ капсулы остаётся на месте.");
                AddFloat("Радиус, доля", span.hurtRadiusScale, "FrameDate: hurtbox", v => span.hurtRadiusScale = v,
                    "1 — исходный радиус тела.");
                _inspector.Add(Hint($"Сейчас: высота {body.height * span.hurtHeightScale:0.00}, радиус {body.radius * span.hurtRadiusScale:0.00} " +
                                    $"(исходно {body.height:0.00} и {body.radius:0.00}). Низ капсулы на месте — так выглядит приседание. " +
                                    "Форма меняется мгновенно на границах интервала."));
                return;
            }

            if (track.role != TrackRole.Attack) return;

            if (track.actor != Actor.Attacker)
            {
                _inspector.Add(Hint("Hitbox у цели в этой версии не считается: атакует только атакующий."));
                return;
            }

            _inspector.Add(Header("Hitbox"));
            var offset = new Vector2Field("Смещение") { value = span.hitOffset };
            offset.tooltip = "Начало оси hitbox от центра тела атакующего. X — вперёд.";
            offset.RegisterValueChangedCallback(evt => Edit("FrameDate: hitbox", () => span.hitOffset = evt.newValue));
            _inspector.Add(offset);
            AddFloat("Длина", span.hitLength, "FrameDate: hitbox", v => span.hitLength = v, "Длина оси вперёд. 0 — шар.");
            AddFloat("Радиус", span.hitRadius, "FrameDate: hitbox", v => span.hitRadius = v);

            _inspector.Add(Header("Отталкивание"));
            AddFloat("Расстояние", span.pushDistance, "FrameDate: отталкивание", v => span.pushDistance = v,
                "На сколько цель отъезжает от атакующего при контакте.");
            var frames = new IntegerField("Кадров") { value = span.pushFrames };
            frames.tooltip = "За сколько кадров проходит отталкивание, начиная с кадра контакта.";
            frames.RegisterValueChangedCallback(evt => Edit("FrameDate: отталкивание", () => span.pushFrames = evt.newValue));
            _inspector.Add(frames);

            _contactReadout = Hint(string.Empty);
            _contactReadout.style.color = new Color(1f, 0.85f, 0.45f);
            _inspector.Add(_contactReadout);
            UpdateContactReadout();
        }

        private void AddFloat(string label, float value, string undoName, System.Action<float> set, string tooltip = null)
        {
            var field = new FloatField(label) { value = value };
            if (tooltip != null) field.tooltip = tooltip;
            field.RegisterValueChangedCallback(evt => Edit(undoName, () => set(evt.newValue)));
            _inspector.Add(field);
        }

        /// <summary>Правка из инспектора: отдельный шаг Undo, пометка ассета, обновление картинки.</summary>
        private void Edit(string undoName, System.Action apply)
        {
            if (_move == null) return;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_move, undoName);
            apply();
            MarkMoveDirty();
            RefreshAfterEdit();
        }

        private void UpdateContactReadout()
        {
            if (_contactReadout == null || _move == null || _timeline == null) return;

            var sim = FrameDateSimulation.Run(_move);
            int index = sim.FindContact(_timeline.SelectedTrack, _timeline.SelectedSpan);
            if (index < 0)
            {
                _contactReadout.text = "Контакта нет: hitbox не задевает hurtbox цели ни на одном кадре интервала.";
                return;
            }

            var contact = sim.contacts[index];
            _contactReadout.text = $"Контакт на кадре {contact.frame}. " +
                                   $"Отталкивание цели [{contact.frame}, {contact.pushEndFrame}).";
        }

        /// <summary>Сведения о приёме, импортированном из игры, и показ скелета.</summary>
        private void AddSourceSection(FrameDatePose pose)
        {
            _inspector.Add(Header("Источник: игра"));
            var lines = new List<string> { $"{pose.frameCount} кадров @ {pose.frameRate} FPS · {pose.boneCount} костей" };
            if (pose.segments != null && pose.segments.Count > 0)
            {
                for (int i = 0; i < pose.segments.Count; i++)
                {
                    var s = pose.segments[i];
                    lines.Add($"Удар {i + 1}: {s.animation} — кадры [{s.seriesStart},{s.seriesStart + s.length})");
                }
            }
            else
            {
                lines.Add($"{pose.sourceAnimation} · {pose.sourceAnimationFile}");
            }
            lines.AddRange(pose.sourceParts);
            lines.Add($"Экспорт {pose.exportedAt}");
            _inspector.Add(Hint(string.Join("\n", lines)));
            var skeleton = new Toggle("Скелет") { value = _preview != null && _preview.ShowSkeleton };
            skeleton.tooltip = "Скелет из анимации игры поверх капсул. Только показ: в расчёт не входит.";
            skeleton.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(ShowSkeletonPref, evt.newValue);
                if (_preview != null) _preview.ShowSkeleton = evt.newValue;
                _viewport?.MarkDirtyRepaint();
            });
            _inspector.Add(skeleton);
        }

        /// <summary>Открыть окно с приёмом — после импорта и из других инструментов.</summary>
        public static void ShowMove(FrameDateMove move)
        {
            Open();
            var window = GetWindow<FrameDateWindow>();
            window._moveField?.SetValueWithoutNotify(move);
            window.BindMove(move);
        }

        private void ImportPackageInteractive()
        {
            string folder = Path.GetFullPath(FrameDateMoveImporter.ImportedFolder);
            string path = EditorUtility.OpenFilePanel("Пакет приёма из игры", Directory.Exists(folder) ? folder : Application.dataPath, "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var result = FrameDateMoveImporter.Import(path);
                ShowMove(result.move);
                string report = string.Join("\n", result.notes);
                Debug.Log($"FrameDate: импортирован «{result.move.moveName}»\n{report}");
                EditorUtility.DisplayDialog("Импорт приёма", $"«{result.move.moveName}» импортирован.\n\n{report}", "OK");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Импорт приёма", "Не удалось импортировать: " + e.Message, "OK");
            }
        }

        private static Label Header(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 10f;
            label.style.marginBottom = 2f;
            return label;
        }

        private static Label Hint(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 10;
            label.style.color = new Color(0.65f, 0.65f, 0.68f);
            label.style.marginBottom = 2f;
            return label;
        }

private void AddTrack() => AddTrack(Actor.Attacker, false);

        /// <summary>
        /// Новая дорожка в заданную группу: в «Технику» — Movement, в «Интервалы» — свободная пометка.
        /// Группа на таймлайне определяется актором и ролью, поэтому дорожка сразу окажется там, где нажали «+».
        /// </summary>
        private void AddTrack(Actor actor, bool technical)
        {
            if (_move == null) return;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_move, "FrameDate: добавить дорожку");
            _move.tracks.Add(new FrameTrack
            {
                name = technical ? "Движение" : "Новая дорожка",
                color = Color.HSVToRGB(Random.value, 0.55f, 1f),
                actor = actor,
                role = technical ? TrackRole.Movement : TrackRole.None
            });
            MarkMoveDirty();
            RefreshAll();
            _timeline.Select(_move.tracks.Count - 1, -1);
        }

        /// <summary>Удаление дорожки вместе с её интервалами. Один шаг Undo; выделение снимается.</summary>
        private void DeleteTrack(int trackIndex)
        {
            if (_move == null || trackIndex < 0 || trackIndex >= _move.tracks.Count) return;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_move, "FrameDate: удалить дорожку «" + _move.tracks[trackIndex].name + "»");
            _move.tracks.RemoveAt(trackIndex);
            MarkMoveDirty();
            // Индексы дорожек после удалённой сдвинулись — старое выделение недействительно.
            _timeline.Select(-1, -1);
            RefreshAll();
        }

        // ------------------------------------------------------------------
        // Вьюпорт и воспроизведение
        // ------------------------------------------------------------------

        private void DrawViewport()
        {
            var rect = new Rect(0f, 0f, _viewport.contentRect.width, _viewport.contentRect.height);
            _preview?.Draw(rect, _move, _frame);
        }

        private void TogglePlay()
        {
            _playing = !_playing;
            _lastTime = EditorApplication.timeSinceStartup;
            _frameAccumulator = 0d;
            UpdatePlayButton();
        }

        private void UpdatePlayButton()
        {
            if (_playButton != null) _playButton.text = _playing ? "❚❚ Пауза" : "▶ Играть";
        }

        private void StepFrame(int delta)
        {
            _playing = false;
            UpdatePlayButton();
            SetFrame(_frame + delta);
        }

        private void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            double dt = now - _lastTime;
            _lastTime = now;

            if (!_playing || _move == null) return;

            _frameAccumulator += dt * _speed * _move.frameRate;
            if (_frameAccumulator < 1d) return;

            int advance = (int)_frameAccumulator;
            _frameAccumulator -= advance;
            SetFrame(_frame + advance, applyLoop: true);
        }

        private void SetFrame(int value, bool applyLoop = false)
        {
            if (_move == null) return;

            int first = 0;
            int last = Mathf.Max(0, _move.durationFrames - 1);

            if (_loop == LoopMode.SelectedSpan)
            {
                var span = _timeline?.SelectedSpanOrNull();
                if (span != null)
                {
                    first = span.startFrame;
                    last = Mathf.Max(span.startFrame, span.endFrame - 1);
                }
            }

            if (applyLoop && _loop != LoopMode.Off)
            {
                int length = last - first + 1;
                if (length > 0)
                {
                    int offset = value - first;
                    value = first + ((offset % length) + length) % length;
                }
            }

            _frame = Mathf.Clamp(value, first, last);

            if (_timeline != null) _timeline.CurrentFrame = _frame;
            if (_frameReadout != null)
                _frameReadout.text = $"кадр {_frame} / {Mathf.Max(0, _move.durationFrames - 1)}   ·   {_move.FrameToSeconds(_frame):0.###} с";
            _viewport?.MarkDirtyRepaint();
            Repaint();
        }

        // ------------------------------------------------------------------
        // Данные
        // ------------------------------------------------------------------

private void BindMove(FrameDateMove move)
        {
            _move = move;
            EnsureMetrics(move);
            if (_timeline != null)
            {
                _timeline.Move = move;
                _timeline.style.height = _timeline.PreferredHeight;
            }
            _frame = 0;
            RefreshAll();
        }

        /// <summary>
        /// Приёму без конфига метрик назначается общий конфиг проекта: так все приёмы по умолчанию
        /// стоят на одной арене и одних метриках игры. Без шага Undo — это настройка по умолчанию, а не правка.
        /// </summary>
        private static void EnsureMetrics(FrameDateMove move)
        {
            if (move == null || move.metrics != null) return;
            var shared = AssetDatabase.LoadAssetAtPath<FrameDateMetrics>(FrameDateMetrics.DefaultAssetPath);
            if (shared == null) return;
            move.metrics = shared;
            EditorUtility.SetDirty(move);
            Debug.Log($"FrameDate: приёму «{move.name}» назначен конфиг метрик {FrameDateMetrics.DefaultAssetPath}");
        }

        private void RefreshAll()
        {
            if (_timeline != null)
            {
                _timeline.Refresh();
                _timeline.style.height = _timeline.PreferredHeight;
            }
            RebuildInspector();
            RefreshProblems();
            SetFrame(_frame);
            _viewport?.MarkDirtyRepaint();
        }

private void RefreshAfterEdit()
        {
            _timeline?.Refresh();
            RefreshProblems();
            UpdateContactReadout();
            _viewport?.MarkDirtyRepaint();
        }

        private void RefreshProblems()
        {
            if (_problems == null) return;

            if (_move == null)
            {
                _problems.text = string.Empty;
                _problems.style.display = DisplayStyle.None;
                return;
            }

            var problems = _move.Validate();
            if (problems.Count == 0)
            {
                _problems.text = string.Empty;
                _problems.style.display = DisplayStyle.None;
                return;
            }

            _problems.style.display = DisplayStyle.Flex;
            _problems.text = "Ошибки: " + string.Join("  •  ", problems);
        }

        private void MarkMoveDirty()
        {
            if (_move != null) EditorUtility.SetDirty(_move);
        }

        private void OnUndoRedo()
        {
            RefreshAll();
            Repaint();
        }

        private void CreateMoveInteractive()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Новый приём FrameDate", "NewMove", "asset", "Куда сохранить приём");
            if (string.IsNullOrEmpty(path)) return;

            var move = CreateInstance<FrameDateMove>();
            move.moveName = Path.GetFileNameWithoutExtension(path);
            move.frameRate = 30;
            move.durationFrames = 40;
            move.tracks = FrameDateMove.CreateDefaultTracks();

            AssetDatabase.CreateAsset(move, path);
            AssetDatabase.SaveAssets();

            BindMove(move);
            Selection.activeObject = move;
        }
    

        /// <summary>
        /// Синхронизирует числовые поля с данными после правки мышью.
        /// SetValueWithoutNotify — чтобы не запустить обратную запись и не плодить шаги Undo
        /// на каждый кадр протяжки.
        /// </summary>
private void SyncSpanFields()
        {
            UpdateContactReadout();
            var span = _timeline?.SelectedSpanOrNull();
            if (span == null) return;
            _spanStartField?.SetValueWithoutNotify(span.startFrame);
            _spanEndField?.SetValueWithoutNotify(span.endFrame);
        }
}
}
