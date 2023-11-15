using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using ReverseMarkdown;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("tests")]

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();
var wikiSecret = configuration["WIKI_SECRET"] ?? string.Empty;

var httpClientHandler = new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = true,
};
var client = new HttpClient(httpClientHandler)
{
    BaseAddress = new Uri("https://leowiki.htl-leonding.ac.at/")
};

var dataDir = Path.Combine("data");
if (!Directory.Exists(dataDir))
{
    Directory.CreateDirectory(dataDir);
}

await client.Login(wikiSecret);
var (_, _, links) = await client.GetPage("start") ?? throw new NotImplementedException();
var visited = new HashSet<string>();
var compendium = new Dictionary<string, List<Page>>();
while (links.Any(l => !visited.Contains(l)))
{
    var link = links.First(l => !visited.Contains(l));
    visited.Add(link);
    var p = await client.GetPage(link);
    if (p == null) { continue; }
    var (title, page, newLinks) = p;

    if (link != "start")
    {
        if (compendium.TryGetValue(link[..link.IndexOf(':')], out var pages))
        {
            pages.Add(p);
        }
        else
        {
            compendium.Add(link[..link.IndexOf(':')], [p]);
        }

        var fileName = link.Replace(":", "_").Replace("/", "_");
        await File.WriteAllTextAsync(Path.Combine(dataDir, $"{fileName}.md"), page);
        foreach (var newLink in newLinks)
        {
            if (!visited.Contains(newLink))
            {
                links.Add(newLink);
            }
        }
    }
}

foreach (var section in compendium.Keys
    .Where(k => !new string[] { "archive", "werkstaette", "tutorial" }.Contains(k)))
{
    var pages = compendium[section];

    var summary = new StringBuilder();
    foreach (var page in pages)
    {
        if (summary.Length > 0) { summary.Append("\n\n"); }
        summary.Append(page.Content);
    }

    File.WriteAllText(
        Path.Combine(dataDir, $"summary_{section}.md"),
        summary.ToString());
}

record Link(string Url, bool Visited = false);

record Page(string Title, string Content,
    [property: JsonIgnore] HashSet<string> Links);

static partial class LeoWikiExtensions
{
    public static async Task Login(this HttpClient client, string wikiSecret)
    {
        var res = await client.PostAsync("doku.php?id=start", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                { "sectok", "" },
                { "id", "start" },
                { "do", "login" },
                { "u", "r.stropek" },
                { "p", wikiSecret },
                }
            ));
        Debug.Assert(res.StatusCode == HttpStatusCode.Found);
        Debug.Assert(res.Headers.TryGetValues("Set-Cookie", out _));
    }

    public static async Task<Page?> GetPage(this HttpClient client, string url)
    {
        string content;
        Console.WriteLine($"Getting {url}");
        var res = await client.GetAsync($"doku.php?id={url}&do=export_xhtml");
        Debug.Assert(res.StatusCode == HttpStatusCode.OK);
        content = await res.Content.ReadAsStringAsync();
        if (content.Contains("Dieses Thema existiert noch nicht") || content.Contains("Diese Seite existiert nicht mehr"))
        {
            return null;
        }

        try
        {
            var (cleanedContent, links) = CleanupAndExtractLinks(content);

            var converter = new Converter();
            var markdownContent = converter.Convert(cleanedContent);

            string title;
            if (markdownContent.Contains('\n'))
            {
                title = markdownContent[..markdownContent.IndexOf('\n')];
            }
            else
            {
                title = markdownContent;
            }

            return new Page(title, markdownContent, [.. links]);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while processing {url}: {ex}");
            return null;
        }
    }

    internal static (string content, List<string> links) CleanupAndExtractLinks(string content)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        List<string> links = [];

        var head = doc.DocumentNode.SelectSingleNode("//head");
        head?.Remove();

        var specificDiv = doc.DocumentNode.SelectSingleNode("//div[@id='dw__toc']");
        specificDiv?.Remove();

        var comments = doc.DocumentNode.SelectNodes("//comment()");
        if (comments != null)
        {
            foreach (var comment in doc.DocumentNode.SelectNodes("//comment()"))
            {
                comment.Remove();
            }
        }

        foreach (var node in doc.DocumentNode.Descendants())
        {
            node.Attributes.Remove("class");
            node.Attributes.Remove("id");
        }

        var hrefs = doc.DocumentNode.SelectNodes("//a[@href]");
        if (hrefs != null)
        {
            foreach (var link in hrefs)
            {
                const string prefix = "/doku.php?id=";
                var href = link.GetAttributeValue("href", "");
                if (href.StartsWith(prefix))
                {
                    if (href.Contains('#'))
                    {
                        href = href[..href.IndexOf('#')];
                    }
                    if (href.Contains('&'))
                    {
                        href = href[..href.IndexOf('&')];
                    }
                    links.Add(href[prefix.Length..]);
                }
            }
        }

        var divs = doc.DocumentNode.SelectNodes("//div");
        while (divs != null && divs.Count != 0)
        {
            foreach (var div in divs.ToList())
            {
                var parentNode = div.ParentNode;
                foreach (var child in div.ChildNodes)
                {
                    parentNode.InsertBefore(child, div);
                }
                parentNode.RemoveChild(div);
            }
            divs = doc.DocumentNode.SelectNodes("//div");
        }


        return (doc.DocumentNode.OuterHtml, links);
    }
}