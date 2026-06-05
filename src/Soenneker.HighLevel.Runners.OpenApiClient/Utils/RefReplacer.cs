using System.Threading;
using System.Threading.Tasks;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.HighLevel.Runners.OpenApiClient.Utils;

/// <summary>
/// Represents the ref replacer.
/// </summary>
public static class RefReplacer
{
    private static readonly (string oldRef, string newRef)[] _replacements =
    [
        ("\"$ref\": \"../common/common-schemas.json#/components/schemas/BadRequestDTO\"",
            "\"$ref\": \"#/components/schemas/BadRequestDTO\""),

        ("\"$ref\": \"../common/common-schemas.json#/components/schemas/UnauthorizedDTO\"",
            "\"$ref\": \"#/components/schemas/UnauthorizedDTO\""),

        ("\"$ref\": \"../common/common-schemas.json#/components/schemas/UnprocessableDTO\"",
            "\"$ref\": \"#/components/schemas/UnprocessableDTO\"")
    ];

    /// <summary>
    /// Executes the replace refs operation.
    /// </summary>
    /// <param name="fileUtil">The file util.</param>
    /// <param name="inputPath">The input path.</param>
    /// <param name="outputPath">The output path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static async Task ReplaceRefs(IFileUtil fileUtil, string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        string text = await fileUtil.Read(inputPath, cancellationToken: cancellationToken);

        foreach (var (oldRef, newRef) in _replacements)
            text = text.Replace(oldRef, newRef);

        await fileUtil.Write(outputPath, text, cancellationToken: cancellationToken);
    }
}