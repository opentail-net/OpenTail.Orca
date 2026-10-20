namespace OpenTail.Core.Tools.Abstractions;

public interface IOpenTailTool
{
    string Name { get; }
    string Description { get; }
    AIFunction ToAIFunction(); // Compatible with .NET 10 AI ecosystem
}
