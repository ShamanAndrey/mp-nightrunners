using System;
using System.Collections.Generic;
using System.Text;

namespace NightRunnersMP.Shared;

/// <summary>
/// Optional word filter shared by the mod and the relay server (compiled into both).
/// Matching is whole-word, case-insensitive, tolerant of leet-speak substitutions and stretched
/// letters. Entries ending in '*' also match as a prefix ("shit*" catches "shitty"). Matches are
/// masked keeping the first letter: "f***". No fancy NLP — a wall for the casual, not a fortress.
/// </summary>
public sealed class ProfanityFilter
{
    /// <summary>Built-in list: strong profanity and slurs. Operators extend it with a words file.</summary>
    public static readonly string[] DefaultWords =
    {
        "fuck*", "motherfuck*", "shit*", "bullshit*", "cunt*", "cock", "cocks", "cocksuck*", "dick", "dicks", "dickhead*",
        "asshole*", "arsehole*", "bitch*", "bastard*", "whore*", "slut*", "twat*", "wanker*", "prick", "pricks",
        "pussy", "pussies", "cum", "jizz", "blowjob*", "handjob*", "dildo*", "rapist*",
        "nigg*", "nigga*", "faggot*", "fag", "fags", "retard*", "kike*", "spic", "spics", "chink*", "wetback*",
        "tranny", "trannies", "dyke", "dykes", "gook", "gooks", "raghead*", "towelhead*", "beaner*",
    };

    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _prefixes = new();

    public bool Enabled { get; set; }
    public int WordCount => _exact.Count + _prefixes.Count;

    public ProfanityFilter(bool enabled = false, IEnumerable<string>? extraWords = null)
    {
        Enabled = enabled;
        foreach (var w in DefaultWords) AddWord(w);
        if (extraWords != null) foreach (var w in extraWords) AddWord(w);
    }

    public void AddWord(string entry)
    {
        var w = entry.Trim();
        if (w.Length == 0 || w.StartsWith('#')) return;
        var prefix = w.EndsWith('*');
        if (prefix) w = w[..^1];
        w = Normalize(w);
        if (w.Length < 3) return; // too short to match safely
        if (prefix) { if (!_prefixes.Contains(w)) _prefixes.Add(w); }
        else _exact.Add(w);
    }

    /// <summary>Returns the text with matching words masked, or the same instance when nothing matched.</summary>
    public string Apply(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return text;

        StringBuilder? sb = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            if (!Matches(text.AsSpan(start, i - start))) continue;

            sb ??= new StringBuilder(text);
            for (var k = start + 1; k < i; k++) sb[k] = '*';
        }
        return sb?.ToString() ?? text;
    }

    public bool Contains(string text) => !ReferenceEquals(Apply(text), text);

    private bool Matches(ReadOnlySpan<char> token)
    {
        var norm = Normalize(token);
        if (norm.Length < 3) return false;
        if (Hit(norm)) return true;
        var collapsed = CollapseRepeats(norm);
        return collapsed.Length != norm.Length && Hit(collapsed);
    }

    private bool Hit(string norm)
    {
        if (norm.IndexOf('?') < 0)
        {
            if (_exact.Contains(norm)) return true;
            foreach (var p in _prefixes) if (norm.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // Censor characters ("f@ck", "sh*t") became '?' wildcards; at least half the letters must be real.
        var wild = 0;
        foreach (var c in norm) if (c == '?') wild++;
        if (wild * 2 > norm.Length) return false;
        foreach (var w in _exact) if (WildEquals(norm, w)) return true;
        foreach (var p in _prefixes) if (WildStartsWith(norm, p)) return true;
        return false;
    }

    private static bool WildEquals(string norm, string word)
    {
        if (norm.Length != word.Length) return false;
        for (var i = 0; i < norm.Length; i++) if (norm[i] != '?' && norm[i] != word[i]) return false;
        return true;
    }

    private static bool WildStartsWith(string norm, string prefix)
    {
        if (norm.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++) if (norm[i] != '?' && norm[i] != prefix[i]) return false;
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '@' or '$' or '!' or '|' or '+' or '*' or '#' or '%' or '&';

    private static string Normalize(ReadOnlySpan<char> token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var raw in token)
        {
            var c = char.ToLowerInvariant(raw);
            c = c switch
            {
                '4' => 'a',
                '3' => 'e',
                '1' or '!' or '|' => 'i',
                '0' => 'o',
                '$' or '5' => 's',
                '7' or '+' => 't',
                '@' or '*' or '#' or '%' or '&' => '?', // censor characters: match any letter
                _ => c,
            };
            if (char.IsLetter(c) || c == '?') sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CollapseRepeats(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        return sb.ToString();
    }
}
