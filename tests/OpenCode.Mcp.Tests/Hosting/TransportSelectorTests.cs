using OpenCode.Mcp.Hosting;
using OpenCode.Mcp.Hosting.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Checks mode selection and host argument ownership at the command-line boundary.</summary>
public sealed class TransportSelectorTests
{
    /// <summary>Valid selections preserve the exact host argument sequence.</summary>
    [Test]
    [Arguments(new string[0], McpTransport.Stdio, new string[0])]
    [Arguments(new[] { "--z", "--a" }, McpTransport.Stdio, new[] { "--z", "--a" })]
    [Arguments(new[] { "--Transport", "http" }, McpTransport.Stdio, new[] { "--Transport", "http" })]
    [Arguments(new[] { "--transport", "stdio" }, McpTransport.Stdio, new string[0])]
    [Arguments(new[] { "--transport", "http", "--urls", "http://127.0.0.1:0" }, McpTransport.Http,
        new[] { "--urls", "http://127.0.0.1:0" })]
    [Arguments(new[] { "--z", "--transport", "http", "--a", "--z" }, McpTransport.Http, new[] { "--z", "--a", "--z" })]
    public async Task Parse_Should_Select_Mode_And_Preserve_Host_Arguments_When_Selection_Is_Valid(
        IReadOnlyList<string> arguments, McpTransport expectedTransport, IReadOnlyList<string> expectedHostArguments)
    {
        var result = TransportSelector.Parse(arguments);

        await Assert.That(result.Transport).IsEqualTo(expectedTransport);
        await Assert.That(result.HostArguments).IsEquivalentTo(expectedHostArguments, CollectionOrdering.Matching);
    }

    /// <summary>Invalid selections report the permitted values before host launch.</summary>
    [Test]
    [Arguments((object)new[] { "--transport", "smtp" })]
    [Arguments((object)new[] { "--transport", "HTTP" })]
    [Arguments((object)new[] { "--transport", "Stdio" })]
    [Arguments((object)new[] { "--transport", "--urls" })]
    [Arguments((object)new[] { "--transport=smtp" })]
    [Arguments((object)new[] { "--transport=http" })]
    [Arguments((object)new[] { "--transport=" })]
    [Arguments((object)new[] { "--transport=http=extra" })]
    [Arguments((object)new[] { "--transport", "http", "--transport=smtp" })]
    [Arguments((object)new[] { "--transport", "http", "--transport=http" })]
    [Arguments((object)new[] { "--transport", "http", "--transport=" })]
    [Arguments((object)new[] { "--transport", "http", "--transport=http=extra" })]
    [Arguments((object)new[] { "--transport" })]
    [Arguments((object)new[] { "--a", "--transport" })]
    [Arguments((object)new[] { "--transport", "stdio", "--transport", "stdio" })]
    [Arguments((object)new[] { "--transport", "http", "--transport", "http" })]
    [Arguments((object)new[] { "--transport", "http", "--transport", "stdio" })]
    public async Task Parse_Should_Refuse_With_Permitted_Values_When_Selection_Is_Invalid(IReadOnlyList<string> arguments)
    {
        var caught = await Assert.That(() => TransportSelector.Parse(arguments)).ThrowsExactly<ArgumentException>();
        var exception = await Assert.That(caught).IsNotNull();

        await Assert.That(exception.ParamName).IsEqualTo("arguments");
        await Assert.That(exception.Message).Contains("'stdio'");
        await Assert.That(exception.Message).Contains("'http'");
    }

    /// <summary>A missing list is distinct from an invalid element.</summary>
    [Test]
    public async Task Parse_Should_Throw_ArgumentNullException_When_List_Is_Null()
    {
        // Deliberately violate the non-null list precondition.
        await Assert.That(() => TransportSelector.Parse(null!))
            .ThrowsExactly<ArgumentNullException>()
            .WithParameterName("arguments");
    }

    /// <summary>A null host token is refused rather than forwarded.</summary>
    [Test]
    public async Task Parse_Should_Throw_ArgumentException_When_Host_Argument_Is_Null()
    {
        // Deliberately violate the non-null element precondition.
        await Assert.That(() => TransportSelector.Parse([null!]))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("arguments");
    }

    /// <summary>A null selector value is not a missing list.</summary>
    [Test]
    public async Task Parse_Should_Throw_ArgumentException_When_Selector_Value_Is_Null()
    {
        // Deliberately violate the non-null element precondition in the value position.
        await Assert.That(() => TransportSelector.Parse(["--transport", null!]))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("arguments");
    }

    /// <summary>A valid selection does not bypass later invalid elements.</summary>
    [Test]
    public async Task Parse_Should_Throw_ArgumentException_When_Null_Follows_Valid_Selection()
    {
        // Deliberately violate the non-null element precondition after a valid selection.
        await Assert.That(() => TransportSelector.Parse(["--transport", "http", null!]))
            .ThrowsExactly<ArgumentException>()
            .WithParameterName("arguments");
    }

    /// <summary>Host arguments remain owned by the result after caller mutation in every mode.</summary>
    [Test]
    [Arguments(new[] { "--z", "--a" }, McpTransport.Stdio)]
    [Arguments(new[] { "--z", "--transport", "stdio", "--a" }, McpTransport.Stdio)]
    [Arguments(new[] { "--z", "--transport", "http", "--a" }, McpTransport.Http)]
    public async Task Parse_Should_Return_Read_Only_Snapshot_When_Input_Is_Mutated(
        IList<string> input, McpTransport expectedTransport)
    {
        ArgumentNullException.ThrowIfNull(input);
        var expectedHostArguments = new[] { "--z", "--a" };
        var result = TransportSelector.Parse((IReadOnlyList<string>)input);

        await Assert.That(result.Transport).IsEqualTo(expectedTransport);
        await Assert.That(result.HostArguments).IsEquivalentTo(expectedHostArguments, CollectionOrdering.Matching);

        for (var index = 0; index < input.Count; index++)
        {
            input[index] = "changed";
        }

        await Assert.That(result.HostArguments).IsEquivalentTo(expectedHostArguments, CollectionOrdering.Matching);
        await Assert.That(result.HostArguments).IsNotTypeOf<string[]>();
        var collection = await Assert.That(result.HostArguments).IsAssignableTo<ICollection<string>>();
        var readOnlyCollection = await Assert.That<ICollection<string>>(collection).IsNotNull();
        await Assert.That(readOnlyCollection.IsReadOnly).IsTrue();
    }
}
