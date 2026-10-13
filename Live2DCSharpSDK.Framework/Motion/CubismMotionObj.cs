namespace Live2DCSharpSDK.Framework.Motion;

/// <summary>
/// motion3.json のデシリアライズ用 DTO。値は JsonSerializer が設定するため、
/// 宣言時は null! で初期化しておく（CS8618 対策）。
/// </summary>
public record CubismMotionObj
{
    public record MetaObj
    {
        public float Duration { get; set; }
        public bool Loop { get; set; }
        public bool AreBeziersRestricted { get; set; }
        public int CurveCount { get; set; }
        public float Fps { get; set; }
        public int TotalSegmentCount { get; set; }
        public int TotalPointCount { get; set; }
        public float? FadeInTime { get; set; }
        public float? FadeOutTime { get; set; }
        public int UserDataCount { get; set; }
        public int TotalUserDataSize { get; set; }
    }
    public record Curve
    {
        public float? FadeInTime { get; set; }
        public float? FadeOutTime { get; set; }
        public List<float> Segments { get; set; } = null!;
        public string Target { get; set; } = null!;
        public string Id { get; set; } = null!;
    }
    public record UserDataObj
    {
        public float Time { get; set; }
        public string Value { get; set; } = null!;
    }
    public MetaObj Meta { get; set; } = null!;
    public List<Curve> Curves { get; set; } = null!;
    public List<UserDataObj> UserData { get; set; } = null!;
}
