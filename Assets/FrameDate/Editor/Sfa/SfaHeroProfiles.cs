using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Профили героев (FrameDateHero): по одному ассету в папке героя, Assets/FrameDate/Moves/&lt;Герой&gt;/&lt;Герой&gt;.asset.
    /// Собираются сканированием папки: каждый приём — запись с категорией (имя папки) и пометкой, если он из общего файла.
    /// Ручные поля профиля (стиль ног) при пересборке сохраняются.
    /// </summary>
    public static class SfaHeroProfiles
    {
        public static string AssetPath(string title) => SfaBatchImport.MovesFolder + "/" + title + "/" + title + ".asset";

        /// <summary>Пересобирает профили всех героев, у которых есть папка с приёмами. Возвращает число профилей.</summary>
        public static int RebuildAll(string root)
        {
            int count = 0;
            foreach (var folder in AssetDatabase.GetSubFolders(SfaBatchImport.MovesFolder))
            {
                string title = Path.GetFileName(folder);
                if (title.StartsWith("_")) continue;
                if (Rebuild(root, title) != null) count++;
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        public static FrameDateHero Rebuild(string root, string title)
        {
            string folder = SfaBatchImport.MovesFolder + "/" + title;
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            string stem = title.ToLowerInvariant();
            string path = AssetPath(title);
            var hero = AssetDatabase.LoadAssetAtPath<FrameDateHero>(path);
            if (hero == null)
            {
                hero = ScriptableObject.CreateInstance<FrameDateHero>();
                AssetDatabase.CreateAsset(hero, path);
            }

            hero.displayName = title;
            hero.stem = stem;

            // Данные модели — по тем же правилам, что при переносе.
            hero.notes = new List<string>();
            if (SfaMoveExporter.LooksLikeSfa(root))
            {
                string settings = SfaMoveExporter.Full(root, SfaMoveExporter.SettingsDir + "/settings_arena_" + stem + ".yaml");
                if (File.Exists(settings))
                {
                    var profile = SfaHeroes.Resolve(root, stem, MoveSettingsReader.Read(settings));
                    hero.skeleton = profile.Skeleton;
                    hero.armor = profile.Armor;
                    hero.helmet = profile.Helmet;
                    hero.weapon = profile.Weapon;
                    hero.idleAnimation = profile.IdleAnimation;
                    hero.blockTriggers = profile.BlockTriggers;
                    hero.notes.AddRange(profile.Problems);
                }
            }
            hero.reactions = AssetDatabase.LoadAssetAtPath<FrameDateReactionCatalog>(FrameDateReactionCatalog.Folder + "/" + title + "_Reactions.asset");

            hero.moves = new List<FrameDateHero.MoveEntry>();
            string ownFile = "settings_arena_" + stem + ".yaml";
            foreach (var guid in AssetDatabase.FindAssets("t:FrameDateMove", new[] { folder }))
            {
                string movePath = AssetDatabase.GUIDToAssetPath(guid);
                var parts = movePath.Split('/');
                // Assets/FrameDate/Moves/<Герой>/<Категория>/<приём>.asset
                if (parts.Length < 6) continue;
                var move = AssetDatabase.LoadAssetAtPath<FrameDateMove>(movePath);
                if (move == null) continue;

                string shared = string.Empty;
                if (move.pose != null && !string.IsNullOrEmpty(move.pose.sourceSettingsFile))
                {
                    string source = Path.GetFileName(move.pose.sourceSettingsFile);
                    if (!string.Equals(source, ownFile, StringComparison.OrdinalIgnoreCase)) shared = Path.GetFileNameWithoutExtension(source);
                }
                hero.moves.Add(new FrameDateHero.MoveEntry { category = parts[4], move = move, shared = shared });
            }
            hero.moves.Sort((a, b) =>
            {
                int c = FrameDateHero.CategoryRank(a.category).CompareTo(FrameDateHero.CategoryRank(b.category));
                if (c != 0) return c;
                c = string.CompareOrdinal(a.category, b.category);
                return c != 0 ? c : string.CompareOrdinal(a.move.name, b.move.name);
            });
            EditorUtility.SetDirty(hero);
            return hero;
        }

        /// <summary>Все профили героев проекта, по имени.</summary>
        public static List<FrameDateHero> All()
        {
            var list = new List<FrameDateHero>();
            foreach (var guid in AssetDatabase.FindAssets("t:FrameDateHero"))
            {
                var hero = AssetDatabase.LoadAssetAtPath<FrameDateHero>(AssetDatabase.GUIDToAssetPath(guid));
                if (hero != null) list.Add(hero);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.displayName, b.displayName));
            return list;
        }

        /// <summary>Профиль героя, в чьей папке лежит приём; null — приём не из Moves/&lt;Герой&gt;.</summary>
        public static FrameDateHero HeroOf(FrameDateMove move)
        {
            if (move == null) return null;
            string path = AssetDatabase.GetAssetPath(move);
            string prefix = SfaBatchImport.MovesFolder + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string title = path.Substring(prefix.Length).Split('/')[0];
            return AssetDatabase.LoadAssetAtPath<FrameDateHero>(AssetPath(title));
        }
    }
}
