using System.Text;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Reads the voxel narrowphase funnel counters and logs them per tick, split by query.
    /// </summary>
    /// <remarks>
    /// Requires the scripting define <c>VOXELIS_CONTACT_PROFILING</c>. Without it the counters are
    /// compiled out of the physics assembly and every reading here is zero; the toggle below warns
    /// once instead of reporting silence as data.
    ///
    /// The report is a funnel, one column per query plus the total:
    ///
    ///   sources -> window roots -> touched -> active -> cell tests -> contacts
    ///
    /// The split matters because scenes load the two queries in opposite proportions. Large bodies
    /// resting on ground are rim-heavy and run mostly edge sources; machinery of cogs and chains is
    /// corner-heavy and runs mostly vertex sources. A blended total hides which query a change moved.
    ///
    /// <c>roots/contact</c> says how much of each window is swept for nothing, <c>cache hit</c>
    /// whether sector hash lookups still cost anything, and <c>rows skipped</c> how much empty space
    /// the word-level bitmask test rejects before any voxel is read.
    ///
    /// <c>touched</c> counts the roots the bitmask prefilter admitted, and the two queries use
    /// different masks: the vertex query scans occupancy, the edge query scans the physics key mask.
    /// So the vertex and edge columns of <c>touched share</c> are not measuring the same thing.
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

        const int k_ProfileLabelWidth = 18;
        const int k_ProfileColumnWidth = 13;

        static string FormatContactProfile(in VoxelContactCounters c, int divisor, int steps)
        {
            VoxelContactQueryCounters vertex = c.Vertex;
            VoxelContactQueryCounters edge = c.Edge;
            VoxelContactQueryCounters total = c.Total;

            var sb = new StringBuilder(2048);
            sb.Append("[VoxelContactProfile] ")
              .Append(divisor > 1 ? "mean of " : "last of ")
              .Append(steps).Append(divisor > 1 ? " steps" : " step(s)")
              .Append("    body pairs ")
              .AppendLine(Mean(c.BodyPairs, divisor).ToString("F1"));
            sb.AppendLine();

            Header(sb);
            Counts(sb, "sources", vertex.Sources, edge.Sources, total.Sources, divisor);
            Counts(sb, "window roots", vertex.WindowRoots, edge.WindowRoots, total.WindowRoots, divisor);
            Counts(sb, "touched", vertex.TouchedRoots, edge.TouchedRoots, total.TouchedRoots, divisor);
            Counts(sb, "active", vertex.ActiveRoots, edge.ActiveRoots, total.ActiveRoots, divisor);
            Counts(sb, "cell tests", vertex.CellTests, edge.CellTests, total.CellTests, divisor);
            Counts(sb, "contacts", vertex.ContactsEmitted, edge.ContactsEmitted, total.ContactsEmitted, divisor);
            Counts(sb, "out of range", vertex.ContactsOutOfRange, edge.ContactsOutOfRange, total.ContactsOutOfRange, divisor);
            Counts(sb, "deduped", vertex.ContactsDeduped, edge.ContactsDeduped, total.ContactsDeduped, divisor);
            Counts(sb, "degenerate", vertex.ContactsDegenerate, edge.ContactsDegenerate, total.ContactsDegenerate, divisor);
            Counts(sb, "rows tested", vertex.RowsTested, edge.RowsTested, total.RowsTested, divisor);
            Counts(sb, "brick lookups", vertex.BrickLookups, edge.BrickLookups, total.BrickLookups, divisor);

            sb.AppendLine();
            sb.AppendLine("  ---- where the sweep goes");
            Ratios(sb, "roots / source", vertex.WindowRoots, vertex.Sources, edge.WindowRoots, edge.Sources,
                total.WindowRoots, total.Sources, "F1");
            Ratios(sb, "roots / contact", vertex.WindowRoots, vertex.ContactsEmitted, edge.WindowRoots,
                edge.ContactsEmitted, total.WindowRoots, total.ContactsEmitted, "F1");
            Ratios(sb, "tests / contact", vertex.CellTests, vertex.ContactsEmitted, edge.CellTests,
                edge.ContactsEmitted, total.CellTests, total.ContactsEmitted, "F1");
            Percents(sb, "touched share", vertex.TouchedRoots, vertex.WindowRoots, edge.TouchedRoots,
                edge.WindowRoots, total.TouchedRoots, total.WindowRoots);
            Percents(sb, "active of touched", vertex.ActiveRoots, vertex.TouchedRoots, edge.ActiveRoots,
                edge.TouchedRoots, total.ActiveRoots, total.TouchedRoots);
            Percents(sb, "rows skipped", vertex.RowsSkipped, vertex.RowsTested, edge.RowsSkipped,
                edge.RowsTested, total.RowsSkipped, total.RowsTested);
            Percents(sb, "brick cache hit", vertex.BrickCacheHits, vertex.BrickLookups, edge.BrickCacheHits,
                edge.BrickLookups, total.BrickCacheHits, total.BrickLookups);
            Percents(sb, "dedup share", vertex.ContactsDeduped, vertex.ContactsDeduped + vertex.ContactsEmitted,
                edge.ContactsDeduped, edge.ContactsDeduped + edge.ContactsEmitted,
                total.ContactsDeduped, total.ContactsDeduped + total.ContactsEmitted);

            return sb.ToString();
        }

        static double Mean(long value, int divisor) => divisor > 0 ? value / (double)divisor : 0.0;

        static double Ratio(long numerator, long denominator) =>
            denominator > 0 ? numerator / (double)denominator : 0.0;

        static void Header(StringBuilder sb)
        {
            sb.Append(' ', k_ProfileLabelWidth + 2)
              .Append("vertex".PadLeft(k_ProfileColumnWidth))
              .Append("edge".PadLeft(k_ProfileColumnWidth))
              .AppendLine("total".PadLeft(k_ProfileColumnWidth));
        }

        static void Counts(StringBuilder sb, string label, long v, long e, long t, int divisor)
        {
            sb.Append("  ").Append(label.PadRight(k_ProfileLabelWidth))
              .Append(Mean(v, divisor).ToString("F1").PadLeft(k_ProfileColumnWidth))
              .Append(Mean(e, divisor).ToString("F1").PadLeft(k_ProfileColumnWidth))
              .AppendLine(Mean(t, divisor).ToString("F1").PadLeft(k_ProfileColumnWidth));
        }

        static void Ratios(StringBuilder sb, string label, long vn, long vd, long en, long ed,
            long tn, long td, string format)
        {
            sb.Append("  ").Append(label.PadRight(k_ProfileLabelWidth))
              .Append(Ratio(vn, vd).ToString(format).PadLeft(k_ProfileColumnWidth))
              .Append(Ratio(en, ed).ToString(format).PadLeft(k_ProfileColumnWidth))
              .AppendLine(Ratio(tn, td).ToString(format).PadLeft(k_ProfileColumnWidth));
        }

        static void Percents(StringBuilder sb, string label, long vn, long vd, long en, long ed,
            long tn, long td)
        {
            sb.Append("  ").Append(label.PadRight(k_ProfileLabelWidth))
              .Append((100.0 * Ratio(vn, vd)).ToString("F1").PadLeft(k_ProfileColumnWidth - 1)).Append('%')
              .Append((100.0 * Ratio(en, ed)).ToString("F1").PadLeft(k_ProfileColumnWidth - 1)).Append('%')
              .Append((100.0 * Ratio(tn, td)).ToString("F1").PadLeft(k_ProfileColumnWidth - 1)).AppendLine("%");
        }
    }
}
