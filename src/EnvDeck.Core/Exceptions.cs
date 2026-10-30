namespace EnvDeck.Core;

/// <summary>An expected failure with a message that can be shown to the user as-is.</summary>
public class DeckException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The bundle is password protected and no password was given.</summary>
public sealed class PasswordRequiredException(string path)
    : DeckException($"{Path.GetFileName(path)} is password protected.");

/// <summary>Wrong password, or the encrypted file was modified or damaged (the two cannot be told apart).</summary>
public sealed class WrongPasswordException()
    : DeckException("Wrong password, or the file was changed or damaged.");

public sealed class ElevationRequiredException(EnvScope scope)
    : DeckException($"Changing {scope} variables requires administrator rights.")
{
    public EnvScope Scope { get; } = scope;
}
