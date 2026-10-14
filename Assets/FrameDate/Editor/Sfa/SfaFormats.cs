using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FrameDate.EditorTools.Sfa
{
    /// <summary>
    /// Чтение двоичных форматов игры SF4: Arena без её кода. Повторяет парсеры фреймворка игры
    /// (com.banzai.sf-game-framework 7.9.0, com.banzai.dmf 4.3.1) — сверено с ними по исходникам:
    /// AnimationSequenceParser, CompressedDmfQuat, DmfVector3ImplGeneral.HalfToDmf, SObjPartConfigIO,
    /// SObjSkeletonConfigParser, SimStateStringUtils.ComputeHash. Каждый массив проверяется по хэшу типа:
    /// если игра поменяет формат, чтение остановится с ошибкой, а не прочтёт мусор.
    /// </summary>
    public static class Q20
    {
        /// <summary>DmfNumber игры — фиксированная точка Q44.20 в long.</summary>
        public const int FractionalBits = 20;
        public const double Scale = 1 << FractionalBits;

        public static float ToFloat(long raw) => (float)(raw / Scale);
    }

    /// <summary>Хэш имён игры: MurMur3 по UTF-16-символам парами, A–Z без учёта регистра, seed 180591.</summary>
    public static class SfaHash
    {
        private const uint Seed = 180591u;

        public static uint Compute(string text)
        {
            if (text == null) return 0u;
            unchecked
            {
                uint hash = Seed;
                uint bytes = (uint)text.Length * 2u;
                int pairs = text.Length / 2;
                for (int i = 0; i < pairs; i++)
                {
                    uint k = ToLower(text[2 * i]) | ((uint)ToLower(text[2 * i + 1]) << 16);
                    hash = Batch(hash, k);
                }
                if (pairs * 4 < bytes) hash = Batch(hash, ToLower(text[text.Length - 1]));

                hash ^= bytes;
                hash ^= hash >> 16;
                hash *= 0x85ebca6b;
                hash ^= hash >> 13;
                hash *= 0xc2b2ae35;
                hash ^= hash >> 16;
                return hash;
            }
        }

        private static uint Batch(uint hash, uint k)
        {
            unchecked
            {
                k *= 0xcc9e2d51;
                k = (k << 15) | (k >> 17);
                k *= 0x1b873593;
                hash ^= k;
                hash = (hash << 13) | (hash >> 19);
                return hash * 5 + 0xe6546b64;
            }
        }

        private static char ToLower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
    }

    /// <summary>Анимация игры (*.bytes): кадры × кости, локальные позиция и поворот.</summary>
    public sealed class SfaAnimation
    {
        public const long MagicNumber = 457546134634734L;

        public ushort[] BoneIds;
        public Vector3[][] Positions;
        public Quaternion[][] Rotations;

        public int FrameCount => Positions.Length;

        public static SfaAnimation Read(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                long magic = reader.ReadInt64();
                if (magic != MagicNumber)
                    throw new InvalidDataException($"{path}: не анимация игры (магическое число {magic}, ожидалось {MagicNumber}).");

                short blockCount = reader.ReadInt16();
                for (int i = 0; i < blockCount; i++) reader.ReadInt64();

                int frameCount = reader.ReadInt32();
                int boneCount = reader.ReadInt32();
                var animation = new SfaAnimation
                {
                    BoneIds = new ushort[boneCount],
                    Positions = new Vector3[frameCount][],
                    Rotations = new Quaternion[frameCount][]
                };
                for (int i = 0; i < boneCount; i++) animation.BoneIds[i] = (ushort)reader.ReadInt16();

                // Касательные (второй блок) не нужны: поза берётся по кадрам.
                for (int f = 0; f < frameCount; f++)
                {
                    animation.Positions[f] = new Vector3[boneCount];
                    animation.Rotations[f] = new Quaternion[boneCount];
                    for (int b = 0; b < boneCount; b++)
                    {
                        ushort px = reader.ReadUInt16(), py = reader.ReadUInt16(), pz = reader.ReadUInt16();
                        ushort r0 = reader.ReadUInt16(), r1 = reader.ReadUInt16(), r2 = reader.ReadUInt16();
                        animation.Positions[f][b] = new Vector3(SfaNumbers.Half(px), SfaNumbers.Half(py), SfaNumbers.Half(pz));
                        animation.Rotations[f][b] = SfaNumbers.CompressedQuaternion(r0, r1, r2);
                    }
                }
                return animation;
            }
        }
    }

    public static class SfaNumbers
    {
        /// <summary>Half-float позиции в число игры, как DmfVector3ImplGeneral.HalfToDmf (с обрезанием до Q20).</summary>
/// <summary>
        /// Half-float позиции в число игры — точно как DmfVector3ImplGeneral.HalfToDmf: таблица экспонент
        /// DmfLutPreDividedBinary16ExponentValues = 2^(e−5) с нулями для e < 5, поэтому очень малые значения
        /// (|x| < 2^−10) и субнормальные игра обращает в 0.
        /// </summary>
        public static float Half(ushort value)
        {
            int exponent = (value >> 10) & 0x1F;
            int mantissa = value & 0x3FF;
            if (exponent == 0x1F) throw new InvalidDataException("NaN или бесконечность в позиции анимации.");
            long lut = exponent < 5 ? 0L : 1L << (exponent - 5);
            long raw = lut * (exponent == 0 ? mantissa : mantissa + 1024);
            if ((value & 0x8000) != 0) raw = -raw;
            return Q20.ToFloat(raw);
        }

        /// <summary>
        /// Корень в фиксированной точке — точно как DmfMathImplGeneral.Sqrt: аргумент делится на 4, пока не меньше 4,
        /// индекс таблицы — сырое значение >> SQRT_PRECISION (3), результат сдвигается обратно. Таблица
        /// DmfLutSquareRoots[i] = ⌊√(i·8·2^20)⌋ (сверено с её значениями) — считается, а не хранится.
        /// </summary>
        public static long SqrtRaw(long raw)
        {
            if (raw <= 0) return 0L;
            int fours = 0;
            while (raw >= 4L << Q20.FractionalBits)
            {
                fours++;
                raw >>= 2;
            }
            long index = raw >> 3;
            long table = (long)Math.Floor(Math.Sqrt((double)(index * 8L) * Q20.Scale));
            return table << fours;
        }

        private const long QuatScaledRatio = 199038940000000L; // (√2 / 0x7FFF) << 22, в Q20
        private const int QuatShift = 22 + Q20.FractionalBits;
        private const long QuatMaxValueShift = 741455L;       // 1/√2 в Q20

        private static long QuatComponent(ushort packed) => ((long)packed * QuatScaledRatio >> QuatShift) - QuatMaxValueShift;

        /// <summary>
        /// Сжатый поворот, как CompressedDmfQuat: 2 бита индекса опущенной (наибольшей) компоненты и три по 15 бит;
        /// опущенная восстанавливается из единичной длины и снова квантуется в 15 бит — целочисленной арифметикой Q20.
        /// </summary>
        public static Quaternion CompressedQuaternion(ushort r0, ushort r1, ushort r2)
        {
            ulong packed = ((ulong)r0 << 32) | ((ulong)r1 << 16) | r2;
            int missing = (int)((packed >> 45) & 0b11);
            var p0 = (ushort)((packed >> 30) & 0x7FFF);
            var p1 = (ushort)((packed >> 15) & 0x7FFF);
            var p2 = (ushort)(packed & 0x7FFF);

            long c0 = QuatComponent(p0), c1 = QuatComponent(p1), c2 = QuatComponent(p2);
            long rest = (1L << Q20.FractionalBits) - (c0 * c0 >> Q20.FractionalBits) - (c1 * c1 >> Q20.FractionalBits) - (c2 * c2 >> Q20.FractionalBits);
            long c3 = SqrtRaw(rest);
            var p3 = (ushort)((((c3 + QuatMaxValueShift) * 32767L) * QuatMaxValueShift >> Q20.FractionalBits) >> Q20.FractionalBits);

            ushort x, y, z, w;
            switch (missing)
            {
                case 0: x = p3; y = p0; z = p1; w = p2; break;
                case 1: x = p0; y = p3; z = p1; w = p2; break;
                case 2: x = p0; y = p1; z = p3; w = p2; break;
                default: x = p0; y = p1; z = p2; w = p3; break;
            }
            return new Quaternion(
                Q20.ToFloat(QuatComponent(x)), Q20.ToFloat(QuatComponent(y)),
                Q20.ToFloat(QuatComponent(z)), Q20.ToFloat(QuatComponent(w)));
        }
    }

    public struct SfaVertex
    {
        public uint NameHash;
        public uint BoneNameHash;
        public Vector3 LocalPosition;
    }

    public struct SfaCapsule
    {
        public uint NameHash;
        public uint Vertex1;
        public uint Vertex2;
        public float Radius;
        public bool Collidable;
    }

    public struct SfaSkeletonBone
    {
        public uint NameHash;
        public int ParentIdx;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public sealed class SfaSkeletonPart
    {
        public uint ParentBoneNameHash;
        public SfaSkeletonBone[] Bones;
    }

    public struct SfaBoneInfo
    {
        public uint NameHash;
        public int Id;
    }

    public sealed class SfaBoneSet
    {
        public uint NameHash;
        public SfaBoneInfo[] Bones;
    }

    /// <summary>Деталь модели игры (SObjPart.bytes): наборы костей, вершины, капсулы и подскелеты.</summary>
    public sealed class SfaPart
    {
        // Хэши типов и размеры структур (sizeof) — сняты с рантайма игры.
        private const uint HeaderHash = 2393292721u;
        private const uint MeshConfigsHash = 375684238u;
        private const uint WeaponMirroringInfoHash = 4012514511u;
        private const uint PrefabInfoHash = 1226211245u;
        private const uint MaterialInfoHash = 3560126978u;
        private const uint BonesHash = 2220198741u;
        private const uint BoneInfoHash = 2232105117u;
        private const uint MirroringBoneHash = 769853970u;
        private const uint VertexInfoHash = 381742000u;
        private const uint UIntHash = 259043434u;
        private const uint CapsuleInfoHash = 3485538053u;
        private const uint WeaponMirroringBoneHash = 2343633118u;
        private const uint SkeletonHash = 2210279075u;
        private const uint SkeletonBoneHash = 3754955813u;

        public string Path;
        public string Type;
        public readonly List<SfaBoneSet> BoneSets = new List<SfaBoneSet>();
        public SfaVertex[] Vertices;
        public SfaCapsule[] Capsules;
        public readonly List<SfaSkeletonPart> Skeletons = new List<SfaSkeletonPart>();

        public static SfaPart Read(string file, string path, string type)
        {
            var part = new SfaPart { Path = path, Type = type };
            using (var reader = new BinaryReader(File.OpenRead(file)))
            {
                Expect(reader, HeaderHash, file);
                reader.ReadInt32();
                reader.ReadInt32();
                Expect(reader, MeshConfigsHash, file);
                reader.ReadBytes(2);
                Expect(reader, WeaponMirroringInfoHash, file);
                reader.ReadBytes(12);
                SkipArray(reader, PrefabInfoHash, 129, file);
                SkipArray(reader, PrefabInfoHash, 129, file);
                SkipArray(reader, MaterialInfoHash, 8, file);

                Expect(reader, BonesHash, file);
                int sets = reader.ReadInt32();
                for (int s = 0; s < sets; s++)
                {
                    Expect(reader, BonesHash, file);
                    var set = new SfaBoneSet { NameHash = reader.ReadUInt32() };
                    Expect(reader, BoneInfoHash, file);
                    set.Bones = new SfaBoneInfo[reader.ReadInt32()];
                    for (int i = 0; i < set.Bones.Length; i++)
                    {
                        var b = reader.ReadBytes(24);
                        set.Bones[i] = new SfaBoneInfo { NameHash = BitConverter.ToUInt32(b, 8), Id = BitConverter.ToInt32(b, 12) };
                    }
                    part.BoneSets.Add(set);
                }

                SkipArray(reader, MirroringBoneHash, 12, file);

                Expect(reader, VertexInfoHash, file);
                part.Vertices = new SfaVertex[reader.ReadInt32()];
                for (int i = 0; i < part.Vertices.Length; i++)
                {
                    var b = reader.ReadBytes(32);
                    part.Vertices[i] = new SfaVertex
                    {
                        LocalPosition = Vector(b, 0),
                        NameHash = BitConverter.ToUInt32(b, 24),
                        BoneNameHash = BitConverter.ToUInt32(b, 28)
                    };
                }

                SkipArray(reader, UIntHash, 4, file);

                Expect(reader, CapsuleInfoHash, file);
                part.Capsules = new SfaCapsule[reader.ReadInt32()];
                for (int i = 0; i < part.Capsules.Length; i++)
                {
                    var b = reader.ReadBytes(40);
                    part.Capsules[i] = new SfaCapsule
                    {
                        Radius = Q20.ToFloat(BitConverter.ToInt64(b, 0)),
                        NameHash = BitConverter.ToUInt32(b, 8),
                        Vertex1 = BitConverter.ToUInt32(b, 12),
                        Vertex2 = BitConverter.ToUInt32(b, 16),
                        Collidable = b[32] != 0
                    };
                }

                SkipArray(reader, WeaponMirroringBoneHash, 12, file);

                Expect(reader, SkeletonHash, file);
                int skeletons = reader.ReadInt32();
                for (int s = 0; s < skeletons; s++)
                {
                    Expect(reader, SkeletonHash, file);
                    var skeleton = new SfaSkeletonPart { ParentBoneNameHash = reader.ReadUInt32() };
                    Expect(reader, SkeletonBoneHash, file);
                    skeleton.Bones = new SfaSkeletonBone[reader.ReadInt32()];
                    for (int i = 0; i < skeleton.Bones.Length; i++)
                    {
                        var b = reader.ReadBytes(64);
                        skeleton.Bones[i] = new SfaSkeletonBone
                        {
                            Position = Vector(b, 0),
                            Rotation = new Quaternion(
                                Q20.ToFloat(BitConverter.ToInt64(b, 24)), Q20.ToFloat(BitConverter.ToInt64(b, 32)),
                                Q20.ToFloat(BitConverter.ToInt64(b, 40)), Q20.ToFloat(BitConverter.ToInt64(b, 48))),
                            NameHash = BitConverter.ToUInt32(b, 56),
                            ParentIdx = BitConverter.ToInt32(b, 60)
                        };
                    }
                    part.Skeletons.Add(skeleton);
                }
            }
            return part;
        }

        /// <summary>Кости набора (Fight), как SObjPartConfig.GetBones: набор по хэшу имени, иначе набор без имени.</summary>
        public SfaBoneInfo[] GetBones(string boneSet)
        {
            uint hash = SfaHash.Compute(boneSet);
            SfaBoneInfo[] fallback = null;
            foreach (var set in BoneSets)
            {
                if (set.NameHash == hash) return set.Bones;
                if (set.NameHash == 0u && fallback == null) fallback = set.Bones;
            }
            return fallback ?? new SfaBoneInfo[0];
        }

        private static Vector3 Vector(byte[] b, int offset) => new Vector3(
            Q20.ToFloat(BitConverter.ToInt64(b, offset)),
            Q20.ToFloat(BitConverter.ToInt64(b, offset + 8)),
            Q20.ToFloat(BitConverter.ToInt64(b, offset + 16)));

        private static void Expect(BinaryReader reader, uint hash, string file)
        {
            uint read = reader.ReadUInt32();
            if (read != hash)
                throw new InvalidDataException($"{file}: на позиции {reader.BaseStream.Position - 4} ожидался хэш типа {hash}, прочитан {read}. Формат детали изменился?");
        }

        private static void SkipArray(BinaryReader reader, uint hash, int elementSize, string file)
        {
            Expect(reader, hash, file);
            int count = reader.ReadInt32();
            reader.BaseStream.Seek((long)count * elementSize, SeekOrigin.Current);
        }
    }

    /// <summary>Скелет героя из деталей, как SObjSkeletonConfigParser: подскелеты цепляются к кости-родителю по имени.</summary>
    public sealed class SfaSkeleton
    {
        public const int BoneIdCount = 2000;
        public const int MotionBone = 2;

        public int[] Parents;
        public uint[] Hashes;
        public Vector3[] LocalPositions;
        public Quaternion[] LocalRotations;
        public short[] BoneIdToIndex;
        public readonly Dictionary<uint, int> IndexByName = new Dictionary<uint, int>();

        public static SfaSkeleton Build(IList<SfaPart> parts, string boneSet = "Fight")
        {
            int size = 0;
            foreach (var part in parts) foreach (var skeleton in part.Skeletons) size += skeleton.Bones.Length;

            var result = new SfaSkeleton
            {
                Parents = new int[size],
                Hashes = new uint[size],
                LocalPositions = new Vector3[size],
                LocalRotations = new Quaternion[size],
                BoneIdToIndex = new short[BoneIdCount]
            };
            for (int i = 0; i < BoneIdCount; i++) result.BoneIdToIndex[i] = -1;

            int parsed = 0;
            foreach (var part in parts)
            {
                foreach (var skeleton in part.Skeletons)
                {
                    int skeletonParent;
                    if (!result.IndexByName.TryGetValue(skeleton.ParentBoneNameHash, out skeletonParent)) skeletonParent = -1;
                    int offset = parsed;
                    foreach (var bone in skeleton.Bones)
                    {
                        result.Parents[parsed] = bone.ParentIdx >= 0 ? offset + bone.ParentIdx : skeletonParent;
                        result.Hashes[parsed] = bone.NameHash;
                        result.LocalPositions[parsed] = bone.Position;
                        result.LocalRotations[parsed] = bone.Rotation;
                        result.IndexByName.Add(bone.NameHash, parsed);
                        parsed++;
                    }
                }
            }

            foreach (var part in parts)
            {
                if (part.BoneSets.Count == 0) continue;
                foreach (var bone in part.GetBones(boneSet))
                {
                    int index;
                    if (!result.IndexByName.TryGetValue(bone.NameHash, out index))
                        throw new InvalidDataException($"{part.Path}: кость {bone.NameHash} из набора {boneSet} не найдена в скелете.");
                    if (bone.Id < 0 || bone.Id >= BoneIdCount)
                        throw new InvalidDataException($"{part.Path}: id кости {bone.Id} вне диапазона.");
                    result.BoneIdToIndex[bone.Id] = (short)index;
                }
            }
            return result;
        }
    }
}
