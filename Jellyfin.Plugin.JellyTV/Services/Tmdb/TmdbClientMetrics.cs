using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal sealed class TmdbClientMetrics
{
    private int _requests;
    private int _retries;

    public int Requests => _requests;

    public int Retries => _retries;

    public void IncrementRequests() => Interlocked.Increment(ref _requests);

    public void IncrementRetries() => Interlocked.Increment(ref _retries);
}
