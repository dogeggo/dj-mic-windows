using System.Net;
using System.Text;
using System.Security.Cryptography;
using MicMini.Desktop.Services;

if (args.Contains("--official-readonly"))
{
    using var live = new DjiFirmwareCatalogClient();
    var endpoints = await live.DiscoverAsync();
    Console.WriteLine($"Official discovery: {endpoints.AllFile.Host}; HTTPS endpoints accepted.");
    foreach (var product in new[] { "dm300", "dm202b" })
    {
        try { var catalog = await live.GetCatalogAsync(product); Console.WriteLine($"{product}: {catalog.Releases.Count} version records"); }
        catch (FirmwareCatalogException error) { Console.WriteLine($"{product}: {error.Message}"); }
    }
    return;
}

var checks = 0;
const string discovery = """{"state":0,"apis":{"allfile":"https://mydjiflight.dji.com/getfile/getallfile?need_upgrade_app=2","config":"https://mydjiflight.dji.com/getfile/download","firm":"https://mydjiflight.dji.com/getfile/firmware","down":"https://mydjiflight.dji.com/getfile/downpath"}}""";
var payload = Encoding.UTF8.GetBytes("offline synthetic firmware content");
var digest = Convert.ToHexString(MD5.HashData(payload));
var fixtureXml = $"""<?xml version="1.0"?><dji><device id="dm300"><firmware formal="01.02.0304"><release version="01.02.0304"><module id="2602" version="01.02.03.04" size="{payload.Length}" md5="{digest}">fixture.bin</module></release></firmware></device></dji>""";
const string fixtureCatalog = """[{"product_version":"01.02.0304","release_note":{"zh_cn":"a\\nb"},"released_time":"2026-01-01"}]""";
const string fixtureDownload = """{"code":0,"msg":"success","url":"https://dl.djicdn.com/fixture.bin"}""";
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
}
async Task Reject(Func<Task> action, string label)
{
    try { await action(); }
    catch (FirmwareCatalogException) { checks++; return; }
    throw new Exception("Expected a safe rejection: " + label);
}
HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

using (var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) =>
{
    Check(request.RequestUri!.Scheme == "https", "HTTPS");
    return Task.FromResult(Json(discovery));
})))
{
    var endpoints = await client.DiscoverAsync();
    Check(endpoints.AllFile.Query == "?need_upgrade_app=2", "discovery preserves query");
}
foreach (var bad in new[]
{
    "http://mydjiflight.dji.com/a", "https://mydjiflight.dji.com.evil.invalid/a", "https://evil.invalid/a",
    "https://mydjiflight.dji.com:8443/a", "https://user@mydjiflight.dji.com/a", "https://dl.djicdn.com/a#bad"
})
{
    await Reject(() => { DjiFirmwareCatalogClient.ValidateOfficialUri(new Uri(bad), false); return Task.CompletedTask; }, "invalid origin");
}
using (var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => Task.FromResult(Json(discovery.Replace("https://mydjiflight.dji.com/getfile/download", "https://evil.invalid/download"))))))
    await Reject(() => client.DiscoverAsync(), "untrusted discovered API");
foreach (var body in new[] { "{\"status\":2,\"msg\":\"can't find the record\"}", "[]", "{}", "not json", "[{}]" })
{
    var call = 0;
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => Task.FromResult(Json(call++ == 0 ? discovery : body))));
    await Reject(() => client.GetCatalogAsync("dm300"), "failed/empty catalog must not mean latest");
}
{
    var call = 0;
    using var client = new DjiFirmwareCatalogClient(new FakeHandler(async (request, token) =>
    {
        var number = call++;
        if (number == 0) return Json(discovery);
        Check(request.Method == HttpMethod.Post, "catalog uses POST");
        Check((await request.Content!.ReadAsStringAsync(token)).Contains("product_id=dm300"), "product parameter in form");
        if (number == 1)
        {
            Check(request.RequestUri!.Query == "?need_upgrade_app=2", "preserve discovery parameters");
            return Json(fixtureCatalog);
        }
        return Json(number == 2 ? fixtureXml : fixtureDownload);
    }));
    var catalog = await client.GetCatalogAsync("dm300");
    Check(catalog.Releases.Count == 1 && catalog.Releases[0].Version == "01.02.0304", "version read");
    Check(catalog.Releases[0].ReleaseNotes == "a\nb", "escaped release notes");
    Check(catalog.Releases[0].Assets.Count == 1, "configuration and downpath produce asset");
    Check(catalog.Releases[0].Assets[0].Md5 == digest, "digest from official config");
}
using (var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.invalid/collect") } }))))
    await Reject(() => client.DiscoverAsync(), "redirect host must be validated before request");
{
    var calls = 0;
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) =>
    {
        calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://mydjiflight.dji.com/loop") } });
    }));
    await Reject(() => client.DiscoverAsync(), "redirect limit");
    Check(calls == 4, "bounded redirect requests");
}
using (var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => Task.FromResult(Json(new string(' ', 2 * 1024 * 1024 + 1))))))
    await Reject(() => client.DiscoverAsync(), "metadata size limit");
{
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(Json(discovery)); }));
    try { await client.DiscoverAsync(cancel.Token); throw new Exception("cancellation ignored"); }
    catch (OperationCanceledException) { checks++; }
}
{
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => throw new Exception("must not send HTTP")));
    var forged = new DjiFirmwareAsset("dm300", "1.0", "arbitrary.bin", new Uri("https://dl.djicdn.com/arbitrary.bin"), 12,
        new string('0', 32), new Uri("https://mydjiflight.dji.com/getfile/getallfile"));
    await Reject(() => client.DownloadAsync(forged, Path.Combine(Path.GetTempPath(), "must-not-create-firmware.bin")), "unissued asset");
    await Reject(() => client.GetCatalogAsync("dm303"), "unrelated product");
}
foreach (var badConfig in new[]
{
    fixtureXml.Replace("id=\"dm300\"", "id=\"dm303\""),
    fixtureXml.Replace("formal=\"01.02.0304\"", "formal=\"99.99.9999\""),
    fixtureXml.Replace(digest, "invalid"),
    fixtureXml.Replace("fixture.bin", "../bad.bin"),
    "<!DOCTYPE dji [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><dji>&e;</dji>"
})
{
    var call = 0;
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) => Task.FromResult(Json(call++ switch
    {
        0 => discovery, 1 => fixtureCatalog, _ => badConfig
    }))));
    await Reject(() => client.GetCatalogAsync("dm300"), "invalid module configuration");
}
foreach (var scenario in new[] { "valid", "bad-md5", "too-long", "too-short", "cancel" })
{
    var call = 0;
    using var cancellation = new CancellationTokenSource();
    using var client = new DjiFirmwareCatalogClient(new FakeHandler((request, token) =>
    {
        var number = call++;
        if (number < 4) return Task.FromResult(Json(number switch { 0 => discovery, 1 => fixtureCatalog, 2 => fixtureXml, _ => fixtureDownload }));
        if (scenario == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
        var bytes = scenario switch
        {
            "bad-md5" => payload.Select(x => (byte)(x ^ 1)).ToArray(),
            "too-long" => payload.Concat(new byte[] { 0 }).ToArray(),
            "too-short" => payload[..^1], _ => payload
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }));
    var catalog = await client.GetCatalogAsync("dm300");
    var tempDirectory = Path.Combine(Path.GetTempPath(), "mic-firmware-selftest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    var target = Path.Combine(tempDirectory, "firmware.bin");
    try
    {
        var action = () => client.DownloadAsync(catalog.Releases[0].Assets[0], target, token: cancellation.Token);
        if (scenario == "valid")
        {
            var result = await action();
            Check(result.Length == payload.Length && result.Md5 == digest, "download verified");
            Check(File.ReadAllBytes(target).SequenceEqual(payload), "download bytes");
            File.Delete(target);
        }
        else if (scenario == "cancel")
        {
            try { await action(); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { checks++; }
        }
        else await Reject(action, "download mismatch");
        Check(Directory.GetFiles(tempDirectory).Length == 0, "no partial file remains");
    }
    finally { Directory.Delete(tempDirectory); }
}
Console.WriteLine($"Firmware catalog self-test passed: {checks} checks; no hardware commands, no network.");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
}
