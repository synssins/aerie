using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kb.Plugins.Actions;

namespace Kb.Tests.Unit;

/// <summary>Rules every action record of the contract assembly keeps, checked by reflection so a new action cannot miss them.</summary>
public sealed class ContractTests
{
    private const string Marker = "S3CRET-MARKER";

    private static readonly List<Type> Actions = [.. typeof(IAction<>).Assembly.GetTypes().Where(t => t.GetCustomAttribute<ActionAttribute>() is not null)];

    /// <summary>An instance with every text member that is marked [JsonIgnore] (a secret) set to the marker, and the other text members to "x".</summary>
    private static object Filled(Type action)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(action);
        foreach (var p in action.GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
            p.SetValue(instance, p.GetCustomAttribute<JsonIgnoreAttribute>() is null ? "x" : Marker);
        return instance;
    }

    [Fact]
    public void EveryAction_CanBeSerialized_WithoutItsSecrets()
    {
        Assert.True(Actions.Count > 60, "the contract actions were not read");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var action in Actions)
        {
            // System.Text.Json refuses a type whose member is both required and ignored; such an action could never be logged.
            var json = JsonSerializer.Serialize(Filled(action), action, options);
            Assert.DoesNotContain(Marker, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnActionsText_DoesNotShowItsSecrets()
    {
        // A record prints every member in ToString(); one log line of the action would then hold the password.
        foreach (var action in Actions)
            Assert.DoesNotContain(Marker, Filled(action).ToString(), StringComparison.Ordinal);
    }
}
