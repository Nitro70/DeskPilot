using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Vault;

namespace DeskPilot.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly TempVault _vault = new();
    private readonly List<TempVault> _extra = new();

    public void Dispose()
    {
        _vault.Dispose();
        foreach (var v in _extra) v.Dispose();
    }

    private TempVault NewTempDir()
    {
        var v = new TempVault();
        _extra.Add(v);
        return v;
    }

    // ------------------------------------------------------------------ helpers

    private sealed class TempVault : IDisposable
    {
        private readonly List<string> _links = new();

        public TempVault()
        {
            Root = Path.Combine(Path.GetTempPath(), "deskpilot-vault-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Full(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

        public string Write(string rel, string content, DateTime? mtimeUtc = null)
        {
            var full = Full(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            if (mtimeUtc != null) File.SetLastWriteTimeUtc(full, mtimeUtc.Value);
            return full;
        }

        public string Read(string rel) => File.ReadAllText(Full(rel));

        /// <summary>Creates a directory junction with "cmd /c mklink /J" (no admin needed). Returns false when that is not possible here.</summary>
        public bool TryCreateJunction(string rel, string target)
        {
            var link = Full(rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(link);
                psi.ArgumentList.Add(target);
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) return false;
                var ok = Directory.Exists(link) && new DirectoryInfo(link).LinkTarget != null;
                if (ok) _links.Add(link);
                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryCreateFileSymlink(string rel, string target)
        {
            var link = Full(rel);
            try
            {
                File.CreateSymbolicLink(link, target);
                _links.Add(link);
                return File.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            // Remove links first so a recursive delete can never reach their targets.
            foreach (var link in _links)
            {
                try
                {
                    if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
                    else if (File.Exists(link)) File.Delete(link);
                }
                catch (Exception)
                {
                    // best effort
                }
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(Root)) return;
                    foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static VaultFilter DefaultFilter() => new(new VaultSettings().IncludeExtensions, new VaultSettings().ExcludeFolders);

    private VaultIndex NewIndex(FakeClock? clock = null, VaultFilter? filter = null) =>
        new(_vault.Root, filter ?? DefaultFilter(), clock == null ? null : clock.Get);

    private static List<string> Paths(VaultSearchResult r) => r.Hits.Select(h => h.Path).ToList();

    private (VaultToolHost Host, AppSettings Settings) NewHost(Action<VaultSettings>? configure = null)
    {
        var settings = new AppSettings();
        settings.Vault.Path = _vault.Root;
        configure?.Invoke(settings.Vault);
        return (new VaultToolHost(() => settings), settings);
    }

    private static Task<ToolResult> Call(VaultToolHost host, string tool, object args, CancellationToken ct = default) =>
        host.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), ct);

    // ------------------------------------------------------------------ tokenizer and parser

    [Fact]
    public void Tokenize_splits_camel_snake_and_kebab_case_and_lowercases()
    {
        var tokens = VaultText.Tokenize("DeskPilot vault_tool-host HTTPServer");
        Assert.Contains("desk", tokens);
        Assert.Contains("pilot", tokens);
        Assert.Contains("deskpilot", tokens);
        Assert.Contains("vault", tokens);
        Assert.Contains("tool", tokens);
        Assert.Contains("host", tokens);
        Assert.Contains("http", tokens);
        Assert.Contains("server", tokens);
        Assert.Contains("httpserver", tokens);
        Assert.All(tokens, t => Assert.Equal(t.ToLowerInvariant(), t));
    }

    [Fact]
    public void Tokenize_drops_stopwords_punctuation_and_single_ascii_letters()
    {
        Assert.Equal(new[] { "user", "notes", "plan" }, VaultText.Tokenize("The user's notes, and a (plan)!"));
        Assert.Equal(new[] { "the", "user", "notes", "and", "plan" }, VaultText.Tokenize("The user's notes and a plan", dropStopwords: false));
    }

    [Fact]
    public void Tokenize_keeps_unicode_letters_and_digits()
    {
        var tokens = VaultText.Tokenize("Café Müller naïve 東京 version 42 v2");
        Assert.Contains("café", tokens);
        Assert.Contains("müller", tokens);
        Assert.Contains("naïve", tokens);
        Assert.Contains("東京", tokens);
        Assert.Contains("42", tokens);
        Assert.Contains("v2", tokens);
    }

    [Fact]
    public void NormalizeForPhrase_collapses_punctuation_and_case()
    {
        Assert.Equal("desk pilot setup", VaultText.NormalizeForPhrase("  Desk-Pilot   **setup**: "));
        Assert.Equal(3, VaultText.CountWords("Desk-Pilot setup"));
    }

    [Fact]
    public void SplitLines_handles_all_newline_styles()
    {
        Assert.Equal(new[] { "a", "b", "c", "", "d" }, VaultText.SplitLines("a\r\nb\nc\r\rd\n"));
        Assert.Empty(VaultText.SplitLines(""));
    }

    [Fact]
    public void TryDecode_rejects_binary_and_accepts_utf16_with_bom()
    {
        Assert.False(VaultText.TryDecode(new byte[] { 0x50, 0x4B, 0x03, 0x00, 0x14 }, out _));
        Assert.True(VaultText.TryDecode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo")).ToArray(), out var t16));
        Assert.Equal("héllo", t16);
        Assert.True(VaultText.TryDecode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("plain")).ToArray(), out var t8));
        Assert.Equal("plain", t8);
    }

    [Fact]
    public void Parser_reads_frontmatter_aliases_tags_headings_and_inline_tags()
    {
        var text = """
            ---
            aliases:
              - Desk Copilot
              - "DP"
            tags: [project, "#windows"]
            status: active build
            ---
            # DeskPilot Overview
            Intro with #inline-tag and #nested/topic but not #123 or a#b.
            ## Setup ##
            ```bash
            # not a heading
            echo #notatag
            ```
            ### Plan #todo
            """;
        var lines = VaultText.SplitLines(text);
        var s = NoteParser.ReadStructure(lines);
        Assert.Equal("DeskPilot Overview", s.FirstH1);
        Assert.Equal(new[] { "Desk Copilot", "DP" }, s.Aliases);
        Assert.Equal(new[] { "DeskPilot Overview", "Setup", "Plan #todo" }, s.Headings);
        Assert.Contains("project", s.Tags);
        Assert.Contains("windows", s.Tags);
        Assert.Contains("inline-tag", s.Tags);
        Assert.Contains("nested/topic", s.Tags);
        Assert.Contains("todo", s.Tags);
        Assert.DoesNotContain("123", s.Tags);
        Assert.DoesNotContain("notatag", s.Tags);
        Assert.DoesNotContain("b", s.Tags);
        Assert.Equal(7, s.BodyStartLine);
        Assert.Contains("active build", s.FrontmatterBodyText);

        var parsed = NoteParser.Parse("Projects/DeskPilot.md", text);
        Assert.Equal("DeskPilot Overview", parsed.DisplayTitle);
        Assert.True(parsed.Terms["copilot"].Aliases > 0);
        Assert.True(parsed.Terms["windows"].Tags > 0);
        Assert.True(parsed.Terms["setup"].Headings > 0);
        Assert.True(parsed.Terms["overview"].Title > 0);
        Assert.True(parsed.Terms["projects"].Path > 0);
        Assert.True(parsed.Terms["active"].Body > 0);
    }

    [Fact]
    public void Parser_without_frontmatter_or_h1_uses_file_name()
    {
        var parsed = NoteParser.Parse("Inbox/Shopping List.md", "milk\neggs\n");
        Assert.Equal("Shopping List", parsed.DisplayTitle);
        Assert.True(parsed.Terms["shopping"].Title > 0);
        Assert.True(parsed.Terms["milk"].Body > 0);
    }

    // ------------------------------------------------------------------ ranking

    [Fact]
    public void Title_match_beats_body_match()
    {
        _vault.Write("Kubernetes.md", "Cluster notes about pods and nodes and more words.");
        _vault.Write("Misc.md", "Kubernetes kubernetes kubernetes appears in this body text a lot.");
        _vault.Write("Other.md", "Unrelated filler content.");
        var r = NewIndex().Search("kubernetes", 10);
        Assert.Equal(new[] { "Kubernetes.md", "Misc.md" }, Paths(r));
    }

    [Fact]
    public void First_h1_counts_as_title()
    {
        _vault.Write("a1.md", "# Garden Irrigation\nPlan the beds.");
        _vault.Write("b1.md", "Plan the beds.\nIrrigation once.");
        var r = NewIndex().Search("irrigation", 10);
        Assert.Equal("a1.md", r.Hits[0].Path);
        Assert.Equal("Garden Irrigation", r.Hits[0].Title);
    }

    [Fact]
    public void Frontmatter_tag_beats_body_mention()
    {
        _vault.Write("Tagged.md", "---\ntags: [rust]\n---\nsome words about programming here");
        _vault.Write("Mentioned.md", "some words about rust programming here");
        var r = NewIndex().Search("rust", 10);
        Assert.Equal(new[] { "Tagged.md", "Mentioned.md" }, Paths(r));
    }

    [Fact]
    public void Inline_tag_beats_body_mention()
    {
        _vault.Write("Tagged.md", "words about the server #homelab\nmore text");
        _vault.Write("Mentioned.md", "words about the homelab server\nmore text");
        var r = NewIndex().Search("homelab", 10);
        Assert.Equal("Tagged.md", r.Hits[0].Path);
    }

    [Fact]
    public void Heading_match_beats_body_match()
    {
        _vault.Write("WithHeading.md", "intro line\n## Deployment\nsteps are listed here");
        _vault.Write("WithBody.md", "intro line\nthe deployment steps are listed here");
        var r = NewIndex().Search("deployment", 10);
        Assert.Equal(new[] { "WithHeading.md", "WithBody.md" }, Paths(r));
    }

    [Fact]
    public void Alias_match_ranks_above_body_match()
    {
        _vault.Write("Person.md", "---\naliases: [Grandma Rose]\n---\nbirthday is in May");
        _vault.Write("Diary.md", "visited grandma today, birthday plans");
        var r = NewIndex().Search("grandma", 10);
        Assert.Equal("Person.md", r.Hits[0].Path);
    }

    [Fact]
    public void Phrase_bonus_ranks_adjacent_words_first()
    {
        _vault.Write("Apart.md", "coffee alpha beta gamma\ngrinder delta");
        _vault.Write("Together.md", "coffee grinder alpha beta\ngamma delta");
        var r = NewIndex().Search("coffee grinder", 10);
        Assert.Equal(new[] { "Together.md", "Apart.md" }, Paths(r));
        Assert.True(r.Hits[0].Score > r.Hits[1].Score);
    }

    [Fact]
    public void Prefix_match_finds_longer_words_at_lower_weight()
    {
        _vault.Write("Long.md", "the configuration of the router");
        _vault.Write("Short.md", "the config of the router");
        _vault.Write("None.md", "the router");
        var idx = NewIndex();

        var r = idx.Search("config", 10);
        Assert.Equal(new[] { "Short.md", "Long.md" }, Paths(r));

        Assert.Equal(new[] { "Long.md", "Short.md" }, Paths(idx.Search("conf", 10)).OrderBy(x => x).ToArray());
        // Terms shorter than 4 characters are not expanded.
        Assert.Empty(idx.Search("con", 10).Hits);
    }

    [Fact]
    public void Plural_query_finds_singular()
    {
        _vault.Write("One.md", "a single project lives here");
        var r = NewIndex().Search("projects", 10);
        Assert.Equal("One.md", Assert.Single(r.Hits).Path);
    }

    [Fact]
    public void Relative_path_match_gives_a_small_bonus()
    {
        _vault.Write("Recipes/Pasta.md", "tomato sauce with basil");
        _vault.Write("Misc/Pasta.md", "tomato sauce with basil");
        var r = NewIndex().Search("recipes tomato", 10);
        Assert.Equal("Recipes/Pasta.md", r.Hits[0].Path);
    }

    [Fact]
    public void More_matching_terms_rank_higher()
    {
        _vault.Write("Both.md", "backup script for the nas");
        _vault.Write("One.md", "backup backup backup of photos");
        var r = NewIndex().Search("nas backup", 10);
        Assert.Equal("Both.md", r.Hits[0].Path);
    }

    [Fact]
    public void CamelCase_query_and_compound_words_match()
    {
        _vault.Write("Tool.md", "Notes about the DeskPilot app");
        var idx = NewIndex();
        Assert.Single(idx.Search("deskpilot", 5).Hits);
        Assert.Single(idx.Search("DeskPilot", 5).Hits);
        Assert.Single(idx.Search("pilot", 5).Hits);
    }

    [Fact]
    public void Stopword_only_query_reports_no_keywords()
    {
        _vault.Write("A.md", "the and of");
        var r = NewIndex().Search("the and", 5);
        Assert.True(r.NoKeywords);
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void Snippets_show_up_to_three_best_lines_with_numbers()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 30; i++) sb.Append(i is 5 or 12 or 20 or 25 ? $"line {i} mentions the zeppelin\n" : $"filler line {i}\n");
        _vault.Write("Long.md", sb.ToString());
        var hit = Assert.Single(NewIndex().Search("zeppelin", 5).Hits);
        Assert.Equal(3, hit.Lines.Count);
        Assert.All(hit.Lines, l => Assert.Contains("zeppelin", l.Text));
        Assert.Equal(hit.Lines.Select(l => l.LineNumber).OrderBy(x => x), hit.Lines.Select(l => l.LineNumber));
        Assert.All(hit.Lines, l => Assert.Contains(l.LineNumber, new[] { 5, 12, 20, 25 }));
    }

    [Fact]
    public void Long_lines_are_trimmed_around_the_match()
    {
        var line = new string('x', 10) + " " + string.Join(" ", Enumerable.Repeat("padding", 100)) + " the walrus is here " + string.Join(" ", Enumerable.Repeat("more", 100));
        _vault.Write("Wide.md", "first\n" + line + "\n");
        var hit = Assert.Single(NewIndex().Search("walrus", 5).Hits);
        var snip = Assert.Single(hit.Lines);
        Assert.Equal(2, snip.LineNumber);
        Assert.Contains("walrus", snip.Text);
        Assert.True(snip.Text.Length <= 210, $"snippet too long: {snip.Text.Length}");
        Assert.StartsWith("...", snip.Text);
        Assert.EndsWith("...", snip.Text);
    }

    [Fact]
    public void Search_folder_filter_limits_results()
    {
        _vault.Write("Work/Plan.md", "quarterly roadmap");
        _vault.Write("Home/Plan.md", "roadmap for the garden");
        var r = NewIndex().Search("roadmap", 10, "Home");
        Assert.Equal(new[] { "Home/Plan.md" }, Paths(r));
    }

    [Fact]
    public void Max_results_limits_hits_and_reports_total()
    {
        for (var i = 0; i < 12; i++) _vault.Write($"N{i:00}.md", $"llama number {i}");
        var r = NewIndex().Search("llama", 5);
        Assert.Equal(5, r.Hits.Count);
        Assert.Equal(12, r.TotalMatches);
    }

    // ------------------------------------------------------------------ scanning rules

    [Fact]
    public void Excluded_hidden_large_binary_and_other_types_are_not_indexed()
    {
        _vault.Write("Visible.md", "kiwi visible");
        _vault.Write("Upper.MD", "kiwi upper extension");
        _vault.Write(".obsidian/workspace.md", "kiwi obsidian");
        _vault.Write("node_modules/pkg/readme.md", "kiwi modules");
        _vault.Write("Archive/OLD/x.md", "kiwi old");
        _vault.Write("Sub/.hidden/y.md", "kiwi dotfolder");
        _vault.Write(".dotfile.md", "kiwi dotfile");
        var hidden = _vault.Write("HiddenAttr.md", "kiwi hidden attribute");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        _vault.Write("image.png", "kiwi png");
        _vault.Write("Big.md", "kiwi big " + new string('a', 2 * 1024 * 1024 + 10));
        File.WriteAllBytes(_vault.Full("Binary.md"), Encoding.UTF8.GetBytes("kiwi\0binary"));

        var filter = new VaultFilter(new[] { "md" }, new[] { ".obsidian", "node_modules", "old" });
        var idx = NewIndex(filter: filter);
        var r = idx.Search("kiwi", 25);
        Assert.Equal(new[] { "Upper.MD", "Visible.md" }, Paths(r).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, idx.SkippedLargeFiles);
    }

    [Fact]
    public void Exclude_entries_with_a_slash_match_a_relative_folder()
    {
        _vault.Write("Private/Journal/day.md", "mango journal");
        _vault.Write("Public/Journal/day.md", "mango public");
        var idx = NewIndex(filter: new VaultFilter(new[] { ".md" }, new[] { "private\\journal" }));
        Assert.Equal(new[] { "Public/Journal/day.md" }, Paths(idx.Search("mango", 10)));
    }

    [Fact]
    public void Empty_extension_list_falls_back_to_markdown()
    {
        _vault.Write("a.md", "papaya");
        _vault.Write("b.txt", "papaya");
        var idx = NewIndex(filter: new VaultFilter(Array.Empty<string>(), null));
        Assert.Equal(new[] { "a.md" }, Paths(idx.Search("papaya", 10)));
    }

    [Fact]
    public void File_cap_truncates_the_scan()
    {
        for (var i = 0; i < 8; i++) _vault.Write($"n{i}.md", "cherry");
        var idx = NewIndex();
        idx.FileLimit = 5;
        idx.Refresh();
        Assert.True(idx.Truncated);
        Assert.Equal(5, idx.NoteCount);
    }

    [Fact]
    public void Indexing_a_thousand_notes_is_fast()
    {
        for (var i = 0; i < 1000; i++)
            _vault.Write($"F{i % 20}/Note {i}.md", $"# Note {i}\n---\nSome text about topic{i % 37} and project{i % 11}.\n#tag{i % 5}\n" + string.Join(' ', Enumerable.Repeat("filler words here", 40)));
        var idx = NewIndex();
        var sw = Stopwatch.StartNew();
        idx.Refresh();
        sw.Stop();
        Assert.Equal(1000, idx.NoteCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"indexing took {sw.Elapsed}");
        Assert.NotEmpty(idx.Search("topic7 project3", 5).Hits);
    }

    // ------------------------------------------------------------------ incremental refresh

    [Fact]
    public void Refresh_reindexes_only_changed_added_and_removed_files_after_the_interval()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++) _vault.Write($"n{i}.md", $"stable note {i} apricot", t0);
        var clock = new FakeClock();
        var idx = NewIndex(clock);

        Assert.Equal(10, idx.Search("apricot", 25).TotalMatches);
        Assert.Equal(10, idx.ParseCount);

        _vault.Write("n3.md", "edited note now about blueberry", t0.AddMinutes(5));
        _vault.Write("added.md", "a new blueberry note", t0.AddMinutes(5));

        // Within 30 s the cached scan is used.
        clock.Now = clock.Now.AddSeconds(10);
        Assert.Empty(idx.Search("blueberry", 25).Hits);
        Assert.Equal(10, idx.ParseCount);

        clock.Now = clock.Now.AddSeconds(25);
        var r = idx.Search("blueberry", 25);
        Assert.Equal(new[] { "added.md", "n3.md" }, Paths(r).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(12, idx.ParseCount);
        Assert.Equal(9, idx.Search("apricot", 25).TotalMatches);

        File.Delete(_vault.Full("n5.md"));
        clock.Now = clock.Now.AddSeconds(31);
        Assert.Equal(8, idx.Search("apricot", 25).TotalMatches);
        Assert.Equal(10, idx.NoteCount);
        Assert.Equal(12, idx.ParseCount);
    }

    [Fact]
    public void MarkDirty_forces_a_rescan_before_the_interval()
    {
        var clock = new FakeClock();
        _vault.Write("a.md", "plum");
        var idx = NewIndex(clock);
        Assert.Single(idx.Search("plum", 5).Hits);
        _vault.Write("b.md", "plum too");
        Assert.Single(idx.Search("plum", 5).Hits);
        idx.MarkDirty();
        Assert.Equal(2, idx.Search("plum", 5).Hits.Count);
    }

    [Fact]
    public void Deleted_file_disappears_from_results_even_before_the_rescan()
    {
        var clock = new FakeClock();
        _vault.Write("a.md", "quince");
        _vault.Write("b.md", "quince");
        var idx = NewIndex(clock);
        Assert.Equal(2, idx.Search("quince", 5).Hits.Count);
        File.Delete(_vault.Full("a.md"));
        Assert.Equal(new[] { "b.md" }, Paths(idx.Search("quince", 5)));
    }

    [Fact]
    public async Task Concurrent_searches_and_refreshes_are_safe()
    {
        for (var i = 0; i < 50; i++) _vault.Write($"c{i}.md", $"grape {i}");
        var idx = NewIndex();
        idx.RefreshInterval = TimeSpan.Zero;
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var k = 0; k < 20; k++)
            {
                if (t == 0) _vault.Write($"c{k}.md", $"grape edited {k} {t}");
                if (k % 5 == 0) idx.MarkDirty();
                var r = idx.Search("grape", 10);
                Assert.Equal(10, r.Hits.Count);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(50, idx.NoteCount);
    }

    [Fact]
    public void Search_honours_cancellation()
    {
        _vault.Write("a.md", "lime");
        var idx = NewIndex();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => idx.Search("lime", 5, null, cts.Token));
    }

    // ------------------------------------------------------------------ path guard

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..\\outside.md")]
    [InlineData("Notes/../../outside.md")]
    [InlineData("Notes/..")]
    [InlineData("..")]
    [InlineData("./../outside.md")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:win.ini")]
    [InlineData("c:")]
    [InlineData("\\\\server\\share\\x.md")]
    [InlineData("//server/share/x.md")]
    [InlineData("\\\\?\\C:\\Windows\\win.ini")]
    [InlineData("\\\\.\\C:\\x.md")]
    [InlineData("\\\\?\\UNC\\server\\share\\x.md")]
    [InlineData("Note.md:secret")]
    [InlineData("Note.md::$DATA")]
    [InlineData("Notes/Note.md:Zone.Identifier")]
    [InlineData("...")]
    [InlineData("Notes/.../x.md")]
    [InlineData(". .")]
    [InlineData("Note.md.")]
    [InlineData("Notes /x.md")]
    [InlineData("Notes/ x.md")]
    [InlineData("CON")]
    [InlineData("Notes/nul.md")]
    [InlineData("COM1.txt")]
    [InlineData("Notes/*.md")]
    [InlineData("Notes/?.md")]
    [InlineData("Notes/a|b.md")]
    [InlineData("Notes/a\0b.md")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Path_escape_attempts_are_rejected_by_normalization(string input)
    {
        Assert.False(VaultPathGuard.TryNormalize(input, allowEmpty: false, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("Notes/Note.md", "Notes/Note.md")]
    [InlineData("Notes\\Note.md", "Notes/Note.md")]
    [InlineData("/Notes/Note.md", "Notes/Note.md")]
    [InlineData("./Notes/./Note.md", "Notes/Note.md")]
    [InlineData("\"Notes/Note.md\"", "Notes/Note.md")]
    [InlineData("Notes//Note.md", "Notes/Note.md")]
    [InlineData(" Notes/My Note.md ", "Notes/My Note.md")]
    public void Relative_paths_are_normalized(string input, string expected)
    {
        Assert.True(VaultPathGuard.TryNormalize(input, allowEmpty: false, out var segments, out var error), error);
        Assert.Equal(expected, string.Join('/', segments));
    }

    [Fact]
    public async Task Read_rejects_every_escape_and_never_returns_outside_content()
    {
        var outside = NewTempDir();
        outside.Write("secret.md", "TOPSECRET outside content");
        _vault.Write("Notes/Note.md", "inside");
        var (host, _) = NewHost();

        var rel = Path.GetRelativePath(_vault.Root, outside.Full("secret.md"));
        var attempts = new[]
        {
            rel, rel.Replace('\\', '/'), "Notes/../" + rel.Replace('\\', '/'),
            outside.Full("secret.md"), _vault.Full("Notes/Note.md"),
            "\\\\localhost\\" + outside.Full("secret.md").Replace(":", "$"),
            "Notes/Note.md:hidden", "Notes/Note.md::$DATA", "C:secret.md",
        };
        foreach (var attempt in attempts)
        {
            var r = await Call(host, "vault_read", new { path = attempt });
            Assert.True(r.IsError, $"expected rejection for {attempt}");
            Assert.DoesNotContain("TOPSECRET", r.Text);
        }
    }

    [Fact]
    public async Task Junction_pointing_outside_is_never_followed()
    {
        var outside = NewTempDir();
        outside.Write("secret.md", "TOPSECRET junction content");
        _vault.Write("Notes/Note.md", "inside note");
        if (!_vault.TryCreateJunction("linkout", outside.Root)) return; // cannot create junctions here: skip
        Assert.True(_vault.TryCreateJunction("Notes/deeper", outside.Root));

        var (host, settings) = NewHost();
        settings.Vault.AllowWrites = true;

        foreach (var p in new[] { "linkout/secret.md", "linkout/secret", "Notes/deeper/secret.md", "LINKOUT\\secret.md" })
        {
            var read = await Call(host, "vault_read", new { path = p });
            Assert.True(read.IsError, p);
            Assert.DoesNotContain("TOPSECRET", read.Text);
        }

        var search = await Call(host, "vault_search", new { query = "topsecret" });
        Assert.False(search.IsError);
        Assert.DoesNotContain("TOPSECRET", search.Text);
        Assert.StartsWith("No notes matched", search.Text);

        var list = await Call(host, "vault_list", new { });
        Assert.DoesNotContain("linkout", list.Text);
        Assert.True((await Call(host, "vault_list", new { folder = "linkout" })).IsError);

        var append = await Call(host, "vault_append", new { path = "linkout/new.md", text = "escape" });
        Assert.True(append.IsError);
        Assert.False(File.Exists(outside.Full("new.md")));
        var appendDeep = await Call(host, "vault_append", new { path = "linkout/sub/new.md", text = "escape" });
        Assert.True(appendDeep.IsError);
        Assert.False(Directory.Exists(outside.Full("sub")));
    }

    [Fact]
    public async Task Junction_pointing_inside_the_vault_can_be_read()
    {
        _vault.Write("Projects/Foo.md", "foo content");
        if (!_vault.TryCreateJunction("Alias", _vault.Full("Projects"))) return; // cannot create junctions here: skip
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "Alias/Foo.md" });
        Assert.False(r.IsError, r.Text);
        Assert.Contains("1| foo content", r.Text);
    }

    [Fact]
    public async Task Junction_into_an_excluded_or_hidden_folder_is_rejected()
    {
        _vault.Write(".obsidian/plugins/data.json", "{\"apiKey\": \"PLUGINSECRET\"}");
        _vault.Write("node_modules/pkg/readme.md", "MODULESECRET");
        if (!_vault.TryCreateJunction("cfg", _vault.Full(".obsidian"))) return; // cannot create junctions here: skip
        Assert.True(_vault.TryCreateJunction("mods", _vault.Full("node_modules")));
        var (host, _) = NewHost();
        foreach (var p in new[] { "cfg/plugins/data.json", "mods/pkg/readme.md" })
        {
            var r = await Call(host, "vault_read", new { path = p });
            Assert.True(r.IsError, p);
            Assert.DoesNotContain("SECRET", r.Text);
        }
        Assert.True((await Call(host, "vault_list", new { folder = "cfg" })).IsError);
    }

    [Fact]
    public async Task File_symlink_pointing_outside_is_rejected()
    {
        var outside = NewTempDir();
        var target = outside.Write("secret.md", "TOPSECRET symlink content");
        if (!_vault.TryCreateFileSymlink("link.md", target)) return; // needs developer mode or admin: skip
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "link.md" });
        Assert.True(r.IsError);
        Assert.DoesNotContain("TOPSECRET", r.Text);
        var s = await Call(host, "vault_search", new { query = "topsecret" });
        Assert.DoesNotContain("TOPSECRET", s.Text);
    }

    [Fact]
    public async Task Hidden_and_excluded_paths_cannot_be_read()
    {
        _vault.Write(".obsidian/plugins/data.md", "plugin secrets");
        _vault.Write("node_modules/x.md", "module");
        var hidden = _vault.Write("Hidden.md", "hidden attr");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var (host, _) = NewHost();
        foreach (var p in new[] { ".obsidian/plugins/data.md", "NODE_MODULES/x.md", "Hidden.md" })
            Assert.True((await Call(host, "vault_read", new { path = p })).IsError, p);
    }

    // ------------------------------------------------------------------ vault_read

    private void WriteTenLines(string rel) =>
        _vault.Write(rel, string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n");

    [Fact]
    public async Task Read_returns_numbered_range_with_header_and_more_note()
    {
        WriteTenLines("Notes/Ten.md");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "Notes/Ten.md", start_line = 3, max_lines = 4 });
        Assert.False(r.IsError, r.Text);
        var lines = r.Text.Split('\n');
        Assert.Equal("File: Notes/Ten.md (lines 3-6 of 10)", lines[0]);
        Assert.Equal("3| line 3", lines[1]);
        Assert.Equal("6| line 6", lines[4]);
        Assert.Contains("4 more lines", r.Text);
        Assert.Contains("start_line 7", r.Text);
        Assert.DoesNotContain("7| line 7", r.Text);
    }

    [Fact]
    public async Task Read_whole_short_note_and_omitted_extension()
    {
        WriteTenLines("Notes/Ten.md");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "notes\\ten" });
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("File: notes/ten.md (lines 1-10 of 10)", r.Text);
        Assert.Contains("10| line 10", r.Text);
        Assert.DoesNotContain("more lines", r.Text);
    }

    [Fact]
    public async Task Read_is_capped_at_MaxReadLines_and_accepts_string_numbers()
    {
        WriteTenLines("Ten.md");
        var (host, _) = NewHost(v => v.MaxReadLines = 4);
        var r = await Call(host, "vault_read", new { path = "Ten.md", start_line = "2", max_lines = "100" });
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("File: Ten.md (lines 2-5 of 10)", r.Text);
        Assert.DoesNotContain("6| line 6", r.Text);
    }

    [Fact]
    public async Task Read_past_end_is_an_error_and_empty_note_is_reported()
    {
        WriteTenLines("Ten.md");
        _vault.Write("Empty.md", "");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "Ten.md", start_line = 11 });
        Assert.True(r.IsError);
        Assert.Contains("10 lines", r.Text);
        var e = await Call(host, "vault_read", new { path = "Empty.md" });
        Assert.False(e.IsError);
        Assert.Contains("empty", e.Text);
    }

    [Fact]
    public async Task Read_refuses_binary_and_non_included_files()
    {
        File.WriteAllBytes(_vault.Full("Binary.md"), new byte[] { 0x23, 0x20, 0x41, 0x00, 0x42 });
        _vault.Write("tool.exe", "MZ fake");
        _vault.Write("photo.png", "fake");
        var (host, _) = NewHost();
        var bin = await Call(host, "vault_read", new { path = "Binary.md" });
        Assert.True(bin.IsError);
        Assert.Contains("binary", bin.Text);
        Assert.True((await Call(host, "vault_read", new { path = "tool.exe" })).IsError);
        Assert.True((await Call(host, "vault_read", new { path = "photo.png" })).IsError);
    }

    [Fact]
    public async Task Read_missing_note_suggests_the_right_path_and_folder_points_to_list()
    {
        _vault.Write("Projects/Foo.md", "foo");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "Foo.md" });
        Assert.True(r.IsError);
        Assert.Contains("Projects/Foo.md", r.Text);
        var f = await Call(host, "vault_read", new { path = "Projects" });
        Assert.True(f.IsError);
        Assert.Contains("vault_list", f.Text);
    }

    [Fact]
    public async Task Read_cuts_very_long_lines()
    {
        _vault.Write("Wide.md", new string('w', 5000));
        var (host, _) = NewHost();
        var r = await Call(host, "vault_read", new { path = "Wide.md" });
        Assert.False(r.IsError);
        Assert.Contains("line cut, 5000 characters", r.Text);
        Assert.True(r.Text.Length < 2200);
    }

    // ------------------------------------------------------------------ vault_search tool

    [Fact]
    public async Task Search_tool_formats_results_with_paths_titles_lines_and_hint()
    {
        _vault.Write("Projects/Foo.md", "# Foo\nintro\nthe flamingo setup lives here\nend");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_search", new { query = "flamingo" });
        Assert.False(r.IsError, r.Text);
        Assert.Contains("1. Projects/Foo.md  (Foo)", r.Text);
        Assert.Contains("   L3: the flamingo setup lives here", r.Text);
        Assert.Contains("vault_read", r.Text);
        Assert.Contains("start_line", r.Text);
    }

    [Fact]
    public async Task Search_tool_no_results_suggests_other_keywords()
    {
        _vault.Write("a.md", "nothing relevant");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_search", new { query = "xylophone" });
        Assert.False(r.IsError);
        Assert.StartsWith("No notes matched \"xylophone\"", r.Text);
        Assert.Contains("keywords", r.Text);
    }

    [Fact]
    public async Task Search_tool_caps_max_results_and_validates_folder()
    {
        for (var i = 0; i < 30; i++) _vault.Write($"Many/n{i:00}.md", "walnut");
        var (host, _) = NewHost(v => v.MaxSearchResults = 3);
        var def = await Call(host, "vault_search", new { query = "walnut" });
        Assert.Contains("3. ", def.Text);
        Assert.DoesNotContain("4. ", def.Text);
        var many = await Call(host, "vault_search", new { query = "walnut", max_results = 100 });
        Assert.Contains("25. ", many.Text);
        Assert.DoesNotContain("26. ", many.Text);
        Assert.True((await Call(host, "vault_search", new { query = "walnut", folder = "../x" })).IsError);
        Assert.True((await Call(host, "vault_search", new { query = "walnut", folder = "Missing" })).IsError);
        Assert.False((await Call(host, "vault_search", new { query = "walnut", folder = "/Many/" })).IsError);
        Assert.True((await Call(host, "vault_search", new { query = "  " })).IsError);
    }

    // ------------------------------------------------------------------ vault_list

    [Fact]
    public async Task List_shows_folders_with_counts_and_notes_and_hides_excluded()
    {
        _vault.Write("Projects/a.md", "a");
        _vault.Write("Projects/Sub/b.md", "b");
        _vault.Write("Projects/Sub/c.txt", "c");
        _vault.Write("Empty/readme.png", "x");
        _vault.Write(".obsidian/app.json", "{}");
        _vault.Write("node_modules/m.md", "m");
        _vault.Write("Root note.md", "r");
        _vault.Write("data.bin", "x");
        var (host, _) = NewHost();

        var root = await Call(host, "vault_list", new { });
        Assert.False(root.IsError, root.Text);
        Assert.Contains("Projects/  (3 notes)", root.Text);
        Assert.Contains("Empty/  (0 notes)", root.Text);
        Assert.Contains("Root note.md", root.Text);
        Assert.DoesNotContain(".obsidian", root.Text);
        Assert.DoesNotContain("node_modules", root.Text);
        Assert.DoesNotContain("data.bin", root.Text);

        var sub = await Call(host, "vault_list", new { folder = "Projects" });
        Assert.Contains("Projects/Sub/  (2 notes)", sub.Text);
        Assert.Contains("Projects/a.md", sub.Text);

        Assert.True((await Call(host, "vault_list", new { folder = "Nope" })).IsError);
        Assert.True((await Call(host, "vault_list", new { folder = "Projects/a.md" })).IsError);
        Assert.True((await Call(host, "vault_list", new { folder = ".obsidian" })).IsError);
        Assert.True((await Call(host, "vault_list", new { folder = ".." })).IsError);
    }

    [Fact]
    public async Task List_is_capped_at_200_entries()
    {
        for (var i = 0; i < 210; i++) _vault.Write($"Big/n{i:000}.md", "x");
        var (host, _) = NewHost();
        var r = await Call(host, "vault_list", new { folder = "Big" });
        Assert.Contains("Big/n199.md", r.Text);
        Assert.DoesNotContain("Big/n200.md", r.Text);
        Assert.Contains("10 more entries", r.Text);
    }

    // ------------------------------------------------------------------ vault_append

    [Fact]
    public async Task Append_is_not_offered_and_refused_when_writes_are_off()
    {
        var (host, _) = NewHost();
        Assert.DoesNotContain(host.GetTools(), t => t.Name == "vault_append");
        var r = await Call(host, "vault_append", new { path = "x.md", text = "hello" });
        Assert.True(r.IsError);
        Assert.False(File.Exists(_vault.Full("x.md")));
    }

    [Fact]
    public async Task Append_creates_note_and_folders()
    {
        var (host, _) = NewHost(v => v.AllowWrites = true);
        Assert.Contains(host.GetTools(), t => t.Name == "vault_append");
        var r = await Call(host, "vault_append", new { path = "Inbox/New Folder/Idea", text = "first line\nsecond line" });
        Assert.False(r.IsError, r.Text);
        Assert.Contains("Inbox/New Folder/Idea.md", r.Text);
        Assert.Contains("2 lines", r.Text);
        Assert.Equal("first line\nsecond line\n", _vault.Read("Inbox/New Folder/Idea.md"));
    }

    [Theory]
    [InlineData("a\nb", "a\nb\n\nnew\n")]
    [InlineData("a\nb\n", "a\nb\n\nnew\n")]
    [InlineData("a\nb\n\n", "a\nb\n\nnew\n")]
    [InlineData("a\r\nb", "a\r\nb\r\n\r\nnew\r\n")]
    [InlineData("", "new\n")]
    public async Task Append_separates_with_one_blank_line(string existing, string expected)
    {
        _vault.Write("Log.md", existing);
        var (host, _) = NewHost(v => v.AllowWrites = true);
        var r = await Call(host, "vault_append", new { path = "Log.md", text = "\nnew\n" });
        Assert.False(r.IsError, r.Text);
        Assert.Equal(expected, _vault.Read("Log.md"));
    }

    [Fact]
    public async Task Append_reports_resulting_line_count()
    {
        _vault.Write("Log.md", "one\ntwo\nthree\n");
        var (host, _) = NewHost(v => v.AllowWrites = true);
        var r = await Call(host, "vault_append", new { path = "Log.md", text = "four\nfive" });
        Assert.False(r.IsError, r.Text);
        Assert.Contains("Appended 2 lines", r.Text);
        Assert.Contains("now has 6 lines", r.Text);
    }

    [Fact]
    public async Task Append_rejects_bad_paths_types_and_sizes()
    {
        var outside = NewTempDir();
        var (host, _) = NewHost(v => v.AllowWrites = true);
        var rel = Path.GetRelativePath(_vault.Root, outside.Full("x.md")).Replace('\\', '/');
        foreach (var p in new[] { "notes.txt", "script.ps1", "../x.md", rel, outside.Full("x.md"), ".obsidian/x.md", "node_modules/x.md", "a.md:ads", "CON.md" })
            Assert.True((await Call(host, "vault_append", new { path = p, text = "hi" })).IsError, p);
        Assert.False(File.Exists(outside.Full("x.md")));
        Assert.False(Directory.Exists(_vault.Full(".obsidian")));

        Assert.True((await Call(host, "vault_append", new { path = "ok.md", text = new string('a', VaultToolHost.MaxAppendChars + 1) })).IsError);
        Assert.True((await Call(host, "vault_append", new { path = "ok.md", text = "   " })).IsError);
        Assert.False(File.Exists(_vault.Full("ok.md")));

        Assert.False((await Call(host, "vault_append", new { path = "ok.md", text = new string('a', VaultToolHost.MaxAppendChars) })).IsError);
        Assert.False((await Call(host, "vault_append", new { path = "Meeting 2026.10.08", text = "x" })).IsError);
        Assert.True(File.Exists(_vault.Full("Meeting 2026.10.08.md")));
    }

    [Fact]
    public async Task Append_refuses_non_utf8_and_binary_notes()
    {
        File.WriteAllBytes(_vault.Full("Utf16.md"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hello")).ToArray());
        File.WriteAllBytes(_vault.Full("Bin.md"), new byte[] { 1, 0, 2 });
        var (host, _) = NewHost(v => v.AllowWrites = true);
        Assert.True((await Call(host, "vault_append", new { path = "Utf16.md", text = "x" })).IsError);
        Assert.True((await Call(host, "vault_append", new { path = "Bin.md", text = "x" })).IsError);
        Assert.Equal(new byte[] { 1, 0, 2 }, File.ReadAllBytes(_vault.Full("Bin.md")));
    }

    [Fact]
    public async Task Appended_text_is_searchable_immediately()
    {
        var clock = new FakeClock();
        _vault.Write("Log.md", "start");
        var (host, _) = NewHost(v => v.AllowWrites = true);
        host.Clock = clock.Get;
        var before = await Call(host, "vault_search", new { query = "pomegranate" });
        Assert.StartsWith("No notes matched", before.Text);
        Assert.False((await Call(host, "vault_append", new { path = "Log.md", text = "pomegranate harvest" })).IsError);
        var after = await Call(host, "vault_search", new { query = "pomegranate" });
        Assert.Contains("1. Log.md", after.Text);
    }

    // ------------------------------------------------------------------ host behaviour

    [Fact]
    public async Task Host_is_unavailable_without_an_enabled_existing_folder()
    {
        var settings = new AppSettings();
        var host = new VaultToolHost(() => settings);

        Assert.False(host.IsAvailable);
        Assert.Empty(host.GetTools());
        var r = await Call(host, "vault_search", new { query = "x" });
        Assert.True(r.IsError);

        settings.Vault.Path = Path.Combine(_vault.Root, "does-not-exist");
        Assert.False(host.IsAvailable);

        settings.Vault.Path = _vault.Root;
        Assert.True(host.IsAvailable);
        Assert.Equal(new[] { "vault_search", "vault_read", "vault_list" }, host.GetTools().Select(t => t.Name));

        settings.Vault.Enabled = false;
        Assert.False(host.IsAvailable);
        Assert.Empty(host.GetTools());

        settings.Vault.Enabled = true;
        settings.Vault.AllowWrites = true;
        Assert.Equal(new[] { "vault_search", "vault_read", "vault_list", "vault_append" }, host.GetTools().Select(t => t.Name));
    }

    [Fact]
    public void Tool_specs_are_object_schemas_without_long_dashes()
    {
        var (host, _) = NewHost(v => v.AllowWrites = true);
        foreach (var t in host.GetTools())
        {
            Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
            Assert.DoesNotContain('\u2014', t.Description);
            Assert.DoesNotContain('\u2013', t.Description);
            Assert.DoesNotContain('\u2014', t.InputSchema.GetRawText());
        }
        var search = host.GetTools().Single(t => t.Name == "vault_search");
        Assert.Contains("specific keywords", search.Description);
        Assert.Contains("query", search.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Changing_the_vault_folder_rebuilds_the_index()
    {
        _vault.Write("a.md", "first vault banana");
        var second = NewTempDir();
        second.Write("b.md", "second vault banana");
        var (host, settings) = NewHost();

        Assert.Contains("a.md", (await Call(host, "vault_search", new { query = "banana" })).Text);
        settings.Vault.Path = second.Root;
        var r = await Call(host, "vault_search", new { query = "banana" });
        Assert.Contains("b.md", r.Text);
        Assert.DoesNotContain("a.md", r.Text);
    }

    [Fact]
    public async Task Changing_include_extensions_rebuilds_the_index()
    {
        _vault.Write("a.md", "fig");
        _vault.Write("b.txt", "fig");
        var (host, settings) = NewHost();
        Assert.Contains("b.txt", (await Call(host, "vault_search", new { query = "fig" })).Text);
        settings.Vault.IncludeExtensions = new List<string> { ".md" };
        Assert.DoesNotContain("b.txt", (await Call(host, "vault_search", new { query = "fig" })).Text);
    }

    [Fact]
    public async Task Unknown_tool_is_an_error_and_cancellation_throws()
    {
        var (host, _) = NewHost();
        Assert.True((await Call(host, "screenshot", new { })).IsError);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(host, "vault_search", new { query = "x" }, cts.Token));
    }

    [Fact]
    public async Task Settings_function_that_throws_makes_the_host_unavailable()
    {
        var host = new VaultToolHost(() => throw new InvalidOperationException("boom"));
        Assert.False(host.IsAvailable);
        Assert.Empty(host.GetTools());
        Assert.True((await Call(host, "vault_list", new { })).IsError);
    }

    // ------------------------------------------------------------------ Obsidian detector

    [Fact]
    public void Detector_returns_existing_vaults_newest_first()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        var c = NewTempDir();
        var json = JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["id1"] = new { path = a.Root, ts = 100, open = false },
                ["id2"] = new { path = b.Root, ts = 300 },
                ["id3"] = new { path = Path.Combine(c.Root, "gone"), ts = 999, open = true },
                ["id4"] = new { path = c.Root.Replace('\\', '/'), ts = 200, open = true },
                ["id5"] = new { path = a.Root + "\\", ts = 50 },
            },
            updateDisabled = true,
        });
        var file = _vault.Write("obsidian.json", json);
        var vaults = ObsidianVaultDetector.FindVaults(file);
        Assert.Equal(new[] { b.Root, c.Root, a.Root }, vaults);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"vaults\": ")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"vaults\": 5}")]
    [InlineData("{\"vaults\": {\"x\": 5, \"y\": {\"path\": 12}, \"z\": {\"ts\": 1}}}")]
    [InlineData("{\"vaults\": {\"x\": {\"path\": \"\"}}}")]
    public void Detector_tolerates_malformed_json(string json)
    {
        var file = _vault.Write("obsidian.json", json);
        Assert.Empty(ObsidianVaultDetector.FindVaults(file));
    }

    [Fact]
    public void Detector_handles_missing_file_comments_and_string_timestamps()
    {
        Assert.Empty(ObsidianVaultDetector.FindVaults(Path.Combine(_vault.Root, "missing", "obsidian.json")));
        var a = NewTempDir();
        var b = NewTempDir();
        var json = "{ // comment\n \"vaults\": { \"a\": {\"path\": " + JsonSerializer.Serialize(a.Root) + ", \"ts\": \"5\"}, \"b\": {\"path\": " +
                   JsonSerializer.Serialize(b.Root) + ", \"ts\": 7,}, } }";
        var file = _vault.Write("obsidian.json", json);
        Assert.Equal(new[] { b.Root, a.Root }, ObsidianVaultDetector.FindVaults(file));
    }

    [Fact]
    public void Detector_public_entry_point_never_throws()
    {
        var vaults = ObsidianVaultDetector.FindVaults();
        Assert.NotNull(vaults);
        Assert.All(vaults, v => Assert.True(Directory.Exists(v)));
    }
}
