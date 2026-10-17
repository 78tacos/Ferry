using System.Security.Cryptography;

namespace Ferry.Core.Transport;

/// <summary>
/// The one-time code the new PC shows and the old PC types in: ten characters from an alphabet without look-alikes
/// (no 0/O, 1/I), about 50 bits, shown as "K7QMX-2XRP9". It changes after every wrong attempt.
/// </summary>
public static class PairingCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 10;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Display(new string(chars));
    }

    /// <summary>Upper case, separators and spaces removed, so "k7qmx 2xrp9" equals "K7QMX-2XRP9".</summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Display(string code)
    {
        var n = Normalize(code);
        return n.Length == Length ? $"{n[..5]}-{n[5..]}" : n;
    }

    /// <summary>
    /// At least ten letters or digits. Shorter codes could be guessed offline by anyone posing as the new PC,
    /// so a code chosen in advance for silent mode must be at least as long as a generated one.
    /// </summary>
    public static bool LooksValid(string code) => Normalize(code).Length >= Length;
}
