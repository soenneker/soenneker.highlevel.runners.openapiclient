using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.HighLevel.Runners.OpenApiClient.Utils;

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

    public static async Task ReplaceRefs(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        string text = await File.ReadAllTextAsync(inputPath, Encoding.UTF8, cancellationToken);

        foreach (var (oldRef, newRef) in _replacements)
            text = text.Replace(oldRef, newRef);

        await File.WriteAllTextAsync(outputPath, text, Encoding.UTF8, cancellationToken);
    }
}