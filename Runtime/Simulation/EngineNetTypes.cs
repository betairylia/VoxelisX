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
        }
    }
}
