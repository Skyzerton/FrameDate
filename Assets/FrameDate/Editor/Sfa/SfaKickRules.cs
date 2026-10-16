using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Правила общих ударов ногами из triggers_kicks.yaml: какой набор (триггер-сет) каким героям доступен.
    /// Условия набора — шаблоны LOCK_* (определения читаются из триггеров игры): стиль (LOCK_STYLE_*), герой (LOCK_ARENA_*),
    /// оружие (LOCK_SUBTYPE_*, LOCK_WEAPON_TYPE_*), плюс теги в Locks самого набора. Тег внутри Not — исключение.
    /// Условие считается проверяемым, только если его теги бывают у героев (ARENA_*, оружие из Style анимаций, стиль);
    /// непроверяемое (служебные теги вроде SUPER_KICK) считается выполненным.
    /// Стиль героя в файлах игры не хранится (теги приходят из конфигов героев) — он задаётся вручную в FrameDateHero.kickStyle.
    /// </summary>
    public static class SfaKickRules
    {
        public class TemplateDef
        {
            /// <summary>Положительные теги шаблона — одна группа «любой из».</summary>
            public readonly HashSet<string> Any = new HashSet<string>();
            public readonly HashSet<string> Not = new HashSet<string>();
        }

        public class KickSet
        {
            public string Name;
            public readonly HashSet<string> Styles = new HashSet<string>();
            public readonly HashSet<string> NotStyles = new HashSet<string>();
            /// <summary>Требования: набор подходит, если из каждой группы у героя есть хотя бы один тег.</summary>
            public readonly List<HashSet<string>> Groups = new List<HashSet<string>>();
            public readonly HashSet<string> Excluded = new HashSet<string>();
            public readonly List<string> Animations = new List<string>();
            /// <summary>Набор только для события (FightTagExists) — в общий список не входит.</summary>
            public bool EventOnly;
        }

        private static readonly Regex SetStart = new Regex(@"^  - Name:\s*(\S+)");
        private static readonly Regex TemplatesLine = new Regex(@"Templates:\s*\[([^\]]*)\]");
        private static readonly Regex Tag = new Regex("\\?Player\\[Me\\]\\.TagExists\"\\s*,\\s*Value2:\\s*\"(\\w+)\"");
        private static readonly Regex Animation = new Regex(@"Animation:\s*\{\s*Name:\s*([^,}\s]+)");
        private static readonly Regex Container = new Regex(@"^(\s*)-\s*(Not|Or|And):");
        private static readonly Regex TemplateName = new Regex(@"^  - Name:\s*(LOCK_\S+)");
        private static readonly HashSet<string> Styles3 = new HashSet<string> { "PRECISE", "AGILE", "STRONG" };

        /// <summary>Определения шаблонов LOCK_* из всех файлов триггеров игры.</summary>
        public static Dictionary<string, TemplateDef> LoadTemplates(string triggersDir)
        {
            var defs = new Dictionary<string, TemplateDef>();
            foreach (var file in Directory.GetFiles(triggersDir, "*.yaml"))
            {
                TemplateDef current = null;
                var stack = new List<KeyValuePair<int, string>>();
                foreach (var raw in File.ReadAllLines(file))
                {
                    var name = TemplateName.Match(raw);
                    if (name.Success)
                    {
                        current = defs.ContainsKey(name.Groups[1].Value) ? null : new TemplateDef();
                        if (current != null) defs[name.Groups[1].Value] = current;
                        stack.Clear();
                        continue;
                    }
                    if (raw.StartsWith("  - Name:")) { current = null; continue; }
                    if (current == null) continue;
                    string trimmed = raw.TrimStart();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                    int indent = raw.Length - trimmed.Length;
                    while (stack.Count > 0 && stack[stack.Count - 1].Key >= indent) stack.RemoveAt(stack.Count - 1);
                    var container = Container.Match(raw);
                    if (container.Success) stack.Add(new KeyValuePair<int, string>(container.Groups[1].Value.Length, container.Groups[2].Value));
                    var tag = Tag.Match(raw);
                    if (tag.Success) (stack.Exists(s => s.Value == "Not") ? current.Not : current.Any).Add(tag.Groups[1].Value);
                }
            }
            return defs;
        }

        public static List<KickSet> Parse(string path, Dictionary<string, TemplateDef> templates)
        {
            var sets = new List<KickSet>();
            KickSet current = null;
            var stack = new List<KeyValuePair<int, string>>();

            foreach (var raw in File.ReadAllLines(path))
            {
                var start = SetStart.Match(raw);
                if (start.Success)
                {
                    current = new KickSet { Name = start.Groups[1].Value };
                    sets.Add(current);
                    stack.Clear();
                    continue;
                }
                if (current == null) continue;
                string trimmed = raw.TrimStart();
                if (trimmed.StartsWith("#") || trimmed.Length == 0) continue;
                int indent = raw.Length - trimmed.Length;

                while (stack.Count > 0 && stack[stack.Count - 1].Key >= indent) stack.RemoveAt(stack.Count - 1);
                var container = Container.Match(raw);
                if (container.Success) stack.Add(new KeyValuePair<int, string>(container.Groups[1].Value.Length, container.Groups[2].Value));

                // Шаблоны набора — только на его уровне (отступ 4); шаблоны внутри триггеров (KICK.*, TACTICS.*) не условия.
                var templatesLine = TemplatesLine.Match(raw);
                if (templatesLine.Success && indent == 4)
                {
                    foreach (var token in templatesLine.Groups[1].Value.Split(','))
                    {
                        string t = token.Trim();
                        if (t.StartsWith("LOCK_STYLE_NOT_")) { current.NotStyles.Add(t.Substring("LOCK_STYLE_NOT_".Length)); continue; }
                        if (t.StartsWith("LOCK_STYLE_")) { current.Styles.Add(t.Substring("LOCK_STYLE_".Length)); continue; }
                        TemplateDef def;
                        if (!templates.TryGetValue(t, out def)) continue;
                        if (def.Any.Count > 0) current.Groups.Add(new HashSet<string>(def.Any));
                        current.Excluded.UnionWith(def.Not);
                    }
                }

                if (raw.Contains("FightTagExists")) current.EventOnly = true;

                var tag = Tag.Match(raw);
                if (tag.Success)
                {
                    string value = tag.Groups[1].Value;
                    bool inNot = stack.Exists(s => s.Value == "Not");
                    if (Styles3.Contains(value)) (inNot ? current.NotStyles : current.Styles).Add(value);
                    else if (inNot) current.Excluded.Add(value);
                    else current.Groups.Add(new HashSet<string> { value });
                }

                var animation = Animation.Match(raw);
                if (animation.Success && !current.Animations.Contains(animation.Groups[1].Value)) current.Animations.Add(animation.Groups[1].Value);
            }
            return sets;
        }

        /// <summary>
        /// Удары ногами, доступные герою. tags — теги героя (ARENA_&lt;ГЕРОЙ&gt; и оружие); universe — все теги, какие вообще бывают
        /// у героев: условие из тегов, которых нет ни у одного героя, проверить нельзя и считается выполненным.
        /// </summary>
        public static HashSet<string> AnimationsFor(List<KickSet> sets, ICollection<string> tags, KickStyle style, ICollection<string> universe)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            string style0 = style.ToString().ToUpperInvariant();
            var heroTags = new HashSet<string>(tags);

            foreach (var set in sets)
            {
                if (set.EventOnly || set.Animations.Count == 0) continue;
                if (set.Styles.Count > 0 && !set.Styles.Contains(style0)) continue;
                if (set.NotStyles.Contains(style0)) continue;
                if (set.Excluded.Overlaps(heroTags)) continue;
                bool ok = true;
                foreach (var group in set.Groups)
                {
                    bool verifiable = false;
                    foreach (var tag in group) if (universe.Contains(tag)) verifiable = true;
                    if (verifiable && !group.Overlaps(heroTags)) { ok = false; break; }
                }
                if (!ok) continue;
                foreach (var name in set.Animations) result.Add(name);
            }
            return result;
        }

        private static readonly HashSet<string> NotWeaponTags = new HashSet<string> { "STRONG", "AGILE", "PRECISE", "SKELETON" };

        /// <summary>
        /// Теги героя для правил ног: ARENA_&lt;ГЕРОЙ&gt; и оружие. Оружие в файлах игры видно по значению Style у анимаций героя
        /// (KATANA, GIANT_SWORD…) — берём значения, встретившиеся не реже двух раз; FISTS_&lt;СТИЛЬ&gt; даёт теги FISTS и FISTS_&lt;СТИЛЬ&gt;.
        /// </summary>
        public static HashSet<string> HeroTags(string root, string stem)
        {
            var tags = new HashSet<string> { "ARENA_" + stem.ToUpperInvariant() };
            if (stem.StartsWith("dr_")) tags.Add("ARENA_" + stem.Substring(3).ToUpperInvariant());
            var tally = new Dictionary<string, int>();
            var rx = new Regex(@"^\s{4}Style:\s*(\w+)", RegexOptions.Multiline);
            foreach (var file in new[] { "settings_arena_" + stem + ".yaml", "settings_arena_" + stem + "_stances.yaml" })
            {
                string path = SfaMoveExporter.Full(root, SfaMoveExporter.SettingsDir + "/" + file);
                if (!File.Exists(path)) continue;
                foreach (Match m in rx.Matches(File.ReadAllText(path)))
                {
                    int v;
                    tally.TryGetValue(m.Groups[1].Value, out v);
                    tally[m.Groups[1].Value] = v + 1;
                }
            }
            foreach (var kv in tally)
            {
                if (kv.Value < 2 || NotWeaponTags.Contains(kv.Key)) continue;
                tags.Add(kv.Key);
                if (kv.Key.StartsWith("FISTS_")) tags.Add("FISTS");
            }
            return tags;
        }
    }
}
