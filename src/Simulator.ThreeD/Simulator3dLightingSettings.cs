using System.ComponentModel;
using System.Text.Json.Nodes;

namespace Simulator.ThreeD;

internal sealed class Simulator3dLightingSettings
{
    [Category("总开关")]
    [DisplayName("启用光照")]
    [Description("关闭后局内 GPU 固定管线光照会被禁用，材质将主要依赖原始顶点颜色。")]
    public bool Enabled { get; set; } = true;

    [Category("主光方向")]
    [DisplayName("方向 X")]
    [Description("主光方向的 X 分量。负值表示从场地左侧方向照入。")]
    public float KeyDirectionX { get; set; } = -0.42f;

    [Category("主光方向")]
    [DisplayName("方向 Y")]
    [Description("主光方向的高度分量。数值越大，主光越接近从上方照下。")]
    public float KeyDirectionY { get; set; } = 0.96f;

    [Category("主光方向")]
    [DisplayName("方向 Z")]
    [Description("主光方向的 Z 分量，用来控制前后方向的照射角。")]
    public float KeyDirectionZ { get; set; } = -0.26f;

    [Category("主光环境光")]
    [DisplayName("红色 R")]
    [Description("主光环境光红色分量。环境光影响暗部基础亮度。")]
    public float KeyAmbientR { get; set; } = 0.30f;

    [Category("主光环境光")]
    [DisplayName("绿色 G")]
    [Description("主光环境光绿色分量。")]
    public float KeyAmbientG { get; set; } = 0.36f;

    [Category("主光环境光")]
    [DisplayName("蓝色 B")]
    [Description("主光环境光蓝色分量。")]
    public float KeyAmbientB { get; set; } = 0.48f;

    [Category("主光漫反射")]
    [DisplayName("红色 R")]
    [Description("主光漫反射红色分量。漫反射决定受光面主要亮度。")]
    public float KeyDiffuseR { get; set; } = 0.86f;

    [Category("主光漫反射")]
    [DisplayName("绿色 G")]
    [Description("主光漫反射绿色分量。")]
    public float KeyDiffuseG { get; set; } = 1.02f;

    [Category("主光漫反射")]
    [DisplayName("蓝色 B")]
    [Description("主光漫反射蓝色分量。")]
    public float KeyDiffuseB { get; set; } = 1.20f;

    [Category("主光高光")]
    [DisplayName("红色 R")]
    [Description("主光高光红色分量。高光影响金属和斜面上的反光。")]
    public float KeySpecularR { get; set; } = 0.24f;

    [Category("主光高光")]
    [DisplayName("绿色 G")]
    [Description("主光高光绿色分量。")]
    public float KeySpecularG { get; set; } = 0.34f;

    [Category("主光高光")]
    [DisplayName("蓝色 B")]
    [Description("主光高光蓝色分量。")]
    public float KeySpecularB { get; set; } = 0.52f;

    [Category("补光方向")]
    [DisplayName("方向 X")]
    [Description("补光方向的 X 分量。补光用于抬亮阴影侧。")]
    public float FillDirectionX { get; set; } = 0.62f;

    [Category("补光方向")]
    [DisplayName("方向 Y")]
    [Description("补光方向的高度分量。")]
    public float FillDirectionY { get; set; } = 0.34f;

    [Category("补光方向")]
    [DisplayName("方向 Z")]
    [Description("补光方向的 Z 分量。")]
    public float FillDirectionZ { get; set; } = 0.64f;

    [Category("补光环境光")]
    [DisplayName("红色 R")]
    [Description("补光环境光红色分量。")]
    public float FillAmbientR { get; set; } = 0.018f;

    [Category("补光环境光")]
    [DisplayName("绿色 G")]
    [Description("补光环境光绿色分量。")]
    public float FillAmbientG { get; set; } = 0.030f;

    [Category("补光环境光")]
    [DisplayName("蓝色 B")]
    [Description("补光环境光蓝色分量。")]
    public float FillAmbientB { get; set; } = 0.052f;

    [Category("补光漫反射")]
    [DisplayName("红色 R")]
    [Description("补光漫反射红色分量。")]
    public float FillDiffuseR { get; set; } = 0.12f;

    [Category("补光漫反射")]
    [DisplayName("绿色 G")]
    [Description("补光漫反射绿色分量。")]
    public float FillDiffuseG { get; set; } = 0.22f;

    [Category("补光漫反射")]
    [DisplayName("蓝色 B")]
    [Description("补光漫反射蓝色分量。")]
    public float FillDiffuseB { get; set; } = 0.38f;

    [Category("补光高光")]
    [DisplayName("红色 R")]
    [Description("补光高光红色分量。")]
    public float FillSpecularR { get; set; } = 0.05f;

    [Category("补光高光")]
    [DisplayName("绿色 G")]
    [Description("补光高光绿色分量。")]
    public float FillSpecularG { get; set; } = 0.09f;

    [Category("补光高光")]
    [DisplayName("蓝色 B")]
    [Description("补光高光蓝色分量。")]
    public float FillSpecularB { get; set; } = 0.18f;

    [Category("材质高光")]
    [DisplayName("红色 R")]
    [Description("材质高光红色分量。它会和灯光高光共同决定反光颜色。")]
    public float MaterialSpecularR { get; set; } = 0.18f;

    [Category("材质高光")]
    [DisplayName("绿色 G")]
    [Description("材质高光绿色分量。")]
    public float MaterialSpecularG { get; set; } = 0.26f;

    [Category("材质高光")]
    [DisplayName("蓝色 B")]
    [Description("材质高光蓝色分量。")]
    public float MaterialSpecularB { get; set; } = 0.42f;

    [Category("材质高光")]
    [DisplayName("锐度")]
    [Description("高光锐度，范围 0-128。数值越大，高光越小越硬。")]
    public float MaterialShininess { get; set; } = 28.0f;

    public static Simulator3dLightingSettings CreateDefault()
        => new();

    public Simulator3dLightingSettings Clone()
    {
        return (Simulator3dLightingSettings)MemberwiseClone();
    }

    public Simulator3dLightingSettings Normalized()
    {
        Simulator3dLightingSettings copy = Clone();
        copy.KeyDirectionX = ClampFinite(copy.KeyDirectionX, -4f, 4f, -0.42f);
        copy.KeyDirectionY = ClampFinite(copy.KeyDirectionY, -4f, 4f, 0.96f);
        copy.KeyDirectionZ = ClampFinite(copy.KeyDirectionZ, -4f, 4f, -0.26f);
        copy.FillDirectionX = ClampFinite(copy.FillDirectionX, -4f, 4f, 0.62f);
        copy.FillDirectionY = ClampFinite(copy.FillDirectionY, -4f, 4f, 0.34f);
        copy.FillDirectionZ = ClampFinite(copy.FillDirectionZ, -4f, 4f, 0.64f);

        copy.KeyAmbientR = ClampColor(copy.KeyAmbientR);
        copy.KeyAmbientG = ClampColor(copy.KeyAmbientG);
        copy.KeyAmbientB = ClampColor(copy.KeyAmbientB);
        copy.KeyDiffuseR = ClampColor(copy.KeyDiffuseR);
        copy.KeyDiffuseG = ClampColor(copy.KeyDiffuseG);
        copy.KeyDiffuseB = ClampColor(copy.KeyDiffuseB);
        copy.KeySpecularR = ClampColor(copy.KeySpecularR);
        copy.KeySpecularG = ClampColor(copy.KeySpecularG);
        copy.KeySpecularB = ClampColor(copy.KeySpecularB);

        copy.FillAmbientR = ClampColor(copy.FillAmbientR);
        copy.FillAmbientG = ClampColor(copy.FillAmbientG);
        copy.FillAmbientB = ClampColor(copy.FillAmbientB);
        copy.FillDiffuseR = ClampColor(copy.FillDiffuseR);
        copy.FillDiffuseG = ClampColor(copy.FillDiffuseG);
        copy.FillDiffuseB = ClampColor(copy.FillDiffuseB);
        copy.FillSpecularR = ClampColor(copy.FillSpecularR);
        copy.FillSpecularG = ClampColor(copy.FillSpecularG);
        copy.FillSpecularB = ClampColor(copy.FillSpecularB);

        copy.MaterialSpecularR = ClampColor(copy.MaterialSpecularR);
        copy.MaterialSpecularG = ClampColor(copy.MaterialSpecularG);
        copy.MaterialSpecularB = ClampColor(copy.MaterialSpecularB);
        copy.MaterialShininess = ClampFinite(copy.MaterialShininess, 0f, 128f, 28f);
        return copy;
    }

    public Simulator3dLightingSettings WithCoolPalette()
    {
        Simulator3dLightingSettings copy = Clone();
        copy.KeyAmbientR = 0.30f;
        copy.KeyAmbientG = 0.36f;
        copy.KeyAmbientB = 0.48f;
        copy.KeyDiffuseR = 0.86f;
        copy.KeyDiffuseG = 1.02f;
        copy.KeyDiffuseB = 1.20f;
        copy.KeySpecularR = 0.24f;
        copy.KeySpecularG = 0.34f;
        copy.KeySpecularB = 0.52f;
        copy.FillAmbientR = 0.018f;
        copy.FillAmbientG = 0.030f;
        copy.FillAmbientB = 0.052f;
        copy.FillDiffuseR = 0.12f;
        copy.FillDiffuseG = 0.22f;
        copy.FillDiffuseB = 0.38f;
        copy.FillSpecularR = 0.05f;
        copy.FillSpecularG = 0.09f;
        copy.FillSpecularB = 0.18f;
        copy.MaterialSpecularR = 0.18f;
        copy.MaterialSpecularG = 0.26f;
        copy.MaterialSpecularB = 0.42f;
        return copy.Normalized();
    }

    public static Simulator3dLightingSettings Load(JsonObject simulator)
    {
        Simulator3dLightingSettings settings = CreateDefault();
        if (simulator["sim3d_lighting"] is not JsonObject lighting)
        {
            return settings.WithCoolPalette();
        }

        settings.Enabled = ReadBool(lighting["enabled"], settings.Enabled);
        float keyDirectionX = settings.KeyDirectionX;
        float keyDirectionY = settings.KeyDirectionY;
        float keyDirectionZ = settings.KeyDirectionZ;
        ReadVector(lighting["key_direction"], out keyDirectionX, out keyDirectionY, out keyDirectionZ, settings.KeyDirectionX, settings.KeyDirectionY, settings.KeyDirectionZ);
        settings.KeyDirectionX = keyDirectionX;
        settings.KeyDirectionY = keyDirectionY;
        settings.KeyDirectionZ = keyDirectionZ;

        float keyAmbientR = settings.KeyAmbientR;
        float keyAmbientG = settings.KeyAmbientG;
        float keyAmbientB = settings.KeyAmbientB;
        ReadColor(lighting["key_ambient"], out keyAmbientR, out keyAmbientG, out keyAmbientB, settings.KeyAmbientR, settings.KeyAmbientG, settings.KeyAmbientB);
        settings.KeyAmbientR = keyAmbientR;
        settings.KeyAmbientG = keyAmbientG;
        settings.KeyAmbientB = keyAmbientB;

        float keyDiffuseR = settings.KeyDiffuseR;
        float keyDiffuseG = settings.KeyDiffuseG;
        float keyDiffuseB = settings.KeyDiffuseB;
        ReadColor(lighting["key_diffuse"], out keyDiffuseR, out keyDiffuseG, out keyDiffuseB, settings.KeyDiffuseR, settings.KeyDiffuseG, settings.KeyDiffuseB);
        settings.KeyDiffuseR = keyDiffuseR;
        settings.KeyDiffuseG = keyDiffuseG;
        settings.KeyDiffuseB = keyDiffuseB;

        float keySpecularR = settings.KeySpecularR;
        float keySpecularG = settings.KeySpecularG;
        float keySpecularB = settings.KeySpecularB;
        ReadColor(lighting["key_specular"], out keySpecularR, out keySpecularG, out keySpecularB, settings.KeySpecularR, settings.KeySpecularG, settings.KeySpecularB);
        settings.KeySpecularR = keySpecularR;
        settings.KeySpecularG = keySpecularG;
        settings.KeySpecularB = keySpecularB;

        float fillDirectionX = settings.FillDirectionX;
        float fillDirectionY = settings.FillDirectionY;
        float fillDirectionZ = settings.FillDirectionZ;
        ReadVector(lighting["fill_direction"], out fillDirectionX, out fillDirectionY, out fillDirectionZ, settings.FillDirectionX, settings.FillDirectionY, settings.FillDirectionZ);
        settings.FillDirectionX = fillDirectionX;
        settings.FillDirectionY = fillDirectionY;
        settings.FillDirectionZ = fillDirectionZ;

        float fillAmbientR = settings.FillAmbientR;
        float fillAmbientG = settings.FillAmbientG;
        float fillAmbientB = settings.FillAmbientB;
        ReadColor(lighting["fill_ambient"], out fillAmbientR, out fillAmbientG, out fillAmbientB, settings.FillAmbientR, settings.FillAmbientG, settings.FillAmbientB);
        settings.FillAmbientR = fillAmbientR;
        settings.FillAmbientG = fillAmbientG;
        settings.FillAmbientB = fillAmbientB;

        float fillDiffuseR = settings.FillDiffuseR;
        float fillDiffuseG = settings.FillDiffuseG;
        float fillDiffuseB = settings.FillDiffuseB;
        ReadColor(lighting["fill_diffuse"], out fillDiffuseR, out fillDiffuseG, out fillDiffuseB, settings.FillDiffuseR, settings.FillDiffuseG, settings.FillDiffuseB);
        settings.FillDiffuseR = fillDiffuseR;
        settings.FillDiffuseG = fillDiffuseG;
        settings.FillDiffuseB = fillDiffuseB;

        float fillSpecularR = settings.FillSpecularR;
        float fillSpecularG = settings.FillSpecularG;
        float fillSpecularB = settings.FillSpecularB;
        ReadColor(lighting["fill_specular"], out fillSpecularR, out fillSpecularG, out fillSpecularB, settings.FillSpecularR, settings.FillSpecularG, settings.FillSpecularB);
        settings.FillSpecularR = fillSpecularR;
        settings.FillSpecularG = fillSpecularG;
        settings.FillSpecularB = fillSpecularB;

        float materialSpecularR = settings.MaterialSpecularR;
        float materialSpecularG = settings.MaterialSpecularG;
        float materialSpecularB = settings.MaterialSpecularB;
        ReadColor(lighting["material_specular"], out materialSpecularR, out materialSpecularG, out materialSpecularB, settings.MaterialSpecularR, settings.MaterialSpecularG, settings.MaterialSpecularB);
        settings.MaterialSpecularR = materialSpecularR;
        settings.MaterialSpecularG = materialSpecularG;
        settings.MaterialSpecularB = materialSpecularB;

        settings.MaterialShininess = ReadFloat(lighting["material_shininess"], settings.MaterialShininess);
        return settings.WithCoolPalette();
    }

    public void Save(JsonObject simulator)
    {
        Simulator3dLightingSettings settings = Normalized();
        var lighting = new JsonObject
        {
            ["enabled"] = settings.Enabled,
            ["key_direction"] = Vector(settings.KeyDirectionX, settings.KeyDirectionY, settings.KeyDirectionZ),
            ["key_ambient"] = Vector(settings.KeyAmbientR, settings.KeyAmbientG, settings.KeyAmbientB),
            ["key_diffuse"] = Vector(settings.KeyDiffuseR, settings.KeyDiffuseG, settings.KeyDiffuseB),
            ["key_specular"] = Vector(settings.KeySpecularR, settings.KeySpecularG, settings.KeySpecularB),
            ["fill_direction"] = Vector(settings.FillDirectionX, settings.FillDirectionY, settings.FillDirectionZ),
            ["fill_ambient"] = Vector(settings.FillAmbientR, settings.FillAmbientG, settings.FillAmbientB),
            ["fill_diffuse"] = Vector(settings.FillDiffuseR, settings.FillDiffuseG, settings.FillDiffuseB),
            ["fill_specular"] = Vector(settings.FillSpecularR, settings.FillSpecularG, settings.FillSpecularB),
            ["material_specular"] = Vector(settings.MaterialSpecularR, settings.MaterialSpecularG, settings.MaterialSpecularB),
            ["material_shininess"] = settings.MaterialShininess,
        };
        simulator["sim3d_lighting"] = lighting;
    }

    private static JsonArray Vector(float x, float y, float z)
        => new(x, y, z);

    private static void ReadVector(JsonNode? node, out float x, out float y, out float z, float fallbackX, float fallbackY, float fallbackZ)
    {
        x = fallbackX;
        y = fallbackY;
        z = fallbackZ;
        if (node is not JsonArray array || array.Count < 3)
        {
            return;
        }

        x = ReadFloat(array[0], fallbackX);
        y = ReadFloat(array[1], fallbackY);
        z = ReadFloat(array[2], fallbackZ);
    }

    private static void ReadColor(JsonNode? node, out float r, out float g, out float b, float fallbackR, float fallbackG, float fallbackB)
        => ReadVector(node, out r, out g, out b, fallbackR, fallbackG, fallbackB);

    private static bool ReadBool(JsonNode? node, bool fallback)
    {
        if (node is JsonValue value && value.TryGetValue(out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static float ReadFloat(JsonNode? node, float fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out float parsedFloat))
            {
                return parsedFloat;
            }

            if (value.TryGetValue(out double parsedDouble))
            {
                return (float)parsedDouble;
            }

            if (value.TryGetValue(out int parsedInt))
            {
                return parsedInt;
            }

            if (value.TryGetValue(out string? text) && float.TryParse(text, out parsedFloat))
            {
                return parsedFloat;
            }
        }

        return fallback;
    }

    private static float ClampColor(float value)
        => ClampFinite(value, 0f, 2f, 0f);

    private static float ClampFinite(float value, float min, float max, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
