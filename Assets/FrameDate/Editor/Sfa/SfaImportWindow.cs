using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Импорт приёмов прямо из папки проекта игры SFA: FrameDate сам читает настройки, анимации и детали моделей
    /// (SfaFormats), без кода и без открытого редактора игры. Приём пишется пакетом в Assets/FrameDate/Imported
    /// и импортируется тем же импортёром, что пакеты из экспортёра в проекте игры. Настройки окна — в EditorPrefs.
    /// </summary>
    public class SfaImportWindow : EditorWindow
    {
        private const string Prefix = "FrameDate.Sfa.";

        private string _root;
        private string _settingsFile;
        private List<string> _series = new List<string>();
        private List<int> _overrides = new List<int>();
        private string _skeleton;
        private string _armor;
        private string _helmet;
        private string _weapon;
        private string _idleSettings;
        private string _idleAnimation;
        private string _reactions;
        private string _blockTriggers;
        private string _reactionSet;

        private string[] _settingsFiles = new string[0];
        private string[] _triggerFiles = new string[0];
        private List<AnimationEntry> _entries = new List<AnimationEntry>();
        private readonly Dictionary<string, int> _frameCounts = new Dictionary<string, int>();
        private string _status;
        private MessageType _statusType;
        private Vector2 _scroll;

        [MenuItem("Tools/FrameDate SFA Import")]
        public static void Open() => GetWindow<SfaImportWindow>("Импорт из SFA");

        private void OnEnable()
        {
            _root = EditorPrefs.GetString(Prefix + "root", "D:/_ACTIVE PROJECTS/SF4/sf-arena-client");
            _settingsFile = EditorPrefs.GetString(Prefix + "settings", SfaMoveExporter.SettingsDir + "/settings_arena_katana.yaml");
            _series = new List<string>(EditorPrefs.GetString(Prefix + "series", "Katana_Slash_1").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            _overrides = new List<int>();
            foreach (var s in EditorPrefs.GetString(Prefix + "overrides", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                _overrides.Add(int.TryParse(s, out v) ? v : -1);
            }
            _skeleton = EditorPrefs.GetString(Prefix + "skeleton", "Commons/m_skeleton");
            _armor = EditorPrefs.GetString(Prefix + "armor", "arena_ling/_base/arm_ling");
            _helmet = EditorPrefs.GetString(Prefix + "helmet", "arena_ling/_base/helm_ling");
            _weapon = EditorPrefs.GetString(Prefix + "weapon", "arena_ling/_Weapons/_base");
            _idleSettings = EditorPrefs.GetString(Prefix + "idleSettings", SfaMoveExporter.SettingsDir + "/settings_arena_katana.yaml");
            _idleAnimation = EditorPrefs.GetString(Prefix + "idleAnimation", "Katana_Idle_No_WeaponSwitchFrames");
            _reactions = EditorPrefs.GetString(Prefix + "reactions", SfaMoveExporter.TriggersDir + "/reactions_effects.yaml");
            _blockTriggers = EditorPrefs.GetString(Prefix + "blockTriggers", SfaMoveExporter.TriggersDir + "/triggers_arena_katana.yaml");
            _reactionSet = EditorPrefs.GetString(Prefix + "reactionSet", "Ling");
            ReloadFiles();
        }

        private void Save()
        {
            EditorPrefs.SetString(Prefix + "root", _root);
            EditorPrefs.SetString(Prefix + "settings", _settingsFile);
            EditorPrefs.SetString(Prefix + "series", string.Join("|", _series));
            EditorPrefs.SetString(Prefix + "overrides", string.Join("|", _overrides.ConvertAll(v => v.ToString())));
            EditorPrefs.SetString(Prefix + "skeleton", _skeleton);
            EditorPrefs.SetString(Prefix + "armor", _armor);
            EditorPrefs.SetString(Prefix + "helmet", _helmet);
            EditorPrefs.SetString(Prefix + "weapon", _weapon);
            EditorPrefs.SetString(Prefix + "idleSettings", _idleSettings);
            EditorPrefs.SetString(Prefix + "idleAnimation", _idleAnimation);
            EditorPrefs.SetString(Prefix + "reactions", _reactions);
            EditorPrefs.SetString(Prefix + "blockTriggers", _blockTriggers);
            EditorPrefs.SetString(Prefix + "reactionSet", _reactionSet);
        }

        private bool RootOk => SfaMoveExporter.LooksLikeSfa(_root);

        // Прогресс массового переноса обновляется без кликов по окну.
        private void OnInspectorUpdate()
        {
            if (SfaBatchImport.IsRunning) Repaint();
        }

        private void ReloadFiles()
        {
            _frameCounts.Clear();
            _settingsFiles = RootOk ? Relative(Directory.GetFiles(SfaMoveExporter.Full(_root, SfaMoveExporter.SettingsDir), "*.yaml"), SfaMoveExporter.SettingsDir) : new string[0];
            _triggerFiles = RootOk ? Relative(Directory.GetFiles(SfaMoveExporter.Full(_root, SfaMoveExporter.TriggersDir), "*.yaml"), SfaMoveExporter.TriggersDir) : new string[0];
            ReloadEntries();
        }

        private static string[] Relative(string[] files, string dir)
        {
            var result = new string[files.Length];
            for (int i = 0; i < files.Length; i++) result[i] = dir + "/" + Path.GetFileName(files[i]);
            return result;
        }

        private void ReloadEntries()
        {
            _frameCounts.Clear();
            _entries = new List<AnimationEntry>();
            if (!RootOk) return;
            string path = SfaMoveExporter.Full(_root, _settingsFile);
            try
            {
                if (File.Exists(path)) _entries = MoveSettingsReader.Read(path);
            }
            catch (Exception e)
            {
                SetStatus("Не удалось прочитать " + _settingsFile + ": " + e.Message, MessageType.Error);
            }
        }

        private AnimationEntry Find(string name) => _entries.Find(e => e.Name == name);

        private int FrameCount(AnimationEntry entry)
        {
            int count;
            if (_frameCounts.TryGetValue(entry.Name, out count)) return count;
            try { count = SfaMoveExporter.LoadAnimation(_root, entry).FrameCount; }
            catch { count = -1; }
            _frameCounts[entry.Name] = count;
            return count;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Проект игры", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            string root = EditorGUILayout.TextField("Папка SFA", _root);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.OpenFolderPanel("Папка проекта SFA", _root, "");
                if (!string.IsNullOrEmpty(picked)) root = picked;
            }
            EditorGUILayout.EndHorizontal();
            if (root != _root)
            {
                _root = root;
                ReloadFiles();
            }
            if (!RootOk)
            {
                EditorGUILayout.HelpBox("Это не папка проекта SFA: в ней нет " + SfaMoveExporter.SettingsDir + " и " + SfaMoveExporter.ModelsDir + ".", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                if (GUI.changed) Save();
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Приём", EditorStyles.boldLabel);
            _settingsFile = Popup("Файл настроек", _settingsFile, _settingsFiles, true);

            var names = _entries.ConvertAll(e => e.Name).ToArray();
            if (names.Length == 0) EditorGUILayout.HelpBox("В файле нет приёмов.", MessageType.Warning);
            else
            {
                if (_series.Count == 0) _series.Add(names[0]);
                DrawSeries(names);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Модель героя (папки в Items/Models)", EditorStyles.boldLabel);
            _skeleton = EditorGUILayout.TextField("Скелет", _skeleton);
            _armor = EditorGUILayout.TextField("Броня", _armor);
            _helmet = EditorGUILayout.TextField("Шлем", _helmet);
            _weapon = EditorGUILayout.TextField("Оружие", _weapon);

            EditorGUILayout.LabelField("Idle героя — тело цели", EditorStyles.miniBoldLabel);
            _idleSettings = Popup("Файл Idle", _idleSettings, _settingsFiles, false);
            _idleAnimation = EditorGUILayout.TextField("Анимация Idle", _idleAnimation);

            EditorGUILayout.LabelField("Реакции цели (та же модель — зеркальный бой)", EditorStyles.miniBoldLabel);
            _reactions = Popup("Триггеры реакций", _reactions, _triggerFiles, false);
            _blockTriggers = Popup("Блок оружия цели", _blockTriggers, _triggerFiles, false);
            _reactionSet = EditorGUILayout.TextField("Набор реакций", _reactionSet);

            if (GUILayout.Button("Пресет: Линг (база)"))
            {
                _skeleton = "Commons/m_skeleton";
                _armor = "arena_ling/_base/arm_ling";
                _helmet = "arena_ling/_base/helm_ling";
                _weapon = "arena_ling/_Weapons/_base";
                _idleSettings = SfaMoveExporter.SettingsDir + "/settings_arena_katana.yaml";
                _idleAnimation = "Katana_Idle_No_WeaponSwitchFrames";
                _reactions = SfaMoveExporter.TriggersDir + "/reactions_effects.yaml";
                _blockTriggers = SfaMoveExporter.TriggersDir + "/triggers_arena_katana.yaml";
                _reactionSet = "Ling";
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(names.Length == 0))
            {
                if (GUILayout.Button("Импорт в FrameDate", GUILayout.Height(28))) Import();
            }
            if (GUILayout.Button("Сверить с эталоном (пакеты экспортёра игры в Reference)")) CheckReferences();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Массовый перенос героев", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Все приёмы всех героев (settings_arena_<герой>.yaml) → Assets/FrameDate/Moves/<Герой>/<Категория>. " +
                                    "Серия ударов — один приём; снаряды и анимации призванных моделей пропускаются. Идёт в фоне, редактор доступен.", MessageType.None);
            if (SfaBatchImport.IsRunning)
            {
                EditorGUILayout.LabelField("Идёт: " + SfaBatchImport.Progress());
                if (GUILayout.Button("Остановить")) SfaBatchImport.Stop();
            }
            else
            {
                if (SfaBatchImport.Plan != null) EditorGUILayout.LabelField("Последний запуск: " + SfaBatchImport.Progress());
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Перенести всех героев", GUILayout.Height(24)) &&
                    EditorUtility.DisplayDialog("Массовый перенос", "Будут созданы или перезаписаны приёмы всех героев (сотни ассетов, ~320 МБ). Продолжить?", "Перенести", "Отмена"))
                    SfaBatchImport.Start(SfaBatchImport.BuildPlan(_root, SfaHeroes.FindStems(_root)));
                using (new EditorGUI.DisabledScope(SfaBatchImport.Plan == null))
                {
                    if (GUILayout.Button("Повторить неудачные", GUILayout.Height(24))) SfaBatchImport.Start(SfaBatchImport.Plan, true);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
            EditorGUILayout.EndScrollView();

            if (GUI.changed) Save();
        }

        private string Popup(string label, string value, string[] options, bool reloadOnChange)
        {
            if (options.Length == 0) return EditorGUILayout.TextField(label, value);
            int index = Mathf.Max(0, Array.IndexOf(options, value));
            int picked = EditorGUILayout.Popup(label, index, Array.ConvertAll(options, Path.GetFileName));
            string result = options[picked];
            if (reloadOnChange && result != value)
            {
                _settingsFile = result;
                ReloadEntries();
            }
            return result;
        }

        private void DrawSeries(string[] names)
        {
            EditorGUILayout.LabelField("Серия ударов", EditorStyles.miniBoldLabel);
            while (_overrides.Count < _series.Count) _overrides.Add(-1);
            int seriesFrame = 0;
            int previousEnd = 0;
            int remove = -1;

            for (int i = 0; i < _series.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                int index = Mathf.Max(0, Array.IndexOf(names, _series[i]));
                _series[i] = names[EditorGUILayout.Popup($"Удар {i + 1}", index, names)];
                if (i > 0 && GUILayout.Button("−", GUILayout.Width(24))) remove = i;
                EditorGUILayout.EndHorizontal();

                var entry = Find(_series[i]);
                if (entry == null) continue;
                int frames = FrameCount(entry);
                int start = i == 0 ? 0 : Mathf.Min(SeriesRules.StartFrame(entry, _series[i - 1], previousEnd), Mathf.Max(0, frames - 1));
                int attacks = entry.Intervals.FindAll(v => v.type == "Attack").Count;
                string info = $"{entry.FileName} · {frames} кадров · Attack: {attacks} · с кадра {start} · в серии с кадра {seriesFrame}";

                if (i < _series.Count - 1 && frames > 0)
                {
                    string rule;
                    int auto = SeriesRules.TransitionFrame(entry, frames, out rule);
                    EditorGUILayout.BeginHorizontal();
                    bool manual = EditorGUILayout.ToggleLeft("Переход вручную", _overrides[i] >= 0, GUILayout.Width(130));
                    if (manual)
                    {
                        if (_overrides[i] < 0) _overrides[i] = auto;
                        _overrides[i] = Mathf.Clamp(EditorGUILayout.IntField("кадр", _overrides[i]), start + 1, frames);
                    }
                    else
                    {
                        _overrides[i] = -1;
                        EditorGUILayout.LabelField($"переход на кадре {auto}: {rule}", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndHorizontal();
                    int transition = _overrides[i] >= 0 ? _overrides[i] : auto;
                    seriesFrame += Mathf.Max(1, transition - start);
                    previousEnd = transition;
                }
                else if (frames > 0)
                {
                    seriesFrame += Mathf.Max(1, frames - start);
                }
                EditorGUILayout.HelpBox(info, MessageType.None);
            }

            if (remove > 0)
            {
                _series.RemoveAt(remove);
                _overrides.RemoveAt(remove);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Собрать серию автоматически"))
            {
                _series = SeriesRules.AutoSeries(_entries, _series[0]);
                _overrides = _series.ConvertAll(_ => -1);
            }
            if (GUILayout.Button("+ удар"))
            {
                var next = SeriesRules.FindNext(_entries, Find(_series[_series.Count - 1]) ?? _entries[0]);
                _series.Add(next != null ? next.Name : _series[_series.Count - 1]);
                _overrides.Add(-1);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Всего в серии: {seriesFrame} кадров @ {SfaMoveExporter.ReferenceFps} FPS", EditorStyles.miniLabel);
        }

        public ExportRequest BuildRequest() => new ExportRequest
        {
            SfaRoot = _root,
            SettingsFile = _settingsFile,
            Animations = new List<string>(_series),
            TransitionOverrides = new List<int>(_overrides),
            SkeletonPart = _skeleton,
            ArmorPart = _armor,
            HelmetPart = _helmet,
            WeaponPart = _weapon,
            OutputDir = Path.GetFullPath(FrameDateMoveImporter.ImportedFolder),
            IdleSettingsFile = _idleSettings,
            IdleAnimation = _idleAnimation,
            ReactionsTriggers = _reactions,
            DefenderBlockTriggers = _blockTriggers,
            ReactionSet = _reactionSet
        };

        private void Import()
        {
            Save();
            try
            {
                string path = SfaMoveExporter.Export(BuildRequest());
                AssetDatabase.Refresh();
                var result = FrameDateMoveImporter.Import(path);
                FrameDateWindow.ShowMove(result.move);
                SetStatus($"«{result.move.moveName}» импортирован из SFA.\n" + string.Join("\n", result.notes), MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Ошибка импорта: " + e.Message, MessageType.Error);
                Debug.LogException(e);
            }
        }

        private void CheckReferences()
        {
            var lines = new List<string>();
            bool all = true;
            // Эталоны — пакеты экспортёра в проекте игры, отдельно от Imported: импорт из SFA пишет пакеты туда же под теми же именами.
            string folder = SfaReferenceCheck.ReferenceFolder;
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.fdmove.json"))
                {
                    var report = SfaReferenceCheck.Compare(file, _root, _reactions, _blockTriggers);
                    all &= report.passed;
                    lines.Add(report.ToString());
                }
            }
            if (lines.Count == 0) lines.Add("В " + folder + " нет пакетов для сверки.");
            SetStatus(string.Join("\n", lines), all ? MessageType.Info : MessageType.Warning);
        }

        private void SetStatus(string text, MessageType type)
        {
            _status = text;
            _statusType = type;
        }
    }
}
