using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.Config;

/// <summary>
/// The single random token that authenticates phones (design memo §3.4).
/// Kept in its own file so exports never include it.
/// </summary>
public sealed class DeckTokenStore
{
    public const string FileName = "deck-token";
    private const int TokenBytes = 32;

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private string _token = string.Empty;

    public DeckTokenStore(string directory, ILogger<DeckTokenStore>? logger = null)
    {
        TokenPath = Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<DeckTokenStore>.Instance;
    }

    public string TokenPath { get; }

    /// <summary>Base64url token, at least 32 random bytes.</summary>
    public string Token
    {
        get
        {
            lock (_sync)
            {
                return _token;
            }
        }
    }

    /// <summary>Raised after <see cref="Rotate"/>; existing sessions must be invalidated.</summary>
    public event EventHandler<string>? Rotated;

    /// <summary>Loads the token, generating one on first run.</summary>
    public void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        if (File.Exists(TokenPath))
        {
            var stored = File.ReadAllText(TokenPath, Encoding.UTF8).Trim();
            if (stored.Length >= 32)
            {
                lock (_sync)
                {
                    _token = stored;
                }

                return;
            }

            _logger.LogWarning("Deck token file {Path} is invalid; generating a new token", TokenPath);
        }

        Write(Generate());
    }

    /// <summary>Replaces the token; every phone has to scan the QR code again.</summary>
    public string Rotate()
    {
        var token = Generate();
        Write(token);
        _logger.LogInformation("Deck token rotated");
        Rotated?.Invoke(this, token);
        return token;
    }

    public static string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    private void Write(string token)
    {
        lock (_sync)
        {
            // No BOM: the file is meant to be readable by scripts (Encoding.UTF8 would prepend one).
            File.WriteAllText(TokenPath, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _token = token;
        }
    }
}
