using System.Security.Cryptography;
using System.Text;

namespace EnvDeck.Core;

/// <summary>
/// Password protection for exported bundles. File layout:
/// "ENVDECK1" | PBKDF2 iterations (int32, little endian) | salt (16) | nonce (12) | tag (16) | ciphertext.
/// The key is PBKDF2-HMAC-SHA256(password, salt) and the cipher AES-256-GCM; everything before the tag is
/// authenticated, so a wrong password and a modified file are both detected before anything is read.
/// </summary>
public static class BundleCrypto
{
    static readonly byte[] Magic = "ENVDECK1"u8.ToArray();
    const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;
    const int HeaderSize = 8 + 4 + SaltSize + NonceSize + TagSize;
    const int AuthenticatedSize = HeaderSize - TagSize;

    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256; stored in the file so it can be raised later.</summary>
    public const int DefaultIterations = 600_000;

    public const int MinPasswordLength = 8;

    public static bool IsEncrypted(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return stream.Read(head) == head.Length && head.SequenceEqual(Magic);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Encrypts <paramref name="plain"/> (an in-memory bundle, so nothing readable ever reaches the disk).</summary>
    public static byte[] Encrypt(byte[] plain, string password, int iterations = DefaultIterations)
    {
        if (password.Length < MinPasswordLength)
            throw new DeckException($"The password must have at least {MinPasswordLength} characters.");

        var output = new byte[HeaderSize + plain.Length];
        Magic.CopyTo(output, 0);
        BitConverter.TryWriteBytes(output.AsSpan(8, 4), iterations);
        RandomNumberGenerator.Fill(output.AsSpan(12, SaltSize));
        RandomNumberGenerator.Fill(output.AsSpan(12 + SaltSize, NonceSize));

        var key = DeriveKey(password, output.AsSpan(12, SaltSize), iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                output.AsSpan(12 + SaltSize, NonceSize),
                plain,
                output.AsSpan(HeaderSize),
                output.AsSpan(AuthenticatedSize, TagSize),
                output.AsSpan(0, AuthenticatedSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return output;
    }

    /// <summary>Decrypts a file written by <see cref="Encrypt"/>; the caller should zero the result when done.</summary>
    /// <exception cref="WrongPasswordException">Wrong password, or the file was changed or damaged.</exception>
    public static byte[] Decrypt(byte[] input, string password)
    {
        if (input.Length < HeaderSize || !input.AsSpan(0, 8).SequenceEqual(Magic))
            throw new DeckException("This is not an encrypted EnvDeck bundle.");

        var iterations = BitConverter.ToInt32(input, 8);
        if (iterations is < 1 or > 100_000_000) throw new DeckException("The encrypted bundle is damaged.");

        var key = DeriveKey(password, input.AsSpan(12, SaltSize), iterations);
        var plain = new byte[input.Length - HeaderSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                input.AsSpan(12 + SaltSize, NonceSize),
                input.AsSpan(HeaderSize),
                input.AsSpan(AuthenticatedSize, TagSize),
                plain,
                input.AsSpan(0, AuthenticatedSize));
            return plain;
        }
        catch (CryptographicException)
        {
            throw new WrongPasswordException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
    // FormKC so the same password typed through different input methods (composed or decomposed accents) matches.
    static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), salt, iterations,
            HashAlgorithmName.SHA256, KeySize);
}
