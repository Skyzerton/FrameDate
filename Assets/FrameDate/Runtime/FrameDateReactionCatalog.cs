using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>
    /// Каталог реакций цели из игры: стандартные хит-реакции (settings_hits.yaml) и реакции блока под оружие
    /// защищающегося. Один каталог — один набор (герой и оружие), у всех реакций общий скелет и капсулы.
    /// Пополняется импортом приёмов: каждый пакет приносит реакции, нужные его ударам.
    /// Только для показа: поза, капсулы и собственный сдвиг цели по анимации реакции.
    /// </summary>
    [CreateAssetMenu(fileName = "Reactions", menuName = "FrameDate/Reaction Catalog")]
    public class FrameDateReactionCatalog : ScriptableObject
    {
        public const string Folder = "Assets/FrameDate/Reactions";

        [Tooltip("Чей набор: герой и оружие защищающегося.")]
        public string setName;

        [Header("Скелет и капсулы (общие для всех реакций)")]
        public int boneCount;
        public int motionBone = 2;
        public float forwardSign = -1f;
        public int[] boneParents;
        public List<FrameDatePose.PoseCapsule> capsules = new List<FrameDatePose.PoseCapsule>();

        [System.Serializable]
        public class ReactionInterval
        {
            public string name;
            public int start;
            /// <summary>Конец исключён: [start, end).</summary>
            public int end;
        }

        [System.Serializable]
        public class Reaction
        {
            public string name;
            public string settingsFile;
            public string animationFile;
            public List<string> groups = new List<string>();
            public int frameCount;
            public List<ReactionInterval> intervals = new List<ReactionInterval>();
            [HideInInspector] public float[] positions;
            [HideInInspector] public float[] capsulePoints;
        }

        public List<Reaction> reactions = new List<Reaction>();

        public Reaction Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var reaction in reactions)
                if (string.Equals(reaction.name, name, System.StringComparison.OrdinalIgnoreCase)) return reaction;
            return null;
        }

        public bool IsValid(Reaction reaction) =>
            reaction != null && reaction.frameCount > 0 && boneCount > 0 &&
            reaction.positions != null && reaction.positions.Length == reaction.frameCount * boneCount * 3 &&
            reaction.capsulePoints != null && reaction.capsulePoints.Length == reaction.frameCount * capsules.Count * 4;

        public Vector3 Position(Reaction reaction, int frame, int bone)
        {
            frame = Mathf.Clamp(frame, 0, reaction.frameCount - 1);
            int i = (frame * boneCount + bone) * 3;
            return new Vector3(reaction.positions[i], reaction.positions[i + 1], reaction.positions[i + 2]);
        }

        public void CapsuleModel(Reaction reaction, int frame, int capsule, out Vector2 a, out Vector2 b)
        {
            frame = Mathf.Clamp(frame, 0, reaction.frameCount - 1);
            int i = (frame * capsules.Count + capsule) * 4;
            a = new Vector2(reaction.capsulePoints[i], reaction.capsulePoints[i + 1]);
            b = new Vector2(reaction.capsulePoints[i + 2], reaction.capsulePoints[i + 3]);
        }

        /// <summary>Сдвиг motion-кости реакции вперёд (к атакующему) от её кадра 0, Units. Отлёт назад — отрицательный.</summary>
        public float MotionForward(Reaction reaction, int frame) =>
            forwardSign * (Position(reaction, frame, motionBone).x - Position(reaction, 0, motionBone).x);

        /// <summary>Вид реакции по группам игры — для подписей.</summary>
        public static string KindLabel(Reaction reaction)
        {
            if (reaction == null) return "—";
            if (reaction.groups.Contains("BLOCK_IN_OUT")) return "подъём защиты";
            if (reaction.groups.Contains("BLOCK_HIT")) return "удар в блок";
            if (reaction.groups.Contains("FALL_CRITICAL")) return "крит, падение";
            if (reaction.groups.Contains("FALL")) return "падение";
            if (reaction.groups.Contains("HIT")) return "попадание";
            return "реакция";
        }
    }
}
