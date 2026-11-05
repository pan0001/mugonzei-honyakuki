namespace Mugonzei.Server;

internal static class AdminPages
{
    public static void Map(WebApplication app, Func<HttpContext, bool> allowed)
    {
        MapAsset("/admin", "index.html", "text/html; charset=utf-8");
        MapAsset("/admin/admin.js", "admin.js", "text/javascript; charset=utf-8");
        MapAsset("/admin/admin.css", "admin.css", "text/css; charset=utf-8");

        void MapAsset(string path, string name, string contentType)
        {
            app.MapGet(path, (HttpContext context) =>
            {
                if (!allowed(context)) return Results.NotFound();
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; " +
                    "base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                var content = typeof(AdminPages).Assembly.GetManifestResourceStream("Mugonzei.Admin." + name)
                    ?? throw new InvalidOperationException("管理界面资源缺失。");
                return Results.Stream(content, contentType);
            });
        }
    }
}
