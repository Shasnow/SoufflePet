using System.Numerics;

namespace Live2DCSharpSDK.Framework.Physics;

/// <summary>
/// physics3.json 反序列化 DTO。字段值由 JsonSerializer 填充，
/// 故声明时用 null! 初始化（消除 CS8618 警告）。
/// </summary>
public record CubismPhysicsObj
{
    public record MetaObj
    {
        public record EffectiveForce
        {
            public Vector2 Gravity { get; set; }
            public Vector2 Wind { get; set; }
        }

        public EffectiveForce EffectiveForces { get; set; } = null!;
        public float Fps { get; set; }
        public int PhysicsSettingCount { get; set; }
        public int TotalInputCount { get; set; }
        public int TotalOutputCount { get; set; }
        public int VertexCount { get; set; }
    }
    public record PhysicsSetting
    {
        public record NormalizationObj
        {
            public record PositionObj
            {
                public float Minimum { get; set; }
                public float Maximum { get; set; }
                public float Default { get; set; }
            }
            public PositionObj Position { get; set; } = null!;
            public PositionObj Angle { get; set; } = null!;
        }
        public record InputObj
        {
            public record SourceObj
            {
                public string Id { get; set; } = null!;
            }
            public float Weight { get; set; }
            public bool Reflect { get; set; }
            public string Type { get; set; } = null!;
            public SourceObj Source { get; set; } = null!;
        }
        public record OutputObj
        {
            public record DestinationObj
            {
                public string Id { get; set; } = null!;
            }
            public int VertexIndex { get; set; }
            public float Scale { get; set; }
            public float Weight { get; set; }
            public DestinationObj Destination { get; set; } = null!;
            public string Type { get; set; } = null!;
            public bool Reflect { get; set; }
        }
        public record Vertice
        {
            public float Mobility { get; set; }
            public float Delay { get; set; }
            public float Acceleration { get; set; }
            public float Radius { get; set; }
            public Vector2 Position { get; set; }
        }
        public NormalizationObj Normalization { get; set; } = null!;
        public List<InputObj> Input { get; set; } = null!;
        public List<OutputObj> Output { get; set; } = null!;
        public List<Vertice> Vertices { get; set; } = null!;
    }
    public MetaObj Meta { get; set; } = null!;
    public List<PhysicsSetting> PhysicsSettings { get; set; } = null!;
}
