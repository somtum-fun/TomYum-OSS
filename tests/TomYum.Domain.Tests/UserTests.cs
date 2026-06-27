using TomYum.Domain.Entities;
using Xunit;

namespace TomYum.Domain.Tests;

public class UserTests
{
    [Fact]
    public void Placeholder_DomainProjectIsReferenced()
    {
        // Scaffold smoke test: proves the Domain project is referenced and builds.
        // Replace with real entity behaviour tests as the domain takes shape.
        Assert.NotNull(typeof(User).Assembly);
    }
}
