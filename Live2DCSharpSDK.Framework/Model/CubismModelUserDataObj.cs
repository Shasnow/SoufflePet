namespace Live2DCSharpSDK.Framework.Model;

/// <summary>
/// userdata3.json のデシリアライズ用 DTO。値は JsonSerializer が設定するため、
/// 宣言時は null! で初期化しておく（CS8618 対策）。
/// </summary>
public record CubismModelUserDataObj
{
    public record MetaObj
    {
        public int UserDataCount { get; set; }
        public int TotalUserDataSize { get; set; }
    }

    public record UserDataObj
    {
        public string Target { get; set; } = null!;
        public string Id { get; set; } = null!;
        public string Value { get; set; } = null!;
    }

    public MetaObj Meta { get; set; } = null!;
    public List<UserDataObj> UserData { get; set; } = null!;
}
