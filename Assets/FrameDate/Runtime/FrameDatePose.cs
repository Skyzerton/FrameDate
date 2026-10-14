using System.Collections.Generic;
using UnityEngine;

namespace FrameDate
{
    /// <summary>
    /// Поза скелета приёма, импортированная из игры: положения всех костей на каждом кадре
    /// в пространстве модели игры (Units). Только для показа и анализа — в расчёт не входит.
    /// Персонаж в анимациях игры смотрит в −X, поэтому «вперёд» = forwardSign × X.
    /// </summary>
    public class FrameDatePose : ScriptableObject
    {
        [Header("Источник")]
        public string sourceAnimation;
        public string sourceAnimationFile;
        public string sourceSettingsFile;
        public string exportedAt;
        public List<string> sourceParts = new List<string>();

        /// <summary>Удар серии: какая анимация, с какого кадра серии и сколько кадров идёт в серию.</summary>
        [System.Serializable]
        public class PoseSegment
        {
            public string animation;
            public string animationFile;
            public int seriesStart;
            public int length;
            public int startFrame;
            public string transition;
        }

        [Tooltip("Удары серии по порядку. Приём игры часто — серия: например, два слэша подряд.")]
        public List<PoseSegment> segments = new List<PoseSegment>();

        /// <summary>
        /// Капсула героя из игры: тело, броня, шлем, оружие. Collidable — по ней противник может попасть;
        /// атакующие детали опознаются по имени из AttackingParts интервала Attack.
        /// </summary>
        [System.Serializable]
        public class PoseCapsule
        {
            public string name;
            public uint hash;
            public string part;
            public float radius;
            public bool collidable;
        }

        [Header("Капсулы из игры")]
        public List<PoseCapsule> capsules = new List<PoseCapsule>();

        /// <summary>a.x, a.y, b.x, b.y каждой капсулы, кадр за кадром: [(кадр × капсул + капсула) × 4], пространство модели.</summary>
        [HideInInspector] public float[] capsulePoints;

        [Header("Idle героя — тело цели")]
        public bool hasIdle;
        public string idleAnimation;
        public float idleMotionX;
        [HideInInspector] public float[] idlePositions;
        [HideInInspector] public float[] idlePoints;

        public bool HasCapsules =>
            capsules != null && capsules.Count > 0 && capsulePoints != null &&
            capsulePoints.Length == frameCount * capsules.Count * 4;

        public bool HasIdle =>
            hasIdle && HasCapsules && idlePoints != null && idlePoints.Length == capsules.Count * 4;

        /// <summary>Концы капсулы на кадре, пространство модели игры.</summary>
        public void CapsuleModel(int frame, int capsule, out Vector2 a, out Vector2 b)
        {
            frame = Mathf.Clamp(frame, 0, frameCount - 1);
            int i = (frame * capsules.Count + capsule) * 4;
            a = new Vector2(capsulePoints[i], capsulePoints[i + 1]);
            b = new Vector2(capsulePoints[i + 2], capsulePoints[i + 3]);
        }

        public void IdleCapsuleModel(int capsule, out Vector2 a, out Vector2 b)
        {
            int i = capsule * 4;
            a = new Vector2(idlePoints[i], idlePoints[i + 1]);
            b = new Vector2(idlePoints[i + 2], idlePoints[i + 3]);
        }

        public Vector3 IdlePosition(int bone)
        {
            int i = bone * 3;
            return new Vector3(idlePositions[i], idlePositions[i + 1], idlePositions[i + 2]);
        }

        /// <summary>Индекс капсулы по имени без учёта регистра (как игра сравнивает хэши имён); −1, если нет.</summary>
        public int FindCapsule(string name)
        {
            if (capsules == null) return -1;
            for (int i = 0; i < capsules.Count; i++)
                if (string.Equals(capsules[i].name, name, System.StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        [Header("Скелет")]
        public int frameRate = 60;
        public int frameCount;
        public int boneCount;
        public int motionBone = 2;
        public float forwardSign = -1f;
        public int[] boneParents;

        /// <summary>x, y, z каждой кости, кадр за кадром: [(кадр × boneCount + кость) × 3].</summary>
        [HideInInspector] public float[] positions;

        public bool IsValid =>
            frameCount > 0 && boneCount > 0 && boneParents != null && boneParents.Length == boneCount &&
            positions != null && positions.Length == frameCount * boneCount * 3;

        public Vector3 Position(int frame, int bone)
        {
            frame = Mathf.Clamp(frame, 0, frameCount - 1);
            int i = (frame * boneCount + bone) * 3;
            return new Vector3(positions[i], positions[i + 1], positions[i + 2]);
        }

        /// <summary>Продвижение motion-кости вперёд от кадра 0, Units.</summary>
        public float MotionForward(int frame) =>
            forwardSign * (Position(frame, motionBone).x - Position(0, motionBone).x);
    }
}
