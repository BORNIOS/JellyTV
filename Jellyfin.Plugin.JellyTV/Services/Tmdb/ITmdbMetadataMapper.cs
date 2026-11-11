using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal interface ITmdbMetadataMapper
{
    TmdbMetadata Map(TmdbSearchResult search, TmdbDetailResponse? detail);
}
