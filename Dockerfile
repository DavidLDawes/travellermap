# The map site on Linux: the .NET 10 host (host/) with the site's files and a prebuilt search
# index. See host/README.md.
#   docker build -t travellermap .
#   docker run -p 8080:8080 -e AdminKey=... travellermap      # http://localhost:8080/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .

# The host, framework-dependent, with native libraries for this architecture only.
RUN RID=linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
    && dotnet publish host -c Release -r "$RID" --self-contained false -o /app

# The search index, built from the sector data in the image.
RUN dotnet run -c Release --project tools/reindex -- /index/search.db

# The site's files: everything served, without the server's source or build files.
RUN rm -rf server core host unittests tools/validate tools/reindex \
        Maps.csproj Maps.sln Maps.ruleset Global.asax Global.asax.cs AssemblyInfo.cs GlobalSuppressions.cs \
        Directory.Build.targets Dockerfile .dockerignore \
    && find . -name bin -o -name obj | xargs rm -rf

# Chiseled: Ubuntu with only what .NET needs (no shell or package manager), non-root by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src /site
# /admin/reindex rewrites the index, so the app user owns App_Data.
COPY --from=build --chown=$APP_UID /index /site/App_Data

ENV SiteRoot=/site \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Maps.Host.dll"]
