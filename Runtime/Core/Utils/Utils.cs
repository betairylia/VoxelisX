using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Utils
{
    /// <summary>
    /// Burst-friendly 128-bit identifier stored as four uint lanes.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Guid128 : IEquatable<Guid128>, IComparable<Guid128>
    {
        public static Guid128 Zero
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default;
        }

        private readonly uint4 value;

        public uint4 Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value;
        }

        public bool IsZero
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => math.all(value == uint4.zero);
        }

        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !IsZero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Guid128(uint4 value)
        {
            this.value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Guid128(uint x, uint y, uint z, uint w)
        {
            value = new uint4(x, y, z, w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 NewGuid(ref Unity.Mathematics.Random random)
        {
            uint4 randomValue = random.NextUInt4();

            // RFC 4122 version 4 and variant bits, using little-endian uint lanes.
            randomValue.y = (randomValue.y & 0xFF0FFFFFu) | 0x00400000u;
            randomValue.z = (randomValue.z & 0xFFFFFF3Fu) | 0x00000080u;

            return new Guid128(randomValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 Random(ref Unity.Mathematics.Random random)
        {
            return NewGuid(ref random);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Guid128 other)
        {
            return math.all(value == other.value);
        }

        /// <summary>
        /// Deterministic total order over the uint lanes in x, y, z, w order. This is the
        /// canonical comparison for graph and sort keys built on Guid128.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(Guid128 other)
        {
            if (value.x != other.value.x) return value.x < other.value.x ? -1 : 1;
            if (value.y != other.value.y) return value.y < other.value.y ? -1 : 1;
            if (value.z != other.value.z) return value.z < other.value.z ? -1 : 1;
            if (value.w != other.value.w) return value.w < other.value.w ? -1 : 1;
            return 0;
        }

        public override bool Equals(object obj)
        {
            return obj is Guid128 other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            return (int)math.hash(value);
        }

        public override string ToString()
        {
            return $"{value.x:x8}{value.y:x8}{value.z:x8}{value.w:x8}";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Guid128 left, Guid128 right)
        {
            return math.all(left.value == right.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Guid128 left, Guid128 right)
        {
            return math.any(left.value != right.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 operator ^(Guid128 left, Guid128 right)
        {
            return new Guid128(left.value ^ right.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 operator &(Guid128 left, Guid128 right)
        {
            return new Guid128(left.value & right.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 operator |(Guid128 left, Guid128 right)
        {
            return new Guid128(left.value | right.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid128 operator ~(Guid128 guid)
        {
            return new Guid128(~guid.value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator uint4(Guid128 guid)
        {
            return guid.value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Guid128(uint4 value)
        {
            return new Guid128(value);
        }

    }

    /// <summary>
    /// Provides assertion utilities that are compatible with Burst-compiled code.
    /// </summary>
    /// <remarks>
    /// Standard Unity assertions cannot be used in Burst-compiled jobs.
    /// This class provides simple assertion methods that work within Burst constraints.
    /// Assertions are only active when UNITY_ASSERTIONS is defined.
    /// </remarks>
    static class BurstAssertSimpleExperssionsOnly
    {
        /// <summary>
        /// Asserts that a condition is true. Throws an exception if the condition is false.
        /// </summary>
        /// <param name="truth">The condition to assert.</param>
        /// <exception cref="Exception">Thrown when the condition is false and UNITY_ASSERTIONS is defined.</exception>
        /// <remarks>
        /// This method is designed to be used in Burst-compiled code where standard
        /// Unity assertions are not available. In release builds (when UNITY_ASSERTIONS
        /// is not defined), this method becomes a no-op and has zero overhead.
        /// </remarks>
        public static void IsTrue(bool truth)
        {
#if UNITY_ASSERTIONS
            if (!truth)
            {
                throw new Exception("Assertion failed");
            }
#endif
        }
    }

    /// <summary>
    /// Extension methods for converting between Vector3Int and int3 types.
    /// </summary>
    public static class VectorConversionExtensions
    {
        /// <summary>
        /// Converts an int3 to a Vector3Int.
        /// </summary>
        /// <param name="value">The int3 value to convert.</param>
        /// <returns>A Vector3Int with the same x, y, z components.</returns>
        public static Vector3Int ToVector3Int(this int3 value)
        {
            return new Vector3Int(value.x, value.y, value.z);
        }

        /// <summary>
        /// Converts a Vector3Int to an int3.
        /// </summary>
        /// <param name="value">The Vector3Int value to convert.</param>
        /// <returns>An int3 with the same x, y, z components.</returns>
        public static int3 ToInt3(this Vector3Int value)
        {
            return new int3(value.x, value.y, value.z);
        }
    }
}
