namespace Servyx.Mcp;

/// <summary>Identity constants the stdio host's <c>AddMcpServer</c> call reports as this server's <c>ServerInfo</c>.</summary>
public static class ServyxMcpServer
{
    /// <summary>The name reported to an MCP client during <c>initialize</c>.</summary>
    public const string Name = "servyx";

    /// <summary>
    /// The version reported to an MCP client during <c>initialize</c>. Bumped by hand alongside this
    /// assembly's tool surface — there is no build-time stamping yet, since the tool surface itself does
    /// not exist before Phase 4.
    /// </summary>
    public const string Version = "0.1.0";
}
