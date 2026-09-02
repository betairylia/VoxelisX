using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using Caelix.Client;
using Caelix.Net;
using Caelix.Rendering.Meshing;
using Caelix.Simulation;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Caelix
{
    /// <summary>
    /// Scene bootstrap for the Host role: runs a <see cref="CaelixServer"/> with one world and a
    /// <see cref="CaelixClient"/> in the same process, connected through an in-process channel
    /// with real serialization. Scene-authored <see cref="VoxelEntity"/> components register
    /// with this host by convention (see <c>SERVER_CLIENT_ARCHITECTURE.md</c> section 4).
    /// </summary>
    /// <remarks>
    /// Client and Server roles are deferred. This component replaces the former
    /// <c>CaelixWorld</c> MonoBehaviour and keeps its script identity so existing scenes stay wired.
    /// </remarks>
    public class CaelixHost : MonoBehaviour
    {
        /// <summary>Exclusive CPU timing buckets from the last host frame.</summary>
        public struct HostTimingStats
        {
            public bool IsCreated;
            public bool UsedRayTracing;
            public bool UsedMeshing;
            public int ServerTicks;
            public double TickMilliseconds;
            public double PhysicsMilliseconds;
            public double BrickGraphMilliseconds;
            public double ClientMilliseconds;
            public double RenderingMilliseconds;
            public double TotalMilliseconds;
        }

        private const string DefaultSaveLoadFileName = "caelix-world.cxw";

        private static readonly List<CaelixHost> s_hosts = new();

        // ---------------- COMPONENTS ------------------
        [Header("Components")]
        [Tooltip("Scene-authored physics settings for the world. Optional; defaults are used when empty.")]
        [SerializeField] protected PhysicsWorldConfig physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;
        [SerializeField] protected CaelixRenderer rayTracedRenderer;
        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;

        // ---------------- SIMULATION ------------------
        [Header("Simulation")]
        [Tooltip("Server ticks per second. The fixed step is 1 / TPS.")]
        public float targetTPS = 100.0f;

        [Header("Alien Dirty Propagation")]
        [Tooltip("Propagate dirtiness between entities through the post-physics brick-overlap graph.")]
        public bool doAlienPropagation = false;

        [Tooltip("Flags a moving (non-static) entity's bricks hand to their alien neighbors.")]
        [SerializeField] private DirtyFlags alienMotionDirtyMask = DirtyFlags.GeneralAutomata;

        [Tooltip("Query every allocated brick of every non-static entity, not only the dirty ones. " +
                 "This is the heaviest input the graph can get; keep it on to benchmark motion.")]
        [SerializeField] private bool alienIncludeMovingBricks = true;

        // ---------------- DEBUG ------------------
        [Header("Debug")]
        [Tooltip("While frozen the server runs no ticks. The first frame always runs one tick so the " +
                 "client receives the initial state.")]
        public bool freeze = true;

        // ---------------- SAVE / LOAD ------------------
        [Header("Save / Load")]
        [SerializeField] private bool autoLoadOnStart = false;
        [SerializeField] private string saveLoadPath = DefaultSaveLoadFileName;

        private bool initialized;
        private bool destroyed;
        private bool firstFrameDone;
        private LocalChannel serverEnd;
        private LocalChannel clientEnd;

        public CaelixServer Server { get; private set; }
        public CaelixClient Client { get; private set; }

        /// <summary>The server world this host simulates.</summary>
        public CaelixWorld World => Server?.DefaultWorld;

        /// <summary>The client replica the renderers and tools read.</summary>
        public ClientWorld ClientWorld => Client?.World;

        public HostTimingStats LastTickTimings { get; private set; }

        /// <summary>Counters of the last alien propagation pass.</summary>
        public BrickOverlapPropagationStats LastBrickOverlapPropagationStats =>
            World != null ? World.LastBrickOverlapPropagationStats : default;

        public BrickOverlapGraph BrickOverlapGraph => World != null ? World.BrickOverlapGraph : default;

        #region Host lookup

        public static IReadOnlyList<CaelixHost> All => s_hosts;

        /// <summary>Any live host, preferring registered ones. Null when the scene has none.</summary>
        public static CaelixHost Any
        {
            get
            {
                for (int i = 0; i < s_hosts.Count; i++)
                {
                    if (s_hosts[i] != null) return s_hosts[i];
                }

                return FindFirstObjectByType<CaelixHost>();
            }
        }

        public static CaelixHost FindForScene(Scene scene)
        {
            for (int i = 0; i < s_hosts.Count; i++)
            {
                if (s_hosts[i] != null && s_hosts[i].gameObject.scene == scene) return s_hosts[i];
            }

            foreach (CaelixHost host in FindObjectsByType<CaelixHost>(FindObjectsSortMode.None))
            {
                if (host.gameObject.scene == scene) return host;
            }

            return null;
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            EnsureInitialized();
        }

        private void OnEnable()
        {
            EnsureInitialized();
            if (!s_hosts.Contains(this))
            {
                s_hosts.Add(this);
            }
        }

        private void OnDisable()
        {
            s_hosts.Remove(this);
        }

        /// <summary>
        /// Creates the server, the world, the client, and the channel between them. Safe to call
        /// from any component's <c>OnEnable</c>, which may run before this host's <c>Awake</c>.
        /// </summary>
        public void EnsureInitialized()
        {
            if (initialized || destroyed)
            {
                return;
            }

            initialized = true;
            if (!s_hosts.Contains(this))
            {
                s_hosts.Add(this);
            }

            LocalChannel.CreatePair(out serverEnd, out clientEnd);

            Server = new CaelixServer
            {
                TickRate = targetTPS,
                Frozen = freeze,
            };
            Server.CreateWorld(BuildWorldConfig());

            Client = new CaelixClient(clientEnd, Server.Types) { Host = this };
            Server.AddConnection(serverEnd);
        }

        private void Start()
        {
            if (autoLoadOnStart)
            {
                Load();
            }
        }

        private void OnDestroy()
        {
            destroyed = true;
            s_hosts.Remove(this);
            Client?.Dispose();
            Client = null;
            Server?.Dispose();
            Server = null;
        }

        private CaelixWorldConfig BuildWorldConfig()
        {
            CaelixWorldConfig config = CaelixWorldConfig.Default(0, name);
            if (physicsWorld != null)
            {
                config.physics = physicsWorld.ToSettings();
            }

            config.doAlienPropagation = doAlienPropagation;
            config.alienMotionDirtyMask = alienMotionDirtyMask;
            config.alienIncludeMovingBricks = alienIncludeMovingBricks;
            return config;
        }

        /// <summary>Pushes inspector edits into the running server and world.</summary>
        private void PushSettings()
        {
            Server.TickRate = targetTPS;
            Server.Frozen = freeze;

            CaelixWorld world = World;
            if (world == null) return;
            if (physicsWorld != null)
            {
                world.Config.physics = physicsWorld.ToSettings();
            }

            world.Config.doAlienPropagation = doAlienPropagation;
            world.Config.alienMotionDirtyMask = alienMotionDirtyMask;
            world.Config.alienIncludeMovingBricks = alienIncludeMovingBricks;
        }

        #endregion

        #region Frame

        private void Update()
        {
            if (Server == null || Client == null)
            {
                return;
            }

            long frameStart = Stopwatch.GetTimestamp();
            PushSettings();

            uint tickBefore = Server.TickIndex;
            if (!firstFrameDone)
            {
                // The initial state reaches the client through replication, which runs inside a
                // tick. A frozen scene therefore still gets exactly one tick, as it always did.
                firstFrameDone = true;
                Server.Step();
            }
            else
            {
                Server.Update(Time.deltaTime);
            }

            long serverEnd = Stopwatch.GetTimestamp();
            int serverTicks = (int)(Server.TickIndex - tickBefore);

            /////////////////////////////////////////////////////////////////////////
            // Client frame: apply replication, run input, render.
            /////////////////////////////////////////////////////////////////////////

            Client.Update();
            rayCaster?.Tick();
            long clientEnd = Stopwatch.GetTimestamp();

            bool usedRayTracing = rayTracedRenderer != null && rayTracedRenderer.enabled;
            bool usedMeshing = meshingRenderer != null && meshingRenderer.enabled;
            if (usedRayTracing) rayTracedRenderer.Tick();
            if (usedMeshing) meshingRenderer.Tick();
            long renderEnd = Stopwatch.GetTimestamp();

            Client.EndFrame();

            TickTimingStats worldTimings = World != null ? World.LastTickTimings : default;
            double total = TicksToMilliseconds(Stopwatch.GetTimestamp() - frameStart);
            LastTickTimings = new HostTimingStats
            {
                IsCreated = true,
                UsedRayTracing = usedRayTracing,
                UsedMeshing = usedMeshing,
                ServerTicks = serverTicks,
                TickMilliseconds = serverTicks > 0 ? worldTimings.TickMilliseconds : 0.0,
                PhysicsMilliseconds = serverTicks > 0 ? worldTimings.PhysicsMilliseconds : 0.0,
                BrickGraphMilliseconds = serverTicks > 0 ? worldTimings.BrickGraphMilliseconds : 0.0,
                ClientMilliseconds = TicksToMilliseconds(clientEnd - serverEnd),
                RenderingMilliseconds = TicksToMilliseconds(renderEnd - clientEnd),
                TotalMilliseconds = total,
            };
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Runs exactly <paramref name="count"/> server ticks now, frozen or not.</summary>
        public void Step(int count = 1)
        {
            EnsureInitialized();
            Server.Step(Mathf.Max(1, count));
        }

        #endregion

        #region Entities

        /// <summary>
        /// The view components of every replicated entity, in spawn order. Builds a new list on
        /// every call.
        /// </summary>
        public List<VoxelEntity> AllEntities
        {
            get
            {
                var list = new List<VoxelEntity>();
                ClientWorld world = ClientWorld;
                if (world == null) return list;
                IReadOnlyList<EntityView> views = world.Views;
                for (int i = 0; i < views.Count; i++)
                {
                    if (views[i].Component != null) list.Add(views[i].Component);
                }

                return list;
            }
        }

        #endregion

        #region Save / Load

        /// <summary>Saves the server world to a <c>.cxw</c> file at <paramref name="path"/>.</summary>
        public void Save(string path)
        {
            EnsureInitialized();
            path = EnsureWorldSaveExtension(path);
            World.Save(path);
        }

        [InspectorButton("Save World", PlayModeOnly = true)]
        public void Save()
        {
            string path = ResolveSaveLoadPath();
            Save(path);
            Debug.Log($"Saved Caelix world to {path}", this);
        }

        [InspectorButton("Load World", PlayModeOnly = true)]
        public void Load()
        {
            Load(ResolveSaveLoadPath());
        }

        /// <summary>
        /// Loads every entity stored in the <c>.cxw</c> file at <paramref name="path"/> into the
        /// server world. The client spawns view objects for them on the next tick.
        /// </summary>
        public void Load(string path)
        {
            EnsureInitialized();
            path = EnsureWorldSaveExtension(path);
            World.Load(path);
            Debug.Log($"Loaded Caelix world from {path}", this);
        }

        [InspectorButton("Choose Save/Load Path")]
        private void ChooseSaveLoadPath()
        {
#if UNITY_EDITOR
            string currentPath = ResolveSaveLoadPath();
            string directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Application.persistentDataPath;
            }

            string fileName = Path.GetFileName(currentPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = DefaultSaveLoadFileName;
            }

            string selectedPath = EditorUtility.SaveFilePanel(
                "Choose Caelix world file",
                directory,
                fileName,
                "cxw");

            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            saveLoadPath = selectedPath;
            EditorUtility.SetDirty(this);
#endif
        }

        private string ResolveSaveLoadPath()
        {
            string path = string.IsNullOrWhiteSpace(saveLoadPath)
                ? DefaultSaveLoadFileName
                : saveLoadPath;

            path = EnsureWorldSaveExtension(path);

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Application.persistentDataPath, path);
        }

        private static string EnsureWorldSaveExtension(string path)
        {
            return string.IsNullOrEmpty(Path.GetExtension(path)) ? path + ".cxw" : path;
        }

        #endregion
    }
}
