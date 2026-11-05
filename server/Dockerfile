FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Mugonzei.Server.csproj ./
RUN dotnet restore Mugonzei.Server.csproj
COPY . ./
RUN dotnet publish Mugonzei.Server.csproj -c Release --no-restore -o /publish -p:UseAppHost=false

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
