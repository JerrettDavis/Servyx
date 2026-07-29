namespace Servyx.Infrastructure.Aws;

// The three mutating actions an EC2 instance-type change needs, plus the one read that makes each of them
// checkable, kept in their own file so the provisioning client in Ec2QueryApiClient.cs stays exactly the five
// actions it was. (A plain comment rather than an XML one: the type's documentation lives on the first part,
// and a partial type carries its doc comment once.)
//
// WHY THREE CALLS AND NOT ONE. Neither sibling adapter needs this file's shape. A DigitalOcean resize is a
// single action POST; an Azure resize is a single PATCH. EC2 has no equivalent: ModifyInstanceAttribute
// refuses to write the instanceType attribute of an instance that is not STOPPED, and there is no live form
// of it. So the sequence is StopInstances, ModifyInstanceAttribute, StartInstances - and the middle of that
// sequence is a deliberately powered-down machine. Everything in this file exists to make each of the three
// steps observable, because a caller who cannot tell which of them ran cannot tell whether their server is
// down.
//
// THERE IS NO FORCE. StopInstances accepts a Force parameter that skips the guest's own shutdown - AWS's own
// documentation warns it can corrupt the filesystem and that any data not flushed is lost. It is never sent
// from here, and it is not a parameter of StopInstanceAsync, so no caller of this client can ask for it and no
// argument to anything above can turn a clean stop into an abrupt one. That is the same rule as everywhere
// else on the update path, expressed as an absent parameter rather than as a default.
//
// SUBMISSION IS NOT SUCCESS, TWICE OVER. StopInstances answers with the instance in state 'stopping' and
// StartInstances answers with it in 'pending'; neither response describes a finished operation. Both are
// therefore returned as the CURRENT STATE the service reported, never as a boolean success, and
// PollInstanceAsync below is the only thing in this assembly that can say an instance reached a state - from a
// DescribeInstances read that observed it.
internal sealed partial class Ec2QueryApiClient
{
    /// <summary>The instance state <c>ModifyInstanceAttribute</c> requires before it will write the type.</summary>
    internal const string StoppedState = "stopped";

    /// <summary>The instance state a started instance reaches when it is serving again.</summary>
    internal const string RunningState = "running";

    /// <summary>
    /// Asks EC2 to stop one instance cleanly, and returns the state EC2 reports for it as it answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stop is not a terminate. The instance keeps its id and every EBS volume in its block device mapping
    /// stays attached — <c>DeleteOnTermination</c> is consulted on termination and on nothing else — which is
    /// the fact an instance-type change's data claim rests on.
    /// </para>
    /// <para>
    /// The returned state is almost always <c>stopping</c>: this is a submission, and the only thing that can
    /// establish the instance actually stopped is <see cref="PollInstanceAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="instanceId">The instance to stop.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The <c>currentState</c> name EC2 reported, or <see langword="null"/> if it named none.</returns>
    internal async Task<string?> StopInstanceAsync(string instanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var response = await PostAsync(
                "StopInstances",
                [new KeyValuePair<string, string>("InstanceId.1", instanceId)],
                "stop an instance",
                ct)
            .ConfigureAwait(false);

        return CurrentStateOf(response);
    }

    /// <summary>Asks EC2 to start one instance, and returns the state EC2 reports for it as it answers.</summary>
    /// <remarks>
    /// The returned state is almost always <c>pending</c>. As with <see cref="StopInstanceAsync"/>, only
    /// <see cref="PollInstanceAsync"/> can establish that the instance reached <see cref="RunningState"/>.
    /// </remarks>
    /// <param name="instanceId">The instance to start.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The <c>currentState</c> name EC2 reported, or <see langword="null"/> if it named none.</returns>
    internal async Task<string?> StartInstanceAsync(string instanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var response = await PostAsync(
                "StartInstances",
                [new KeyValuePair<string, string>("InstanceId.1", instanceId)],
                "start an instance",
                ct)
            .ConfigureAwait(false);

        return CurrentStateOf(response);
    }

    /// <summary>
    /// Writes one attribute of one stopped instance: its instance type, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ModifyInstanceAttribute</c> writes exactly one attribute per call, which is what makes this the
    /// narrowest of the three adapters' resize requests: the parameter list below carries an instance id and
    /// <c>InstanceType.Value</c>, and there is no argument to this method that could add a second attribute.
    /// In particular there is no way to reach the <c>ImageId</c> attribute from here — and there would be no
    /// point, because EC2 refuses to change a running instance's AMI by any route at all.
    /// </para>
    /// <para>
    /// EC2 answers a successful attribute write with <c>&lt;return&gt;true&lt;/return&gt;</c>. A response
    /// carrying <c>false</c> is a refusal that arrived with a 200, so it is surfaced as
    /// <see langword="false"/> rather than being read as success.
    /// </para>
    /// </remarks>
    /// <param name="instanceId">The stopped instance whose type is being written.</param>
    /// <param name="instanceType">The instance type to write, e.g. <c>t3.large</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What EC2's <c>return</c> element said.</returns>
    internal async Task<bool> ModifyInstanceTypeAsync(string instanceId, string instanceType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceType);

        var response = await PostAsync(
                "ModifyInstanceAttribute",
                [
                    new KeyValuePair<string, string>("InstanceId", instanceId),
                    new KeyValuePair<string, string>("InstanceType.Value", instanceType),
                ],
                "change an instance's type",
                ct)
            .ConfigureAwait(false);

        return !string.Equals(Ec2Xml.Text(response, "return"), "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads one instance repeatedly until it satisfies <paramref name="satisfied"/>, it goes away, or the
    /// attempts are spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only thing in this assembly that can turn a submitted EC2 operation into an observed one. Each
    /// attempt is a <c>DescribeInstances</c> read; the first happens immediately, because a stop submitted
    /// against an already-stopped instance is finished before the first interval elapses.
    /// </para>
    /// <para>
    /// <strong>"Gone" is checked as a state, not as a 404.</strong> EC2 keeps a terminated instance visible to
    /// <c>DescribeInstances</c> for about an hour, so an instance that is terminated mid-poll would otherwise
    /// satisfy nothing and simply exhaust the attempts, reporting "still waiting" for a machine that has
    /// stopped existing. Both spellings — the 404 and the terminal state — answer
    /// <see cref="Ec2PollOutcome.Gone"/>.
    /// </para>
    /// <para>
    /// An <see cref="AwsApiException"/> from a read is <em>not</em> swallowed. A poll that cannot read the
    /// instance has no idea what state the machine is in, and returning "still waiting" would claim knowledge
    /// this method does not have.
    /// </para>
    /// </remarks>
    /// <param name="instanceId">The instance to watch.</param>
    /// <param name="satisfied">What is being waited for, evaluated against each observation.</param>
    /// <param name="interval">How long to wait between reads.</param>
    /// <param name="attempts">How many reads to make at most. At least one.</param>
    /// <param name="timeProvider">The clock the waits are taken from.</param>
    /// <param name="ct">Cancellation.</param>
    internal async Task<Ec2InstancePoll> PollInstanceAsync(
        string instanceId,
        Func<Ec2Instance, bool> satisfied,
        TimeSpan interval,
        int attempts,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(satisfied);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        string? state = null;

        for (var poll = 1; poll <= attempts; poll++)
        {
            var instance = await DescribeInstanceAsync(instanceId, ct).ConfigureAwait(false);

            if (instance is null)
            {
                return new Ec2InstancePoll(Ec2PollOutcome.Gone, null, null, poll);
            }

            state = instance.State;

            if (instance.IsGone)
            {
                return new Ec2InstancePoll(Ec2PollOutcome.Gone, instance, state, poll);
            }

            if (satisfied(instance))
            {
                return new Ec2InstancePoll(Ec2PollOutcome.Satisfied, instance, state, poll);
            }

            if (poll < attempts)
            {
                await Task.Delay(interval, timeProvider, ct).ConfigureAwait(false);
            }
        }

        return new Ec2InstancePoll(Ec2PollOutcome.StillWaiting, null, state, attempts);
    }

    /// <summary>The <c>currentState</c> name of the first instance in a Stop/Start response.</summary>
    private static string? CurrentStateOf(System.Xml.Linq.XElement response) =>
        Ec2Xml.Items(response, "instancesSet")
            .Select(item => Ec2Xml.Text(Ec2Xml.Child(item, "currentState"), "name"))
            .FirstOrDefault(name => name is not null);
}

/// <summary>How a wait on an EC2 instance ended.</summary>
/// <remarks>
/// Three cases and not two, for the reason <see cref="Servyx.Domain.Provisioning.UpdateExecutionResult"/> has
/// four: an operator has to be able to tell "it has not got there yet" from "the machine is not there any
/// more", because the first may still resolve itself and the second never will.
/// </remarks>
internal enum Ec2PollOutcome
{
    /// <summary>The instance was observed in the state that was being waited for.</summary>
    Satisfied,

    /// <summary>EC2 no longer has the instance, or reports it terminated or shutting down.</summary>
    Gone,

    /// <summary>The attempts were spent and the instance had still not reached the state.</summary>
    StillWaiting,
}

/// <summary>The result of waiting on one EC2 instance.</summary>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Instance">
/// The observation that ended the wait, when there was one. <see langword="null"/> for
/// <see cref="Ec2PollOutcome.StillWaiting"/>, because nothing was established about the instance.
/// </param>
/// <param name="State">The last state EC2 reported, whatever the outcome. Carried so a message can name it.</param>
/// <param name="Polls">How many reads were made — the number a message quotes as "after N check(s)".</param>
internal sealed record Ec2InstancePoll(
    Ec2PollOutcome Outcome,
    Ec2Instance? Instance,
    string? State,
    int Polls);
