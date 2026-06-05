using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Utils;

namespace Voxelis
{
    public enum VoxelBodyForceKind
    {
        Force,
        Torque,
        ForceAtPosition
    }

    public enum VoxelBodyForceMode
    {
        Force,
        Acceleration,
        Impulse,
        VelocityChange
    }

    public struct VoxelBodyForceCommand
    {
        public Guid128 BodyId;
        public float3 Vector;
        public float3 WorldPosition;
        public VoxelBodyForceKind Kind;
        public VoxelBodyForceMode Mode;

        public static VoxelBodyForceCommand Force(Guid128 bodyId, float3 force, VoxelBodyForceMode mode)
        {
            return new VoxelBodyForceCommand
            {
                BodyId = bodyId,
                Vector = force,
                Kind = VoxelBodyForceKind.Force,
                Mode = mode
            };
        }

        public static VoxelBodyForceCommand Torque(Guid128 bodyId, float3 torque, VoxelBodyForceMode mode)
        {
            return new VoxelBodyForceCommand
            {
                BodyId = bodyId,
                Vector = torque,
                Kind = VoxelBodyForceKind.Torque,
                Mode = mode
            };
        }

        public static VoxelBodyForceCommand ForceAtPosition(
            Guid128 bodyId,
            float3 force,
            float3 worldPosition,
            VoxelBodyForceMode mode)
        {
            return new VoxelBodyForceCommand
            {
                BodyId = bodyId,
                Vector = force,
                WorldPosition = worldPosition,
                Kind = VoxelBodyForceKind.ForceAtPosition,
                Mode = mode
            };
        }
    }

    /// <summary>
    /// Frame-local force command stream owned by the world. Do not store this inside
    /// VoxelBodyData or VoxelEntityData; it is intentionally outside copied tick data.
    /// </summary>
    public sealed class VoxelBodyForceCommandStream : IDisposable
    {
        public const int MainThreadForEachIndex = 0;
        public const int DefaultForEachCount = 128;

        public struct JobWriter
        {
            public NativeStream.Writer StreamWriter;

            public void BeginForEachIndex(int jobIndex)
            {
                StreamWriter.BeginForEachIndex(VoxelBodyForceCommandStream.JobForEachIndex(jobIndex));
            }

            public void AddForce(Guid128 bodyId, float3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                StreamWriter.Write(VoxelBodyForceCommand.Force(bodyId, force, mode));
            }

            public void AddTorque(Guid128 bodyId, float3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                StreamWriter.Write(VoxelBodyForceCommand.Torque(bodyId, torque, mode));
            }

            public void AddForceAtPosition(
                Guid128 bodyId,
                float3 force,
                float3 worldPosition,
                VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                StreamWriter.Write(VoxelBodyForceCommand.ForceAtPosition(bodyId, force, worldPosition, mode));
            }

            public void EndForEachIndex()
            {
                StreamWriter.EndForEachIndex();
            }
        }

        private readonly Allocator allocator;
        private readonly object syncRoot = new object();
        private NativeStream stream;
        private NativeStream.Writer mainThreadWriter;
        private int forEachCount;
        private bool mainThreadWriterOpen;

        public VoxelBodyForceCommandStream(Allocator allocator, int initialForEachCount = DefaultForEachCount)
        {
            this.allocator = allocator;
            BeginFrame(initialForEachCount);
        }

        public int ForEachCount => forEachCount;

        public static int JobForEachIndex(int jobIndex) => jobIndex + 1;

        /// <summary>
        /// Returns a writer for jobs. Job writers must use unique foreach indices in
        /// [1, ForEachCount); index 0 is reserved for main-thread convenience calls.
        /// For an IJobParallelFor, schedule at most ForEachCount - 1 iterations and
        /// call JobWriter.BeginForEachIndex(index) in each Execute.
        /// </summary>
        public JobWriter AsJobWriter(int jobForEachCount)
        {
            lock (syncRoot)
            {
                if (jobForEachCount < 0 || JobForEachIndex(jobForEachCount - 1) >= forEachCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(jobForEachCount),
                        $"Force command stream supports {forEachCount - 1} job foreach indices.");
                }

                return new JobWriter { StreamWriter = stream.AsWriter() };
            }
        }

        public void AddForce(Guid128 bodyId, float3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            WriteMainThread(VoxelBodyForceCommand.Force(bodyId, force, mode));
        }

        public void AddTorque(Guid128 bodyId, float3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            WriteMainThread(VoxelBodyForceCommand.Torque(bodyId, torque, mode));
        }

        public void AddForceAtPosition(
            Guid128 bodyId,
            float3 force,
            float3 worldPosition,
            VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            WriteMainThread(VoxelBodyForceCommand.ForceAtPosition(bodyId, force, worldPosition, mode));
        }

        public void ApplyTo(ref VoxelisXWorld.WorldStageInputs tickBuf, float deltaTime)
        {
            lock (syncRoot)
            {
                EndMainThreadWrites();

                NativeStream.Reader reader = stream.AsReader();
                for (int forEachIndex = 0; forEachIndex < forEachCount; forEachIndex++)
                {
                    int itemCount = reader.BeginForEachIndex(forEachIndex);
                    for (int i = 0; i < itemCount; i++)
                    {
                        ApplyCommand(ref tickBuf, reader.Read<VoxelBodyForceCommand>(), deltaTime);
                    }
                    reader.EndForEachIndex();
                }

                BeginFrame(forEachCount);
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                EndMainThreadWrites();
                if (stream.IsCreated)
                {
                    stream.Dispose();
                }
            }
        }

        private void BeginFrame(int requestedForEachCount)
        {
            if (stream.IsCreated)
            {
                stream.Dispose();
            }

            forEachCount = math.max(1, requestedForEachCount);
            stream = new NativeStream(forEachCount, allocator);
            mainThreadWriter = stream.AsWriter();
            mainThreadWriter.BeginForEachIndex(MainThreadForEachIndex);
            mainThreadWriterOpen = true;
        }

        private void EndMainThreadWrites()
        {
            if (!mainThreadWriterOpen)
            {
                return;
            }

            mainThreadWriter.EndForEachIndex();
            mainThreadWriterOpen = false;
        }

        private void WriteMainThread(VoxelBodyForceCommand command)
        {
            lock (syncRoot)
            {
                if (!mainThreadWriterOpen)
                {
                    mainThreadWriter = stream.AsWriter();
                    mainThreadWriter.BeginForEachIndex(MainThreadForEachIndex);
                    mainThreadWriterOpen = true;
                }

                mainThreadWriter.Write(command);
            }
        }

        private static void ApplyCommand(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            VoxelBodyForceCommand command,
            float deltaTime)
        {
            if (!tickBuf.VoxelBodies.TryGetValue(command.BodyId, out VoxelBodyData body) ||
                !tickBuf.VoxelEntities.TryGetValue(command.BodyId, out VoxelEntityData entity) ||
                body.isStatic)
            {
                return;
            }

            VoxelBodyData.MassProperties massProperties = body.massProperties;
            if (massProperties.mass <= 0f)
            {
                return;
            }

            MotionData motionData = CurrentMotionData(entity.transform, body.motionData, massProperties.centerOfMass);
            MotionVelocity motionVelocity = CurrentMotionVelocity(body.motionVelocity, massProperties);

            switch (command.Kind)
            {
                case VoxelBodyForceKind.Force:
                    ApplyLinear(ref motionVelocity, command.Vector, massProperties.mass, command.Mode, deltaTime);
                    break;
                case VoxelBodyForceKind.Torque:
                    ApplyAngular(ref motionVelocity, motionData, command.Vector, command.Mode, deltaTime);
                    break;
                case VoxelBodyForceKind.ForceAtPosition:
                    ApplyForceAtPosition(
                        ref motionVelocity,
                        motionData,
                        command.Vector,
                        command.WorldPosition,
                        massProperties.mass,
                        command.Mode,
                        deltaTime);
                    break;
            }

            body.motionData = motionData;
            body.motionVelocity = motionVelocity;
            tickBuf.VoxelBodies[command.BodyId] = body;
        }

        private static MotionData CurrentMotionData(
            RigidTransform worldFromBody,
            MotionData persistedMotionData,
            float3 centerOfMass)
        {
            RigidTransform bodyFromMotion = new RigidTransform(quaternion.identity, centerOfMass);
            return new MotionData
            {
                WorldFromMotion = math.mul(worldFromBody, bodyFromMotion),
                BodyFromMotion = bodyFromMotion,
                LinearDamping = persistedMotionData.LinearDamping,
                AngularDamping = persistedMotionData.AngularDamping
            };
        }

        private static MotionVelocity CurrentMotionVelocity(
            MotionVelocity persistedMotionVelocity,
            VoxelBodyData.MassProperties massProperties)
        {
            persistedMotionVelocity.InverseMass = massProperties.mass > 0f ? 1.0f / massProperties.mass : 0.0f;
            persistedMotionVelocity.InverseInertia = InverseInertia(massProperties.inertiaTensor);
            return persistedMotionVelocity;
        }

        private static float3 InverseInertia(float3 inertiaTensor)
        {
            return new float3(
                inertiaTensor.x > 0f ? 1.0f / inertiaTensor.x : 0.0f,
                inertiaTensor.y > 0f ? 1.0f / inertiaTensor.y : 0.0f,
                inertiaTensor.z > 0f ? 1.0f / inertiaTensor.z : 0.0f);
        }

        private static void ApplyLinear(
            ref MotionVelocity motionVelocity,
            float3 vector,
            float mass,
            VoxelBodyForceMode mode,
            float deltaTime)
        {
            switch (mode)
            {
                case VoxelBodyForceMode.Force:
                    motionVelocity.LinearVelocity += vector * (deltaTime / mass);
                    break;
                case VoxelBodyForceMode.Acceleration:
                    motionVelocity.LinearVelocity += vector * deltaTime;
                    break;
                case VoxelBodyForceMode.Impulse:
                    motionVelocity.LinearVelocity += vector / mass;
                    break;
                case VoxelBodyForceMode.VelocityChange:
                    motionVelocity.LinearVelocity += vector;
                    break;
            }
        }

        private static void ApplyAngular(
            ref MotionVelocity motionVelocity,
            MotionData motionData,
            float3 vector,
            VoxelBodyForceMode mode,
            float deltaTime)
        {
            float3 motionSpaceVector = math.rotate(math.inverse(motionData.WorldFromMotion.rot), vector);
            switch (mode)
            {
                case VoxelBodyForceMode.Force:
                    motionVelocity.AngularVelocity += motionSpaceVector * motionVelocity.InverseInertia * deltaTime;
                    break;
                case VoxelBodyForceMode.Acceleration:
                    motionVelocity.AngularVelocity += motionSpaceVector * deltaTime;
                    break;
                case VoxelBodyForceMode.Impulse:
                    motionVelocity.AngularVelocity += motionSpaceVector * motionVelocity.InverseInertia;
                    break;
                case VoxelBodyForceMode.VelocityChange:
                    motionVelocity.AngularVelocity += motionSpaceVector;
                    break;
            }
        }

        private static void ApplyForceAtPosition(
            ref MotionVelocity motionVelocity,
            MotionData motionData,
            float3 vector,
            float3 worldPosition,
            float mass,
            VoxelBodyForceMode mode,
            float deltaTime)
        {
            float3 linearImpulse = LinearImpulse(vector, mass, mode, deltaTime);
            float3 angularImpulse = math.cross(worldPosition - motionData.WorldFromMotion.pos, linearImpulse);

            motionVelocity.LinearVelocity += linearImpulse / mass;
            float3 angularImpulseMotionSpace = math.rotate(math.inverse(motionData.WorldFromMotion.rot), angularImpulse);
            motionVelocity.AngularVelocity += angularImpulseMotionSpace * motionVelocity.InverseInertia;
        }

        private static float3 LinearImpulse(
            float3 vector,
            float mass,
            VoxelBodyForceMode mode,
            float deltaTime)
        {
            switch (mode)
            {
                case VoxelBodyForceMode.Force:
                    return vector * deltaTime;
                case VoxelBodyForceMode.Acceleration:
                    return vector * mass * deltaTime;
                case VoxelBodyForceMode.Impulse:
                    return vector;
                case VoxelBodyForceMode.VelocityChange:
                    return vector * mass;
                default:
                    return float3.zero;
            }
        }
    }
}
