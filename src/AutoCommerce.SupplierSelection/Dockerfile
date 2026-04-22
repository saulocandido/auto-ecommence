# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["AutoCommerce.sln", "./"]
COPY ["src/AutoCommerce.Shared/AutoCommerce.Shared.csproj", "src/AutoCommerce.Shared/"]
COPY ["src/AutoCommerce.SupplierSelection/AutoCommerce.SupplierSelection.csproj", "src/AutoCommerce.SupplierSelection/"]

RUN dotnet restore "src/AutoCommerce.SupplierSelection/AutoCommerce.SupplierSelection.csproj"

COPY ["src/AutoCommerce.Shared/", "src/AutoCommerce.Shared/"]
COPY ["src/AutoCommerce.SupplierSelection/", "src/AutoCommerce.SupplierSelection/"]

RUN dotnet publish "src/AutoCommerce.SupplierSelection/AutoCommerce.SupplierSelection.csproj" \
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
    && mkdir -p /app/.aspnet/DataProtection-Keys \
    && chown -R appuser:appgroup /app

COPY --from=build --chown=appuser:appgroup /app/publish/ ./

EXPOSE 8080

USER appuser

ENTRYPOINT ["dotnet", "AutoCommerce.SupplierSelection.dll"]
