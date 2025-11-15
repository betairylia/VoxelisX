using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using UnityEngine.Rendering;
using Voxelis.Tick;
using Collider = Unity.Physics.Collider;
using Joint = Unity.Physics.Joint;
using Math = Unity.Physics.Math;

namespace Voxelis.Simulation
{
    public class VoxelisXTestPhysicsWorld : VoxelisXPhysicsWorld
    {
        [Header("Test zone")]
        [SerializeField] private List<VoxelBody> dynamicbodies = new();
        [SerializeField] private List<VoxelBody> staticBodies = new();
        [SerializeField] private Vector3 AxisOfRotation = Vector3.right;
        [SerializeField] private float TargetAngularVelocity = 90f;
        [SerializeField] private int RotatingDynamicObjectID = 0;

        private int frameCount = 0;

        // Store collider blob assets to prevent garbage collection
        private List<Unity.Entities.BlobAssetReference<Collider>> colliders = new();

        private bool prepared = false;

        // Contact event tracking
        private struct VoxelContactInfo
        {
            public int bodyIndex;
            public int3 voxelCoords;
            public Block originalBlock;
        }

        private List<VoxelContactInfo> previousFrameContacts = new();
        
        public override void Init()
        {
            base.Init();
            // PrepareTestWorld();
        }
        
        private void AddBodyToPhysicsWorld(VoxelBody vb, int bodyIndex)
        {
            Transform t = vb.transform;

            // Update sector data
            vb.BeforePhysicsTick();

            // Debug.Log($"[Body {bodyIndex}] Mass: {massProps.mass}, CoM: {massProps.centerOfMass}, Inertia: {massProps.inertiaTensor}, IsStatic: {vb.isStatic}");

            // Create material that raises collision events
            var material = new Unity.Physics.Material
            {
                Friction = 0.5f,
                Restitution = 0.0f,
                FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                CollisionResponse = Unity.Physics.CollisionResponsePolicy.CollideRaiseCollisionEvents
            };

            // Create VoxelCollider
            var voxelCollider = Unity.Physics.VoxelCollider.Create(
                null,
                Unity.Physics.CollisionFilter.Default,
                material
            );

            // Store collider to prevent garbage collection
            colliders.Add(voxelCollider);

            // Create rigid body
            var bodies = physicsWorld.Bodies;
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

            // If dynamic, create motion data and velocity
            if (!vb.isStatic)
            {
                int motionIndex = bodyIndex; // Dynamic bodies come first
                var motionDatas = physicsWorld.MotionDatas;
                var motionVelocities = physicsWorld.MotionVelocities;

                motionDatas[motionIndex] = new Unity.Physics.MotionData
                {
                    WorldFromMotion = RigidTransform.identity,
                    BodyFromMotion = RigidTransform.identity,
                    LinearDamping = 0.01f,
                    AngularDamping = 0.05f
                };
                
                motionVelocities[motionIndex] = new Unity.Physics.MotionVelocity
                {
                    LinearVelocity = Unity.Mathematics.float3.zero,
                    AngularVelocity = Unity.Mathematics.float3.zero,
                    InverseInertia = new float3(1, 1, 1),
                    InverseMass = 1.0f,
                    AngularExpansionFactor = 0.0f, // Will be computed by physics engine
                    GravityFactor = 1.0f
                };
            }
            
            UpdateBodyInfo(vb, bodyIndex);
        }

        private unsafe void UpdateBodyInfo(VoxelBody vb, int bodyIndex)
        {
            // Update sector data
            vb.BeforePhysicsTick();

            // Compute mass properties using VoxelBody API
            var massProps = vb.ComputeMassProperties();

            VoxelCollider* vc = (VoxelCollider*)physicsWorld.Bodies[bodyIndex].Collider.GetUnsafePtr();
            vc->ReloadSectors(vb.entity.Sectors);

            if (vb.isStatic) return;
            
            //// Dynamics
            
            int motionIndex = bodyIndex; // Dynamic bodies come first
            var motionDatas = physicsWorld.MotionDatas;
            var motionVelocities = physicsWorld.MotionVelocities;
        
            /// CoM
            
            // Create BodyFromMotion transform (offset by center of mass in body space)
            Unity.Mathematics.RigidTransform bodyFromMotion = new Unity.Mathematics.RigidTransform(
                Unity.Mathematics.quaternion.identity,
                massProps.centerOfMass
            );
            
            // WorldFromMotion = WorldFromBody * BodyFromMotion
            Unity.Mathematics.RigidTransform worldFromBody = physicsWorld.Bodies[bodyIndex].WorldFromBody;
            Unity.Mathematics.RigidTransform worldFromMotion = Unity.Mathematics.math.mul(worldFromBody, bodyFromMotion);

            var md = motionDatas[motionIndex];
            motionDatas[motionIndex] = new Unity.Physics.MotionData
            {
                WorldFromMotion = worldFromMotion,
                BodyFromMotion = bodyFromMotion,
                LinearDamping = md.LinearDamping,
                AngularDamping = md.AngularDamping
            };
            
            /// Mass
            
            // Create motion velocity with computed mass properties
            float inverseMass = massProps.mass > 0 ? 1.0f / massProps.mass : 0.0f;
            Unity.Mathematics.float3 inverseInertia = Unity.Mathematics.float3.zero;
            if (massProps.inertiaTensor.x > 0) inverseInertia.x = 1.0f / massProps.inertiaTensor.x;
            if (massProps.inertiaTensor.y > 0) inverseInertia.y = 1.0f / massProps.inertiaTensor.y;
            if (massProps.inertiaTensor.z > 0) inverseInertia.z = 1.0f / massProps.inertiaTensor.z;

            var mv = motionVelocities[motionIndex];
            motionVelocities[motionIndex] = new Unity.Physics.MotionVelocity
            {
                LinearVelocity = mv.LinearVelocity,
                AngularVelocity = mv.AngularVelocity,
                InverseInertia = inverseInertia,
                InverseMass = inverseMass,
                AngularExpansionFactor = mv.AngularExpansionFactor, // Will be computed by physics engine
                GravityFactor = mv.GravityFactor
            };
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

            physicsWorld.Reset(staticBodies.Count, dynamicbodies.Count, 1);

            int bodyIndex = 0;

            Debug.Log($"[PrepareTestWorld] Bodies array length: {physicsWorld.Bodies.Length}, MotionDatas length: {physicsWorld.MotionDatas.Length}");

            // Add dynamic bodies
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                VoxelBody vb = dynamicbodies[i];
                vb.isStatic = false;
                Debug.Log($"[Dynamic {i}] Position: {vb.transform.position}, Rotation: {vb.transform.rotation}");
                AddBodyToPhysicsWorld(vb, bodyIndex);
                bodyIndex++;
            }

            // Add static bodies
            for (int i = 0; i < staticBodies.Count; i++)
            {
                VoxelBody vb = staticBodies[i];
                vb.isStatic = true;
                Debug.Log($"[Static {i}] Position: {vb.transform.position}, Rotation: {vb.transform.rotation}");
                AddBodyToPhysicsWorld(vb, bodyIndex);
                bodyIndex++;
            }
            
            // Test joint
            // Add motor to dynamic body #0
            // A: body #0
            // B: world ("default static" in ECS)
            RigidTransform bFromA = Math.DecomposeRigidBodyTransform(
                math.mul(
                    staticBodies[0].transform.worldToLocalMatrix,
                    dynamicbodies[RotatingDynamicObjectID].transform.localToWorldMatrix)
            );

            quaternion AFromJointRot = Quaternion.FromToRotation(
                Vector3.right,
                AxisOfRotation
            );
            
            // float3 AxisOfRotation = new float3(1, 0, 0);
            float3 PivotPos = dynamicbodies[RotatingDynamicObjectID].massProperties.centerOfMass;
            Debug.Log(PivotPos);
            // Math.CalculatePerpendicularNormalized(AxisOfRotation, out var perpLocal, out _);

            Joint joint = new Joint
            {
                AFromJoint = new Math.MTransform(AFromJointRot, PivotPos),
                BFromJoint = new Math.MTransform(math.mul(bFromA.rot, AFromJointRot), math.transform(bFromA, PivotPos)),
                BodyPair = new BodyIndexPair
                {
                    BodyIndexA = RotatingDynamicObjectID,
                    BodyIndexB = dynamicbodies.Count
                },
                Entity = Entity.Null,
                EnableCollision = 0,
                Version = 0,
                Constraints = new ConstraintBlock3
                {
                    Length = 3,
                    A = Constraint.BallAndSocket(),
                    B = Constraint.Hinge(0),
                    C = Constraint.AngularVelocityMotor(math.radians(TargetAngularVelocity))
                }
            };

            var joints = physicsWorld.Joints;
            joints[0] = joint;

            haveStaticBodiesChanged.Value = 1;
            Debug.Log($"[PrepareTestWorld] Complete. Total bodies added: {bodyIndex}");

            // Verify collision detection setup
            Debug.Log($"[PrepareTestWorld] Verifying PhysicsWorld - NumBodies: {physicsWorld.NumBodies}, NumDynamicBodies: {physicsWorld.NumDynamicBodies}, NumStaticBodies: {physicsWorld.NumStaticBodies}");

            // Check if colliders are valid
            var bodies = physicsWorld.Bodies;
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

            if (!prepared) return;
            
            int bodyIndex = 0;

            // Sync dynamic bodies
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                VoxelBody vb = dynamicbodies[i];
                UpdateBodyInfo(vb, bodyIndex);
                bodyIndex++;
            }

            // Sync static bodies
            for (int i = 0; i < staticBodies.Count; i++)
            {
                VoxelBody vb = staticBodies[i];
                UpdateBodyInfo(vb, bodyIndex);
                bodyIndex++;
            }
        }

        public override void OnSimulationFinished()
        {
            if (!prepared) return;
            
            // First, reset all previously marked voxels to their original state
            foreach (var contactInfo in previousFrameContacts)
            {
                VoxelBody body = GetBodyByIndex(contactInfo.bodyIndex);
                if (body != null)
                {
                    body.entity.SetBlock(contactInfo.voxelCoords, contactInfo.originalBlock);
                }
            }

            // Clear the previous frame contacts list
            previousFrameContacts.Clear();

            // Now process the current frame's contact events
            var voxelContactEvents = simulation.VoxelContactEvents;

            // Create a red block for marking contacts
            Block redBlock = new Block(31, 8, 8, false);
            Block blueBlock = new Block(8, 8, 31, false);

            foreach (var contactEvent in voxelContactEvents)
            {
                // Process body A
                VoxelBody bodyA = GetBodyByIndex(contactEvent.BodyIndexA);
                if (bodyA != null)
                {
                    int3 voxelCoordsA = contactEvent.VoxelCoordsInA;

                    // Store original block state
                    Block originalBlockA = bodyA.entity.GetBlock(voxelCoordsA);

                    // Only store and mark if not already red (avoids duplicates)
                    if (originalBlockA != redBlock && originalBlockA != blueBlock)
                    {
                        previousFrameContacts.Add(new VoxelContactInfo
                        {
                            bodyIndex = contactEvent.BodyIndexA,
                            voxelCoords = voxelCoordsA,
                            originalBlock = originalBlockA
                        });
                    }
                    
                    // Mark in blue > red
                    if (!contactEvent.IsPhysicsContact && originalBlockA != blueBlock)
                        bodyA.entity.SetBlock(voxelCoordsA, redBlock);
                    else
                        bodyA.entity.SetBlock(voxelCoordsA, contactEvent.IsPhysicsContact ? blueBlock : redBlock);
                }

                // Process body B
                VoxelBody bodyB = GetBodyByIndex(contactEvent.BodyIndexB);
                if (bodyB != null)
                {
                    int3 voxelCoordsB = contactEvent.VoxelCoordsInB;

                    // Store original block state
                    Block originalBlockB = bodyB.entity.GetBlock(voxelCoordsB);

                    // Only store and mark if not already red (avoids duplicates)
                    if (originalBlockB != redBlock && originalBlockB != blueBlock)
                    {
                        previousFrameContacts.Add(new VoxelContactInfo
                        {
                            bodyIndex = contactEvent.BodyIndexB,
                            voxelCoords = voxelCoordsB,
                            originalBlock = originalBlockB
                        });
                    }
                    
                    // Mark in blue > red
                    if (!contactEvent.IsPhysicsContact && originalBlockB != blueBlock)
                        bodyB.entity.SetBlock(voxelCoordsB, redBlock);
                    else
                        bodyB.entity.SetBlock(voxelCoordsB, contactEvent.IsPhysicsContact ? blueBlock : redBlock);
                }
            }

            ExportTestWorld();
        }

        private VoxelBody GetBodyByIndex(int bodyIndex)
        {
            if (bodyIndex < dynamicbodies.Count)
            {
                return dynamicbodies[bodyIndex];
            }
            else
            {
                int staticIndex = bodyIndex - dynamicbodies.Count;
                if (staticIndex >= 0 && staticIndex < staticBodies.Count)
                {
                    return staticBodies[staticIndex];
                }
            }
            return null;
        }

        private void Update()
        {
            if (!prepared) return;
            
            frameCount++;
            if (frameCount <= 5)  // Only log first 5 frames to avoid spam
            {
                Debug.Log($"[Frame {frameCount}] dt={Time.deltaTime}, Simulating...");
            }
            // SimulateStep(1.0f / targetTPS);
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