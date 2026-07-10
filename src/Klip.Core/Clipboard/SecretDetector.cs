using System.Text.RegularExpressions;

namespace Klip.Core.Clipboard;

/// <summary>
/// Heuristic to spot content that's probably a secret: API tokens, keys, JWTs,
/// connection strings. We use it to NOT keep those in history when the option is
/// on. Conservative on purpose: rather let one slip (false negative) than block
/// legit text (false positive).
/// </summary>
public static partial class SecretDetector
{
    public static bool LooksLikeSecret(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        // a secret is usually one long "word" with no spaces; text with lots
        // of spaces/lines is almost never a standalone credential
        if (trimmed.Contains(' ') && !HasKeyValueSecret(trimmed))
            return false;

        // JWT: three base64url segments split by dots
        if (JwtRegex().IsMatch(trimmed))
            return true;

        // known provider prefixes
        if (KnownPrefixRegex().IsMatch(trimmed))
            return true;

        // connection string with a password/secret baked in
        if (HasKeyValueSecret(trimmed))
            return true;

        // generic token: long (>= 32), no spaces, mix of upper/lower/digits.
        // high entropy, tipico de chave
        if (trimmed.Length is >= 32 and <= 512 && !trimmed.Contains(' ') && IsHighEntropyToken(trimmed))
            return true;

        return false;
    }

    private static bool HasKeyValueSecret(string text) =>
        KeyValueSecretRegex().IsMatch(text);

    private static bool IsHighEntropyToken(string token)
    {
        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        var allowed = 0;
        foreach (var c in token)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else if (c is not ('-' or '_' or '+' or '/' or '=' or '.')) return false; // not a token-ish char
            allowed++;
        }
        // needs all three classes: cuts false positives from plain text way down
        return hasUpper && hasLower && hasDigit && allowed == token.Length;
    }

    // sk-..., ghp_..., xoxb-..., AKIA..., AIza..., etc.
    [GeneratedRegex(@"^(sk-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{20,}|ya29\.[0-9A-Za-z_-]+)$")]
    private static partial Regex KnownPrefixRegex();

    // JWT: header.payload.signature
    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
    private static partial Regex JwtRegex();

    // password=... / pwd=... / secret=... / api[_-]?key=...
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token)\b\s*[=:]\s*\S{6,}")]
    private static partial Regex KeyValueSecretRegex();
}
