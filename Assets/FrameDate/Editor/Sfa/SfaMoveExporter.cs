using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FrameDate.EditorTools.Sfa
{
    // Пакет приёма — тот же формат v4, что пишет экспортёр в проекте игры (Assets/FrameDateExport): импортёр FrameDate
    // один на оба пути, а пакеты двух путей можно сверять поле в поле.

    [Serializable]
    public class MovePackage
    {
        public string format = "framedate-move";
        public int version = 4;
        public string name;
        public string exportedAt;
        public string settingsFile;
        public List<string> parts = new List<string>();
        public int frameRate = SfaMoveExporter.ReferenceFps;
        public int frameCount;
        public float forwardSign = -1f;
        public int motionBone;
        public int[] boneParents;
        public uint[] boneHashes;
        public List<PackageSegment> segments = new List<PackageSegment>();
        public List<PackageCapsule> capsules = new List<PackageCapsule>();
        public bool hasIdle;
        public PackageIdle idle = new PackageIdle();
        public string reactionSet;
        public List<PackageReaction> reactions = new List<PackageReaction>();
    }

    [Serializable]
    public class PackageReaction
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
    public class PackageSegment
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
    public class PackageInterval
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
    public class PackageFrame
    {
        public float[] positions;
    }

    [Serializable]
    public class PackageCapsule
    {
        public string name;
        public uint hash;
        public string part;
        public float radius;
        public bool collidable;
    }

    [Serializable]
    public class PackageCapsuleFrame
    {
        public float[] points;
    }

    [Serializable]
    public class PackageIdle
    {
        public string animation;
        public string settingsFile;
        public float motionX;
        public float[] positions;
        public float[] points;
    }

    public class TransitionSource
    {
        public int FrameShift;
        public readonly List<string> Animations = new List<string>();
    }

    public class AnimationEntry
    {
        public string Name;
        public string FileName;
        public readonly List<string> Groups = new List<string>();
        public readonly List<PackageInterval> Intervals = new List<PackageInterval>();
        public readonly List<TransitionSource> TransitionsFrom = new List<TransitionSource>();
    }

    /// <summary>
    /// Список приёмов из settings_*.yaml: Animation, FileName, Groups, Transitions и Intervals
    /// (Name, Type, Start, End, AttackingParts, HitTags, Block: Distance, Name). Остальное пропускается.
    /// </summary>
    public static class MoveSettingsReader
    {
        public static List<AnimationEntry> Read(string path)
        {
            var result = new List<AnimationEntry>();
            AnimationEntry entry = null;
            PackageInterval interval = null;
            TransitionSource transition = null;
            string section = null;
            string list = null;

            foreach (var raw in File.ReadAllLines(path))
            {
                string line = StripComment(raw);
                string text = line.Trim();
                if (text.Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;

                if (indent == 2 && text.StartsWith("- Animation:"))
                {
                    entry = new AnimationEntry { Name = ValueOf(text.Substring(2)) };
                    result.Add(entry);
                    section = null;
                    interval = null;
                    transition = null;
                    continue;
                }
                if (entry == null) continue;

                if (indent == 4)
                {
                    section = null;
                    interval = null;
                    transition = null;
                    list = null;
                    if (text.StartsWith("FileName:")) entry.FileName = ValueOf(text);
                    else if (text == "Groups:") section = "Groups";
                    else if (text == "Intervals:") section = "Intervals";
                    else if (text == "Transitions:") section = "Transitions";
                    continue;
                }

                if (section == "Groups" && indent == 6 && text.StartsWith("- "))
                {
                    entry.Groups.Add(text.Substring(2).Trim());
                    continue;
                }

                if (section == "Transitions")
                {
                    if (indent == 6 && text.StartsWith("- "))
                    {
                        transition = new TransitionSource();
                        entry.TransitionsFrom.Add(transition);
                        list = null;
                        ApplyTransitionKey(transition, text.Substring(2).Trim(), ref list);
                    }
                    else if (transition != null && indent == 8)
                    {
                        list = null;
                        ApplyTransitionKey(transition, text, ref list);
                    }
                    else if (transition != null && indent == 10 && list == "Animations" && text.StartsWith("- "))
                    {
                        transition.Animations.Add(text.Substring(2).Trim());
                    }
                    continue;
                }

                if (section != "Intervals") continue;

                if (indent == 6 && text.StartsWith("- "))
                {
                    interval = new PackageInterval();
                    entry.Intervals.Add(interval);
                    list = null;
                    ApplyIntervalKey(interval, text.Substring(2).Trim());
                    continue;
                }
                if (interval == null) continue;

                if (indent == 8)
                {
                    list = null;
                    if (text == "AttackingParts:") list = "AttackingParts";
                    else if (text == "HitTags:") list = "HitTags";
                    else if (text == "Block:") { list = "Block"; interval.hasBlock = true; }
                    else ApplyIntervalKey(interval, text);
                    continue;
                }

                if (indent == 10 && text.StartsWith("- "))
                {
                    if (list == "AttackingParts") interval.attackingParts.Add(text.Substring(2).Trim());
                    else if (list == "HitTags") interval.hitTags.Add(text.Substring(2).Trim());
                    continue;
                }

                if (list == "Block" || list == "BlockNames")
                {
                    int number;
                    if (indent == 10 && text.StartsWith("Distance:") && int.TryParse(ValueOf(text), out number)) interval.blockDistance = number;
                    else if (indent == 10 && text.StartsWith("Name:"))
                    {
                        string inline = ValueOf(text);
                        if (inline.Length > 0) interval.blockNames.Add(inline);
                        list = "BlockNames";
                    }
                    else if (indent == 12 && list == "BlockNames" && text.StartsWith("- ")) interval.blockNames.Add(text.Substring(2).Trim());
                }
            }
            return result;
        }

        private static void ApplyTransitionKey(TransitionSource transition, string text, ref string list)
        {
            int colon = text.IndexOf(':');
            if (colon < 0) return;
            string key = text.Substring(0, colon).Trim();
            string value = text.Substring(colon + 1).Trim();
            int number;
            if (key == "FrameShift" && int.TryParse(value, out number)) transition.FrameShift = number;
            else if (key == "Animations") list = "Animations";
        }

        private static void ApplyIntervalKey(PackageInterval interval, string text)
        {
            int colon = text.IndexOf(':');
            if (colon < 0) return;
            string key = text.Substring(0, colon).Trim();
            string value = text.Substring(colon + 1).Trim();
            int number;
            switch (key)
            {
                case "Name": interval.name = value; break;
                case "Type": interval.type = value; break;
                case "Start": if (int.TryParse(value, out number)) { interval.hasStart = true; interval.start = number; } break;
                case "End": if (int.TryParse(value, out number)) { interval.hasEnd = true; interval.end = number; } break;
            }
        }

        private static string ValueOf(string text)
        {
            int colon = text.IndexOf(':');
            return colon < 0 ? text.Trim() : text.Substring(colon + 1).Trim();
        }

        private static string StripComment(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#")) return string.Empty;
            int hash = line.IndexOf(" #", StringComparison.Ordinal);
            return hash >= 0 ? line.Substring(0, hash) : line;
        }
    }

    /// <summary>
    /// Правила серии, как в игре: следующий удар (ATTACK.DOUBLE/TRIPLE) — на IntervalEnd интервала Transitional
    /// вне Transition_To_Another_Attack; IntervalEnd приходит на кадре End + 1. Если предыдущий удар есть в Transitions
    /// следующего, игра продолжает следующий с текущего кадра предыдущего (FrameShift из YAML игра не читает).
    /// </summary>
    public static class SeriesRules
    {
        public const string Transitional = "Transitional";
        public const string TransitionBlock = "Transition_To_Another_Attack";

        public static int TransitionFrame(AnimationEntry entry, int frameCount, out string rule)
        {
            int best = -1;
            foreach (var interval in entry.Intervals)
            {
                if (interval.name != Transitional) continue;
                int endExcluded = interval.hasEnd ? interval.end + 1 : frameCount;
                if (IsInside(entry, TransitionBlock, endExcluded, frameCount)) continue;
                if (best < 0 || endExcluded < best) best = endExcluded;
            }
            if (best < 0 || best >= frameCount)
            {
                rule = "нет перехода по Transitional — удар целиком";
                return frameCount;
            }
            rule = $"конец Transitional на кадре {best} вне {TransitionBlock}";
            return best;
        }

        public static int StartFrame(AnimationEntry next, string previous, int previousFrame) =>
            IsTransitionSource(next, previous) ? previousFrame : 0;

        public static bool IsTransitionSource(AnimationEntry next, string previous)
        {
            foreach (var source in next.TransitionsFrom)
                foreach (var name in source.Animations)
                    if (string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static AnimationEntry FindNext(List<AnimationEntry> entries, AnimationEntry current)
        {
            AnimationEntry fallback = null;
            foreach (var candidate in entries)
            {
                if (candidate == current || !candidate.Groups.Contains("ATTACK")) continue;
                if (!IsTransitionSource(candidate, current.Name)) continue;
                if (candidate.Groups.Contains("ATTACK_DOUBLE") || candidate.Groups.Contains("ATTACK_TRIPLE")) return candidate;
                if (fallback == null) fallback = candidate;
            }
            return fallback;
        }

        public static List<string> AutoSeries(List<AnimationEntry> entries, string first)
        {
            var series = new List<string>();
            var current = entries.Find(e => e.Name == first);
            while (current != null && series.Count < 8 && !series.Contains(current.Name))
            {
                series.Add(current.Name);
                current = FindNext(entries, current);
            }
            return series;
        }

        private static bool IsInside(AnimationEntry entry, string name, int frame, int frameCount)
        {
            foreach (var interval in entry.Intervals)
            {
                if (interval.name != name) continue;
                int start = interval.hasStart ? interval.start : 0;
                int endExcluded = interval.hasEnd ? interval.end + 1 : frameCount;
                if (frame >= start && frame < endExcluded) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Как игра выбирает реакцию цели: REACTION.HIT (?Hit[].Name), REACTION.HIT_CRIT (список обычных реакций,
    /// которые становятся критической), REACTION.BLOCK_IN и REACTION.BLOCK_HIT (?Block[].BlockName) в триггерах
    /// оружия защищающегося. Первое совпадение выигрывает.
    /// </summary>
    public class ReactionRules
    {
        public const string Hit = "REACTION.HIT";
        public const string Crit = "REACTION.HIT_CRIT";
        public const string BlockIn = "REACTION.BLOCK_IN";
        public const string BlockHit = "REACTION.BLOCK_HIT";

        public readonly Dictionary<string, Dictionary<string, string>> Maps = new Dictionary<string, Dictionary<string, string>>
        {
            { Hit, new Dictionary<string, string>() },
            { Crit, new Dictionary<string, string>() },
            { BlockIn, new Dictionary<string, string>() },
            { BlockHit, new Dictionary<string, string>() }
        };

        private static readonly System.Text.RegularExpressions.Regex Condition =
            new System.Text.RegularExpressions.Regex("(Hit\\[\\]\\.Name|Block\\[\\]\\.BlockName)\"\\s*,\\s*Value2:\\s*\"([^\"]+)\"");
        private static readonly System.Text.RegularExpressions.Regex Animation =
            new System.Text.RegularExpressions.Regex("- Animation:\\s*\\{\\s*Name:\\s*([^,}\\s]+)");

        public static ReactionRules Load(params string[] files)
        {
            var rules = new ReactionRules();
            foreach (var file in files)
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) rules.Parse(file);
            return rules;
        }

        public string Find(string kind, string key)
        {
            string value;
            return key != null && Maps[kind].TryGetValue(key, out value) ? value : null;
        }

        private void Parse(string file)
        {
            string kind = null;
            var keys = new List<string>();
            foreach (var raw in File.ReadAllLines(file))
            {
                string text = raw.Trim();
                if (text.StartsWith("#")) continue;
                if (text.StartsWith("- Name:") || text.StartsWith("- Templates:") || text.StartsWith("- Events:"))
                {
                    kind = null;
                    keys.Clear();
                    if (text.StartsWith("- Templates:"))
                        foreach (var token in text.Substring(12).Trim().Trim('[', ']').Split(','))
                            if (Maps.ContainsKey(token.Trim())) kind = token.Trim();
                    continue;
                }
                if (kind == null) continue;
                var condition = Condition.Match(text);
                if (condition.Success) { keys.Add(condition.Groups[2].Value); continue; }
                var animation = Animation.Match(text);
                if (!animation.Success) continue;
                foreach (var key in keys)
                    if (!Maps[kind].ContainsKey(key)) Maps[kind][key] = animation.Groups[1].Value;
                keys.Clear();
            }
        }
    }

    public class ExportRequest
    {
        /// <summary>Папка проекта игры SFA (где лежит Assets).</summary>
        public string SfaRoot;
        /// <summary>Пути ниже — относительно SfaRoot, как в проекте игры.</summary>
        public string SettingsFile;
        public List<string> Animations = new List<string>();
        public List<int> TransitionOverrides = new List<int>();
        public string SkeletonPart;
        public string ArmorPart;
        public string HelmetPart;
        public string WeaponPart;
        public string OutputDir;
        public string IdleSettingsFile;
        public string IdleAnimation;
        public string ReactionsTriggers;
        public string DefenderBlockTriggers;
        public string ReactionSet;
    }

    /// <summary>
    /// Экспорт приёма из файлов проекта игры без её кода: серия ударов, поза и капсулы по кадрам, Idle героя,
    /// реакции цели. Повторяет экспортёр в проекте игры (Assets/FrameDateExport) — он остаётся эталоном для сверки.
    /// </summary>
    public static class SfaMoveExporter
    {
        public const int ReferenceFps = 60; // ConfigConsts.REFERENCE_FPS игры
        public const string SettingsDir = "Assets/Arena/GameContent/GameSettings/TriggersLogic/Moves/Settings";
        public const string TriggersDir = "Assets/Arena/GameContent/GameSettings/TriggersLogic/Moves/Triggers";
        public const string AnimationsDir = "Assets/Arena/GameContent/Content/animations";
        public const string ModelsDir = "Assets/Arena/GameContent/Content/Items/Models";

        public static string Full(string root, string relative) => Path.Combine(root, relative).Replace('\\', '/');

        public static bool LooksLikeSfa(string root) =>
            !string.IsNullOrEmpty(root) && Directory.Exists(Full(root, SettingsDir)) && Directory.Exists(Full(root, ModelsDir));

        public static string SeriesName(List<string> animations) => string.Join("+", animations);

        public static SfaAnimation LoadAnimation(string root, AnimationEntry entry)
        {
            if (string.IsNullOrEmpty(entry.FileName)) throw new InvalidOperationException($"У {entry.Name} нет FileName.");
            string path = Full(root, AnimationsDir + "/" + Path.ChangeExtension(entry.FileName, ".bytes"));
            if (!File.Exists(path)) throw new FileNotFoundException("Нет файла анимации", path);
            return SfaAnimation.Read(path);
        }

        public static MovePackage Build(ExportRequest request)
        {
            if (!LooksLikeSfa(request.SfaRoot)) throw new DirectoryNotFoundException($"{request.SfaRoot}: это не папка проекта SFA.");
            if (request.Animations.Count == 0) throw new InvalidOperationException("Серия пуста.");
            string root = request.SfaRoot;
            var entries = MoveSettingsReader.Read(Full(root, request.SettingsFile));

            var package = new MovePackage
            {
                name = SeriesName(request.Animations),
                exportedAt = DateTime.Now.ToString("s"),
                settingsFile = request.SettingsFile,
                motionBone = SfaSkeleton.MotionBone
            };

            var parts = new List<SfaPart>();
            AddPart(root, parts, package, request.SkeletonPart, "Skeleton");
            AddPart(root, parts, package, request.ArmorPart, "Armor");
            AddPart(root, parts, package, request.HelmetPart, "Helmet");
            AddPart(root, parts, package, request.WeaponPart, "Weapon");
            var skeleton = SfaSkeleton.Build(parts);
            var rig = new Rig(skeleton);
            package.boneParents = (int[])skeleton.Parents.Clone();
            package.boneHashes = (uint[])skeleton.Hashes.Clone();
            var capsules = BuildCapsules(parts, skeleton, package, KnownPartNames(entries));

            int seriesFrame = 0;
            int previousEnd = 0;
            for (int k = 0; k < request.Animations.Count; k++)
            {
                string name = request.Animations[k];
                var entry = entries.Find(e => e.Name == name);
                if (entry == null) throw new InvalidOperationException($"В {request.SettingsFile} нет анимации {name}.");
                var animation = LoadAnimation(root, entry);

                var segment = new PackageSegment
                {
                    animation = entry.Name,
                    animationFile = entry.FileName,
                    animationFrames = animation.FrameCount,
                    startFrame = k == 0 ? 0 : Mathf.Min(SeriesRules.StartFrame(entry, request.Animations[k - 1], previousEnd), animation.FrameCount - 1),
                    seriesStart = seriesFrame
                };
                segment.groups.AddRange(entry.Groups);
                segment.intervals.AddRange(entry.Intervals);

                bool last = k == request.Animations.Count - 1;
                int end = animation.FrameCount;
                if (last)
                {
                    segment.transition = "последний удар серии — до конца анимации";
                }
                else
                {
                    int manual = k < request.TransitionOverrides.Count ? request.TransitionOverrides[k] : -1;
                    string rule;
                    int auto = SeriesRules.TransitionFrame(entry, animation.FrameCount, out rule);
                    end = manual >= 0 ? Mathf.Clamp(manual, segment.startFrame + 1, animation.FrameCount) : auto;
                    segment.transition = manual >= 0 ? $"переход задан вручную: кадр {end} (по правилу игры {auto})" : rule;
                }
                segment.usedFrames = Mathf.Max(1, end - segment.startFrame);
                previousEnd = end;
                if (k > 0)
                    segment.transition += SeriesRules.IsTransitionSource(entry, request.Animations[k - 1])
                        ? $"; вход из {request.Animations[k - 1]} по Transitions — продолжение с кадра {segment.startFrame}"
                        : "; в Transitions нет предыдущего удара — с кадра 0";

                for (int f = 0; f < animation.FrameCount; f++)
                {
                    segment.frames.Add(new PackageFrame { positions = rig.Evaluate(animation, f) });
                    segment.capsuleFrames.Add(new PackageCapsuleFrame { points = rig.CapsulePoints(capsules) });
                }

                package.segments.Add(segment);
                seriesFrame += segment.usedFrames;
            }
            package.frameCount = seriesFrame;

            if (!string.IsNullOrEmpty(request.IdleAnimation))
            {
                string idleFile = string.IsNullOrEmpty(request.IdleSettingsFile) ? request.SettingsFile : request.IdleSettingsFile;
                var idleEntries = idleFile == request.SettingsFile ? entries : MoveSettingsReader.Read(Full(root, idleFile));
                var idleEntry = idleEntries.Find(e => e.Name == request.IdleAnimation);
                if (idleEntry == null) throw new InvalidOperationException($"В {idleFile} нет анимации Idle {request.IdleAnimation}.");
                var idlePositions = rig.Evaluate(LoadAnimation(root, idleEntry), 0);
                package.hasIdle = true;
                package.idle = new PackageIdle
                {
                    animation = idleEntry.Name,
                    settingsFile = idleFile,
                    motionX = idlePositions[package.motionBone * 3],
                    positions = idlePositions,
                    points = rig.CapsulePoints(capsules)
                };
            }

            if (!string.IsNullOrEmpty(request.ReactionsTriggers))
                ExportReactions(request, package, rig, capsules);

            return package;
        }

        public static string Export(ExportRequest request)
        {
            var package = Build(request);
            Directory.CreateDirectory(request.OutputDir);
            string outPath = Path.Combine(request.OutputDir, SafeName(package.name) + ".fdmove.json");
            File.WriteAllText(outPath, JsonUtility.ToJson(package));
            return outPath;
        }

        private static void ExportReactions(ExportRequest request, MovePackage package, Rig rig, List<CapsuleDef> capsules)
        {
            string root = request.SfaRoot;
            var rules = ReactionRules.Load(Full(root, request.ReactionsTriggers),
                string.IsNullOrEmpty(request.DefenderBlockTriggers) ? null : Full(root, request.DefenderBlockTriggers));
            package.reactionSet = request.ReactionSet;
            var needed = new List<string>();
            Action<string> need = name => { if (!string.IsNullOrEmpty(name) && !needed.Contains(name)) needed.Add(name); };

            foreach (var segment in package.segments)
                foreach (var interval in segment.intervals)
                {
                    if (interval.type != "Attack") continue;
                    interval.unblockable = interval.hitTags.Contains("Unblockable");
                    foreach (var tag in interval.hitTags)
                    {
                        string hit = rules.Find(ReactionRules.Hit, tag);
                        if (hit == null) continue;
                        interval.reactionHit = hit;
                        interval.reactionCrit = rules.Find(ReactionRules.Crit, tag);
                        break;
                    }
                    if (interval.blockNames.Count > 0)
                    {
                        interval.reactionBlockIn = rules.Find(ReactionRules.BlockIn, interval.blockNames[0]);
                        interval.reactionBlockHit = rules.Find(ReactionRules.BlockHit, interval.blockNames[0]);
                    }
                    need(interval.reactionHit);
                    need(interval.reactionCrit);
                    need(interval.reactionBlockIn);
                    need(interval.reactionBlockHit);
                }

            if (needed.Count == 0) return;
            var index = new Dictionary<string, KeyValuePair<string, AnimationEntry>>();
            // Порядок файлов — как у экспортёра в проекте игры (порядок файловой системы): при одноимённых анимациях выигрывает первая.
            var settingsFiles = Directory.GetFiles(Full(root, SettingsDir), "*.yaml");
            foreach (var file in settingsFiles)
            {
                List<AnimationEntry> found;
                try { found = MoveSettingsReader.Read(file); } catch { continue; }
                string relative = SettingsDir + "/" + Path.GetFileName(file);
                foreach (var entry in found)
                    if (needed.Contains(entry.Name) && !index.ContainsKey(entry.Name))
                        index[entry.Name] = new KeyValuePair<string, AnimationEntry>(relative, entry);
            }

            foreach (var name in needed)
            {
                KeyValuePair<string, AnimationEntry> found;
                if (!index.TryGetValue(name, out found)) throw new InvalidOperationException($"Анимация реакции {name} не найдена в настройках.");
                var animation = LoadAnimation(root, found.Value);
                var reaction = new PackageReaction
                {
                    name = name,
                    settingsFile = found.Key,
                    animationFile = found.Value.FileName,
                    frameCount = animation.FrameCount
                };
                reaction.groups.AddRange(found.Value.Groups);
                reaction.intervals.AddRange(found.Value.Intervals);
                for (int f = 0; f < animation.FrameCount; f++)
                {
                    reaction.frames.Add(new PackageFrame { positions = rig.Evaluate(animation, f) });
                    reaction.capsuleFrames.Add(new PackageCapsuleFrame { points = rig.CapsulePoints(capsules) });
                }
                package.reactions.Add(reaction);
            }
        }

        private class CapsuleDef
        {
            public int BoneA;
            public int BoneB;
            public Vector3 LocalA;
            public Vector3 LocalB;
        }

        private static HashSet<string> KnownPartNames(List<AnimationEntry> entries)
        {
            var names = new HashSet<string>();
            foreach (var entry in entries)
                foreach (var interval in entry.Intervals)
                    foreach (var part in interval.attackingParts)
                    {
                        names.Add(part);
                        if (part.EndsWith("_l")) names.Add(part.Substring(0, part.Length - 2) + "_r");
                        if (part.EndsWith("_r")) names.Add(part.Substring(0, part.Length - 2) + "_l");
                    }
            return names;
        }

        private static List<CapsuleDef> BuildCapsules(List<SfaPart> parts, SfaSkeleton skeleton, MovePackage package, HashSet<string> knownNames)
        {
            var nameByHash = new Dictionary<uint, string>();
            foreach (var name in knownNames) nameByHash[SfaHash.Compute(name)] = name;

            var vertices = new Dictionary<uint, SfaVertex>();
            foreach (var part in parts)
                foreach (var vertex in part.Vertices)
                    vertices[vertex.NameHash] = vertex;

            var defs = new List<CapsuleDef>();
            foreach (var part in parts)
            {
                foreach (var capsule in part.Capsules)
                {
                    SfaVertex a, b;
                    int boneA, boneB;
                    if (!vertices.TryGetValue(capsule.Vertex1, out a) || !vertices.TryGetValue(capsule.Vertex2, out b)) continue;
                    if (!skeleton.IndexByName.TryGetValue(a.BoneNameHash, out boneA) ||
                        !skeleton.IndexByName.TryGetValue(b.BoneNameHash, out boneB)) continue;

                    string name;
                    if (!nameByHash.TryGetValue(capsule.NameHash, out name)) name = "capsule_" + capsule.NameHash;
                    defs.Add(new CapsuleDef { BoneA = boneA, BoneB = boneB, LocalA = a.LocalPosition, LocalB = b.LocalPosition });
                    package.capsules.Add(new PackageCapsule
                    {
                        name = name,
                        hash = capsule.NameHash,
                        part = part.Path,
                        radius = capsule.Radius,
                        collidable = capsule.Collidable
                    });
                }
            }
            return defs;
        }

        private static void AddPart(string root, List<SfaPart> parts, MovePackage package, string part, string type)
        {
            if (string.IsNullOrEmpty(part)) return;
            string path = Full(root, ModelsDir + "/" + part + "/Common/SObjPart.bytes");
            if (!File.Exists(path)) throw new FileNotFoundException("Нет детали модели", path);
            parts.Add(SfaPart.Read(path, part, type));
            package.parts.Add(type + ": " + part);
        }

        /// <summary>Скелет героя и прямая кинематика кадра анимации в пространство модели.</summary>
        private class Rig
        {
            private readonly SfaSkeleton _skeleton;
            private readonly Vector3[] _localPosition;
            private readonly Quaternion[] _localRotation;
            private readonly Vector3[] _worldPosition;
            private readonly Quaternion[] _worldRotation;

            public Rig(SfaSkeleton skeleton)
            {
                _skeleton = skeleton;
                int count = skeleton.Parents.Length;
                _localPosition = new Vector3[count];
                _localRotation = new Quaternion[count];
                _worldPosition = new Vector3[count];
                _worldRotation = new Quaternion[count];
            }

            public float[] CapsulePoints(List<CapsuleDef> capsules)
            {
                var points = new float[capsules.Count * 4];
                for (int i = 0; i < capsules.Count; i++)
                {
                    var c = capsules[i];
                    var a = _worldPosition[c.BoneA] + _worldRotation[c.BoneA] * c.LocalA;
                    var b = _worldPosition[c.BoneB] + _worldRotation[c.BoneB] * c.LocalB;
                    points[i * 4] = a.x;
                    points[i * 4 + 1] = a.y;
                    points[i * 4 + 2] = b.x;
                    points[i * 4 + 3] = b.y;
                }
                return points;
            }

            public float[] Evaluate(SfaAnimation animation, int frame)
            {
                int count = _skeleton.Parents.Length;
                Array.Copy(_skeleton.LocalPositions, _localPosition, count);
                Array.Copy(_skeleton.LocalRotations, _localRotation, count);
                for (int k = 0; k < animation.BoneIds.Length; k++)
                {
                    int id = animation.BoneIds[k];
                    if (id >= _skeleton.BoneIdToIndex.Length) continue;
                    int index = _skeleton.BoneIdToIndex[id];
                    if (index < 0) continue;
                    _localPosition[index] = animation.Positions[frame][k];
                    _localRotation[index] = animation.Rotations[frame][k];
                }

                var positions = new float[count * 3];
                for (int i = 0; i < count; i++)
                {
                    int p = _skeleton.Parents[i];
                    if (p < 0)
                    {
                        _worldPosition[i] = _localPosition[i];
                        _worldRotation[i] = _localRotation[i];
                    }
                    else
                    {
                        _worldPosition[i] = _worldPosition[p] + _worldRotation[p] * _localPosition[i];
                        _worldRotation[i] = _worldRotation[p] * _localRotation[i];
                    }
                    positions[i * 3] = _worldPosition[i].x;
                    positions[i * 3 + 1] = _worldPosition[i].y;
                    positions[i * 3 + 2] = _worldPosition[i].z;
                }
                return positions;
            }
        }

        public static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
