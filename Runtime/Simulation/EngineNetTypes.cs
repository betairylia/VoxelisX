using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
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
        }
    }
}
