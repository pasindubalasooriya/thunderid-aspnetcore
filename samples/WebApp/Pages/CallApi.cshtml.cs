using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ThunderID.AspNetCore;

namespace WebApp.Pages;

[Authorize]
public class CallApiModel(IHttpClientFactory httpClientFactory, IConfiguration configuration) : PageModel
{
    public int? BillingStatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? WwwAuthenticate { get; private set; }
    public string? AccessTokenPayload { get; private set; }
    public int? MeStatusCode { get; private set; }
    public string? MeResponseBody { get; private set; }
    public string BillingUrl => $"{configuration["BillingApi:BaseUrl"]}/billing";

    public async Task OnGetAsync()
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (accessToken is null)
        {
            return;
        }

        // Shown for learning only: decoding doesn't verify the token, so never authorize on it.
        try
        {
            AccessTokenPayload = JsonSerializer.Serialize(ThunderIDJwt.DecodeUnverified(accessToken), new JsonSerializerOptions { WriteIndented = true });
        }
        catch (ThunderIDException)
        {
            // An opaque access token has no payload to show.
        }

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BillingUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);

        BillingStatusCode = (int)response.StatusCode;
        ResponseBody = await response.Content.ReadAsStringAsync();
        WwwAuthenticate = response.Headers.WwwAuthenticate.ToString();

        // /me accepts any valid token and echoes the claims exactly as the API sees them.
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, $"{configuration["BillingApi:BaseUrl"]}/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var meResponse = await client.SendAsync(meRequest);
        MeStatusCode = (int)meResponse.StatusCode;
        var meBody = await meResponse.Content.ReadAsStringAsync();
        MeResponseBody = meResponse.IsSuccessStatusCode
            ? JsonSerializer.Serialize(JsonDocument.Parse(meBody), new JsonSerializerOptions { WriteIndented = true })
            : meBody;
    }
}
