using System.Text;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Reads the voxel narrowphase funnel counters and logs them per tick.
    /// </summary>
    /// <remarks>
    /// Requires the scripting define <c>VOXELIS_CONTACT_PROFILING</c>. Without it the counters are
    /// compiled out of the physics assembly and every reading here is zero; the toggle below warns
    /// once instead of reporting silence as data.
    ///
    /// The report is a funnel. Each stage narrows the one above it, so the ratios say where the
    /// work goes:
    ///
    ///   body pairs -> source features -> window roots -> occupied -> active -> cell tests -> contacts
    ///
    /// Two ratios decide whether the target loop is worth restructuring into a gathered local
    /// window and a branch-free kernel. <c>roots/contact</c> is how much of each window is swept
    /// for nothing. <c>cache hit</c> is whether sector hash lookups still cost anything after the
    /// brick cache. If roots/contact is small and the cache hit rate is high, the loop is already
    /// near its floor and the remaining cost is arithmetic, not search.
    /// </remarks>
    public partial class VoxelisXPhysicsWorld
    {
        [Header("Contact Profiling")]
        [Tooltip("Collect and log voxel narrowphase funnel counters. Needs the VOXELIS_CONTACT_PROFILING scripting define.")]
        public bool enableContactProfiling = false;

        [Tooltip("Log every Nth step. 1 logs every step; larger values keep the console readable while the numbers settle.")]
        public int contactProfilingLogInterval = 60;

        [Tooltip("Average the counters over the interval instead of reporting only the last step.")]
        public bool contactProfilingAverage = true;

        VoxelContactCounters m_ContactProfileAccumulated;
        int m_ContactProfileSteps;
        bool m_ContactProfileWarned;

        /// <summary>
        /// Clears the tick accumulator. Must run before the step's jobs are scheduled.
        /// </summary>
        void BeginVoxelContactProfiling()
        {
            if (!enableContactProfiling)
            {
                return;
            }

            if (!WarnIfContactProfilingUnavailable())
            {
                return;
            }

            VoxelContactProfiler.Enabled = true;
            VoxelContactProfiler.Reset();
        }

        /// <summary>
        /// Reads the tick totals and logs them on the configured interval. Must run after the
        /// step's jobs have completed.
        /// </summary>
        void LogVoxelContactProfileAfterStep()
        {
            if (!enableContactProfiling)
            {
                if (m_ContactProfileSteps != 0)
                {
                    m_ContactProfileAccumulated = default;
                    m_ContactProfileSteps = 0;
                    VoxelContactProfiler.Enabled = false;
                }
                return;
            }

            if (!VoxelContactProfiler.IsCollecting)
            {
                return;
            }

            VoxelContactCounters step = VoxelContactProfiler.Snapshot();
            m_ContactProfileAccumulated.Add(step);
            m_ContactProfileSteps++;

            int interval = math.max(1, contactProfilingLogInterval);
            if (m_ContactProfileSteps < interval)
            {
                return;
            }

            VoxelContactCounters report = contactProfilingAverage ? m_ContactProfileAccumulated : step;
            int divisor = contactProfilingAverage ? m_ContactProfileSteps : 1;
            Debug.Log(FormatContactProfile(report, divisor, m_ContactProfileSteps));

            m_ContactProfileAccumulated = default;
            m_ContactProfileSteps = 0;
        }

        /// <summary>
        /// True when the counters are actually compiled in. Warns once when they are not, so an
        /// all-zero report is never mistaken for a measurement.
        /// </summary>
        bool WarnIfContactProfilingUnavailable()
        {
            VoxelContactProfiler.Enabled = true;
            if (VoxelContactProfiler.IsCollecting)
            {
                return true;
            }

            if (!m_ContactProfileWarned)
            {
                m_ContactProfileWarned = true;
                Debug.LogWarning(
                    "[VoxelContactProfile] Counters are compiled out. Add VOXELIS_CONTACT_PROFILING "
                    + "to Project Settings > Player > Scripting Define Symbols to collect them.");
            }
            return false;
        }

        static string FormatContactProfile(in VoxelContactCounters c, int divisor, int steps)
        {
            double Per(long value) => divisor > 0 ? value / (double)divisor : 0.0;
            double Ratio(long numerator, long denominator) =>
                denominator > 0 ? numerator / (double)denominator : 0.0;

            long sources = c.VertexSources + c.EdgeSources;
            var sb = new StringBuilder(768);

            sb.Append("[VoxelContactProfile] ")
              .Append(divisor > 1 ? "mean of " : "last of ")
              .Append(steps).AppendLine(divisor > 1 ? " steps" : " step(s)");

            sb.Append("  body pairs      ").AppendLine(Per(c.BodyPairs).ToString("F1"));
            sb.Append("  sources         ").Append(Per(sources).ToString("F1"))
              .Append("   (vertex ").Append(Per(c.VertexSources).ToString("F1"))
              .Append(", edge ").Append(Per(c.EdgeSources).ToString("F1")).AppendLine(")");
            sb.Append("  window roots    ").Append(Per(c.WindowRoots).ToString("F1"))
              .Append("   occupied ").Append(Per(c.OccupiedRoots).ToString("F1"))
              .Append("   active ").AppendLine(Per(c.ActiveRoots).ToString("F1"));
            sb.Append("  cell tests      ").AppendLine(Per(c.CellTests).ToString("F1"));
            sb.Append("  contacts        ").Append(Per(c.ContactsEmitted).ToString("F1"))
              .Append("   out of range ").Append(Per(c.ContactsOutOfRange).ToString("F1"))
              .Append("   deduped ").Append(Per(c.ContactsDeduped).ToString("F1"))
              .Append("   degenerate ").AppendLine(Per(c.ContactsDegenerate).ToString("F1"));
            sb.Append("  brick lookups   ").Append(Per(c.BrickLookups).ToString("F1"))
              .Append("   cache hit ")
              .Append((100.0 * Ratio(c.BrickCacheHits, c.BrickLookups)).ToString("F1")).AppendLine("%");

            sb.AppendLine("  ---- ratios that decide whether the target loop needs restructuring");
            sb.Append("  roots / source  ").AppendLine(Ratio(c.WindowRoots, sources).ToString("F1"));
            sb.Append("  roots / contact ").AppendLine(Ratio(c.WindowRoots, c.ContactsEmitted).ToString("F1"));
            sb.Append("  tests / contact ").AppendLine(Ratio(c.CellTests, c.ContactsEmitted).ToString("F1"));
            sb.Append("  occupied share  ")
              .Append((100.0 * Ratio(c.OccupiedRoots, c.WindowRoots)).ToString("F1")).AppendLine("%");
            sb.Append("  active share    ")
              .Append((100.0 * Ratio(c.ActiveRoots, c.OccupiedRoots)).ToString("F1")).AppendLine("% of occupied");
            sb.Append("  dedup share     ")
              .Append((100.0 * Ratio(c.ContactsDeduped, c.ContactsDeduped + c.ContactsEmitted)).ToString("F1"))
              .AppendLine("% of in-range hits");

            return sb.ToString();
        }
    }
}
