using System.Security.Claims;
using AMPay.Web.Controllers;
using AMPay.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace AMPay.Tests;

public class PasswordTests
{
    // ----------------------------------------------------------------- temporary passwords

    [Fact]
    public void TemporaryPasswords_AlwaysMeetThePolicy()
    {
        // Identity is configured for 12+ characters with an uppercase letter, a digit and a
        // symbol. A generated password that missed one would make the reset fail at random.
        for (var i = 0; i < 2_000; i++)
        {
            var p = UsersController.TemporaryPassword();

            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public void TemporaryPasswords_AvoidLookAlikeCharacters()
    {
        // Read out over the phone: no 0/O, 1/l/I.
        for (var i = 0; i < 500; i++)
            Assert.DoesNotContain(UsersController.TemporaryPassword(), c => "0O1lI".Contains(c));
    }

    [Fact]
    public void TemporaryPasswords_AreNotRepeated()
    {
        var seen = Enumerable.Range(0, 1_000).Select(_ => UsersController.TemporaryPassword()).ToHashSet();
        Assert.Equal(1_000, seen.Count);
    }

    // ----------------------------------------------------------------- the must-change gate

    [Theory]
    [InlineData("Clients", true)]
    [InlineData("Loans", true)]
    [InlineData("Users", true)]
    [InlineData("Account", false)]
    [InlineData("Sign", false)]
    public async Task AUserOnAnAdministratorsPassword_CanOnlyChangeIt(string controller, bool redirected)
    {
        var (context, ran) = await RunFilterAsync(controller, mustChange: true);

        if (redirected)
        {
            var r = Assert.IsType<RedirectToActionResult>(context.Result);
            Assert.Equal("ChangePassword", r.ActionName);
            Assert.Equal("Account", r.ControllerName);
            Assert.False(ran);
        }
        else
        {
            Assert.Null(context.Result);
            Assert.True(ran);
        }
    }

    [Fact]
    public async Task AUserWithTheirOwnPassword_IsNotStopped()
    {
        var (context, ran) = await RunFilterAsync("Clients", mustChange: false);

        Assert.Null(context.Result);
        Assert.True(ran);
    }

    private static async Task<(ActionExecutingContext Context, bool Ran)> RunFilterAsync(string controller, bool mustChange)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "someone@example.test") };
        if (mustChange) claims.Add(new Claim(AppClaims.MustChangePassword, "true"));

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var route = new RouteData();
        route.Values["controller"] = controller;

        var action = new ActionContext(http, route, new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);

        var ran = false;
        await new MustChangePasswordFilter().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), controller: null!));
        });

        return (context, ran);
    }
}
