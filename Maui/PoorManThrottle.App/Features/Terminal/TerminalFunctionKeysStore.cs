using System.Text.Json;
using Microsoft.Maui.Storage;

namespace PoorManThrottle.App.Features.Terminal;

public static class TerminalFunctionKeysStore
{
    // Bump this key if you ever change the JSON schema
    private const string PrefKey = "TerminalFunctionKeys.v1";

    public const int KeyCount = 10;

    public sealed record FunctionKeyConfig(string? Name, string? Command);

    public static IReadOnlyList<FunctionKeyConfig> Load()
    {
        try
        {
            var json = Preferences.Get(PrefKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return Default();

            var list = JsonSerializer.Deserialize<List<FunctionKeyConfig>>(json);
            if (list is null || list.Count != KeyCount)
                return Default();

            return list;
        }
        catch
        {
            return Default();
        }
    }

    public static void Save(IReadOnlyList<FunctionKeyConfig> configs)
    {
        if (configs is null || configs.Count != KeyCount)
            throw new ArgumentException($"Expected exactly {KeyCount} keys.", nameof(configs));

        var json = JsonSerializer.Serialize(configs);
        Preferences.Set(PrefKey, json);
    }

    public static IReadOnlyList<FunctionKeyConfig> Default()
    {
        var list = new List<FunctionKeyConfig>(KeyCount);
        for (int i = 1; i <= KeyCount; i++)
            list.Add(new FunctionKeyConfig(Name: $"#{i}", Command: null));

        return list;
    }
}