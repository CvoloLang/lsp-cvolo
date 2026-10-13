using Cvolo.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The semantic-token resource budget is a setting the editor owns: it is read from a
/// workspace/didChangeConfiguration payload, validated conservatively, and updated at runtime
/// without restarting the session (§16, §64, §65).
/// </summary>
public class SemanticTokensSettingsTests
{
    [Fact]
    public void TheDefaultBudget_IsHalfAMillionCharacters()
    {
        Assert.Equal(500_000, SemanticTokensSettings.DefaultMaxInteractiveTextLength);
        Assert.Equal(500_000, SemanticTokensSettings.Default.MaxInteractiveTextLength);
    }

    [Fact]
    public void TheFullyQualifiedKey_IsRead()
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = 1234 },
            SemanticTokensSettings.Default);

        Assert.Equal(1234, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void TheNestedSection_IsRead()
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject
            {
                ["cvolo"] = new JObject
                {
                    ["semanticTokens"] = new JObject { ["maxInteractiveLength"] = 4096 },
                },
            },
            SemanticTokensSettings.Default);

        Assert.Equal(4096, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void KeyNames_AreMatchedWithoutRegardToCase()
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject
            {
                ["CVOLO"] = new JObject
                {
                    ["SemanticTokens"] = new JObject { ["MaxInteractiveLength"] = 2048 },
                },
            },
            SemanticTokensSettings.Default);

        Assert.Equal(2048, settings.MaxInteractiveTextLength);
    }

    [Theory]
    [InlineData("500000")]
    [InlineData(1.5)]
    [InlineData(true)]
    public void AValueThatIsNotAnInteger_IsTreatedAsAbsent(object value)
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = JToken.FromObject(value) },
            SemanticTokensSettings.Default);

        Assert.Equal(SemanticTokensSettings.DefaultMaxInteractiveTextLength, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void ANullValue_IsTreatedAsAbsent()
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = JValue.CreateNull() },
            SemanticTokensSettings.Default);

        Assert.Equal(SemanticTokensSettings.Default, settings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBudget_IsTreatedAsAbsent(int value)
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = value },
            SemanticTokensSettings.Default);

        Assert.Equal(SemanticTokensSettings.DefaultMaxInteractiveTextLength, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void AnOutOfRangeNumber_IsTreatedAsAbsent()
    {
        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = 9_999_999_999L },
            SemanticTokensSettings.Default);

        Assert.Equal(SemanticTokensSettings.DefaultMaxInteractiveTextLength, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void NoSettingsPayload_KeepsTheCurrentBudget()
    {
        Assert.Equal(
            SemanticTokensSettings.Default,
            SemanticTokensSettings.FromConfiguration(null, SemanticTokensSettings.Default));
    }

    [Fact]
    public void AnUnrelatedPayload_KeepsTheCurrentBudget()
    {
        SemanticTokensSettings current = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = 900 },
            SemanticTokensSettings.Default);

        SemanticTokensSettings settings = SemanticTokensSettings.FromConfiguration(
            new JObject { ["cvolo.codeLens.references"] = false, ["editor.fontSize"] = 14 },
            current);

        Assert.Equal(900, settings.MaxInteractiveTextLength);
    }

    [Fact]
    public void SessionConfiguration_UpdatesTheBudgetAtRuntime_AndReportsAChange()
    {
        var state = new SessionState();

        Assert.True(state.TryApplyConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = 777 }));
        Assert.Equal(777, state.SemanticTokens.MaxInteractiveTextLength);

        // Repeating the same value is not a change, so no refresh is due.
        Assert.False(state.TryApplyConfiguration(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = 777 }));
    }

    [Fact]
    public void SessionConfiguration_UpdatesBothSettingsTogether()
    {
        var state = new SessionState();

        Assert.True(state.TryApplyConfiguration(new JObject
        {
            ["cvolo.codeLens.references"] = false,
            ["cvolo.semanticTokens.maxInteractiveLength"] = 321,
        }));

        Assert.False(state.EditorIntelligence.CodeLens.References);
        Assert.Equal(321, state.SemanticTokens.MaxInteractiveTextLength);
    }

    [Fact]
    public void SessionConfiguration_WithoutAPayload_ReportsNoChange()
    {
        var state = new SessionState();

        Assert.False(state.TryApplyConfiguration(null));
        Assert.Equal(SemanticTokensSettings.Default, state.SemanticTokens);
    }
}
