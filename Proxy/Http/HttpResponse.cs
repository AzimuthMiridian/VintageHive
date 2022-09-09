﻿using AngleSharp.Io;
using Fluid;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Net.Sockets;
using System.Text;
using VintageHive.Processors.LocalServer;
using static VintageHive.Proxy.Http.HttpUtilities;

namespace VintageHive.Proxy.Http;

public sealed class HttpResponse
{
    const string SessionCookieName = "sessionid";

    public readonly static List<string> InlineDispositions = new() {
        // Plain Text
        "text/plain",
        "text/html",
        // Images
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif",
        "image/tif",
        // Audio
        "audio/mpeg",
    };

    public HttpRequest Request { get; private set; }

    public Socket Socket => Request.ListenerSocket.RawSocket;

    public Encoding Encoding { get; private set; } = Encoding.UTF8;

    public string Version { get; private set; } = "HTTP/1.1";

    public HttpStatusCode StatusCode { get; private set; } = HttpStatusCode.OK;

    public Dictionary<string, string> Headers { get; private set; } = new();

    public ReadOnlyDictionary<string, string> Cookies => Request.Cookies;

    public Guid SessionId { get; private set; } = Guid.Empty;

    public dynamic Session { get; private set; } = new ExpandoObject();

    public TemplateContext Context { get; }

    public Stream DownloadStream { get; internal set; }

    public byte[] Body { get; internal set; }

    public bool Cache { get; set; } = true;

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(60);

    public bool Handled { get; set; }

    Dictionary<string, string> _deltaCookies = new();

    public HttpResponse(HttpRequest request)
    {
        (Session as INotifyPropertyChanged).PropertyChanged += SessionPropertyChanged;

        Request = request;
        Encoding = request.Encoding ?? Encoding.UTF8;
        Version = request.Version;

        Context = new TemplateContext(new { Request, Response = this });

        if (Request.Cookies.ContainsKey(SessionCookieName))
        {
            if (Guid.TryParse(Request.Cookies[SessionCookieName], out var result))
            {
                SessionId = result;

                Session = Db.Sessions.Get(SessionId);
            }
        }
    }

    public HttpResponse SetBodyString(string body, string type = HttpContentType.Text.Html)
    {
        SetBodyData(Encoding.GetBytes(body), type);

        return this;
    }

    public HttpResponse SetBodyData(byte[] body, string type = HttpContentType.Application.OctetStream)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));

        Headers.AddOrUpdate(HttpHeaderName.ContentLength, Body.Length.ToString());
        Headers.AddOrUpdate(HttpHeaderName.ContentType, type);

        return this;
    }

    public HttpResponse SetBodyFileStream(FileStream stream, string type = HttpContentType.Application.OctetStream)
    {
        Cache = false; // DO NOT OVERLOAD SQLite

        DownloadStream = stream;

        var disposition = "attachment";

        if (InlineDispositions.Contains(type))
        {
            disposition = "inline";
        }

        var contentDisposition = $"{disposition}; filename=\"{Path.GetFileName(stream.Name)}\"";

        Headers.AddOrUpdate(HttpHeaderName.ContentDisposition, contentDisposition);
        Headers.AddOrUpdate(HttpHeaderName.ContentLength, DownloadStream.Length.ToString());
        Headers.AddOrUpdate(HttpHeaderName.ContentType, type);

        return this;
    }

    public HttpResponse SetEncoding(Encoding encoding)
    {
        Encoding = encoding;

        return this;
    }

    public HttpResponse SetStatusCode(HttpStatusCode statusCode)
    {
        StatusCode = statusCode;

        return this;
    }

    public HttpResponse SetOk()
    {
        StatusCode = HttpStatusCode.OK;

        return this;
    }

    public HttpResponse SetNotFound()
    {
        StatusCode = HttpStatusCode.NotFound;

        return this;
    }

    public HttpResponse SetFound(string foundUri)
    {
        StatusCode = HttpStatusCode.Found;

        Headers.AddOrUpdate(HttpHeaderName.Location, foundUri);

        return this;
    }

    public HttpResponse SetCookie(string name, string content)
    {
        if (Cookies.ContainsKey(name) && Cookies[name] == content)
        {
            return this;
        }

        _deltaCookies.Add(name, content);

        return this;
    }

    public byte[] GetResponseEncodedData()
    {
        var outputBuilder = new StringBuilder();

        outputBuilder.Append($"{Version} {(int)StatusCode} {StatusCode}{HttpSeperator}");

        foreach (var newCookie in _deltaCookies)
        {
            Headers.Add("Set-Cookie", $"{newCookie.Key}={newCookie.Value}");
        }

        foreach (var header in Headers)
        {
            outputBuilder.Append($"{header.Key}: {header.Value}{HttpSeperator}");
        }

        outputBuilder.Append($"{HttpHeaderName.Server}: VintageHive/{HttpProxy.ApplicationVersion}"); // Fuck them

        outputBuilder.Append(HttpBodySeperator);

        var headerData = Encoding.GetBytes(outputBuilder.ToString());

        if (Body != null && Body.Length > 0)
        {
            return headerData.Concat(Body).ToArray();
        }

        return headerData;
    }

    private void SessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SessionId == Guid.Empty)
        {
            SessionId = Guid.NewGuid();

            SetCookie(SessionCookieName, SessionId.ToString() + "; HttpOnly");
        }

        Display.WriteLog($"{e.PropertyName} changed!");
    }
}