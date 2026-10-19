// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

namespace Live2DCSharpSDK.Framework.Math;

using System.Numerics;

/// <summary>
/// 4x4行列の便利クラス。
/// </summary>
public record CubismMatrix44
{
    private readonly float[] Ident =
    [
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    ];

    private readonly float[] _mpt1 =
    [
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    ];

    private readonly float[] _mpt2 =
    [
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    ];

    /// <summary>
    /// 4x4行列データ
    /// </summary>
    protected float[] _tr = new float[16];

    public float[] Tr => _tr;

    /// <summary>
    /// コンストラクタ。
    /// </summary>
    public CubismMatrix44()
    {
        LoadIdentity();
    }

    /// <summary>
    /// 単位行列に初期化する。
    /// </summary>
    public void LoadIdentity()
    {
        SetMatrix(Ident);
    }

    /// <summary>
    /// 行列を設定する。
    /// </summary>
    /// <param name="tr">16個の浮動小数点数で表される4x4の行列</param>
    public void SetMatrix(float[] tr)
    {
        Array.Copy(tr, _tr, 16);
    }

    public void SetMatrix(CubismMatrix44 tr)
    {
        SetMatrix(tr.Tr);
    }

    /// <summary>
    /// X軸の拡大率を取得する。
    /// </summary>
    /// <returns>X軸の拡大率</returns>
    public float GetScaleX()
    {
        return _tr[0];
    }

    /// <summary>
    /// Y軸の拡大率を取得する。
    /// </summary>
    /// <returns>Y軸の拡大率</returns>
    public float GetScaleY()
    {
        return _tr[5];
    }

    /// <summary>
    /// X軸の移動量を取得する。
    /// </summary>
    /// <returns>X軸の移動量</returns>
    public float GetTranslateX()
    {
        return _tr[12];
    }

    /// <summary>
    /// Y軸の移動量を取得する。
    /// </summary>
    /// <returns>Y軸の移動量</returns>
    public float GetTranslateY()
    {
        return _tr[13];
    }

    /// <summary>
    /// X軸の値を現在の行列で計算する。
    /// </summary>
    /// <param name="src">X軸の値</param>
    /// <returns>現在の行列で計算されたX軸の値</returns>
    public float TransformX(float src)
    {
        return _tr[0] * src + _tr[12];
    }

    /// <summary>
    /// Y軸の値を現在の行列で計算する。
    /// </summary>
    /// <param name="src">Y軸の値</param>
    /// <returns>現在の行列で計算されたY軸の値</returns>
    public float TransformY(float src)
    {
        return _tr[5] * src + _tr[13];
    }

    /// <summary>
    /// X軸の値を現在の行列で逆計算する。
    /// </summary>
    /// <param name="src">X軸の値</param>
    /// <returns>現在の行列で逆計算されたX軸の値</returns>
    public float InvertTransformX(float src)
    {
        return (src - _tr[12]) / _tr[0];
    }

    /// <summary>
    /// Y軸の値を現在の行列で逆計算する。
    /// </summary>
    /// <param name="src">Y軸の値</param>
    /// <returns>現在の行列で逆計算されたY軸の値</returns>
    public float InvertTransformY(float src)
    {
        return (src - _tr[13]) / _tr[5];
    }

    /// <summary>
    /// 現在の行列の位置を起点にして相対的に移動する。
    /// </summary>
    /// <param name="x">X軸の移動量</param>
    /// <param name="y">Y軸の移動量</param>
    public void TranslateRelative(float x, float y)
    {
        _mpt1[12] = x;
        _mpt1[13] = y;
        MultiplyByMatrix(_mpt1);
    }

    /// <summary>
    /// 現在の行列の位置を指定した位置へ移動する。
    /// </summary>
    /// <param name="x">X軸の移動量</param>
    /// <param name="y">Y軸の移動量</param>
    public void Translate(float x, float y)
    {
        _tr[12] = x;
        _tr[13] = y;
    }

    /// <summary>
    /// 現在の行列のX軸の位置を指定した位置へ移動する。
    /// </summary>
    /// <param name="x">X軸の移動量</param>
    public void TranslateX(float x)
    {
        _tr[12] = x;
    }

    /// <summary>
    /// 現在の行列のY軸の位置を指定した位置へ移動する。
    /// </summary>
    /// <param name="y">Y軸の移動量</param>
    public void TranslateY(float y)
    {
        _tr[13] = y;
    }

    /// <summary>
    /// 現在の行列の拡大率を相対的に設定する。
    /// </summary>
    /// <param name="x">X軸の拡大率</param>
    /// <param name="y">Y軸の拡大率</param>
    public void ScaleRelative(float x, float y)
    {
        _mpt2[0] = x;
        _mpt2[5] = y;
        MultiplyByMatrix(_mpt2);
    }

    /// <summary>
    /// 現在の行列の拡大率を指定した倍率に設定する。
    /// </summary>
    /// <param name="x">X軸の拡大率</param>
    /// <param name="y">Y軸の拡大率</param>
    public void Scale(float x, float y)
    {
        _tr[0] = x;
        _tr[5] = y;
    }

    public unsafe void MultiplyByMatrix(float[] a)
    {
        // 矩阵数组与 System.Numerics.Matrix4x4 的内存布局逐元素一致
        //（都是 16 个 float 顺序存放，平移分量在 index 12~14），
        // operator* 的展开式 R[0]=l0·r0+l1·r4+l2·r8+l3·r12 与原三重循环逐元素等价，
        // 因此可直接 reinterpret 成 Matrix4x4 走 JIT SIMD intrinsic（AVX/SSE 向量化）。
        // benchmark（MatrixTest）：原三重循环 ~60ns，手写展开 ~17ns，SIMD ~9ns。
        // 先整体读入两个寄存器结构再相乘，天然处理 a 与 _tr 为同一数组时的自乘别名情形。
        Matrix4x4 l, r;
        fixed (float* pa = a, pt = _tr)
        {
            l = *(Matrix4x4*)pa;
            r = *(Matrix4x4*)pt;
        }
        var result = l * r;
        fixed (float* pt = _tr)
        {
            *(Matrix4x4*)pt = result;
        }
    }

    /// <summary>
    /// 現在の行列に行列を乗算する。
    /// </summary>
    /// <param name="m">行列</param>
    public void MultiplyByMatrix(CubismMatrix44 m)
    {
        MultiplyByMatrix(m.Tr);
    }
}
