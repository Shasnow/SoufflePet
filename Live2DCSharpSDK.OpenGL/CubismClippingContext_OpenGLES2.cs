using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.OpenGL;

public unsafe class CubismClippingContext_OpenGLES2(CubismClippingManager manager,
    int* clippingDrawableIndices, int clipCount) : CubismClippingContext(manager, clippingDrawableIndices, clipCount);