using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Caelix.Tests
{
    /// <summary>
    /// Tripwire for the brick-key facade: sector types are backend details of Caelix-Core, and no
    /// NEW file outside it may name them. The allowance below lists every file that still does;
    /// milestone 3 of <c>brick-key-facade-proposal.md</c> empties it and then makes the types
    /// internal. The list only shrinks: a stale entry fails too, so it stays accurate.
    /// </summary>
    public class SectorReferenceAllowanceTests
    {
        private static readonly Regex SectorReference =
            new(@"SectorHandle|SectorNeighborHandles|\bSector\.", RegexOptions.Compiled);

        // Caelix package, relative to the package root, forward slashes.
        private static readonly string[] CaelixAllowance =
        {
            "Runtime/Authoring/VoxelEntityAuthoringTool.cs",
            "Runtime/Client/BrickReceiveBatch.cs",
            "Runtime/Client/CaelixClient.cs",
            "Runtime/Client/ClientWorld.cs",
            "Runtime/Client/InfiniteLoader.cs",
            "Runtime/Client/VoxelEntity.cs",
            "Runtime/Debugging/CaelixDebugGUI.cs",
            "Runtime/Rendering/Mesh/SectorMeshRenderer.cs",
            "Runtime/Rendering/Mesh/VoxelMeshRenderer.cs",
            "Runtime/Simulation/CaelixServer.cs",
            "Runtime/Simulation/CaelixWorld.cs",
            "Runtime/Simulation/ReplicationBatch.cs",
            "Runtime/Simulation/ServerConnection.cs",
            "Runtime/TestWorld.cs",
        };

        // Physics package, relative to the package root.
        private static readonly string[] PhysicsAllowance =
        {
            "Caelix.Physics/Runtime/Physics/BrickOverlapDirtyPropagation.cs",
            "Caelix.Physics/Runtime/Physics/BrickOverlapQueryBuilder.cs",
            "Caelix.Physics/Runtime/VoxelBodyData.PhysSlots.cs",
            "Caelix.Physics/Runtime/VoxelBodyData.cs",
            "Caelix.Physics/Runtime/VoxelCollisionSolver.cs",
            "Caelix.Physics/Runtime/VoxelEntityPhysics.cs",
            "Unity.Physics/Collision/Colliders/VoxelCollider.cs",
            "Unity.Physics/Collision/Queries/VoxelCollisionManifold.cs",
            "Unity.Physics/Dynamics/Simulation/VoxelBrickOverlap.cs",
        };

        [Test]
        public void CaelixPackage_OnlyAllowedFilesNameSectorTypes()
        {
            string root = PackageRoot(typeof(Rendering.RayQuery.CaelixRayQueryRenderer).Assembly);
            Check(root, new[] { "Runtime", "Editor" }, CaelixAllowance);
        }

        [Test]
        public void PhysicsPackage_OnlyAllowedFilesNameSectorTypes()
        {
            string root = PackageRoot(typeof(VoxelBodyData).Assembly);
            Check(root, new[] { "Caelix.Physics/Runtime", "Unity.Physics" }, PhysicsAllowance);
        }

        private static string PackageRoot(System.Reflection.Assembly assembly)
        {
            PackageInfo info = PackageInfo.FindForAssembly(assembly);
            Assert.That(info, Is.Not.Null, $"{assembly.GetName().Name} is not in a package");
            return info.resolvedPath.Replace('\\', '/').TrimEnd('/');
        }

        private static void Check(string root, string[] scanDirs, string[] allowance)
        {
            var allowed = new HashSet<string>(allowance, StringComparer.Ordinal);
            var offenders = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string dir in scanDirs)
            {
                string full = root + "/" + dir;
                if (!Directory.Exists(full)) continue;

                foreach (string file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = file.Replace('\\', '/').Substring(root.Length + 1);
                    if (!NamesSectorType(file)) continue;

                    seen.Add(relative);
                    if (!allowed.Contains(relative)) offenders.Add(relative);
                }
            }

            var stale = new List<string>();
            foreach (string entry in allowance)
            {
                if (!seen.Contains(entry)) stale.Add(entry);
            }

            Assert.That(offenders, Is.Empty,
                "New files outside Caelix-Core name sector types. Use the VoxelEntityData facade " +
                "(brick keys, TryBindBrick, EnumerateBricks, OpenNeighborhood, Changes) instead:\n  " +
                string.Join("\n  ", offenders));
            Assert.That(stale, Is.Empty,
                "These allowance entries no longer name sector types; remove them so the list keeps shrinking:\n  " +
                string.Join("\n  ", stale));
        }

        private static bool NamesSectorType(string path)
        {
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // comments and XML docs may explain history
                if (SectorReference.IsMatch(line)) return true;
            }

            return false;
        }
    }
}
