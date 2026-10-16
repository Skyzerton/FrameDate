using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// План копий общих приёмов героям. Общие файлы — удары ногами, удары лёжа, катана и теневая катана (события и PvE не входят).
    /// Герою достаются:
    ///  1) общие приёмы, которые вызывают его собственные триггеры (катана у Ling, теневая катана у Kibo, ноги у Xiang_Tzu…);
    ///  2) общие удары ногами по правилам triggers_kicks.yaml — по стилю ног героя (FrameDateHero.kickStyle) и его оружию.
    /// Копия считается на модели именно этого героя (его скелет, капсулы, Idle, реакции), поэтому у каждого героя свой ассет.
    /// Герою без заданного стиля общие ноги не раздаются — это попадает в заметки плана.
    /// </summary>
    public static class SfaSharedPlan
    {
        /// <summary>Файлы настроек, из которых раздаются приёмы.</summary>
        public static readonly string[] SharedFiles =
        {
            "settings_kicks", "moves_settings", "settings_arena_katana", "settings_arena_shadow_moves"
        };

        public static BatchPlan Build(string root, List<string> stems)
        {
            var plan = new BatchPlan { root = root, shared = true };
            string settingsDir = SfaMoveExporter.Full(root, SfaMoveExporter.SettingsDir);

            var sharedEntries = new Dictionary<string, List<AnimationEntry>>();
            var sharedAttacks = new Dictionary<string, HashSet<string>>();
            foreach (var file in SharedFiles)
            {
                string path = settingsDir + "/" + file + ".yaml";
                if (!File.Exists(path)) continue;
                var entries = MoveSettingsReader.Read(path);
                sharedEntries[file] = entries;
                var names = new HashSet<string>();
                foreach (var e in entries)
                    if (e.Intervals.Exists(i => i.type == "Attack") && !SfaBatchImport.IsModelAnimation(e) && !string.IsNullOrEmpty(e.FileName)) names.Add(e.Name);
                sharedAttacks[file] = names;
            }

            // Шаблоны LOCK_* читаются из триггеров игры; universe — все теги, какие бывают у героев (остальные условия непроверяемы).
            var templates = SfaKickRules.LoadTemplates(SfaMoveExporter.Full(root, SfaMoveExporter.TriggersDir));
            var kickSets = SfaKickRules.Parse(SfaMoveExporter.Full(root, SfaMoveExporter.TriggersDir + "/triggers_kicks.yaml"), templates);
            var universe = new HashSet<string>();
            foreach (var stem0 in stems) universe.UnionWith(SfaKickRules.HeroTags(root, stem0));

            foreach (var stem in stems)
            {
                string title = SfaHeroes.Title(stem);
                var heroAsset = AssetDatabase.LoadAssetAtPath<FrameDateHero>(SfaHeroProfiles.AssetPath(title));
                var style = heroAsset != null ? heroAsset.kickStyle : KickStyle.Unknown;

                // Собственные триггеры героя (без квестов): какие общие приёмы он вызывает.
                string triggerStem = stem == "dr_viper" ? "viper" : stem;
                var text = new StringBuilder();
                foreach (var file in Directory.GetFiles(SfaMoveExporter.Full(root, SfaMoveExporter.TriggersDir), "triggers_arena_" + triggerStem + "*.yaml"))
                {
                    if (Path.GetFileName(file).Contains("_quests")) continue;
                    text.Append(File.ReadAllText(file));
                }
                string triggers = text.ToString();

                HashSet<string> byStyle = null;
                if (style != KickStyle.Unknown) byStyle = SfaKickRules.AnimationsFor(kickSets, SfaKickRules.HeroTags(root, stem), style, universe);
                else plan.heroNotes.Add($"{title}: стиль ног не задан — общие удары ногами не розданы (задайте «Kick Style» в профиле героя)");

                foreach (var file in SharedFiles)
                {
                    if (!sharedEntries.ContainsKey(file)) continue;
                    var allowed = new HashSet<string>();
                    foreach (var name in sharedAttacks[file])
                    {
                        bool referenced = Regex.IsMatch(triggers, "Name:\\s*" + Regex.Escape(name) + "\\s*[,}\\r\\n]");
                        bool byKickRule = file == "settings_kicks" && byStyle != null && byStyle.Contains(name);
                        if (referenced || byKickRule) allowed.Add(name);
                    }
                    if (allowed.Count == 0) continue;
                    AddSeries(plan, sharedEntries[file], allowed, stem, SfaMoveExporter.SettingsDir + "/" + file + ".yaml");
                }
            }

            plan.items.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.hero, b.hero);
                return c != 0 ? c : string.CompareOrdinal(string.Join("+", a.series), string.Join("+", b.series));
            });
            SfaBatchImport.Register(plan);
            return plan;
        }

        private static void AddSeries(BatchPlan plan, List<AnimationEntry> entries, HashSet<string> allowed, string stem, string source)
        {
            var playable = entries.FindAll(e => allowed.Contains(e.Name));
            var nextOf = new HashSet<string>();
            foreach (var entry in playable)
            {
                var next = SeriesRules.FindNext(entries, entry);
                if (next != null && allowed.Contains(next.Name)) nextOf.Add(next.Name);
            }

            var seen = new HashSet<string>();
            var covered = new HashSet<string>();
            Action<AnimationEntry> add = head =>
            {
                var series = SeriesRules.AutoSeries(entries, head.Name);
                series.RemoveAll(name => !allowed.Contains(name));
                if (series.Count == 0) return;
                if (!seen.Add(string.Join("+", series))) return;
                foreach (var name in series) covered.Add(name);
                var first = entries.Find(e => e.Name == series[0]);
                plan.items.Add(new BatchItem { hero = stem, category = SfaBatchImport.Category(first), series = series, source = source });
            };
            foreach (var entry in playable) if (!nextOf.Contains(entry.Name)) add(entry);
            foreach (var entry in playable) if (!covered.Contains(entry.Name)) add(entry);
        }
    }
}
