using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>AB#5543: engine Secret exceptions become stable, value-free tool messages.</summary>
public class SecretErrorsTests
{
    [Fact]
    public void Describe_NotQueryable_PrefixesErrorCodeAndHint()
    {
        var message = SecretErrors.Describe(new SecretAttributeNotQueryableException("Password", "sort", "Cred"));

        message.Should().StartWith("SecretAttributeNotQueryable: ").And.Contain("Password").And.Contain("IsNotNull");
    }

    [Fact]
    public void Describe_NotQueryableAsInnerException_IsFound()
    {
        var wrapped = new InvalidOperationException("outer",
            new SecretAttributeNotQueryableException("Token", "group-by", "Endpoint"));

        SecretErrors.Describe(wrapped).Should().StartWith("SecretAttributeNotQueryable");
    }

    [Fact]
    public void Describe_NotQueryableInsideAggregate_IsFound()
    {
        var aggregate = new AggregateException(new TimeoutException("t"),
            new SecretAttributeNotQueryableException("Token", "aggregation", "Endpoint"));

        SecretErrors.Describe(aggregate).Should().StartWith("SecretAttributeNotQueryable");
    }

    [Fact]
    public void Describe_EncryptionNotConfigured_UsesItsCode()
    {
        var ex = new SecretEncryptionNotConfiguredException("No secret encryption key is configured.");

        SecretErrors.Describe(ex).Should().StartWith("SecretEncryptionNotConfigured: ");
    }

    [Fact]
    public void Describe_UnrelatedException_PassesMessageThrough()
    {
        SecretErrors.TryDescribe(new InvalidOperationException("boom")).Should().BeNull();
        SecretErrors.Describe(new InvalidOperationException("boom")).Should().Be("boom");
    }
}
