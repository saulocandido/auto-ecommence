# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["AutoCommerce.sln", "./"]
COPY ["src/AutoCommerce.Shared/AutoCommerce.Shared.csproj", "src/AutoCommerce.Shared/"]
COPY ["src/AutoCommerce.Brain/AutoCommerce.Brain.csproj", "src/AutoCommerce.Brain/"]

RUN dotnet restore "src/AutoCommerce.Brain/AutoCommerce.Brain.csproj"

COPY ["src/AutoCommerce.Shared/", "src/AutoCommerce.Shared/"]
COPY ["src/AutoCommerce.Brain/", "src/AutoCommerce.Brain/"]

RUN dotnet publish "src/AutoCommerce.Brain/AutoCommerce.Brain.csproj" \
    -c "${BUILD_CONFIGURATION}" \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

RUN addgroup --system appgroup \
    && adduser --system --ingroup appgroup --home /app appuser \
    && mkdir -p /app/data \
    && mkdir -p /app/.aspnet/DataProtection-Keys \
    && chown -R appuser:appgroup /app

COPY --from=build --chown=appuser:appgroup /app/publish/ ./

EXPOSE 8080
VOLUME ["/app/data"]

USER appuser

ENTRYPOINT ["dotnet", "AutoCommerce.Brain.dll"]
