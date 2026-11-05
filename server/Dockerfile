FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY server/Mugonzei.Server.csproj server/
RUN dotnet restore server/Mugonzei.Server.csproj
COPY server/ server/
COPY shared/ shared/
COPY website/admin/ website/admin/
RUN dotnet publish server/Mugonzei.Server.csproj -c Release --no-restore -o /publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish ./
RUN mkdir /data && chown app:app /data
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:5187
ENV MUGONZEI_DATABASE=/data/mugonzei.db
USER app
EXPOSE 5187
ENTRYPOINT ["dotnet", "Mugonzei.Server.dll"]
