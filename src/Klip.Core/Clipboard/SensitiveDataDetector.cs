using System.Text.RegularExpressions;

namespace Klip.Core.Clipboard;

/// <summary>
/// Finds personal data in a chunk of text: emails, phone numbers, credit cards,
/// IPs, CPF/SSN. Used by the editor's quick redact to know what to blur out.
/// Returns the raw matched strings so the caller can line them up with OCR words.
/// </summary>
public static partial class SensitiveDataDetector
{
    /// <summary>All sensitive substrings found in the text (may repeat).</summary>
    public static IReadOnlyList<string> Find(string text)
    {
        var hits = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return hits;

        foreach (Match m in EmailRegex().Matches(text)) hits.Add(m.Value);
        foreach (Match m in CreditCardRegex().Matches(text)) if (LuhnValid(m.Value)) hits.Add(m.Value);
        foreach (Match m in CpfRegex().Matches(text)) hits.Add(m.Value);
        foreach (Match m in PhoneRegex().Matches(text)) hits.Add(m.Value);
        foreach (Match m in IpRegex().Matches(text)) hits.Add(m.Value);
        return hits;
    }

    /// <summary>True if any word of the OCR line looks sensitive (word-level check).</summary>
    public static bool IsSensitiveWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 4)
            return false;
        if (EmailRegex().IsMatch(word)) return true;
        if (IpRegex().IsMatch(word)) return true;
        if (CpfRegex().IsMatch(word)) return true;

        // for cards/phones the OCR often splits digits across words, so also match
        // a word that's mostly digits and long enough to be part of a number
        var digits = word.Count(char.IsDigit);
        if (digits >= 4 && digits >= word.Length - 3)
            return true;

        return false;
    }

    /// <summary>Luhn check so we don't blur any random 16-digit run.</summary>
    private static bool LuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
            return false;

        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    // 13 to 16 digits, optional spaces or dashes between groups
    [GeneratedRegex(@"\b(?:\d[ -]?){13,16}\b")]
    private static partial Regex CreditCardRegex();

    // CPF: 000.000.000-00 (also plain 11 digits handled by the word check)
    [GeneratedRegex(@"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b")]
    private static partial Regex CpfRegex();

    // loose phone: optional +, groups of digits with spaces/dashes/parens, 8+ digits total
    [GeneratedRegex(@"(?:\+?\d{1,3}[ -]?)?(?:\(?\d{2,4}\)?[ -]?)?\d{3,5}[ -]?\d{3,4}")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpRegex();
}
