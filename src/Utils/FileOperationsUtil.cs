using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Soenneker.Extensions.Stream;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Util.Abstract;
using Soenneker.HighLevel.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.HighLevel.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IProcessUtil _processUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiFixer _openApiFixer;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IProcessUtil processUtil, IFileUtil fileUtil,
        IDirectoryUtil directoryUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _processUtil = processUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiFixer = openApiFixer;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string appsDirectory = Path.Combine(gitDirectory, "apps");

        _directoryUtil.CreateIfDoesNotExist(Path.Combine(gitDirectory, "common"));
        _directoryUtil.CreateIfDoesNotExist(appsDirectory);

        string commonFilePath = Path.Combine(gitDirectory, "common", "common-schemas.json");
        string targetFilePath = Path.Combine(appsDirectory, "openapi.json");
        string fixedFilePath = Path.Combine(appsDirectory, "fixed.json");

        await _fileUtil.DeleteIfExists(commonFilePath, cancellationToken: cancellationToken);
        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openapiDocsDirectory = await _gitUtil.CloneToTempDirectory("https://github.com/GoHighLevel/highlevel-api-docs", cancellationToken: cancellationToken);

        string appsDir = Path.Combine(openapiDocsDirectory, "apps");

        string commonDir = Path.Combine(openapiDocsDirectory, "common");

        List<string> files = Directory.EnumerateFiles(appsDir, "*.*", SearchOption.TopDirectoryOnly).Where(f =>
            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();

        List<string> commonFiles = Directory.EnumerateFiles(commonDir, "*.*", SearchOption.TopDirectoryOnly).Where(f =>
            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();

        files.AddRange(commonFiles);

        (string prefix, string f)[] inputs = files.Select(f =>
        {
            string prefix = Path.GetFileNameWithoutExtension(f);
            return (prefix, f);
        }).ToArray();

        OpenApiDocument merged = await MergeOpenApis(inputs);

        string json = ToJson(merged);

        await _fileUtil.Write(targetFilePath, json, true, cancellationToken);

        await _openApiFixer.Fix(targetFilePath, fixedFilePath, cancellationToken);

        await RefReplacer.ReplaceRefs(fixedFilePath, fixedFilePath, cancellationToken);

        await _processUtil.Start("dotnet", null, "tool update --global Microsoft.OpenApi.Kiota", waitForExit: true, cancellationToken: cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src");

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken).NoSync();

        await _openApiFixer.GenerateKiota(fixedFilePath, "HighLevelOpenApiClient", Constants.Library, srcDirectory, cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    public async ValueTask<OpenApiDocument> MergeOpenApis(params (string prefix, string file)[] inputs)
    {
        var merged = new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = "Merged HL APIs", Version = "1.0.0" },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents(),
            Servers = new List<OpenApiServer>()
        };

        foreach ((string prefix, string file) in inputs)
        {
            await using FileStream fs = File.OpenRead(file);
            var memoryStream = new MemoryStream();
            await fs.CopyToAsync(memoryStream).NoSync();
            memoryStream.ToStart();

            var uri = new Uri($"file://{file}");

            var reader = new OpenApiJsonReader();

            ReadResult readResult = await reader.ReadAsync(memoryStream, uri, new OpenApiReaderSettings()).NoSync();

            OpenApiDocument? document = readResult.Document;

            if (document == null)
            {
                _logger.LogInformation("Document was null, skipping");
                continue;
            }

            // carry over servers (dedupe by URL)
            foreach (OpenApiServer s in document.Servers ?? Enumerable.Empty<OpenApiServer>())
                if (!merged.Servers.Any(x => x.Url == s.Url))
                    merged.Servers.Add(s);

            // 1) merge paths with a prefix (e.g., "/contacts")
            foreach (KeyValuePair<string, IOpenApiPathItem> kvp in document.Paths)
            {
                string trimmedPrefix = prefix.Trim('/');
                string newKey;
                
                // Check if the path already starts with the prefix to avoid duplication
                if (kvp.Key.StartsWith("/" + trimmedPrefix + "/") || kvp.Key == "/" + trimmedPrefix)
                {
                    newKey = kvp.Key; // Use the original path if it already has the prefix
                }
                else
                {
                    newKey = "/" + trimmedPrefix + (kvp.Key.StartsWith("/") ? "" : "/") + kvp.Key;
                }
                
                merged.Paths[newKey] = kvp.Value;
            }

            // 2) merge components with a name prefix to avoid clashes
            string compPrefix = ToSafeId(prefix) + "_";

            static IDictionary<string, T> CopyDict<T>(IDictionary<string, T>? from, IDictionary<string, T>? to, string compPrefix)
            {
                var target = to ?? new Dictionary<string, T>();
                if (from is null) return target;

                foreach (var c in from)
                {
                    var name = target.ContainsKey(c.Key) ? compPrefix + c.Key : c.Key;
                    while (target.ContainsKey(name)) name = "_" + name;
                    target[name] = c.Value;
                }

                return target;
            }

            OpenApiComponents src = document.Components ?? new OpenApiComponents();
            OpenApiComponents? dst = merged.Components;

            dst.Schemas = CopyDict(src.Schemas, dst.Schemas, compPrefix);
            dst.Parameters = CopyDict(src.Parameters, dst.Parameters, compPrefix);
            dst.Responses = CopyDict(src.Responses, dst.Responses, compPrefix);
            dst.RequestBodies = CopyDict(src.RequestBodies, dst.RequestBodies, compPrefix);
            dst.Headers = CopyDict(src.Headers, dst.Headers, compPrefix);
            dst.SecuritySchemes = CopyDict(src.SecuritySchemes, dst.SecuritySchemes, compPrefix);
            dst.Links = CopyDict(src.Links, dst.Links, compPrefix);
            dst.Callbacks = CopyDict(src.Callbacks, dst.Callbacks, compPrefix);
            dst.Examples = CopyDict(src.Examples, dst.Examples, compPrefix);
            // (You can also rewrite $refs inside operations if you need strict renaming.)
        }

        return merged;

        static string ToSafeId(string s) =>
            new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    }

    public static string ToJson(OpenApiDocument doc)
    {
        using var sb = new StringWriter(new StringBuilder(1024));
        var writer = new OpenApiJsonWriter(sb);
        doc.SerializeAsV3(writer);
        return sb.ToString();
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            foreach (string file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: true, cancellationToken).NoSync();
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            foreach (string dir in Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, "Jake Soenneker", "jake@soenneker.com", cancellationToken);
    }
}