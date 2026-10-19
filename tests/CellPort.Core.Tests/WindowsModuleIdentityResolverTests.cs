using System.Collections.Immutable;
using System.Text.Json;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class WindowsModuleIdentityResolverTests
{
    [Fact]
    public void DuplicateFriendlyNamesOnTwoModulesAreAmbiguousUntilPhysicalContainerIsSelected()
    {
        var nodes = LoadFixture();
        var ambiguous = WindowsModuleIdentityResolver.Resolve(nodes);

        Assert.Equal(WindowsModuleResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.Module);
        Assert.Equal(2, ambiguous.CandidateCount);

        var selectedContainer = Guid.Parse("2f123456-1111-2222-3333-000000000001");
        var selected = WindowsModuleIdentityResolver.Resolve(nodes, selectedContainer);

        Assert.Equal(WindowsModuleResolutionStatus.Unique, selected.Status);
        Assert.Equal(selectedContainer, selected.Module!.ContainerId);
        Assert.Equal("USB\\VID_2CA3&PID_4006\\PORT_A", selected.Module.ParentInstanceId);
        Assert.Contains(selected.Module.LocationPaths, path => path.Contains("USB(2)", StringComparison.Ordinal));
        Assert.Single(selected.Module.AtControlInterfaces);
        Assert.Equal(3, selected.Module.AtControlInterfaces[0].InterfaceNumber);
    }

    [Fact]
    public void MissingContainerParentOrLocationFailsClosed()
    {
        var incomplete = new WindowsPnpDeviceNode(
            "USB\\VID_2CA3&PID_4006&MI_11\\DEVICE",
            null,
            null,
            ImmutableArray<string>.Empty,
            ["USB\\VID_2CA3&PID_4006&MI_11"],
            "AT Control Port");

        var result = WindowsModuleIdentityResolver.Resolve([incomplete]);

        Assert.Equal(WindowsModuleResolutionStatus.IdentityIncomplete, result.Status);
        Assert.Null(result.Module);
    }

    [Fact]
    public void InterfaceNumberIsObservedButNeverUsedAsTheControlRole()
    {
        var node = new WindowsPnpDeviceNode(
            "USB\\VID_2CA3&PID_4006&MI_09\\DEVICE",
            "USB\\VID_2CA3&PID_4006\\PARENT",
            Guid.Parse("2f123456-1111-2222-3333-000000000009"),
            ["PCIROOT(0)#USBROOT(0)#USB(9)#USBMI(09)"],
            ["USB\\VID_2CA3&PID_4006&MI_09"],
            "AC Interface");

        var result = WindowsModuleIdentityResolver.Resolve([node]);

        Assert.Equal(WindowsModuleResolutionStatus.Unique, result.Status);
        var mapped = Assert.Single(result.Module!.Interfaces);
        Assert.Equal(9, mapped.InterfaceNumber);
        Assert.Equal(WindowsInterfaceFunction.Unknown, mapped.Function);
        Assert.Empty(result.Module.AtControlInterfaces);
    }

    private static IReadOnlyList<WindowsPnpDeviceNode> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "windows-dual-module-same-name.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("deviceNodes").EnumerateArray().Select(node =>
            new WindowsPnpDeviceNode(
                node.GetProperty("instanceId").GetString()!,
                node.GetProperty("parentInstanceId").GetString(),
                node.GetProperty("containerId").GetGuid(),
                [.. node.GetProperty("locationPaths").EnumerateArray().Select(item => item.GetString()!)],
                [.. node.GetProperty("hardwareIds").EnumerateArray().Select(item => item.GetString()!)],
                node.GetProperty("friendlyName").GetString())).ToArray();
    }
}
