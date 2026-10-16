using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>Стиль ударов ногами героя в игре: определяет, какие общие удары ногами ему доступны.</summary>
    public enum KickStyle
    {
        Unknown,
        Precise,
        Agile,
        Strong
    }

    /// <summary>
    /// Профиль героя: все его приёмы одним списком по категориям, плюс то, на чём они посчитаны — детали модели, Idle цели,
    /// каталог реакций, правила блока. Приёмы выбираются из списка в окне FrameDate; сами приёмы — отдельные ассеты
    /// в папке героя (Assets/FrameDate/Moves/&lt;Герой&gt;/&lt;Категория&gt;/). Общие приёмы (удары ногами и т. п.) у каждого героя
    /// свои копии: они посчитаны на модели именно этого героя.
    /// </summary>
    [CreateAssetMenu(fileName = "Hero", menuName = "FrameDate/Hero")]
    public class FrameDateHero : ScriptableObject
    {
        /// <summary>Порядок категорий в списке; неизвестные идут в конце по алфавиту.</summary>
        public static readonly string[] CategoryOrder =
        {
            "Базовые", "Верхние", "Нижние", "Тяжёлые", "Вращения", "Супер", "Теневые", "Удары ногами", "Броски", "Особые", "Прочие"
        };

        [System.Serializable]
        public class MoveEntry
        {
            public string category;
            public FrameDateMove move;
            /// <summary>Пусто — приём из файла самого героя; иначе файл общих приёмов, из которого он взят (settings_kicks и т. п.).</summary>
            public string shared;
        }

        [Header("Герой")]
        public string displayName;
        [Tooltip("Имя в файлах игры: settings_arena_<stem>.yaml, папка модели arena_<stem>.")]
        public string stem;

        [Tooltip("Стиль ударов ногами. В файлах игры не хранится — задаётся вручную; по нему героям раздаются общие удары ногами.")]
        public KickStyle kickStyle;

        [Header("На чём посчитаны приёмы")]
        public string skeleton;
        public string armor;
        public string helmet;
        public string weapon;
        public string idleAnimation;
        public string blockTriggers;
        public FrameDateReactionCatalog reactions;

        [Tooltip("Чего не удалось найти в файлах игры при переносе.")]
        public List<string> notes = new List<string>();

        [Header("Приёмы")]
        public List<MoveEntry> moves = new List<MoveEntry>();

        public static int CategoryRank(string category)
        {
            int index = System.Array.IndexOf(CategoryOrder, category);
            return index >= 0 ? index : CategoryOrder.Length;
        }

        /// <summary>Категории героя в порядке списка.</summary>
        public List<string> Categories()
        {
            var result = new List<string>();
            foreach (var entry in moves)
                if (entry.move != null && !result.Contains(entry.category)) result.Add(entry.category);
            result.Sort((a, b) =>
            {
                int c = CategoryRank(a).CompareTo(CategoryRank(b));
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            return result;
        }

        public List<MoveEntry> MovesIn(string category)
        {
            var result = moves.FindAll(e => e.move != null && e.category == category);
            result.Sort((a, b) => string.CompareOrdinal(a.move.name, b.move.name));
            return result;
        }

        /// <summary>Все приёмы одним порядком списка: по категориям, внутри — по имени. По нему листают ◀ ▶.</summary>
        public List<MoveEntry> Ordered()
        {
            var result = new List<MoveEntry>();
            foreach (var category in Categories()) result.AddRange(MovesIn(category));
            return result;
        }

        public int IndexOf(FrameDateMove move)
        {
            var ordered = Ordered();
            for (int i = 0; i < ordered.Count; i++) if (ordered[i].move == move) return i;
            return -1;
        }
    }
}
