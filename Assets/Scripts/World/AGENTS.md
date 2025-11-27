[codex] Chunk.OnMeshGenerated now supports flat shading when DualContouringConfig.EnableSmoothShading is false; flat shading allocates temporary NativeArrays for vertices and triangles and disposes them after mesh upload.
[codex] Flat shading normals are packed with half precision using scalar components to satisfy half4 constructors.
