using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Профиль героя игры: где его настройки приёмов, детали модели, Idle и правила блока. Всё находится по шаблонам
    /// проекта игры (arena_&lt;герой&gt;/_base/arm_*, helm_*, _Weapons/_base, стойки в settings_arena_&lt;герой&gt;_stances.yaml),
    /// а то, что найти не удалось, попадает в Problems — приём всё равно импортируется, но без этой части.
    /// </summary>
    public class HeroProfile
    {
        public string Stem;
        public string Title;
        public string SettingsFile;
        public string ModelDir;
        public string Skeleton = "Commons/m_skeleton";
        public string Armor;
        public string Helmet;
        public string Weapon;
        public string IdleSettings;
        public string IdleAnimation;
        public string BlockTriggers;
        public readonly List<string> Problems = new List<string>();
    }

    public static class SfaHeroes
    {
        private static readonly Dictionary<string, string> ModelDirOverrides = new Dictionary<string, string>
        {
            { "hong_ju", "arena_hong_joo" },
            { "dr_viper", "arena_viper" }
        };

        /// <summary>Герои: файлы settings_arena_*.yaml, у которых есть папка модели arena_*.</summary>
        public static List<string> FindStems(string root)
        {
            var stems = new List<string>();
            foreach (var file in Directory.GetFiles(SfaMoveExporter.Full(root, SfaMoveExporter.SettingsDir), "settings_arena_*.yaml"))
            {
                string stem = Path.GetFileNameWithoutExtension(file).Substring("settings_arena_".Length);
                if (stem.EndsWith("_stances")) continue;
                if (!Directory.Exists(SfaMoveExporter.Full(root, SfaMoveExporter.ModelsDir + "/" + ModelDir(stem)))) continue;
                stems.Add(stem);
            }
            stems.Sort(StringComparer.Ordinal);
            return stems;
        }

        public static string ModelDir(string stem)
        {
            string dir;
            return ModelDirOverrides.TryGetValue(stem, out dir) ? dir : "arena_" + stem;
        }

        public static string Title(string stem)
        {
            var parts = stem.Split('_');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join("_", parts);
        }

        public static HeroProfile Resolve(string root, string stem, List<AnimationEntry> entries)
        {
            var profile = new HeroProfile
            {
                Stem = stem,
                Title = Title(stem),
                ModelDir = ModelDir(stem),
                SettingsFile = SfaMoveExporter.SettingsDir + "/settings_arena_" + stem + ".yaml"
            };

            string models = SfaMoveExporter.Full(root, SfaMoveExporter.ModelsDir + "/" + profile.ModelDir);
            profile.Armor = FindPart(models + "/_base", "arm_", profile.ModelDir + "/_base/");
            profile.Helmet = FindPart(models + "/_base", "helm_", profile.ModelDir + "/_base/");
            if (File.Exists(models + "/_Weapons/_base/Common/SObjPart.bytes")) profile.Weapon = profile.ModelDir + "/_Weapons/_base";
            if (profile.Armor == null) profile.Problems.Add("нет брони _base/arm_*");
            if (profile.Weapon == null) profile.Problems.Add("нет оружия _Weapons/_base");
            if (!File.Exists(SfaMoveExporter.Full(root, SfaMoveExporter.ModelsDir + "/" + profile.Skeleton + "/Common/SObjPart.bytes")))
                profile.Problems.Add("нет скелета " + profile.Skeleton);

            // Idle цели: стартовая стойка по умолчанию из файла стоек героя.
            foreach (var candidate in new[] { stem, stem.StartsWith("dr_") ? stem.Substring(3) : stem })
            {
                string stances = SfaMoveExporter.SettingsDir + "/settings_arena_" + candidate + "_stances.yaml";
                if (!File.Exists(SfaMoveExporter.Full(root, stances))) continue;
                var idle = FindIdle(MoveSettingsReader.Read(SfaMoveExporter.Full(root, stances)));
                if (idle == null) continue;
                profile.IdleSettings = stances;
                profile.IdleAnimation = idle;
                break;
            }
            if (profile.IdleAnimation == null) profile.Problems.Add("не найден Idle в стойках — цель капсулой тела FrameDate");

            // Блок: правила в триггерах самого героя, иначе в триггерах оружия, которым он бьёт.
            var candidates = new List<string>
            {
                "triggers_arena_" + stem + ".yaml", "triggers_arena_" + stem + "_weapons.yaml", "triggers_arena_" + stem + "_unique.yaml"
            };
            var groups = new HashSet<string>();
            foreach (var entry in entries) foreach (var group in entry.Groups) groups.Add(group);
            if (groups.Contains("KATANA")) candidates.Add("triggers_arena_katana.yaml");
            if (groups.Contains("FISTS") || groups.Contains("UNARMED")) candidates.Add("triggers_arena_fists.yaml");
            if (groups.Contains("POLEARM")) candidates.Add("triggers_arena_polearm.yaml");
            if (groups.Contains("GIANT_SWORD")) candidates.Add("triggers_arena_marcus.yaml");
            foreach (var name in candidates)
            {
                string relative = SfaMoveExporter.TriggersDir + "/" + name;
                string full = SfaMoveExporter.Full(root, relative);
                if (File.Exists(full) && File.ReadAllText(full).Contains("REACTION.BLOCK_HIT")) { profile.BlockTriggers = relative; break; }
            }
            if (profile.BlockTriggers == null) profile.Problems.Add("правил блока не найдено — реакции блока пусты, попадание работает");
            return profile;
        }

        private static string FindPart(string baseDir, string prefix, string relativeBase)
        {
            if (!Directory.Exists(baseDir)) return null;
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(dir + "/Common/SObjPart.bytes"))
                    return relativeBase + name;
            }
            return null;
        }

        private static string FindIdle(List<AnimationEntry> entries)
        {
            string fallback = null;
            foreach (var entry in entries)
            {
                if (!entry.Groups.Contains("STANCE_START_IDLE")) continue;
                string lower = entry.Name.ToLowerInvariant();
                if (lower.Contains("short") || lower.EndsWith("_out")) continue;
                if (lower.Contains("c_default") || lower.Contains("c_base")) return entry.Name;
                if (fallback == null && lower.Contains("default")) fallback = entry.Name;
            }
            return fallback;
        }
    }

    [Serializable]
    public class BatchItem
    {
        public string hero;
        public string category;
        public List<string> series = new List<string>();
        /// <summary>pending · done · failed</summary>
        public string status = "pending";
        /// <summary>Файл настроек, откуда взят приём (относительно проекта игры). Пусто — файл самого героя.</summary>
        public string source;
        public string message;
        public int frames;
        public int contacts;
        public int errors;
    }

    [Serializable]
    public class BatchPlan
    {
        public string root;
        public List<BatchItem> items = new List<BatchItem>();
        public List<string> skipped = new List<string>();
        public List<string> heroNotes = new List<string>();
        /// <summary>План общих приёмов (копии для героев), а не их собственных.</summary>
        public bool shared;
    }

    /// <summary>
    /// Массовый перенос приёмов героев из проекта игры. План — по файлам настроек героев: приём = серия ударов
    /// (цепочка по Transitions), в серию не входят снаряды (группа RANGED — это отдельные модели на другом скелете).
    /// Раскладка: Assets/FrameDate/Moves/&lt;Герой&gt;/&lt;Категория&gt;/, позы — в &lt;Герой&gt;/_Poses/.
    /// Выполняется в фоне по одному приёму за тик редактора, план и ход хранятся в Library/FrameDateBatch.
    /// </summary>
    public static class SfaBatchImport
    {
        public const string MovesFolder = "Assets/FrameDate/Moves";
        private const string StateDir = "Library/FrameDateBatch";
        private const string PlanFile = StateDir + "/plan.json";
        private const string RunningKey = "FrameDate.Batch.Running";
        private const double SaveEverySeconds = 8.0;

        private static BatchPlan _plan;
        private static readonly Dictionary<string, HeroProfile> Profiles = new Dictionary<string, HeroProfile>();
        private static readonly Dictionary<string, List<AnimationEntry>> EntriesByHero = new Dictionary<string, List<AnimationEntry>>();
        private static double _lastSave;
        private static int _sinceUnload;
        private static bool _hooked;

        public static bool IsRunning => SessionState.GetBool(RunningKey, false);

        /// <summary>Сделать план текущим и сохранить (для планов, построенных вне этого класса).</summary>
        public static void Register(BatchPlan plan)
        {
            SavePlan(plan);
            _plan = plan;
        }

        public static BatchPlan Plan => _plan ?? LoadPlan();

        public static string Progress()
        {
            var plan = Plan;
            if (plan == null) return "плана нет";
            int done = 0, failed = 0, pending = 0;
            foreach (var item in plan.items)
            {
                if (item.status == "done") done++;
                else if (item.status == "failed") failed++;
                else pending++;
            }
            return $"{done} готово, {failed} с ошибкой, {pending} осталось из {plan.items.Count}" + (IsRunning ? " · идёт" : "");
        }

        // ------------------------------------------------------------------ план

        public static BatchPlan BuildPlan(string root, List<string> stems)
        {
            var plan = new BatchPlan { root = root };
            foreach (var stem in stems)
            {
                string settings = SfaMoveExporter.Full(root, SfaMoveExporter.SettingsDir + "/settings_arena_" + stem + ".yaml");
                var entries = MoveSettingsReader.Read(settings);
                var profile = HeroProfileFor(root, stem, entries);
                foreach (var problem in profile.Problems) plan.heroNotes.Add($"{profile.Title}: {problem}");

                var attacks = entries.FindAll(e => e.Intervals.Exists(i => i.type == "Attack"));
                var playable = new List<AnimationEntry>();
                foreach (var entry in attacks)
                {
                    bool ranged = false;
                    foreach (var group in entry.Groups) if (group.StartsWith("RANGED")) ranged = true;
                    if (ranged) plan.skipped.Add($"{profile.Title}/{entry.Name}: снаряд (RANGED) — отдельная модель, пока не поддержана");
                    else if (IsModelAnimation(entry)) plan.skipped.Add($"{profile.Title}/{entry.Name}: анимация отдельной модели (снаряд, волна, призыв) — на скелет героя не накладывается");
                    else if (string.IsNullOrEmpty(entry.FileName)) plan.skipped.Add($"{profile.Title}/{entry.Name}: нет FileName");
                    else playable.Add(entry);
                }

                // Корни серий: атаки, в которые не переходят из другой атаки.
                var nextOf = new HashSet<string>();
                foreach (var entry in playable)
                {
                    var next = SeriesRules.FindNext(entries, entry);
                    if (next != null && playable.Contains(next)) nextOf.Add(next.Name);
                }

                var seen = new HashSet<string>();
                var covered = new HashSet<string>();
                Action<AnimationEntry> addSeries = root0 =>
                {
                    var series = SeriesRules.AutoSeries(entries, root0.Name);
                    series.RemoveAll(name => !playable.Exists(p => p.Name == name));
                    if (series.Count == 0) return;
                    string key = string.Join("+", series);
                    if (!seen.Add(key)) return;
                    foreach (var name in series) covered.Add(name);
                    var first = entries.Find(e => e.Name == series[0]);
                    plan.items.Add(new BatchItem { hero = stem, category = Category(first), series = series });
                };
                foreach (var entry in playable) if (!nextOf.Contains(entry.Name)) addSeries(entry);
                foreach (var entry in playable) if (!covered.Contains(entry.Name)) addSeries(entry);
            }
            plan.items.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.hero, b.hero);
                return c != 0 ? c : string.CompareOrdinal(string.Join("+", a.series), string.Join("+", b.series));
            });
            SavePlan(plan);
            _plan = plan;
            return plan;
        }

        /// <summary>
        /// Анимация призванной модели, а не героя: в имени «_Model» или группа вида *_MODEL, либо анимация без групп вовсе
        /// (у героя группы есть всегда). Её кости принадлежат другому скелету.
        /// </summary>
        public static bool IsModelAnimation(AnimationEntry entry)
        {
            if (entry.Name.IndexOf("_Model", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (entry.Groups.Count == 0) return true;
            foreach (var group in entry.Groups) if (group.EndsWith("_MODEL") || group.Contains("_MODEL_")) return true;
            return false;
        }

        /// <summary>Категория серии — по группам её первого удара.</summary>
        public static string Category(AnimationEntry first)
        {
            var g = first.Groups;
            if (g.Contains("SHADOW_MOVE")) return "Теневые";
            if (g.Contains("TYPE_SUPER")) return "Супер";
            if (g.Contains("TYPE_HEAVY") || g.Contains("ATTACK_HEAVY")) return "Тяжёлые";
            if (g.Contains("TYPE_LOW") || g.Contains("ATTACK_LOW")) return "Нижние";
            if (g.Contains("TYPE_UPPER") || g.Contains("ATTACK_UPPER")) return "Верхние";
            if (g.Contains("TYPE_SPINNING") || g.Contains("ATTACK_SPINNING")) return "Вращения";
            foreach (var group in g) if (group.StartsWith("TYPE_KICK") || group.StartsWith("KICK")) return "Удары ногами";
            foreach (var group in g) if (group.StartsWith("THROW")) return "Броски";
            foreach (var group in g) if (group == "RAGE_MOVE" || group.EndsWith("_SPECIAL")) return "Особые";
            if (g.Contains("TYPE_BASIC") || g.Contains("ATTACK_BASIC")) return "Базовые";
            return "Прочие";
        }

        private static HeroProfile HeroProfileFor(string root, string stem, List<AnimationEntry> entries)
        {
            HeroProfile profile;
            if (!Profiles.TryGetValue(stem, out profile))
            {
                profile = SfaHeroes.Resolve(root, stem, entries);
                Profiles[stem] = profile;
                EntriesByHero[stem] = entries;
            }
            return profile;
        }

        // ------------------------------------------------------------------ запуск

        public static void Start(BatchPlan plan, bool retryFailed = false)
        {
            _plan = plan;
            if (retryFailed) foreach (var item in plan.items) if (item.status == "failed") item.status = "pending";
            SavePlan(plan);
            SessionState.SetBool(RunningKey, true);
            Hook();
        }

        public static void Stop()
        {
            SessionState.SetBool(RunningKey, false);
            if (_plan != null) SavePlan(_plan);
            AssetDatabase.SaveAssets();
        }

        [InitializeOnLoadMethod]
        private static void Resume()
        {
            if (IsRunning && File.Exists(PlanFile)) Hook();
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!IsRunning)
            {
                EditorApplication.update -= Tick;
                _hooked = false;
                return;
            }

            var plan = Plan;
            if (plan == null) { Stop(); return; }

            BatchItem item = null;
            foreach (var candidate in plan.items) if (candidate.status == "pending") { item = candidate; break; }
            if (item == null)
            {
                FinishRun(plan);
                return;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                Process(plan, item);
            }
            catch (Exception e)
            {
                item.status = "failed";
                item.message = e.Message;
            }
            item.message = item.message ?? string.Empty;

            _sinceUnload++;
            if (EditorApplication.timeSinceStartup - _lastSave > SaveEverySeconds)
            {
                _lastSave = EditorApplication.timeSinceStartup;
                FrameDateMoveImporter.DeferSave = false;
                AssetDatabase.SaveAssets();
                SavePlan(plan);
            }
            if (_sinceUnload >= 40)
            {
                _sinceUnload = 0;
                EditorUtility.UnloadUnusedAssetsImmediate();
            }
            if (watch.ElapsedMilliseconds > 4000) Debug.LogWarning($"FrameDate batch: {string.Join("+", item.series)} занял {watch.ElapsedMilliseconds} мс");
        }

        private static void FinishRun(BatchPlan plan)
        {
            Stop();
            FrameDateMoveImporter.DeferSave = false;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            // Профили героев: список приёмов по категориям для выбора в окне.
            SfaHeroProfiles.RebuildAll(plan.root);
            WriteIndex(plan);
            Debug.Log("FrameDate batch завершён: " + Progress());
        }

        private static void Process(BatchPlan plan, BatchItem item)
        {
            var entries = EntriesByHero.ContainsKey(item.hero) ? EntriesByHero[item.hero] : null;
            var profile = entries != null ? Profiles[item.hero] : null;
            if (profile == null)
            {
                entries = MoveSettingsReader.Read(SfaMoveExporter.Full(plan.root, SfaMoveExporter.SettingsDir + "/settings_arena_" + item.hero + ".yaml"));
                profile = HeroProfileFor(plan.root, item.hero, entries);
            }

            string name = string.Join("+", item.series);
            string heroFolder = MovesFolder + "/" + profile.Title;
            string moveFolder = heroFolder + "/" + item.category;
            string poseFolder = heroFolder + "/_Poses";
            EnsureFolder(moveFolder);
            EnsureFolder(poseFolder);

            var request = new ExportRequest
            {
                SfaRoot = plan.root,
                SettingsFile = string.IsNullOrEmpty(item.source) ? profile.SettingsFile : item.source,
                Animations = new List<string>(item.series),
                TransitionOverrides = new List<int>(),
                SkeletonPart = profile.Skeleton,
                ArmorPart = profile.Armor,
                HelmetPart = profile.Helmet,
                WeaponPart = profile.Weapon,
                IdleSettingsFile = profile.IdleSettings,
                IdleAnimation = profile.IdleAnimation,
                ReactionsTriggers = SfaMoveExporter.TriggersDir + "/reactions_effects.yaml",
                DefenderBlockTriggers = profile.BlockTriggers,
                ReactionSet = profile.Title
            };

            var package = SfaMoveExporter.Build(request);
            string json = JsonUtility.ToJson(package);
            FrameDateMoveImporter.DeferSave = true;
            var result = FrameDateMoveImporter.ImportJson(json, moveFolder, poseFolder, name);

            var sim = FrameDateSimulation.Run(result.move);
            item.frames = result.move.durationFrames;
            item.contacts = sim.contacts.Count;
            item.errors = result.move.Validate().Count;
            item.status = "done";
            item.message = item.errors > 0 ? "ошибок валидации: " + item.errors : string.Empty;
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------ состояние и отчёт

        private static void SavePlan(BatchPlan plan)
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(PlanFile, JsonUtility.ToJson(plan));
        }

        private static BatchPlan LoadPlan()
        {
            if (!File.Exists(PlanFile)) return null;
            _plan = JsonUtility.FromJson<BatchPlan>(File.ReadAllText(PlanFile));
            return _plan;
        }

        /// <summary>Сводка переноса: герои → категории → серии с кадрами и контактами; пропущенное и проблемы героев.</summary>
        public static void WriteIndex(BatchPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine(plan.shared ? "# Общие приёмы, розданные героям (копии на модели героя)" : "# Перенесённые приёмы героев SFA");
            sb.AppendLine();
            sb.AppendLine($"Итог: {Progress()}. Раскладка: `Assets/FrameDate/Moves/<Герой>/<Категория>/`, позы — `<Герой>/_Poses/`, каталоги реакций — `Assets/FrameDate/Reactions/<Герой>_Reactions.asset`.");
            sb.AppendLine("Приём = серия ударов (цепочка по Transitions). Цель — тот же герой в Idle (зеркальный бой). Контакты считаются со стартовой дистанцией из конфига метрик.");
            sb.AppendLine();
            string hero = null, category = null;
            foreach (var item in plan.items)
            {
                if (item.hero != hero)
                {
                    hero = item.hero;
                    category = null;
                    sb.AppendLine($"## {SfaHeroes.Title(hero)}");
                }
                if (item.category != category)
                {
                    category = item.category;
                    sb.AppendLine($"### {category}");
                }
                string status = item.status == "done" ? $"{item.frames} кадр., контактов {item.contacts}" : "**ОШИБКА** " + item.message;
                sb.AppendLine($"- `{string.Join("+", item.series)}` — {status}" + (item.status == "done" && !string.IsNullOrEmpty(item.message) ? " · " + item.message : ""));
            }
            if (plan.heroNotes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Что не удалось найти у героев");
                foreach (var note in plan.heroNotes) sb.AppendLine("- " + note);
            }
            if (plan.skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Пропущено");
                foreach (var skip in plan.skipped) sb.AppendLine("- " + skip);
            }
            Directory.CreateDirectory("docs");
            File.WriteAllText(plan.shared ? "docs/IMPORTED_MOVES_SHARED.md" : "docs/IMPORTED_MOVES.md", sb.ToString());
        }
    }
}
