using System.Net.Http.Headers;
using System.Text;

using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Tests.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests;

/// <summary>A clock that never moves, so a signature is reproducible.</summary>
/// <remarks>
/// SigV4 mixes the timestamp into both the string to sign and the credential scope, so a signature is only a
/// stable value if the clock is. Every expected hex string in this file is therefore pinned to
/// <see cref="AwsSigV4Tests.SigningInstant"/>.
/// </remarks>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// The signer, pinned. This is the file that decides whether hand-rolling SigV4 was defensible.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two classes of assertion live here, and the difference matters.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>AWS-published test vectors.</strong> <c>get-vanilla</c> and <c>get-vanilla-query-order-key-case</c>
/// come from AWS's own SigV4 test suite, whose fixed inputs (access key <c>AKIDEXAMPLE</c>, the documented
/// example secret, region <c>us-east-1</c>, service <c>service</c>, <c>20150830T123600Z</c>) and expected
/// <c>Authorization</c> headers AWS publishes. These are not characterisation tests: the expected values were
/// not produced by the code under test, they are the values every AWS SDK is validated against. If this
/// implementation had a defect in canonicalisation, key derivation, or the string to sign, these would fail.
/// </description></item>
/// <item><description>
/// <strong>Golden EC2-shaped cases</strong>, further down, whose expected signatures were produced by an
/// <em>independent</em> reference implementation written from the specification rather than by this code. They
/// exist because the published vectors do not cover a POST with a real payload hash, and because a future
/// refactor must not be able to change a signature silently.
/// </description></item>
/// </list>
/// <para>
/// Every case below also exercises the pure steps individually — canonical request, string to sign, signing
/// key — so a failure names <em>which</em> step broke instead of only reporting a different 64-character hex
/// string.
/// </para>
/// </remarks>
public class AwsSigV4Tests
{
    /// <summary>The access key id AWS's published test suite uses.</summary>
    internal const string VectorAccessKeyId = "AKIDEXAMPLE";

    /// <summary>The secret access key AWS's published test suite uses.</summary>
    internal const string VectorSecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

    /// <summary>The instant AWS's published test suite signs at.</summary>
    internal static DateTimeOffset SigningInstant { get; } = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    private static ReadOnlySpan<byte> VectorSecretBytes => "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"u8;

    // -----------------------------------------------------------------------------------------------------
    // AWS's own published test vectors
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Get_vanilla_reproduces_the_signature_AWS_publishes_for_it()
    {
        // AWS SigV4 test suite, case "get-vanilla": GET / with only Host and X-Amz-Date, empty body.
        var canonicalRequest = AwsSigV4.CanonicalRequest(
            "GET",
            "/",
            string.Empty,
            "host:example.amazonaws.com\nx-amz-date:20150830T123600Z\n",
            "host;x-amz-date",
            AwsSigV4.HashedEmptyPayload);

        var scope = AwsSigV4.CredentialScope(SigningInstant, "us-east-1", "service");
        var stringToSign = AwsSigV4.StringToSign(SigningInstant, scope, canonicalRequest);

        scope.Should().Be("20150830/us-east-1/service/aws4_request");

        var signature = AwsSigV4.Signature(VectorSecretBytes, SigningInstant, "us-east-1", "service", stringToSign);

        // The published expected value. Not computed by this code, and not adjustable without breaking AWS.
        signature.Should().Be("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31");

        AwsSigV4.AuthorizationHeader(VectorAccessKeyId, scope, "host;x-amz-date", signature)
            .Should().Be(
                "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, "
                + "SignedHeaders=host;x-amz-date, "
                + "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31");
    }

    [Fact]
    public async Task Get_vanilla_reproduces_the_published_signature_through_the_whole_request_signer_too()
    {
        // The same vector, driven through the type that actually signs production requests rather than through
        // the pure helpers. This is what proves the plumbing - host derivation, header selection, timestamp
        // formatting - agrees with the algorithm, not just that the algorithm is right.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");

        await VectorSigner("service").SignAsync(request);

        Authorization(request).Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, "
            + "SignedHeaders=host;x-amz-date, "
            + "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31");

        Header(request, AwsSigV4.AmzDateHeader).Should().Be("20150830T123600Z");
    }

    [Fact]
    public async Task Get_vanilla_query_order_key_case_reproduces_the_signature_AWS_publishes_for_it()
    {
        // AWS SigV4 test suite, case "get-vanilla-query-order-key-case": the parameters arrive out of order and
        // must be canonicalised into byte order of the encoded name before signing.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://example.amazonaws.com/?Param2=value2&Param1=value1");

        await VectorSigner("service").SignAsync(request);

        Authorization(request).Should().EndWith(
            "Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500");

        // And the request that would go on the wire carries the canonical order, so what was signed and what
        // would be sent are the same bytes.
        request.RequestUri!.Query.Should().Be("?Param1=value1&Param2=value2");
    }

    [Fact]
    public void The_empty_payload_hash_constant_really_is_the_sha256_of_nothing()
    {
        // Written out as a constant so a reader can recognise it in a canonical request without computing it -
        // which is only safe if it is checked.
        AwsSigV4.Sha256Hex(ReadOnlySpan<byte>.Empty).Should().Be(AwsSigV4.HashedEmptyPayload);
        AwsSigV4.HashedEmptyPayload.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    // -----------------------------------------------------------------------------------------------------
    // URI encoding: the single most common SigV4 defect
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("a=b", "a%3Db")]
    [InlineData("a&b", "a%26b")]
    [InlineData("tag:servyx.managed", "tag%3Aservyx.managed")]
    [InlineData("-._~", "-._~")]
    [InlineData("ABCabc0189", "ABCabc0189")]
    [InlineData("ሴ", "%E1%88%B4")]
    public void UriEncode_leaves_the_unreserved_set_alone_and_percent_encodes_everything_else_in_upper_case(
        string input,
        string expected) =>
        AwsSigV4.UriEncode(input, encodeSlash: true).Should().Be(expected);

    [Fact]
    public void A_space_is_never_encoded_as_plus()
    {
        // The defect that produces a working form post and a broken signature. Stated as its own test because
        // it is the one that gets reintroduced by anyone who reaches for FormUrlEncodedContent.
        AwsSigV4.UriEncode("hello world", encodeSlash: true).Should().NotContain("+");
        AwsSigV4.UriEncode("hello world", encodeSlash: true).Should().Be("hello%20world");
    }

    [Theory]
    [InlineData(true, "a%2Fb")]
    [InlineData(false, "a/b")]
    public void The_slash_is_encoded_for_query_values_and_left_alone_for_path_segments(bool encodeSlash, string expected) =>
        AwsSigV4.UriEncode("a/b", encodeSlash).Should().Be(expected);

    // -----------------------------------------------------------------------------------------------------
    // Canonical query
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Canonical_query_sorts_by_the_byte_order_of_the_encoded_name() =>
        AwsSigV4.CanonicalQuery("?b=2&A=1&a=3&B=4").Should().Be("A=1&B=4&a=3&b=2");

    [Fact]
    public void Canonical_query_sorts_repeats_of_one_name_by_value() =>
        AwsSigV4.CanonicalQuery("?x=b&x=a&x=c").Should().Be("x=a&x=b&x=c");

    [Fact]
    public void Canonical_query_gives_a_valueless_parameter_an_empty_value() =>
        AwsSigV4.CanonicalQuery("?flag").Should().Be("flag=");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    public void Canonical_query_of_nothing_is_the_empty_string(string? raw) =>
        AwsSigV4.CanonicalQuery(raw).Should().BeEmpty();

    [Fact]
    public void Canonical_query_re_encodes_a_raw_non_ascii_parameter() =>
        AwsSigV4.CanonicalQuery("?ሴ=bar").Should().Be("%E1%88%B4=bar");

    [Fact]
    public void Canonical_query_normalises_lower_case_escapes_to_upper_case() =>
        AwsSigV4.CanonicalQuery("?k=a%3ab").Should().Be("k=a%3Ab");

    [Fact]
    public void Canonical_query_is_idempotent_on_its_own_output()
    {
        // The property the whole "signed == sent" guarantee rests on: canonicalising an already-canonical query
        // must be identity, or rewriting the request URI would loop or drift.
        const string Raw = "?Filter.1.Name=tag%3Aservyx.managed&Filter.1.Value.1=true&MaxResults=1000&b=a%20b";

        var once = AwsSigV4.CanonicalQuery(Raw);
        AwsSigV4.CanonicalQuery(once).Should().Be(once);
        AwsSigV4.CanonicalQuery("?" + once).Should().Be(once);
    }

    [Fact]
    public void Canonical_query_leaves_unreserved_characters_untouched()
    {
        const string Unreserved = "-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        AwsSigV4.CanonicalQuery($"?{Unreserved}={Unreserved}").Should().Be($"{Unreserved}={Unreserved}");
    }

    // -----------------------------------------------------------------------------------------------------
    // Canonical headers
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Canonical_headers_lower_case_names_sort_them_and_end_the_block_with_a_newline()
    {
        var (canonical, signed) = AwsSigV4.CanonicalHeaders(
        [
            new KeyValuePair<string, string>("X-Amz-Date", "20150830T123600Z"),
            new KeyValuePair<string, string>("Host", "example.amazonaws.com"),
        ]);

        canonical.Should().Be("host:example.amazonaws.com\nx-amz-date:20150830T123600Z\n");
        signed.Should().Be("host;x-amz-date");
    }

    [Fact]
    public void Canonical_headers_trim_a_value_and_collapse_internal_whitespace_runs()
    {
        var (canonical, _) = AwsSigV4.CanonicalHeaders(
        [
            new KeyValuePair<string, string>("My-Header1", "  value1  value2     value3  "),
        ]);

        canonical.Should().Be("my-header1:value1 value2 value3\n");
    }

    [Fact]
    public void Canonical_headers_join_repeats_of_one_name_with_a_comma_in_the_order_given()
    {
        var (canonical, signed) = AwsSigV4.CanonicalHeaders(
        [
            new KeyValuePair<string, string>("My-Header1", "value2"),
            new KeyValuePair<string, string>("my-header1", "value1"),
        ]);

        canonical.Should().Be("my-header1:value2,value1\n");
        signed.Should().Be("my-header1");
    }

    [Fact]
    public void The_canonical_request_puts_a_blank_line_between_the_header_block_and_the_signed_header_list()
    {
        // The header block's own trailing newline plus the format's separator. Two newlines in a row is
        // correct, and a reader who "tidies" one away breaks every signature.
        var canonicalRequest = AwsSigV4.CanonicalRequest(
            "GET",
            "/",
            string.Empty,
            "host:example.amazonaws.com\n",
            "host",
            AwsSigV4.HashedEmptyPayload);

        canonicalRequest.Should().Be(
            "GET\n/\n\nhost:example.amazonaws.com\n\nhost\n" + AwsSigV4.HashedEmptyPayload);
    }

    // -----------------------------------------------------------------------------------------------------
    // Canonical URI
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/a/b", "/a/b")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/c/../b", "/a/b")]
    [InlineData("/a/", "/a/")]
    [InlineData("/example space/", "/example%20space/")]
    public void Canonical_uri_normalises_and_encodes_each_segment(string? path, string expected) =>
        AwsSigV4.CanonicalUri(path).Should().Be(expected);

    // -----------------------------------------------------------------------------------------------------
    // Key derivation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_signing_key_is_the_documented_four_stage_hmac_chain_and_is_deterministic()
    {
        var first = AwsSigV4.DeriveSigningKey(VectorSecretBytes, "20150830", "us-east-1", "service");
        var second = AwsSigV4.DeriveSigningKey(VectorSecretBytes, "20150830", "us-east-1", "service");

        first.Should().HaveCount(32, "the chain ends in one HMAC-SHA256");
        first.Should().Equal(second);
    }

    [Theory]
    [InlineData("20150831", "us-east-1", "service")]
    [InlineData("20150830", "eu-west-1", "service")]
    [InlineData("20150830", "us-east-1", "ec2")]
    public void The_signing_key_changes_with_every_scope_component(string dateStamp, string region, string service)
    {
        var baseline = AwsSigV4.DeriveSigningKey(VectorSecretBytes, "20150830", "us-east-1", "service");

        AwsSigV4.DeriveSigningKey(VectorSecretBytes, dateStamp, region, service)
            .Should().NotEqual(baseline, "a key scoped to one day, region and service must not sign for another");
    }

    [Fact]
    public void The_date_stamp_and_amz_date_are_rendered_in_utc_regardless_of_the_offset_they_arrive_with()
    {
        var withOffset = new DateTimeOffset(2015, 8, 30, 14, 36, 0, TimeSpan.FromHours(2));

        AwsSigV4.AmzDate(withOffset).Should().Be("20150830T123600Z");
        AwsSigV4.DateStamp(withOffset).Should().Be("20150830");
    }

    // -----------------------------------------------------------------------------------------------------
    // Golden EC2-shaped cases: fixed inputs, exact signatures, cross-checked against an independent
    // implementation of the specification rather than produced by the code under test
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fixed_ec2_post_produces_an_exact_stable_signature()
    {
        const string Body =
            "Action=RunInstances&ImageId=ami-0abcdef1234567890&InstanceType=t3.medium"
            + "&MaxCount=1&MinCount=1&Version=2016-11-15";

        using var content = new StringContent(Body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ec2.us-east-1.amazonaws.com/")
        {
            Content = content,
        };

        await VectorSigner("ec2").SignAsync(request);

        Authorization(request).Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/ec2/aws4_request, "
            + "SignedHeaders=content-type;host;x-amz-date, "
            + "Signature=ee375c86932bc5f33813bcd4ef40f2819efc3627a4710d86b7ab854e4bfdcc8c");
    }

    [Fact]
    public async Task A_fixed_ec2_get_with_a_tag_filter_produces_an_exact_stable_signature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://ec2.us-east-1.amazonaws.com/?Action=DescribeInstances"
            + "&Filter.1.Name=tag%3Aservyx.managed&Filter.1.Value.1=true&MaxResults=1000&Version=2016-11-15");

        await VectorSigner("ec2").SignAsync(request);

        Authorization(request).Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/ec2/aws4_request, "
            + "SignedHeaders=host;x-amz-date, "
            + "Signature=c92f71740a6390a68a5b37be6e8ff99c2056e343f8994e04c9d28d404f854d1f");

        // The URI was already canonical, so it was not rewritten - which also proves .NET's Uri did not
        // silently re-normalise the %3A the client wrote.
        request.RequestUri!.Query.Should().Contain("tag%3Aservyx.managed");
    }

    [Fact]
    public async Task The_payload_hash_is_part_of_the_signature_so_a_changed_body_changes_it()
    {
        var first = await SignedPostSignature("Action=DescribeInstances");
        var second = await SignedPostSignature("Action=TerminateInstances");

        first.Should().NotBe(second, "the canonical request hashes the payload, so two bodies cannot share a signature");
    }

    [Fact]
    public async Task A_session_token_is_sent_and_signed_when_temporary_credentials_are_configured()
    {
        var secrets = new RecordingSecretStore();
        var sessionTokenUrn = SecretUrn.Create("global", "aws", "api", "session-token");
        secrets.Put(SecretUrn.Create("global", "aws", "api", "access-key-id"), VectorAccessKeyId);
        secrets.Put(SecretUrn.Create("global", "aws", "api", "secret-access-key"), VectorSecretAccessKey);
        secrets.Put(sessionTokenUrn, "FwoGZXIvYXdzEXAMPLESESSIONTOKEN");

        var signer = new AwsRequestSigner(
            secrets,
            new AwsSigningIdentity(
                SecretUrn.Create("global", "aws", "api", "access-key-id"),
                SecretUrn.Create("global", "aws", "api", "secret-access-key"),
                sessionTokenUrn),
            "us-east-1",
            "ec2",
            new FixedTimeProvider(SigningInstant));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");
        await signer.SignAsync(request);

        Header(request, AwsSigV4.SecurityTokenHeader).Should().Be("FwoGZXIvYXdzEXAMPLESESSIONTOKEN");

        // Sent is not enough: an unsigned session token is rejected by AWS, so it must appear in the signed set.
        Authorization(request).Should().Contain("SignedHeaders=host;x-amz-date;x-amz-security-token");
    }

    [Fact]
    public async Task The_content_sha256_header_is_sent_and_signed_only_when_the_service_requires_it()
    {
        using var withoutHeader = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");
        await VectorSigner("ec2").SignAsync(withoutHeader);

        Header(withoutHeader, AwsSigV4.ContentSha256Header).Should().BeNull("EC2 does not require it");
        Authorization(withoutHeader).Should().Contain("SignedHeaders=host;x-amz-date");

        using var withHeader = new HttpRequestMessage(HttpMethod.Get, "https://s3.us-east-1.amazonaws.com/");
        await VectorSigner("s3", includeContentSha256Header: true).SignAsync(withHeader);

        Header(withHeader, AwsSigV4.ContentSha256Header).Should().Be(AwsSigV4.HashedEmptyPayload);
        Authorization(withHeader).Should().Contain("SignedHeaders=host;x-amz-content-sha256;x-amz-date");
    }

    [Fact]
    public async Task Signing_twice_replaces_the_headers_rather_than_appending_to_them()
    {
        // A retry that re-signs must not end up with two x-amz-date values, which would canonicalise to a
        // comma-joined header AWS never saw.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");

        var signer = VectorSigner("ec2");
        await signer.SignAsync(request);
        await signer.SignAsync(request);

        request.Headers.GetValues(AwsSigV4.AmzDateHeader).Should().ContainSingle();
        request.Headers.GetValues("Authorization").Should().ContainSingle();
    }

    [Fact]
    public async Task A_relative_uri_is_refused_with_an_explanation_rather_than_signed_against_nothing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VectorSigner("ec2").SignAsync(request));

        error.Message.Should().Contain("absolute request URI");
    }

    // -----------------------------------------------------------------------------------------------------
    // The credential itself
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_secret_access_key_never_appears_on_the_wire_in_any_form()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/?a=1");

        await VectorSigner("ec2").SignAsync(request);

        var rendered = string.Join(
            "\n",
            request.RequestUri!.AbsoluteUri,
            string.Join("\n", request.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));

        // The structural difference from both existing cloud adapters: DigitalOcean transmits its stored token
        // and Azure transmits a token bought with its stored secret. SigV4 transmits neither - what travels is
        // an HMAC. An intercepted Servyx AWS request does not disclose the credential that signed it.
        rendered.Should().NotContain(VectorSecretAccessKey);
        rendered.Should().NotContain("wJalrXUtnFEMI");

        // The access key id, by contrast, is *supposed* to travel: AWS needs it to look up which key to verify
        // against. It is an identifier, not the secret.
        rendered.Should().Contain(VectorAccessKeyId);
    }

    [Fact]
    public async Task A_missing_credential_is_reported_as_a_missing_secret_naming_the_urn_and_not_the_value()
    {
        var secrets = new RecordingSecretStore();
        secrets.Put(SecretUrn.Create("global", "aws", "api", "access-key-id"), VectorAccessKeyId);

        var signer = new AwsRequestSigner(
            secrets,
            new AwsSigningIdentity(
                SecretUrn.Create("global", "aws", "api", "access-key-id"),
                SecretUrn.Create("global", "aws", "api", "secret-access-key")),
            "us-east-1",
            "ec2",
            new FixedTimeProvider(SigningInstant));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => signer.SignAsync(request));

        error.Message.Should().Contain("secret://global/aws/api/secret-access-key");
        error.Message.Should().Contain("instance metadata");
        error.ToString().Should().NotContain(VectorSecretAccessKey);
    }

    [Fact]
    public void A_signing_identity_refuses_a_default_secret_urn_rather_than_signing_with_nothing()
    {
        var real = SecretUrn.Create("global", "aws", "api", "access-key-id");

        Assert.Throws<ArgumentException>(() => new AwsSigningIdentity(default, real));
        Assert.Throws<ArgumentException>(() => new AwsSigningIdentity(real, default));
    }

    [Fact]
    public async Task The_key_pair_is_resolved_afresh_for_every_request_and_never_cached()
    {
        var scenario = new AwsScenario();
        scenario.Api.Responder = _ => AwsApiDouble.Xml(
            System.Net.HttpStatusCode.OK,
            AwsScenario.DescribeInstancesXml());

        var provisioner = scenario.Provisioner();
        await provisioner.RefreshAsync(AwsScenario.RecordedHandle());
        await provisioner.RefreshAsync(AwsScenario.RecordedHandle());

        // Two resolutions per request, one per half of the key pair, and nothing kept between calls - so
        // revoking the stored key takes effect on the very next request. This is stricter than the Azure
        // adapter, which caches a derived access token for its stated lifetime.
        scenario.Api.Requests.Should().HaveCount(2);
        scenario.Secrets.Resolved.Should().HaveCount(4);
        scenario.Secrets.Resolved.Should().OnlyContain(u =>
            u == AwsScenario.AccessKeyIdUrn.Value || u == AwsScenario.SecretAccessKeyUrn.Value);
    }

    private static AwsRequestSigner VectorSigner(string service, bool includeContentSha256Header = false)
    {
        var secrets = new RecordingSecretStore();
        var accessKeyIdUrn = SecretUrn.Create("global", "aws", "api", "access-key-id");
        var secretAccessKeyUrn = SecretUrn.Create("global", "aws", "api", "secret-access-key");

        secrets.Put(accessKeyIdUrn, VectorAccessKeyId);
        secrets.Put(secretAccessKeyUrn, VectorSecretAccessKey);

        return new AwsRequestSigner(
            secrets,
            new AwsSigningIdentity(accessKeyIdUrn, secretAccessKeyUrn),
            "us-east-1",
            service,
            new FixedTimeProvider(SigningInstant),
            includeContentSha256Header);
    }

    private static async Task<string> SignedPostSignature(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ec2.us-east-1.amazonaws.com/")
        {
            Content = content,
        };

        await VectorSigner("ec2").SignAsync(request);
        return Authorization(request) ?? string.Empty;
    }

    private static string? Authorization(HttpRequestMessage request) => Header(request, "Authorization");

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
}
