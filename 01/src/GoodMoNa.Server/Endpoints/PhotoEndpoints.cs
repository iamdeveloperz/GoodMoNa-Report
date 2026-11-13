using GoodMoNa.Server.Data;
using GoodMoNa.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Endpoints;

public static class PhotoEndpoints
{
    public static void MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/photos/{photoId:int}", async (int photoId, AppDbContext db, MediaStorage storage, HttpResponse response, CancellationToken ct) =>
        {
            var photo = await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId, ct);
            if (photo is null) return Problems.NotFound("사진을 찾을 수 없습니다.");

            var full = storage.GetFullPath(photo.StoragePath);
            if (!File.Exists(full)) return Problems.NotFound("사진 파일이 없습니다.");

            // 사진은 id별로 바뀌지 않으므로 오래 캐시하되, 의료 사진이라 공유 캐시(CDN)에는 남기지 않습니다.
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(full, photo.ContentType);
        });
    }
}
