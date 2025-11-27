using System;
using System.Collections.Generic;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.World
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class Chunk : MonoBehaviour, IPoolable
    {
        private int m_currentLOD;
        private int m_targetLOD;
        private int m_vertexCount;
        private int m_triangleCount;

        private Mesh m_mesh;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private MeshCollider m_meshCollider;
        private OnMeshGenerated m_onMeshGeneratedDelegate;

        private ComputeBuffer m_voxelVolumeBuffer;
        private IRequest m_request;
        private JobHandle m_bakeJobHandle;
        private List<GPUVoxelVolumeCSGOperation> m_voxelVolumeCSGOperations;
        private ChunkFlags m_flags;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += ApplyRenderMaterial;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();
        }

        private void Update()
        {
            if (m_flags == 0)
            {
                return;
            }

            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);
            }

            if ((m_flags & ChunkFlags.CSGOperationPerformed) == ChunkFlags.CSGOperationPerformed)
            {
                m_flags &= ~ChunkFlags.CSGOperationPerformed;
                WorldManager.VoxelVolume.ApplyVoxelVolumeCSGOperations(m_voxelVolumeBuffer, transform.position, m_voxelVolumeCSGOperations);
                m_voxelVolumeCSGOperations.Clear();
            }

            if ((m_flags & ChunkFlags.MeshRegenerationRequested) == ChunkFlags.MeshRegenerationRequested && (m_flags & ChunkFlags.IsBakingMesh) != ChunkFlags.IsBakingMesh && m_request == null)
            {
                m_flags &= ~ChunkFlags.MeshRegenerationRequested;
                m_request = WorldManager.DualContouring.RequestMeshAsync
                (
                    m_voxelVolumeBuffer,
                    m_currentLOD,
                    m_targetLOD,
                    m_vertexCount,
                    m_triangleCount,
                    transform.position,
                    m_onMeshGeneratedDelegate
                );
            }

            if ((m_flags & ChunkFlags.IsBakingMesh) == ChunkFlags.IsBakingMesh && m_bakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingMesh;
                m_meshCollider.sharedMesh = null;
                m_meshCollider.sharedMesh = m_mesh;
            }
        }

        private void OnDestroy()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied -= ApplyRenderMaterial;
            ReleaseBuffers();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);
        }

        public void OnAcquire()
        {
            m_currentLOD = m_targetLOD = m_vertexCount = m_triangleCount = -1;
            CreateBuffers();
            gameObject.SetActive(true);
        }

        public void OnRelease()
        {
            m_meshFilter.sharedMesh = null;
            m_meshCollider.sharedMesh = null;
            m_request?.Cancel();
            m_request = null;
            m_voxelVolumeCSGOperations.Clear();
            m_flags = 0;
            gameObject.SetActive(false);
        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (hit.triangleIndex >= m_triangleCount)
            {
                return false;
            }

            using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(m_mesh);
            {
                Mesh.MeshData meshData = meshDataArray[0];
                NativeArray<int> triangles = meshData.GetIndexData<int>();
                NativeArray<GPUVertex> vertices = meshData.GetVertexData<GPUVertex>();

                float shortestDistanceSquared = float.MaxValue;

                for (int index = 0; index < 3; index++)
                {
                    GPUVertex vertex = vertices[triangles[3 * hit.triangleIndex + index]];
                    float distanceSquared = math.lengthsq(hit.transform.TransformPoint(vertex.Position) - hit.point);

                    if (distanceSquared < shortestDistanceSquared)
                    {
                        shortestDistanceSquared = distanceSquared;
                        materialIndex = vertex.MaterialIndex;
                    }
                }
            }

            return true;
        }

        private void CreateBuffers()
        {
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount, 2 * sizeof(uint));
            }
        }

        private void ReleaseBuffers()
        {
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
            }
        }

        public void RegenerateVoxelVolume() => m_flags |= ChunkFlags.VoxelVolumeRegenerationRequested;

        public void RegenerateMesh(int lod = -1)
        {
            if (lod != -1 && lod != m_targetLOD)
            {
                m_targetLOD = lod;
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
            else if (lod == -1)
            {
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
        }

        public void ApplyCSGPrimitiveOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive, MaterialIndex materialIndex, Matrix4x4 worldToObjectMatrix)
        {
            m_voxelVolumeCSGOperations.Add(new GPUVoxelVolumeCSGOperation(csgOperator, csgPrimitive, materialIndex, worldToObjectMatrix));
            m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
        }

        private void OnMeshGenerated(NativeArray<GPUVertex> vertices, int vertexCount, int vertexStartIndex, NativeArray<int> triangles, int triangleCount, int triangleStartIndex)
        {
            m_request = null;
            m_currentLOD = m_targetLOD;
            bool enableSmoothShading = WorldManager.VoxelConfig.DualContouringConfig.EnableSmoothShading;
            NativeArray<GPUVertex> processedVertices = vertices;
            NativeArray<int> processedTriangles = triangles;
            int processedVertexCount = vertexCount;
            int processedTriangleCount = triangleCount;
            int processedVertexStartIndex = vertexStartIndex;
            int processedTriangleStartIndex = triangleStartIndex;
            NativeArray<GPUVertex> flatShadedVertices = default;
            NativeArray<int> flatShadedTriangles = default;

            if (!enableSmoothShading && processedVertexCount > 0 && processedTriangleCount > 0)
            {
                ApplyFlatShading(vertices, vertexStartIndex, triangles, triangleCount, triangleStartIndex, out flatShadedVertices, out flatShadedTriangles);
                processedVertices = flatShadedVertices;
                processedTriangles = flatShadedTriangles;
                processedVertexCount = flatShadedVertices.Length;
                processedTriangleCount = flatShadedTriangles.Length;
                processedVertexStartIndex = 0;
                processedTriangleStartIndex = 0;
            }

            m_vertexCount = processedVertexCount;
            m_triangleCount = processedTriangleCount;

            if (processedVertexCount == 0 || processedTriangleCount == 0)
            {
                m_meshFilter.sharedMesh = null;
                m_meshCollider.sharedMesh = null;

                if (flatShadedVertices.IsCreated)
                {
                    flatShadedVertices.Dispose();
                    flatShadedTriangles.Dispose();
                }

                return;
            }

            m_mesh.SetVertexBufferParams(processedVertexCount, GPUVertex.Attributes);
            m_mesh.SetIndexBufferParams(processedTriangleCount, IndexFormat.UInt32);
#if !UNITY_EDITOR
            MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
            m_mesh.SetVertexBufferData(processedVertices, processedVertexStartIndex, 0, processedVertexCount, 0, flags);
            m_mesh.SetIndexBufferData(processedTriangles, processedTriangleStartIndex, 0, processedTriangleCount, flags);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, processedTriangleCount), flags);
            m_mesh.RecalculateBounds(flags);
#else
            m_mesh.SetVertexBufferData(processedVertices, processedVertexStartIndex, 0, processedVertexCount);
            m_mesh.SetIndexBufferData(processedTriangles, processedTriangleStartIndex, 0, processedTriangleCount);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, processedTriangleCount));
            m_mesh.RecalculateBounds(MeshUpdateFlags.DontValidateIndices);
#endif
            m_meshFilter.sharedMesh = null;
            m_meshFilter.sharedMesh = m_mesh;

            m_bakeJobHandle = new BakeJob(m_mesh.GetInstanceID()).Schedule();
            m_flags |= ChunkFlags.IsBakingMesh;

            if (flatShadedVertices.IsCreated)
            {
                flatShadedVertices.Dispose();
                flatShadedTriangles.Dispose();
            }
        }

        private void ApplyFlatShading(NativeArray<GPUVertex> vertices, int vertexStartIndex, NativeArray<int> triangles, int triangleCount, int triangleStartIndex, out NativeArray<GPUVertex> flatShadedVertices, out NativeArray<int> flatShadedTriangles)
        {
            int triangleGroupCount = triangleCount / 3;
            flatShadedVertices = new NativeArray<GPUVertex>(triangleCount, Allocator.TempJob);
            flatShadedTriangles = new NativeArray<int>(triangleCount, Allocator.TempJob);

            for (int triangleIndex = 0; triangleIndex < triangleGroupCount; triangleIndex++)
            {
                int sourceIndex = triangleStartIndex + 3 * triangleIndex;
                int firstVertexIndex = triangles[sourceIndex];
                int secondVertexIndex = triangles[sourceIndex + 1];
                int thirdVertexIndex = triangles[sourceIndex + 2];

                GPUVertex firstVertex = vertices[vertexStartIndex + firstVertexIndex];
                GPUVertex secondVertex = vertices[vertexStartIndex + secondVertexIndex];
                GPUVertex thirdVertex = vertices[vertexStartIndex + thirdVertexIndex];

                float3 edgeAB = secondVertex.Position - firstVertex.Position;
                float3 edgeAC = thirdVertex.Position - firstVertex.Position;
                float3 faceNormal = math.normalizesafe(math.cross(edgeAB, edgeAC));

                half4 packedNormal = new half4(faceNormal.x, faceNormal.y, faceNormal.z, 0.0f);
                int destinationIndex = 3 * triangleIndex;

                flatShadedVertices[destinationIndex] = GPUVertex.Create(firstVertex.Position, packedNormal, firstVertex.MaterialIndex);
                flatShadedVertices[destinationIndex + 1] = GPUVertex.Create(secondVertex.Position, packedNormal, secondVertex.MaterialIndex);
                flatShadedVertices[destinationIndex + 2] = GPUVertex.Create(thirdVertex.Position, packedNormal, thirdVertex.MaterialIndex);

                flatShadedTriangles[destinationIndex] = destinationIndex;
                flatShadedTriangles[destinationIndex + 1] = destinationIndex + 1;
                flatShadedTriangles[destinationIndex + 2] = destinationIndex + 2;
            }
        }

        private void InitializeMeshComponents()
        {
            m_mesh = new Mesh();
            m_mesh.MarkDynamic();
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();
            m_meshCollider = GetComponent<MeshCollider>();
            m_onMeshGeneratedDelegate = OnMeshGenerated;
        }

        private void ApplyRenderMaterial() => m_meshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.RenderMaterial;

        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8
        }
    }
}