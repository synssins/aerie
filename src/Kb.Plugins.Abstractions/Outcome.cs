namespace Kb.Plugins;

/// <summary>Why something was not done: an HTTP status, and a message, field errors or a body to show.</summary>
public sealed record Problem(int Status, string? Message = null, IReadOnlyDictionary<string, string[]>? Fields = null, object? Body = null)
{
    public static readonly Problem NotFound = new(404);
    public static readonly Problem Forbidden = new(403);
    public static readonly Problem Unauthorized = new(401);

    public static Problem Conflict(string? message = null, object? body = null) => new(409, message, Body: body);
    public static Problem Invalid(string field, string message) => new(400, Fields: new Dictionary<string, string[]> { [field] = [message] });
    /// <summary>A plugin hook said no.</summary>
    public static Problem Refused(string message) => new(422, message);
    /// <summary>An enforcing plugin is disabled by the core.</summary>
    public static Problem Locked(string message) => new(423, message);
}

/// <summary>A result or the problem that prevented it.</summary>
public readonly struct Outcome<T> : IEquatable<Outcome<T>>
{
    private Outcome(T? value, Problem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public T? Value { get; }
    public Problem? Problem { get; }
    public bool Ok => Problem is null;

    public static implicit operator Outcome<T>(T value) => new(value, null);
    public static implicit operator Outcome<T>(Problem problem) => new(default, problem ?? throw new ArgumentNullException(nameof(problem)));

    public bool Equals(Outcome<T> other) => EqualityComparer<T?>.Default.Equals(Value, other.Value) && Equals(Problem, other.Problem);
    public override bool Equals(object? obj) => obj is Outcome<T> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Value, Problem);
    public static bool operator ==(Outcome<T> left, Outcome<T> right) => left.Equals(right);
    public static bool operator !=(Outcome<T> left, Outcome<T> right) => !left.Equals(right);
}

/// <summary>The result of an action that returns nothing (HTTP 204).</summary>
public readonly record struct Unit;
