// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Coloryr
// Copyright (c) 2026 Shasnow

using System.Text.Json;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Effect;
using Live2DCSharpSDK.Framework.Math;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Motion;

namespace Live2DCSharpSDK.App;

public class LAppModel : CubismUserModel
{
    /// <summary>
    /// モデルセッティング情報
    /// </summary>
    private readonly ModelSettingObj _modelSetting;
    /// <summary>
    /// モデルセッティングが置かれたディレクトリ
    /// </summary>
    private readonly string _modelHomeDir;
    /// <summary>
    /// モデルに設定されたまばたき機能用パラメータID
    /// </summary>
    private readonly List<string> _eyeBlinkIds = [];
    /// <summary>
    /// モデルに設定されたリップシンク機能用パラメータID
    /// </summary>
    private readonly List<string> _lipSyncIds = [];
    /// <summary>
    /// 読み込まれているモーションのリスト
    /// </summary>
    private readonly Dictionary<string, ACubismMotion> _motions = [];
    /// <summary>
    /// 読み込まれている表情のリスト
    /// </summary>
    private readonly Dictionary<string, ACubismMotion> _expressions = [];

    public List<string> Motions => new(_motions.Keys);
    public List<string> Expressions => new(_expressions.Keys);

    /// <summary>当前生效的表情名（null = 无表情、处于默认状态）。</summary>
    public string? CurrentExpression { get; private set; }
    public List<(string, int, float)> Parts
    {
        get
        {
            var list = new List<(string, int, float)>();
            var count = Model.GetPartCount();
            for (int a = 0; a < count; a++)
            {
                list.Add((Model.GetPartId(a),
                    a, Model.GetPartOpacity(a)));
            }
            return list;
        }
    }

    public List<TextureInfo> Textures = [];

    public List<string> Parameters => new(Model.ParameterIds);

    /// <summary>
    /// デルタ時間の積算値[秒]
    /// </summary>
    public float UserTimeSeconds { get; set; }

    public bool RandomMotion { get; set; } = true;
    public bool CustomValueUpdate { get; set; }

    /// <summary>目光跟随开关（应用层托盘勾选项）：关闭时不应用拖拽角度参数，视线回正。</summary>
    public bool EnableFaceTracking { get; set; } = true;

    public Action<LAppModel>? ValueUpdate;

    /// <summary>
    /// 手柄参数回调（应用层挂载）：在动作/表情之后、物理演算之前写入摇杆与按键参数。
    /// 与 ValueUpdate 的区别——后者被 CustomValueUpdate 与拖拽（目光跟随）二选一门控，
    /// 手柄参数需要在两种情况下都能写入，故单独开一个不受门控的钩子。
    /// </summary>
    public Action<LAppModel>? GamepadValueUpdate;

    /// <summary>
    /// パラメータID: ParamAngleX
    /// </summary>
    public string IdParamAngleX { get; set; }
    /// <summary>
    /// パラメータID: ParamAngleY
    /// </summary>
    public string IdParamAngleY { get; set; }
    /// <summary>
    /// パラメータID: ParamAngleZ
    /// </summary>
    public string IdParamAngleZ { get; set; }
    /// <summary>
    /// パラメータID: ParamBodyAngleX
    /// </summary>
    public string IdParamBodyAngleX { get; set; }
    /// <summary>
    /// パラメータID: ParamEyeBallX
    /// </summary>
    public string IdParamEyeBallX { get; set; }
    /// <summary>
    /// パラメータID: ParamEyeBallXY
    /// </summary>
    public string IdParamEyeBallY { get; set; }

    public string IdParamBreath { get; set; } = CubismFramework.CubismIdManager
        .GetId(CubismDefaultParameterId.ParamBreath);

    /// <summary>
    /// wavファイルハンドラ
    /// </summary>
    //LAppWavFileHandler _wavFileHandler;

    private readonly LAppDelegate _lapp;

    private readonly Random _random = new();

    public event Action<LAppModel, string>? Motion;

    public LAppModel(LAppDelegate lapp, string dir, string fileName)
    {
        _lapp = lapp;

        if (LAppDefine.MocConsistencyValidationEnable)
        {
            _mocConsistency = true;
        }

        IdParamAngleX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamAngleX);
        IdParamAngleY = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamAngleY);
        IdParamAngleZ = CubismFramework.CubismIdManager.
            GetId(CubismDefaultParameterId.ParamAngleZ);
        IdParamBodyAngleX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamBodyAngleX);
        IdParamEyeBallX = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamEyeBallX);
        IdParamEyeBallY = CubismFramework.CubismIdManager
            .GetId(CubismDefaultParameterId.ParamEyeBallY);

        _modelHomeDir = dir;

        CubismLog.Debug($"[Live2D App]load model setting: {fileName}");

        using var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        _modelSetting = JsonSerializer.Deserialize(stream, ModelSettingObjContext.Default.ModelSettingObj)
            ?? throw new Exception("model3.json error");

        Updating = true;
        Initialized = false;

        //Cubism Model
        var path = _modelSetting.FileReferences?.Moc;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (!File.Exists(path))
            {
                throw new Exception("model is null");
            }

            CubismLog.Debug($"[Live2D App]create model: {path}");

            LoadModel(File.ReadAllBytes(path), _mocConsistency);
        }

        //Expression
        if (_modelSetting.FileReferences?.Expressions?.Count > 0)
        {
            for (int i = 0; i < _modelSetting.FileReferences.Expressions.Count; i++)
            {
                var item = _modelSetting.FileReferences.Expressions[i];
                string name = item.Name;
                path = item.File;
                path = Path.GetFullPath(_modelHomeDir + path);
                if (!File.Exists(path))
                {
                    continue;
                }

                var motion = new CubismExpressionMotion(path);

                if (_expressions.ContainsKey(name))
                {
                    _expressions[name] = motion;
                }
                else
                {
                    _expressions.Add(name, motion);
                }
            }
        }

        //Physics
        path = _modelSetting.FileReferences?.Physics;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (File.Exists(path))
            {
                LoadPhysics(path);
            }
        }

        //Pose
        path = _modelSetting.FileReferences?.Pose;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (File.Exists(path))
            {
                LoadPose(path);
            }
        }

        //EyeBlink
        if (_modelSetting.IsExistEyeBlinkParameters())
        {
            _eyeBlink = new CubismEyeBlink(_modelSetting);
        }

        LoadBreath();

        //UserData
        path = _modelSetting.FileReferences?.UserData;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = Path.GetFullPath(_modelHomeDir + path);
            if (File.Exists(path))
            {
                LoadUserData(path);
            }
        }

        // EyeBlinkIds
        if (_eyeBlink != null)
        {
            _eyeBlinkIds.AddRange(_eyeBlink.ParameterIds);
        }

        // LipSyncIds
        if (_modelSetting.IsExistLipSyncParameters())
        {
            foreach (var item in _modelSetting.Groups)
            {
                if (item.Name == CubismModelSettingJson.LipSync)
                {
                    _lipSyncIds.AddRange(item.Ids);
                }
            }
        }

        //Layout
        Dictionary<string, float> layout = [];
        _modelSetting.GetLayoutMap(layout);
        ModelMatrix.SetupFromLayout(layout);

        Model.SaveParameters();

        if (_modelSetting.FileReferences?.Motions?.Count > 0)
        {
            foreach (var item in _modelSetting.FileReferences.Motions)
            {
                PreloadMotionGroup(item.Key);
            }
        }

        _motionManager.StopAllMotions();

        Updating = false;
        Initialized = true;
        if (Renderer != null)
        {
            DeleteRenderer();
        }
        Renderer = lapp.CreateRenderer(Model);

        SetupTextures();
    }

    public new void Dispose()
    {
        base.Dispose();

        _motions.Clear();
        _expressions.Clear();

        if (_modelSetting.FileReferences?.Motions.Count > 0)
        {
            foreach (var item in _modelSetting.FileReferences.Motions)
            {
                ReleaseMotionGroup(item.Key);
            }
        }

        foreach (var item in Textures)
        {
            _lapp.TextureManager.ReleaseTexture(item);
        }
        Textures.Clear();
    }

    public void LoadBreath()
    {
        //Breath
        _breath = new()
        {
            Parameters =
            [
                new()
                {
                    ParameterId = IdParamAngleX,
                    Offset = 0.0f,
                    Peak = 15.0f,
                    Cycle = 6.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamAngleY,
                    Offset = 0.0f,
                    Peak = 8.0f,
                    Cycle = 3.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamAngleZ,
                    Offset = 0.0f,
                    Peak = 10.0f,
                    Cycle = 5.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamBodyAngleX,
                    Offset = 0.0f,
                    Peak = 4.0f,
                    Cycle = 15.5345f,
                    Weight = 0.5f
                },
                new()
                {
                    ParameterId = IdParamBreath,
                    Offset = 0.5f,
                    Peak = 0.5f,
                    Cycle = 3.2345f,
                    Weight = 0.5f
                }
            ]
        };
    }

    /// <summary>
    /// レンダラを再構築する
    /// </summary>
    public void ReloadRenderer()
    {
        DeleteRenderer();

        CreateRenderer(_lapp.CreateRenderer(Model));

        SetupTextures();
    }

    /// <summary>
    /// モデルの更新処理。モデルのパラメータから描画状態を決定する。
    /// </summary>
    public void Update()
    {
        float deltaTimeSeconds = LAppPal.DeltaTime;
        UserTimeSeconds += deltaTimeSeconds;

        _dragManager.Update(deltaTimeSeconds);
        _dragX = _dragManager.FaceX;
        _dragY = _dragManager.FaceY;

        //-----------------------------------------------------------------
        Model.LoadParameters(); // 前回セーブされた状態をロード
        if (_motionManager.IsFinished() && RandomMotion)
        {
            // モーションの再生がない場合、待機モーションの中からランダムで再生する
            StartRandomMotion(LAppDefine.MotionGroupIdle, MotionPriority.PriorityIdle);
        }
        else
        {
            _motionManager.UpdateMotion(Model, deltaTimeSeconds); // モーションを更新
        }
        Model.SaveParameters(); // 状態を保存

        //-----------------------------------------------------------------

        // 不透明度
        Opacity = Model.GetModelOpacity();

        // 自动眨眼：待机动作是循环动作（Loop=true），每帧都会更新参数，
        // 因此不能照搬官方示例的 !motionUpdated 门控，否则永远不会眨眼。
        // 动作已通过 SetEffectIds 排除眨眼参数，这里直接叠加眨眼值即可。
        _eyeBlink?.UpdateParameters(Model, deltaTimeSeconds); // 目パチ

        _expressionManager?.UpdateMotion(Model, deltaTimeSeconds); // 表情でパラメータ更新（相対変化）

        if (CustomValueUpdate)
        {
            ValueUpdate?.Invoke(this);
        }
        else if (EnableFaceTracking)
        {
            //ドラッグによる変化
            //ドラッグによる顔の向きの調整
            Model.AddParameterValue(IdParamAngleX, _dragX * 30); // -30から30の値を加える
            Model.AddParameterValue(IdParamAngleY, _dragY * 30);
            Model.AddParameterValue(IdParamAngleZ, _dragX * _dragY * -30);

            //ドラッグによる体の向きの調整
            Model.AddParameterValue(IdParamBodyAngleX, _dragX * 10); // -10から10の値を加える

            //ドラッグによる目の向きの調整
            Model.AddParameterValue(IdParamEyeBallX, _dragX); // -1から1の値を加える
            Model.AddParameterValue(IdParamEyeBallY, _dragY);
        }
        // 目光跟随关闭：跳过拖拽角度参数。LoadParameters 每帧重置为保存值，
        // 上一帧的角度自然回正，无需手动清理拖拽残留值。

        // 呼吸など
        _breath?.UpdateParameters(Model, deltaTimeSeconds);

        // 手柄摇杆/按键：由宿主实时写入（必须在 SaveParameters 之后——Update 开头会
        // LoadParameters 把参数恢复到保存值，提前写入的下场是被覆盖）。
        // 位置选在物理演算之前：physics3.json 把摇杆、扳机、ABXY 作为物理输入源，
        // 这样推摇杆/按键会随即带出身体与手臂的晃动（模型作者的设计意图）。
        GamepadValueUpdate?.Invoke(this);

        // 物理演算の設定
        _physics?.Evaluate(Model, deltaTimeSeconds);

        // リップシンクの設定
        if (_lipSync)
        {
            // リアルタイムでリップシンクを行う場合、システムから音量を取得して0〜1の範囲で値を入力します。
            // 口型音量改由外部每帧写入，不再写死 0：
            //   第 1 个口型参数（Ids[0]，嘴开闭）写 LipSyncValue，其余（Ids[1]，水平宽窄）写 LipSyncWideValue
            for (int i = 0; i < _lipSyncIds.Count; ++i)
            {
                var value = i == 0 ? LipSyncValue : LipSyncWideValue;
                Model.AddParameterValue(_lipSyncIds[i], value, 0.8f);
            }
        }

        // ポーズの設定
        _pose?.UpdateParameters(Model, deltaTimeSeconds);

        Model.Update();
    }

    /// <summary>
    /// モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
    /// </summary>
    /// <param name="matrix">View-Projection行列</param>
    public void Draw(CubismMatrix44 matrix)
    {
        if (Model == null)
        {
            return;
        }

        matrix.MultiplyByMatrix(ModelMatrix);
        if (Renderer != null)
        {
            Renderer.SetMvpMatrix(matrix);
        }

        DoDraw();
    }

    /// <summary>
    /// 引数で指定したモーションの再生を開始する。
    /// </summary>
    /// <param name="group">モーショングループ名</param>
    /// <param name="no">グループ内の番号</param>
    /// <param name="priority">優先度</param>
    /// <param name="onFinishedMotionHandler">モーション再生終了時に呼び出されるコールバック関数。NULLの場合、呼び出されない。</param>
    /// <returns>開始したモーションの識別番号を返す。個別のモーションが終了したか否かを判定するIsFinished()の引数で使用する。開始できない時は「-1」</returns>
    public CubismMotionQueueEntry? StartMotion(string name, MotionPriority priority, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        var temp = name.Split("_");
        if (temp.Length != 2)
        {
            throw new Exception("motion name error");
        }
        return StartMotion(temp[0], int.Parse(temp[1]), priority, onFinishedMotionHandler);
    }

    public CubismMotionQueueEntry? StartMotion(string group, int no, MotionPriority priority, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        if (priority == MotionPriority.PriorityForce)
        {
            _motionManager.ReservePriority = priority;
        }
        else if (!_motionManager.ReserveMotion(priority))
        {
            CubismLog.Debug("[Live2D App]can't start motion.");
            return null;
        }

        var item = _modelSetting.FileReferences.Motions[group][no];

        //ex) idle_0
        string name = $"{group}_{no}";

        CubismMotion motion;
        if (!_motions.TryGetValue(name, out var value))
        {
            string path = item.File;
            path = Path.GetFullPath(_modelHomeDir + path);
            if (!File.Exists(path))
            {
                return null;
            }

            motion = new CubismMotion(path, onFinishedMotionHandler);
            float fadeTime = item.FadeInTime;
            if (fadeTime >= 0.0f)
            {
                motion.FadeInSeconds = fadeTime;
            }

            fadeTime = item.FadeOutTime;
            if (fadeTime >= 0.0f)
            {
                motion.FadeOutSeconds = fadeTime;
            }
            motion.SetEffectIds(_eyeBlinkIds, _lipSyncIds);
        }
        else
        {
            motion = (value as CubismMotion)!;
            motion.OnFinishedMotion = onFinishedMotionHandler;
        }

        //voice
        string voice = item.Sound;
        if (!string.IsNullOrWhiteSpace(voice))
        {
            //string path = voice;
            //path = _modelHomeDir + path;
            //_wavFileHandler.Start(path);
        }

        CubismLog.Debug($"[Live2D App]start motion: [{group}_{no}]");
        return _motionManager.StartMotionPriority(motion, priority);
    }

    /// <summary>
    /// ランダムに選ばれたモーションの再生を開始する。
    /// </summary>
    /// <param name="group">モーショングループ名</param>
    /// <param name="priority">優先度</param>
    /// <param name="onFinishedMotionHandler">モーション再生終了時に呼び出されるコールバック関数。NULLの場合、呼び出されない。</param>
    /// <returns>開始したモーションの識別番号を返す。個別のモーションが終了したか否かを判定するIsFinished()の引数で使用する。開始できない時は「-1」</returns>
    public object? StartRandomMotion(string group, MotionPriority priority, FinishedMotionCallback? onFinishedMotionHandler = null)
    {
        if (_modelSetting.FileReferences?.Motions?.ContainsKey(group) == true)
        {
            int no = _random.Next() % _modelSetting.FileReferences.Motions[group].Count;
            return StartMotion(group, no, priority, onFinishedMotionHandler);
        }

        return null;
    }

    /// <summary>
    /// 引数で指定した表情モーションをセットする
    /// </summary>
    /// <param name="expressionID">表情モーションのID</param>
    public void SetExpression(string expressionID)
    {
        ACubismMotion motion = _expressions[expressionID];
        CubismLog.Debug($"[Live2D App]expression: [{expressionID}]");

        if (motion != null)
        {
            CurrentExpression = expressionID;
            _expressionManager.StartMotionPriority(motion, MotionPriority.PriorityForce);
        }
        else
        {
            CubismLog.Debug($"[Live2D App]expression[{expressionID}] is null ");
        }
    }

    /// <summary>
    /// ランダムに選ばれた表情モーションをセットする
    /// </summary>
    public void SetRandomExpression()
    {
        if (_expressions.Count == 0)
        {
            return;
        }

        int no = _random.Next() % _expressions.Count;
        int i = 0;
        foreach (var item in _expressions)
        {
            if (i == no)
            {
                SetExpression(item.Key);
                return;
            }
            i++;
        }
    }

    /// <summary>
    /// 清除当前表情，恢复默认（无表情）状态。
    /// 表情启动时已捕获基线参数值，清空表情队列后每帧以 weight=0 写回基线，
    /// 参数即回到表情前的状态，等价于 Web SDK 的 resetExpression()。
    /// </summary>
    public void ResetExpression()
    {
        CurrentExpression = null;
        _expressionManager.StopAllMotions();
    }

    /// <summary>
    /// イベントの発火を受け取る
    /// </summary>
    /// <param name="eventValue"></param>
    protected override void MotionEventFired(string eventValue)
    {
        CubismLog.Debug($"[Live2D App]{eventValue} is fired on LAppModel!!");
        Motion?.Invoke(this, eventValue);
    }

    /// <summary>
    /// 当たり判定テスト。
    /// 指定IDの頂点リストから矩形を計算し、座標が矩形範囲内か判定する。
    /// </summary>
    /// <param name="hitAreaName">当たり判定をテストする対象のID</param>
    /// <param name="x">判定を行うX座標</param>
    /// <param name="y">判定を行うY座標</param>
    /// <returns></returns>
    public bool HitTest(string hitAreaName, float x, float y)
    {
        // 透明時は当たり判定なし。
        if (Opacity < 1)
        {
            return false;
        }
        if (_modelSetting.HitAreas?.Count > 0)
        {
            for (int i = 0; i < _modelSetting.HitAreas?.Count; i++)
            {
                if (_modelSetting.HitAreas[i].Name == hitAreaName)
                {
                    var id = CubismFramework.CubismIdManager.GetId(_modelSetting.HitAreas[i].Id);

                    return IsHit(id, x, y);
                }
            }
        }
        return false; // 存在しない場合はfalse
    }

    /// <summary>
    /// モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
    /// </summary>
    protected void DoDraw()
    {
        if (Model == null)
        {
            return;
        }

        Renderer?.DrawModel();
    }

    /// <summary>
    /// モーションデータをグループ名から一括で解放する。
    /// モーションデータの名前は内部でModelSettingから取得する。
    /// </summary>
    /// <param name="group">モーションデータのグループ名</param>
    private void ReleaseMotionGroup(string group)
    {
        var list = _modelSetting.FileReferences.Motions[group];
        for (int i = 0; i < list.Count; i++)
        {
            string voice = list[i].Sound;
            //TODO Voice
        }
    }

    /// <summary>
    /// OpenGLのテクスチャユニットにテクスチャをロードする
    /// </summary>
    private void SetupTextures()
    {
        if (_modelSetting.FileReferences?.Textures?.Count > 0)
        {
            for (int index = 0; index < _modelSetting.FileReferences.Textures.Count; index++)
            {
                var texturePath = _modelSetting.FileReferences.Textures[index];
                if (string.IsNullOrWhiteSpace(texturePath))
                    continue;
                texturePath = Path.GetFullPath(_modelHomeDir + texturePath);
                var texture = _lapp.TextureManager.CreateTextureFromPngFile(this, index, texturePath);
                Textures.Add(texture);
            }
        }
    }

    /// <summary>
    /// モーションデータをグループ名から一括でロードする。
    /// モーションデータの名前は内部でModelSettingから取得する。
    /// </summary>
    /// <param name="group">モーションデータのグループ名</param>
    private void PreloadMotionGroup(string group)
    {
        // グループに登録されているモーション数を取得
        var list = _modelSetting.FileReferences.Motions[group];

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            //ex) idle_0
            // モーションのファイル名とパスの取得
            string name = $"{group}_{i}";
            var path = Path.GetFullPath(_modelHomeDir + item.File);

            // モーションデータの読み込み
            var tmpMotion = new CubismMotion(path);

            // フェードインの時間を取得
            float fadeTime = item.FadeInTime;
            if (fadeTime >= 0.0f)
            {
                tmpMotion.FadeInSeconds = fadeTime;
            }

            // フェードアウトの時間を取得
            fadeTime = item.FadeOutTime;
            if (fadeTime >= 0.0f)
            {
                tmpMotion.FadeOutSeconds = fadeTime;
            }
            tmpMotion.SetEffectIds(_eyeBlinkIds, _lipSyncIds);

            if (_motions.ContainsKey(name))
            {
                _motions[name] = tmpMotion;
            }
            else
            {
                _motions.Add(name, tmpMotion);
            }
        }
    }
}
