using System.Security.Cryptography;

namespace MicMini.Desktop.Services;

public sealed record FirmwareFileInspection(string Path, long Length, string Sha256, string Md5, byte[] Header);

/// <summary>Execution-free file inspection. A digest alone is not model compatibility or authenticity proof.</summary>
public static class FirmwarePackageInspector
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    public static async Task<FirmwareFileInspection> InspectAsync(string path, CancellationToken token = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        // Do not permit a concurrent writer while hashing the selected file.
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if (length == 0) throw new InvalidDataException("固件文件为空。");
        if (length > MaximumFileBytes) throw new InvalidDataException("文件超过 512 MiB，未读取；请选择官方发布的设备固件文件。");
        var header = new byte[Math.Min(length, 64)];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        input.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > MaximumFileBytes) throw new InvalidDataException("文件读取期间大小超出限制。");
            sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read);
        }
        if (total != length) throw new InvalidDataException("文件在读取期间发生变化，请重新选择。");
        return new(fullPath, length, Convert.ToHexString(sha.GetHashAndReset()), Convert.ToHexString(md5.GetHashAndReset()), header);
    }
}
