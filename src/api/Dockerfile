# Build from the repository root: docker build -f src/api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/api/GmailOrganiser.Api.csproj src/api/
RUN dotnet restore src/api/GmailOrganiser.Api.csproj
COPY src/api/ src/api/
RUN dotnet publish src/api/GmailOrganiser.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "GmailOrganiser.Api.dll"]
