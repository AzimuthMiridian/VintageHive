﻿using Fluid;
using HeyRed.Mime;
using HtmlAgilityPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SmartReader;
using System.Net;
using VintageHive.Utilities;
using Image = SixLabors.ImageSharp.Image;

namespace VintageHive.Processors.LocalServer.Controllers;

internal class HiveController : Controller
{
    public override async Task CallInitial(string rawPath)
    {
        await Task.Delay(0);

        Response.Context.SetValue("menu", new [] {
            "Download",
            "Search",
            "Viewer",
            "News",
            "Weather",
            "Settings",
            "Help"
        });
    }

    [Controller("/index.html")]
    public async Task Index()
    {
        Response.Context.SetValue("directory_hotlinks", Mind.Db.LinksGetAll());

        Response.Context.SetValue("directory_protohttp", await ProtoWebUtils.GetAvailableHttpSites());

        Response.Context.SetValue("directory_protoftp", await ProtoWebUtils.GetAvailableFtpSites());
    }

    [Controller("/download.html")]
    public async Task Download()
    {
        await Task.Delay(0);

        var repos = RepoUtils.Get();

        Response.Context.SetValue("repos", repos);

        if (Request.QueryParams.ContainsKey("repo") && repos.ContainsKey(Request.QueryParams["repo"]))
        {
            var path = Request.QueryParams.ContainsKey("path") ? Request.QueryParams["path"] : "/";

            // TODO: LOL Security ^.^;;
            if (!Path.EndsInDirectorySeparator(path) || !path.ConfirmValidPath() || path.Contains(".."))
            {
                path = "/";
            }

            Response.Context.SetValue("path", path);

            var reposhortname = Request.QueryParams["repo"];
            var repo = repos[reposhortname];

            Response.Context.SetValue("reponame", repo.Item1);
            Response.Context.SetValue("reposhortname", reposhortname);

            var directoryInfo = new DirectoryInfo(repo.Item2);

            var isRootPath = true;

            if (path.Length > 3)
            {
                var cadidates = directoryInfo.GetDirectories(path[1..^1]).FirstOrDefault();

                if (cadidates != null)
                {
                    directoryInfo = cadidates;

                    isRootPath = false;
                }
            }

            if (Request.QueryParams.ContainsKey("file"))
            {
                var file = Request.QueryParams["file"];

                var filePath = Path.Combine(directoryInfo.FullName, file);

                var fileInfo = new FileInfo(filePath);

                if (fileInfo.Exists)
                {
                    var mimetype = MimeTypesMap.GetMimeType(file);

                    var fileHandle = fileInfo.OpenRead();

                    Response.SetBodyFileStream(fileHandle, mimetype);

                    Response.Handled = true;
                }
            }

            Response.Context.SetValue("isroot", isRootPath);
            
            if (!isRootPath)
            {
                var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

                if (pathSegments.Count > 0)
                {
                    pathSegments.RemoveAt(pathSegments.Count - 1);
                }

                var output = '/' + string.Join('/', pathSegments);

                if (pathSegments.Count > 0)
                {
                    output += '/';
                }

                Response.Context.SetValue("parentpath", output);
            }

            var dirs = directoryInfo.EnumerateDirectories();
            var files = directoryInfo.EnumerateFiles();

            Response.Context.SetValue("dirs", dirs);
            Response.Context.SetValue("dirs_total", dirs.Count());
            Response.Context.SetValue("files", files);
            Response.Context.SetValue("files_total", files.Count());
        }
    }

    [Controller("/search.html")]
    public async Task Search()
    {
        if (Request.QueryParams.ContainsKey("q"))
        {
            var keywords = Request.QueryParams["q"];

            Response.Context.SetValue("keywords", keywords);

            var results = await DDGUtils.Search(keywords);

            Response.Context.SetValue("results", results);
        }
    }

    [Controller("/viewer.html")]
    public async Task Viewer()
    {
        if (Request.QueryParams.ContainsKey("url"))
        {
            var url = Request.QueryParams["url"];

            Response.Context.SetValue("url", url);

            Response.Context.SetValue("type", "document");

            var mimetype = MimeTypesMap.GetMimeType(url);

            if (mimetype.StartsWith("image"))
            {
                Response.Context.SetValue("type", "image");
                Response.Context.SetValue("image", $"/api/image/fetch?url={url}");
            }
            else
            {
                var result = await Clients.GetReaderOutput(url);

                var articleDocument = new HtmlDocument();

                if (result == null)
                {
                    Response.Context.SetValue("result", "No worky! :(");

                    return;
                }

                Response.Context.SetValue("doctitle", result.Title);

                articleDocument.LoadHtml(result.Content);
                
                NormalizeAnchorLinks(articleDocument);

                NormalizeImages(articleDocument);

                Response.Context.SetValue("document", articleDocument.DocumentNode.OuterHtml);
            }
        }
    }

    private static void NormalizeAnchorLinks(HtmlDocument articleDocument)
    {
        var anchorNodes = articleDocument.DocumentNode.SelectNodes("//a");

        if (anchorNodes != null)
        {
            foreach (var node in anchorNodes)
            {
                var href = node.GetAttributeValue("href", "");

                if (!href.Any() || href[0] == '#')
                {
                    continue;
                }

                href = $"/viewer.html?url={WebUtility.UrlDecode(href)}";

                node.SetAttributeValue("href", href);

                node.SetAttributeValue("target", "");
            }
        }
    }

    private static void NormalizeImages(HtmlDocument articleDocument)
    {
        var imgNodes = articleDocument.DocumentNode.SelectNodes("//img");

        if (imgNodes != null)
        {
            foreach (var node in imgNodes)
            {
                var img = node.GetAttributeValue("src", "");

                if (string.IsNullOrEmpty(img))
                {
                    img = node.GetAttributeValue("data-src", "");
                }

                if (string.IsNullOrEmpty(img))
                {
                    img = node.GetAttributeValue("data-src-medium", "");
                }

                var imgUri = new Uri(img.StartsWith("//") ? $"https:{img}" : img);

                var imageLinkNode = HtmlNode.CreateNode($"<a href=\"/viewer.html?url={Uri.EscapeDataString(imgUri.ToString())}\"><img src=\"/api/image/fetch?url={Uri.EscapeDataString(imgUri.ToString())}\" border=\"0\"></a>");

                if (node.ParentNode.Name == "picture")
                {
                    var pictureEl = node.ParentNode;

                    pictureEl.ParentNode.InsertAfter(imageLinkNode, pictureEl);

                    pictureEl.Remove();
                }

                node.ParentNode.InsertAfter(imageLinkNode, node);

                node.Remove();
            }
        }
    }

    [Controller("/news.html")]
    public async Task News()
    {
        var articles = await Clients.GetGoogleArticles("US");

        Response.Context.SetValue("articles", articles);
    }

    [Controller("/weather.html")]
    public async Task Weather()
    {
        var geoipLocation = Mind.Db.ConfigGet<string>(ConfigNames.Location);

        var location = Request.QueryParams.ContainsKey("location") ? Request.QueryParams["location"] : geoipLocation;

        if (string.IsNullOrEmpty(location))
        {
            location = geoipLocation;
        }

        var weatherData = await Clients.GetWeatherData(location);

        var stringLocation = location;

        if (stringLocation == geoipLocation)
        {
            stringLocation = "Your Location";
        }

        Response.Context.SetValue("weather", weatherData);

        Response.Context.SetValue("weather_location", stringLocation);
    }

    [Controller("/settings.html")]
    public async Task Settings()
    {
        await Task.Delay(0);

        var isInternetArchiveEnabled = Mind.Db.ConfigLocalGet<bool>(Request.ListenerSocket.RemoteIP, ConfigNames.InternetArchive);

        Response.Context.SetValue("ia_years", InternetArchiveProcessor.ValidYears);
        Response.Context.SetValue("ia_toggle", isInternetArchiveEnabled);
        Response.Context.SetValue("ia_current", Mind.Db.ConfigLocalGet<int>(Request.ListenerSocket.RemoteIP, ConfigNames.InternetArchiveYear));

        var isProtoWebEnabled = Mind.Db.ConfigLocalGet<bool>(Request.ListenerSocket.RemoteIP, ConfigNames.ProtoWeb);

        Response.Context.SetValue("proto_toggle", isProtoWebEnabled);
    }

    [Controller("/settings/users.html")]
    public async Task SettingsUser()
    {
        await Task.Delay(0);

        var users = Mind.Db.UserList();

        Response.Context.SetValue("users", users);
    }

    [Controller("/api/user/exist")]
    public async Task UserExists()
    {
        await Task.Delay(0);

        string username;

        if (Request.QueryParams.ContainsKey("username"))
        {
            username = Request.QueryParams["username"];
        }
        else if (Request.FormData.ContainsKey("username"))
        {
            username = Request.FormData["username"];
        }
        else
        {
            return;
        }

        var result = Mind.Db.UserExistsByUsername(username);

        Response.SetBodyString(result.ToString().ToLower(), "text/plain");

        Response.Handled = true;
    }

    [Controller("/api/user/create")]
    public async Task UserCreate()
    {
        await Task.Delay(0);

        if (!Request.FormData.ContainsKey("username") || !Request.FormData.ContainsKey("password"))
        {
            return;
        }

        var username = Request.FormData["username"];

        var password = Request.FormData["password"];

        var result = Mind.Db.UserCreate(username, password);

        Response.SetBodyString(result.ToString().ToLower(), "text/plain").SetFound();
    }

    [Controller("/api/image/fetch")]
    public async Task ImageFetch()
    {
        if (!Request.QueryParams.ContainsKey("url"))
        {
            Response.SetNotFound();

            Response.Handled = true;

            return;
        }

        Image image;

        try
        {
            var fetchUri = new Uri(Request.QueryParams["url"]);

            byte[] _imageData;

            using var httpClient = Clients.GetHttpClient(Request);

            _imageData = await httpClient.GetByteArrayAsync(fetchUri);

            image = Image.Load(_imageData);
        }
        catch
        {
            Response.SetNotFound();

            Response.Handled = true;

            return;
        }

        if (image.Size().Width > 800)
        {
            image.Mutate(x => x.Resize(800, 0));
        }

        var memoryStream = new MemoryStream();

        await image.SaveAsJpegAsync(memoryStream);

        Response.SetBodyData(memoryStream.ToArray(), "image/jpeg");

        Response.Handled = true;
    }   

    [Controller("/api/ia/setyear")]
    public async Task InternetArchiveSetYear()
    {
        await Task.Delay(0);

        if (!Request.FormData.ContainsKey("year"))
        {
            return;
        }

        if (int.TryParse(Request.FormData["year"], System.Globalization.NumberStyles.Integer, null, out var year))
        {
            var currentYear = Mind.Db.ConfigLocalGet<int>(Request.ListenerSocket.RemoteIP, ConfigNames.InternetArchiveYear);

            if (year != currentYear && InternetArchiveProcessor.ValidYears.Contains(year))
            {
                Mind.Db.ConfigLocalSet(Request.ListenerSocket.RemoteIP, ConfigNames.InternetArchiveYear, year);
            }

            Response.SetFound();
        }
    }
}
