// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

namespace Live2DCSharpSDK.OpenGL;

public enum ShaderNames : int
{
    // SetupMask
    SetupMask,

    //Normal
    Normal,
    NormalMasked,
    NormalMaskedInverted,
    NormalPremultipliedAlpha,
    NormalMaskedPremultipliedAlpha,
    NormalMaskedInvertedPremultipliedAlpha,

    //Add
    Add,
    AddMasked,
    AddMaskedInverted,
    AddPremultipliedAlpha,
    AddMaskedPremultipliedAlpha,
    AddMaskedPremultipliedAlphaInverted,

    //Mult
    Mult,
    MultMasked,
    MultMaskedInverted,
    MultPremultipliedAlpha,
    MultMaskedPremultipliedAlpha,
    MultMaskedPremultipliedAlphaInverted,
};
