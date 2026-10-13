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
