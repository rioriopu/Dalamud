using System.Reflection;

using Xunit;

namespace Dalamud.Test;

public class ConfigurationRecoveryTests
{
    [Theory]
    [InlineData("{\"$type\":\"Dalamud.Interface.FontIdentifier.SingleFontSpec, Dalamud\",\"FontId\":{\"$type\":\"MissingPlugin.FontId, MissingPlugin\"}}")]
    [InlineData("{\"$type\":\"Dalamud.Interface.FontIdentifier.SingleFontSpec, Dalamud\",\"FontId\":null}")]
    [InlineData("{\"$type\":\"MissingPlugin.FontSpec, MissingPlugin\"}")]
    public void InvalidFontFallsBackWithoutDiscardingOtherSettings(string font)
    {
        var (config, degraded) = Deserialize("{\"LanguageOverride\":\"ja\",\"DutyFinderChatMessage\":false,\"DefaultFontSpec\":" + font + "}");
        Assert.True(degraded);
        Assert.Null(config.GetType().GetProperty("DefaultFontSpec")!.GetValue(config));
        Assert.Equal("ja", config.GetType().GetProperty("LanguageOverride")!.GetValue(config));
        Assert.Equal(false, config.GetType().GetProperty("DutyFinderChatMessage")!.GetValue(config));
    }

    [Fact]
    public void NormalDefaultsAreNotTreatedAsDamaged()
    {
        var (config, degraded) = Deserialize("{\"LanguageOverride\":\"ja\",\"DefaultFontSpec\":null}");
        Assert.False(degraded);
        Assert.Null(config.GetType().GetProperty("DefaultFontSpec")!.GetValue(config));
    }

    [Fact]
    public void MalformedJsonIsNotSilentlyAccepted()
    {
        Assert.Throws<TargetInvocationException>(() => Deserialize("{\"LanguageOverride\":"));
    }

    private static (object Config, bool Degraded) Deserialize(string json)
    {
        var type = typeof(Localization).Assembly.GetType("Dalamud.Configuration.Internal.DalamudConfiguration", true)!;
        var method = type.GetMethod("DeserializeLeniently", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = [json, false];
        var config = method.Invoke(null, args)!;
        return (config, (bool)args[1]!);
    }
}
