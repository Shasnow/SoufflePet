using System.ComponentModel;

namespace Live2DCSharpSDK.Framework;

/// <summary>
/// model3.json のデシリアライズ用 DTO。値は JsonSerializer が設定するため、
/// 宣言時は null! で初期化しておく（CS8618 対策）。
/// </summary>
public record ModelSettingObj
{
    public record FileReference
    {
        public record Expression
        {
            public string Name { get; set; } = null!;
            public string File { get; set; } = null!;
        }
        public record Motion
        {
            public string File { get; set; } = null!;
            public string Sound { get; set; } = null!;
            [DefaultValue(-1f)]
            public float FadeInTime { get; set; }
            [DefaultValue(-1f)]
            public float FadeOutTime { get; set; }
        }
        public string Moc { get; set; } = null!;
        public List<string> Textures { get; set; } = null!;
        public string Physics { get; set; } = null!;
        public string Pose { get; set; } = null!;
        public string DisplayInfo { get; set; } = null!;
        public List<Expression> Expressions { get; set; } = null!;
        public Dictionary<string, List<Motion>> Motions { get; set; } = null!;
        public string UserData { get; set; } = null!;
    }

    public record HitArea
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    public record Parameter
    {
        public string Name { get; set; } = null!;
        public List<string> Ids { get; set; } = null!;
    }

    public FileReference FileReferences { get; set; } = null!;
    public List<HitArea> HitAreas { get; set; } = null!;
    public Dictionary<string, float> Layout { get; set; } = null!;
    public List<Parameter> Groups { get; set; } = null!;
}
