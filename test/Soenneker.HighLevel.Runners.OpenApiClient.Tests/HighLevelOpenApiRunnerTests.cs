using Soenneker.Tests.HostedUnit;

namespace Soenneker.HighLevel.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class HighLevelOpenApiRunnerTests : HostedUnitTest
{

    public HighLevelOpenApiRunnerTests(Host host) : base(host)
    {

    }

    [Test]
    public void Default()
    {

    }
}
