namespace EnvDeck.Core;

/// <summary>An expected failure with a message that can be shown to the user as-is.</summary>
public class DeckException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ElevationRequiredException(EnvScope scope)
    : DeckException($"Changing {scope} variables requires administrator rights.")
{
    public EnvScope Scope { get; } = scope;
}
