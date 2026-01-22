using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Voxelis;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Example manager demonstrating how to integrate save/load with VoxelisX world
    /// Handles entity registration, auto-save, and streaming integration
    /// </summary>
    public class WorldPersistenceManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private WorldSaveConfiguration configuration;

        [Header("Main Infinite World")]
        [SerializeField] private VoxelEntity mainWorldEntity;

        [Header("Debug")]
        [SerializeField] private bool logSaveLoad = true;

        // Save manager
        private WorldSaveManager saveManager;

        // Entity tracking
        private Dictionary<VoxelEntity, Guid> entityGuids;
        private Guid mainWorldGuid;

        // Auto-save
        private float autoSaveTimer;

        private void Awake()
        {
            entityGuids = new Dictionary<VoxelEntity, Guid>();

            // Apply configuration
            if (configuration != null)
            {
                configuration.ApplyConfiguration();

                // Initialize save manager
                saveManager = new WorldSaveManager(configuration.GetSavePath());

                // Load or create main world GUID
                // (In a full implementation, this would load from world.meta)
                mainWorldGuid = Guid.NewGuid();
                saveManager.SaveWorldMeta(mainWorldGuid, UnityEngine.Random.Range(0, int.MaxValue));
            }
            else
            {
                Debug.LogError("WorldSaveConfiguration not assigned!");
            }
        }

        private void Start()
        {
            // Register main world entity
            if (mainWorldEntity != null)
            {
                RegisterEntity(mainWorldEntity, mainWorldGuid, EntityType.Infinite);
            }
        }

        private void Update()
        {
            if (configuration != null && configuration.enableAutoSave)
            {
                autoSaveTimer += Time.deltaTime;
                if (autoSaveTimer >= configuration.autoSaveInterval)
                {
                    AutoSave();
                    autoSaveTimer = 0f;
                }
            }
        }

        private void OnDestroy()
        {
            // Save on exit
            SaveAll();

            // Dispose save manager
            saveManager?.Dispose();
        }

        /// <summary>
        /// Register an entity for save/load tracking
        /// </summary>
        public void RegisterEntity(VoxelEntity entity, Guid guid, EntityType type)
        {
            if (entityGuids.ContainsKey(entity))
            {
                Debug.LogWarning($"Entity {entity.name} already registered");
                return;
            }

            entityGuids[entity] = guid;

            // Save initial metadata
            entity.SaveMetadata(guid, type, saveManager);

            if (logSaveLoad)
                Debug.Log($"Registered entity {entity.name} ({guid}) as {type}");
        }

        /// <summary>
        /// Unregister an entity (call before destroying)
        /// </summary>
        public void UnregisterEntity(VoxelEntity entity, bool saveBeforeUnregister = true)
        {
            if (!entityGuids.TryGetValue(entity, out var guid))
                return;

            if (saveBeforeUnregister)
            {
                // Save one last time before unregistering
                SaveEntity(entity);
            }

            entityGuids.Remove(entity);

            if (logSaveLoad)
                Debug.Log($"Unregistered entity {entity.name} ({guid})");
        }

        /// <summary>
        /// Save a single entity
        /// </summary>
        public void SaveEntity(VoxelEntity entity)
        {
            if (!entityGuids.TryGetValue(entity, out var guid))
            {
                Debug.LogWarning($"Entity {entity.name} not registered for saving");
                return;
            }

            // Determine entity type from metadata
            if (saveManager.LoadEntityMetadata(guid, out var metadata))
            {
                if (metadata.type == EntityType.Infinite)
                {
                    entity.SaveToRegions(guid, saveManager);
                }
                else
                {
                    entity.SaveFiniteEntity(guid, saveManager);
                }

                if (logSaveLoad)
                    Debug.Log($"Saved entity {entity.name} ({guid})");
            }
        }

        /// <summary>
        /// Save all registered entities
        /// </summary>
        public void SaveAll()
        {
            if (saveManager == null)
                return;

            foreach (var entity in entityGuids.Keys)
            {
                SaveEntity(entity);
            }

            saveManager.FlushAll();

            if (logSaveLoad)
                Debug.Log($"Saved all entities ({entityGuids.Count} total)");
        }

        /// <summary>
        /// Auto-save (called periodically)
        /// </summary>
        private void AutoSave()
        {
            if (configuration.saveDirtyOnly)
            {
                // TODO: Only save dirty sectors
                // For now, just save all
                SaveAll();
            }
            else
            {
                SaveAll();
            }

            if (logSaveLoad)
                Debug.Log($"Auto-save completed at {Time.time:F1}s");
        }

        /// <summary>
        /// Load a sector for the main infinite world
        /// Call this from your streaming/loading system when a sector is needed
        /// </summary>
        public bool LoadSectorForMainWorld(int3 sectorPos)
        {
            if (mainWorldEntity == null || saveManager == null)
                return false;

            bool loaded = mainWorldEntity.LoadSectorFromRegion(mainWorldGuid, sectorPos, saveManager);

            if (loaded && logSaveLoad)
                Debug.Log($"Loaded sector {sectorPos} for main world");

            return loaded;
        }

        /// <summary>
        /// Check if a sector exists in storage for the main world
        /// Helps distinguish "never generated" vs "generated but unloaded"
        /// </summary>
        public bool MainWorldSectorExists(int3 sectorPos)
        {
            if (mainWorldEntity == null || saveManager == null)
                return false;

            return mainWorldEntity.SectorExistsInStorage(mainWorldGuid, sectorPos, saveManager);
        }

        /// <summary>
        /// Load a finite entity by GUID
        /// </summary>
        public VoxelEntity LoadFiniteEntity(Guid entityGuid)
        {
            if (saveManager == null)
                return null;

            var entity = VoxelEntitySaveLoadExtensions.LoadFiniteEntity(entityGuid, saveManager, transform);

            if (entity != null)
            {
                RegisterEntity(entity, entityGuid, EntityType.Finite);

                if (logSaveLoad)
                    Debug.Log($"Loaded finite entity {entityGuid}");
            }

            return entity;
        }

        /// <summary>
        /// Get all entities intersecting a region (for streaming)
        /// </summary>
        public List<EntityMetadata> GetEntitiesInRegion(int3 regionPos)
        {
            if (saveManager == null)
                return new List<EntityMetadata>();

            return saveManager.GetEntitiesIntersectingRegion(regionPos);
        }

        // Public accessors
        public WorldSaveManager SaveManager => saveManager;
        public Guid GetEntityGuid(VoxelEntity entity)
        {
            return entityGuids.TryGetValue(entity, out var guid) ? guid : Guid.Empty;
        }
    }
}
