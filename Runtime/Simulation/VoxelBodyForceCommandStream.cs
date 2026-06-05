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
        public const int DefaultInitialCapacity = 128;

        /// <summary>
        /// Parallel writer for jobs, backed by <see cref="NativeList{T}.ParallelWriter"/>. The owning
        /// stream must reserve capacity via <see cref="VoxelBodyForceCommandStream.AsJobWriter"/> before
        /// the job is scheduled — the writer only does AddNoResize and will not grow the buffer.
        /// </summary>
        public struct JobWriter
        {
            public NativeList<VoxelBodyForceCommand>.ParallelWriter Writer;

            public void AddForce(Guid128 bodyId, float3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                Writer.AddNoResize(VoxelBodyForceCommand.Force(bodyId, force, mode));
            }

            public void AddTorque(Guid128 bodyId, float3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                Writer.AddNoResize(VoxelBodyForceCommand.Torque(bodyId, torque, mode));
            }

            public void AddForceAtPosition(
                Guid128 bodyId,
                float3 force,
                float3 worldPosition,
                VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
            {
                Writer.AddNoResize(VoxelBodyForceCommand.ForceAtPosition(bodyId, force, worldPosition, mode));
            }
        }

        private readonly Allocator allocator;
        private readonly object syncRoot = new object();

        // Persistent command buffer, reused across frames (cleared in ApplyTo) so steady-state
        // force submission allocates nothing. Replaces the previous per-frame NativeStream churn.
        private NativeList<VoxelBodyForceCommand> commands;

        public VoxelBodyForceCommandStream(Allocator allocator, int initialCapacity = DefaultInitialCapacity)
        {
            this.allocator = allocator;
            commands = new NativeList<VoxelBodyForceCommand>(math.max(1, initialCapacity), allocator);
        }

        public int Count
        {
            get { lock (syncRoot) { return commands.IsCreated ? commands.Length : 0; } }
        }

        /// <summary>
        /// Reserves room for <paramref name="additionalCommands"/> more entries and returns a parallel
        /// writer for jobs. Call on the main thread before scheduling; the returned writer only does
        /// AddNoResize, so the reservation must cover every concurrent write the job will perform.
        /// </summary>
        public JobWriter AsJobWriter(int additionalCommands)
        {
            lock (syncRoot)
            {
                if (additionalCommands < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(additionalCommands));
                }

                int required = commands.Length + additionalCommands;
                if (commands.Capacity < required)
                {
                    commands.Capacity = required;
                }

                return new JobWriter { Writer = commands.AsParallelWriter() };
            }
        }

        public void AddForce(Guid128 bodyId, float3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Write(VoxelBodyForceCommand.Force(bodyId, force, mode));
        }

        public void AddTorque(Guid128 bodyId, float3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Write(VoxelBodyForceCommand.Torque(bodyId, torque, mode));
        }

        public void AddForceAtPosition(
            Guid128 bodyId,
            float3 force,
            float3 worldPosition,
            VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Write(VoxelBodyForceCommand.ForceAtPosition(bodyId, force, worldPosition, mode));
        }

        public void ApplyTo(ref VoxelisXWorld.WorldStageInputs tickBuf, float deltaTime)
        {
            lock (syncRoot)
            {
                for (int i = 0; i < commands.Length; i++)
                {
                    ApplyCommand(ref tickBuf, commands[i], deltaTime);
                }

                // Reuse the buffer next frame; Clear keeps capacity, so steady state allocates nothing.
                commands.Clear();
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (commands.IsCreated)
                {
                    commands.Dispose();
                }
            }
        }

        private void Write(VoxelBodyForceCommand command)
        {
            lock (syncRoot)
            {
                commands.Add(command);
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
