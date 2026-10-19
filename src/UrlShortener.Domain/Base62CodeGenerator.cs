using System.Security.Cryptography;

namespace UrlShortener.Domain;

public sealed class Base62CodeGenerator : ICodeGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int CodeLength = 7;

    public string Generate()
    {
        Span<char> characters = stackalloc char[CodeLength];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(characters);
    }
}
