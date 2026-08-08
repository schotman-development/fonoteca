using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fonoteca.Data;

/// <summary>
/// Teaches EF Core to store the strongly-typed ids from <c>Fonoteca.Domain</c>
/// as plain <c>uuid</c> columns.
/// </summary>
/// <remarks>
/// Registered once in <see cref="FonotecaDbContext.ConfigureConventions"/>, so
/// every property of these types is converted automatically — an entity can
/// never accidentally be mapped with a raw <c>Guid</c> and lose the type safety.
/// </remarks>
internal sealed class WorkIdConverter() : ValueConverter<WorkId, Guid>(id => id.Value, v => new WorkId(v));

internal sealed class RecordingIdConverter()
    : ValueConverter<RecordingId, Guid>(id => id.Value, v => new RecordingId(v));

internal sealed class ReleaseGroupIdConverter()
    : ValueConverter<ReleaseGroupId, Guid>(id => id.Value, v => new ReleaseGroupId(v));

internal sealed class ReleaseIdConverter()
    : ValueConverter<ReleaseId, Guid>(id => id.Value, v => new ReleaseId(v));

internal sealed class TrackIdConverter()
    : ValueConverter<TrackId, Guid>(id => id.Value, v => new TrackId(v));

internal sealed class MediaFileIdConverter()
    : ValueConverter<MediaFileId, Guid>(id => id.Value, v => new MediaFileId(v));

internal sealed class ArtistIdConverter()
    : ValueConverter<ArtistId, Guid>(id => id.Value, v => new ArtistId(v));

internal sealed class MbidConverter() : ValueConverter<Mbid, Guid>(id => id.Value, v => new Mbid(v));

internal sealed class AcoustIdConverter()
    : ValueConverter<AcoustId, Guid>(id => id.Value, v => new AcoustId(v));
