using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Servyx.Domain.Common;

namespace Servyx.Infrastructure.Persistence.Converters;

/// <summary>
/// Persists a <see cref="ServerId"/> as its underlying <see cref="Guid"/>.
/// </summary>
/// <remarks>
/// Declared as a converter <em>type</em> (rather than an inline lambda pair) so it can be registered once
/// through <c>ModelConfigurationBuilder.Properties&lt;ServerId&gt;().HaveConversion&lt;ServerIdConverter&gt;()</c>
/// in <see cref="ServyxDbContext.ConfigureConventions"/>, which is what keeps every current and future
/// mapping of a <see cref="ServerId"/> consistent without each entity configuration having to remember.
/// </remarks>
public sealed class ServerIdConverter : ValueConverter<ServerId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public ServerIdConverter()
        : base(id => id.Value, value => new ServerId(value))
    {
    }
}

/// <summary>
/// Persists a <see cref="HostId"/> as its underlying <see cref="Guid"/>.
/// </summary>
/// <remarks>See <see cref="ServerIdConverter"/> for why this is a named type rather than an inline lambda pair.</remarks>
public sealed class HostIdConverter : ValueConverter<HostId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public HostIdConverter()
        : base(id => id.Value, value => new HostId(value))
    {
    }
}

/// <summary>
/// Persists a <see cref="ChangePlanId"/> as its underlying <see cref="Guid"/>.
/// </summary>
/// <remarks>See <see cref="ServerIdConverter"/> for why this is a named type rather than an inline lambda pair.</remarks>
public sealed class ChangePlanIdConverter : ValueConverter<ChangePlanId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public ChangePlanIdConverter()
        : base(id => id.Value, value => new ChangePlanId(value))
    {
    }
}
