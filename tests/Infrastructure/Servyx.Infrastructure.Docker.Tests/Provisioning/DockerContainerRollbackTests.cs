using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Unit tests for the recreate half of <see cref="DockerContainerProvisioner"/> — the update that records
/// what a container was, and the rollback that restores it. Same house pattern as
/// <see cref="DockerContainerMaintenanceTests"/>: the Docker Engine is an NSubstitute-substituted
/// <see cref="IDockerClient"/>, so no live daemon is involved and every engine call is countable.
/// </summary>
/// <remarks>
/// <para>
/// The negative these tests exist for is the one that made a rollback dishonest before it was written:
/// <em>nothing recorded what a container was</em>. A <see cref="ResourceHandle"/> carries labels, so the
/// ledger knew the image and the root path and nothing about ports, environment or mounts — and the
/// container, which knew all of them, is exactly what a recreate deletes. The tests below therefore pin two
/// things above all: that a rollback with no recorded prior state <em>refuses without touching the engine</em>,
/// and that the recorded state a rollback does restore came from a real observation of the live container
/// rather than from a default.
/// </para>
/// <para>
/// <see cref="FakeEngine"/> is stateful rather than a bare stub because the interesting properties are
/// end-to-end: a container is created, updated, and rolled back against the same engine, and the assertions
/// are on the parameters the engine actually received.
/// </para>
/// </remarks>
public class DockerContainerRollbackTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";
    private const string OriginalImage = "thijsvanloef/palworld-server-docker:latest";
    private const string UpdatedImage = "thijsvanloef/palworld-server-docker:v2";

    // ── No recorded prior state ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanRollbackAsync_refuses_a_container_that_has_never_been_updated_and_invents_nothing()
    {
        // The container exists, is Servyx-managed, and carries a servyx.image label — so an adapter that
        // wanted to could have fabricated a "previous" state from it. This one refuses instead.
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var created = await provisioner.CreateOperation(DockerContainerProvisionerTests.PalworldRequest()).CreateAsync();

        var result = await provisioner.PlanRollbackAsync(created.Handle);

        result.Should().BeOfType<DockerRollbackPlan.NoRecordedPriorState>();
        result.Message.Should().Contain(ServyxResourceTags.PreviousSpecLabel)
            .And.Contain("will not reconstruct");
    }

    [Fact]
    public async Task PrepareRollbackAsync_with_no_recorded_prior_state_issues_no_mutating_docker_call()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var created = await provisioner.CreateOperation(DockerContainerProvisionerTests.PalworldRequest()).CreateAsync();
        engine.Containers.ClearReceivedCalls();

        var confirmation = await provisioner.PrepareRollbackAsync(created.Handle, "any-hash-at-all");

        confirmation.Should().BeOfType<DockerRecreateConfirmation.Refused>();
        confirmation.Message.Should().Contain("Nothing was sent to the Docker Engine");

        // The whole engine call log, so a mutating call added later cannot slip past an enumerated list.
        engine.Containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
    }

    [Fact]
    public async Task PlanRollbackAsync_refuses_another_provisioners_handle_without_touching_the_engine()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);

        var result = await provisioner.PlanRollbackAsync(
            new ResourceHandle("hetzner", "srv-77", "nbg1", new Dictionary<string, string>()));

        result.Should().BeOfType<DockerRollbackPlan.Refused>();
        engine.Containers.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PlanRollbackAsync_reports_a_vanished_container_as_gone_rather_than_as_nothing_recorded()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);

        var result = await provisioner.PlanRollbackAsync(
            new ResourceHandle(DockerContainerProvisioner.Id, "container-does-not-exist", null, new Dictionary<string, string>()));

        result.Should().BeOfType<DockerRollbackPlan.ResourceGone>();
    }

    [Fact]
    public async Task A_caller_cannot_plant_a_prior_state_through_a_label_parameter()
    {
        // A `label:servyx.previous-spec` provisioning parameter would otherwise let a caller record a state
        // Servyx never observed, which a later rollback would restore as if it had been.
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var planted = DockerContainerProvisionerTests.PalworldRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"label:{ServyxResourceTags.PreviousSpecLabel}"] = """{"Image":"attacker/image:1","ContainerName":"x","InstanceId":"srv-0001","JobId":"job-42","ConnectorId":"docker-local"}""",
        });

        var created = await provisioner.CreateOperation(planted).CreateAsync();

        engine.Created[0].Labels.Should().NotContainKey(ServyxResourceTags.PreviousSpecLabel);
        (await provisioner.PlanRollbackAsync(created.Handle)).Should().BeOfType<DockerRollbackPlan.NoRecordedPriorState>();
    }

    // ── The recording an update leaves behind ────────────────────────────────────────────────────

    [Fact]
    public async Task PrepareUpdateAsync_records_what_the_container_is_now_on_the_replacement()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);

        var recorded = engine.Created[1].Labels.Should().ContainKey(ServyxResourceTags.PreviousSpecLabel).WhoseValue;

        // The record is of the container that was replaced, not of the one that replaced it.
        recorded.Should().Contain(OriginalImage).And.NotContain(UpdatedImage);
        recorded.Should().Contain("8211").And.Contain("/palworld").And.Contain("SERVER_NAME");

        updated.Handle.ProviderResourceId.Should().NotBe("container-1", "a recreate produces a new container id");
    }

    [Fact]
    public async Task An_update_of_a_container_Servyx_cannot_record_is_refused_rather_than_applied()
    {
        // Applying it would delete the only copy of what the container is, leaving nothing to roll back to.
        var (client, containers) = Unmanaged();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);
        var handle = new ResourceHandle(DockerContainerProvisioner.Id, "foreign-1", null, new Dictionary<string, string>());

        var plan = await provisioner.PlanUpdateAsync(handle, DockerContainerProvisionerTests.PalworldRequest());
        var confirmation = await provisioner.PrepareUpdateAsync(
            handle,
            DockerContainerProvisionerTests.PalworldRequest(),
            plan!.PlanHash,
            plan.DataImpact == DataImpact.Preserved ? null : plan.DataImpact);

        confirmation.Should().BeOfType<DockerRecreateConfirmation.Refused>();
        confirmation.Message.Should().Contain("nothing to roll back to");

        containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
    }

    // ── The rollback itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rollback_restores_the_recorded_image_and_port_configuration()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        await RollBackAsync(provisioner);

        var original = engine.Created[0];
        var restored = engine.Created[2];

        restored.Image.Should().Be(OriginalImage);
        restored.Image.Should().Be(original.Image);
        restored.Name.Should().Be(original.Name);

        // The published bindings and the exposed set, compared as the engine received them.
        Describe(restored.HostConfig.PortBindings).Should().Equal(Describe(original.HostConfig.PortBindings));
        restored.ExposedPorts.Keys.Order(StringComparer.Ordinal)
            .Should().Equal(original.ExposedPorts.Keys.Order(StringComparer.Ordinal));

        // Environment is part of the recorded spec too, and is restored with it.
        restored.Env.Order(StringComparer.Ordinal).Should().Equal(original.Env.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Volumes_survive_a_rollback()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        await RollBackAsync(provisioner);

        // 1. No removal on any path — the update's or the rollback's — ever asks the engine to take the
        //    volumes with the container. This is the assertion that makes DataImpact.Preserved defensible.
        engine.Removed.Should().HaveCount(2);
        engine.Removed.Should().OnlyContain(p => p.RemoveVolumes == false);

        // 2. The restored container is attached to exactly the mounts the original had, at the same host
        //    paths with the same access. Bytes surviving is not enough: a container that comes up attached to
        //    nothing has preserved the data and lost the state.
        engine.Created[2].HostConfig.Binds.Order(StringComparer.Ordinal)
            .Should().Equal(engine.Created[0].HostConfig.Binds.Order(StringComparer.Ordinal));
        engine.Created[2].HostConfig.Binds.Should().ContainSingle()
            .Which.Should().Contain(@"C:\srv\palworld").And.Contain("/palworld").And.EndWith(":rw");
    }

    [Fact]
    public async Task A_rollback_of_a_container_whose_mounts_all_carry_over_states_its_data_impact_as_preserved()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);

        var planned = (await provisioner.PlanRollbackAsync(updated.Handle)).Should().BeOfType<DockerRollbackPlan.Planned>().Which;

        Enum.IsDefined(planned.Plan.DataImpact).Should().BeTrue();
        planned.Plan.DataImpact.Should().Be(DataImpact.Preserved);
        planned.Plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        planned.Plan.Changes.Should().ContainSingle(c => c.Aspect == "image")
            .Which.Should().BeEquivalentTo(new { Current = UpdatedImage, Desired = OriginalImage, RequiresRecreate = true });
    }

    [Fact]
    public async Task Planning_a_rollback_mutates_nothing()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);
        engine.Containers.ClearReceivedCalls();

        var planned = await provisioner.PlanRollbackAsync(updated.Handle);

        planned.Should().BeOfType<DockerRollbackPlan.Planned>();
        engine.Containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
        engine.Containers.ReceivedCalls().Should().HaveCount(1);
    }

    // ── The rollback records itself ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rollback_is_recorded_so_a_second_consecutive_rollback_refuses_instead_of_re_applying_the_update()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var restored = await RollBackAsync(provisioner);

        var labels = engine.Created[2].Labels;
        labels.Should().ContainKey(ServyxResourceTags.RolledBackAtLabel);
        labels.Should().ContainKey(ServyxResourceTags.RolledBackFromLabel);

        // The load-bearing absence. Carrying the undone update forward as a "previous spec" would make the
        // next rollback restore it — which is re-applying the update under the name of undoing one.
        labels.Should().NotContainKey(ServyxResourceTags.PreviousSpecLabel);

        var second = await provisioner.PlanRollbackAsync(restored.Handle);
        second.Should().BeOfType<DockerRollbackPlan.NoRecordedPriorState>();

        // And the refusal is not merely advisory: confirming it does nothing either.
        engine.Containers.ClearReceivedCalls();
        (await provisioner.PrepareRollbackAsync(restored.Handle, "whatever-hash"))
            .Should().BeOfType<DockerRecreateConfirmation.Refused>();
        engine.Created.Should().HaveCount(3, "no fourth container was created");
    }

    [Fact]
    public async Task The_rollback_operation_is_an_ordinary_provisioning_operation_so_the_ledger_records_it()
    {
        // It goes through the same IProvisioningOperation seam the create path uses, which is what puts the
        // write-ahead ledger row in front of the first mutating call and stamps the new container id onto it.
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);

        var planned = (await provisioner.PlanRollbackAsync(updated.Handle)).Should().BeOfType<DockerRollbackPlan.Planned>().Which;
        var ready = (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash))
            .Should().BeOfType<DockerRecreateConfirmation.Ready>().Which;

        ready.Operation.ProvisionerId.Should().Be(DockerContainerProvisioner.Id);
        ready.Operation.Region.Should().BeNull();
        ready.Operation.Tags.Should().ContainKey(ServyxResourceTags.ManagedLabel);
        ready.Operation.Tags.Should().ContainKey(ServyxResourceTags.InstanceIdLabel);
        ready.Operation.Tags[ServyxResourceTags.ImageLabel].Should().Be(OriginalImage);

        // Preparing is not applying: nothing has been stopped, removed, or created by getting this far.
        engine.Created.Should().HaveCount(2);
        engine.Removed.Should().HaveCount(1);
    }

    // ── The gate ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrepareRollbackAsync_refuses_a_hash_that_is_not_the_plan_the_container_now_produces()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);
        engine.Containers.ClearReceivedCalls();

        var confirmation = await provisioner.PrepareRollbackAsync(updated.Handle, "not-the-approved-hash");

        confirmation.Should().BeOfType<DockerRecreateConfirmation.Refused>();
        confirmation.Message.Should().Contain("not-the-approved-hash").And.Contain("Nothing was sent to the Docker Engine");
        engine.Containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
    }

    [Fact]
    public async Task A_rollback_that_does_not_preserve_data_refuses_without_the_matching_acknowledgement()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner, MountlessRequest(OriginalImage), MountlessRequest(UpdatedImage));

        var planned = (await provisioner.PlanRollbackAsync(updated.Handle)).Should().BeOfType<DockerRollbackPlan.Planned>().Which;

        // No mounts means the state lives in the writable layer the recreate discards. The rollback says so,
        // in the same words an update does, and demands the same acknowledgement.
        planned.Plan.DataImpact.Should().Be(DataImpact.AtRisk);

        (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash))
            .Should().BeOfType<DockerRecreateConfirmation.Refused>()
            .Which.Message.Should().Contain(nameof(DataImpact.AtRisk));

        // The wrong acknowledgement is a mismatch too, not a near-enough.
        (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash, DataImpact.Destroyed))
            .Should().BeOfType<DockerRecreateConfirmation.Refused>();

        // Only the exactly-matching one gets through.
        (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash, DataImpact.AtRisk))
            .Should().BeOfType<DockerRecreateConfirmation.Ready>();
    }

    [Fact]
    public async Task A_preserving_rollback_refuses_an_acknowledgement_it_did_not_ask_for()
    {
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);
        var planned = (await provisioner.PlanRollbackAsync(updated.Handle)).Should().BeOfType<DockerRollbackPlan.Planned>().Which;

        (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash, DataImpact.AtRisk))
            .Should().BeOfType<DockerRecreateConfirmation.Refused>();
    }

    [Fact]
    public async Task The_operation_re_checks_the_approval_immediately_before_the_first_mutating_call()
    {
        // The gate is not a formality: a container that moves between confirmation and execution stops the
        // recreate, and nothing is stopped, removed, or created.
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var (_, updated) = await UpdateAsync(provisioner);

        var planned = (await provisioner.PlanRollbackAsync(updated.Handle)).Should().BeOfType<DockerRollbackPlan.Planned>().Which;
        var ready = (await provisioner.PrepareRollbackAsync(updated.Handle, planned.Plan.PlanHash))
            .Should().BeOfType<DockerRecreateConfirmation.Ready>().Which;

        engine.Relabel(updated.Handle.ProviderResourceId, ServyxResourceTags.JobIdLabel, "job-somebody-else");
        engine.Containers.ClearReceivedCalls();

        var act = async () => await ready.Operation.CreateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has changed since the plan was approved*");

        engine.Containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
    }

    // ── Round-trip fidelity of the record ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_state_recorded_for_a_container_describes_that_container_exactly()
    {
        // If the capture were lossy, a rollback would restore something subtly different while reporting a
        // faithful restore. Proved end-to-end: rolling back and then planning an update to the pre-update
        // request finds nothing to change.
        var engine = new FakeEngine();
        var provisioner = new DockerContainerProvisioner(engine.Client, Endpoint);
        var restored = await RollBackAsync(provisioner);

        var plan = await provisioner.PlanUpdateAsync(restored.Handle, DockerContainerProvisionerTests.PalworldRequest());

        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A request with no mounts, so a recreate of it is honestly <see cref="DataImpact.AtRisk"/>.</summary>
    private static ProvisioningRequest MountlessRequest(string image) =>
        new(
            "palworld",
            "docker-thijsvanloef",
            ConnectorId: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = image,
                ["containerName"] = "palworld-mountless",
                ["instanceId"] = "srv-0002",
                ["jobId"] = "job-43",
                ["connectorId"] = "docker-local",
                ["rootPath"] = "/palworld",
                ["port:8211/udp"] = "8211",
            });

    /// <summary>Creates a container, then updates it through the gated path. Returns both resources.</summary>
    private static async Task<(ProvisionedResource Original, ProvisionedResource Updated)> UpdateAsync(
        DockerContainerProvisioner provisioner,
        ProvisioningRequest? before = null,
        ProvisioningRequest? after = null)
    {
        var original = await provisioner.CreateOperation(before ?? DockerContainerProvisionerTests.PalworldRequest()).CreateAsync();

        var desired = after ?? DockerContainerProvisionerTests.PalworldRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = UpdatedImage,
        });

        var plan = await provisioner.PlanUpdateAsync(original.Handle, desired);
        var confirmation = await provisioner.PrepareUpdateAsync(
            original.Handle,
            desired,
            plan!.PlanHash,
            plan.DataImpact == DataImpact.Preserved ? null : plan.DataImpact);

        var ready = confirmation.Should().BeOfType<DockerRecreateConfirmation.Ready>().Which;
        var updated = await ready.Operation.CreateAsync();

        return (original, updated);
    }

    /// <summary>Creates, updates, then rolls back through the gated path. Returns the restored resource.</summary>
    private static async Task<ProvisionedResource> RollBackAsync(DockerContainerProvisioner provisioner)
    {
        var (_, updated) = await UpdateAsync(provisioner);

        var planned = (await provisioner.PlanRollbackAsync(updated.Handle))
            .Should().BeOfType<DockerRollbackPlan.Planned>().Which;

        var ready = (await provisioner.PrepareRollbackAsync(
            updated.Handle,
            planned.Plan.PlanHash,
            planned.Plan.DataImpact == DataImpact.Preserved ? null : planned.Plan.DataImpact))
            .Should().BeOfType<DockerRecreateConfirmation.Ready>().Which;

        return await ready.Operation.CreateAsync();
    }

    private static IReadOnlyList<string> Describe(IDictionary<string, IList<PortBinding>> bindings) =>
        [.. bindings.Select(b => $"{b.Key}->{string.Join(',', b.Value.Select(v => v.HostPort))}").Order(StringComparer.Ordinal)];

    /// <summary>An engine whose one container carries no Servyx labels at all.</summary>
    private static (IDockerClient Client, IContainerOperations Containers) Unmanaged()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);

        containers.InspectContainerAsync("foreign-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new ContainerInspectResponse
            {
                ID = "foreign-1",
                Name = "/somebody-elses-container",
                Config = new Config
                {
                    Image = "nginx:1.27",
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal),
                    ExposedPorts = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal),
                },
                HostConfig = new HostConfig(),
                State = new ContainerState { Status = "running", Running = true },
            }));

        return (client, containers);
    }

    /// <summary>
    /// A substituted Docker Engine that remembers the containers created against it, so an update and a
    /// rollback can be driven end-to-end and asserted on the parameters the engine actually received.
    /// </summary>
    private sealed class FakeEngine
    {
        private readonly Dictionary<string, ContainerInspectResponse> _byId = new(StringComparer.Ordinal);
        private int _next;

        public FakeEngine()
        {
            Client = Substitute.For<IDockerClient>();
            Containers = Substitute.For<IContainerOperations>();
            Client.Containers.Returns(Containers);

            Containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => _byId.TryGetValue(call.ArgAt<string>(0), out var inspect)
                    ? Task.FromResult(inspect)
                    : Task.FromException<ContainerInspectResponse>(
                        new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container")));

            Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var parameters = call.ArgAt<CreateContainerParameters>(0);
                    Created.Add(parameters);
                    var id = $"container-{++_next}";
                    _byId[id] = ToInspect(id, parameters);
                    return Task.FromResult(new CreateContainerResponse { ID = id });
                });

            Containers.StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(true));

            Containers.StopContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Stopped.Add(call.ArgAt<string>(0));
                    return Task.FromResult(true);
                });

            Containers
                .When(c => c.RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>()))
                .Do(call =>
                {
                    Removed.Add(call.ArgAt<ContainerRemoveParameters>(1));
                    _byId.Remove(call.ArgAt<string>(0));
                });
        }

        public IDockerClient Client { get; }

        public IContainerOperations Containers { get; }

        /// <summary>Every create the engine received, in order.</summary>
        public List<CreateContainerParameters> Created { get; } = [];

        /// <summary>The parameters of every removal the engine received, in order.</summary>
        public List<ContainerRemoveParameters> Removed { get; } = [];

        /// <summary>Every container id the engine was asked to stop, in order.</summary>
        public List<string> Stopped { get; } = [];

        /// <summary>Changes a live container's label, simulating drift under the operator.</summary>
        public void Relabel(string containerId, string key, string value) =>
            _byId[containerId].Config.Labels[key] = value;

        /// <summary>
        /// What the engine would report for a container it just created. Faithful enough that a plan computed
        /// against it round-trips, which is what makes the end-to-end assertions meaningful.
        /// </summary>
        private static ContainerInspectResponse ToInspect(string id, CreateContainerParameters parameters)
        {
            var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
            foreach (var key in parameters.ExposedPorts?.Keys ?? [])
            {
                exposed[key] = default;
            }

            var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);
            foreach (var binding in parameters.HostConfig?.PortBindings ?? new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal))
            {
                bindings[binding.Key] = [.. binding.Value];
            }

            var binds = parameters.HostConfig?.Binds ?? [];
            var mounts = new List<MountPoint>();
            foreach (var bind in binds)
            {
                // `<host>:<container>:<mode>`, split from the right so a Windows drive-letter colon is safe.
                var parts = bind.Split(':');
                mounts.Add(new MountPoint
                {
                    Type = "bind",
                    Source = string.Join(':', parts[..^2]),
                    Destination = parts[^2],
                    RW = string.Equals(parts[^1], "rw", StringComparison.OrdinalIgnoreCase),
                });
            }

            return new ContainerInspectResponse
            {
                ID = id,
                Name = "/" + parameters.Name,
                Created = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
                Config = new Config
                {
                    Image = parameters.Image,
                    Labels = new Dictionary<string, string>(parameters.Labels, StringComparer.Ordinal),
                    ExposedPorts = exposed,
                    Env = [.. parameters.Env ?? []],
                },
                HostConfig = new HostConfig
                {
                    PortBindings = bindings,
                    Binds = [.. binds],
                    RestartPolicy = parameters.HostConfig?.RestartPolicy,
                },
                Mounts = mounts,
                NetworkSettings = new NetworkSettings { IPAddress = "172.18.0.2" },
                State = new ContainerState { Status = "running", Running = true },
            };
        }
    }
}
