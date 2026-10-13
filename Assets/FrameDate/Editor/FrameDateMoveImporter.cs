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
        public const int SupportedVersion = 2;

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
        }

        [Serializable]
        private class PackageFrame
        {
            public float[] positions;
        }

        public static Result Import(string jsonPath)
        {
            string assetPath = ToAssetPath(jsonPath);
            var package = JsonUtility.FromJson<MovePackage>(File.ReadAllText(jsonPath));
            if (package == null || package.format != Format)
                throw new InvalidDataException($"{jsonPath}: это не пакет приёма FrameDate.");
            if (package.version > SupportedVersion)
                throw new InvalidDataException($"{jsonPath}: версия пакета {package.version}, импортёр понимает до {SupportedVersion}.");
            Normalize(package);
            Check(package, jsonPath);

            var result = new Result();
            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string baseName = SafeName(package.name);

            result.pose = LoadOrCreate<FrameDatePose>(folder + "/" + baseName + "_Pose.asset");
            FillPose(result.pose, package);

            result.move = LoadOrCreate<FrameDateMove>(folder + "/" + baseName + ".asset");
            FillMove(result.move, result.pose, package, result);

            EditorUtility.SetDirty(result.pose);
            EditorUtility.SetDirty(result.move);
            AssetDatabase.SaveAssets();
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
                        AddMerged(attack, span, result, false);
                        if (interval.attackingParts.Count > 0)
                            result.notes.Add($"Attack [{span.startFrame},{span.endFrame}) ({segment.animation}): hitbox в игре — {string.Join(", ", interval.attackingParts)}; у нас пока капсула по умолчанию.");
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
                span = new FrameSpan(Mathf.Min(span.startFrame, other.startFrame), Mathf.Max(span.endFrame, other.endFrame));
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
