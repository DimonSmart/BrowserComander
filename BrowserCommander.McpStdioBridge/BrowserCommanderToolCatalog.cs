using System.Reflection;
using BrowserCommanderServer;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrowserCommander.McpStdioBridge;

public sealed class BrowserCommanderToolCatalog
{
    public BrowserCommanderToolCatalog(IServiceProvider services)
    {
        Tools = CreateTools(services);
    }

    public IReadOnlyList<Tool> Tools { get; }

    private static IReadOnlyList<Tool> CreateTools(IServiceProvider services)
    {
        return typeof(BrowserAutomationMcpTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type => CreateTools(type, services))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<Tool> CreateTools(Type declaringType, IServiceProvider services)
    {
        foreach (var method in declaringType
                     .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                     .OrderBy(method => method.MetadataToken))
        {
            yield return CreateServerTool(method, declaringType, services).ProtocolTool;
        }
    }

    private static McpServerTool CreateServerTool(
        MethodInfo method,
        Type declaringType,
        IServiceProvider services)
    {
        if (method.IsStatic)
        {
            return McpServerTool.Create(method);
        }

        return McpServerTool.Create(
            method,
            request => ActivatorUtilities.CreateInstance(
                request.Services ?? services,
                declaringType));
    }
}
