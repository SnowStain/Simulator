using System.Text.Json;
using System.Text.Json.Nodes;

namespace Simulator.Core.Gameplay;

public sealed class DecisionDeploymentConfig
{
    private static readonly string[] OrderedRoles =
    {
        "hero",
        "engineer",
        "infantry",
        "sentry",
    };

    public IDictionary<string, string> RoleModes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hero"] = "aggressive",
            ["engineer"] = "support",
            ["infantry"] = "aggressive",
            ["sentry"] = "hold",
        };

    public static DecisionDeploymentConfig CreateDefault()
    {
        return new DecisionDeploymentConfig();
    }

    public static DecisionDeploymentConfig LoadFromConfig(JsonObject root)
    {
        var config = CreateDefault();
        JsonObject? ai = root["ai"] as JsonObject;
        JsonObject? deployment = ai?["decision_deployment"] as JsonObject;
        JsonObject? roleNode = deployment?["roles"] as JsonObject;

        if (roleNode is null)
        {
            return config;
        }

        foreach (string role in OrderedRoles)
        {
            string value = roleNode[role]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                config.RoleModes[role] = NormalizeMode(value);
            }
        }

        return config;
    }

    public void WriteToConfig(JsonObject root)
    {
        JsonObject ai = EnsureObject(root, "ai");
        JsonObject deployment = EnsureObject(ai, "decision_deployment");
        JsonObject roles = EnsureObject(deployment, "roles");

        foreach (string role in OrderedRoles)
        {
            roles[role] = ResolveMode(role);
        }
    }

    public string ResolveMode(string roleKey)
    {
        string role = NormalizeRoleKey(roleKey);
        if (RoleModes.TryGetValue(role, out string? mode))
        {
            return NormalizeMode(mode);
        }

        return RoleModes.TryGetValue("infantry", out string? infantryMode)
            ? NormalizeMode(infantryMode)
            : "aggressive";
    }

    public bool SetRoleMode(string roleKey, string mode)
    {
        string role = NormalizeRoleKey(roleKey);
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        RoleModes[role] = NormalizeMode(mode);
        return true;
    }

    public void ApplyPreset(string preset)
    {
        string normalized = (preset ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "aggressive":
                RoleModes["hero"] = "aggressive";
                RoleModes["engineer"] = "aggressive";
                RoleModes["infantry"] = "aggressive";
                RoleModes["sentry"] = "flank";
                break;
            case "defensive":
                RoleModes["hero"] = "hold";
                RoleModes["engineer"] = "support";
                RoleModes["infantry"] = "hold";
                RoleModes["sentry"] = "hold";
                break;
            default:
                RoleModes["hero"] = "aggressive";
                RoleModes["engineer"] = "support";
                RoleModes["infantry"] = "aggressive";
                RoleModes["sentry"] = "hold";
                break;
        }
    }

    public JsonObject ToJson()
    {
        var roles = new JsonObject();
        foreach (string role in OrderedRoles)
        {
            roles[role] = ResolveMode(role);
        }

        return new JsonObject
        {
            ["roles"] = roles,
        };
    }

    public static string NormalizeRoleKey(string? roleKey)
    {
        string role = (roleKey ?? string.Empty).Trim().ToLowerInvariant();
        return role switch
        {
            "英雄" => "hero",
            "工程" => "engineer",
            "步兵" => "infantry",
            "哨兵" => "sentry",
            "hero" => "hero",
            "engineer" => "engineer",
            "infantry" => "infantry",
            "sentry" => "sentry",
            _ => "infantry",
        };
    }

    public static string NormalizeMode(string? mode)
    {
        string value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "push" => "aggressive",
            "attack" => "aggressive",
            "aggressive" => "aggressive",
            "defensive" => "hold",
            "defend" => "hold",
            "hold" => "hold",
            "support" => "support",
            "assist" => "support",
            "flank" => "flank",
            _ => "aggressive",
        };
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is JsonObject objectNode)
        {
            return objectNode;
        }

        var created = new JsonObject();
        root[key] = created;
        return created;
    }
}
