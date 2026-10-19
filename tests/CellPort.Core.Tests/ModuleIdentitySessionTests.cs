using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class ModuleIdentitySessionTests
{
    [Fact]
    public void SamePortReplacementWithNewContainerInvalidatesOldSession()
    {
        var session = new ModuleIdentitySession();
        var lease = session.Begin(Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a"));

        var status = session.Revalidate(lease,
            Evidence("container-b", "USB\\PORT_2", "boot-b", "owner-b"));

        Assert.Equal(ModuleIdentityRevalidationStatus.PhysicalIdentityChanged, status);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public void MovingTheSameDeviceToAnotherPortInvalidatesSession()
    {
        var session = new ModuleIdentitySession();
        var lease = session.Begin(Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a"));

        var status = session.Revalidate(lease,
            Evidence("container-a", "USB\\PORT_5", "boot-a", "owner-a"));

        Assert.Equal(ModuleIdentityRevalidationStatus.PhysicalIdentityChanged, status);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public void OldBootAndOwnerEvidenceCannotRestoreThePreviousSession()
    {
        var bootSession = new ModuleIdentitySession();
        var bootLease = bootSession.Begin(Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a"));
        Assert.Equal(ModuleIdentityRevalidationStatus.BootChanged,
            bootSession.Revalidate(bootLease, Evidence("container-a", "USB\\PORT_2", "boot-b", "owner-a")));

        var ownerSession = new ModuleIdentitySession();
        var ownerLease = ownerSession.Begin(Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a"));
        Assert.Equal(ModuleIdentityRevalidationStatus.OwnerChanged,
            ownerSession.Revalidate(ownerLease, Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-b")));
    }

    [Fact]
    public void LateAsynchronousResultIsRejectedAfterGenerationChanges()
    {
        var session = new ModuleIdentitySession();
        var lease = session.Begin(Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a"));
        var capturedGeneration = lease.Generation;
        session.Invalidate();

        Assert.False(session.IsCurrentGeneration(capturedGeneration));
        Assert.Equal(ModuleIdentityRevalidationStatus.StaleSession,
            session.Revalidate(lease, Evidence("container-a", "USB\\PORT_2", "boot-a", "owner-a")));

        var tracker = new CallTracker();
        var oldModuleGeneration = tracker.BeginModuleSession();
        tracker.MarkModuleDisconnected();
        tracker.BeginModuleSession();
        var applied = tracker.TryApply(
            ClccCallListParser.ParseSuccessfulLines(["+CLCC: 1,0,0,0,0"]),
            oldModuleGeneration,
            out var current);
        Assert.False(applied);
        Assert.True(current.IsUnknown);
        Assert.Empty(current.Calls);
    }

    [Fact]
    public void IdentityEvidenceStringDoesNotExposeBootOrOwnerSecrets()
    {
        var evidence = Evidence("container-a", "USB\\PORT_2", "private-boot-value", "private-owner-value");

        Assert.DoesNotContain("private-boot-value", evidence.ToString());
        Assert.DoesNotContain("private-owner-value", evidence.ToString());
    }

    private static ModuleIdentityEvidence Evidence(string container, string parent, string boot, string owner) =>
        new(Guid.Parse(container == "container-a"
                ? "2f123456-1111-2222-3333-000000000001"
                : "2f123456-1111-2222-3333-000000000002"),
            parent,
            [$"PCIROOT(0)#USBROOT(0)#{parent[(parent.LastIndexOf('\\') + 1)..]}"] ,
            reliableSerial: null,
            boot,
            owner);
}
