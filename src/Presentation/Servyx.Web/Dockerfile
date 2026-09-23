# syntax=docker/dockerfile:1.7
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-local
ARG ASSEMBLY_VERSION=0.0.0.0
ARG FILE_VERSION=0.0.0.0
ARG INFORMATIONAL_VERSION=0.0.0-local
WORKDIR /src
COPY . .
RUN dotnet restore src/Presentation/Servyx.Web/Servyx.Web.csproj -a $TARGETARCH
RUN dotnet publish src/Presentation/Servyx.Web/Servyx.Web.csproj -c Release -a $TARGETARCH --no-restore \
    --self-contained false -o /app/publish -p:UseAppHost=false \
    -p:Version=$VERSION -p:AssemblyVersion=$ASSEMBLY_VERSION -p:FileVersion=$FILE_VERSION \
    -p:InformationalVersion=$INFORMATIONAL_VERSION -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/servyx-data && chown -R $APP_UID:$APP_UID /app/servyx-data
VOLUME ["/app/servyx-data"]
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Servyx.Web.dll"]
