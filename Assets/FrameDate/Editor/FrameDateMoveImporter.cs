using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FrameDate.EditorTools
{
    /// <summary>
    /// Импорт пакета приёма (*.fdmove.json) из экспортёра проекта игры (SFA → FrameDate → Экспорт приёма в FrameDate).
    /// Приём игры — серия ударов; версия 2 пакета хранит каждый удар отдельно, версия 1 читается как серия из одного.
    /// Пакет — данные игры как есть, здесь они переводятся в модель FrameDate. Импорт односторонний: приём создаётся
    /// рядом с пакетом, повторный импорт перезаписывает его данные (ассет и ссылки на него сохраняются).
    /// </summary>
    public static class FrameDateMoveImporter
    {
        public const string ImportedFolder = "Assets/FrameDate/Imported";
        public const string Format = "framedate-move";
        public const int SupportedVersion = 4;

        /// <summary>Допуск раскладки продвижения на линейные интервалы Movement, Units.</summary>
        public const float MovementTolerance = 1f;

        /// <summary>Интервалы-события презентации: в фреймдату не входят.</summary>
        private static readonly string[] PresentationPrefixes = { "sfx_", "Sound_", "Character_Voice", "Face_" };

        public class Result
        {
            public FrameDateMove move;
            public FrameDatePose pose;
            public float movementMaxError;
            public readonly List<string> notes = new List<string>();
        }

        [Serializable]
        private class MovePackage
        {
            public string format;
            public int version;
            public string name;
            public string exportedAt;
            public string settingsFile;
            public List<string> parts = new List<string>();
            public int frameRate;
            public int frameCount;
            public float forwardSign;
            public int motionBone;
            public int[] boneParents;
            public uint[] boneHashes;
            public List<PackageSegment> segments = new List<PackageSegment>();
            public List<PackageCapsule> capsules = new List<PackageCapsule>();
            public bool hasIdle;
            public PackageIdle idle = new PackageIdle();
            public string reactionSet;
            public List<PackageReaction> reactions = new List<PackageReaction>();

            // Версия 1: один удар без сегментов.
            public string animation;
            public string animationFile;
            public List<string> groups = new List<string>();
            public List<PackageInterval> intervals = new List<PackageInterval>();
            public List<PackageFrame> frames = new List<PackageFrame>();
        }

        [Serializable]
        private class PackageSegment
        {
            public string animation;
            public string animationFile;
            public List<string> groups = new List<string>();
            public int animationFrames;
            public int startFrame;
            public int usedFrames;
            public int seriesStart;
            public string transition;
            public List<PackageInterval> intervals = new List<PackageInterval>();
            public List<PackageFrame> frames = new List<PackageFrame>();
            public List<PackageCapsuleFrame> capsuleFrames = new List<PackageCapsuleFrame>();
        }

        [Serializable]
        private class PackageCapsule
        {
            public string name;
            public uint hash;
            public string part;
            public float radius;
            public bool collidable;
        }

        [Serializable]
        private class PackageCapsuleFrame
        {
            public float[] points;
        }

        [Serializable]
        private class PackageReaction
        {
            public string name;
            public string settingsFile;
            public string animationFile;
            public List<string> groups = new List<string>();
            public int frameCount;
            public List<PackageInterval> intervals = new List<PackageInterval>();
            public List<PackageFrame> frames = new List<PackageFrame>();
            public List<PackageCapsuleFrame> capsuleFrames = new List<PackageCapsuleFrame>();
        }

        [Serializable]
        private class PackageIdle
        {
            public string animation;
            public string settingsFile;
            public float motionX;
            public float[] positions;
            public float[] points;
        }

        [Serializable]
        private class PackageInterval
        {
            public string name;
            public string type;
            public bool hasStart;
            public int start;
            public bool hasEnd;
            public int end;
            public List<string> attackingParts = new List<string>();
            public List<string> hitTags = new List<string>();
            public bool hasBlock;
            public int blockDistance;
            public List<string> blockNames = new List<string>();
            public string reactionHit;
            public string reactionCrit;
            public string reactionBlockIn;
            public string reactionBlockHit;
            public bool unblockable;
        }

        [Serializable]
        private class PackageFrame
        {
            public float[] positions;
        }

/// <summary>Отложить SaveAssets: при массовом импорте вызывающий сохраняет ассеты сам, пачками.</summary>
        public static bool DeferSave;

        public static Result Import(string jsonPath)
        {
            string assetPath = ToAssetPath(jsonPath);
            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            return ImportJson(File.ReadAllText(jsonPath), folder, folder, jsonPath);
        }

        /// <summary>
        /// Импорт пакета из строки: приём пишется в moveFolder, его поза — в poseFolder (папки должны существовать).
        /// label — для сообщений об ошибках.
        /// </summary>
        public static Result ImportJson(string json, string moveFolder, string poseFolder, string label)
        {
            var package = JsonUtility.FromJson<MovePackage>(json);
            if (package == null || package.format != Format)
                throw new InvalidDataException($"{label}: это не пакет приёма FrameDate.");
            if (package.version > SupportedVersion)
                throw new InvalidDataException($"{label}: версия пакета {package.version}, импортёр понимает до {SupportedVersion}.");
            Normalize(package);
            Check(package, label);

            var result = new Result();
            string baseName = SafeName(package.name);

            result.pose = LoadOrCreate<FrameDatePose>(poseFolder + "/" + baseName + "_Pose.asset");
            FillPose(result.pose, package);

            result.move = LoadOrCreate<FrameDateMove>(moveFolder + "/" + baseName + ".asset");
            FillMove(result.move, result.pose, package, result);
            FillReactions(result, package);

            EditorUtility.SetDirty(result.pose);
            EditorUtility.SetDirty(result.move);
            if (!DeferSave) AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>Версия 1 — серия из одного удара целиком.</summary>
        private static void Normalize(MovePackage package)
        {
            if (package.version >= 2) return;
            package.name = package.animation;
            package.segments = new List<PackageSegment>
            {
                new PackageSegment
                {
                    animation = package.animation,
                    animationFile = package.animationFile,
                    groups = package.groups,
                    animationFrames = package.frameCount,
                    startFrame = 0,
                    usedFrames = package.frameCount,
                    seriesStart = 0,
                    transition = "пакет версии 1: один удар целиком",
                    intervals = package.intervals,
                    frames = package.frames
                }
            };
        }

        private static void Check(MovePackage package, string jsonPath)
        {
            if (package.segments.Count == 0) throw new InvalidDataException($"{jsonPath}: в серии нет ударов.");
            int expected = 0;
            int stride = package.boneParents.Length * 3;
            foreach (var segment in package.segments)
            {
                if (segment.seriesStart != expected)
                    throw new InvalidDataException($"{jsonPath}: удар {segment.animation} начинается с кадра {segment.seriesStart}, ожидался {expected}.");
                if (segment.frames.Count != segment.animationFrames)
                    throw new InvalidDataException($"{jsonPath}: у {segment.animation} кадров {segment.animationFrames}, в позе {segment.frames.Count}.");
                if (segment.startFrame + segment.usedFrames > segment.animationFrames)
                    throw new InvalidDataException($"{jsonPath}: {segment.animation} берёт кадры за концом анимации.");
                foreach (var frame in segment.frames)
                    if (frame.positions == null || frame.positions.Length != stride)
                        throw new InvalidDataException($"{jsonPath}: у {segment.animation} кадр позы не на {package.boneParents.Length} костей.");
                if (package.capsules.Count > 0)
                {
                    if (segment.capsuleFrames.Count != segment.animationFrames)
                        throw new InvalidDataException($"{jsonPath}: у {segment.animation} кадров капсул {segment.capsuleFrames.Count} вместо {segment.animationFrames}.");
                    foreach (var frame in segment.capsuleFrames)
                        if (frame.points == null || frame.points.Length != package.capsules.Count * 4)
                            throw new InvalidDataException($"{jsonPath}: у {segment.animation} кадр капсул не на {package.capsules.Count} капсул.");
                }
                expected += segment.usedFrames;
            }
            if (expected != package.frameCount)
                throw new InvalidDataException($"{jsonPath}: кадров серии {package.frameCount}, по ударам {expected}.");
        }

        /// <summary>
        /// Поза серии: кадры ударов подряд. Каждый следующий удар сдвинут по X так, чтобы его motion-кость на первом
        /// кадре совпала с motion-костью предыдущего на кадре перехода — как Align по тазу в игре (ось X).
        /// </summary>
        private static void FillPose(FrameDatePose pose, MovePackage package)
        {
            pose.sourceAnimation = package.name;
            pose.sourceAnimationFile = string.Join(" + ", package.segments.ConvertAll(s => s.animationFile));
            pose.sourceSettingsFile = package.settingsFile;
            pose.exportedAt = package.exportedAt;
            pose.sourceParts = new List<string>(package.parts);
            pose.frameRate = package.frameRate;
            pose.frameCount = package.frameCount;
            pose.boneCount = package.boneParents.Length;
            pose.motionBone = package.motionBone;
            pose.forwardSign = package.forwardSign;
            pose.boneParents = (int[])package.boneParents.Clone();
            pose.segments = new List<FrameDatePose.PoseSegment>();

            int stride = pose.boneCount * 3;
            int motionX = package.motionBone * 3;
            pose.positions = new float[package.frameCount * stride];
            float offset = 0f;

            int capsuleCount = package.capsules.Count;
            int capsuleStride = capsuleCount * 4;
            pose.capsules = package.capsules.ConvertAll(c => new FrameDatePose.PoseCapsule
            {
                name = c.name, hash = c.hash, part = c.part, radius = c.radius, collidable = c.collidable
            });
            pose.capsulePoints = new float[package.frameCount * capsuleStride];

            pose.hasIdle = package.hasIdle && package.idle != null && package.idle.points != null && package.idle.points.Length == capsuleStride;
            pose.idleAnimation = pose.hasIdle ? package.idle.animation : null;
            pose.idleMotionX = pose.hasIdle ? package.idle.motionX : 0f;
            pose.idlePositions = pose.hasIdle && package.idle.positions != null ? (float[])package.idle.positions.Clone() : new float[0];
            pose.idlePoints = pose.hasIdle ? (float[])package.idle.points.Clone() : new float[0];

            for (int k = 0; k < package.segments.Count; k++)
            {
                var segment = package.segments[k];
                if (k > 0)
                {
                    var previous = package.segments[k - 1];
                    int handover = Mathf.Min(previous.startFrame + previous.usedFrames, previous.animationFrames - 1);
                    float previousX = previous.frames[handover].positions[motionX] + offset;
                    offset = previousX - segment.frames[segment.startFrame].positions[motionX];
                }

                for (int g = 0; g < segment.usedFrames; g++)
                {
                    var source = segment.frames[segment.startFrame + g].positions;
                    int target = (segment.seriesStart + g) * stride;
                    for (int i = 0; i < stride; i += 3)
                    {
                        pose.positions[target + i] = source[i] + offset;
                        pose.positions[target + i + 1] = source[i + 1];
                        pose.positions[target + i + 2] = source[i + 2];
                    }

                    // Капсулы сдвигаются по X так же, как кости этого удара.
                    if (capsuleCount == 0) continue;
                    var points = segment.capsuleFrames[segment.startFrame + g].points;
                    int capsuleTarget = (segment.seriesStart + g) * capsuleStride;
                    for (int i = 0; i < capsuleStride; i += 2)
                    {
                        pose.capsulePoints[capsuleTarget + i] = points[i] + offset;
                        pose.capsulePoints[capsuleTarget + i + 1] = points[i + 1];
                    }
                }

                pose.segments.Add(new FrameDatePose.PoseSegment
                {
                    animation = segment.animation,
                    animationFile = segment.animationFile,
                    seriesStart = segment.seriesStart,
                    length = segment.usedFrames,
                    startFrame = segment.startFrame,
                    transition = segment.transition
                });
            }
        }

        private static void FillMove(FrameDateMove move, FrameDatePose pose, MovePackage package, Result result)
        {
            move.moveName = package.name;
            move.frameRate = package.frameRate;
            move.durationFrames = package.frameCount;
            move.pose = pose;

            if (move.metrics == null)
                move.metrics = AssetDatabase.LoadAssetAtPath<FrameDateMetrics>(FrameDateMetrics.DefaultAssetPath);
            float half = move.Metrics.startDistance * 0.5f;
            move.attackerPosition = new Vector2(-half, 0f);
            move.targetPosition = new Vector2(half, 0f);
            move.attackerBody = new ActorBody();
            move.targetBody = new ActorBody();

            move.tracks = BuildIntervalTracks(package, result);
            move.tracks.Add(BuildMovementTrack(move, pose, result));

            for (int k = 0; k < package.segments.Count; k++)
            {
                var s = package.segments[k];
                result.notes.Insert(k, $"Удар {k + 1}: {s.animation} — кадры серии [{s.seriesStart},{s.seriesStart + s.usedFrames}), " +
                                       $"своей анимации [{s.startFrame},{s.startFrame + s.usedFrames}) из {s.animationFrames}; {s.transition}.");
            }
            if (package.frameRate != 30)
                result.notes.Add($"Частота {package.frameRate} FPS: кадры игры — тики симуляции, 1 кадр = 1/{package.frameRate} с.");
            if (package.capsules.Count > 0)
                result.notes.Add($"Капсулы игры: {package.capsules.Count}, по ним можно попасть — {package.capsules.FindAll(c => c.collidable).Count}. " +
                                 (pose.hasIdle ? $"Цель — капсулы Idle героя ({pose.idleAnimation})." : "Idle нет — цель капсулой тела FrameDate."));
        }

        private static List<FrameTrack> BuildIntervalTracks(MovePackage package, Result result)
        {
            var markers = new List<FrameTrack>();
            FrameTrack attack = null;
            var states = new List<FrameTrack>();
            var marks = new List<FrameTrack>();
            var byName = new Dictionary<string, FrameTrack>();
            var skipped = new SortedSet<string>();

            for (int k = 0; k < package.segments.Count; k++)
            {
                var segment = package.segments[k];
                int shift = segment.seriesStart - segment.startFrame;
                int usedEnd = segment.startFrame + segment.usedFrames;

                // Пометка удара серии — видно, где кончается один удар и начинается следующий.
                if (package.segments.Count > 1)
                {
                    var marker = NewTrack($"Удар {k + 1}: {segment.animation}", new Color(0.62f, 0.64f, 0.70f), TrackRole.None);
                    marker.spans.Add(new FrameSpan(segment.seriesStart, segment.seriesStart + segment.usedFrames));
                    markers.Add(marker);
                }

                foreach (var interval in segment.intervals)
                {
                    int start = interval.hasStart ? interval.start : 0;
                    int endExcluded = (interval.hasEnd ? interval.end : segment.animationFrames - 1) + 1;
                    string label = interval.type == "Attack" ? "Attack" : interval.name;

                    // Переход в следующий удар обрывает интервалы текущего.
                    int from = Mathf.Max(start, segment.startFrame);
                    int to = Mathf.Min(endExcluded, usedEnd);
                    if (to <= from)
                    {
                        if (interval.type == "Attack")
                            result.notes.Add($"{segment.animation}: Attack {start}..{endExcluded - 1} не входит в серию — обрезан переходом.");
                        continue;
                    }
                    var span = new FrameSpan(from + shift, to + shift);

                    if (interval.type == "Attack")
                    {
                        if (attack == null)
                            attack = NewTrack("Attack", new Color(1.00f, 0.35f, 0.30f), TrackRole.Attack);
                        // Атаки разных ударов не сливаются даже встык: у каждой свой контакт.
                        span.attackingParts = new List<string>(interval.attackingParts);
                        span.reactionHit = interval.reactionHit;
                        span.reactionCrit = interval.reactionCrit;
                        span.reactionBlockIn = interval.reactionBlockIn;
                        span.reactionBlockHit = interval.reactionBlockHit;
                        span.blockDistance = interval.blockDistance;
                        span.unblockable = interval.unblockable;
                        if (!string.IsNullOrEmpty(interval.reactionHit))
                            result.notes.Add($"Реакции удара [{span.startFrame},{span.endFrame}): попадание {interval.reactionHit}, крит {Or(interval.reactionCrit)}, " +
                                             (interval.unblockable ? "блок невозможен (Unblockable)." :
                                                 $"блок {Or(interval.reactionBlockIn)} / {Or(interval.reactionBlockHit)} (дистанция {interval.blockDistance})." ));
                        AddMerged(attack, span, result, false);
                        if (interval.attackingParts.Count == 0)
                            result.notes.Add($"Attack [{span.startFrame},{span.endFrame}) ({segment.animation}): у игры нет AttackingParts — hitbox по умолчанию FrameDate.");
                        else if (package.capsules.Count == 0)
                            result.notes.Add($"Attack [{span.startFrame},{span.endFrame}) ({segment.animation}): {string.Join(", ", interval.attackingParts)} — в пакете нет капсул, hitbox по умолчанию.");
                        else
                        {
                            var missing = interval.attackingParts.FindAll(n => package.capsules.FindIndex(c => string.Equals(c.name, n, StringComparison.OrdinalIgnoreCase)) < 0);
                            result.notes.Add($"Attack [{span.startFrame},{span.endFrame}) ({segment.animation}): hitbox — капсулы игры {string.Join(", ", interval.attackingParts)}" +
                                             (missing.Count > 0 ? $", нет в модели: {string.Join(", ", missing)}" : "") + ".");
                        }
                        continue;
                    }

                    if (string.IsNullOrEmpty(interval.name))
                    {
                        result.notes.Add($"{segment.animation}: интервал {start}..{endExcluded - 1} без имени и типа — пропущен.");
                        continue;
                    }

                    if (IsPresentation(interval.name))
                    {
                        skipped.Add(interval.name);
                        continue;
                    }

                    FrameTrack track;
                    if (!byName.TryGetValue(interval.name, out track))
                    {
                        var role = RoleFor(interval.name);
                        track = NewTrack(interval.name, ColorFor(interval.name, role), role);
                        byName[interval.name] = track;
                        (role == TrackRole.None ? marks : states).Add(track);
                    }
                    AddMerged(track, span, result, true);
                }
            }

            if (skipped.Count > 0)
                result.notes.Add("Презентация (звук, голос, мимика) пропущена: " + string.Join(", ", skipped) + ".");

            var tracks = new List<FrameTrack>();
            if (attack != null) tracks.Add(attack);
            tracks.AddRange(states);
            tracks.AddRange(markers);
            tracks.AddRange(marks);
            return tracks;
        }

        /// <summary>
        /// Продвижение — траектория motion-кости вперёд, разложенная на линейные интервалы Movement:
        /// узлы ставятся жадно, пока все промежуточные кадры лежат на прямой в пределах допуска.
        /// </summary>
        private static FrameTrack BuildMovementTrack(FrameDateMove move, FrameDatePose pose, Result result)
        {
            var track = NewTrack("Продвижение", new Color(0.95f, 0.95f, 0.55f), TrackRole.Movement);
            int count = pose.frameCount;
            var forward = new float[count];
            for (int f = 0; f < count; f++) forward[f] = pose.MotionForward(f);

            int a = 0;
            while (a < count - 1)
            {
                int b = a + 1;
                while (b + 1 <= count - 1 && Fits(forward, a, b + 1)) b++;
                float distance = forward[b] - forward[a];
                if (Mathf.Abs(distance) > 0.01f)
                    track.spans.Add(new FrameSpan(a, b) { distance = distance, profile = MoveProfile.Linear });
                a = b;
            }

            // Проверка: расчёт FrameDate против траектории игры на каждом кадре.
            move.tracks.Add(track);
            float maxError = 0f;
            for (int f = 0; f < count; f++)
                maxError = Mathf.Max(maxError, Mathf.Abs(FrameDateSimulation.AuthoredOffset(move, Actor.Attacker, f) - forward[f]));
            move.tracks.Remove(track);
            result.movementMaxError = maxError;
            result.notes.Add($"Продвижение: {track.spans.Count} интервалов, итог {forward[count - 1]:0.#} Units (пик {Peak(forward):0.#}), " +
                             $"отклонение от игры не больше {maxError:0.##} Units.");
            return track;
        }

        private static string Or(string value) => string.IsNullOrEmpty(value) ? "—" : value;

        /// <summary>
        /// Пополняет каталог реакций набора (Assets/FrameDate/Reactions/<набор>_Reactions.asset) реакциями пакета:
        /// одноимённые обновляются, новые добавляются. Все реакции каталога — на одном скелете и капсулах.
        /// </summary>
        private static void FillReactions(Result result, MovePackage package)
        {
            if (package.reactions == null || package.reactions.Count == 0) return;
            if (!AssetDatabase.IsValidFolder(FrameDateReactionCatalog.Folder)) AssetDatabase.CreateFolder("Assets/FrameDate", "Reactions");
            string setName = string.IsNullOrEmpty(package.reactionSet) ? "Default" : package.reactionSet;
            var catalog = LoadOrCreate<FrameDateReactionCatalog>(FrameDateReactionCatalog.Folder + "/" + SafeName(setName) + "_Reactions.asset");

            int bones = package.boneParents.Length;
            if (catalog.boneCount > 0 && (catalog.boneCount != bones || catalog.capsules.Count != package.capsules.Count))
                throw new InvalidDataException($"Каталог реакций «{setName}» собран на другом скелете: {catalog.boneCount} костей и {catalog.capsules.Count} капсул, в пакете {bones} и {package.capsules.Count}.");

            catalog.setName = setName;
            catalog.boneCount = bones;
            catalog.motionBone = package.motionBone;
            catalog.forwardSign = package.forwardSign;
            catalog.boneParents = (int[])package.boneParents.Clone();
            catalog.capsules = package.capsules.ConvertAll(c => new FrameDatePose.PoseCapsule
            {
                name = c.name, hash = c.hash, part = c.part, radius = c.radius, collidable = c.collidable
            });

            int stride = bones * 3;
            int capsuleStride = package.capsules.Count * 4;
            var names = new List<string>();
            foreach (var source in package.reactions)
            {
                var reaction = catalog.Find(source.name);
                if (reaction == null)
                {
                    reaction = new FrameDateReactionCatalog.Reaction();
                    catalog.reactions.Add(reaction);
                }
                reaction.name = source.name;
                reaction.settingsFile = source.settingsFile;
                reaction.animationFile = source.animationFile;
                reaction.groups = new List<string>(source.groups);
                reaction.frameCount = source.frameCount;
                reaction.positions = new float[source.frameCount * stride];
                reaction.capsulePoints = new float[source.frameCount * capsuleStride];
                for (int f = 0; f < source.frameCount; f++)
                {
                    Array.Copy(source.frames[f].positions, 0, reaction.positions, f * stride, stride);
                    if (capsuleStride > 0) Array.Copy(source.capsuleFrames[f].points, 0, reaction.capsulePoints, f * capsuleStride, capsuleStride);
                }
                reaction.intervals = new List<FrameDateReactionCatalog.ReactionInterval>();
                foreach (var interval in source.intervals)
                {
                    if (string.IsNullOrEmpty(interval.name) || IsPresentation(interval.name)) continue;
                    int start = interval.hasStart ? interval.start : 0;
                    int end = (interval.hasEnd ? interval.end : source.frameCount - 1) + 1;
                    reaction.intervals.Add(new FrameDateReactionCatalog.ReactionInterval { name = interval.name, start = start, end = Mathf.Min(end, source.frameCount) });
                }
                names.Add($"{source.name} ({source.frameCount} к.)");
            }
            catalog.reactions.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            EditorUtility.SetDirty(catalog);
            result.move.reactions = catalog;
            result.notes.Add($"Каталог реакций «{setName}»: {catalog.reactions.Count} реакций; из этого пакета — {string.Join(", ", names)}.");
        }

        private static bool Fits(float[] values, int a, int b)
        {
            for (int f = a + 1; f < b; f++)
            {
                float line = values[a] + (values[b] - values[a]) * (f - a) / (float)(b - a);
                if (Mathf.Abs(values[f] - line) > MovementTolerance) return false;
            }
            return true;
        }

        private static float Peak(float[] values)
        {
            float peak = 0f;
            foreach (float v in values) if (Mathf.Abs(v) > Mathf.Abs(peak)) peak = v;
            return peak;
        }

        /// <summary>
        /// Добавляет интервал, сливая с пересекающимися (и стыкующимися, если touching): на дорожке интервалы не пересекаются.
        /// </summary>
        private static void AddMerged(FrameTrack track, FrameSpan span, Result result, bool touching)
        {
            for (int i = track.spans.Count - 1; i >= 0; i--)
            {
                var other = track.spans[i];
                bool joins = touching
                    ? span.startFrame <= other.endFrame && other.startFrame <= span.endFrame
                    : span.startFrame < other.endFrame && other.startFrame < span.endFrame;
                if (!joins) continue;
                bool seam = span.startFrame == other.endFrame || other.startFrame == span.endFrame;
                if (!seam)
                    result.notes.Add($"«{track.name}»: [{other.startFrame},{other.endFrame}) и [{span.startFrame},{span.endFrame}) слиты.");
                var merged = new FrameSpan(Mathf.Min(span.startFrame, other.startFrame), Mathf.Max(span.endFrame, other.endFrame));
                merged.attackingParts = new List<string>(span.attackingParts);
                merged.reactionHit = string.IsNullOrEmpty(span.reactionHit) ? other.reactionHit : span.reactionHit;
                merged.reactionCrit = string.IsNullOrEmpty(span.reactionCrit) ? other.reactionCrit : span.reactionCrit;
                merged.reactionBlockIn = string.IsNullOrEmpty(span.reactionBlockIn) ? other.reactionBlockIn : span.reactionBlockIn;
                merged.reactionBlockHit = string.IsNullOrEmpty(span.reactionBlockHit) ? other.reactionBlockHit : span.reactionBlockHit;
                merged.blockDistance = Mathf.Max(span.blockDistance, other.blockDistance);
                merged.unblockable = span.unblockable || other.unblockable;
                foreach (var part in other.attackingParts) if (!merged.attackingParts.Contains(part)) merged.attackingParts.Add(part);
                span = merged;
                track.spans.RemoveAt(i);
            }
            track.spans.Add(span);
            track.spans.Sort((x, y) => x.startFrame.CompareTo(y.startFrame));
        }

        private static TrackRole RoleFor(string name)
        {
            switch (name)
            {
                case "Uninterrupt": return TrackRole.Uninterrupt;
                case "Blocking": return TrackRole.Blocking;
                case "Throwable": return TrackRole.Throwable;
                case "Invulnerable": return TrackRole.Invulnerable;
                case "Unbreakable": return TrackRole.Unbreakable;
                case "Invincible": return TrackRole.Invincible;
                default: return TrackRole.None;
            }
        }

        private static Color ColorFor(string name, TrackRole role)
        {
            switch (role)
            {
                case TrackRole.Uninterrupt: return new Color(1.00f, 0.75f, 0.25f);
                case TrackRole.Blocking: return new Color(0.35f, 0.70f, 1.00f);
                case TrackRole.Throwable: return new Color(0.75f, 0.55f, 1.00f);
                case TrackRole.Invulnerable: return new Color(0.45f, 0.90f, 0.60f);
                case TrackRole.Unbreakable: return new Color(0.90f, 0.55f, 0.75f);
                case TrackRole.Invincible: return new Color(0.95f, 0.95f, 0.95f);
            }
            // Пометкам — стабильный цвет по имени, приглушённый.
            float hue = (Mathf.Abs(name.GetHashCode()) % 360) / 360f;
            return Color.HSVToRGB(hue, 0.35f, 0.85f);
        }

        private static FrameTrack NewTrack(string name, Color color, TrackRole role) =>
            new FrameTrack { name = name, color = color, actor = Actor.Attacker, role = role };

        private static bool IsPresentation(string name)
        {
            foreach (var prefix in PresentationPrefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;
            var created = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private static string ToAssetPath(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string data = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (full.StartsWith(data, StringComparison.OrdinalIgnoreCase)) return "Assets" + full.Substring(data.Length);
            if (path.Replace('\\', '/').StartsWith("Assets/")) return path.Replace('\\', '/');
            throw new InvalidOperationException($"Пакет должен лежать внутри Assets проекта FrameDate: {path}");
        }

        private static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
