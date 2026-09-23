using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// One response, described once and written in either of the protocol's two
/// formats.
/// </summary>
/// <remarks>
/// <b>Both formats are mandatory and <c>f=xml</c> is the default</b> — a client
/// that omits <c>f</c> gets XML, and several still do. The two are not
/// mechanically related: XML puts scalars in attributes and children in
/// elements, JSON puts everything in properties, and a repeated child is a
/// sibling element in one and an array in the other. So a response is written
/// against this interface and the format picks the implementation.
///
/// This is the one interface in the Subsonic layer that earns itself, and it
/// earns itself on the day it is written: it has two implementations rather than
/// one, and the alternatives are reflection over response records or writing
/// every response twice.
///
/// <b>Attributes come before children.</b> JSON does not care and XML cannot be
/// written any other way, so <see cref="XmlWriter"/> is the stricter of the two
/// and throws on a violation — which is the right way round, because a caller
/// that gets it wrong fails in tests rather than only for the clients asking for
/// XML.
///
/// <b>A null is not written at all.</b> Subsonic has no way to say "unknown", so
/// an absent attribute is the only honest answer for a duration nobody has
/// probed or a year MusicBrainz does not hold. Writing a zero would be a claim.
/// </remarks>
internal interface ISubsonicWriter
{
    void Attr(string name, string? value);

    void Attr(string name, bool? value);

    void Attr(string name, int? value);

    void Attr(string name, long? value);

    void Attr(string name, DateTimeOffset? value);

    /// <summary>One nested object.</summary>
    void Child(string name, Action<ISubsonicWriter> body);

    /// <summary>A repeated child: sibling elements in XML, an array in JSON.</summary>
    void Children<T>(string name, IEnumerable<T> items, Action<ISubsonicWriter, T> body);

    /// <summary>A repeated scalar, which only the extension list needs.</summary>
    void Numbers(string name, IEnumerable<int> values);
}

/// <summary>
/// The <c>subsonic-response</c> envelope, as an <see cref="IResult"/>.
/// </summary>
/// <remarks>
/// <b>Every response is HTTP 200, including every error.</b> That is the
/// protocol: a refusal is <c>status="failed"</c> with a numbered
/// <c>&lt;error&gt;</c> inside an otherwise ordinary envelope, and a client
/// reading a 401 shows "server unreachable" rather than "wrong password". The
/// one exception is <c>/rest/stream</c>, which answers with bytes and is not
/// written through here at all.
///
/// The document is built into memory and then written in one go. Kestrel
/// disallows synchronous writes to the response body, <see cref="XmlWriter"/>'s
/// async surface is partial, and these documents are a list of albums — so
/// buffering is both the simpler and the smaller of the two options.
/// </remarks>
internal sealed class SubsonicResult : IResult
{
    /// <summary>The protocol version this server claims.</summary>
    internal const string ApiVersion = "1.16.1";

    /// <summary>What <c>type</c> reports. Clients key their quirks off this.</summary>
    internal const string ServerName = "Fonoteca";

    private static readonly string Version =
        typeof(SubsonicResult).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Action<ISubsonicWriter>? _content;
    private readonly int? _errorCode;
    private readonly string? _errorMessage;

    private SubsonicResult(
        Action<ISubsonicWriter>? content,
        int? errorCode,
        string? errorMessage)
    {
        _content = content;
        _errorCode = errorCode;
        _errorMessage = errorMessage;
    }

    /// <summary>An empty success, which is what several endpoints are.</summary>
    internal static SubsonicResult Ok() => new(null, null, null);

    /// <summary>A success carrying one named child object.</summary>
    internal static SubsonicResult Ok(string child, Action<ISubsonicWriter> body) =>
        new(writer => writer.Child(child, body), null, null);

    /// <summary>
    /// A success whose child is a repeated one, which only the extension list is.
    /// </summary>
    internal static SubsonicResult OkList<T>(
        string child,
        IEnumerable<T> items,
        Action<ISubsonicWriter, T> body) =>
        new(writer => writer.Children(child, items, body), null, null);

    /// <summary>
    /// A refusal, by the protocol's own code.
    /// </summary>
    /// <remarks>
    /// 0 generic, 10 a required parameter is missing, 20 the client is too old,
    /// 30 the server is too old, 40 wrong credentials, 50 not authorised,
    /// 70 not found. Clients render several of these specifically, which is why
    /// none of them is collapsed into 0.
    /// </remarks>
    internal static SubsonicResult Error(int code, string message) =>
        new(null, code, message);

    /// <summary>Which format a request asked for.</summary>
    internal static string FormatOf(HttpContext context) =>
        context.Request.Query["f"].ToString().ToLowerInvariant() switch
        {
            "json" => "json",
            "jsonp" => "jsonp",
            _ => "xml",
        };

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var format = FormatOf(httpContext);

        using var buffer = new MemoryStream();

        if (format == "xml")
        {
            WriteXml(buffer);
        }
        else
        {
            WriteJson(buffer, format == "jsonp" ? Callback(httpContext) : string.Empty);
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = format switch
        {
            "json" => "application/json; charset=utf-8",
            "jsonp" when Callback(httpContext).Length > 0 => "text/javascript; charset=utf-8",
            "jsonp" => "application/json; charset=utf-8",
            _ => "application/xml; charset=utf-8",
        };

        // Whatever it is, it is not for a browser to reinterpret.
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        httpContext.Response.ContentLength = buffer.Length;

        buffer.Position = 0;
        await buffer.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The JSONP callback, if it is one.
    /// </summary>
    /// <remarks>
    /// Checked rather than echoed. What comes back is a script on this
    /// application's own origin, and the protocol only ever needs an identifier
    /// there — anything else is somebody writing the body of the response. An
    /// unusable callback falls back to plain JSON, which is a client bug the
    /// client can see rather than a gadget it cannot.
    /// </remarks>
    private static string Callback(HttpContext context)
    {
        var sent = context.Request.Query["callback"].ToString();

        if (sent.Length is 0 or > 128) return string.Empty;

        foreach (var character in sent)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '$' or '.'))
            {
                return string.Empty;
            }
        }

        return sent;
    }

    private void WriteXml(Stream destination)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8,
            Indent = false,
            OmitXmlDeclaration = false,
            CloseOutput = false,
        };

        using var xml = XmlWriter.Create(destination, settings);

        xml.WriteStartElement("subsonic-response", "http://subsonic.org/restapi");

        var writer = new XmlSubsonicWriter(xml);
        WriteEnvelope(writer);

        xml.WriteEndElement();
        xml.Flush();
    }

    private void WriteJson(Stream destination, string callback)
    {
        if (!string.IsNullOrEmpty(callback))
        {
            var prefix = Utf8.GetBytes(callback + "(");
            destination.Write(prefix, 0, prefix.Length);
        }

        using (var json = new Utf8JsonWriter(destination))
        {
            json.WriteStartObject();
            json.WriteStartObject("subsonic-response");

            WriteEnvelope(new JsonSubsonicWriter(json));

            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
        }

        if (!string.IsNullOrEmpty(callback))
        {
            var suffix = Utf8.GetBytes(");");
            destination.Write(suffix, 0, suffix.Length);
        }
    }

    private void WriteEnvelope(ISubsonicWriter writer)
    {
        writer.Attr("status", _errorCode is null ? "ok" : "failed");
        writer.Attr("version", ApiVersion);
        writer.Attr("type", ServerName);
        writer.Attr("serverVersion", Version);
        writer.Attr("openSubsonic", true);

        if (_errorCode is { } code)
        {
            writer.Child("error", error =>
            {
                error.Attr("code", code);
                error.Attr("message", _errorMessage);
            });

            return;
        }

        _content?.Invoke(writer);
    }

    private sealed class JsonSubsonicWriter(Utf8JsonWriter json) : ISubsonicWriter
    {
        public void Attr(string name, string? value)
        {
            if (value is not null) json.WriteString(name, value);
        }

        public void Attr(string name, bool? value)
        {
            if (value is { } set) json.WriteBoolean(name, set);
        }

        public void Attr(string name, int? value)
        {
            if (value is { } set) json.WriteNumber(name, set);
        }

        public void Attr(string name, long? value)
        {
            if (value is { } set) json.WriteNumber(name, set);
        }

        public void Attr(string name, DateTimeOffset? value)
        {
            if (value is { } set) json.WriteString(name, Iso(set));
        }

        public void Child(string name, Action<ISubsonicWriter> body)
        {
            ArgumentNullException.ThrowIfNull(body);

            json.WriteStartObject(name);
            body(this);
            json.WriteEndObject();
        }

        public void Children<T>(string name, IEnumerable<T> items, Action<ISubsonicWriter, T> body)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(body);

            json.WriteStartArray(name);

            foreach (var item in items)
            {
                json.WriteStartObject();
                body(this, item);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        public void Numbers(string name, IEnumerable<int> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            json.WriteStartArray(name);

            foreach (var value in values)
            {
                json.WriteNumberValue(value);
            }

            json.WriteEndArray();
        }
    }

    private sealed class XmlSubsonicWriter(XmlWriter xml) : ISubsonicWriter
    {
        public void Attr(string name, string? value)
        {
            if (value is not null) xml.WriteAttributeString(name, value);
        }

        public void Attr(string name, bool? value)
        {
            if (value is { } set) xml.WriteAttributeString(name, set ? "true" : "false");
        }

        public void Attr(string name, int? value)
        {
            if (value is { } set)
            {
                xml.WriteAttributeString(name, set.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void Attr(string name, long? value)
        {
            if (value is { } set)
            {
                xml.WriteAttributeString(name, set.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void Attr(string name, DateTimeOffset? value)
        {
            if (value is { } set) xml.WriteAttributeString(name, Iso(set));
        }

        public void Child(string name, Action<ISubsonicWriter> body)
        {
            ArgumentNullException.ThrowIfNull(body);

            xml.WriteStartElement(name);
            body(this);
            xml.WriteEndElement();
        }

        public void Children<T>(string name, IEnumerable<T> items, Action<ISubsonicWriter, T> body)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(body);

            foreach (var item in items)
            {
                xml.WriteStartElement(name);
                body(this, item);
                xml.WriteEndElement();
            }
        }

        public void Numbers(string name, IEnumerable<int> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            foreach (var value in values)
            {
                xml.WriteElementString(name, value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
