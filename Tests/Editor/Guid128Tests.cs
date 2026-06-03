using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Voxelis.Utils;

namespace VoxelisX.Tests
{
    public class Guid128Tests
    {
        [Test]
        public void Guid128IsSixteenBytes()
        {
            Assert.That(UnsafeUtility.SizeOf<Guid128>(), Is.EqualTo(16));
        }

        [Test]
        public void EqualityUsesAllFourLanes()
        {
            var a = new Guid128(1, 2, 3, 4);
            var b = new Guid128(1, 2, 3, 4);
            var c = new Guid128(1, 2, 3, 5);

            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void BitwiseOperatorsUseVectorLanes()
        {
            var a = new Guid128(0xFFFF0000u, 0x00FF00FFu, 0xAAAAAAAAu, 0x00000000u);
            var b = new Guid128(0x00FFFF00u, 0x0F0F0F0Fu, 0x55555555u, 0xFFFFFFFFu);

            Assert.That((uint4)(a & b), Is.EqualTo(new uint4(0x00FF0000u, 0x000F000Fu, 0x00000000u, 0x00000000u)));
            Assert.That((uint4)(a | b), Is.EqualTo(new uint4(0xFFFFFF00u, 0x0FFF0FFFu, 0xFFFFFFFFu, 0xFFFFFFFFu)));
            Assert.That((uint4)(a ^ b), Is.EqualTo(new uint4(0xFF00FF00u, 0x0FF00FF0u, 0xFFFFFFFFu, 0xFFFFFFFFu)));
            Assert.That((uint4)(~a), Is.EqualTo(new uint4(0x0000FFFFu, 0xFF00FF00u, 0x55555555u, 0xFFFFFFFFu)));
        }

        [Test]
        public void NewGuidUsesRandomValueWithVersion4AndVariantBits()
        {
            var random = new Unity.Mathematics.Random(1234);

            Guid128 guid = Guid128.NewGuid(ref random);
            uint4 value = (uint4)guid;

            Assert.That(guid.IsValid, Is.True);
            Assert.That((value.y >> 20) & 0xFu, Is.EqualTo(4u));
            Assert.That(value.z & 0xC0u, Is.EqualTo(0x80u));
        }

        [Test]
        public void ToStringConcatenatesZeroPaddedUintLanes()
        {
            var guid = new Guid128(0x1u, 0x23u, 0x456u, 0x789ABCDu);

            Assert.That(guid.ToString(), Is.EqualTo("0000000100000023000004560789abcd"));
        }
    }
}
