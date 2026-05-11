using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;

namespace SampleApp.Tests;

public static class Extensions
{
    public static async Task EnsureSuccessAsProblem(this HttpResponseMessage response, Action<string> log)
    {
        if (!response.IsSuccessStatusCode) // TODO utility method for this
        {
            var error = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            log($"Response: {response.StatusCode}, Detail: {error!.Detail}");
        }

        response.EnsureSuccessStatusCode();

    }
}
