using FoodDelivery.Api.Auth;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    IPasswordHasher<User> passwordHasher,
    TokenService tokenService,
    IOptions<AccountLockoutOptions> lockoutOptions,
    TimeProvider timeProvider,
    ILogger<AuthController> logger) : ControllerBase
{
    private readonly AccountLockoutOptions _lockout = lockoutOptions.Value;

    // Roles a user may pick for themselves. Admin is never self-assignable.
    private static readonly UserRole[] SelfRegisterRoles = [UserRole.Customer, UserRole.RestaurantOwner];

    // Used to spend the same hashing time when the email doesn't exist (anti timing attack).
    private static readonly User DummyUser = new() { FullName = "", Email = "", PasswordHash = "" };
    private static readonly string DummyHash =
        new PasswordHasher<User>().HashPassword(DummyUser, "dummy-password-for-timing");

    // POST /api/auth/register
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Register)]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        if (!SelfRegisterRoles.Contains(request.Role))
        {
            ModelState.AddModelError(nameof(request.Role), "Role must be Customer or RestaurantOwner.");
            return ValidationProblem(ModelState);
        }

        var email = NormalizeEmail(request.Email);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return EmailTaken();

        var user = new User
        {
            FullName = request.FullName.Trim(),
            Email = email,
            PhoneNumber = request.PhoneNumber,
            Role = request.Role,
            PasswordHash = "" // set below; the hasher takes the user as input
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two simultaneous registrations with the same email: the unique index wins.
            return EmailTaken();
        }

        return StatusCode(StatusCodes.Status201Created, ToAuthResponse(user));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            // Hash anyway so "unknown email" takes as long as "wrong password".
            passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, request.Password);
            return InvalidCredentials();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (user.LockoutEnd > now)
        {
            // Locked: don't even look at whether the password is right, so guessing during a
            // lockout reveals nothing. Hash anyway (result ignored) to keep the timing the same,
            // and answer exactly like a wrong password so the lockout doesn't reveal that the
            // account exists. Attempts during a lockout don't extend it.
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            return InvalidCredentials();
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            await RegisterFailedLoginAsync(user.Id, now, ct);
            return InvalidCredentials();
        }

        // Reset the failure counter, but only if no concurrent failed attempt has locked the
        // account since we read it (the condition is checked inside the UPDATE itself).
        var reset = await db.Users
            .Where(u => u.Id == user.Id && (u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LockoutEnd, (DateTime?)null), ct);
        if (reset == 0)
            return InvalidCredentials();

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // Hashing algorithm got stronger since this hash was made: upgrade it silently.
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            await db.SaveChangesAsync(ct);
        }

        return Ok(ToAuthResponse(user));
    }

    // GET /api/auth/me
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserResponse>> Me(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new UserResponse(u.Id, u.FullName, u.Email, u.Role.ToString()))
            .FirstOrDefaultAsync(ct);

        // Token is valid but the account was deleted after it was issued.
        return user is null ? Unauthorized() : Ok(user);
    }

    // Counts a failed login atomically in the database (no read-modify-write in C#, so
    // concurrent failures can't overwrite each other). The attempt that reaches the limit
    // locks the account and resets the counter, so the user gets a fresh set of attempts
    // once the lockout ends. Failures while already locked are not counted.
    private async Task RegisterFailedLoginAsync(int userId, DateTime now, CancellationToken ct)
    {
        var max = _lockout.MaxFailedAttempts;
        DateTime? lockUntil = now.AddMinutes(_lockout.DurationMinutes);

        // In SQL every SET expression sees the row's old values, so both CASEs agree.
        await db.Users
            .Where(u => u.Id == userId && (u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LockoutEnd, u => u.FailedLoginCount + 1 >= max ? lockUntil : u.LockoutEnd)
                .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= max ? 0 : u.FailedLoginCount + 1), ct);

        var lockedUntil = await db.Users
            .Where(u => u.Id == userId && u.LockoutEnd > now)
            .Select(u => u.LockoutEnd)
            .FirstOrDefaultAsync(ct);

        if (lockedUntil is not null)
        {
            // Log the user id, not the email: logs travel widely and shouldn't carry personal data.
            logger.LogWarning("Account {UserId} locked until {LockoutEnd} after {MaxFailedAttempts} failed logins",
                userId, lockedUntil, max);
        }
    }

    private AuthResponse ToAuthResponse(User user)
    {
        var token = tokenService.CreateToken(user);
        return new AuthResponse(
            token.Token,
            token.ExpiresAt,
            new UserResponse(user.Id, user.FullName, user.Email, user.Role.ToString()));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    // Same message whether the email or the password is wrong (no user enumeration).
    private ObjectResult InvalidCredentials() => Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid email or password.");

    private ObjectResult EmailTaken() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Email is already registered.");
}
