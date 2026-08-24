FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/ClassGraph.SampleDomain/ClassGraph.SampleDomain.csproj src/ClassGraph.SampleDomain/
COPY src/ClassGraph.Server/ClassGraph.Server.csproj src/ClassGraph.Server/
RUN dotnet restore src/ClassGraph.Server/ClassGraph.Server.csproj

COPY src/ src/
RUN dotnet publish src/ClassGraph.Server/ClassGraph.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000

USER $APP_UID
ENTRYPOINT ["dotnet", "ClassGraph.Server.dll"]
