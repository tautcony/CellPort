using System.Reflection;
using System.Runtime.Versioning;
using CellPort.Audio.Core.Models;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public class AudioCoreContractTests
{
    [Fact]
    public void FailureKeepsStructuredRecoveryInformationWithoutAValue()
    {
        var error = new AudioOperationError(
            AudioErrorCode.DeviceBusyData,
            AudioOperationStage.CallState,
            Retryable: true,
            AudioRecoveryStatus.NotRequired);

        var result = AudioOperationResult<ModuleRuntimeStatus>.Failure(error);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void SuccessHasAValueAndNoError()
    {
        var status = new ModuleRuntimeStatus(
            ModuleAudioPhase.StandbyReady,
            Generation: 4,
            ProfileId: "candidate",
            CompatibilityApproved: false,
            RouteReady: false,
            AudioRecoveryStatus.NotRequired,
            LastErrorCode: null);

        var result = AudioOperationResult<ModuleRuntimeStatus>.Success(status);

        Assert.True(result.Succeeded);
        Assert.Same(status, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void OpaqueIdentifiersDoNotLeakThroughDefaultFormatting()
    {
        var module = new ModuleDeviceId("physical-parent-secret");
        var endpoint = new AudioEndpointId("native-endpoint-secret");

        Assert.DoesNotContain("physical-parent-secret", module.ToString());
        Assert.DoesNotContain("native-endpoint-secret", endpoint.ToString());
    }

    [Fact]
    public void AudioCoreTargetsPortableDotNetAndHasNoHostPlatformReferences()
    {
        var assembly = typeof(AudioOperationResult<>).Assembly;
        Assert.Equal(
            ".NETCoreApp,Version=v10.0",
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "PresentationFramework" or "System.Management" or "System.IO.Ports" ||
            reference.Name!.StartsWith("CellPort.Platform.", StringComparison.Ordinal));
    }
}
