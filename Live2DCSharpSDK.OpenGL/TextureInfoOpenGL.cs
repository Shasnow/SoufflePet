// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

using Live2DCSharpSDK.App;

namespace Live2DCSharpSDK.OpenGL;

public class TextureInfoOpenGL(OpenGLApi api) : TextureInfo
{
    public override void Dispose()
    {
        api.DeleteTexture(Id);
    }
}
