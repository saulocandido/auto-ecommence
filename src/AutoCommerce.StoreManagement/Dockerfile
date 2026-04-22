FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj", "src/AutoCommerce.StoreManagement/"]
COPY ["src/AutoCommerce.Shared/AutoCommerce.Shared.csproj", "src/AutoCommerce.Shared/"]
RUN dotnet restore "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj"
COPY . .
RUN dotnet build "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image ships with Chromium + all its shared-library deps preinstalled,
# which Playwright needs to drive the Shopify admin flow.
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy
WORKDIR /app
COPY --from=publish /app/publish .
# Playwright browsers live at /ms-playwright in this image; tell .NET about it.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENTRYPOINT ["dotnet", "AutoCommerce.StoreManagement.dll"]
