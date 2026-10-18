using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MicMini.Desktop.Services;

public sealed record DjiFirmwareEndpoints(Uri AllFile, Uri Config, Uri Firmware, Uri Download);
public sealed record DjiFirmwareCatalog(string ProductId, DateTimeOffset CheckedAtUtc, IReadOnlyList<DjiFirmwareRelease> Releases);
public sealed record DjiFirmwareRelease(string Version, string ReleaseNotes, IReadOnlyList<DjiFirmwareAsset> Assets);

/// <summary>An immutable asset issued from an HTTPS catalog response. Never a device command.</summary>
public sealed class DjiFirmwareAsset
{
    internal DjiFirmwareAsset(string productId, string version, string fileName, Uri downloadUri,
        long expectedLength, string md5, Uri sourceUri)
        => (ProductId, Version, FileName, DownloadUri, ExpectedLength, Md5, SourceUri) =
            (productId, version, fileName, downloadUri, expectedLength, md5, sourceUri);
    public string ProductId { get; }
    public string Version { get; }
    public string FileName { get; }
    public Uri DownloadUri { get; }
    public long ExpectedLength { get; }
    public string Md5 { get; }
    public Uri SourceUri { get; }
}

public sealed class FirmwareCatalogException(string message) : IOException(message);

/// <summary>
/// Read-only official firmware metadata and downloads, independent of APK/Python and device access.
/// Application signing material is deliberately excluded from diagnostics and exceptions.
/// </summary>
public sealed class DjiFirmwareCatalogClient : IDisposable
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private static readonly Uri DiscoveryUri = new("https://mydjiflight.dji.com/loadconfig/geturl");
    private readonly HttpClient _http;
    private readonly HashSet<DjiFirmwareAsset> _issuedAssets = [];
    private DjiFirmwareEndpoints? _endpoints;

    public DjiFirmwareCatalogClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    internal DjiFirmwareCatalogClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MicMiniDesktop/0.3.1");
    }

    public async Task<DjiFirmwareEndpoints> DiscoverAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var query = AddQuery(DiscoveryUri, new Dictionary<string, string>
        {
            ["time"] = timestamp, ["os"] = "android", ["version"] = "2.12.1",
            ["signature"] = Sign(timestamp + "android" + "2.12.1", false)
        });
        using var json = await ReadJsonAsync(query, timeout.Token).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Number || state.GetInt32() != 0)
            throw new FirmwareCatalogException("DJI 固件服务发现失败，未取得有效的官方版本接口。");
        if (!root.TryGetProperty("apis", out var apis) || apis.ValueKind != JsonValueKind.Object)
            throw new FirmwareCatalogException("DJI 固件服务未返回接口清单。");
        return _endpoints = new(ReadApiUri(apis, "allfile"), ReadApiUri(apis, "config"),
            ReadApiUri(apis, "firm"), ReadApiUri(apis, "down"));
    }

    public async Task<DjiFirmwareCatalog> GetCatalogAsync(string productId, CancellationToken token = default)
    {
        if (productId is not ("dm300" or "dm202b"))
            throw new FirmwareCatalogException("设备产品编号尚未确证对应的官方固件清单；不能据此判断已是最新版本。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var endpoints = _endpoints ?? await DiscoverAsync(timeout.Token).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["product_id"] = productId, ["signature"] = Sign(productId, true),
            ["token"] = "", ["language"] = "zh_cn", ["country"] = "CN", ["force_update"] = "false"
        };
        using var json = await ReadJsonAsync(endpoints.AllFile, timeout.Token, parameters).ConfigureAwait(false);
        var catalog = ParseCatalog(productId, endpoints.AllFile, json.RootElement);
        var releases = catalog.Releases.OrderByDescending(r => Version.Parse(r.Version)).ToArray();
        var latest = releases[0];
        var assets = await FetchReleaseAssetsAsync(productId, latest.Version, endpoints, timeout.Token).ConfigureAwait(false);
        releases[0] = latest with { Assets = assets };
        return catalog with { Releases = Array.AsReadOnly(releases) };
    }

    private DjiFirmwareCatalog ParseCatalog(string productId, Uri source, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
            throw new FirmwareCatalogException($"DJI 固件服务未返回 {productId} 的有效版本清单（状态 {status.GetInt32()}）。这不代表设备已是最新版本。");
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root.GetArrayLength() > 1000)
            throw new FirmwareCatalogException("DJI 固件清单为空或格式无法确认；未生成升级候选。");
        var releases = new List<DjiFirmwareRelease>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("product_version", out var version) ||
                version.ValueKind != JsonValueKind.String || !Version.TryParse(version.GetString(), out _))
                throw new FirmwareCatalogException("DJI 固件清单中的版本字段无效。");
            var notes = "";
            if (item.TryGetProperty("release_note", out var note) && note.ValueKind == JsonValueKind.Object &&
                note.TryGetProperty("zh_cn", out var chinese) && chinese.ValueKind == JsonValueKind.String)
                notes = (chinese.GetString() ?? "").Replace("\\n", "\n").Replace("\\t", "\t");
            // Version records are not package URLs. Downloads require the separately verified configuration.
            releases.Add(new(version.GetString()!, notes, []));
        }
        return new(productId, DateTimeOffset.UtcNow, releases.AsReadOnly());
    }

    private async Task<IReadOnlyList<DjiFirmwareAsset>> FetchReleaseAssetsAsync(string productId, string version,
        DjiFirmwareEndpoints endpoints, CancellationToken token)
    {
        var configParameters = new Dictionary<string, string>
        {
            ["product_id"] = productId, ["product_version"] = version, ["token"] = "",
            ["signature"] = Sign(productId + version, true)
        };
        var configBytes = await ReadMetadataAsync(endpoints.Config, token, configParameters, true).ConfigureAwait(false);
        XDocument config;
        try
        {
            using var input = new MemoryStream(configBytes);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumMetadataBytes, MaxCharactersFromEntities = 0
            });
            config = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException) { throw new FirmwareCatalogException("官方固件配置不是支持的 XML，不能生成下载候选。"); }
        var devices = config.Root?.Name == "dji" ? config.Root.Elements("device").ToArray() : [];
        if (devices.Length != 1 || (string?)devices[0].Attribute("id") != productId)
            throw new FirmwareCatalogException("官方固件配置的产品编号与请求不匹配。");
        var firmware = devices[0].Element("firmware");
        var releases = firmware?.Elements("release").Where(x => (string?)x.Attribute("version") == version).ToArray() ?? [];
        if ((string?)firmware?.Attribute("formal") != version || releases.Length != 1)
            throw new FirmwareCatalogException("官方固件配置没有唯一匹配的正式版本。");
        var modules = releases[0].Elements("module").ToArray();
        if (modules.Length is 0 or > 128) throw new FirmwareCatalogException("官方固件配置的模块清单无效。");
        var assets = new List<DjiFirmwareAsset>();
        foreach (var module in modules)
        {
            var moduleId = (string?)module.Attribute("id") ?? "";
            var moduleVersion = (string?)module.Attribute("version") ?? "";
            var name = module.Value.Trim();
            var md5 = (string?)module.Attribute("md5") ?? "";
            if (moduleId.Length != 4 || !moduleId.All(char.IsAsciiDigit) || !Version.TryParse(moduleVersion, out _) ||
                !long.TryParse((string?)module.Attribute("size"), out var length) || length <= 0 ||
                length > FirmwarePackageInspector.MaximumFileBytes || md5.Length != 32 || !md5.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 240 || name != Path.GetFileName(name) ||
                name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl))
                throw new FirmwareCatalogException("官方固件模块缺少有效的名称、大小、版本或摘要。");
            var downloadParameters = new Dictionary<string, string>
            {
                ["product_id"] = productId, ["product_version"] = version, ["module_id"] = moduleId,
                ["module_version"] = moduleVersion, ["filename"] = name, ["token"] = "",
                ["signature"] = Sign(productId + version + moduleId + moduleVersion, true)
            };
            using var downloadJson = await ReadJsonAsync(endpoints.Download, token, downloadParameters).ConfigureAwait(false);
            var root = downloadJson.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0 ||
                !root.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url))
                throw new FirmwareCatalogException("官方服务未返回模块下载地址。");
            ValidateOfficialUri(url, false);
            if (Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? "") != name)
                throw new FirmwareCatalogException("官方模块下载地址的文件名与配置不匹配。");
            assets.Add(new(productId, version, name, url, length, md5.ToUpperInvariant(), endpoints.Config));
        }
        // Keep records private until the entire response has passed validation.
        lock (_issuedAssets) foreach (var asset in assets) _issuedAssets.Add(asset);
        return assets.AsReadOnly();
    }

    public async Task<FirmwareFileInspection> DownloadAsync(DjiFirmwareAsset asset, string destination,
        IProgress<long>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_issuedAssets)
            if (!_issuedAssets.Contains(asset)) throw new FirmwareCatalogException("该固件未由当前官方版本清单确认，请重新检查更新。");
        ValidateOfficialUri(asset.DownloadUri, false);
        ValidateOfficialUri(asset.SourceUri, true);
        if (asset.ExpectedLength <= 0 || asset.ExpectedLength > FirmwarePackageInspector.MaximumFileBytes ||
            asset.Md5.Length != 32 || !asset.Md5.All(Uri.IsHexDigit))
            throw new FirmwareCatalogException("官方固件缺少可验证的大小或摘要，不能下载为安装候选。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var fullPath = Path.GetFullPath(destination);
        if (File.Exists(fullPath)) throw new IOException("目标文件已存在，请选择另一个保存位置。");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await SendAsync(asset.DownloadUri, false, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != asset.ExpectedLength)
                throw new FirmwareCatalogException("固件响应大小与官方清单不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total = checked(total + count);
                    if (total > asset.ExpectedLength) throw new FirmwareCatalogException("固件数据超过官方清单声明的大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(total);
                }
                if (total != asset.ExpectedLength || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Md5)))
                    throw new FirmwareCatalogException("固件大小或 MD5 与官方清单不一致，下载文件已拒绝。");
            }
            timeout.Token.ThrowIfCancellationRequested();
            var inspection = await FirmwarePackageInspector.InspectAsync(temporary, timeout.Token).ConfigureAwait(false);
            File.Move(temporary, fullPath, false);
            return inspection with { Path = fullPath };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, CancellationToken token, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var bytes = await ReadMetadataAsync(uri, token, parameters).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new FirmwareCatalogException("官方接口未返回可识别的 JSON 元数据。"); }
    }

    private async Task<byte[]> ReadMetadataAsync(Uri uri, CancellationToken token,
        IReadOnlyDictionary<string, string>? parameters = null, bool allowCdn = false)
    {
        var body = parameters is null ? null : string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var response = await SendAsync(uri, !allowCdn, token, body).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
            throw new FirmwareCatalogException("官方固件元数据超过读取上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaximumMetadataBytes) throw new FirmwareCatalogException("官方固件元数据超过读取上限。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool apiOnly, CancellationToken token, string? formBody = null)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            ValidateOfficialUri(uri, apiOnly);
            using var request = new HttpRequestMessage(formBody is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (formBody is not null) request.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new FirmwareCatalogException("无法连接 DJI 官方固件服务，请检查网络后重试。"); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther)
                    formBody = null;
                response.Dispose();
                if (location is null) throw new FirmwareCatalogException("官方固件重定向缺少地址。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new FirmwareCatalogException($"DJI 固件服务返回 HTTP {code}。");
            }
            return response;
        }
        throw new FirmwareCatalogException("官方固件服务重定向次数过多。");
    }

    internal static void ValidateOfficialUri(Uri uri, bool apiOnly)
    {
        if (!uri.IsAbsoluteUri) throw new FirmwareCatalogException("固件来源必须是绝对 HTTPS 地址。");
        var host = uri.IdnHost;
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (apiOnly ? host != "mydjiflight.dji.com" : host != "mydjiflight.dji.com" && host != "dl.djicdn.com" &&
                !host.EndsWith(".djicdn.com", StringComparison.OrdinalIgnoreCase)))
            throw new FirmwareCatalogException("固件来源不是支持的 DJI 官方 HTTPS 地址。");
    }

    private static Uri ReadApiUri(JsonElement apis, string key)
    {
        if (!apis.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
            throw new FirmwareCatalogException("官方固件接口清单不完整。");
        ValidateOfficialUri(uri, true);
        return uri;
    }

    private static Uri AddQuery(Uri uri, IReadOnlyDictionary<string, string> values) => new(uri.AbsoluteUri +
        (string.IsNullOrEmpty(uri.Query) ? "?" : "&") + string.Join("&", values.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));

    private static string Sign(string value, bool keyFirst)
    {
        var key = ApplicationSigningValue();
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            hash.AppendData(keyFirst ? key : bytes);
            hash.AppendData(keyFirst ? bytes : key);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] ApplicationSigningValue() => [65, 97, 85, 116, 48, 69, 108, 101, 104, 73, 77, 118, 102, 66, 117, 70, 99, 122, 89, 67, 103, 50, 79, 72, 48, 65, 55, 107, 122, 110, 77, 67];
    public void Dispose() => _http.Dispose();
}
