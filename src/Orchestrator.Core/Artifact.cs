using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Orchestrator.Core;

public sealed class Artifact
{
    public string Name { get; }
    public string Content { get; }
    public string ContentHash { get; }

    public Artifact(string name, string content) : this(name, content, Hash(content)) { }

    [JsonConstructor]
    public Artifact(string name, string content, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(Hash(content), contentHash, StringComparison.Ordinal))
            throw new ArgumentException("Artifact SHA-256 hash does not match its content.", nameof(contentHash));
        Name = name;
        Content = content;
        ContentHash = contentHash;
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
