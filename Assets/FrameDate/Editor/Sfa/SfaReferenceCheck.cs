using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Сверка своего читателя SFA с эталоном — пакетами, которые выгрузил экспортёр в проекте игры
    /// (он работает на коде игры). Приём пересобирается с параметрами из эталонного пакета и сравнивается
    /// поле за полем: серия, интервалы, реакции, скелет, капсулы, позы. Числа — с допуском Tolerance.
    /// </summary>
    public static class SfaReferenceCheck
    {
        /// <summary>Допуск по координатам, Units. Расхождение ожидается на уровне округления фиксированной точки.</summary>
        public const float Tolerance = 0.05f;

        /// <summary>
        /// Папка эталонов: пакеты экспортёра в проекте игры (Assets/FrameDateExport). Чтобы обновить эталон, в экспортёре
        /// игры укажите «Папка FrameDate» = эта папка. Отдельно от Imported, куда пишет импорт из SFA.
        /// </summary>
        public const string ReferenceFolder = "Assets/FrameDate/Reference";

        public class Report
        {
            public string reference;
            public bool passed;
            public float maxPose;
            public float maxCapsule;
            public float maxIdle;
            public float maxReaction;
            public readonly List<string> problems = new List<string>();

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append(passed ? "СОШЛОСЬ  " : "РАСХОЖДЕНИЕ  ").Append(Path.GetFileName(reference));
                sb.Append($" · поза {maxPose:0.####}, капсулы {maxCapsule:0.####}, Idle {maxIdle:0.####}, реакции {maxReaction:0.####} Units");
                foreach (var problem in problems) sb.Append("\n   ! ").Append(problem);
                return sb.ToString();
            }
        }

        public static Report Compare(string referencePath, string sfaRoot, string reactionsTriggers, string blockTriggers)
        {
            var report = new Report { reference = referencePath };
            var reference = JsonUtility.FromJson<MovePackage>(File.ReadAllText(referencePath));

            var request = new ExportRequest
            {
                SfaRoot = sfaRoot,
                SettingsFile = reference.settingsFile,
                Animations = reference.segments.ConvertAll(s => s.animation),
                TransitionOverrides = new List<int>(),
                IdleSettingsFile = reference.hasIdle ? reference.idle.settingsFile : null,
                IdleAnimation = reference.hasIdle ? reference.idle.animation : null,
                ReactionsTriggers = reference.reactions.Count > 0 ? reactionsTriggers : null,
                DefenderBlockTriggers = blockTriggers,
                ReactionSet = reference.reactionSet
            };
            foreach (var part in reference.parts)
            {
                int colon = part.IndexOf(':');
                string type = part.Substring(0, colon).Trim();
                string path = part.Substring(colon + 1).Trim();
                if (type == "Skeleton") request.SkeletonPart = path;
                else if (type == "Armor") request.ArmorPart = path;
                else if (type == "Helmet") request.HelmetPart = path;
                else if (type == "Weapon") request.WeaponPart = path;
            }

            MovePackage built;
            try { built = SfaMoveExporter.Build(request); }
            catch (Exception e)
            {
                report.problems.Add("Не удалось собрать: " + e.Message);
                return report;
            }

            Same(report, "кадров серии", reference.frameCount, built.frameCount);
            Same(report, "частота", reference.frameRate, built.frameRate);
            Same(report, "motion-кость", reference.motionBone, built.motionBone);
            Same(report, "костей", reference.boneParents.Length, built.boneParents.Length);
            if (reference.boneParents.Length == built.boneParents.Length)
                for (int i = 0; i < reference.boneParents.Length; i++)
                    if (reference.boneParents[i] != built.boneParents[i] || reference.boneHashes[i] != built.boneHashes[i])
                    {
                        report.problems.Add($"кость {i}: родитель/хэш {reference.boneParents[i]}/{reference.boneHashes[i]} против {built.boneParents[i]}/{built.boneHashes[i]}");
                        break;
                    }

            Same(report, "капсул", reference.capsules.Count, built.capsules.Count);
            for (int i = 0; i < Math.Min(reference.capsules.Count, built.capsules.Count); i++)
            {
                var a = reference.capsules[i];
                var b = built.capsules[i];
                if (a.name != b.name || a.hash != b.hash || a.collidable != b.collidable || Math.Abs(a.radius - b.radius) > 1e-4f)
                    report.problems.Add($"капсула {i}: {a.name}/{a.radius}/{a.collidable} против {b.name}/{b.radius}/{b.collidable}");
            }

            Same(report, "ударов", reference.segments.Count, built.segments.Count);
            for (int k = 0; k < Math.Min(reference.segments.Count, built.segments.Count); k++)
            {
                var a = reference.segments[k];
                var b = built.segments[k];
                string label = $"удар {k + 1} ({a.animation})";
                Same(report, label + ": кадров анимации", a.animationFrames, b.animationFrames);
                Same(report, label + ": начало", a.startFrame, b.startFrame);
                Same(report, label + ": кадров в серии", a.usedFrames, b.usedFrames);
                Same(report, label + ": начало в серии", a.seriesStart, b.seriesStart);
                CompareIntervals(report, label, a.intervals, b.intervals);
                for (int f = 0; f < Math.Min(a.frames.Count, b.frames.Count); f++)
                {
                    report.maxPose = Math.Max(report.maxPose, MaxDiff(a.frames[f].positions, b.frames[f].positions));
                    report.maxCapsule = Math.Max(report.maxCapsule, MaxDiff(a.capsuleFrames[f].points, b.capsuleFrames[f].points));
                }
            }

            Same(report, "Idle", reference.hasIdle ? 1 : 0, built.hasIdle ? 1 : 0);
            if (reference.hasIdle && built.hasIdle)
            {
                report.maxIdle = Math.Max(MaxDiff(reference.idle.positions, built.idle.positions), MaxDiff(reference.idle.points, built.idle.points));
                report.maxIdle = Math.Max(report.maxIdle, Math.Abs(reference.idle.motionX - built.idle.motionX));
            }

            Same(report, "реакций", reference.reactions.Count, built.reactions.Count);
            foreach (var a in reference.reactions)
            {
                var b = built.reactions.Find(r => r.name == a.name);
                if (b == null) { report.problems.Add("нет реакции " + a.name); continue; }
                Same(report, a.name + ": кадров", a.frameCount, b.frameCount);
                if (a.settingsFile != b.settingsFile) report.problems.Add($"{a.name}: файл {a.settingsFile} против {b.settingsFile}");
                for (int f = 0; f < Math.Min(a.frames.Count, b.frames.Count); f++)
                {
                    report.maxReaction = Math.Max(report.maxReaction, MaxDiff(a.frames[f].positions, b.frames[f].positions));
                    report.maxReaction = Math.Max(report.maxReaction, MaxDiff(a.capsuleFrames[f].points, b.capsuleFrames[f].points));
                }
            }

            if (report.maxPose > Tolerance) report.problems.Add($"поза расходится на {report.maxPose} Units");
            if (report.maxCapsule > Tolerance) report.problems.Add($"капсулы расходятся на {report.maxCapsule} Units");
            if (report.maxIdle > Tolerance) report.problems.Add($"Idle расходится на {report.maxIdle} Units");
            if (report.maxReaction > Tolerance) report.problems.Add($"реакции расходятся на {report.maxReaction} Units");
            report.passed = report.problems.Count == 0;
            return report;
        }

        private static void CompareIntervals(Report report, string label, List<PackageInterval> a, List<PackageInterval> b)
        {
            Same(report, label + ": интервалов", a.Count, b.Count);
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var x = a[i];
                var y = b[i];
                string tag = $"{label}, интервал {i} ({x.name ?? x.type})";
                if (N(x.name) != N(y.name) || N(x.type) != N(y.type) || x.hasStart != y.hasStart || x.start != y.start || x.hasEnd != y.hasEnd || x.end != y.end)
                    report.problems.Add($"{tag}: границы/имя различаются");
                if (string.Join(",", x.attackingParts) != string.Join(",", y.attackingParts)) report.problems.Add($"{tag}: AttackingParts");
                if (string.Join(",", x.hitTags) != string.Join(",", y.hitTags)) report.problems.Add($"{tag}: HitTags");
                if (x.blockDistance != y.blockDistance || string.Join(",", x.blockNames) != string.Join(",", y.blockNames)) report.problems.Add($"{tag}: Block");
                if (N(x.reactionHit) != N(y.reactionHit) || N(x.reactionCrit) != N(y.reactionCrit) || N(x.reactionBlockIn) != N(y.reactionBlockIn) ||
                    N(x.reactionBlockHit) != N(y.reactionBlockHit) || x.unblockable != y.unblockable)
                    report.problems.Add($"{tag}: реакции {x.reactionHit}/{x.reactionCrit}/{x.reactionBlockIn}/{x.reactionBlockHit} против {y.reactionHit}/{y.reactionCrit}/{y.reactionBlockIn}/{y.reactionBlockHit}");
            }
        }

        /// <summary>JsonUtility пишет null-строки как пустые — для сравнения это одно и то же.</summary>
        private static string N(string value) => value ?? string.Empty;

        private static void Same(Report report, string what, int expected, int actual)
        {
            if (expected != actual) report.problems.Add($"{what}: эталон {expected}, свой читатель {actual}");
        }

        private static float MaxDiff(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return float.PositiveInfinity;
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }
    }
}
