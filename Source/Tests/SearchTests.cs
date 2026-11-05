using CDir.Models;

internal sealed partial class RegressionRunner
{
    private void RunSearchChecks()
    {
        this.Test("Plain search keeps literal punctuation and case-insensitive substring matching", () =>
        {
            Check(SearchQuery.Parse("port[1].TXT").Matches("report[1].txt", false), "Plain punctuation must stay literal.");
            Check(!SearchQuery.Parse("report[1]").Matches("report1.txt", false), "Brackets must not enable regular expressions implicitly.");
            Check(SearchQuery.Parse("资料").Matches("项目资料.txt", false), "Plain Unicode names must still match substrings.");
        });
        this.Test("Regular expressions support anchors alternatives groups escapes and type filters", () =>
        {
            foreach (var Raw in new[] { @"/regex ^(report|notes)-\d+\.txt$ /file", @"/F /R ^(report|notes)-\d+\.txt$" })
            {
                var Query = SearchQuery.Parse(Raw);
                Check(Query.IsRegex && !Query.UsesWildcards && Query.ErrorKey is null, "Regex mode must compile once and take precedence over wildcards.");
                Check(Query.Matches("REPORT-42.TXT", false) && Query.Matches("notes-7.txt", false), "Regex matching must ignore filename case.");
                Check(!Query.Matches("xreport-42.txt", false) && !Query.Matches("report-x.txt", false)
                    && !Query.Matches("report-42.txt", true), "Anchors character classes and file filters must constrain results.");
            }
            Check(SearchQuery.Parse(@"/r ^资料\s+文件$ /d").Matches("资料 文件", true), "Regex and directory filters must support Unicode names.");
            Check(SearchQuery.Parse(@"/r ^a  b$").Matches("a  b", false)
                && !SearchQuery.Parse(@"/r ^a  b$").Matches("a b", false), "Literal whitespace inside a regex must be preserved.");
            Check(SearchQuery.Parse(@"/r ^(ab)\1(?=\.txt)\.txt$").Matches("abab.txt", false), "Backreferences and lookarounds must remain available.");
            Check(SearchQuery.Parse(@"/regex leaf").Matches("needle-leaf.txt", false), "Unanchored regexes must match any part of the name.");
            Check(SearchQuery.Parse("/regex").Matches("anything", false), "An empty regex must remain usable while entering a pattern.");
        });
        this.Test("Wildcard searches match full names with stars question marks and escaped punctuation", () =>
        {
            foreach (var Raw in new[] { "report*.txt /file", "/w /f report*.txt", "report*.txt /wildcard /f" })
            {
                var Query = SearchQuery.Parse(Raw);
                Check(Query.UsesWildcards && !Query.IsRegex && Query.Matches("REPORT.txt", false)
                    && Query.Matches("report-final.txt", false), "Stars must match zero or more characters and ignore case.");
                Check(!Query.Matches("xreport.txt", false) && !Query.Matches("report.txt.bak", false)
                    && !Query.Matches("report.txt", true), "Wildcard matching must use the full name and honor file filters.");
            }
            Check(SearchQuery.Parse("资料?.txt").Matches("资料甲.txt", false)
                && !SearchQuery.Parse("资料?.txt").Matches("资料甲乙.txt", false), "Question marks must match exactly one character.");
            Check(SearchQuery.Parse("*[1].txt").Matches("report[1].txt", false)
                && !SearchQuery.Parse("*[1].txt").Matches("report1.txt", false), "Only star and question mark have wildcard meanings.");
            Check(SearchQuery.Parse("/wildcard report").Matches("report", false)
                && !SearchQuery.Parse("/wildcard report").Matches("report.txt", false), "Explicit wildcard mode must use a whole-name match without implicit stars.");
            Check(SearchQuery.Parse("/w").Matches("anything", true), "An empty wildcard mode must allow entering a pattern without hiding everything.");
        });
        this.Test("Invalid and excessively expensive regexes fail safely and do not poison later queries", () =>
        {
            foreach (var Expression in new[] { "[", "(", @"\", "*" })
            {
                var Query = SearchQuery.Parse("/regex " + Expression);
                Check(Query.IsActive && Query.ErrorKey == "SearchRegexInvalid" && !Query.Matches("anything", false), "Invalid syntax must be reported without throwing.");
            }
            var Expensive = SearchQuery.Parse("/r ^(a+)+$");
            var Timer = System.Diagnostics.Stopwatch.StartNew();
            Check(!Expensive.Matches(new string('a', 20000) + "!", false) && Expensive.ErrorKey == "SearchRegexTimeout", "Backtracking must stop at the configured timeout.");
            for (var Index = 0; Index < 100; Index++) Check(!Expensive.Matches("aaa", false), "A timed-out query must stop matching immediately.");
            Check(Timer.Elapsed < TimeSpan.FromSeconds(2), "One timeout must not be repeated for every filename.");
            Check(SearchQuery.Parse("/r ^a+$").Matches("aaa", false), "Correcting the expression must start a fresh working query.");
        });
        this.Test("Search switches work before or after names and match only complete case-insensitive tokens", () =>
        {
            foreach (var Raw in new[] { "/file report", "report /file", "  report  /FiLe  ", "/f report", "report /F" })
            {
                var Query = SearchQuery.Parse(Raw);
                Check(Query.IsActive && Query.Matches("REPORT.txt", false) && !Query.Matches("report", true), "File filter must match only files.");
            }
            var Directories = SearchQuery.Parse("/DIR 工作");
            Check(Directories.Matches("工作目录", true) && !Directories.Matches("工作.txt", false), "Directory filter must support Unicode names.");
            Check(SearchQuery.Parse("/D 工作") == Directories && SearchQuery.Parse("工作 /d") == Directories,
                "Directory shorthand must work before or after the name and ignore case.");
            Check(SearchQuery.Parse("/F") == SearchQuery.Parse("/file") && SearchQuery.Parse("/d") == SearchQuery.Parse("/dir"),
                "Standalone shorthand must have the same behavior as the full switches.");
            Check(SearchQuery.Parse("/f /dir report") == SearchQuery.Parse("/file /d report"),
                "Mixed long and short switches must preserve combined-type behavior.");
            Check(SearchQuery.Parse("/file").Matches("anything", false) && SearchQuery.Parse("/dir").Matches("anything", true), "Type-only queries must match all names of that type.");
            var Both = SearchQuery.Parse("/file /dir report");
            Check(Both.Matches("report", true) && Both.Matches("report.txt", false), "Both switches together must include both types.");
            Check(SearchQuery.Parse("report/file").Name == "report/file" && SearchQuery.Parse("/files").Name == "/files", "Partial or unknown switches must stay literal.");
            Check(SearchQuery.Parse("report/f").Name == "report/f" && SearchQuery.Parse("/dx").Name == "/dx",
                "Shorthand must only be recognized as complete standalone tokens.");
            Check(!SearchQuery.Parse(" \t ").IsActive, "Whitespace must clear the search.");
        });
        this.Test("Recursive search finds deep files folders Unicode and duplicate names by full path", () =>
        {
            var Root = this.MakeDirectory("recursive-search");
            this.MakeFile("recursive-search\\needle-root.txt", "root");
            this.MakeFile("recursive-search\\a\\b\\needle.txt", "deep");
            this.MakeFile("recursive-search\\c\\needle.txt", "duplicate");
            this.MakeFile("recursive-search\\a\\b\\资料 needle 文件.txt", "unicode");
            this.MakeDirectory("recursive-search", "a", "needle-folder");
            this.MakeFile("recursive-search\\a\\needle-folder\\ordinary.txt", "not a name match");
            var Results = this.Service.SearchEntries(Root, SearchQuery.Parse("needle"), false);
            Check(Results.Count == 5 && Results.Count(Entry => Entry.IsDirectory) == 1, "Every matching file and directory must be found at any depth.");
            Check(Results.Count(Entry => Entry.Name == "needle.txt") == 2 && Results.Select(Entry => Entry.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5, "Duplicate names must remain distinct results.");
            Check(Results.All(Entry => !Entry.Name.Equals("ordinary.txt", StringComparison.Ordinal)), "Names must match, not just parent directory paths.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("needle /file"), false).Count == 4, "File filter must include deep matching files.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("needle /dir"), false).Count == 1, "Directory filter must still traverse nonmatching ancestors.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse(@"/r ^needle.*\.txt$ /f"), false).Count == 3,
                "Regex searches must reach deep matches while respecting name anchors.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("*needle*.txt /f"), false).Count == 4,
                "Wildcard searches must include Unicode and duplicate names at any depth.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("needle-* /d"), false).Count == 1,
                "Wildcard directory filters must traverse nonmatching parent folders.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("/r ["), false).Count == 0,
                "Invalid regexes must return no results without traversing the tree.");
        });
        this.Test("Search obeys hidden and system settings for files and entire hidden subtrees", () =>
        {
            var Root = this.MakeDirectory("search-hidden");
            var Hidden = this.MakeFile("search-hidden\\needle-hidden.txt", "hidden");
            var SystemFile = this.MakeFile("search-hidden\\needle-system.txt", "system");
            var Secret = this.MakeDirectory("search-hidden", "secret");
            this.MakeFile("search-hidden\\secret\\needle-child.txt", "child");
            System.IO.File.SetAttributes(Hidden, System.IO.FileAttributes.Hidden);
            System.IO.File.SetAttributes(SystemFile, System.IO.FileAttributes.System);
            System.IO.File.SetAttributes(Secret, System.IO.FileAttributes.Directory | System.IO.FileAttributes.Hidden);
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("needle"), false).Count == 0, "Hidden/system files and hidden subtrees must be excluded by default.");
            Check(this.Service.SearchEntries(Root, SearchQuery.Parse("needle"), true).Count == 3, "Show hidden must include files inside hidden folders.");
        });
        this.Test("Search displays matching directory links without following loops or leaving its root", () =>
        {
            var Root = this.MakeDirectory("search-links");
            var Outside = this.MakeDirectory("search-outside");
            this.MakeFile("search-outside\\needle-outside.txt", "outside");
            var Link = System.IO.Path.Combine(Root, "needle-link");
            var Loop = System.IO.Path.Combine(Root, "loop");
            try
            {
                try { System.IO.Directory.CreateSymbolicLink(Link, Outside); }
                catch (Exception Error) { CreateJunction(Link, Outside, Error); }
                try { System.IO.Directory.CreateSymbolicLink(Loop, Root); }
                catch (Exception Error) { CreateJunction(Loop, Root, Error); }
                var Results = this.Service.SearchEntries(Root, SearchQuery.Parse("needle"), true);
                Check(Results.Count == 1 && SamePath(Results[0].FullPath, Link) && Results[0].TypeKey == "TypeFolderLink", "A matching link may be listed, but its target must not be traversed.");
            }
            finally
            {
                if (System.IO.Directory.Exists(Link)) System.IO.Directory.Delete(Link);
                if (System.IO.Directory.Exists(Loop)) System.IO.Directory.Delete(Loop);
            }
        });
        this.Test("Search cancellation is checked before traversal and between result batches", () =>
        {
            var Root = this.MakeDirectory("search-cancel");
            for (var Index = 0; Index < 300; Index++) this.MakeFile($"search-cancel\\match-{Index}.txt", "test");
            using var Before = new CancellationTokenSource();
            Before.Cancel();
            Throws<OperationCanceledException>(() => this.Service.SearchEntries(Root, SearchQuery.Parse("match"), false, Before.Token));
            using var During = new CancellationTokenSource();
            var Batches = 0;
            Throws<OperationCanceledException>(() => this.Service.SearchEntries(Root, SearchQuery.Parse("match"), false, During.Token, Batch =>
            {
                Check(Batch.Count > 0 && Batch.Count <= 128, "Batches must be bounded and nonempty.");
                Batches++;
                During.Cancel();
            }));
            Check(Batches == 1, "Cancellation must stop all later batches.");
        });
        this.Test("Incremental search batches preserve every result exactly once", () =>
        {
            var Root = System.IO.Path.Combine(this.Root, "search-cancel");
            var Streamed = new List<string>();
            var Results = this.Service.SearchEntries(Root, SearchQuery.Parse("/file"), false,
                OnBatch: Batch => Streamed.AddRange(Batch.Select(Entry => Entry.FullPath)));
            Check(Results.Count == 300 && Streamed.Count == 300 && Streamed.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 300,
                "Progress batches must not duplicate or lose results.");
            Check(Streamed.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(Results.Select(Entry => Entry.FullPath).Order(StringComparer.OrdinalIgnoreCase)), "Streamed and completed results must agree.");
        });
        this.Test("Search reports a missing root instead of silently returning an empty result", () =>
            Throws<System.IO.DirectoryNotFoundException>(() => this.Service.SearchEntries(System.IO.Path.Combine(this.Root, "missing-search-root"), SearchQuery.Parse("needle"), false)));
    }
}
