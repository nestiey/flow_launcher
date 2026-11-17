using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flow.Launcher;

internal sealed class MinecraftSession(string name, string id, string accessToken, Uri? skinUrl)
{
    public string Name { get; } = name;
    public string Id { get; } = id;
    public string AccessToken { get; } = accessToken;
    public Uri? SkinUrl { get; } = skinUrl;
}

internal static class MinecraftLogin
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.signin offline_access";
    private static readonly JsonSerializerOptions AuthJson = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<MinecraftSession> SignInAsync(string clientId, CancellationToken cancellation)
    {

        if (!Guid.TryParse(clientId, out _))
            throw new IOException("FLOW용 Microsoft 앱의 클라이언트 ID를 먼저 설정하세요.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        using var listener = new HttpListener();
        var redirect = StartLoopback(listener);
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account"
        };
        using var query = new FormUrlEncodedContent(fields);
        using var browser = Process.Start(new ProcessStartInfo(
            $"{Authority}/authorize?{await query.ReadAsStringAsync(ct)}") { UseShellExecute = true });

        string code;
        while (true)
        {
            var callback = await listener.GetContextAsync().WaitAsync(ct);
            if (callback.Request.HttpMethod != "GET" ||
                !string.Equals(callback.Request.QueryString["state"], state, StringComparison.Ordinal))
            {
                callback.Response.StatusCode = 400;
                callback.Response.Close();
                continue;
            }
            code = callback.Request.QueryString["code"] ?? "";
            var message = code.Length == 0 ? "Login cancelled. Return to FLOW." : "Return to FLOW to finish signing in.";
            callback.Response.ContentType = "text/plain; charset=utf-8";
            callback.Response.Headers["Cache-Control"] = "no-store";
            var body = Encoding.UTF8.GetBytes(message);
            callback.Response.ContentLength64 = body.Length;
            await callback.Response.OutputStream.WriteAsync(body, ct);
            callback.Response.Close();
            break;
        }
        listener.Stop();
        if (code.Length == 0)
            throw new IOException("Microsoft 로그인이 취소되었거나 승인되지 않았습니다.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
            ["scope"] = Scope
        });
        using var tokenResponse = await Http.PostAsync($"{Authority}/token", form, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var body = await tokenResponse.Content.ReadAsStringAsync(ct);
            var diagnostic = System.Text.RegularExpressions.Regex.Match(body, @"\bAADSTS[0-9]{4,10}\b");
            var detail = diagnostic.Value switch
            {
                "AADSTS7000218" => "Entra에서 모바일 및 데스크톱 리디렉션 설정을 확인하세요.",
                "" => "Microsoft가 상세 오류 코드를 제공하지 않았습니다.",
                _ => diagnostic.Value
            };
            throw new IOException($"Microsoft 토큰 실패 (HTTP {(int)tokenResponse.StatusCode})\n{detail}");
        }
        var microsoft = await ReadAsync(tokenResponse, "Microsoft 토큰", ct);
        var session = await CompleteAsync(microsoft, ct);
        RememberRefreshToken(microsoft);
        return session;
    }

    public static async Task<MinecraftSession?> RestoreAsync(string clientId, CancellationToken ct)
    {
        var refresh = LoginCache.Read();
        if (string.IsNullOrWhiteSpace(refresh))
            return null;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["scope"] = Scope
        });
        using var response = await Http.PostAsync($"{Authority}/token", form, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var rejection = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (rejection.TryGetProperty("error", out var error) && error.GetString() == "invalid_grant")
            {
                LoginCache.Clear();
                return null;
            }
        }
        var microsoft = await ReadAsync(response, "Microsoft 자동 로그인", ct);
        RememberRefreshToken(microsoft);
        return await CompleteAsync(microsoft, ct);
    }

    private static void RememberRefreshToken(JsonElement token)
    {
        if (token.TryGetProperty("refresh_token", out var refresh) &&
            refresh.GetString() is { Length: > 0 } value)
            LoginCache.Write(value);
    }

    private static async Task<MinecraftSession> CompleteAsync(JsonElement microsoft, CancellationToken ct)
    {
        var xbox = await PostAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + Required(microsoft, "access_token") },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, "Xbox 로그인", ct);
        var xsts = await PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Required(xbox, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, "Xbox 계정 권한 확인", ct);
        var userHash = Required(xsts.GetProperty("DisplayClaims").GetProperty("xui")[0], "uhs");
        var minecraft = await PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={userHash};{Required(xsts, "Token")}" },
            "Minecraft 로그인", ct);

        var token = Required(minecraft, "access_token");
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var profileResponse = await Http.SendAsync(profileRequest, ct);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("이 계정에 Minecraft Java Edition 프로필이 없습니다.");
        var profile = await ReadAsync(profileResponse, "Minecraft 프로필 확인", ct);
        Uri? skinUrl = null;
        if (profile.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
        {
            foreach (var skin in skins.EnumerateArray())
            {
                if (skin.TryGetProperty("state", out var skinState) && skinState.GetString() == "ACTIVE" &&
                    skin.TryGetProperty("url", out var url) &&
                    Uri.TryCreate(url.GetString(), UriKind.Absolute, out var texture) &&
                    (texture.Scheme == Uri.UriSchemeHttps || texture.Scheme == Uri.UriSchemeHttp) &&
                    texture.Host == "textures.minecraft.net" && texture.IsDefaultPort)
                {
                    skinUrl = new UriBuilder(texture) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                    break;
                }
            }
        }
        return new MinecraftSession(Required(profile, "name"), Required(profile, "id"), token, skinUrl);
    }

    private static string StartLoopback(HttpListener listener)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(49152, 65536);
            var prefix = $"http://localhost:{port}/";
            listener.Prefixes.Clear();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return prefix;
            }
            catch (HttpListenerException) when (attempt < 9) { }
        }
        throw new IOException("Microsoft 로그인 응답을 받을 로컬 주소를 열 수 없습니다.");
    }

    private static async Task<JsonElement> PostAsync(string url, object body, string step, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: AuthJson)
        };
        request.Headers.Accept.ParseAdd("application/json");
        if (request.RequestUri!.Host is "user.auth.xboxlive.com" or "xsts.auth.xboxlive.com")
            request.Headers.Add("x-xbl-contract-version", "1");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden &&
            request.RequestUri!.Host == "api.minecraftservices.com")
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if (error.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Minecraft 인증 거절 (403)\nFLOW 앱 등록이 Minecraft 서비스에서 허용되지 않았습니다.");
        }
        return await ReadAsync(response, step, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string step, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode && response.RequestMessage?.RequestUri?.Host is
            ("user.auth.xboxlive.com" or "xsts.auth.xboxlive.com"))
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("XErr", out var xerr)
                    && xerr.ValueKind == JsonValueKind.Number && xerr.TryGetInt64(out var code) && code != 0)
                    throw new IOException($"{step} 실패 (HTTP {(int)response.StatusCode}, Xbox {code})");
            }
            catch (JsonException) { }
        }
        if (!response.IsSuccessStatusCode)
            throw new IOException($"{step} 실패 (HTTP {(int)response.StatusCode})");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static string Required(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new InvalidDataException("인증 서버 응답에 필요한 값이 없습니다.");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}








