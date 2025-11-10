using System;
using System.Collections.Generic;
using Unity.Physics;
using UnityEngine;
using Collider = Unity.Physics.Collider;

namespace Voxelis.Simulation
{
    public class VoxelisXTestPhysicsWorld : VoxelisXPhysicsWorld
    {
        [Header("Test zone")]
        [SerializeField] private List<VoxelBody> dynamicbodies = new();
        [SerializeField] private List<VoxelBody> staticBodies = new();

        private int frameCount = 0;

        // Store collider blob assets to prevent garbage collection
        private List<Unity.Entities.BlobAssetReference<Collider>> colliders = new();

        private bool prepared = false;
        
        public override void Init()
        {
            base.Init();
            // PrepareTestWorld();
        }
        
        [ContextMenu("Prepare Test World")]
        public void PrepareTestWorld()
        {
            Debug.Log($"[PrepareTestWorld] Static bodies: {staticBodies.Count}, Dynamic bodies: {dynamicbodies.Count}");

            // Dispose existing colliders before creating new ones
            foreach (var collider in colliders)
            {
                if (collider.IsCreated)
                {
                    collider.Dispose();
                }
            }
            colliders.Clear();

            physicsWorld.Reset(staticBodies.Count, dynamicbodies.Count, 0);

            int bodyIndex = 0;

            var bodies = physicsWorld.Bodies;
            var motionDatas = physicsWorld.MotionDatas;
            var motionVelocities = physicsWorld.MotionVelocities;

            Debug.Log($"[PrepareTestWorld] Bodies array length: {bodies.Length}, MotionDatas length: {motionDatas.Length}");

            // Add dynamic bodies (with sphere colliders)
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                VoxelBody vb = dynamicbodies[i];
                Transform t = vb.transform;
                
                vb.BeforePhysicsTick();

                Debug.Log($"[Dynamic {i}] Position: {t.position}, Rotation: {t.rotation}");

                // Create material that raises collision events
                var material = new Unity.Physics.Material
                {
                    Friction = 0.5f,
                    Restitution = 0.0f,
                    FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                    RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                    CollisionResponse = Unity.Physics.CollisionResponsePolicy.CollideRaiseCollisionEvents
                };

                var voxelCollider = Unity.Physics.VoxelCollider.Create(
                    vb.entity.sectors,
                    Unity.Physics.CollisionFilter.Default,
                    material
                );

                // Store collider to prevent garbage collection
                colliders.Add(voxelCollider);

                // Create rigid body
                bodies[bodyIndex] = new Unity.Physics.RigidBody
                {
                    WorldFromBody = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    Collider = voxelCollider,
                    Entity = Unity.Entities.Entity.Null,
                    Scale = 1.0f,
                    CustomTags = 0
                };

                // Create motion data (center of mass at body origin)
                motionDatas[i] = new Unity.Physics.MotionData
                {
                    WorldFromMotion = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    BodyFromMotion = Unity.Mathematics.RigidTransform.identity,
                    LinearDamping = 0.01f,
                    AngularDamping = 0.05f
                };

                // Create motion velocity
                var massProperties = Unity.Physics.MassProperties.CreateSphere(0.5f);
                motionVelocities[i] = new Unity.Physics.MotionVelocity
                {
                    LinearVelocity = Unity.Mathematics.float3.zero,
                    AngularVelocity = Unity.Mathematics.float3.zero,
                    InverseInertia = Unity.Mathematics.math.rcp(massProperties.MassDistribution.InertiaTensor),
                    InverseMass = 1.0f / 1.0f, // mass = 1kg
                    AngularExpansionFactor = massProperties.AngularExpansionFactor,
                    GravityFactor = 1.0f
                };

                bodyIndex++;
            }
            
            // Add static bodies (with box colliders)
            for (int i = 0; i < staticBodies.Count; i++)
            {
                VoxelBody vb = staticBodies[i];
                Transform t = vb.transform;
                
                vb.BeforePhysicsTick();

                Debug.Log($"[Static {i}] Position: {t.position}, Rotation: {t.rotation}");

                // Create material that raises collision events
                var boxMaterial = new Unity.Physics.Material
                {
                    Friction = 0.5f,
                    Restitution = 0.0f,
                    FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                    RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                    CollisionResponse = Unity.Physics.CollisionResponsePolicy.CollideRaiseCollisionEvents
                };

                var voxelCollider = Unity.Physics.VoxelCollider.Create(
                    vb.entity.sectors,
                    Unity.Physics.CollisionFilter.Default,
                    boxMaterial
                );

                // Store collider to prevent garbage collection
                colliders.Add(voxelCollider);

                // Create rigid body
                bodies[bodyIndex] = new Unity.Physics.RigidBody
                {
                    WorldFromBody = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    Collider = voxelCollider,
                    Entity = Unity.Entities.Entity.Null,
                    Scale = 1.0f,
                    CustomTags = 0
                };

                bodyIndex++;
            }

            haveStaticBodiesChanged.Value = 1;
            Debug.Log($"[PrepareTestWorld] Complete. Total bodies added: {bodyIndex}");

            // Verify collision detection setup
            Debug.Log($"[PrepareTestWorld] Verifying PhysicsWorld - NumBodies: {physicsWorld.NumBodies}, NumDynamicBodies: {physicsWorld.NumDynamicBodies}, NumStaticBodies: {physicsWorld.NumStaticBodies}");

            // Check if colliders are valid
            for (int i = 0; i < bodies.Length; i++)
            {
                Debug.Log($"[Body {i}] Collider IsCreated: {bodies[i].Collider.IsCreated}, ColliderType: {bodies[i].Collider.Value.Type}");
            }

            prepared = true;
        }

        public void ExportTestWorld()
        {
            if (!prepared) return;
            
            var motionDatas = physicsWorld.MotionDatas;
            var motionVelocities = physicsWorld.MotionVelocities;

            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                // Get motion data for this dynamic body
                Unity.Physics.MotionData md = motionDatas[i];
                Unity.Physics.MotionVelocity mv = motionVelocities[i];

                // Calculate world transform: WorldFromBody = WorldFromMotion * inverse(BodyFromMotion)
                Unity.Mathematics.RigidTransform worldFromBody = Unity.Mathematics.math.mul(
                    md.WorldFromMotion,
                    Unity.Mathematics.math.inverse(md.BodyFromMotion)
                );

                if (frameCount <= 5)  // Only log first 5 frames
                {
                    Debug.Log($"[Export Dynamic {i}] Pos: {worldFromBody.pos}, Velocity: {mv.LinearVelocity}");
                }

                // Update GameObject transform
                dynamicbodies[i].transform.position = new Vector3(
                    worldFromBody.pos.x,
                    worldFromBody.pos.y,
                    worldFromBody.pos.z
                );
                dynamicbodies[i].transform.rotation = new Quaternion(
                    worldFromBody.rot.value.x,
                    worldFromBody.rot.value.y,
                    worldFromBody.rot.value.z,
                    worldFromBody.rot.value.w
                );
            }
        }

        public override void BeforeSimulationStart()
        {
            base.BeforeSimulationStart();
            // PrepareTestWorld();
        }

        public override void OnSimulationFinished()
        {
            ExportTestWorld();
        }

        private void Update()
        {
            if (!prepared) return;
            
            frameCount++;
            if (frameCount <= 5)  // Only log first 5 frames to avoid spam
            {
                Debug.Log($"[Frame {frameCount}] dt={Time.deltaTime}, Simulating...");
            }
            SimulateStep(Time.deltaTime);
        }

        private void OnDestroy()
        {
            // Dispose all colliders on cleanup
            foreach (var collider in colliders)
            {
                if (collider.IsCreated)
                {
                    if (collider.Value.Type == ColliderType.Voxel)
                    {
                        unsafe
                        {
                            var voxelCollider = (VoxelCollider*)collider.GetUnsafePtr();
                            voxelCollider->Dispose();
                        }
                    }

                    collider.Dispose();
                }
            }
            colliders.Clear();
        }
    }
}