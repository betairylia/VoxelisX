using System.Text;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Read-only debug instrumentation for the voxel narrowphase. Drains the per-bucket voxel
    /// contact events after a step and logs them when a body launches (linear-speed spike) or
    /// when manually armed. Never touches solver/detection state - safe to leave compiled in.
    /// </summary>
    public partial class VoxelisXPhysicsWorld
    {
        [Header("Contact Debug Logging")]
        [Tooltip("Master switch for voxel contact launch/instability logging.")]
        public bool enableContactDebugLogging = false;

        [Tooltip("Linear speed jump (m/s) within one step that counts as a launch and dumps the contact set.")]
        public float contactDebugSpikeSpeedDelta = 1.0f;

        [Tooltip("Force-log this many upcoming steps unconditionally (set >0 to watch a manual edit). Auto-decrements.")]
        public int contactDebugForceFrames = 0;

        // Extra frames logged after a spike, to watch the instability persist or recover.
        const int k_ContactDebugFollowFrames = 3;

        // Cap on the per-frame detail text so a large body cannot spam megabytes.
        const int k_ContactDebugDetailCharCap = 4000;

        float[] m_ContactDebugPrevSpeed;
        int m_ContactDebugFollowRemaining;

        /// <summary>
        /// Arms unconditional logging for the next <paramref name="frames"/> steps. Call this right
        /// before performing a voxel edit to capture the fill frame and the ones around it.
        /// </summary>
        public void ArmContactDebugLogging(int frames = 5)
        {
            enableContactDebugLogging = true;
            contactDebugForceFrames = math.max(contactDebugForceFrames, frames);
        }

        /// <summary>
        /// Logs voxel contact diagnostics for the just-completed step. Must be called on the main
        /// thread after <c>handles.FinalExecutionHandle.Complete()</c> and before the next
        /// <c>ResetSimulationContext</c> (the contact event stream is only valid in that window).
        /// </summary>
        void LogVoxelContactsAfterStep()
        {
            if (!enableContactDebugLogging)
            {
                m_ContactDebugFollowRemaining = 0;
                return;
            }

            var velocities = physicsWorld.MotionVelocities;
            int dynamicCount = math.min(nDynamic, velocities.Length);

            if (m_ContactDebugPrevSpeed == null || m_ContactDebugPrevSpeed.Length != dynamicCount)
            {
                m_ContactDebugPrevSpeed = new float[dynamicCount];
            }

            // Detect a launch: the first dynamic body whose linear speed jumped this step.
            bool spike = false;
            int spikeBody = -1;
            float spikeFrom = 0f, spikeTo = 0f;
            for (int i = 0; i < dynamicCount; i++)
            {
                float speed = math.length(velocities[i].LinearVelocity);
                if (!spike && speed - m_ContactDebugPrevSpeed[i] > contactDebugSpikeSpeedDelta)
                {
                    spike = true;
                    spikeBody = i;
                    spikeFrom = m_ContactDebugPrevSpeed[i];
                    spikeTo = speed;
                }

                m_ContactDebugPrevSpeed[i] = speed;
            }

            bool force = contactDebugForceFrames > 0;
            if (!spike && !force && m_ContactDebugFollowRemaining <= 0)
            {
                return;
            }

            if (force)
            {
                contactDebugForceFrames--;
            }

            if (spike)
            {
                m_ContactDebugFollowRemaining = k_ContactDebugFollowFrames;
            }
            else if (m_ContactDebugFollowRemaining > 0)
            {
                m_ContactDebugFollowRemaining--;
            }

            // Aggregate this frame's physics contacts, detailing the suspicious ones.
            int total = 0, freeCount = 0, binnedCount = 0;
            float minDist = float.MaxValue;
            float maxHorizNormal = 0f;
            var details = new StringBuilder();

            foreach (VoxelContactEvent e in simulation.VoxelContactEvents)
            {
                if (!e.IsPhysicsContact)
                {
                    continue;
                }

                total++;
                // if (e.NormalBin == 0) freeCount++; else binnedCount++;
                freeCount++;

                minDist = math.min(minDist, e.Distance);
                float3 n = e.Normal;
                maxHorizNormal = math.max(maxHorizNormal, math.length(new float2(n.x, n.z)));

                // Detail the newsworthy contacts: penetrating ones and binned (constrained) ones.
                // if ((e.NormalBin != 0 || e.Distance < 0f) && details.Length < k_ContactDebugDetailCharCap)
                if ((e.Distance < 0f) && details.Length < k_ContactDebugDetailCharCap)
                {
                    details.Append(
                        // $"\n  ! {(e.NormalBin == 0 ? "free  " : $"bin{e.NormalBin,3} ")}" +
                        $"\n  ! {e._debug_constraintRecord}  " +
                        $"A{e.BodyIndexA}{e.VoxelCoordsInA} B{e.BodyIndexB}{e.VoxelCoordsInB} " +
                        $"n=({n.x:F2},{n.y:F2},{n.z:F2}) d={e.Distance:F5}");
                }
            }

            if (minDist == float.MaxValue)
            {
                minDist = 0f;
            }

            string header = spike
                ? $"SPIKE body{spikeBody} |v| {spikeFrom:F3}->{spikeTo:F3}"
                : (force ? "force" : "follow");

            var velSummary = new StringBuilder();
            for (int i = 0; i < dynamicCount; i++)
            {
                float3 lv = velocities[i].LinearVelocity;
                float3 av = velocities[i].AngularVelocity;
                velSummary.Append(
                    $"\n  body{i} |v|={math.length(lv):F3} v=({lv.x:F2},{lv.y:F2},{lv.z:F2}) |w|={math.length(av):F3}");
            }

            Debug.Log(
                $"[VoxelContactDebug] frame {debugFrameCount} {header}\n" +
                $"  contacts total={total} free={freeCount} binned={binnedCount} " +
                $"minDist={minDist:F5} maxHorizN={maxHorizNormal:F3}" +
                velSummary + details);
        }
    }
}
