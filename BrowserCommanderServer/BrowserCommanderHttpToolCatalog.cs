using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrowserCommanderServer;

public sealed class BrowserCommanderHttpToolCatalog
{
    public BrowserCommanderHttpToolCatalog(McpToolPresentationOptions presentationOptions)
    {
        ArgumentNullException.ThrowIfNull(presentationOptions);
        Tools = CreateTools(presentationOptions);
    }

    public IReadOnlyList<McpServerTool> Tools { get; }

    private static IReadOnlyList<McpServerTool> CreateTools(McpToolPresentationOptions presentationOptions)
    {
        return typeof(BrowserAutomationMcpTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type => CreateTools(type, presentationOptions))
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<McpServerTool> CreateTools(
        Type declaringType,
        McpToolPresentationOptions presentationOptions)
    {
        foreach (var method in declaringType
                     .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                     .OrderBy(method => method.MetadataToken))
        {
            var tool = CreateServerTool(method, declaringType);
            ApplyPresentationHints(tool.ProtocolTool, presentationOptions);
            yield return tool;
        }
    }

    private static McpServerTool CreateServerTool(MethodInfo method, Type declaringType)
    {
        if (method.IsStatic)
        {
            return McpServerTool.Create(method);
        }

        return McpServerTool.Create(
            method,
            request => ActivatorUtilities.CreateInstance(
                request.Services ?? throw new InvalidOperationException("Request services are unavailable."),
                declaringType));
    }

    private static void ApplyPresentationHints(Tool protocolTool, McpToolPresentationOptions presentationOptions)
    {
        if (!presentationOptions.ForceReadOnlyHints)
        {
            return;
        }

        var currentAnnotations = protocolTool.Annotations;
        protocolTool.Annotations = new ToolAnnotations
        {
            Title = currentAnnotations?.Title,
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = currentAnnotations?.IdempotentHint,
            OpenWorldHint = currentAnnotations?.OpenWorldHint
        };
    }
}
