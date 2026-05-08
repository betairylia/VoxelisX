using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public class AlienDirtyPropagationScenarioTests
    {
        [Test]
        public void DirtyBrickInEntityMarksOverlappingTargetBrick()
        {
            using var source = new EntityDataTestScope();
            using var target = new EntityDataTestScope();
            AddAllocatedBrick(source, int3.zero, int3.zero);
            AddAllocatedBrick(target, int3.zero, int3.zero);
            target.Data.ClearDirtyFlags();

            Propagate(source, target, BlockEditSettings());

            Assert.That(RequireFlags(target, int3.zero, int3.zero) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(target.SectorAt(int3.zero).Get().sectorDirtyFlags, Is.EqualTo(0));
        }

        [Test]
        public void AlienPropagationSkipsSelf()
        {
            using var source = new EntityDataTestScope();
            AddAllocatedBrick(source, int3.zero, int3.zero);

            Propagate(source, BlockEditSettings());

            Assert.That(RequireFlags(source, int3.zero, int3.zero) & (ushort)DirtyFlags.GeneralAutomata, Is.EqualTo(0));
        }

        [Test]
        public void AlienPropagationDoesNotCreateSectorsOrBricks()
        {
            using var source = new EntityDataTestScope();
            using var target = new EntityDataTestScope();
            AddAllocatedBrick(source, int3.zero, int3.zero);

            Propagate(source, target, BlockEditSettings());

            Assert.That(target.Data.sectors.Count, Is.EqualTo(0));
        }

        [Test]
        public void AlienPropagationDoesNotMarkNonAllocatedTargetBrickSlots()
        {
            using var source = new EntityDataTestScope();
            using var target = new EntityDataTestScope();
            AddAllocatedBrick(source, int3.zero, int3.zero);
            target.AddSector(int3.zero);

            Propagate(source, target, BlockEditSettings());

            Assert.That(target.SectorAt(int3.zero).Get().sectorRequireUpdateFlags, Is.EqualTo(0));
            Assert.That(target.SectorAt(int3.zero).Get().NonEmptyBrickCount, Is.EqualTo(0));
        }

        [Test]
        public void AllocatedButEmptySourceBrickCountsForMotionPropagation()
        {
            using var source = new EntityDataTestScope();
            using var target = new EntityDataTestScope();
            AddAllocatedEmptyBrick(source, int3.zero, int3.zero);
            AddAllocatedBrick(target, int3.zero, int3.zero);
            source.Data.ClearDirtyFlags();
            target.Data.ClearDirtyFlags();
            SetMotion(source, new float3(32f, 0f, 0f), float3.zero);
            SetStatic(target);

            Propagate(source, target, MotionSettings());

            Assert.That(RequireFlags(target, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.Not.EqualTo(0));
        }

        [Test]
        public void MovingEntityMarksGainedOverlap()
        {
            using var moving = new EntityDataTestScope();
            using var other = new EntityDataTestScope();
            AddAllocatedBrick(moving, int3.zero, int3.zero);
            AddAllocatedBrick(other, int3.zero, int3.zero);
            moving.Data.ClearDirtyFlags();
            other.Data.ClearDirtyFlags();
            SetMotion(moving, new float3(32f, 0f, 0f), float3.zero);
            SetStatic(other);

            Propagate(moving, other, MotionSettings());

            Assert.That(RequireFlags(other, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.Not.EqualTo(0));
        }

        [Test]
        public void MovingEntityMarksLostOverlapThroughSweptAabb()
        {
            using var moving = new EntityDataTestScope();
            using var other = new EntityDataTestScope();
            AddAllocatedBrick(moving, int3.zero, int3.zero);
            AddAllocatedBrick(other, int3.zero, int3.zero);
            moving.Data.ClearDirtyFlags();
            other.Data.ClearDirtyFlags();
            SetMotion(moving, float3.zero, new float3(32f, 0f, 0f));
            SetStatic(other);

            Propagate(moving, other, MotionSettings());

            Assert.That(RequireFlags(other, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.Not.EqualTo(0));
        }

        [Test]
        public void BidirectionalMotionMarksMovingAndOtherTargets()
        {
            using var moving = new EntityDataTestScope();
            using var other = new EntityDataTestScope();
            AddAllocatedBrick(moving, int3.zero, int3.zero);
            AddAllocatedBrick(other, int3.zero, int3.zero);
            moving.Data.ClearDirtyFlags();
            other.Data.ClearDirtyFlags();
            SetMotion(moving, new float3(32f, 0f, 0f), float3.zero);
            SetStatic(other);

            Propagate(moving, other, MotionSettings());

            Assert.That(RequireFlags(moving, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.Not.EqualTo(0));
            Assert.That(RequireFlags(other, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.Not.EqualTo(0));
        }

        [Test]
        public void SameVelocityPairDoesNotMarkWhenRelativeMotionIsZero()
        {
            using var a = new EntityDataTestScope();
            using var b = new EntityDataTestScope();
            AddAllocatedBrick(a, int3.zero, int3.zero);
            AddAllocatedBrick(b, int3.zero, int3.zero);
            a.Data.ClearDirtyFlags();
            b.Data.ClearDirtyFlags();
            SetMotion(a, new float3(-32f, 0f, 0f), float3.zero);
            SetMotion(b, new float3(-32f, 0f, 0f), float3.zero);

            Propagate(a, b, MotionSettings());

            Assert.That(RequireFlags(a, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.EqualTo(0));
            Assert.That(RequireFlags(b, int3.zero, int3.zero) & (ushort)DirtyFlags.Reserved1, Is.EqualTo(0));
        }

        private static AlienDirtyPropagationSettings BlockEditSettings()
        {
            var settings = AlienDirtyPropagationSettings.Default;
            settings.FlagsToPropagate = DirtyFlags.GeneralAutomata;
            settings.AlienMotionDirtyMask = DirtyFlags.Reserved1;
            return settings;
        }

        private static AlienDirtyPropagationSettings MotionSettings()
        {
            var settings = BlockEditSettings();
            settings.AlienMotionDirtyMask = DirtyFlags.Reserved1;
            settings.MotionThreshold = 0f;
            settings.DeltaTime = 1f;
            return settings;
        }

        private static void Propagate(EntityDataTestScope entity, AlienDirtyPropagationSettings settings)
        {
            var entities = new NativeArray<VoxelEntityData>(1, Allocator.TempJob);
            try
            {
                entities[0] = entity.Data;
                AlienDirtyPropagation.Propagate(entities, settings);
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static void Propagate(EntityDataTestScope a, EntityDataTestScope b, AlienDirtyPropagationSettings settings)
        {
            var entities = new NativeArray<VoxelEntityData>(2, Allocator.TempJob);
            try
            {
                entities[0] = a.Data;
                entities[1] = b.Data;
                AlienDirtyPropagation.Propagate(entities, settings);
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static void AddAllocatedBrick(EntityDataTestScope entity, int3 sectorPos, int3 brickPos)
        {
            SectorHandle sector = entity.Data.sectors.ContainsKey(sectorPos) ? entity.SectorAt(sectorPos) : entity.AddSector(sectorPos);
            int3 blockPos = brickPos * Sector.SIZE_IN_BLOCKS;
            sector.SetBlock(blockPos.x, blockPos.y, blockPos.z, new Block(1));
        }

        private static void AddAllocatedEmptyBrick(EntityDataTestScope entity, int3 sectorPos, int3 brickPos)
        {
            SectorHandle sector = entity.Data.sectors.ContainsKey(sectorPos) ? entity.SectorAt(sectorPos) : entity.AddSector(sectorPos);
            int3 blockPos = brickPos * Sector.SIZE_IN_BLOCKS;
            sector.SetBlock(blockPos.x, blockPos.y, blockPos.z, new Block(1));
            sector.SetBlock(blockPos.x, blockPos.y, blockPos.z, Block.Empty);
        }

        private static ushort RequireFlags(EntityDataTestScope entity, int3 sectorPos, int3 brickPos)
        {
            return entity.RequireFlagsAt(sectorPos, brickPos);
        }

        private static void SetStatic(EntityDataTestScope entity)
        {
            entity.Data.previousTransform = RigidTransform.identity;
            entity.Data.transform = RigidTransform.identity;
            entity.Data.linearVelocity = float3.zero;
            entity.Data.angularVelocity = float3.zero;
        }

        private static void SetMotion(EntityDataTestScope entity, float3 previousPosition, float3 currentPosition)
        {
            entity.Data.previousTransform = new RigidTransform(quaternion.identity, previousPosition);
            entity.Data.transform = new RigidTransform(quaternion.identity, currentPosition);
            entity.Data.linearVelocity = currentPosition - previousPosition;
            entity.Data.angularVelocity = float3.zero;
        }
    }
}
