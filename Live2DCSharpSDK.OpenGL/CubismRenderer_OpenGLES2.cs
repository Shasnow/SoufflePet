using System.Runtime.InteropServices;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;

namespace Live2DCSharpSDK.OpenGL;

public class CubismRenderer_OpenGLES2 : CubismRenderer
{
    public const int ColorChannelCount = 4;   // 実験時に1チャンネルの場合は1、RGBだけの場合は3、アルファも含める場合は4
    public const int ClippingMaskMaxCountOnDefault = 36;  // 通常のフレームバッファ1枚あたりのマスク最大数
    public const int ClippingMaskMaxCountOnMultiRenderTexture = 32;   // フレームバッファが2枚以上ある場合のフレームバッファ1枚あたりのマスク最大数

    private readonly OpenGLApi GL;

    /// <summary>
    /// モデルが参照するテクスチャとレンダラでバインドしているテクスチャとのマップ
    /// </summary>
    private readonly Dictionary<int, int> _textures;
    /// <summary>
    /// 描画オブジェクトのインデックスを描画順に並べたリスト
    /// </summary>
    private readonly int[] _sortedDrawableIndexList;
    /// <summary>
    /// OpenGLのステートを保持するオブジェクト
    /// </summary>
    private readonly CubismRendererProfile_OpenGLES2 _rendererProfile;
    /// <summary>
    /// クリッピングマスク管理オブジェクト
    /// </summary>
    private CubismClippingManager_OpenGLES2? _clippingManager;
    /// <summary>
    /// マスクテクスチャに描画するためのクリッピングコンテキスト
    /// </summary>
    internal CubismClippingContext? ClippingContextBufferForMask { get; set; }
    /// <summary>
    /// 画面上描画するためのクリッピングコンテキスト
    /// </summary>
    internal CubismClippingContext? ClippingContextBufferForDraw { get; set; }

    internal CubismShader_OpenGLES2 Shader;

    /// <summary>
    /// マスク描画用のフレームバッファ
    /// </summary>
    private readonly List<CubismOffscreenSurface_OpenGLES2> _offscreenFrameBuffers = [];

    internal int VertexArray { get; private set; }

    // 每个 drawable 一组独立网格缓冲。
    // 索引与 UV 是静态数据（加载后永不变形），只在首次绘制时上传一次；
    // 顶点位置每帧随动作形变，用独立动态缓冲按固定容量原地更新。
    // 原实现整个渲染器共用一对 VBO/IBO 交错存放 位置+UV 的 AoS 数据，
    // 每次绘制都要逐顶点搬运 + 重传 UV/索引，是性能分析中 DrawMesh/BufferSubData 热点的来源。
    private int[] _posBuffers = [];
    private int[] _uvBuffers = [];
    private int[] _indexBuffers = [];
    private bool[] _meshPrepared = [];

    /// <summary>
    /// Tegraプロセッサ対応。拡張方式による描画の有効・無効
    /// </summary>
    /// <param name="extMode">trueなら拡張方式で描画する</param>
    /// <param name="extPAMode">trueなら拡張方式のPA設定を有効にする</param>
    public void SetExtShaderMode(bool extMode, bool extPAMode = false)
    {
        Shader.SetExtShaderMode(extMode, extPAMode);
        Shader.ReleaseShaderProgram();
    }

    private readonly LAppDelegate _lapp;

    public unsafe CubismRenderer_OpenGLES2(OpenGLApi gl, LAppDelegate lapp, CubismModel model, int maskBufferCount = 1) : base(model)
    {
        GL = gl;
        Shader = new(gl);
        _lapp = lapp;
        _rendererProfile = new(gl);
        _textures = new Dictionary<int, int>(32);

        VertexArray = GL.GenVertexArray();

        // 按 drawable 数量预分配缓冲句柄数组（缓冲本身在首次绘制时懒创建）
        var drawableCount = model.GetDrawableCount();
        _posBuffers = new int[drawableCount];
        _uvBuffers = new int[drawableCount];
        _indexBuffers = new int[drawableCount];
        _meshPrepared = new bool[drawableCount];

        // 1未満は1に補正する
        if (maskBufferCount < 1)
        {
            maskBufferCount = 1;
            CubismLog.Warning("[Live2D OpenGL]The number of render textures must be an integer greater than or equal to 1. Set the number of render textures to 1.");
        }

        if (model.IsUsingMasking())
        {
            _clippingManager = new CubismClippingManager_OpenGLES2(GL);  //クリッピングマスク・バッファ前処理方式を初期化
            _clippingManager.Initialize(model, maskBufferCount);

            _offscreenFrameBuffers.Clear();
            for (int i = 0; i < maskBufferCount; ++i)
            {
                var offscreenSurface = new CubismOffscreenSurface_OpenGLES2(GL);
                offscreenSurface.CreateOffscreenSurface((int)_clippingManager.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);
                _offscreenFrameBuffers.Add(offscreenSurface);
            }
        }

        _sortedDrawableIndexList = new int[model.GetDrawableCount()];
    }

    /// <summary>
    /// OpenGLテクスチャのバインド処理
    /// CubismRendererにテクスチャを設定し、CubismRenderer中でその画像を参照するためのIndex値を戻り値とする
    /// </summary>
    /// <param name="modelTextureNo">セットするモデルテクスチャの番号</param>
    /// <param name="glTextureNo">OpenGLテクスチャの番号</param>
    public void BindTexture(int modelTextureNo, int glTextureNo)
    {
        _textures[modelTextureNo] = glTextureNo;
    }

    /// <summary>
    /// OpenGLにバインドされたテクスチャのリストを取得する
    /// </summary>
    /// <returns>テクスチャのアドレスのリスト</returns>
    public Dictionary<int, int> GetBindedTextures()
    {
        return _textures;
    }

    /// <summary>
    /// クリッピングマスクバッファのサイズを設定する
    /// マスク用のFrameBufferを破棄・再作成するため処理コストは高い。
    /// </summary>
    /// <param name="width">クリッピングマスクバッファのサイズ</param>
    /// <param name="height">クリッピングマスクバッファのサイズ</param>
    public unsafe void SetClippingMaskBufferSize(float width, float height)
    {
        if (_clippingManager == null)
        {
            return;
        }

        // インスタンス破棄前にレンダーテクスチャの数を保存
        int renderTextureCount = _clippingManager.RenderTextureCount;

        _clippingManager = new CubismClippingManager_OpenGLES2(GL);

        _clippingManager.SetClippingMaskBufferSize(width, height);

        _clippingManager.Initialize(Model, renderTextureCount);
    }

    /// <summary>
    /// クリッピングマスクのバッファを取得する
    /// </summary>
    /// <returns>クリッピングマスクのバッファへのポインタ</returns>
    public CubismOffscreenSurface_OpenGLES2 GetMaskBuffer(int index)
    {
        return _offscreenFrameBuffers[index];
    }

    public bool IsGeneratingMask()
    {
        return ClippingContextBufferForMask != null;
    }

    /// <summary>
    /// [オーバーライド]
    /// 描画オブジェクト（アートメッシュ）を描画する。
    /// ポリゴンメッシュとテクスチャ番号をセットで渡す。
    /// </summary>
    /// <param name="textureNo">描画するテクスチャ番号</param>
    /// <param name="indexCount">描画オブジェクトのインデックス値</param>
    /// <param name="vertexCount">ポリゴンメッシュの頂点数</param>
    /// <param name="indexArray">ポリゴンメッシュのインデックス配列</param>
    /// <param name="vertexArray">ポリゴンメッシュの頂点配列</param>
    /// <param name="uvArray">uv配列</param>
    /// <param name="multiplyColor">乗算色</param>
    /// <param name="screenColor">スクリーン色</param>
    /// <param name="opacity">不透明度</param>
    /// <param name="colorBlendMode">カラー合成タイプ</param>
    /// <param name="invertedMask">マスク使用時のマスクの反転使用</param>
    internal unsafe void DrawMeshOpenGL(CubismModel model, int index)
    {
        if (_textures[model.GetDrawableTextureIndex(index)] == 0) return;    // モデルが参照するテクスチャがバインドされていない場合は描画をスキップする

        // 裏面描画の有効・無効
        if (IsCulling)
        {
            GL.Enable(GL.GL_CULL_FACE);
        }
        else
        {
            GL.Disable(GL.GL_CULL_FACE);
        }

        GL.FrontFace(GL.GL_CCW);    // Cubism SDK OpenGLはマスク・アートメッシュ共にCCWが表面

        var vertexCount = model.GetDrawableVertexCount(index);
        var vertexArray = model.GetDrawableVertices(index);
        var uvArray = (float*)model.GetDrawableVertexUvs(index);
        int indexCount = model.GetDrawableVertexIndexCount(index);
        ushort* indexArray = model.GetDrawableVertexIndices(index);

        // 首次绘制该 drawable 时创建并上传网格缓冲（索引/UV 是静态数据，只上传这一次）
        EnsureMeshBuffers(index, vertexCount, indexCount, uvArray, indexArray);

        GL.BindVertexArray(VertexArray);

        // 顶点位置每帧形变：模型数据本就是 SoA 布局，直接从指针原地写入位置缓冲，
        // 无需再逐顶点搬运成交错的 AoS 结构体
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, _posBuffers[index]);
        GL.BufferSubData(GL.GL_ARRAY_BUFFER, 0, (nint)(vertexCount * 2 * sizeof(float)), vertexArray);

        // 索引缓冲绑定属于 VAO 状态：每个 drawable 的 IBO 不同，绘制前绑定对应的
        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, _indexBuffers[index]);

        if (IsGeneratingMask())  // マスク生成時
        {
            Shader.SetupShaderProgramForMask(this, model, index);
        }
        else
        {
            Shader.SetupShaderProgramForDraw(this, model, index);
        }

        // ポリゴンメッシュを描画する
        GL.DrawElements(GL.GL_TRIANGLES, indexCount, GL.GL_UNSIGNED_SHORT, 0);

        // 后处理：不再逐个 drawable 解绑 GL 状态。
        // 原 SDK 这里会执行 UseProgram(0) + BindBuffer(0) + BindVertexArray(0)，
        // 下一个 drawable 又要把 program / 缓冲 / VAO / 纹理全部重新绑定，
        // 每帧数百次无意义的状态翻转；去掉后由下一次绘制自行绑定所需状态即可。
        ClippingContextBufferForDraw = null;
        ClippingContextBufferForMask = null;
    }

    /// <summary>
    /// 确保 drawable 的网格缓冲就绪（每个 drawable 只执行一次）。
    /// 索引与 UV 为静态数据，按 STATIC_DRAW 一次性上传；
    /// 顶点位置缓冲按该 drawable 固定的顶点数分配存储（DYNAMIC_DRAW），
    /// 供每帧 BufferSubData 原地更新，扩容/容量缓存逻辑由此彻底消失。
    /// </summary>
    private unsafe void EnsureMeshBuffers(int index, int vertexCount, int indexCount, float* uvArray, ushort* indexArray)
    {
        if (_meshPrepared[index]) return;
        _meshPrepared[index] = true;

        _posBuffers[index] = GL.GenBuffer();
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, _posBuffers[index]);
        GL.BufferData(GL.GL_ARRAY_BUFFER, vertexCount * 2 * sizeof(float), IntPtr.Zero, GL.GL_DYNAMIC_DRAW);

        _uvBuffers[index] = GL.GenBuffer();
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, _uvBuffers[index]);
        GL.BufferData(GL.GL_ARRAY_BUFFER, vertexCount * 2 * sizeof(float), (nint)uvArray, GL.GL_STATIC_DRAW);

        _indexBuffers[index] = GL.GenBuffer();
        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, _indexBuffers[index]);
        GL.BufferData(GL.GL_ELEMENT_ARRAY_BUFFER, indexCount * sizeof(ushort), (nint)indexArray, GL.GL_STATIC_DRAW);
    }

    /// <summary>drawable 的顶点位置缓冲（每帧原地更新）。</summary>
    internal int GetPositionBuffer(int index) => _posBuffers[index];

    /// <summary>drawable 的 UV 缓冲（静态，仅首次上传）。</summary>
    internal int GetUvBuffer(int index) => _uvBuffers[index];

    /// <summary>
    /// 描画開始時の追加処理。
    /// モデルを描画する前にクリッピングマスクに必要な処理を実装している。
    /// </summary>
    internal void PreDraw()
    {
        GL.Disable(GL.GL_SCISSOR_TEST);
        GL.Disable(GL.GL_STENCIL_TEST);
        GL.Disable(GL.GL_DEPTH_TEST);

        GL.Enable(GL.GL_BLEND);
        GL.ColorMask(true, true, true, true);

        if (GL.IsPhoneES2)
        {
            GL.BindVertexArrayOES(0);
        }

        GL.BindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, 0);
        GL.BindBuffer(GL.GL_ARRAY_BUFFER, 0); //前にバッファがバインドされていたら破棄する必要がある

        //異方性フィルタリング。プラットフォームのOpenGLによっては未対応の場合があるので、未設定のときは設定しない
        if (Anisotropy > 0.0f)
        {
            for (int i = 0; i < _textures.Count; i++)
            {
                GL.BindTexture(GL.GL_TEXTURE_2D, _textures[i]);
                GL.TexParameterf(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAX_ANISOTROPY_EXT, Anisotropy);
            }
        }
    }

    /// <summary>
    /// モデル描画直前のOpenGLES2のステートを保持する
    /// </summary>
    protected override void SaveProfile()
    {
        _rendererProfile.Save();
    }

    /// <summary>
    /// モデル描画直前のOpenGLES2のステートを保持する
    /// </summary>
    protected override void RestoreProfile()
    {
        _rendererProfile.Restore();
    }

    protected override unsafe void DoDrawModel()
    {
        //------------ クリッピングマスク・バッファ前処理方式の場合 ------------
        if (_clippingManager != null)
        {
            PreDraw();

            // サイズが違う場合はここで作成しなおし
            for (int i = 0; i < _clippingManager.RenderTextureCount; ++i)
            {
                if (_offscreenFrameBuffers[i].BufferWidth != (uint)_clippingManager.ClippingMaskBufferSize.X ||
                    _offscreenFrameBuffers[i].BufferHeight != (uint)_clippingManager.ClippingMaskBufferSize.Y)
                {
                    _offscreenFrameBuffers[i].CreateOffscreenSurface(
                        (int)_clippingManager.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);
                }
            }

            if (UseHighPrecisionMask)
            {
                _clippingManager.SetupMatrixForHighPrecision(Model, false);
            }
            else
            {
                _clippingManager.SetupClippingContext(Model, this, _rendererProfile.LastFBO, _rendererProfile.LastViewport);
            }
        }

        // 上記クリッピング処理内でも一度PreDrawを呼ぶので注意!!
        PreDraw();

        var drawableCount = Model.GetDrawableCount();
        var renderOrder = Model.GetDrawableRenderOrders();

        // インデックスを描画順でソート
        for (int i = 0; i < drawableCount; ++i)
        {
            var order = renderOrder[i];
            _sortedDrawableIndexList[order] = i;
        }

        if (GL.AlwaysClear)
        {
            var color = _lapp.BGColor;
            GL.ClearColor(color.R, color.G, color.B, color.A);
            GL.Clear(GL.GL_COLOR_BUFFER_BIT | GL.GL_DEPTH_BUFFER_BIT);
        }

        // 描画
        for (int i = 0; i < drawableCount; ++i)
        {
            var drawableIndex = _sortedDrawableIndexList[i];

            // Drawableが表示状態でなければ処理をパスする
            if (!Model.GetDrawableDynamicFlagIsVisible(drawableIndex))
            {
                continue;
            }

            // クリッピングマスク
            var clipContext = _clippingManager?.ClippingContextListForDraw[drawableIndex];

            if (clipContext != null && UseHighPrecisionMask) // マスクを書く必要がある
            {
                if (clipContext.IsUsing) // 書くことになっていた
                {
                    // 生成したFrameBufferと同じサイズでビューポートを設定
                    GL.Viewport(0, 0, (int)_clippingManager!.ClippingMaskBufferSize.X, (int)_clippingManager.ClippingMaskBufferSize.Y);

                    PreDraw(); // バッファをクリアする

                    // ---------- マスク描画処理 ----------
                    // マスク用RenderTextureをactiveにセット
                    GetMaskBuffer(clipContext.BufferIndex).BeginDraw(_rendererProfile.LastFBO);

                    // マスクをクリアする
                    // 1が無効（描かれない）領域、0が有効（描かれる）領域。（シェーダで Cd*Csで0に近い値をかけてマスクを作る。1をかけると何も起こらない）
                    GL.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);
                    GL.Clear(GL.GL_COLOR_BUFFER_BIT);
                }

                {
                    var clipDrawCount = clipContext.ClippingIdCount;
                    for (int index = 0; index < clipDrawCount; index++)
                    {
                        var clipDrawIndex = clipContext.ClippingIdList[index];

                        // 頂点情報が更新されておらず、信頼性がない場合は描画をパスする
                        if (!Model.GetDrawableDynamicFlagVertexPositionsDidChange(clipDrawIndex))
                        {
                            continue;
                        }

                        IsCulling = Model.GetDrawableCulling(clipDrawIndex);

                        // 今回専用の変換を適用して描く
                        // チャンネルも切り替える必要がある(A,R,G,B)
                        ClippingContextBufferForMask = clipContext;

                        DrawMeshOpenGL(Model, clipDrawIndex);
                    }
                }

                {
                    // --- 後処理 ---
                    GetMaskBuffer(clipContext.BufferIndex).EndDraw();
                    ClippingContextBufferForMask = null;
                    GL.Viewport(_rendererProfile.LastViewport[0], _rendererProfile.LastViewport[1], _rendererProfile.LastViewport[2], _rendererProfile.LastViewport[3]);

                    PreDraw(); // バッファをクリアする
                }
            }

            // クリッピングマスクをセットする
            ClippingContextBufferForDraw = clipContext;

            IsCulling = Model.GetDrawableCulling(drawableIndex);

            DrawMeshOpenGL(Model, drawableIndex);
        }
    }

    public override void Dispose()
    {
        Shader.ReleaseShaderProgram();
    }

    public int GetBindedTextureId(int textureId)
    {
        return (_textures[textureId] != 0) ? _textures[textureId] : -1;
    }
}
