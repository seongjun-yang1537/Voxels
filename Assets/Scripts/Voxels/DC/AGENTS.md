[codex] Dual contouring smoothing is controlled by DualContouringConfig.EnableSmoothShading; disabling it triggers flat shading in Chunk.OnMeshGenerated using GPUVertex.Create.
[codex] GPUVertex.Create now uses a private constructor so readonly fields can be set without reassigning them after initialization.
