using System.Net;
using System.Net.Http;
using System.Text;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// The login dialog must never echo credentials, must refuse bad input
/// BEFORE spending one of the sidecar's 3 attempts/minute, must surface
/// the rate-limit refusal verbatim, and must fail cleanly when the bridge
/// is down. All traffic goes through an injected handler — no sockets.
/// </summary>
[Trait("Category", "Unit")]
public class LoginViewModelTests
{
    private sealed class FakeBridge : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public bool ThrowOnNext { get; set; }
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(ct);
            }
            if (ThrowOnNext)
            {
                throw new HttpRequestException("connection refused");
            }
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (LoginViewModel Vm, FakeBridge Fake) NewVm(string body)
    {
        var fake = new FakeBridge { Body = body };
        var client = new Mt5BridgeClient(fake, new Uri("http://127.0.0.1:53190/"));
        return (new LoginViewModel(client), fake);
    }

    private const string OkBody =
        "{\"ok\": true, \"login\": 201587365, \"server\": \"Deriv-Demo\"," +
        " \"trade_mode\": 0, \"balance\": 1234.5, \"currency\": \"USD\"}";

    private static void Fill(LoginViewModel vm)
    {
        vm.Account = "201587365";
        vm.Password = "hunter2";
        vm.Server = " Deriv-Demo ";
    }

    [Fact]
    public async Task Success_SignsIn_Refreshes_ClearsPassword_And_Never_Echoes_It()
    {
        var (vm, fake) = NewVm(OkBody);
        Fill(vm);
        var signedIn = 0;
        var refreshed = 0;
        vm.SignedIn += () => signedIn++;
        vm.OnSignedIn = () => { refreshed++; return Task.CompletedTask; };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(1, signedIn);
        Assert.Equal(1, refreshed);
        Assert.Equal("", vm.Password);                    // credentials leave the field
        Assert.Contains("201587365", vm.StatusMessage);   // fresh account verdict shown
        Assert.Contains("Deriv-Demo", vm.StatusMessage);
        Assert.DoesNotContain("hunter2", vm.StatusMessage);
        Assert.False(vm.IsLoggingIn);
        Assert.Contains("\"password\":\"hunter2\"", fake.LastBody); // sent once, over loopback
    }

    [Fact]
    public async Task Failure_Surfaces_The_Bridge_Error_And_Does_Not_Sign_In()
    {
        var (vm, _) = NewVm("{\"ok\": false, \"error\": \"login failed: invalid password\"}");
        Fill(vm);
        var signedIn = 0;
        vm.SignedIn += () => signedIn++;

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(0, signedIn);
        Assert.Contains("login failed", vm.StatusMessage);
        Assert.Equal("hunter2", vm.Password);   // keep it for the retry
    }


    [Fact]
    public async Task RateLimit_422_Body_Surfaces_The_Wait_Message()
    {
        var (vm, fake) = NewVm("{\"error\": \"too many login attempts - wait a minute\"}");
        fake.Status = (HttpStatusCode)422;
        Fill(vm);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Contains("too many login attempts", vm.StatusMessage);
        Assert.False(vm.IsLoggingIn);
    }

    [Fact]
    public async Task RateLimit_429_Without_Body_Gets_The_Client_Message()
    {
        var (vm, fake) = NewVm("{}");
        fake.Status = (HttpStatusCode)429;
        Fill(vm);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Contains("too many login attempts", vm.StatusMessage);
    }

    [Fact]
    public async Task Validation_Refuses_Bad_Fields_Before_Spending_An_Attempt()
    {
        var (vm, fake) = NewVm(OkBody);

        vm.Account = "abc"; vm.Password = "x"; vm.Server = "s";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal("login must be the numeric account id", vm.StatusMessage);

        vm.Account = "201587365"; vm.Password = ""; vm.Server = "s";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal("password and server are required", vm.StatusMessage);

        vm.Password = "x"; vm.Server = "   ";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal("password and server are required", vm.StatusMessage);

        Assert.Equal(0, fake.Calls);   // none of them reached the bridge
    }

    [Fact]
    public async Task Bridge_Down_Fails_Cleanly_Without_Signing_In()
    {
        var (vm, fake) = NewVm(OkBody);
        fake.ThrowOnNext = true;
        Fill(vm);
        var signedIn = 0;
        vm.SignedIn += () => signedIn++;

        await vm.LoginCommand.ExecuteAsync(null);   // must not throw

        Assert.Equal(0, signedIn);
        Assert.Contains("bridge unreachable", vm.StatusMessage);
        Assert.False(vm.IsLoggingIn);
    }
}
