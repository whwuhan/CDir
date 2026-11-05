using System.Text.RegularExpressions;

namespace CDir.Models;

public sealed record SearchQuery(string Name, bool FilesOnly, bool DirectoriesOnly, bool IsActive)
{
    private static readonly Regex SearchSwitch = new(@"(?<!\S)/(file|dir|regex|wildcard|f|d|r|w)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private Regex? Pattern { get; init; }
    private bool InvalidPattern { get; init; }
    private int TimedOut;
    public bool IsRegex { get; init; }
    public bool UsesWildcards { get; init; }
    public string? ErrorKey => this.InvalidPattern ? "SearchRegexInvalid"
        : Volatile.Read(ref this.TimedOut) != 0 ? "SearchRegexTimeout" : null;

    public static SearchQuery Parse(string Text)
    {
        var Files = false;
        var Directories = false;
        var RegularExpression = false;
        var Wildcard = false;
        var Name = SearchSwitch.Replace(Text, Match =>
        {
            switch (char.ToLowerInvariant(Match.Groups[1].Value[0]))
            {
                case 'f': Files = true; break;
                case 'd': Directories = true; break;
                case 'r': RegularExpression = true; break;
                case 'w': Wildcard = true; break;
            }
            return "";
        }).Trim();
        if (!RegularExpression) Name = Regex.Replace(Name, @"\s+", " ");
        Wildcard = !RegularExpression && (Wildcard || Name.IndexOfAny(['*', '?']) >= 0);
        Regex? Pattern = null;
        var Invalid = false;
        if (RegularExpression || Wildcard)
        {
            var Expression = RegularExpression ? Name : @"\A" + Regex.Escape(Name.Length == 0 ? "*" : Name).Replace(@"\*", ".*").Replace(@"\?", ".") + @"\z";
            try
            {
                Pattern = new Regex(Expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    | (Wildcard ? RegexOptions.NonBacktracking : RegexOptions.None), TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException) { Invalid = true; }
        }
        return new(Name, Files && !Directories, Directories && !Files, !string.IsNullOrWhiteSpace(Text))
        {
            IsRegex = RegularExpression, UsesWildcards = Wildcard, Pattern = Pattern, InvalidPattern = Invalid
        };
    }

    public bool Matches(string Name, bool IsDirectory)
    {
        if (this.ErrorKey is not null || (this.FilesOnly && IsDirectory) || (this.DirectoriesOnly && !IsDirectory)) return false;
        if (this.Pattern is null) return Name.Contains(this.Name, StringComparison.OrdinalIgnoreCase);
        try { return this.Pattern.IsMatch(Name); }
        catch (RegexMatchTimeoutException)
        {
            Interlocked.Exchange(ref this.TimedOut, 1);
            return false;
        }
    }

    public bool Matches(FileEntry Entry) => this.Matches(Entry.Name, Entry.IsDirectory);
}
