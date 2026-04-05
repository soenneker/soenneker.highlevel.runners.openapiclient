using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Git.Util.Abstract;
using Soenneker.HighLevel.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Process.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.OpenApi;

namespace Soenneker.HighLevel.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IOpenApiMerger _openApiMerger;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IFileUtil fileUtil,
        IDirectoryUtil directoryUtil, IOpenApiFixer openApiFixer, IOpenApiMerger openApiMerger, IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiFixer = openApiFixer;
        _openApiMerger = openApiMerger;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string appsDirectory = Path.Combine(gitDirectory, "apps");

        await _directoryUtil.Create(Path.Combine(gitDirectory, "common"), cancellationToken: cancellationToken);
        await _directoryUtil.Create(appsDirectory, cancellationToken: cancellationToken);

        string commonFilePath = Path.Combine(gitDirectory, "common", "common-schemas.json");
        string targetFilePath = Path.Combine(appsDirectory, "openapi.json");
        string fixedFilePath = Path.Combine(appsDirectory, "fixed.json");

        await _fileUtil.DeleteIfExists(commonFilePath, cancellationToken: cancellationToken);
        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openapiDocsDirectory =
            await _gitUtil.CloneToTempDirectory("https://github.com/GoHighLevel/highlevel-api-docs", cancellationToken: cancellationToken);

        string appsDir = Path.Combine(openapiDocsDirectory, "apps");

        string commonDir = Path.Combine(openapiDocsDirectory, "common");

        List<string> appsFiles = await _directoryUtil.GetFilesByExtension(appsDir, "", false, cancellationToken);
        List<string> files = appsFiles.Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                                  f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                                                  f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                      .ToList();

        List<string> commonFilesRaw = await _directoryUtil.GetFilesByExtension(commonDir, "", false, cancellationToken);
        List<string> commonFiles = commonFilesRaw.Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                                             f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                                                             f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                                 .ToList();

        files.AddRange(commonFiles);

        (string prefix, string f)[] inputs = files.Select(f =>
                                                  {
                                                      string prefix = Path.GetFileNameWithoutExtension(f);
                                                      return (prefix, f);
                                                  })
                                                  .ToArray();

        OpenApiDocument merged = await _openApiMerger.MergeOpenApis(inputs, cancellationToken);

        string json = _openApiMerger.ToJson(merged);

        await _fileUtil.Write(targetFilePath, json, true, cancellationToken);

        await _openApiFixer.Fix(targetFilePath, fixedFilePath, cancellationToken);

        await RefReplacer.ReplaceRefs(_fileUtil, fixedFilePath, fixedFilePath, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken)
            .NoSync();

        await _kiotaUtil.Generate(fixedFilePath, "HighLevelOpenApiClient", Constants.Library, gitDirectory, cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken)
            .NoSync();
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: true, cancellationToken)
                                       .NoSync();
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
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
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}