using KAST.Core.Models;
using KAST.Infrastructure.Services;
using KAST.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace KAST.Tests;

public class UserAccountServiceTests
{
    [Theory]
    [InlineData("short")]                 // far below minimum
    [InlineData("elevencharss")]          // 12 chars but single character class
    [InlineData("password12345")]         // 13 chars, only two classes
    public async Task CreateAdmin_WeakPassword_IsRejected(string password)
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateInitialAdminAsync("admin", password));
    }

    [Theory]
    [InlineData("Compliant-Pw12")]              // 14 chars, 4 classes
    [InlineData("averylongpassphraseisfine")]   // 16+ chars, one class
    public async Task CreateAdmin_StrongPassword_IsAccepted(string password)
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        var user = await sut.CreateInitialAdminAsync("admin", password);

        Assert.NotNull(await sut.ValidateCredentialsAsync("admin", password));
        Assert.NotEmpty(user.PasswordHash);
    }

    [Fact]
    public async Task CreateInitialAdmin_PersistsUserAndHash()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        var user = await sut.CreateInitialAdminAsync("admin", "Sup3r-Secret-Pw!");

        Assert.True(user.Id > 0);
        Assert.Equal("admin", user.Username);
        Assert.Equal("ADMIN", user.NormalizedUsername);
        Assert.DoesNotContain("Sup3r-Secret-Pw!", user.PasswordHash);
        Assert.True(await sut.HasAnyUsersAsync());
    }

    [Fact]
    public async Task CreateAdmin_DuplicateUsername_Throws()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        await sut.CreateInitialAdminAsync("admin", "Sup3r-Secret-Pw!");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateAdminAsync("ADMIN", "Other-Secret-Pw1!"));
    }

    [Fact]
    public async Task ValidateCredentials_UpdatesLastLogin()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());
        await sut.CreateInitialAdminAsync("admin", "Sup3r-Secret-Pw!");

        var invalid = await sut.ValidateCredentialsAsync("admin", "wrong");
        var valid = await sut.ValidateCredentialsAsync("admin", "Sup3r-Secret-Pw!");

        Assert.Null(invalid);
        Assert.NotNull(valid);
        Assert.NotNull(valid!.LastLoginAt);
    }

    [Fact]
    public async Task ProvisionOidcAdmin_CreatesFirstAdminFromAllowedGroup()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        var user = await sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test/application/o/kast/",
            "user-123",
            "Authentik Admin",
            "admin@example.test",
            ["KAST Admins"]));

        Assert.True(user.Id > 0);
        Assert.Equal("Authentik Admin", user.Username);
        Assert.Equal(KastUser.OidcAuthSource, user.AuthSource);
        Assert.Equal("OpenID Connect", user.ExternalProvider);
        Assert.Equal("https://auth.example.test/application/o/kast/", user.ExternalIssuer);
        Assert.Equal("user-123", user.ExternalSubject);
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task ProvisionOidcAdmin_CreatesLaterAdminWhenAllowedGroupIsPresent()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());
        await sut.CreateInitialAdminAsync("local-admin", "Sup3r-Secret-Pw!");

        var user = await sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            "user-456",
            "External Admin",
            null,
            ["KAST Admins"]));

        Assert.Equal(KastUser.OidcAuthSource, user.AuthSource);
        Assert.Equal(2, db.Users.Count());
    }

    [Fact]
    public async Task ProvisionOidcAdmin_RejectsMissingIdentityOrAllowedGroup()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            null,
            "user-123",
            "Admin",
            null,
            ["KAST Admins"])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            null,
            "Admin",
            null,
            ["KAST Admins"])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            "user-123",
            "Admin",
            null,
            ["Other Group"])));
    }

    [Fact]
    public async Task ProvisionOidcAdmin_ReusesExistingExternalUserAndSyncsUsername()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        var first = await sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            "user-123",
            "Old Name",
            null,
            ["KAST Admins"]));
        var second = await sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            "user-123",
            "New Name",
            null,
            ["KAST Admins"]));

        Assert.Equal(first.Id, second.Id);
        // A later login must not rename the account from the IdP display name —
        // that would discard any rename made locally in KAST.
        Assert.Equal("Old Name", second.Username);
        Assert.Single(db.Users);
    }

    [Fact]
    public async Task ValidateCredentials_DoesNotAuthenticateOidcUsers()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());

        await sut.ProvisionOidcAdminAsync(new OidcProvisioningRequest(
            "https://auth.example.test",
            "user-123",
            "External Admin",
            null,
            ["KAST Admins"]));

        Assert.Null(await sut.ValidateCredentialsAsync("External Admin", "anything"));
    }

    [Fact]
    public async Task AllowSystemAccount_CreatesExternallyManagedUser()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var account = new SystemAccount("DOMAIN\\admin", "Domain Admin", "DOMAIN", "S-1-1-0", "Windows AD");
        var sut = new UserAccountService(db, BuildConfig(), new FakeSystemAccountProvider());

        var user = await sut.AllowSystemAccountAsync(account);

        Assert.Equal(KastUser.SystemAuthSource, user.AuthSource);
        Assert.Equal("Windows AD", user.ExternalProvider);
        Assert.Equal("DOMAIN", user.ExternalIssuer);
        Assert.Equal("S-1-1-0", user.ExternalSubject);
        Assert.True(user.IsExternallyManaged);
    }

    [Fact]
    public async Task RemoveSystemAccount_DeactivatesAllowlistEntry()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var account = new SystemAccount("linux-admin", "Linux Admin", "host", "1001", "Linux Local");
        var sut = new UserAccountService(db, BuildConfig(), new FakeSystemAccountProvider());
        var user = await sut.AllowSystemAccountAsync(account);

        await sut.RemoveSystemAccountAsync(user.Id);

        Assert.Empty(await sut.GetAllUsersAsync());
        Assert.Null(await sut.GetByIdAsync(user.Id));
    }

    [Fact]
    public async Task ValidateSystemCredentials_RequiresEnabledSettingAndAllowedAccount()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var account = new SystemAccount("linux-admin", "Linux Admin", "host", "1001", "Linux Local");
        var provider = new FakeSystemAccountProvider();
        provider.Add("linux-admin", "system-secret", account);
        var sut = new UserAccountService(db, BuildConfig(), provider);

        // Seeding is SettingsService's job; provide the row this test toggles.
        db.Settings.Add(new KAST.Core.Models.KastSettings());
        await db.SaveChangesAsync();

        await sut.AllowSystemAccountAsync(account);
        Assert.Null(await sut.ValidateSystemCredentialsAsync("linux-admin", "system-secret"));

        db.Settings.Single().SystemAuthEnabled = true;
        await db.SaveChangesAsync();

        var valid = await sut.ValidateSystemCredentialsAsync("linux-admin", "system-secret");

        Assert.NotNull(valid);
        Assert.NotNull(valid!.LastLoginAt);
    }

    [Fact]
    public async Task UpdateProfile_ChangesUsernameAndPassword()
    {
        using var db = DbHelper.CreateInMemoryDb();
        var sut = new UserAccountService(db, BuildConfig());
        var user = await sut.CreateInitialAdminAsync("admin", "Sup3r-Secret-Pw!");

        var updated = await sut.UpdateProfileAsync(user.Id, "root", "New-Secret-Pw123!");

        Assert.Equal("root", updated.Username);
        Assert.Null(await sut.ValidateCredentialsAsync("root", "Sup3r-Secret-Pw!"));
        Assert.NotNull(await sut.ValidateCredentialsAsync("root", "New-Secret-Pw123!"));
    }

    [Fact]
    public async Task AvatarUpload_StoresAllowedImageAndRejectsInvalidExtension()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), $"kast-user-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var db = DbHelper.CreateInMemoryDb();
            var sut = new UserAccountService(db, BuildConfig(Path.Join(tempRoot, "kast.db")));

            await using var image = new MemoryStream([1, 2, 3]);
            var user = await sut.CreateInitialAdminAsync("admin", "Sup3r-Secret-Pw!", image, "avatar.png", image.Length);

            Assert.False(string.IsNullOrWhiteSpace(user.AvatarFileName));
            var avatarPath = await sut.GetAvatarPathAsync(user.AvatarFileName!);
            Assert.True(File.Exists(avatarPath));

            await using var invalid = new MemoryStream([1, 2, 3]);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.CreateAdminAsync("other", "Sup3r-Secret-Pw!", invalid, "avatar.gif", invalid.Length));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IConfiguration BuildConfig(string? dbPath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={dbPath ?? "kast.db"}",
            ["Auth:Oidc:AllowedGroups:0"] = "KAST Admins"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
