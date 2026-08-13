using Microsoft.Extensions.Logging;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Secrets;
using Servyx.Domain.Users;

namespace Servyx.Application.Users;

/// <summary>
/// <see cref="IUserService"/> implementation.
/// </summary>
public sealed class UserService : IUserService
{
    private readonly IUserRepository _repository;
    private readonly ILogger<UserService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a <see cref="UserService"/>.</summary>
    /// <param name="timeProvider">Clock used for the row's <see cref="User.CreatedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public UserService(IUserRepository repository, ILogger<UserService> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CreateUserResult> CreateAsync(
        string username, string password, UserRole role, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > CreateUserResult.MaximumUsernameLength)
        {
            return CreateUserResult.InvalidUsername(
                $"A username is required and must be at most {CreateUserResult.MaximumUsernameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < CreateUserResult.MinimumPasswordLength)
        {
            return CreateUserResult.WeakPassword();
        }

        var trimmedUsername = username.Trim();

        // Pre-check the unique index rather than surfacing a raw constraint violation as an exception, the
        // same convention HostRegistrationService.RegisterAsync follows for host names. Not a substitute for
        // the index — a concurrent create under the same username still loses at the database.
        var existing = await _repository.TryGetByUsernameAsync(trimmedUsername, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return CreateUserResult.UsernameTaken(trimmedUsername);
        }

        var user = new User
        {
            Id = UserId.New(),
            Username = trimmedUsername,
            PasswordHash = PasswordHash.Create(password),
            Role = role,
            IsActive = true,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        await _repository.AddAsync(user, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created user account '{Username}' with role {Role}, by '{Actor}'.", trimmedUsername, role, actor);

        return CreateUserResult.Created(user.Id);
    }

    /// <inheritdoc />
    public Task<User?> TryGetAsync(UserId id, CancellationToken ct = default) =>
        _repository.TryGetAsync(id, ct);

    /// <inheritdoc />
    public Task<User?> TryGetByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        return _repository.TryGetByUsernameAsync(username.Trim(), ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) =>
        _repository.ListAsync(ct);

    /// <inheritdoc />
    public async Task<bool> ChangeRoleAsync(UserId id, UserRole role, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var updated = await _repository.SetRoleAsync(id, role, ct).ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        _logger.LogInformation("Changed user '{Username}' to role {Role}, by '{Actor}'.", updated.Username, role, actor);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(UserId id, bool isActive, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var updated = await _repository.SetActiveAsync(id, isActive, ct).ConfigureAwait(false);
        if (updated is null)
        {
            return false;
        }

        _logger.LogInformation(
            "{Action} user '{Username}', by '{Actor}'.",
            isActive ? "Reactivated" : "Deactivated",
            updated.Username,
            actor);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyPasswordAsync(string username, string? password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var user = await _repository.TryGetByUsernameAsync(username.Trim(), ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            // A deactivated account authenticates nobody — the same fail-closed posture
            // OperatorCredentialStore takes for an unbootstrapped install.
            return false;
        }

        return PasswordHash.Verify(user.PasswordHash, password);
    }
}
