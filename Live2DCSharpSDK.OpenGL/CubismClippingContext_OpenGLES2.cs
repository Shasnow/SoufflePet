// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.OpenGL;

public unsafe class CubismClippingContext_OpenGLES2(CubismClippingManager manager,
    int* clippingDrawableIndices, int clipCount) : CubismClippingContext(manager, clippingDrawableIndices, clipCount);
