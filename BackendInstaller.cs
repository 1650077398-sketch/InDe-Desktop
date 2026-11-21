using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class BackendInstaller
{
    private const string ArchiveUrl = "https://github.com/Indigo2233/InDe-Suite/releases/download/"
        + "v1.0.3-local/InDe-1.0.3-local-windows-x64.zip";
    private const string ArchiveSha256 = "E5D591E37192E90907EB30ECD88E6445B1E85E51C859651EEFAAF259DDD3EDFB";
    private static readonly string[] Required = {
        "runtime/python.exe", "catalog.json", "tools/inde_worker.py", "tools/inde_runtime.py",
        "engine/run_models.py", "engine/models/InDeConv.onnx", "engine/models/InDeStars.onnx",
        "engine/models/InDeNoise.onnx"
    };

    public static string ManagedPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InDeDesktop", "backends", "v1.0.3", "InDeBackend");
        }
    }

    public static bool IsUsable(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return false;
        foreach (string file in Required)
            if (!File.Exists(Path.Combine(path, file))) return false;
        return true;
    }

    public static async Task InstallAsync(string destination, IProgress<int> progress, CancellationToken cancel)
    {
        if (IsUsable(destination)) return;
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("后端目标已存在，但文件不完整。请在程序里选择另一个完整的 InDeBackend。");

        string parent = Path.GetDirectoryName(destination);
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string archive = Path.Combine(staging, "official.zip");
        string backend = Path.Combine(staging, "InDeBackend");
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (WebClient client = new WebClient())
            using (cancel.Register(client.CancelAsync))
            {
                client.DownloadProgressChanged += (sender, args) => progress.Report(args.ProgressPercentage);
                cancel.ThrowIfCancellationRequested();
                await client.DownloadFileTaskAsync(new Uri(ArchiveUrl), archive);
            }
            cancel.ThrowIfCancellationRequested();
            progress.Report(-1);
            await Task.Run(() => ExtractVerifiedBackend(archive, backend, cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            if (!IsUsable(destination)) Directory.Move(backend, destination);
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    private static void ExtractVerifiedBackend(string path, string target, CancellationToken cancel)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(hash, ArchiveSha256, StringComparison.Ordinal))
                throw new InvalidDataException("官方后端压缩包的 SHA-256 校验未通过，请重新下载。");
        }
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        const string prefix = "InDeBackend/";
        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                    continue;
                string output = Path.GetFullPath(Path.Combine(target, name.Substring(prefix.Length)));
                if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("后端压缩包路径无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                entry.ExtractToFile(output);
            }
        }
        if (!IsUsable(target)) throw new InvalidDataException("官方压缩包缺少程序所需的后端文件。");
    }
}
