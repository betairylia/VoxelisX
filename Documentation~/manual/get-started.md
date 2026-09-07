# Get started with Caelix

Status: Current API walkthrough. Checked against Caelix `253d632` and Core
`e5ead50` on 2026-09-06. The snippet was source-reviewed and compile-checked against
local Unity validation assemblies. It was not executed in Unity during this pass.

This first example creates a server world, writes one voxel, sends it to a local
client, and checks the replica. Its success signal is a Console message. Scene
rendering can be added after the data path works.

## Install the packages

1. Use a Unity project compatible with the package manifests. The current Caelix
   and Core manifests declare Unity `6000.5`. Check the Physics manifest too;
   the manifests and the project's resolved package versions are the authority
   for setup. The Unity documentation versions used in our writing guide are
   reference material, not an installation recipe.
2. Clone Caelix, Caelix-Core, and Caelix-physics at compatible revisions.
3. In Package Manager, use **Install package from disk** (called **Add package
   from disk** in some versions) and select Core's `package.json`, then Physics's,
   then Caelix's. Let Unity resolve the registry dependencies.
4. Wait for compilation. Address package resolution or compiler errors before
   running the example. In a custom assembly definition, reference `Caelix.Core`,
   `Caelix.Simulation`, and `Caelix`, plus directly used Unity assemblies.

These are Unity packages rather than standalone Unity projects. The previous
[local-host validation report](../archive/validation/RND_VALIDATION.md) records
one working project, exact package revisions, and its test commands. It is dated
evidence; the example below has not inherited that report's test result.

## Run a minimal exchange

Save the following as `Assets/Editor/CaelixGettingStarted.cs` in your project.
After it compiles, select **Tools > Caelix > Run data example**.

```csharp
using Caelix;
using Caelix.Client;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

public static class CaelixGettingStarted
{
    [MenuItem("Tools/Caelix/Run data example")]
    public static void Run()
    {
        using var server = new CaelixServer();
        var world = server.CreateWorld(CaelixWorldConfig.Default());
        LocalChannel.CreatePair(out var serverEnd, out var clientEnd);
        using var client = new CaelixClient(clientEnd, server.Types);
        server.AddConnection(serverEnd);

        var entityId = new Guid128(1u, 2u, 3u, 4u);
        var position = new int3(1, 2, 3);
        var block = new Block(0x8001);
        world.CreateEntity(entityId, RigidTransform.identity, isStatic: true);
        world.SetBlock(entityId, position, block);

        server.Step();
        client.Receive();
        client.PrepareRender();
        // A renderer would consume the replica here.
        client.EndFrame();

        if (!client.World.TryGetView(entityId, out var view) ||
            !view.Data.GetBlock(position).Equals(block))
            throw new System.InvalidOperationException("Replica did not match.");
        Debug.Log("Caelix: one voxel reached the client replica.");
    }
}
```

The fixed GUID is local to this disposable example. A running game must create
unique nonzero entity IDs. The client is disposed before the server; the world
belongs to the server. This example shares a type registry as the local host does.

## Use scene authoring

For a scene workflow, create one `CaelixHost`. It initializes a server, default
world, local channel, and client. Its `freeze` field defaults to true: the first
tick still sends initial state, and subsequent simulation requires unfreezing
or calling `host.Step()`.

Add `VoxelEntity` to a scene object or use the existing
[voxel spline authoring tool](voxel-spline-authoring.md). An authored entity
resolves its serialized host reference, a parent host, or `CaelixHost.Current`.
Keep the entity's scale at 1. Rendering also requires configuring and assigning
the host's mesh or ray-traced renderer; adding a host alone does not draw voxels.
Use the [budget renderer setup](../internals/rendering/BUDGET_RENDERER.md#setup)
when working with that rendering path.

## Load experimental packed scene colors

On `dev/astra/raw-color-materials`, enable `CAELIX_PACKED_SCENE_COLOR` in both
[Core's BlockEncoding.cs](../../../Caelix-Core/Runtime/BlockEncoding.cs) and
[CaelixMaterialConfig.hlsl](../../Runtime/Rendering/Shaders/def/CaelixMaterialConfig.hlsl).
Comment out both defines to restore regular block IDs. Restart with data produced
for the chosen mode. Titania's material generator preserves this selection, and
the world skips automata in packed mode while continuing dirty propagation and
replication. There is no runtime mode setting or additional `.cxw` encoding marker.

The [exporter](../../Tools~/RawColor/export_scene.py) writes native v6 `.cxw` files
from OBJ materials using Python 3.11+, NumPy and Pillow. It preserves alpha cutouts
at a 0.5 threshold, samples emission maps, and accepts explicit material-name to
shared-glass-ID mappings. Maps with MTL texture options must be preprocessed first.
Use the existing save loader for the resulting file. Keep outputs outside Unity
project/package folders, for example `Caelix-family/VoxelTestScenes`.

From the family directory, using the locally built alpha-fixed DLL:

```powershell
py -3.11 Caelix/Tools~/RawColor/export_scene.py Research/alpha-voxelization/bistro-interior/scene.obj VoxelTestScenes/bistro-interior.4096.packed.cxw --dll Research/alpha-voxelization/obj2voxel/build/obj2voxel-shared.dll --mtl Research/alpha-voxelization/bistro-interior/scene-original.mtl --glass Caelix/Tools~/RawColor/bistro-interior-glass.json --up z --resolution 4096
```

The matching exterior mapping is `bistro-exterior-glass.json`. These are stable
shared palette selections, with clear/frosted windows and tinted bottles; they
approximate the source materials. Metallic and rough opaque materials are reduced
to diffuse color. The default emission multiplier before quantization is 64;
`--emission-scale` changes it. `CAELIX_PACKED_EMISSION_SCALE` is the global shader
adjustment. This is material emission, not a reconstruction of source light objects.

For a fresh native build, use obj2voxel commit
`9fb8ae2caffa2732b6ceb064bdf2229532c54bac` with its pinned submodules. Apply
[`obj2voxel.patch`](../../Tools~/RawColor/obj2voxel.patch) in that checkout and
[`voxelio.patch`](../../Tools~/RawColor/voxelio.patch) inside its `voxelio` submodule.
On Windows, replace the placeholder `src/3rd_party/args.hpp` and `tinyobj.hpp`
symlinks with `tayweeargs/args.hxx` and `tinyobjloader/tiny_obj_loader.h`. Build the
`obj2voxel-shared` CMake target with clang-cl/Ninja from a Visual Studio developer
shell, using C++ flags `/EHsc /clang:-mbmi2 /clang:-fconstexpr-steps=10000000`.

[`test_export.py`](../../Tools~/RawColor/test_export.py) verifies all 65,536 codes
through native textured voxelization and generates the small Unity interoperability
fixture. The Unity tests compare all CPU/GPU material codes and face bits, read
native saves and previews, and verify automata/replication behavior in both modes.
Both modes passed 237 EditMode tests locally. Full scene appearance still needs
inspection in Titania's path tracer.

## If the result is unexpected

| Symptom | Check |
|---|---|
| A client edit is not visible yet | Call/allow a server step, then receive on the client. Sending only queues it. |
| Edits remain queued while paused | This is intentional. Use an explicit step or unfreeze. Queries remain available. |
| The first automata pass has no work | Initial writes become required work through propagation; see the tick guide. |
| Data exists but the Scene/Game view is empty | Verify the renderer binding, camera, and rendering-path setup. |
| A type/assembly cannot be found | Check package compilation and custom assembly references. |

## Next steps

- [Write an automaton](automata.md)
- [Use the server and client](server-and-client.md)
- [The tested exchange pattern](../../Tests/Editor/ReplicationTests.cs): `Rig.Exchange` and `InitialSync_ReplicatesEntitiesSectorsAndBlocks`
