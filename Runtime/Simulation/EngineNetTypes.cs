using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    // TODO: VibeReview: Should we replace this with SetSlotCommand?
    // Maybe not yet but I can see it become useful in the future.
    /// <summary>Client request: write one block into an entity.</summary>
    public struct SetBlockCommand
    {
        public Guid128 Entity;
        public int3 Position;
        public Block Block;
    }

    /// <summary>Client request: freeze or release an entity. Releasing a protected entity is refused.</summary>
    public struct SetEntityStaticCommand
    {
        public Guid128 Entity;
        public byte IsStatic;
    }

    /// <summary>
    /// Client request: hold a body by a point and pull that point toward a world-space target with
    /// a spring. This is held input, not a force: the server keeps one drag per client and body,
    /// computes the spring every tick from the body's current pose and velocity, and drops the
    /// drag when it is released or not refreshed within the world's drag timeout.
    /// </summary>
    public struct DragCommand
    {
        public Guid128 Entity;
        public float3 AnchorLocal;

        /// <summary>
        /// When set, the server holds the body by its centre of mass and ignores AnchorLocal for the
        /// spring. A client that draws the spring still sends its best estimate in AnchorLocal.
        /// </summary>
        public byte AnchorAtCenterOfMass;

        public float3 TargetWorld;
        public float Spring;
        public float Damping;
        public float MaxAcceleration;
    }

    /// <summary>Client request: end this client's drag on a body.</summary>
    public struct ReleaseDragCommand
    {
        public Guid128 Entity;
    }

    /// <summary>
    /// Client request: create an empty entity with a client-chosen guid, so that commands sent in the
    /// same frame can already address it. Refused when the guid is zero or already in use.
    /// </summary>
    public struct SpawnEntityCommand
    {
        public Guid128 Guid;
        public RigidTransform Transform;
        public byte IsStatic;
        public byte HasBody;
    }

    /// <summary>
    /// Engine command and event types. Registered first, in this order, on both the server and the
    /// client, so that ids agree. Games register their own types after these.
    /// </summary>
    public static class EngineNetTypes
    {
        public static void RegisterAll(NetTypeRegistry registry)
        {
            registry.Register<SetBlockCommand>();
            registry.Register<SetEntityStaticCommand>();
            registry.Register<VoxelBodyForceCommand>();
            registry.Register<DragCommand>();
            registry.Register<ReleaseDragCommand>();
            registry.Register<SpawnEntityCommand>();
        }

        internal static void RegisterServerHandlers(CaelixServer server)
        {
            server.RegisterCommand<SetBlockCommand>((connection, world, cmd) =>
            {
                world.SetBlock(cmd.Entity, cmd.Position, cmd.Block);
            });

            server.RegisterCommand<SetEntityStaticCommand>((connection, world, cmd) =>
            {
                if (!world.TryGetEntity(cmd.Entity, out VoxelEntityData data))
                {
                    return;
                }

                bool wantsStatic = cmd.IsStatic != 0;
                if (!wantsStatic && data.isProtected)
                {
                    return;
                }

                world.SetEntityStatic(cmd.Entity, wantsStatic);
            });

            server.RegisterCommand<VoxelBodyForceCommand>((connection, world, cmd) =>
            {
                world.Forces.Add(in cmd);
            });

            server.RegisterCommand<DragCommand>((connection, world, cmd) =>
            {
                world.SetDrag(connection.Id, in cmd);
            });

            server.RegisterCommand<ReleaseDragCommand>((connection, world, cmd) =>
            {
                world.ReleaseDrag(connection.Id, cmd.Entity);
            });

            server.RegisterCommand<SpawnEntityCommand>((connection, world, cmd) =>
            {
                if (cmd.Guid.IsZero)
                {
                    return;
                }

                if (!world.CreateEntity(cmd.Guid, cmd.Transform, cmd.IsStatic != 0))
                {
                    return;
                }

                if (cmd.HasBody != 0)
                {
                    world.AddBody(cmd.Guid);
                }
            });
        }
    }
}
